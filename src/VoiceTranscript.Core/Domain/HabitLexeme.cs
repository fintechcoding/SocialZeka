namespace VoiceTranscript.Core.Domain;

/// <summary>
/// One row of the dictionary the habit counters read: a stem and the endings it may carry.
///
/// USER DATA. Seeded once from the embedded list and then the user's — edited, extended, pruned
/// — and never rewritten by a recount. A row is a stem rather than a word because Turkish is
/// agglutinative: a stem with "-tir" and the same stem with "-tirdim" are one habit, and a list
/// that had to spell out every inflection would miss the next one. The endings are data for the
/// same reason.
/// </summary>
public sealed record HabitLexeme
{
    public long Id { get; init; }

    /// <summary>One of <see cref="HabitKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The stem as the user wrote it — what the dictionary screen shows.</summary>
    public required string Lexeme { get; init; }

    /// <summary>The stem folded with <c>TurkishText.NormalizeForSearch</c>: the matching key. Set by the repository.</summary>
    public string LexemeFolded { get; init; } = "";

    /// <summary>
    /// The endings the stem may carry, folded. The bare stem always matches; anything else after
    /// it must be listed here, or the token is not a hit — which is what keeps "klasik" and
    /// "şikayet" out of the count.
    /// </summary>
    public IReadOnlyList<string> Suffixes { get; init; } = [];

    public int Position { get; init; }
}

/// <summary>What a lexicon row counts. Stored as text so a new kind is a constant, not a migration.</summary>
public static class HabitKind
{
    /// <summary>A swear word. The same string as <see cref="VerdictKind.Profanity"/>, so a verdict finds its moment by kind alone.</summary>
    public const string Profanity = "kufur";

    /// <summary>A filler word. The same string as <see cref="VerdictKind.Filler"/>.</summary>
    public const string Filler = "dolgu";

    /// <summary>
    /// A dialect marker. Reserved and unseeded: the transcribers normalise speech towards written
    /// Turkish, so what this would count is the engine's normalisation on the day, not the
    /// speaker — it is not built until a pre-measurement says otherwise.
    /// </summary>
    public const string Dialect = "sive";

    /// <summary>An exclusion: a token the user ruled is not that. Removes hits instead of making them.</summary>
    public const string Exclusion = "haric";

    /// <summary>The kinds that make hits, in the order the screens show them.</summary>
    public static readonly string[] Counted = [Profanity, Filler, Dialect];
}
