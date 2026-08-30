namespace VoiceTranscript.Core.Domain;

public enum CallApp
{
    Unknown = 0,
    WhatsApp = 1,
    Telegram = 2,
}

public enum CallDirection
{
    Unknown = 0,
    Incoming = 1,
    Outgoing = 2,
}

public enum CallKind
{
    /// <summary>One other participant. The dual-stream capture attributes speech exactly.</summary>
    OneToOne = 0,

    /// <summary>
    /// Three or more participants. Every remote party arrives mixed into a single loopback
    /// stream, so attribution is no longer a fact. These calls are archived as audio only:
    /// no transcript and no analysis.
    /// </summary>
    Group = 1,
}

public enum ProcessingState
{
    Recorded = 0,
    Queued = 1,
    Transcribing = 2,
    Transcribed = 3,
    Analysing = 4,
    Analysed = 5,
    Failed = 6,

    /// <summary>Deliberately not processed — a group call, or the user declined to keep it.</summary>
    Skipped = 7,
}

public sealed record Contact
{
    public long Id { get; init; }

    /// <summary>Display name as the user knows the person.</summary>
    public required string Name { get; init; }

    /// <summary>Name folded for search. Set by the repository, never by hand.</summary>
    public string NameNormalised { get; init; } = "";

    public CallApp App { get; init; }

    /// <summary>Phone number or handle, when known. Purely informational.</summary>
    public string? Handle { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastCallAt { get; init; }
    public int CallCount { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// A learned mapping from a window title to a contact.
///
/// Telegram puts the counterpart's name in the call window title, so that case resolves itself.
/// WhatsApp titles its window "WhatsApp" and nothing else, so the user labels the call once and
/// the mapping is remembered — the application gets more accurate with use instead of relying on
/// a fragile scrape.
/// </summary>
public sealed record TitleBinding
{
    public long Id { get; init; }
    public required string TitlePattern { get; init; }
    public required long ContactId { get; init; }
    public CallApp App { get; init; }
    public int TimesUsed { get; init; }
    public DateTimeOffset LastUsedAt { get; init; }
}

public sealed record Call
{
    public long Id { get; init; }
    public long? ContactId { get; init; }
    public CallApp App { get; init; }
    public CallDirection Direction { get; init; }
    public CallKind Kind { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>The user's own microphone stream.</summary>
    public string? MicPath { get; init; }

    /// <summary>The far end, as rendered to the speakers.</summary>
    public string? FarPath { get; init; }

    public ProcessingState State { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>Window title observed while the call was up. Kept for the learned binding.</summary>
    public string? ObservedTitle { get; init; }

    /// <summary>
    /// Diagnostics from the capture layer, serialised. A recording with overlaps or re-anchors
    /// must not be trusted for speaker attribution, and the UI says so.
    /// </summary>
    public string? CaptureStats { get; init; }

    public bool LikelyNoHeadphones { get; init; }

    /// <summary>Pinned calls survive the retention sweep.</summary>
    public bool IsPinned { get; init; }

    public string? AudioSha256 { get; init; }
}

public sealed record Segment
{
    public long Id { get; init; }
    public required long CallId { get; init; }

    /// <summary>True when this is the user speaking. A fact from which file the audio was in.</summary>
    public bool IsMe { get; init; }

    public int StartMs { get; init; }
    public int EndMs { get; init; }
    public required string Text { get; init; }

    /// <summary>Folded copy used by the search index. Set by the repository.</summary>
    public string TextNormalised { get; init; } = "";

    public double? AvgLogprob { get; init; }
    public double? NoSpeechProb { get; init; }

    /// <summary>
    /// Audio the transcriber was unsure about. Numbers and dates from these segments are kept
    /// out of automatic contradiction detection: a misheard amount would otherwise become a
    /// fabricated price conflict attributed to a real person.
    /// </summary>
    public bool LowConfidence { get; init; }

    public bool OverlapsOtherSpeaker { get; init; }
    public bool SuspectedEcho { get; init; }

    public TimeSpan Start => TimeSpan.FromMilliseconds(StartMs);
    public TimeSpan End => TimeSpan.FromMilliseconds(EndMs);
}
