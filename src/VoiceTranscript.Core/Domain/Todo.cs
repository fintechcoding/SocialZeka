namespace VoiceTranscript.Core.Domain;

/// <summary>
/// Something the user wrote down to do.
///
/// The only kind of to-do the application does not derive: suggestions come from analysis,
/// reminders hang on calls, and this is the line typed on the to-do page. It may point at a
/// person or a conversation, and loses that pointer rather than being deleted when they go.
/// </summary>
public sealed record Todo
{
    public long Id { get; init; }
    public required string Text { get; init; }
    public DateOnly? DueDate { get; init; }
    public DateTimeOffset? DoneAt { get; init; }
    public long? ContactId { get; init; }
    public string? ContactName { get; init; }
    public long? CallId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
