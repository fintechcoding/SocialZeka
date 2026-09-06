using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// What the first screen calls a queue.
///
/// The count included Transcribed, which is a resting state rather than a queue: with no analysis
/// model connected every call finishes there and stays. So the figure equalled the total number of
/// calls and could never fall. On a real screen it read "13 görüşme … 13 işlem bekliyor" — the same
/// number twice, one of them presented as a backlog that would never clear, and the health screen
/// said "13 kayıt sırada" about an archive with nothing queued at all.
/// </summary>
public sealed class PendingWorkTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-pending-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public PendingWorkTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private void Call(ProcessingState state) => _repo.InsertCall(new Call
    {
        App = CallApp.WhatsApp,
        StartedAt = DateTimeOffset.Parse("2026-08-31T10:00:00+03:00"),
        State = state,
    });

    /// <summary>
    /// The exact situation on the owner's machine: everything transcribed, nothing analysed,
    /// because no model is connected. Nothing is queued, and the screen must say so.
    /// </summary>
    [Fact]
    public void TranscribedCallsAreNotAQueue()
    {
        for (var i = 0; i < 13; i++) Call(ProcessingState.Transcribed);

        Assert.Equal(0, _repo.PendingWorkCount());
        Assert.Equal(13, _repo.UnanalysedCount());
    }

    [Fact]
    public void WorkThatGenuinelyRemainsIsStillCounted()
    {
        Call(ProcessingState.Recorded);
        Call(ProcessingState.Queued);
        Call(ProcessingState.Transcribing);
        Call(ProcessingState.Analysing);

        Assert.Equal(4, _repo.PendingWorkCount());
        Assert.Equal(0, _repo.UnanalysedCount());
    }

    /// <summary>Finished, failed and skipped calls are not waiting for anything.</summary>
    [Fact]
    public void SettledCallsAreCountedInNeither()
    {
        Call(ProcessingState.Analysed);
        Call(ProcessingState.Failed);
        Call(ProcessingState.Skipped);

        Assert.Equal(0, _repo.PendingWorkCount());
        Assert.Equal(0, _repo.UnanalysedCount());
    }

    /// <summary>
    /// The two figures answer different questions and must be able to disagree. Before the fix
    /// they could not: every unanalysed call was also counted as queued.
    /// </summary>
    [Fact]
    public void TheTwoFiguresAreIndependent()
    {
        Call(ProcessingState.Queued);
        for (var i = 0; i < 5; i++) Call(ProcessingState.Transcribed);

        Assert.Equal(1, _repo.PendingWorkCount());
        Assert.Equal(5, _repo.UnanalysedCount());
    }

    [Fact]
    public void AnEmptyArchiveHasNothingOfEither()
    {
        Assert.Equal(0, _repo.PendingWorkCount());
        Assert.Equal(0, _repo.UnanalysedCount());
    }

    /// <summary>
    /// The application dies while a conversation is being analysed. On the next start it is not
    /// sent back to a transcriber.
    ///
    /// This one is money. "Analyse again, do not transcribe again" was held in a dictionary inside
    /// the process, so a crash took the request with it: the queue reset the call from Analysing
    /// to Queued, the next start found nothing in memory, and an hour of audio went to a paid
    /// transcriber a second time to produce text that was already in the database. Nothing on
    /// screen said so — it looks exactly like ordinary processing.
    ///
    /// The rule is asked of the database instead, and both halves are pinned here because
    /// separately neither is the bug: the queue really does hand the call back, and the decision
    /// really does have to say no anyway. Goes red the moment the answer depends again on
    /// something that did not survive the restart.
    /// </summary>
    [Fact]
    public void ACrashDuringAnalysisDoesNotSendTheAudioBackToTheTranscriber()
    {
        var call = _repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse("2026-09-07T10:00:00+03:00"),
            State = ProcessingState.Analysing,
        });

        _repo.ReplaceSegments(call,
        [
            new Segment { CallId = call, IsMe = false, StartMs = 0, EndMs = 2000, Text = "Bir saatlik arama." },
        ]);

        // The restart: the queue takes back everything that was mid-flight, and nothing in memory
        // remembers why this one was there.
        var waiting = _repo.CallsAwaitingProcessing();
        Assert.Contains(waiting, c => c.Id == call);

        Assert.False(VoiceTranscript.App.Services.CallOrchestrator.MustTranscribe(
            hasTranscript: _repo.CountSegments(call) > 0,
            retranscribeRequested: false));
    }

    /// <summary>
    /// The two cases that must still go through a transcriber: a recording nobody has read yet,
    /// and one the user deliberately sent back with another engine.
    ///
    /// Goes red when the rule hardens into "never transcribe a call twice", which would leave the
    /// reprocess dialog's engine picker with nothing to do.
    /// </summary>
    [Fact]
    public void AFreshRecordingAndADeliberateRedoStillGoThrough()
    {
        Assert.True(VoiceTranscript.App.Services.CallOrchestrator.MustTranscribe(
            hasTranscript: false, retranscribeRequested: false));

        Assert.True(VoiceTranscript.App.Services.CallOrchestrator.MustTranscribe(
            hasTranscript: true, retranscribeRequested: true));
    }
}
