using System.Text.Json;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Analysis;

/// <summary>How sure the count is of one moment.</summary>
public enum HabitBucket
{
    /// <summary>The engine was sure of the word and the line; or the user listened and confirmed it.</summary>
    Certain = 0,

    /// <summary>
    /// The engine's confidence in the word was below the engine's threshold, or the whole line was
    /// marked low-confidence. Listed, not counted: "belirsiz".
    /// </summary>
    Uncertain = 1,

    /// <summary>The user listened and said the words were not said, or were not that. Left out of every figure.</summary>
    Dismissed = 2,
}

/// <summary>One counted word, where it was said, and how sure the count is.</summary>
/// <param name="Kind"><see cref="HabitKind"/>.</param>
/// <param name="Lexeme">The dictionary stem, as the user wrote it.</param>
/// <param name="StartMs">The word's own timing when the line carries word timings; the line's start otherwise.</param>
/// <param name="EndMs">Likewise.</param>
/// <param name="QuoteFolded">The matched token, folded: what a verdict is keyed by, together with the millisecond.</param>
public sealed record HabitMoment(string Kind, string Lexeme, int StartMs, int EndMs, string QuoteFolded, HabitBucket Bucket);

/// <summary>
/// A moment where the user read out something with a shape: an IBAN, a phone number, an amount,
/// a date. The KIND and the time, and deliberately nothing else. Storing the number would make
/// the habit cache a second place the archive keeps bank details, in a backup that may not be
/// encrypted, to answer a question that needs only the fact that it happened.
/// </summary>
/// <param name="Kind"><see cref="DisclosureKind"/>.</param>
public sealed record DisclosureMoment(string Kind, int StartMs, int EndMs);

/// <summary>The shapes a disclosure can have. Only shapes: a name, which the engines capitalise unreliably, is not one.</summary>
public static class DisclosureKind
{
    /// <summary>A number of six or more digits, with or without thousands separators.</summary>
    public const string Amount = "tutar";

    /// <summary>TR followed by twenty-four digits, spaced or not.</summary>
    public const string Iban = "iban";

    /// <summary>A Turkish mobile or national number, spaced or not.</summary>
    public const string Phone = "telefon";

    /// <summary>A day and a month said explicitly, as <see cref="TurkishDates.TryExplicit"/> reads them.</summary>
    public const string Date = "tarih";
}

/// <summary>The three figures for one kind.</summary>
public sealed record HabitCount(string Kind, int Certain, int Uncertain, int Dismissed)
{
    public int Listed => Certain + Uncertain + Dismissed;
}

/// <summary>
/// What the user did while talking in one conversation, counted.
///
/// The denominators are here because the ratio is the product. "6,1 küfür / görüşme" was the
/// first design and it is the wrong number: a conversation is fourteen seconds or four hours,
/// and a count per conversation says more about the length than the habit. Every rate the
/// screens show is per minute of the USER's own speech or per hundred of the user's own words,
/// both of which are kept on the report so the trend page can pool them across calls instead of
/// averaging averages.
///
/// Serialised as-is into the cache; the computed properties are convenience for readers and are
/// ignored on the way back in.
/// </summary>
public sealed record HabitReport
{
    public IReadOnlyList<HabitMoment> Moments { get; init; } = [];
    public IReadOnlyList<DisclosureMoment> Disclosures { get; init; } = [];

    /// <summary>One row per counted kind, always present even at zero, so no reader has to special-case an absent kind.</summary>
    public IReadOnlyList<HabitCount> Counts { get; init; } = [];

    /// <summary>The user's lines that were counted (echo left out).</summary>
    public int MyLines { get; init; }

    /// <summary>The user's words: the engine's word count when it gave one, whitespace pieces otherwise. A denominator.</summary>
    public int MyWords { get; init; }

    /// <summary>The user's speaking time. The other denominator.</summary>
    public int MySpokenMs { get; init; }

