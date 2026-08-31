using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Measuring what the machine actually did.
///
/// The figure that matters is transcription speed against real time. Below one, an hour of
/// conversation costs more than an hour to process, so the backlog grows for as long as calls keep
/// being made — and while that happens a working application is indistinguishable from a hung one.
/// It was already true of this product on a machine without a usable GPU, observed at 0.4× on a
/// forty-seven minute call, and nothing anywhere said so.
/// </summary>
public sealed class UsageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-usage-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public UsageTests()
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

    private long Call() => _repo.InsertCall(new Call
    {
        App = CallApp.WhatsApp,
        StartedAt = DateTimeOffset.Parse("2026-08-31T10:00:00+03:00"),
        State = ProcessingState.Analysed,
    });

    [Fact]
    public void WithNothingRecordedTheTotalsAreEmptyRatherThanNull()
    {
        var usage = _repo.Usage(ProcessingStage.Transcribe);

        Assert.True(usage.IsEmpty);
        Assert.Equal(0, usage.Runs);
        Assert.Null(usage.SpeedFactor);
    }

    /// <summary>
    /// Ten minutes of audio in five minutes of work is twice real time. Getting this ratio
    /// inverted would report a machine that cannot keep up as one running at double speed.
    /// </summary>
    [Fact]
    public void SpeedIsAudioOverElapsedNotTheOtherWayAround()
    {
        _repo.RecordRun(
            Call(), ProcessingStage.Transcribe, "whisper-large-v3",
            DateTimeOffset.UtcNow,
            elapsed: TimeSpan.FromMinutes(5),
            audio: TimeSpan.FromMinutes(10));

        var usage = _repo.Usage(ProcessingStage.Transcribe);

        Assert.Equal(2.0, usage.SpeedFactor!.Value, precision: 3);
    }

    /// <summary>The case the screen exists for: slower than real time.</summary>
    [Fact]
    public void WorkSlowerThanRealTimeIsReportedBelowOne()
    {
        _repo.RecordRun(
            Call(), ProcessingStage.Transcribe, "whisper-large-v3",
            DateTimeOffset.UtcNow,
            elapsed: TimeSpan.FromHours(3.5),
            audio: TimeSpan.FromMinutes(47));

        Assert.True(_repo.Usage(ProcessingStage.Transcribe).SpeedFactor < 1);
    }

    [Fact]
    public void RunsAreSummedAcrossCalls()
    {
        foreach (var _ in Enumerable.Range(0, 3))
        {
            _repo.RecordRun(
                Call(), ProcessingStage.Transcribe, "whisper-large-v3",
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));
        }

        var usage = _repo.Usage(ProcessingStage.Transcribe);

        Assert.Equal(3, usage.Runs);
        Assert.Equal(TimeSpan.FromMinutes(12), usage.Audio);
        Assert.Equal(2.0, usage.SpeedFactor!.Value, precision: 3);
    }

    /// <summary>
    /// The two stages are counted apart. Mixed, the token spend of analysis would be divided by
    /// the audio hours of transcription and produce a speed figure that means nothing.
    /// </summary>
    [Fact]
    public void StagesAreCountedSeparately()
    {
        var call = Call();

        _repo.RecordRun(call, ProcessingStage.Transcribe, "whisper", DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4));

        _repo.RecordRun(call, ProcessingStage.Analyse, "claude-haiku-4-5", DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(9), TimeSpan.Zero, promptTokens: 4000, completionTokens: 600);

        Assert.Equal(1, _repo.Usage(ProcessingStage.Transcribe).Runs);
        Assert.Equal(0, _repo.Usage(ProcessingStage.Transcribe).TotalTokens);

        var analysis = _repo.Usage(ProcessingStage.Analyse);

        Assert.Equal(4600, analysis.TotalTokens);
        Assert.Null(analysis.SpeedFactor);
    }

    [Fact]
    public void OnlyRunsInsideTheWindowAreCounted()
    {
        var call = Call();

        _repo.RecordRun(call, ProcessingStage.Transcribe, "whisper",
            DateTimeOffset.UtcNow.AddDays(-90), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

        _repo.RecordRun(call, ProcessingStage.Transcribe, "whisper",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

        Assert.Equal(2, _repo.Usage(ProcessingStage.Transcribe).Runs);
        Assert.Equal(1, _repo.Usage(ProcessingStage.Transcribe, DateTimeOffset.UtcNow.AddDays(-30)).Runs);
    }

    /// <summary>
    /// "The local model runs at 0.4× and the hosted one at 12×" is a decision somebody can act on;
    /// a single blended average across both is not.
    /// </summary>
    [Fact]
    public void EnginesAreBrokenOutAndOrderedByHowMuchTheyDid()
    {
        var call = Call();

        foreach (var _ in Enumerable.Range(0, 2))
        {
            _repo.RecordRun(call, ProcessingStage.Transcribe, "yerel-whisper",
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(4));
        }

        _repo.RecordRun(call, ProcessingStage.Transcribe, "bulut-whisper",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(12));

        var engines = _repo.UsageByEngine(ProcessingStage.Transcribe);

        Assert.Equal(2, engines.Count);
        Assert.Equal("yerel-whisper", engines[0].Engine);
        Assert.Equal(2, engines[0].Runs);
        Assert.True(engines[0].SpeedFactor < 1);
        Assert.True(engines[1].SpeedFactor > 1);
    }

    [Fact]
    public void FailedAttemptsAreCountedWithoutBeingHidden()
    {
        var call = Call();

        _repo.RecordRun(call, ProcessingStage.Analyse, "model", DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(3), TimeSpan.Zero, succeeded: false);

        var usage = _repo.Usage(ProcessingStage.Analyse);

        Assert.Equal(1, usage.Runs);
        Assert.Equal(1, usage.Failures);
    }

    /// <summary>
    /// Deleting a call takes its usage rows with it, so "her şey silinecek" stays literally true.
    /// The totals shrink, which is the honest outcome of deleting the work they describe.
    /// </summary>
    [Fact]
    public void DeletingACallRemovesWhatItCost()
    {
        var call = Call();

        _repo.RecordRun(call, ProcessingStage.Transcribe, "whisper", DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

        Assert.Equal(1, _repo.Usage(ProcessingStage.Transcribe).Runs);

        _repo.DeleteCall(call);

        Assert.Equal(0, _repo.Usage(ProcessingStage.Transcribe).Runs);
    }

    /// <summary>
    /// Bookkeeping must never break the thing it is measuring. A statistics insert that threw
    /// would turn a finished transcript into a failed call.
    /// </summary>
    [Fact]
    public void RecordingAgainstACallThatDoesNotExistIsSwallowed()
    {
        _repo.RecordRun(
            callId: 999_999, ProcessingStage.Transcribe, "whisper",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));

        // The foreign key rejected it, and nothing was thrown at the caller.
        Assert.Equal(0, _repo.Usage(ProcessingStage.Transcribe).Runs);
    }
}
