using System.Reflection;
using System.Text.Json;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Analysis;

/// <summary>One token of a folded line that a lexicon row matched.</summary>
/// <param name="Kind">The row's kind: <see cref="HabitKind"/>.</param>
/// <param name="Lexeme">The stem as the user wrote it — what the screen shows beside the moment.</param>
/// <param name="Token">The whole token that matched, folded and stripped of punctuation: the verdict key.</param>
/// <param name="TokenIndex">Its position among the line's whitespace-separated pieces, for finding its timing.</param>
/// <param name="CharOffset">Where it starts in the folded line.</param>
public readonly record struct LexiconHit(string Kind, string Lexeme, string Token, int TokenIndex, int CharOffset);

/// <summary>
/// The dictionary the habit counters read, and the one matching rule they all use.
///
/// Substring matching is the obvious way and the wrong one: a three-letter stem sits inside
/// "klasik" and a two-letter one inside "aman", and a counter built that way reports swearing
/// in a sentence about furniture. Whole-token matching is wrong the other way — Turkish
/// inflects, so the stem with "-tir" or "-tirdim" on it would never be found. The rule here is
/// the one that survives both: the stem must START a token, and whatever follows it must be
/// empty or one of the endings the row lists. "klasik" does not start with the stem; "şikayet"
/// starts with it and continues with an ending nobody listed; the stem with "-tir" continues
/// with one somebody did.
///
/// Everything is folded with <see cref="TurkishText.NormalizeForSearch"/> — the stems, the
/// endings and the line — so every spelling of a word meets in one bucket, the same way search
/// works. The line handed in is expected already folded; the tokeniser then only strips the
/// punctuation Whisper leaves on words.
///
/// No word from the dictionary appears in code, comments or logs, here or anywhere: the data
/// file carries them and the log carries counts.
/// </summary>
public sealed class HabitLexicon
{
    /// <summary>The embedded seed's resource name — see the csproj for why the name matters.</summary>
    private const string ResourceName = "VoiceTranscript.Core.Resources.habits.tr.json";

    private sealed record Entry(string Kind, string Lexeme, string Stem, HashSet<string> Suffixes);

    private readonly List<Entry> _counted;
    private readonly List<Entry> _excluded;

    private HabitLexicon(List<Entry> counted, List<Entry> excluded, int version)
    {
        _counted = counted;
        _excluded = excluded;
        LexiconVersion = version;
    }

    /// <summary>
    /// A number that changes when the rows do, stored beside every report so a recount can tell
    /// which reports were counted with an older dictionary. A hash rather than a counter because
    /// the table has no version row and adding one would be a second thing to keep in step.
    /// </summary>
    public int LexiconVersion { get; }

    /// <summary>How many rows make hits (exclusions not counted).</summary>
    public int CountedRows => _counted.Count;

    /// <summary>How many rows remove hits.</summary>
    public int ExcludedRows => _excluded.Count;

    /// <summary>Builds a matcher from rows, wherever they came from.</summary>
    public static HabitLexicon From(IEnumerable<HabitLexeme> rows)
    {
        var list = rows.ToList();
        List<Entry> counted = [];
        List<Entry> excluded = [];

        foreach (var row in list)
        {
            var stem = row.LexemeFolded.Length > 0 ? row.LexemeFolded : TurkishText.NormalizeForSearch(row.Lexeme);
            if (stem.Length == 0) continue;

            var suffixes = row.Suffixes
                .Select(TurkishText.NormalizeForSearch)
                .Where(s => s.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            var entry = new Entry(row.Kind, row.Lexeme, stem, suffixes);

            if (row.Kind == HabitKind.Exclusion) excluded.Add(entry);
            else counted.Add(entry);
        }

        // Longest stem first, so a token that two stems could claim is claimed by the more
        // specific one and counted once.
        counted.Sort((a, b) => b.Stem.Length.CompareTo(a.Stem.Length));

        return new HabitLexicon(counted, excluded, Version(list));
    }

    /// <summary>The rows the user has, as a matcher.</summary>
    public static HabitLexicon Load(Repository repository) => From(repository.Lexicon());

    /// <summary>
    /// The starting dictionary, read from the embedded resource. Empty when the resource is
    /// missing or unreadable — a counter with nothing to count is a visible outcome, whereas an
    /// exception here would take the whole recount down.
    /// </summary>
    public static IReadOnlyList<HabitLexeme> EmbeddedSeed()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return [];

            using var reader = new StreamReader(stream);
            var groups = JsonSerializer.Deserialize<Dictionary<string, List<SeedRow>>>(reader.ReadToEnd());
            if (groups is null) return [];

            List<HabitLexeme> rows = [];

            foreach (var (kind, entries) in groups)
            {
                var position = 0;

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.lexeme)) continue;

