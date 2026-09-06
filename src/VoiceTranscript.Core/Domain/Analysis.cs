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

    /// <summary>When the row was written. Null for rows from before this was recorded.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the user marked it kept. Null while it is not.</summary>
    public DateTimeOffset? FulfilledAt { get; init; }

    /// <summary>The user's last ruling of any kind — kept, dismissed, reopened, brought back.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>
    /// The user's own deadline, when they postponed it. <see cref="DeadlineDate"/> stays what the
    /// words said; this is what they changed it to, and it wins wherever a date is shown or
    /// counted. A re-run never touches a row that has one — and the deterministic check for a
    /// moved deadline reads the spoken date only, so a postponement is never held against the
    /// other person as a slipped promise.
    /// </summary>
    public DateOnly? UserDeadlineDate { get; init; }

    /// <summary>The user's rewording of the obligation. The quote itself is never edited.</summary>
    public string? UserObligation { get; init; }

    public DateTimeOffset? EditedAt { get; init; }

    /// <summary>The date that counts: the user's, when they set one, otherwise the spoken one.</summary>
    public DateOnly? EffectiveDeadline => UserDeadlineDate ?? DeadlineDate;

    /// <summary>The wording that is shown: the user's, when they gave one.</summary>
    public string EffectiveObligation => string.IsNullOrWhiteSpace(UserObligation) ? Obligation : UserObligation;

    /// <summary>True when the user changed the date or the wording; such a row survives re-runs.</summary>
    public bool IsEdited => EditedAt is not null;

    public bool IsOverdue(DateOnly today) =>
        Status == CommitmentStatus.Open && EffectiveDeadline is { } due && due < today;
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

    /// <summary>When the user last ruled on it — dismissed, or brought back. Null: never.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>
    /// Which stored transcript the quote was located in.
    ///
    /// Null when nothing recorded it — every row written before v21 — and the screen reads that
    /// as "bilinmiyor", never as "bayat". The difference matters more here than anywhere else in
    /// the schema: the label would be attached to an accusation about a person.
    /// </summary>
    public long? TranscriptVersionId { get; init; }

    public static class Sources
    {
        public const string Pipeline = "pipeline";
        public const string Consistency = "consistency";
    }
}

/// <summary>
/// One labelled tactic quote, verified against the transcript, filed under the person it was
/// said to. What the contact card counts under "Kalıplar".
///
/// The row is a label and a sentence somebody actually said, and that is the whole of it. The
/// assessment's suspicion level and its written argument are NOT here and never will be: they
/// stay in deception_note, which nothing joins. Nor does anything in this table go back into a
/// prompt — a run that could read its own earlier labels would be building a case on itself
/// rather than on the conversation.
///
/// <see cref="ByMe"/> comes from the stream the quote was found in, never from a field a model
/// filled in. Naming the wrong party as the manipulative one is the worst single output this
/// application can produce.
/// </summary>
public sealed record TacticEvidence
{
    public long Id { get; init; }
    public required long CallId { get; init; }
    public long? ContactId { get; init; }

    /// <summary>Which stored transcript the quote was verified against. Null when unrecorded.</summary>
    public long? TranscriptVersionId { get; init; }

    /// <summary>One of <see cref="Sources"/>. Ownership, like <see cref="Flag.Source"/>.</summary>
    public string Source { get; init; } = Sources.Deception;

    /// <summary>One of <see cref="Whitelist"/>. Anything else is dropped rather than filed.</summary>
    public required string Tactic { get; init; }

    /// <summary>Read off the recorded stream, not off the model's "konusan" field.</summary>
    public bool ByMe { get; init; }

    /// <summary>Verbatim words, located in the transcript before the row was written.</summary>
    public required string Quote { get; init; }

    public int QuoteStartMs { get; init; }

    /// <summary>Carried from the located line: uncertain audio is shown greyed, never counted silently.</summary>
    public bool LowConfidence { get; init; }

    public string? ModelUsed { get; init; }

