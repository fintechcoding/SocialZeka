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
}
