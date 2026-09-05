namespace VoiceTranscript.Core.Domain;

/// <summary>Whether a derived note still describes the transcript the call shows.</summary>
public enum Staleness
{
    /// <summary>There is no such note.</summary>
    Absent,

    /// <summary>Written from the transcript on screen.</summary>
    Fresh,

    /// <summary>Written before this was recorded, or the call has no transcript pointer: not known — and never called stale.</summary>
    Unknown,

    /// <summary>Written from an earlier transcript; its quotes may no longer be in the text.</summary>
    Stale,
}

/// <summary>
/// Every derived note of one call, judged against the transcript the call currently shows.
///
/// The fault this answers: transcribing a call again replaced its lines and left the reading, the
/// assessment, the summary and the suggestions standing, quoting words the screen no longer
/// showed, with nothing on any of them to say so. A note is not deleted when the text changes —
/// a reading was paid for, and a dismissed finding is the user's ruling — but it is labelled.
/// </summary>
public sealed record DerivedFreshness(
    long? CurrentVersionId,
    Staleness Summary,
    Staleness Reading,
    Staleness Deception,
    Staleness Consistency,
    Staleness Actions)
{
    public bool AnyStale =>
        Summary == Staleness.Stale || Reading == Staleness.Stale || Deception == Staleness.Stale
        || Consistency == Staleness.Stale || Actions == Staleness.Stale;

    /// <summary>The rule, in one place: absent, then unknown, then compared.</summary>
    public static Staleness Judge(long count, long? noteVersion, long? current) =>
        count == 0 ? Staleness.Absent
        : noteVersion is null || current is null ? Staleness.Unknown
        : noteVersion == current ? Staleness.Fresh
        : Staleness.Stale;
}
