using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.Tests;

/// <summary>
/// Reading along while the recording plays.
///
/// The line being spoken was marked and the view never moved to it, so on a conversation longer
/// than a screen the marking only worked for the first few lines — which is to say it did not
/// work, because the whole point is following a voice through a transcript you are not scrolling
/// yourself.
///
/// The arithmetic is here rather than in the window because it is the part that can be wrong
/// invisibly: an offset a few pixels off looks like nothing until somebody is watching a call
/// play and the line they are listening to sits half under the player bar.
/// </summary>
public class PlayheadFollowTests
{
    private const double Viewport = 300;
    private const double Extent = 3000;

    [Fact]
    public void ALineBelowTheFoldIsBroughtUpWithRoomAboveIt()
    {
        // A third of the way down, so what was just said is still readable.
        var offset = CallWindowViewModel.FollowOffset(
            top: 1200, height: 60, current: 0, viewport: Viewport, extent: Extent);

        Assert.Equal(1200 - 80, offset);
    }

    /// <summary>
    /// A line already comfortably on screen does not move anything. The player reports twenty
    /// times a second; re-scrolling on every report would twitch the transcript under the reader
    /// even while nothing needed to move.
    /// </summary>
    [Fact]
    public void ALineAlreadyInViewIsLeftWhereItIs()
    {
        var offset = CallWindowViewModel.FollowOffset(
            top: 1150, height: 40, current: 1000, viewport: Viewport, extent: Extent);

        Assert.Equal(1000, offset);
    }

    /// <summary>Hugging an edge counts as needing a move; half a line is not readable.</summary>
    [Fact]
    public void ALineAtTheBottomEdgeIsStillBroughtUp()
    {
        var offset = CallWindowViewModel.FollowOffset(
            top: 1280, height: 40, current: 1000, viewport: Viewport, extent: Extent);

        Assert.NotEqual(1000, offset);
    }

    [Fact]
    public void TheFirstLineDoesNotScrollAboveTheTop()
    {
        var offset = CallWindowViewModel.FollowOffset(
            top: 0, height: 40, current: 500, viewport: Viewport, extent: Extent);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void TheLastLineDoesNotScrollPastTheEnd()
    {
        var offset = CallWindowViewModel.FollowOffset(
            top: 2960, height: 40, current: 0, viewport: Viewport, extent: Extent);

        Assert.Equal(Extent - Viewport, offset);
    }

    /// <summary>
    /// A speech longer than the window goes to its beginning. A third of a negative gap would
    /// scroll past the start of the very line being followed — the reader would be shown the
    /// middle of a sentence whose beginning had just been spoken.
    /// </summary>
    [Fact]
    public void ALineTallerThanTheWindowStartsAtItsFirstWords()
    {
        var offset = CallWindowViewModel.FollowOffset(
            top: 900, height: 800, current: 0, viewport: Viewport, extent: Extent);

        Assert.Equal(900, offset);
    }

    /// <summary>A transcript shorter than the window has nowhere to go.</summary>
    [Fact]
    public void AShortConversationNeverScrolls()
    {
        var offset = CallWindowViewModel.FollowOffset(
            top: 40, height: 40, current: 0, viewport: Viewport, extent: 200);

        Assert.Equal(0, offset);
    }

    // ---- yielding to the reader, and coming back ---------------------------
    //
    // Following used to stop at the first turn of the wheel and stay stopped until the player was
    // pressed again. With the audio still playing and the highlight moving somewhere below the
    // fold, that reads as the feature being broken rather than as the window being polite.

    /// <summary>Untouched, the transcript follows.</summary>
    [Fact]
    public void AReaderWhoHasNotScrolledIsFollowed()
    {
        Assert.True(CallWindowViewModel.ShouldFollow(nowMs: 500_000, lastManualScrollMs: 0));
    }

    /// <summary>Scrolling back to re-read wins immediately.</summary>
    [Fact]
    public void ScrollingByHandStopsTheTranscriptMoving()
    {
        Assert.False(CallWindowViewModel.ShouldFollow(nowMs: 500_000, lastManualScrollMs: 499_000));
    }

    /// <summary>And it is a pause, not a switch: the audio is still playing.</summary>
    [Fact]
    public void FollowingComesBackOnceTheReaderStops()
    {
        var now = 500_000L;
        var scrolled = now - CallWindowViewModel.ResumeFollowingAfterMs;

        Assert.True(CallWindowViewModel.ShouldFollow(now, scrolled));
        Assert.False(CallWindowViewModel.ShouldFollow(now - 1, scrolled));
    }
}
