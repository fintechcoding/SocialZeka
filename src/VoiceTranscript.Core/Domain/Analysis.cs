namespace VoiceTranscript.Core.Domain;

/// <summary>
/// A statement of obligation extracted from a call: who owes what, by when, for how much.
/// Always carries the exact words and the moment they were said, because the user verifies by
/// listening rather than by trusting the model.
/// </summary>
public sealed record Commitment
{
    public long Id { get; init; }
    public required long CallId { get; init; }
    public long? ContactId { get; init; }

    /// <summary>True when the user made the promise, false when the other party did.</summary>
    public bool ByMe { get; init; }

    /// <summary>Verbatim words. Verified to exist in the transcript before the row is written.</summary>
    public required string Quote { get; init; }

    public int QuoteStartMs { get; init; }

    /// <summary>What was promised, in the model's own words.</summary>
    public required string Obligation { get; init; }

    /// <summary>Deadline as spoken, for example "cuma günü".</summary>
    public string? DeadlineRaw { get; init; }

    /// <summary>Deadline resolved to a date, when that could be done unambiguously.</summary>
    public DateOnly? DeadlineDate { get; init; }

    public decimal? Amount { get; init; }
    public string? Currency { get; init; }

    /// <summary>True for "if X then I will Y" — an unconditional promise it is not.</summary>
    public bool IsConditional { get; init; }

    public CommitmentStatus Status { get; init; }
    public long? FulfilledByCallId { get; init; }

    /// <summary>Set when the user says this is not really a commitment. Suppressed thereafter.</summary>
    public bool DismissedByUser { get; init; }

    public bool IsOverdue(DateOnly today) =>
        Status == CommitmentStatus.Open && DeadlineDate is { } due && due < today;
}

public enum CommitmentStatus
{
    Open = 0,
    Fulfilled = 1,
    Renegotiated = 2,
    Abandoned = 3,
}

/// <summary>
/// A factual assertion, stored so that a later contradicting assertion can be found by a plain
/// SQL join rather than by asking a model to remember.
/// </summary>
public sealed record Claim
{
    public long Id { get; init; }
    public required long CallId { get; init; }
    public long? ContactId { get; init; }
    public bool ByMe { get; init; }

    public required string Quote { get; init; }
    public int QuoteStartMs { get; init; }

    /// <summary>What the claim is about, normalised for joining, for example "sozlesme".</summary>
    public required string Entity { get; init; }

    /// <summary>Which property of it, for example "fiyat" or "teslim tarihi".</summary>
    public required string Attribute { get; init; }

    /// <summary>The asserted value as text.</summary>
    public required string Value { get; init; }

    /// <summary>Parsed value when the claim is numeric, so changes can be compared exactly.</summary>
    public decimal? NumericValue { get; init; }

    public string? Unit { get; init; }

    /// <summary>Copied from the segment. Uncertain audio never feeds contradiction detection.</summary>
    public bool LowConfidence { get; init; }
}

public enum FlagKind
{
    /// <summary>A deadline that passed with no fulfilment recorded.</summary>
    OverdueCommitment = 0,

    /// <summary>The same commitment given a later date across calls.</summary>
    MovedDeadline = 1,

    /// <summary>The same item quoted at a different amount across calls.</summary>
    ChangedAmount = 2,

    /// <summary>Two claims about the same entity and attribute that cannot both be true.</summary>
    Contradiction = 3,

    /// <summary>A direct question that was answered evasively or not at all.</summary>
    EvadedQuestion = 4,

    /// <summary>Manufactured urgency, scarcity, appeals to authority, guilt, or threats.</summary>
    PressureTactic = 5,

    /// <summary>Matched a curated list of known Turkish fraud scripts. A heuristic, and labelled as one.</summary>
    ScamPattern = 6,

    /// <summary>Told sequences or dates that cannot all hold at once. From the consistency check.</summary>
    TimelineMismatch = 7,

    /// <summary>A statement that was specific going suddenly vague when pressed. From the consistency check.</summary>
    VagueShift = 8,
}

/// <summary>
/// Something worth the user's attention. Deliberately not a score.
///
/// An LLM cannot detect lying from a transcript, and pretending otherwise would be actively
/// harmful here: at a realistic rate of actual deception, the large majority of "this person is
/// lying" verdicts would be wrong, about the user's own friends, family and business contacts.
/// So the product surfaces what is countable and checkable — what was promised, what changed,
/// what went unanswered — each with the exact words and a timestamp, and lets the user judge.
/// </summary>
public sealed record Flag
{
    public long Id { get; init; }
    public required long CallId { get; init; }
    public long? ContactId { get; init; }
    public FlagKind Kind { get; init; }

    /// <summary>One-line description in Turkish, shown in the ledger.</summary>
    public required string Summary { get; init; }

    /// <summary>Verbatim words this flag rests on.</summary>
    public required string Quote { get; init; }

    public int QuoteStartMs { get; init; }

    /// <summary>The earlier quote being contradicted, when there is one.</summary>
    public string? CounterQuote { get; init; }

    public long? CounterCallId { get; init; }
    public int? CounterQuoteStartMs { get; init; }

    /// <summary>
    /// True when the evidence comes from audio the transcriber was unsure about. Shown as
    /// "ses net değil" and excluded from automatic detection.
    /// </summary>
    public bool LowConfidence { get; init; }

    /// <summary>
    /// True when this came from a keyword rule rather than the model. Labelled honestly in the
    /// UI so a heuristic is never mistaken for an inference.
    /// </summary>
    public bool IsHeuristic { get; init; }

    /// <summary>Dismissed flags stay dismissed, so false positives do not accumulate forever.</summary>
    public bool DismissedByUser { get; init; }

    /// <summary>
    /// Which machinery wrote this: "pipeline" (the per-call analysis) or "consistency" (the
    /// user-triggered consistency check). Ownership, not decoration — each writer clears and
    /// rewrites only its own rows, so a ledger rebuild cannot erase a paid consistency run
    /// and a consistency re-run cannot erase the ledger's findings.
    /// </summary>
    public string Source { get; init; } = Sources.Pipeline;

    /// <summary>The model's own confidence for consistency findings: dusuk/orta/yuksek. Null for pipeline flags.</summary>
    public string? Confidence { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static class Sources
    {
        public const string Pipeline = "pipeline";
        public const string Consistency = "consistency";
    }
}

/// <summary>
/// A short per-contact summary of one call, written by the model from the extracted structure
/// rather than from the raw transcript.
/// </summary>
public sealed record CallSummary
{
    public long Id { get; init; }
    public required long CallId { get; init; }
    public required string Summary { get; init; }
    public string? ActionItems { get; init; }
    public string? ModelUsed { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
