using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// The self-test has to distinguish "recorded silence" from "recorded nothing", because the
/// failure modes that matter produce a stream that looks perfectly healthy from the outside.
/// The verdict logic is pure, so it can be checked here without any audio hardware.
/// </summary>
public class CaptureSelfTestTests
{
    private static VoiceTranscript.Capture.CaptureTestResult Result(
        long micPackets, long farPackets, double micPeak, double farPeak, string? error = null)
        => new("test", error is null, micPackets, farPackets, micPeak, farPeak, error);

    [Fact]
    public void BothStreamsCarryingAudioIsAPass()
    {
        var result = Result(400, 380, 0.42, 0.31);

        Assert.True(result.MicrophoneWorks);
        Assert.True(result.LoopbackWorks);
        Assert.Contains("çalışıyor", result.Summary);
    }

    /// <summary>
    /// The failure worth catching. Correctly-sized buffers full of zeroes are exactly what the
    /// per-process path has been observed returning for some VoIP clients, and counting packets
    /// alone would report that as success — then record an entire call of nothing.
    /// </summary>
    [Fact]
    public void PacketsFullOfSilenceAreNotCountedAsWorking()
    {
        var result = Result(400, 400, 0.35, 0.0);

        Assert.True(result.MicrophoneWorks);
        Assert.False(result.LoopbackWorks);
        Assert.Contains("hoparlöre giden sesten kayıt alınamadı", result.Summary);
    }

    [Fact]
    public void NoMicrophonePacketsIsReportedSeparately()
    {
        var result = Result(0, 400, 0, 0.3);

        Assert.False(result.MicrophoneWorks);
        Assert.Contains("mikrofondan ses gelmedi", result.Summary);
    }

    [Fact]
    public void CompleteSilenceAsksTheUserToTryAgainProperly()
    {
        var result = Result(400, 400, 0.0001, 0.0001);

        Assert.Contains("Hiçbir akıştan ses gelmedi", result.Summary);
    }

    [Fact]
    public void AStartFailureIsReportedWithItsReason()
    {
        var result = Result(0, 0, 0, 0, "ses cihazı bulunamadı");

        Assert.False(result.Succeeded);
        Assert.Contains("ses cihazı bulunamadı", result.Summary);
    }

    /// <summary>Room tone and line noise must not be mistaken for a working stream.</summary>
    [Fact]
    public void VeryQuietNoiseDoesNotCountAsAudio()
        => Assert.False(Result(400, 400, 0.001, 0.001).LoopbackWorks);
}
