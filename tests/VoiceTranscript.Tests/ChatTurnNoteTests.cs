using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.Tests;

/// <summary>
/// What a bubble says about itself beyond its words.
///
/// Both notes change how the line reads, which is why they are worth the space. "Tamam" said into
/// a pause is agreement; the same word over the top of somebody else is an interruption, and a
/// ledger entry quoting it without saying which is quoting half a fact. Echo is stronger still: it
/// questions whether the line was said on this side at all.
/// </summary>
public class ChatTurnNoteTests
{
    private static ChatTurn Turn(bool overlaps = false, bool echo = false) =>
        new("Sen", "Tamam.", 1000, 1400, isMe: true, lowConfidence: false, overlaps, echo);

    [Fact]
    public void AnOrdinaryLineSaysNothingExtra()
    {
        Assert.Null(Turn().Note);
        Assert.False(Turn().HasNote);
    }

    [Fact]
    public void ALineSpokenOverSomebodyElseSaysSo()
    {
        Assert.Equal("üst üste", Turn(overlaps: true).Note);
    }

    /// <summary>
    /// Echo outranks overlap, and always co-occurs with it — bleed is by definition simultaneous.
    /// "Whether this was said here at all" is the more important of the two things to know.
    /// </summary>
    [Fact]
    public void EchoOutranksOverlapBecauseItQuestionsTheLineItself()
    {
        Assert.Equal("yankı", Turn(overlaps: true, echo: true).Note);
    }

    [Fact]
    public void NeitherNoteHidesTheLine()
    {
        // Marked, never deleted: a genuine simultaneous "aynen" cannot be told from loudspeaker
        // bleed, and deleting would erase real speech.
        Assert.Equal("Tamam.", Turn(overlaps: true, echo: true).Text);
    }
}
