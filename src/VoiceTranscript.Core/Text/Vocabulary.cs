namespace VoiceTranscript.Core.Text;

/// <summary>
/// The words the recogniser should expect, assembled from everything the archive already knows.
///
/// A hand-written term list does not scale: the user has hundreds of names, products and bits of
/// jargon, and nobody maintains a list like that. Most of it is already in the application —
/// the contacts, what the user wrote about them, and the proper nouns the transcripts themselves
/// keep producing. Those are gathered here and sent ahead of every transcription; the typed list
/// is kept for the stubborn few ("Sumsub") that the recogniser has never once got right and so
/// cannot be mined.
/// </summary>
public sealed record Vocabulary(string? Terms, string? Prompt)
{
    public static readonly Vocabulary Empty = new(null, null);

    /// <summary>How many terms go as hotwords. Beyond this the bias dilutes into noise.</summary>
    public const int MaxTerms = 300;

    /// <summary>
    /// How many go in the initial prompt. Whisper reads only the last ~220 tokens of it, and a
    /// prompt that long gets echoed into silence; forty short terms stays well under that.
    /// </summary>
    public const int PromptTerms = 40;

    /// <summary>
    /// Merges the sources in order of trust: what the user typed, then the people they know,
    /// then what the transcripts keep saying. Duplicates keep their first, more trusted, place.
    /// </summary>
    public static Vocabulary Compose(
        IEnumerable<string>? manual,
        IEnumerable<string>? names = null,
        IEnumerable<string>? mined = null)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var terms = new List<string>();

        foreach (var source in new[] { manual, names, mined })
        {
            if (source is null) continue;

            foreach (var raw in source)
            {
                var term = raw.Trim().Trim(',', ';', '.');
                if (term.Length < 2 || !seen.Add(term)) continue;

                terms.Add(term);
                if (terms.Count >= MaxTerms) break;
            }

            if (terms.Count >= MaxTerms) break;
        }

        if (terms.Count == 0) return Empty;

        return new Vocabulary(
            string.Join(", ", terms),
            string.Join(", ", terms.Take(PromptTerms)) + ".");
    }
}

/// <summary>
/// Finds the proper nouns a transcript archive keeps producing.
///
/// Whisper capitalises names it recognises and little else mid-sentence, so a capitalised token
/// that is not at the start of a sentence and recurs across the archive is, nearly always, a
/// person, a place, a company or a product. Sentence-initial words are skipped: every sentence
/// starts with a capital. Turkish suffixes after an apostrophe are cut ("Sumsub'a" → "Sumsub").
/// Deterministic and free — no model runs — so it can run before every transcription.
/// </summary>
public static class VocabularyMiner
{
    private static readonly char[] SentenceEnds = ['.', '?', '!', ':', ';'];

    public static IReadOnlyList<string> Mine(IEnumerable<string> texts, int max = 200, int minCount = 2)
    {
        var counts = new Dictionary<string, int>(StringComparer.CurrentCulture);

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sentenceStart = true;

            foreach (var token in tokens)
            {
                var word = Stem(token);
                var endsSentence = token.Length > 0 && SentenceEnds.Contains(token[^1]);

                if (!sentenceStart && LooksLikeAName(word))
                    counts[word] = counts.GetValueOrDefault(word) + 1;

                sentenceStart = endsSentence;
            }
        }

        return
        [
            .. counts
                .Where(kv => kv.Value >= minCount)
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.CurrentCulture)
                .Select(kv => kv.Key)
                .Take(max),
        ];
    }

    /// <summary>The token without punctuation and without a Turkish suffix after an apostrophe.</summary>
    private static string Stem(string token)
    {
        var cut = token.IndexOfAny(['\'', '’', '‘']);
        if (cut >= 0) token = token[..cut];

        return token.Trim('.', ',', '?', '!', ':', ';', '"', '(', ')', '[', ']', '«', '»', '“', '”');
    }

    private static bool LooksLikeAName(string word)
    {
        if (word.Length < 3 || word.Length > 30) return false;
        if (!char.IsUpper(word[0])) return false;

        // Letters only, apart from an inner hyphen or dot ("Coca-Cola", "Node.js").
        return word.All(c => char.IsLetter(c) || c is '-' or '.');
    }
}
