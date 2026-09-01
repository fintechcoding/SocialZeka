namespace VoiceTranscript.Core.Domain;

/// <summary>Where a suggestion stands. Nothing here ever triggers automation.</summary>
public enum ActionStatus
{
    Open = 0,

    /// <summary>The user says it is handled.</summary>
    Done = 1,

    /// <summary>The user hid it; a re-run may never bring it back.</summary>
    Hidden = 2,

    /// <summary>Turned into a reminder or a board card by the user's own click.</summary>
    Routed = 3,
}

/// <summary>
/// One proposed next move for the USER, extracted from a conversation.
///
/// Not a commitment — commitments are what was SAID; an action is the move the conversation
/// leaves on the user's side of the table ("get the date in writing", "send the documents",
/// "ask the price question again"). Machine-owned and quote-anchored: every action rests on a
/// verbatim, verified quote, and it reaches the user's own spaces (reminders, the board) only
/// through their click.
/// </summary>
public sealed record ActionItem
{
    public long Id { get; init; }
    public required long CallId { get; init; }
    public long? ContactId { get; init; }

    /// <summary>The move, imperative and short: "Teslim tarihini yazılı teyit et".</summary>
    public required string Action { get; init; }

    /// <summary>Why this step makes sense — about the situation, never about the person.</summary>
    public string? Reason { get; init; }

    /// <summary>yazili_teyit | gonderme | soru | takip | hazirlik | diger.</summary>
    public string Kind { get; init; } = "diger";

    /// <summary>The verbatim words this action rests on. Verified; never empty.</summary>
    public required string Quote { get; init; }

    public int QuoteStartMs { get; init; }
    public bool QuoteIsMe { get; init; }

    public string? DeadlineRaw { get; init; }
    public DateOnly? DeadlineDate { get; init; }

    public ActionStatus Status { get; init; } = ActionStatus.Open;
    public string? RoutedNote { get; init; }
    public string? ModelUsed { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