    /// <summary>Words on lines that carry word timings, and those lines' length: the speech-rate numerator and denominator.</summary>
    public int TimedWords { get; init; }

    public int TimedMs { get; init; }

    /// <summary>Whether any word carried a confidence. False on OpenAI and whisper.cpp transcripts, which the screen says as "kelime güveni yok".</summary>
    public bool HasWordConfidence { get; init; }

    /// <summary>The threshold the buckets were judged against, or null when the engine has none measured — then only the line gate applies.</summary>
    public double? WordThreshold { get; init; }

    public int EchoLinesExcluded { get; init; }

    public double MyMinutes => MySpokenMs / 60000.0;

    /// <summary>Words per minute over the lines that carry word timings, or null when none do. Comparable only within one engine.</summary>
    public double? WordsPerMinute => TimedMs > 0 ? TimedWords / (TimedMs / 60000.0) : null;

    public HabitCount CountOf(string kind) =>
        Counts.FirstOrDefault(c => c.Kind == kind) ?? new HabitCount(kind, 0, 0, 0);

    /// <summary>Certain hits per minute of the user's speech, or null when they did not speak.</summary>
    public double? PerMinute(string kind) => MySpokenMs > 0 ? CountOf(kind).Certain / MyMinutes : null;

    /// <summary>Certain hits per hundred of the user's words, or null when there were none.</summary>
    public double? PerHundredWords(string kind) => MyWords > 0 ? CountOf(kind).Certain * 100.0 / MyWords : null;
}

/// <summary>The cache row's payload: the report and the talk figures together, so the trend reads a year in one SELECT.</summary>
public sealed record HabitSnapshot(HabitReport Habits, TalkStats Talk)
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Null for anything that does not parse: a cache row from a shape this build no longer knows is a row to recount, not a crash.</summary>
    public static HabitSnapshot? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HabitSnapshot>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The counters. Pure: lines, a dictionary, a threshold and the user's verdicts in; a report out.
///
/// Only the user's own lines are read. The other party's habits are not counted anywhere in this
/// product, and this is the place that rule is enforced rather than remembered: a line with
/// IsMe false never reaches a counter. Lines the capture layer marked as suspected echo are left
/// out too — they are the far end heard through the user's microphone, and they would put the
/// other party's words on the user's side.
///
/// Two gates decide the bucket, and both are the engine's own figures rather than anything this
/// code judges: the word's confidence against the engine's measured threshold, and the line's
/// low-confidence mark. A word from an engine that reports no confidence is judged by the line
/// alone, which the report says with <see cref="HabitReport.HasWordConfidence"/>.
///
/// The user's verdicts come last and win: a moment they listened to and rejected leaves the
/// count, a moment they confirmed enters it whatever the engine thought. Matched by the words
/// and the millisecond, within a second and a half, because a recount from a different
/// transcript moves word boundaries by a little and must still find the verdict.
/// </summary>
public static class SpeechHabits
{
    /// <summary>How far a verdict's millisecond may sit from a recounted moment's and still be its verdict.</summary>
    public const int VerdictWindowMs = 1500;

    /// <summary>The date <see cref="TurkishDates.TryExplicit"/> resolves against. Arbitrary: only whether a date was said matters here, never which.</summary>
    private static readonly DateOnly AnyDay = new(2000, 1, 1);

