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
    /// The distinction is the audio, not the failure. A recording that exists and failed to
    /// transcribe is exactly what the waiting list is for — it is one retry away from working, and
    /// hiding it would lose a conversation the user still has on disk.
    /// </summary>
    [Fact]
    public void ARecordingThatExistsAndFailedIsStillWaiting()
    {
        Assert.True(Row(ProcessingState.Failed, mic: @"C:\ses\call-1-mic.wav").NeedsTranscription);
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
