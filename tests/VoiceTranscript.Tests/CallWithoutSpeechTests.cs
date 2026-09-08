using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// What the screens say about a recording that came back with nothing said on either side.
///
/// The verdict is written on the row and it tells the user two things: what this most likely was,
/// and that the audio is still there to listen to. Both are useless if the window they are sent
/// to says something else — and it did: with no lines stored, the window's empty state read
/// "Bu görüşme henüz yazıya dökülmedi", which promises a transcript that is not coming.
/// </summary>
public sealed class CallWithoutSpeechTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-nospeech-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly HttpClient _http = new();

    public CallWithoutSpeechTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        _http.Dispose();
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A one-minute outgoing call filed exactly as the transcription tail files it.</summary>
    private long Unanswered()
    {
        var contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
        var verdict = EmptyTranscript.Judge(CallDirection.Outgoing, TimeSpan.FromSeconds(67), hadTranscript: false);

        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            Direction = CallDirection.Outgoing,
            StartedAt = DateTimeOffset.Now.AddMinutes(-10),
            Duration = TimeSpan.FromSeconds(67),
            State = ProcessingState.Recorded,
        });

        _repo.AssignContact(call, contact);
        _repo.SetCallState(call, verdict.State, verdict.Reason);

        return call;
    }

    [Fact]
    public void TheWindowRepeatsTheVerdictInsteadOfPromisingATranscript()
    {
        var settings = new AppSettings();
        var window = new CallWindowViewModel(_repo, () => settings, _http, Unanswered());

        Assert.NotNull(window.TranscriptMessage);
        Assert.Contains("Konuşma bulunamadı", window.TranscriptMessage);
        Assert.DoesNotContain("henüz yazıya dökülmedi", window.TranscriptMessage);
    }

    /// <summary>A recording still waiting its turn is not a recording that came back empty.</summary>
    [Fact]
    public void ARecordingStillWaitingIsStillToldItIsWaiting()
    {
        var contact = _repo.UpsertContact("Ayşe", CallApp.WhatsApp);

        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now,
            Duration = TimeSpan.FromMinutes(3),
            State = ProcessingState.Recorded,
        });

        var settings = new AppSettings();
        var window = new CallWindowViewModel(_repo, () => settings, _http, call);

        Assert.Equal("Bu görüşme henüz yazıya dökülmedi.", window.TranscriptMessage);
    }

    /// <summary>
    /// The row this call is reached from says which of the four kinds of "atlandı" it is, and
    /// says that the audio is still there — the one thing that separates it from a discarded one.
    /// </summary>
    [Fact]
    public void TheRowSaysTheAudioIsStillThere()
    {
        var call = _repo.GetCall(Unanswered())!;

        Assert.Equal("Konuşma yok — ses duruyor", Core.Text.CallStateText.Short(call));
    }
}