    public static HabitReport Count(
        IReadOnlyList<Segment> segments,
        HabitLexicon lexicon,
        double? wordThreshold,
        IReadOnlyList<Verdict> verdicts)
    {
        List<HabitMoment> moments = [];
        List<DisclosureMoment> disclosures = [];

        var myLines = 0;
        var myWords = 0;
        var mySpokenMs = 0;
        var timedWords = 0;
        var timedMs = 0;
        var hasConfidence = false;
        var echo = 0;

        foreach (var segment in segments)
        {
            if (!segment.IsMe) continue;

            if (segment.SuspectedEcho)
            {
                echo++;
                continue;
            }

            myLines++;

            // Folded here rather than read from TextNormalised, which is filled by the repository
            // and empty on lines that never went through it.
            var folded = TurkishText.NormalizeForSearch(segment.Text);
            var tokens = HabitLexicon.Tokenize(folded);
            var length = Math.Max(0, segment.EndMs - segment.StartMs);

            var words = segment.Words.Count > 0 ? segment.Words.Count : tokens.Count;
            myWords += words;
            mySpokenMs += length;

            if (segment.Words.Count > 0)
            {
                timedWords += segment.Words.Count;
                timedMs += length;
                if (segment.Words.Any(w => w.Probability is not null)) hasConfidence = true;
            }

            foreach (var hit in lexicon.Matches(folded))
            {
                var (start, end, probability) = Locate(segment, hit.TokenIndex, hit.Token);

                var uncertain = segment.LowConfidence
                    || (wordThreshold is { } threshold && probability is { } p && p < threshold);

                moments.Add(new HabitMoment(
                    hit.Kind, hit.Lexeme, start, end, hit.Token,
                    uncertain ? HabitBucket.Uncertain : HabitBucket.Certain));
            }

            disclosures.AddRange(Disclosures(segment, folded, tokens));
        }

        moments = [.. moments.Select(m => Ruled(m, verdicts))];

        var counts = HabitKind.Counted
            .Select(kind => new HabitCount(
                kind,
                moments.Count(m => m.Kind == kind && m.Bucket == HabitBucket.Certain),
                moments.Count(m => m.Kind == kind && m.Bucket == HabitBucket.Uncertain),
                moments.Count(m => m.Kind == kind && m.Bucket == HabitBucket.Dismissed)))
            .ToList();

        return new HabitReport
        {
            Moments = moments,
            Disclosures = disclosures,
            Counts = counts,
            MyLines = myLines,
            MyWords = myWords,
            MySpokenMs = mySpokenMs,
            TimedWords = timedWords,
            TimedMs = timedMs,
            HasWordConfidence = hasConfidence,
            WordThreshold = wordThreshold,
            EchoLinesExcluded = echo,
        };
    }

    /// <summary>The user's ruling on a moment, applied. No verdict: the bucket stands.</summary>
    private static HabitMoment Ruled(HabitMoment moment, IReadOnlyList<Verdict> verdicts)
    {
        var verdict = verdicts
            .Where(v => v.Kind == moment.Kind
                        && v.QuoteFolded == moment.QuoteFolded
                        && Math.Abs(v.StartMs - moment.StartMs) <= VerdictWindowMs)
            .OrderBy(v => Math.Abs(v.StartMs - moment.StartMs))
            .FirstOrDefault();

        return verdict?.Value switch
        {
            VerdictValue.Misheard or VerdictValue.NotThat => moment with { Bucket = HabitBucket.Dismissed },
            VerdictValue.Correct => moment with { Bucket = HabitBucket.Certain },
            _ => moment,
        };
    }

    /// <summary>
    /// Where a token was said. By position in the engine's word list when the two line up, by
    /// text nearest that position when they do not, and the line's own span when the line has no
    /// word timings at all — every transcript stored before timings were kept.
    /// </summary>
    private static (int StartMs, int EndMs, double? Probability) Locate(Segment segment, int tokenIndex, string token)
    {
        var words = segment.Words;
        if (words.Count == 0) return (segment.StartMs, segment.EndMs, null);

        if (tokenIndex < words.Count && Fold(words[tokenIndex].Text) == token)
            return Timing(words[tokenIndex]);

        SpokenWord? nearest = null;
        var distance = int.MaxValue;

        for (var i = 0; i < words.Count; i++)
        {
            if (Fold(words[i].Text) != token) continue;

            var d = Math.Abs(i - tokenIndex);
            if (d >= distance) continue;

            nearest = words[i];
            distance = d;
        }

        return nearest is { } word ? Timing(word) : (segment.StartMs, segment.EndMs, null);

        static (int, int, double?) Timing(SpokenWord w) => (w.StartMs, w.EndMs, w.Probability);
    }