    /// <summary>A tombstone: the row stays so the next run cannot put the same sentence back.</summary>
    public bool DismissedByUser { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static class Sources
    {
        /// <summary>The opt-in assessment's verified tactic lines.</summary>
        public const string Deception = "deception";

        /// <summary>The extraction's pressure signs, behind their own gate until measured.</summary>
        public const string Pipeline = "pipeline";
    }

    /// <summary>
    /// The eight labels the opt-in assessment may use — the enum in its schema, ASCII so the
    /// stored value is the same string on every machine and in every export.
    /// </summary>
    public static readonly string[] AssessmentTactics =
    [
        "baski", "sucluluk", "kacamak", "geri_yazim",
        "asiri_iltifat", "aciliyet", "tehdit_imasi", "celiski_ortme",
    ];

    /// <summary>
    /// The extraction's own pressure vocabulary that has no equivalent above. "aciliyet" is
    /// shared; "suclama", "tehdit" and "iltifat" are kept apart from "sucluluk",
    /// "tehdit_imasi" and "asiri_iltifat" rather than folded into them, because the card
    /// counts by (label, source) and folding two vocabularies into one would make a row that
    /// pools two different questions asked of two different prompts.
    /// </summary>
    public static readonly string[] PressureSigns =
    [
        "kitlik", "otorite", "suclama", "tehdit", "iltifat",
    ];

    /// <summary>Every label that may be stored. "diger" is deliberately not one of them.</summary>
    public static readonly IReadOnlySet<string> Whitelist =
        new HashSet<string>([.. AssessmentTactics, .. PressureSigns], StringComparer.Ordinal);

    /// <summary>
    /// The label if it is one this code knows, null otherwise — and null means the line is
    /// dropped, not filed under a catch-all. Counting an unrecognised word as a pattern on
    /// somebody's card is the failure this refuses.
    /// </summary>
    public static string? Recognise(string? tactic)
    {
        var trimmed = tactic?.Trim().ToLowerInvariant();
        return trimmed is not null && Whitelist.Contains(trimmed) ? trimmed : null;
    }
}

/// <summary>
/// One thing said that the conversation turns on: today, a direct question and what happened to
/// it. The extraction has always found these; until now they lived for the length of one run.
///
/// Machine evidence with the same rules as everything else here — a verified quote, the moment
/// it was said, and the stream it was found in. <see cref="AnswerStatus"/> is one of four words
/// or null; it is not a score, and nothing in this table is ever fed back to a model.
/// </summary>
public sealed record SpeechAct
{
    public long Id { get; init; }
    public required long CallId { get; init; }
    public long? ContactId { get; init; }

    /// <summary>True when the user asked it. The contact was asked whenever this is true.</summary>
    public bool ByMe { get; init; }

    /// <summary>One of <see cref="Kinds"/>. Only questions are written today.</summary>
    public string Kind { get; init; } = Kinds.Question;

    /// <summary>One of <see cref="Statuses"/>, or null when the model said nothing recognisable.</summary>
    public string? AnswerStatus { get; init; }

    public required string Quote { get; init; }
    public int QuoteStartMs { get; init; }
    public bool LowConfidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>True when the question got no real answer — the two words the card counts.</summary>
    public bool WentUnanswered => AnswerStatus is Statuses.Evasive or Statuses.Deflected;

    public static class Kinds
    {
        public const string Question = "soru";
    }

    public static class Statuses
    {
        public const string Answered = "cevaplandi";
        public const string Partial = "kismi";
        public const string Evasive = "kacamak";
        public const string Deflected = "savusturuldu";

        public static readonly IReadOnlySet<string> All =
            new HashSet<string>([Answered, Partial, Evasive, Deflected], StringComparer.Ordinal);

        /// <summary>
        /// The status if it is one of the four, null otherwise. A word this code does not know
        /// is stored as "not recorded" rather than rounded to the nearest one — the question
        /// itself is evidence, the guess about its answer would not be.
        /// </summary>
        public static string? Recognise(string? status)
        {
            var trimmed = status?.Trim().ToLowerInvariant();
            return trimmed is not null && All.Contains(trimmed) ? trimmed : null;
        }
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

    /// <summary>Which stored transcript it was written from; null when that was not recorded.</summary>
    public long? TranscriptVersionId { get; init; }
}