                    rows.Add(new HabitLexeme
                    {
                        Kind = kind,
                        Lexeme = entry.lexeme.Trim(),
                        LexemeFolded = TurkishText.NormalizeForSearch(entry.lexeme),
                        Suffixes = entry.suffixes ?? [],
                        Position = position++,
                    });
                }
            }

            return rows;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the embedded seed into an EMPTY lexicon table, and does nothing otherwise.
    ///
    /// Only into an empty table, on the tag_def pattern: the rows are the user's the moment they
    /// exist, and a seed that re-inserted missing stems on every start would bring back the ones
    /// they deleted. Returns how many rows were written — zero on every start but the first.
    /// </summary>
    public static int Seed(Repository repository)
    {
        if (repository.Lexicon().Count > 0) return 0;

        var written = 0;

        foreach (var row in EmbeddedSeed())
        {
            repository.UpsertLexeme(row.Kind, row.Lexeme, row.Suffixes, row.Position);
            written++;
        }

        CoreLog.Write("aynam", $"sozluk tohumlandi: {written} satir");
        return written;
    }

    /// <summary>
    /// Every token of a folded line that a row claims, in line order, exclusions already applied.
    /// One hit per token at most.
    /// </summary>
    public IReadOnlyList<LexiconHit> Matches(string normalisedText)
    {
        if (string.IsNullOrWhiteSpace(normalisedText) || _counted.Count == 0) return [];

        List<LexiconHit> hits = [];

        foreach (var token in Tokenize(normalisedText))
        {
            if (token.Text.Length == 0) continue;

            var match = _counted.FirstOrDefault(e => Claims(e, token.Text));
            if (match is null) continue;

            if (_excluded.Any(e => Claims(e, token.Text))) continue;

            hits.Add(new LexiconHit(match.Kind, match.Lexeme, token.Text, token.Index, token.Offset));
        }

        return hits;
    }

    /// <summary>The rule itself: the stem starts the token, and the rest is nothing or a listed ending.</summary>
    private static bool Claims(Entry entry, string token)
    {
        if (token.Length < entry.Stem.Length) return false;
        if (!token.StartsWith(entry.Stem, StringComparison.Ordinal)) return false;

        var rest = token.Length == entry.Stem.Length ? "" : token[entry.Stem.Length..];
        return rest.Length == 0 || entry.Suffixes.Contains(rest);
    }

    /// <summary>One whitespace-separated piece of a line, stripped of surrounding punctuation.</summary>
    /// <param name="Text">The piece without leading or trailing punctuation; empty when it was only punctuation.</param>
    /// <param name="Index">Its position among ALL pieces, punctuation-only ones included, so it lines up with the engine's word list.</param>
    /// <param name="Offset">Where <see cref="Text"/> begins in the line.</param>
    public readonly record struct Token(string Text, int Index, int Offset);

    /// <summary>
    /// Splits a line the way the engines split their word lists — on whitespace — then strips
    /// what is not a letter or digit from each end of every piece. The index counts every piece,
    /// so a token's position can be looked up in <see cref="Segment.Words"/> by order.
    /// </summary>
    public static IReadOnlyList<Token> Tokenize(string normalisedText)
    {
        List<Token> tokens = [];
        if (string.IsNullOrEmpty(normalisedText)) return tokens;

        var index = 0;
        var i = 0;

        while (i < normalisedText.Length)
        {
            while (i < normalisedText.Length && char.IsWhiteSpace(normalisedText[i])) i++;
            if (i >= normalisedText.Length) break;

            var start = i;
            while (i < normalisedText.Length && !char.IsWhiteSpace(normalisedText[i])) i++;

            var first = start;
            var last = i - 1;

            while (first <= last && !char.IsLetterOrDigit(normalisedText[first])) first++;
            while (last >= first && !char.IsLetterOrDigit(normalisedText[last])) last--;

            tokens.Add(first <= last
                ? new Token(normalisedText[first..(last + 1)], index, first)
                : new Token("", index, start));

            index++;
        }

        return tokens;
    }

    /// <summary>
    /// The version of a set of rows: FNV-1a over kind, stem and endings in a fixed order.
    ///
    /// Position is left out because it changes nothing about what is counted, and a hash that
    /// moved when the user reordered the list would mark every report stale for no reason.
    /// String.GetHashCode is not used because it differs from one process to the next.
    /// </summary>
    public static int Version(IEnumerable<HabitLexeme> rows)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        var hash = offset;

        foreach (var row in rows
                     .Select(r => (
                         r.Kind,
                         Stem: r.LexemeFolded.Length > 0 ? r.LexemeFolded : TurkishText.NormalizeForSearch(r.Lexeme),
                         Suffixes: string.Join(",", r.Suffixes.Select(TurkishText.NormalizeForSearch).Where(s => s.Length > 0).Order(StringComparer.Ordinal))))
                     .OrderBy(r => r.Kind, StringComparer.Ordinal)
                     .ThenBy(r => r.Stem, StringComparer.Ordinal))
        {
            // Each field is length-prefixed, so "ab"+"c" and "a"+"bc" hash apart without a
            // separator character — which would have to be one that no stem can contain.
            foreach (var field in new[] { row.Kind, row.Stem, row.Suffixes })
            {
                hash ^= (uint)field.Length;
                hash *= prime;

                foreach (var ch in field)
                {
                    hash ^= ch;
                    hash *= prime;
                }
            }
        }

        return unchecked((int)hash);
    }

    /// <summary>The shape of one entry in the seed file. Lower-case to match the JSON without attributes.</summary>
    private sealed class SeedRow
    {
        public string? lexeme { get; set; }
        public List<string>? suffixes { get; set; }
    }
}
