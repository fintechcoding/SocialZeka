namespace VoiceTranscript.Core.Domain;

/// <summary>
/// The four places a conversation can sit while it is still open.
///
/// Fixed rather than user-named, and that is a design decision worth defending. Three reasons:
///
///   The first screen can only say "Bende: 3 · Onlarda: 1" if the shell knows those words.
///
///   Every empty column can carry a sentence saying what belongs in it. A board that opens with no
///   columns and a "create a column" button answers an empty screen with a second empty screen.
///
///   A lane is a bucket applied to people, and buckets people invent for other people drift toward
///   judgements. "Bende" and "Onlarda" say whose move it is, which is a fact about a conversation.
///   A column somebody names themselves eventually says something about a person instead.
/// </summary>
public static class BoardLane
{
    /// <summary>Marked, not yet decided. Everything lands here first.</summary>
    public const string ToLookAt = "bakilacak";

    /// <summary>The next move is mine.</summary>
    public const string Mine = "bende";

    /// <summary>Waiting on the other person.</summary>
    public const string Theirs = "onlarda";

    /// <summary>Finished. Kept rather than deleted.</summary>
    public const string Done = "kapandi";

    /// <summary>In the order they are shown, left to right.</summary>
    public static IReadOnlyList<string> All { get; } = [ToLookAt, Mine, Theirs, Done];

    public static string NameOf(string lane) => lane switch
    {
        Mine => "Bende",
        Theirs => "Onlarda",
        Done => "Kapandı",
        _ => "Bakılacak",
    };

    /// <summary>
    /// What to say when a lane is empty.
    ///
    /// Every one of these has to be a true sentence that also explains the column, because on a
    /// new board all four are empty at once and four blank rectangles teach nobody anything.
    /// </summary>
    public static string EmptyText(string lane) => lane switch
    {
        Mine => "Senin yapman gereken bir şey kaldıysa buraya.",
        Theirs => "Karşı taraftan beklediklerin buraya.",
        Done => "Biten işler burada durur, silinmez.",
        _ => "Panoya attıkların önce buraya düşer.",
    };

    public static bool IsKnown(string lane) => All.Contains(lane);
}

/// <summary>
/// One conversation on the board.
/// </summary>
/// <param name="Title">What the user called it, or null to show the conversation's own heading.</param>
/// <param name="RemindOn">A day to bring it back, or null.</param>
public sealed record BoardCard
{
    public long CallId { get; set; }
    public string Lane { get; set; } = BoardLane.ToLookAt;
    public int Position { get; set; }
    public string? Title { get; set; }
    public DateOnly? RemindOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>True when the reminder has come due. Compared by day, not by instant.</summary>
    public bool IsDue => RemindOn is { } day && day <= DateOnly.FromDateTime(DateTime.Now);
}
