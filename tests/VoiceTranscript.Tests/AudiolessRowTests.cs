using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// A recording that never captured anything is not pending work.
///
/// When the capture device disappears mid-ring the recorder throws before writing a byte, and the
/// row is left saying "The audio device has been disconnected or the audio hardware has been
/// reconfigured", duration 00:00, no files. Nothing about it can change: the retry button is
/// disabled for it and requeueing already skips it — a real screen read "7 görüşme yeniden kuyruğa
/// alındı. 2 tanesi atlandı".
///
/// The waiting list had not been told, so two such rows from two nights earlier sat permanently at
/// the top of "Bekleyenler" with a red 2 beside them that no amount of work could clear. That is
/// the same fault the pending count itself was written to fix, arriving from a different direction.
/// </summary>
public class AudiolessRowTests
{
    private static ProcessingRow Row(ProcessingState state, string? mic, int segments = 0) =>
        new(new Call
        {
            Id = 1,
            App = CallApp.Telegram,
            StartedAt = DateTimeOffset.Parse("2026-09-02T00:55:00+03:00"),
            State = state,
            MicPath = mic,
            FailureReason = "The audio device has been disconnected or the audio hardware has been reconfigured.",
        }, "Bozkurt", segments);

    [Fact]
    public void ARecordingThatCapturedNothingIsNotWaitingForTranscription()
    {
        Assert.False(Row(ProcessingState.Failed, mic: null).NeedsTranscription);
    }

    /// <summary>
    /// A failure is not pending work, whether or not the audio is still there.
    ///
    /// This test used to assert the opposite, and the reasoning was sound at the time: the row is
    /// one retry away from working, and there was nowhere else it would be seen. There is now —
    /// failures have their own filter, and the first screen's notice leads to it — so counting
    /// them as waiting only made "Bekleyenler" a list nobody could empty. A real recording that
    /// came back with "konuşma bulunamadı" sat at the top of it permanently, beside a number no
    /// amount of work could clear.
    ///
    /// Nothing is hidden by this: the row is in "İşlenemeyenler" with its reason, its retry and
    /// its delete. What changed is which question it answers.
    /// </summary>
    [Fact]
    public void ARecordingThatFailedIsNotWaitingEvenWithItsAudio()
    {
        Assert.False(Row(ProcessingState.Failed, mic: @"C:\ses\call-1-mic.wav").NeedsTranscription);
    }

    [Fact]
    public void AnUntranscribedRecordingWithAudioIsStillWaiting()
    {
        Assert.True(Row(ProcessingState.Recorded, mic: @"C:\ses\call-1-mic.wav").NeedsTranscription);
    }

    /// <summary>Queued and in-flight rows answer on their state; the audio arrives with them.</summary>
    [Fact]
    public void AQueuedRecordingIsWaitingWhateverItsFilesSayYet()
    {
        Assert.True(Row(ProcessingState.Queued, mic: null).NeedsTranscription);
    }

    [Fact]
    public void AFinishedRecordingIsNotWaiting()
    {
        Assert.False(Row(ProcessingState.Analysed, mic: @"C:\ses\call-1-mic.wav", segments: 12).NeedsTranscription);
    }
}
