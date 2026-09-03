using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Which engine heard this conversation better.
///
/// The question was unanswerable, and that is the whole reason for this. Every run overwrote the
/// last, so comparing two engines meant reading a log, re-transcribing one of them by hand, and
/// hoping the audio had not changed underneath. On the call that prompted it the audio HAD
/// changed — a step between the two runs rewrote the recording — so the comparison was
/// meaningless and nothing in the product could say so.
/// </summary>
public class TranscriptHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-history-{Guid.NewGuid():N}");
    private readonly Repository _repository;
    private readonly long _callId;

    public TranscriptHistoryTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var database = new Database(paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);

        _callId = _repository.InsertCall(new Core.Domain.Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse("2026-09-03T21:51:09+03:00"),
            Duration = TimeSpan.FromMinutes(3),
            State = ProcessingState.Analysed,
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The database file can still be held briefly.
        }

        GC.SuppressFinalize(this);
    }

    private List<Segment> Lines(params (string Text, bool Uncertain)[] lines) =>
        [.. lines.Select((line, i) => new Segment
        {
            CallId = _callId,
            IsMe = i % 2 == 0,
            StartMs = i * 3000,
            EndMs = i * 3000 + 2400,
            Text = line.Text,
            LowConfidence = line.Uncertain,
            Words = [.. line.Text.Split(' ').Select((w, j) =>
                new SpokenWord(i * 3000 + (j * 300), i * 3000 + (j * 300) + 250, w))],
        })];

    private void Transcribe(string engine, double? coverage, List<Segment> lines)
    {
        _repository.ReplaceSegments(_callId, lines);
        _repository.SaveTranscriptVersion(_callId, engine, coverage, lines);
    }

    [Fact]
    public void EachRunIsKeptUnderTheEngineThatProducedIt()
    {
        Transcribe("large-v3", 0.808, Lines(("Alo ne", true), ("yapıyorsun canım", true)));
        Transcribe("stt.ex5.ai|whisper-1", 0.839, Lines(("Alo, ne yapıyorsun canım?", false)));

        var versions = _repository.ListTranscriptVersions(_callId);

        Assert.Equal(2, versions.Count);
        Assert.Equal(["stt.ex5.ai|whisper-1", "large-v3"], versions.Select(v => v.Engine));

        // Newest first, and the newest is the one the call is showing.
        Assert.True(versions[0].IsCurrent);
        Assert.False(versions[1].IsCurrent);
    }

    /// <summary>
    /// The figures are the point. A list of engine names decides nothing; how many words came
    /// back and how many lines the engine was unsure about is the comparison.
    /// </summary>
    [Fact]
    public void TheFiguresAreStoredWithIt()
    {
        Transcribe("large-v3", 0.722, Lines(("bir iki", true), ("üç dört beş", true), ("altı", false)));

        var version = _repository.ListTranscriptVersions(_callId).Single();

        Assert.Equal(3, version.SegmentCount);
        Assert.Equal(6, version.WordCount);
        Assert.Equal(2, version.LowConfidenceCount);
        Assert.Equal(0.722, version.SpeechCoverage);
        Assert.Equal(2400 * 3, version.SpokenMs);
    }

    [Fact]
    public void AStoredTranscriptCanBePutBack()
    {
        Transcribe("large-v3", 0.808, Lines(("Alo ne", true), ("yapıyorsun canım", true)));
        Transcribe("cloud-ex5", 0.839, Lines(("Alo, ne yapıyorsun canım?", false)));

        var older = _repository.ListTranscriptVersions(_callId).Single(v => v.Engine == "large-v3");

        Assert.True(_repository.RestoreTranscriptVersion(older.Id));

        var lines = _repository.GetSegments(_callId);
        Assert.Equal(["Alo ne", "yapıyorsun canım"], lines.Select(l => l.Text));

        // Word timings travel with it: without them nothing can follow the audio.
        Assert.NotEmpty(lines[0].Words);
    }

    /// <summary>
    /// Putting one back is itself a thing that happened. The list is a history, and a list that
    /// quietly reordered itself would lose "I went back to the local one at four o'clock".
    /// </summary>
    [Fact]
    public void PuttingOneBackIsRecordedAsWell()
    {
        Transcribe("large-v3", 0.808, Lines(("bir", false)));
        Transcribe("cloud-ex5", 0.839, Lines(("iki", false)));

        var older = _repository.ListTranscriptVersions(_callId).Single(v => v.Engine == "large-v3");
        _repository.RestoreTranscriptVersion(older.Id);

        var after = _repository.ListTranscriptVersions(_callId);

        Assert.Equal(3, after.Count);
        Assert.Equal("large-v3", after[0].Engine);
        Assert.True(after[0].IsCurrent);
    }

    /// <summary>Kept for comparison, not as an archive of its own — the recording is the archive.</summary>
    [Fact]
    public void OnlyTheLastTenAreKept()
    {
        for (var i = 0; i < 14; i++) Transcribe($"motor-{i}", 0.5, Lines(($"satır {i}", false)));

        var versions = _repository.ListTranscriptVersions(_callId);

        Assert.Equal(10, versions.Count);
        Assert.Equal("motor-13", versions[0].Engine);
        Assert.Equal("motor-4", versions[^1].Engine);
    }

    [Fact]
    public void ACallWithNoHistoryHasAnEmptyList()
    {
        Assert.Empty(_repository.ListTranscriptVersions(_callId));
        Assert.False(_repository.RestoreTranscriptVersion(9999));
    }

    /// <summary>Deleting a call takes its history with it; nothing is left pointing at nothing.</summary>
    [Fact]
    public void TheHistoryGoesWithTheCall()
    {
        Transcribe("large-v3", 0.8, Lines(("bir", false)));

        _repository.DeleteCall(_callId);

        Assert.Empty(_repository.ListTranscriptVersions(_callId));
    }
}
