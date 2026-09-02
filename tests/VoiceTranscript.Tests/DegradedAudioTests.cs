using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// Telling the recordings that can be transcribed again from the ones that cannot.
///
/// The archive was written at 24 kbps for a while, chosen when it was only going to be listened
/// to. It is not: "Yeniden yazıya dök" decodes it back and transcribes that, and 24 kbps is on the
/// wrong side of a measured cliff — the same recording gave 1624 words at 21.5 kbps and 330 at
/// 18.2. Those files cannot be improved; a second attempt reads exactly what the first one did.
///
/// Measured, not dated. A cut-off by timestamp would be a guess about which build wrote which
/// file, wrong for anybody who updated late, and impossible to check. The size against the length
/// of the call is the thing itself.
/// </summary>
public class DegradedAudioTests
{
    /// <summary>Sizes and durations taken from a real archive, before the bitrate was raised.</summary>
    [Theory]
    [InlineData(104_369, 43)]        // call-19-mic  ~19 kbps
    [InlineData(3_421_704, 1134)]    // call-22-mic  ~24 kbps
    [InlineData(1_718_894, 668)]     // call-43-mic  ~21 kbps
    public void TheOldArchivesAreRecognised(long bytes, int seconds)
    {
        Assert.True(DegradedAudio.IsDegraded(bytes, TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>What the archive writes now: 64 kbps, undershooting to roughly 55 on speech.</summary>
    [Theory]
    [InlineData(55_000)]
    [InlineData(64_000)]
    [InlineData(96_000)]
    public void ArchivesWrittenSinceTheFixAreLeftAlone(int bitrate)
    {
        var seconds = 600;
        var bytes = (long)bitrate * seconds / 8;

        Assert.False(DegradedAudio.IsDegraded(bytes, TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>
    /// Nothing measurable, nothing removed. This answer deletes recordings permanently, so the
    /// unmeasurable ones are kept: a bad recording costs disk, a wrongly removed one costs a
    /// conversation.
    /// </summary>
    [Theory]
    [InlineData(0, 60)]
    [InlineData(500_000, 0)]
    [InlineData(0, 0)]
    public void WhatCannotBeMeasuredIsNotCondemned(long bytes, int seconds)
    {
        Assert.False(DegradedAudio.IsDegraded(bytes, TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>
    /// The line sits between the two settings, not next to either, so neither the undershoot of
    /// the old one nor the variation of the new one reaches it.
    /// </summary>
    [Fact]
    public void TheLineIsClearOfBothSettings()
    {
        Assert.InRange(DegradedAudio.QualityFloorBps, 30_000, 55_000);
    }

    [Fact]
    public void TheBitrateIsWhatTheFileActuallyHolds()
    {
        Assert.Equal(64_000, DegradedAudio.BitrateOf(80_000, TimeSpan.FromSeconds(10)));
    }
}
