namespace VoiceTranscript.Core.Domain;

/// <summary>
/// What the user heard when they listened to one moment the machine had counted or flagged.
///
/// USER DATA. Nothing in the pipeline writes a verdict and no re-run deletes one. Every honest
/// figure the coaching screens show — "14 sayımın 11'i dinlendi, 10'u doğru", a detector's
/// precision, whether a level strip has earned its place — is a ratio over these rows; without
/// them the product would be asserting things nobody checked.
///
/// Identified by the words and the millisecond, not by the row it was about: a recount moves
/// row ids, a merged archive has different ones, and the verdict must still find its moment.
/// </summary>
public sealed record Verdict
{
    public long Id { get; init; }
    public required long CallId { get; init; }

    /// <summary>One of <see cref="VerdictKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The row it was about when it was given (flag.id, later tactic_evidence.id), if any.</summary>
    public long? TargetId { get; init; }

    /// <summary>The quote, folded with <c>TurkishText.NormalizeForSearch</c> — the matching key.</summary>
    public required string QuoteFolded { get; init; }

    public int StartMs { get; init; }

    public VerdictValue Value { get; init; }

    public DateTimeOffset DecidedAt { get; init; }
}

/// <summary>What was being judged. Stored as text so a new kind is a constant, not a migration.</summary>
public static class VerdictKind
{
    /// <summary>A ledger flag: the finding is real / is not.</summary>
    public const string Flag = "flag";

    /// <summary>A counted swear word.</summary>
    public const string Profanity = "kufur";

    /// <summary>A counted filler word.</summary>
    public const string Filler = "dolgu";

    /// <summary>A moment where the user gave information away.</summary>
    public const string Disclosure = "bilgi";

    /// <summary>A level or pitch peak: a change was / was not audible.</summary>
    public const string Tone = "ton";

    /// <summary>A live-alarm candidate: the user would / would not have wanted a warning.</summary>
    public const string Live = "canli";

    /// <summary>A per-contact pattern row (Kalıplar).</summary>
    public const string Pattern = "kalip";
}

/// <summary>The verdict itself. Numbers are stored; the words are the screen's.</summary>
public enum VerdictValue
{
    /// <summary>The transcript misheard it: the words were not said.</summary>
    Misheard = 0,

    /// <summary>Correct — it is what the machine said it was.</summary>
    Correct = 1,

    /// <summary>The words were said but it is not that (not a swear word, not a flag).</summary>
    NotThat = 2,

    /// <summary>For live-alarm candidates: the user would have wanted a warning here.</summary>
    WantedAlarm = 3,

    /// <summary>For live-alarm candidates: a warning here would have been noise.</summary>
    Unneeded = 4,
}