    private static string Fold(string wordText)
    {
        var tokens = HabitLexicon.Tokenize(TurkishText.NormalizeForSearch(wordText));
        return tokens.Count == 1 ? tokens[0].Text : string.Concat(tokens.Select(t => t.Text));
    }

    /// <summary>
    /// The shaped things in one line: runs of digits read as an IBAN or a phone number, single
    /// numbers of six or more digits, and an explicit date. Consecutive digit tokens are joined
    /// for the first two because the engines write "0532 123 45 67" as four words; a single
    /// token for the amount because joining "saat 12 30" would invent a five-digit figure.
    /// </summary>
    private static IEnumerable<DisclosureMoment> Disclosures(Segment segment, string folded, IReadOnlyList<HabitLexicon.Token> tokens)
    {
        var i = 0;

        while (i < tokens.Count)
        {
            if (!IsNumeric(tokens[i].Text, out var digits, out var iban))
            {
                i++;
                continue;
            }

            // Whether a bare "tr" stood right before the run, spelled as its own word.
            if (!iban && i > 0 && tokens[i - 1].Text == "tr") iban = true;

            var first = i;
            var run = digits;
            i++;

            while (i < tokens.Count && IsNumeric(tokens[i].Text, out var more, out var moreIban) && !moreIban)
            {
                run += more;
                i++;
            }

            var last = i - 1;

            if (iban && run.Length == 24)
            {
                yield return Span(segment, tokens, first, last, DisclosureKind.Iban);
                continue;
            }

            if (!iban && IsPhone(run))
            {
                yield return Span(segment, tokens, first, last, DisclosureKind.Phone);
                continue;
            }

            for (var t = first; t <= last; t++)
            {
                if (IsNumeric(tokens[t].Text, out var single, out _) && single.Length >= 6)
                    yield return Span(segment, tokens, t, t, DisclosureKind.Amount);
            }
        }

        if (TurkishDates.TryExplicit(folded, AnyDay, out _))
            yield return new DisclosureMoment(DisclosureKind.Date, segment.StartMs, segment.EndMs);
    }

    /// <summary>A token that is digits once separators are removed, with an optional leading "tr".</summary>
    private static bool IsNumeric(string token, out string digits, out bool iban)
    {
        iban = false;
        digits = "";
        if (token.Length == 0) return false;

        var rest = token;
        if (rest.StartsWith("tr", StringComparison.Ordinal) && rest.Length > 2)
        {
            rest = rest[2..];
            iban = true;
        }

        var sb = new System.Text.StringBuilder(rest.Length);

        foreach (var ch in rest)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (ch is '.' or ',' or '-' or '(' or ')' or '/') continue;
            else return false;
        }

        if (sb.Length == 0) return false;

        digits = sb.ToString();
        return true;
    }

    /// <summary>Turkish mobile and national shapes: 5xx…, 05xx…, 905xx…, or a ten-digit landline with its area code.</summary>
    private static bool IsPhone(string digits) => digits.Length switch
    {
        10 => digits[0] is '5' or '2' or '3' or '4',
        11 => digits.StartsWith("05", StringComparison.Ordinal) || digits.StartsWith("02", StringComparison.Ordinal)
              || digits.StartsWith("03", StringComparison.Ordinal) || digits.StartsWith("04", StringComparison.Ordinal),
        12 => digits.StartsWith("90", StringComparison.Ordinal),
        _ => false,
    };

    private static DisclosureMoment Span(
        Segment segment, IReadOnlyList<HabitLexicon.Token> tokens, int first, int last, string kind)
    {
        var (start, _, _) = Locate(segment, tokens[first].Index, tokens[first].Text);
        var (_, end, _) = Locate(segment, tokens[last].Index, tokens[last].Text);

        return new DisclosureMoment(kind, start, Math.Max(start, end));
    }
}
