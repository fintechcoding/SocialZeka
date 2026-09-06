using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// What the transcription service heard that was not a word.
///
/// Two rules matter and both are easy to break by accident. An event is never a transcript line —
/// a "(laughter)" in the words would be a sentence nobody said, and this product quotes what
/// people said. And an event belongs to the transcript that reported it, so transcribing again
/// replaces the set rather than adding a second engine's reading of the same moment beside the
/// first.
/// </summary>
public sealed class AudioEventTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-events-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;
    private readonly long _callId;

    public AudioEventTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repository = new Repository(database);

        _callId = _repository.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(4),
            State = ProcessingState.Analysed,
        });
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private List<Segment> Lines(params string[] texts) =>
        [.. texts.Select((text, i) => new Segment
        {
            CallId = _callId, IsMe = i % 2 == 0, StartMs = i * 3000, EndMs = i * 3000 + 2400, Text = text,
        })];

    /// <summary>Goes red when a second transcription leaves the first engine's events beside its own.</summary>
    [Fact]
    public void ANewTranscriptReplacesTheEventsRatherThanAddingToThem()
    {
        var lines = Lines("Alo", "Buyur");
        _repository.ReplaceSegments(_callId, lines);
        var first = _repository.SaveTranscriptVersion(_callId, "scribe_v2", 0.9, lines);

        _repository.ReplaceAudioEvents(_callId, [("far", 12_000, 12_800, "laughter")]);

        var stored = Assert.Single(_repository.AudioEventsOf(_callId));
        Assert.Equal("laughter", stored.Kind);
        Assert.Equal("far", stored.Channel);
        Assert.Equal(12_000, stored.StartMs);

        // A different engine, which reports nothing of the kind: the old reading goes.
        _repository.SaveTranscriptVersion(_callId, "large-v3", 0.8, lines);
        _repository.ReplaceAudioEvents(_callId, []);

        Assert.Empty(_repository.AudioEventsOf(_callId));
        Assert.NotEqual(0, first);
    }

    /// <summary>
    /// Goes red when an event without a name is stored. A row that cannot say what was heard is
    /// a mark on the timeline with nothing behind it — worse than no mark.
    /// </summary>
    [Fact]
    public void AnEventWithNoNameIsNotStored()
    {
        var written = _repository.ReplaceAudioEvents(_callId,
        [
            ("far", 1000, 1500, "laughter"),
            ("far", 2000, 2500, "   "),
            ("mic", 3000, 3500, "cough"),
        ]);

        Assert.Equal(2, written);
        Assert.Equal(["laughter", "cough"], _repository.AudioEventsOf(_callId).Select(e => e.Kind).ToList());
    }

    /// <summary>
    /// Goes red when re-analysing a conversation deletes what the audio said. Events came out of
    /// the recording with the words; they are not the ledger's reasoning and no re-run of it owns
    /// them.
    /// </summary>
    [Fact]
    public void ReAnalysingTheLedgerLeavesTheEventsAlone()
    {
        _repository.ReplaceAudioEvents(_callId, [("far", 1000, 1500, "laughter")]);

        _repository.ClearAnalysis(_callId);
        _repository.ClearConsistency(_callId);

        Assert.Single(_repository.AudioEventsOf(_callId));
    }

    /// <summary>Goes red when events outlive the conversation they belong to.</summary>
    [Fact]
    public void EventsGoWithTheCall()
    {
        _repository.ReplaceAudioEvents(_callId, [("far", 1000, 1500, "laughter")]);

        _repository.DeleteCall(_callId);

        Assert.Empty(_repository.AudioEventsOf(_callId));
    }

    /// <summary>
    /// The wire shape: an event travels beside the words, never inside them. Goes red if a
    /// non-word sound ever lands in a transcript segment, where it would be quoted as speech.
    /// </summary>
    [Fact]
    public void TheWorkerReportsEventsBesideTheWords()
    {
        const string line =
            """
            {"type":"result","segments":[{"speaker":"them","start":1.0,"end":2.0,"text":"Alo"}],
             "audio_events":[{"channel":"far","start_ms":2500,"end_ms":3100,"kind":"laughter"}],
             "duration":10.0}
            """;

        var result = Assert.IsType<WorkerResult>(WorkerProtocol.ParseLine(line.ReplaceLineEndings(" ")));

        var segment = Assert.Single(result.Segments);
        Assert.Equal("Alo", segment.Text);
        Assert.DoesNotContain("laughter", segment.Text, StringComparison.OrdinalIgnoreCase);

        var heard = Assert.Single(result.AudioEvents);
        Assert.Equal("far", heard.Channel);
        Assert.Equal(2500, heard.StartMs);
        Assert.Equal("laughter", heard.Kind);
    }

    /// <summary>An engine that labels nothing sends nothing, and that is an empty list rather than a null.</summary>
    [Fact]
    public void AnEngineThatLabelsNothingSendsAnEmptyList()
    {
        var result = Assert.IsType<WorkerResult>(WorkerProtocol.ParseLine(
            """{"type":"result","segments":[],"duration":1.0}"""));

        Assert.Empty(result.AudioEvents);
    }
}
