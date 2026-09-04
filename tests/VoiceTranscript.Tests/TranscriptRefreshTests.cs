using System.Net.Http;

using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The open window shows the new lines as soon as they exist, not when the summary does.
///
/// Pressing "transcribe again" on an eighteen-minute conversation replaced the transcript within
/// two minutes and then analysed it for three more. The window's only refresh signal was the one
/// that arrives after analysis, so for those three minutes it showed the old text while the new
/// text sat in the database — and the person who pressed the button was watching that exact text
/// for a change. They reported it as "the chat did not update", which is what it looked like.
/// </summary>
public class TranscriptRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-refresh-{Guid.NewGuid():N}");
    private readonly Repository _repository;
    private readonly long _callId;

    public TranscriptRefreshTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var database = new Database(paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);

        _callId = _repository.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse("2026-09-01T17:50:00+03:00"),
            Duration = TimeSpan.FromMinutes(18),
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

    private void Write(params string[] lines) =>
        _repository.ReplaceSegments(_callId, [.. lines.Select((text, i) => new Segment
        {
            CallId = _callId,
            IsMe = i % 2 == 0,
            StartMs = i * 3000,
            EndMs = i * 3000 + 2500,
            Text = text,
        })]);

    private CallWindowViewModel Open() =>
        new(_repository, () => new AppSettings(), new HttpClient(), _callId);

    [Fact]
    public void ReloadPicksUpATranscriptWrittenAfterTheWindowOpened()
    {
        Write("eski birinci", "eski ikinci");

        var model = Open();
        Assert.Equal(2, model.Turns.Count);

        Write("yeni birinci", "yeni ikinci", "yeni üçüncü");

        model.Reload();

        Assert.Equal(3, model.Turns.Count);
        Assert.Equal("yeni birinci", model.Turns[0].Text);
    }

    [Fact]
    public void ReloadingMidRunLeavesTheProgressStripUp()
    {
        // The moment the transcript is replaced the call is Transcribed and analysis has not
        // started, so nothing in the row says "working". The strip must survive that gap, or it
        // blinks off and back on between the two stages.
        Write("bir", "iki");

        var model = Open();
        model.MarkQueued();

        _repository.SetCallState(_callId, ProcessingState.Transcribed);
        Write("bir", "iki", "üç");

        model.Reload();

        Assert.True(model.IsWorking);
        Assert.Equal(3, model.Turns.Count);
    }

    [Fact]
    public void AWindowOpenedOntoAnAnalysingCallShowsTheStripFromTheFirstFrame()
    {
        Write("bir");
        _repository.SetCallState(_callId, ProcessingState.Analysing);

        var model = Open();

        Assert.True(model.IsWorking);
    }
}
