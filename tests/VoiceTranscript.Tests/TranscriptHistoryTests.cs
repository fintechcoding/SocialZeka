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
    /// Putting one back moves the pointer and writes nothing.
    ///
    /// It used to file a second copy of the restored transcript, so that "newest" would keep
    /// meaning "current". The reasoning was that the list is a history and going back is part of
    /// what happened — true, but the list is a history of TRANSCRIPTIONS, and a restore is not
    /// one. The cost showed up immediately in use: four presses of "use this one" left four
    /// identical rows, and a history capped at ten then evicted real transcriptions to make room
    /// for copies. The reading decision belongs in the log; the call now records which transcript
    /// it is showing, so nothing has to be duplicated to say so.
    /// </summary>
    [Fact]
    public void PuttingOneBackMovesThePointerWithoutAddingARow()
    {
        Transcribe("large-v3", 0.808, Lines(("bir", false)));
        Transcribe("cloud-ex5", 0.839, Lines(("iki", false)));

        var older = _repository.ListTranscriptVersions(_callId).Single(v => v.Engine == "large-v3");
        _repository.RestoreTranscriptVersion(older.Id);

        var after = _repository.ListTranscriptVersions(_callId);

        Assert.Equal(2, after.Count);
        Assert.Equal("large-v3", after.Single(v => v.IsCurrent).Engine);
        Assert.Equal("large-v3", _repository.CurrentTranscriptVersion(_callId)?.Engine);
    }

    /// <summary>The complaint that found this: the list grew every time it was used.</summary>
    [Fact]
    public void PressingUseThisFourTimesDoesNotGrowTheList()
    {
        Transcribe("large-v3", 0.808, Lines(("bir", false)));
        Transcribe("cloud-ex5", 0.839, Lines(("iki", false)));

        var older = _repository.ListTranscriptVersions(_callId).Single(v => v.Engine == "large-v3");

        for (var i = 0; i < 4; i++) _repository.RestoreTranscriptVersion(older.Id);

        Assert.Equal(2, _repository.ListTranscriptVersions(_callId).Count);
    }

    /// <summary>
    /// A new transcription becomes the current one, because it is what the call now shows.
    /// </summary>
    [Fact]
    public void ANewTranscriptionBecomesTheCurrentOne()
    {
        Transcribe("large-v3", 0.808, Lines(("bir", false)));
        Transcribe("cloud-ex5", 0.839, Lines(("iki", false)));

        Assert.Equal("cloud-ex5", _repository.CurrentTranscriptVersion(_callId)?.Engine);
    }

    /// <summary>
    /// The sweep may not delete the transcript on screen.
    ///
    /// Otherwise the strip goes back to being unable to say where its own text came from — which
    /// is the fault this pointer exists to fix.
    /// </summary>
    [Fact]
    public void TheOneOnScreenSurvivesTheSweep()
    {
        Transcribe("yerel-ilk", 0.9, Lines(("bir", false)));

        var kept = _repository.ListTranscriptVersions(_callId).Single();
        _repository.RestoreTranscriptVersion(kept.Id);

        for (var i = 0; i < 14; i++) Transcribe($"motor-{i}", 0.5, Lines(($"satır {i}", false)));

        // The last transcription is what the call shows now, and it is still there.
        Assert.Equal("motor-13", _repository.CurrentTranscriptVersion(_callId)?.Engine);
        Assert.Contains(_repository.ListTranscriptVersions(_callId), v => v.Engine == "motor-13");
    }

    /// <summary>A call from before the pointer existed still answers "which is current".</summary>
    [Fact]
    public void ACallWithNoPointerFallsBackToTheNewest()
    {
        Transcribe("large-v3", 0.808, Lines(("bir", false)));
        Transcribe("cloud-ex5", 0.839, Lines(("iki", false)));

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_root, "voicetranscript.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE call SET transcript_version_id = NULL;";
            command.ExecuteNonQuery();
        }

        var after = _repository.ListTranscriptVersions(_callId);

        Assert.True(after[0].IsCurrent);
        Assert.Equal("cloud-ex5", after[0].Engine);
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
