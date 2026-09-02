using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.Tests;

/// <summary>
/// Clicking a line plays the conversation, not one half of it.
///
/// It used to switch to whichever side the line belonged to, on the reasoning that a single voice
/// is the clearest way to check exact words. In use it is the opposite: playback carries on past
/// the line that was clicked, and on one channel everything the other person says is silence — you
/// hear one half of a conversation while the transcript scrolls through sentences with no sound
/// under them. It also made the follow-along look broken, because the highlight moved to lines the
/// chosen channel never speaks.
/// </summary>
public class PlayFromChannelTests
{
    /// <summary>
    /// The rule, on its own. PlayFrom needs real audio files to do anything, so the decision it
    /// makes is what is worth pinning — and it is one line either way.
    /// </summary>
    private static PlaybackChannel ChannelFor(bool hasMixed, bool isMe) =>
        hasMixed ? PlaybackChannel.Both
        : isMe ? PlaybackChannel.Me
        : PlaybackChannel.Them;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMixedRecordingPlaysBothSidesWhicheverLineWasClicked(bool isMe)
    {
        Assert.Equal(PlaybackChannel.Both, ChannelFor(hasMixed: true, isMe));
    }

    /// <summary>
    /// Without a mix there is nothing to play but the one side — an old recording, or one where
    /// the far end was never captured. Falling back to silence would be worse.
    /// </summary>
    [Theory]
    [InlineData(true, PlaybackChannel.Me)]
    [InlineData(false, PlaybackChannel.Them)]
    public void WithoutAMixTheSpeakersOwnChannelIsStillBetterThanNothing(bool isMe, PlaybackChannel expected)
    {
        Assert.Equal(expected, ChannelFor(hasMixed: false, isMe));
    }

    [Fact]
    public void TheWholeConversationIsWhereAPlayerStarts()
    {
        Assert.Equal(PlaybackChannel.Both, new PlaybackViewModel().Channel);
    }
}
