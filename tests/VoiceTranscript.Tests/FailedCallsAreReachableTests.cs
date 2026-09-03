using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// "4 görüşme işlenemedi · Göster" has to land somewhere those four calls are.
///
/// It did not. The button set the transcription tab's filter to "unfinished", and every kind of
/// failure the notice counts is excluded from that set for a different reason: a capture that
/// wrote no audio is deliberately not pending work, and a transcription that failed after
/// producing text reads as finished. On a real archive the card said four and the page it opened
/// said "Bekleyen iş yok" — the first screen and the page it points at, disagreeing in front of
/// the user.
///
/// The filter did not even reload, which is the same fault from the other side: the property was
/// set and nothing re-read it, so the tab's own "Bitenler" and "Hepsi" buttons only moved a
/// highlight.
/// </summary>
public class FailedCallsAreReachableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-failed-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;
    private readonly ProcessingViewModel _model;

    public FailedCallsAreReachableTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);
        _model = new ProcessingViewModel(_repository);
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

    private long Call(ProcessingState state, bool audio, int segments, string? reason = null)
    {
        var id = _repository.InsertCall(new Core.Domain.Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(1),
            State = state,
            MicPath = audio ? Path.Combine(_paths.Recordings, $"call-mic-{Guid.NewGuid():N}.ogg") : null,
            FailureReason = reason,
        });

        if (segments > 0)
        {
            _repository.ReplaceSegments(id, Enumerable.Range(0, segments).Select(i => new Segment
            {
                CallId = id,
                IsMe = i % 2 == 0,
                StartMs = i * 3000,
                EndMs = i * 3000 + 2500,
                Text = $"satır {i}",
            }));
        }

        return id;
    }

    /// <summary>The capture that never started: a reason, no audio, no text, nothing to retry.</summary>
    private long AudiolessFailure() =>
        Call(ProcessingState.Failed, audio: false, segments: 0,
            reason: "The audio device has been disconnected or the audio hardware has been reconfigured.");

    /// <summary>A re-transcription that failed over text an earlier run had already produced.</summary>
    private long FailureWithText() =>
        Call(ProcessingState.Failed, audio: true, segments: 7,
            reason: "Yapılandırılmış servislerin hiçbiri yazıya dökemedi.");

    [Fact]
    public void EveryFailureIsOnTheFailuresFilter()
    {
        var audioless = AudiolessFailure();
        var withText = FailureWithText();
        Call(ProcessingState.Analysed, audio: true, segments: 12);

        _model.TranscriptFilter = TranscriptFilter.Failed;

        Assert.Equal([audioless, withText], _model.TranscriptRows.Select(r => r.Id).Order());
    }

    /// <summary>
    /// The set the button used to open. Kept as a test rather than deleted: it is not that
    /// "unfinished" was computed wrongly, it is that it was the wrong question — and if it ever
    /// starts containing these rows, the reason this filter exists has changed.
    /// </summary>
    [Fact]
    public void NeitherKindOfFailureIsPendingWork()
    {
        AudiolessFailure();
        FailureWithText();

        _model.TranscriptFilter = TranscriptFilter.Unfinished;

        Assert.Empty(_model.TranscriptRows);
    }

    /// <summary>The counter beside the list says the same number the first screen says.</summary>
    [Fact]
    public void TheCounterMatchesWhatTheFirstScreenCounts()
    {
        AudiolessFailure();
        FailureWithText();
        Call(ProcessingState.Analysed, audio: true, segments: 3);

        _model.Refresh();

        Assert.Equal(_repository.FailedCalls().Count, _model.TranscriptFailedCount);
        Assert.Equal(2, _model.TranscriptFailedCount);
    }

    /// <summary>
    /// Changing the filter re-reads. Without this the property was a highlight: "Bitenler" left
    /// the waiting list on screen, and the first screen's "Göster" arrived at whatever the page
    /// happened to be showing — nothing at all, when it had never been opened.
    /// </summary>
    [Fact]
    public void ChangingTheFilterReloadsTheList()
    {
        Call(ProcessingState.Analysed, audio: true, segments: 4);

        _model.TranscriptFilter = TranscriptFilter.Unfinished;
        Assert.Empty(_model.TranscriptRows);

        _model.TranscriptFilter = TranscriptFilter.Done;
        Assert.Single(_model.TranscriptRows);

        _model.TranscriptFilter = TranscriptFilter.All;
        Assert.Single(_model.TranscriptRows);
    }

    /// <summary>An archive with nothing wrong in it says so, rather than showing the wrong words.</summary>
    [Fact]
    public void TheEmptyStateNamesTheFilterItIsEmptyFor()
    {
        Call(ProcessingState.Analysed, audio: true, segments: 2);

        _model.TranscriptFilter = TranscriptFilter.Failed;

        Assert.True(_model.IsTranscriptEmpty);
        Assert.Equal("İşlenemeyen görüşme yok.", _model.TranscriptEmptyMessage);
    }
}
