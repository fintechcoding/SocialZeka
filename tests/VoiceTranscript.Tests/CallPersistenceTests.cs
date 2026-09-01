using System.Buffers.Binary;
using System.Net.Http;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Detection;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Worker;

namespace VoiceTranscript.Tests;

/// <summary>
/// A recording, driven from beginning to end, and what ends up in the database.
///
/// This class exists because of a defect that made the entire product non-functional and was
/// invisible to several hundred other tests. The call row is inserted when a call is *detected* —
/// before anything has been recorded — so it starts with no duration and no file paths. Nothing
/// ever filled them in.
///
/// Every consequence of that was silent. Transcription received a null path and could never run.
/// The waveform player read two nulls and quietly declined to load, so it appeared to work only
/// over the sample data, where paths are written at insert. Durations were all zero. And deleting
/// a contact searched for their recordings with "mic_path IS NOT NULL", found none, and left
/// hours of somebody talking on disk after reporting success.
///
/// The reason nothing caught it: not one test could drive a recording without a sound card. So
/// the capture backend is injectable now, and these tests use the file-backed source that has
/// existed for exactly this purpose since the beginning.
/// </summary>
public class CallPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-persist-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;

    public CallPersistenceTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder is swept anyway.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a WAV of speech-shaped audio, long enough to survive the misfire guard.</summary>
    private string WriteWav(string name, double seconds)
    {
        var path = Path.Combine(_root, name);
        var rate = AudioFormat.WhisperPcm.SampleRate;
        var total = (int)(seconds * rate);
        var data = new byte[total * 2];

        for (var i = 0; i < total; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(i % 2 == 0 ? 9000 : -9000));

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);

        return path;
    }

    /// <summary>
    /// Records a call the way the application does, and hands back its identifier.
    ///
    /// Analysis is switched off: what is under test is that the recording reaches the database,
    /// and running a language model over it would need a worker this machine does not have.
    /// </summary>
    private async Task<long> RecordAsync(double seconds = 8)
    {
        var mic = WriteWav("mic-source.wav", seconds);
        var far = WriteWav("far-source.wav", seconds);

        FileAudioSource? source = null;

        var settings = new AppSettings
        {
            AnalyseAutomatically = false,
            ExportToObsidian = false,
            ExportToNotion = false,

            // The real value is sixty seconds, and it exists so that a laptop is not asked to
            // run a model while the video encoder from the call it just finished is still
            // holding the power budget. In a test it is only a minute of waiting per case.
            GpuCooldownSeconds = 0,
        };

        // There is no worker here, so transcription is expected to fail — what is under test is
        // that the recording reached the database before it was handed over. A short timeout
        // keeps that failure to a couple of seconds instead of a minute per test.
        var worker = new PythonWorkerHost(new PythonWorkerOptions
        {
            PythonExecutable = "python",
            WorkerDirectory = _root,
            ModelCacheDirectory = _paths.Models,
            Timeout = TimeSpan.FromSeconds(2),
        });

        using var http = new HttpClient();

        using var orchestrator = new CallOrchestrator(
            _paths,
            _repository,
            () => settings,
            worker,
            http,
            _ => source = new FileAudioSource(mic, far));

        await orchestrator.StartManualRecordingAsync();

        // The file source only pumps when asked, so the whole recording plays through in a
        // moment rather than in real time.
        Assert.NotNull(source);
        source!.Replay(TimeSpan.FromSeconds(30));

        await orchestrator.StopManualRecordingAsync();

        var call = Assert.Single(_repository.ListCalls());
        return call.Id;
    }

    /// <summary>
    /// A hand-started recording survives whatever the call detector thinks is happening.
    ///
    /// The detector watches WhatsApp and Telegram audio sessions and knows nothing about the
    /// button. It reaches "Abandoned" whenever one of those applications makes a noise for a
    /// couple of seconds and then goes quiet — an incoming call nobody answered, a notification
    /// tone — and that arm called DiscardRecording with no guard at all.
    ///
    /// So recording a forty-minute meeting by hand and receiving one ignored WhatsApp call
    /// during it deleted the meeting: both WAV files unlinked, the row stamped "Çok kısa kayıt",
    /// nothing recoverable. The button then stayed on "Kaydı durdur" for ever, because the flag
    /// that says a manual recording is running was never cleared and the stop path returns at a
    /// guard that requires a recorder which no longer exists.
    /// </summary>
    [Fact]
    public async Task AHandStartedRecordingIsNotDestroyedByTheDetector()
    {
        var mic = WriteWav("manual-mic.wav", 8);
        var far = WriteWav("manual-far.wav", 8);

        FileAudioSource? source = null;

        var settings = new AppSettings
        {
            AnalyseAutomatically = false,
            ExportToObsidian = false,
            ExportToNotion = false,
            GpuCooldownSeconds = 0,
        };

        var worker = new PythonWorkerHost(new PythonWorkerOptions
        {
            PythonExecutable = "python",
            WorkerDirectory = _root,
            ModelCacheDirectory = _paths.Models,
            Timeout = TimeSpan.FromSeconds(2),
        });

        using var http = new HttpClient();

        using var orchestrator = new CallOrchestrator(
            _paths,
            _repository,
            () => settings,
            worker,
            http,
            _ => source = new FileAudioSource(mic, far));

        await orchestrator.StartManualRecordingAsync();
        Assert.True(orchestrator.IsManualRecording);

        Assert.NotNull(source);
        source!.Replay(TimeSpan.FromSeconds(30));

        // What an ignored incoming call looks like to the detector, while the user is recording.
        orchestrator.HandleDetectedEventForTests(
            new CallEvent(CallEventKind.Abandoned, DateTimeOffset.Now, CallApp.WhatsApp, null, TimeSpan.Zero));

        // Still recording, and still the user's to stop.
        Assert.True(orchestrator.IsManualRecording);

        await orchestrator.StopManualRecordingAsync();

        Assert.False(orchestrator.IsManualRecording);

        var call = Assert.Single(_repository.ListCalls());
        var stored = _repository.GetCall(call.Id)!;

        Assert.NotEqual(ProcessingState.Skipped, stored.State);
        Assert.False(string.IsNullOrWhiteSpace(stored.MicPath), "mikrofon kaydı silinmiş");
        Assert.True(File.Exists(stored.MicPath), stored.MicPath);
    }

    [Fact]
    public async Task AFinishedRecordingHasItsAudioPathsWrittenToTheRow()
    {
        // The defect, stated as a test. Without the paths the transcriber is handed null, the
        // player cannot load, and the delete that promises to remove somebody's recordings
        // cannot find them.
        var callId = await RecordAsync();
        var call = _repository.GetCall(callId);

        Assert.NotNull(call);
        Assert.False(string.IsNullOrWhiteSpace(call!.MicPath), "mikrofon yolu yazılmadı");
        Assert.False(string.IsNullOrWhiteSpace(call.FarPath), "karşı taraf yolu yazılmadı");

        Assert.True(File.Exists(call.MicPath), call.MicPath);
        Assert.True(File.Exists(call.FarPath), call.FarPath);
    }

    [Fact]
    public async Task AFinishedRecordingHasItsRealDuration()
    {
        // Every total in the application is a sum of these. A zero here made the archive report
        // "0 dk" of recording however many hours it actually held.
        var callId = await RecordAsync(seconds: 8);
        var call = _repository.GetCall(callId)!;

        Assert.True(call.Duration > TimeSpan.FromSeconds(5), $"süre {call.Duration} çıktı");
        Assert.NotNull(call.EndedAt);
    }

    [Fact]
    public async Task AFinishedRecordingIsQueuedForProcessingWithItsAudioAlreadyAttached()
    {
        // Order matters: a call that reaches the queue before its paths are written would be
        // handed to the transcriber with nothing to transcribe.
        var callId = await RecordAsync();
        var call = _repository.GetCall(callId)!;

        Assert.NotEqual(ProcessingState.Recorded, call.State);
        Assert.NotNull(call.MicPath);
    }

    [Fact]
    public async Task DeletingTheContactRemovesTheRecordingsFromDisk()
    {
        // The promise the whole product rests on, and the one the missing write quietly broke:
        // the delete looks for audio by path, so a row with no path left the audio behind while
        // telling the user it was gone.
        var callId = await RecordAsync();
        var call = _repository.GetCall(callId)!;

        var contactId = _repository.UpsertContact("Silinecek Kişi", CallApp.WhatsApp);
        _repository.AssignContact(callId, contactId);

        var micPath = call.MicPath!;
        Assert.True(File.Exists(micPath));

        var result = _repository.DeleteContactCompletely(contactId);

        Assert.True(result.FilesRemoved >= 2, $"yalnızca {result.FilesRemoved} dosya silindi");
        Assert.False(File.Exists(micPath), "ses dosyası diskte kaldı");
    }

    [Fact]
    public async Task DeletingOneRecordingTakesItsAudioAndItsMixedCopy()
    {
        // Deleting a single call had no implementation at all: the only way to remove one
        // recording was to remove the whole person. And the mixed copy is the trap — it is
        // derived, so it is easy to forget, and it is a playable recording of the entire
        // conversation, so forgetting it makes the delete a lie.
        var callId = await RecordAsync();
        var call = _repository.GetCall(callId)!;

        var mixed = VoiceTranscript.Core.Audio.ConversationMix.Ensure(call.MicPath, call.FarPath);
        Assert.NotNull(mixed);
        Assert.True(File.Exists(mixed));

        var result = _repository.DeleteCall(callId);

        Assert.Null(_repository.GetCall(callId));
        Assert.Empty(result.FilesLeftBehind);

        Assert.False(File.Exists(call.MicPath), "mikrofon kaydı diskte kaldı");
        Assert.False(File.Exists(call.FarPath), "karşı taraf kaydı diskte kaldı");
        Assert.False(File.Exists(mixed), "birleştirilmiş kayıt diskte kaldı");
    }

    [Fact]
    public void ContactsAreFoundWithTurkishFolding()
    {
        // The trap this exists for: SQL lowercases with the Unicode defaults, which do not map
        // İ to i or I to ı. Typing "ısıl" would silently return nothing, and the user would
        // conclude the contact was not there and create a second one — splitting one person's
        // history in two, invisibly, since both halves look complete.
        _repository.UpsertContact("Işıl Demir", CallApp.Telegram);
        _repository.UpsertContact("İbrahim Yılmaz", CallApp.WhatsApp);

        Assert.Single(_repository.SearchContacts("ısıl"));
        Assert.Single(_repository.SearchContacts("IŞIL"));
        Assert.Single(_repository.SearchContacts("ibrahim"));
        Assert.Empty(_repository.SearchContacts("   "));
    }

    [Fact]
    public async Task TheCaptureDiagnosticsAreKeptWithTheCall()
    {
        // Overlaps and re-anchors mean speaker attribution in that recording cannot be trusted.
        // Keeping the figures with the row is what lets the interface say so later.
        var callId = await RecordAsync();
        var call = _repository.GetCall(callId)!;

        Assert.False(string.IsNullOrWhiteSpace(call.CaptureStats));
        Assert.Contains("mic:", call.CaptureStats!);
    }
}
