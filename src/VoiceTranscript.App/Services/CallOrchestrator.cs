using System.IO;
using System.Net.Http;
using VoiceTranscript.Capture;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Detection;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Export;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Worker;
using CoreSegment = VoiceTranscript.Core.Domain.Segment;

namespace VoiceTranscript.App.Services;

public enum OrchestratorState
{
    Idle,
    Ringing,
    Recording,
    Processing,
}

public sealed record CallFinished(
    long CallId,
    TimeSpan Duration,
    string? ObservedTitle,
    CallApp App,
    bool NeedsLabel,
    string AudioSummary,
    bool HasSilentStream);

/// <summary>
/// The background loop that ties everything together.
///
/// Watch audio sessions once a second, record while a call is up, and once it ends put the
/// recording through transcription, analysis and export. Everything after the call is deliberate:
/// nothing touches the GPU while the call is live, because the laptop shares its power budget
/// with the video encoder the call itself is using, and a machine that throttles mid-conversation
/// is a recorder the user notices and turns off.
/// </summary>
public sealed class CallOrchestrator : IDisposable
{
    private readonly AppPaths _paths;
    private readonly Repository _repository;
    private readonly Func<AppSettings> _settings;
    private readonly PythonWorkerHost _worker;
    private readonly HttpClient _http;

    private readonly AudioSessionWatcher _sessions = new();
    private readonly CallDetector _detector = new();
    private readonly SemaphoreSlim _gpu = new(1, 1);

    private CallRecorder? _recorder;
    private bool? _localTranscriptionUsable;
    private long? _currentCallId;
    private DateTimeOffset _callStartedAt;

    /// <summary>
    /// When the recording in progress began. Meaningless while idle.
    ///
    /// Exposed so the on-screen strip counts from the same instant the row in the database will
    /// carry, rather than from when the strip happened to be created — otherwise the two
    /// disagree by however long the capture devices took to open, which on a Bluetooth headset
    /// is a visible second or two.
    /// </summary>
    public DateTimeOffset RecordingStartedAt => _callStartedAt;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <param name="captureBackend">
    /// Where audio comes from. Null builds the real WASAPI capture.
    ///
    /// Injectable so that the record-then-persist path can be exercised by a test with a
    /// file-backed source. That seam is not decoration: the step that writes a finished
    /// recording’s file paths and duration back to its row was missing entirely, and it survived
    /// a suite of several hundred tests because not one of them could drive a recording from
    /// beginning to end without a sound card.
    /// </param>
    public CallOrchestrator(
        AppPaths paths,
        Repository repository,
        Func<AppSettings> settings,
        PythonWorkerHost worker,
        HttpClient http,
        Func<AppSettings, IAudioCaptureBackend>? captureBackend = null)
    {
        _paths = paths;
        _repository = repository;
        _settings = settings;
        _worker = worker;
        _http = http;
        _captureBackend = captureBackend;
    }

    private readonly Func<AppSettings, IAudioCaptureBackend>? _captureBackend;

    public OrchestratorState State { get; private set; } = OrchestratorState.Idle;

    public event EventHandler<OrchestratorState>? StateChanged;

    /// <summary>Raised when a call has been recorded, so the user can label and keep or discard it.</summary>
    public event EventHandler<CallFinished>? CallFinished;

    public event EventHandler<string>? Notice;

    /// <summary>
    /// Loudness of each stream while a call is being recorded, 0-1.
    ///
    /// Forwarded so the window can show two moving bars during the call. The point is timing:
    /// a capture pointed at the wrong endpoint records an hour of silence and looks like a
    /// success from every other angle, and finding that out afterwards does not bring the
    /// conversation back.
    /// </summary>
    public event EventHandler<(double Mic, double Far)>? LevelChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => WatchAsync(_cts.Token));
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await ticker.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // A failure in one poll must never kill the watcher; the next call would then
                // go unrecorded with no indication why.
                Notice?.Invoke(this, $"Tespit hatası: {e.Message}");
            }
        }
    }

    private void Tick()
    {
        var settings = _settings();
        var sample = _sessions.Sample(DateTimeOffset.Now);

        if (!IsWatched(sample.App, settings)) return;

        var callEvent = _detector.Observe(sample);
        UpdateState();

        switch (callEvent?.Kind)
        {
            case CallEventKind.Started:
                // The switch is honoured here rather than by stopping the watcher, so that the
                // status card can still say "a call is happening and I am deliberately not
                // recording it" — which is the reassurance somebody who turned it off wants.
                //
                // This setting existed from the beginning and was never read anywhere, so
                // automatic recording could not in fact be turned off by any means.
                if (!settings.RecordAutomatically)
                {
                    Notice?.Invoke(this, "Otomatik kayıt kapalı — bu görüşme kaydedilmiyor.");
                    break;
                }

                _ = BeginRecordingAsync(callEvent, settings);
                break;

            case CallEventKind.Ended:
                _ = FinishRecordingAsync(callEvent, settings);
                break;

            case CallEventKind.Abandoned:
                DiscardRecording();
                break;
        }
    }

    private static bool IsWatched(CallApp app, AppSettings settings) => app switch
    {
        CallApp.WhatsApp => settings.RecordWhatsApp,
        CallApp.Telegram => settings.RecordTelegram,
        _ => true, // Unknown means no target process is active; the detector handles that.
    };

    private void UpdateState()
    {
        // Reported from what is actually happening, not from what the detector believes.
        //
        // A call can be detected and the recorder still fail to open its devices, and showing
        // "recording" through all of that is worse than showing nothing: the user would trust a
        // conversation was being kept when it was not.
        var next = _detector.State switch
        {
            _ when IsManualRecording && _recorder is not null => OrchestratorState.Recording,
            CallState.InCall when _recorder is not null => OrchestratorState.Recording,
            CallState.InCall => OrchestratorState.Idle,
            CallState.Ringing => OrchestratorState.Ringing,
            _ => State == OrchestratorState.Processing ? OrchestratorState.Processing : OrchestratorState.Idle,
        };

        if (next == State) return;

        State = next;
        StateChanged?.Invoke(this, next);
    }

    /// <summary>True while a recording the user started by hand is running.</summary>
    public bool IsManualRecording { get; private set; }

    /// <summary>
    /// Starts recording on demand, without waiting for a call to be detected.
    ///
    /// Detection is good but it is not perfect: a client updates and moves its audio session, a
    /// call comes in over a route nobody anticipated, or somebody simply wants the next twenty
    /// minutes kept. A recorder that can only be started by a heuristic is a recorder that
    /// silently misses the conversation that mattered, and there is no way to go back for it.
    /// </summary>
    public async Task StartManualRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_recorder is not null) throw new InvalidOperationException("Zaten kayıt yapılıyor.");

        var settings = _settings();
        var now = DateTimeOffset.Now;

        await BeginRecordingAsync(
            new CallEvent(CallEventKind.Started, now, CallApp.Unknown, null, TimeSpan.Zero),
            settings);

        if (_recorder is null) return;

        IsManualRecording = true;
        UpdateState();

        Notice?.Invoke(this, "Elle kayıt başladı. Bitirmek için Kaydı durdur.");
    }

    /// <summary>Stops a hand-started recording and puts it through the usual processing.</summary>
    public async Task StopManualRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsManualRecording || _recorder is null) return;

        IsManualRecording = false;

        await FinishRecordingAsync(
            new CallEvent(CallEventKind.Ended, DateTimeOffset.Now, CallApp.Unknown, null, TimeSpan.Zero),
            _settings());
    }

    /// <summary>
    /// Runs a recording through transcription and analysis again.
    ///
    /// The audio is on disk and intact, so a failure here is almost always transient — a device
    /// that was busy, a rate limit, a model that had not finished downloading. Making somebody
    /// re-record a conversation because of that would be absurd, and impossible anyway.
    /// </summary>
    public async Task ReprocessAsync(long callId, CancellationToken cancellationToken = default)
    {
        _repository.SetCallState(callId, ProcessingState.Queued);
        await ProcessAsync(callId, _settings(), cancellationToken);
    }

    private async Task BeginRecordingAsync(CallEvent callEvent, AppSettings settings)
    {
        try
        {
            _callStartedAt = callEvent.At;

            // The contact may already be known from a title seen on an earlier call, in which
            // case the user is never asked again.
            var contactId = _repository.ResolveTitle(callEvent.WindowTitle, callEvent.App);

            _currentCallId = _repository.InsertCall(new Call
            {
                ContactId = contactId,
                App = callEvent.App,
                StartedAt = callEvent.At,
                State = ProcessingState.Recorded,
                ObservedTitle = callEvent.WindowTitle,
            });

            var directory = _paths.RecordingDirectoryFor(callEvent.At);
            var backend = CreateBackend(settings);

            _recorder = new CallRecorder(backend);
            _recorder.Interrupted += (_, reason) => Notice?.Invoke(this, reason);
            _recorder.LevelChanged += (_, levels) => LevelChanged?.Invoke(this, levels);
            await _recorder.StartAsync(directory, $"call-{_currentCallId}");
        }
        catch (Exception e)
        {
            Notice?.Invoke(this, $"Kayıt başlatılamadı: {e.Message}");

            if (_currentCallId is { } id)
                _repository.SetCallState(id, ProcessingState.Failed, e.Message);

            _recorder?.Dispose();
            _recorder = null;
            _currentCallId = null;

            // The status indicator is derived from _recorder, so clearing it above is what stops
            // the window claiming to be recording. Refresh it now rather than a second later.
            UpdateState();
        }
    }

    private IAudioCaptureBackend CreateBackend(AppSettings settings)
    {
        if (_captureBackend is { } factory) return factory(settings);

        if (settings.PreferProcessLoopback)
        {
            try
            {
                return new ProcessLoopbackCaptureBackend();
            }
            catch (Exception e)
            {
                // Falling back rather than failing: whole-device capture records the same
                // conversation, just with anything else that happens to be playing.
                Notice?.Invoke(this, $"Uygulama bazlı yakalama açılamadı, cihaz yakalamaya geçildi: {e.Message}");
            }
        }

        return new WasapiCaptureBackend(
                    settings.UseEchoCancellation,
                    settings.MicrophoneDeviceId,
                    settings.OutputDeviceId);
    }

    private async Task FinishRecordingAsync(CallEvent callEvent, AppSettings settings)
    {
        if (_recorder is null || _currentCallId is not { } callId) return;

        var recorder = _recorder;
        _recorder = null;
        _currentCallId = null;

        RecordingResult result;
        try
        {
            result = recorder.Stop();
        }
        catch (Exception e)
        {
            Notice?.Invoke(this, $"Kayıt sonlandırılamadı: {e.Message}");
            _repository.SetCallState(callId, ProcessingState.Failed, e.Message);
            return;
        }
        finally
        {
            recorder.Dispose();
        }

        // Very short recordings are almost always a misfire — a ringtone, a notification, a
        // call that never connected. Keeping them would fill the archive with noise.
        if (result.Duration < TimeSpan.FromSeconds(5))
        {
            Discard(callId, result);
            return;
        }

        if (!result.StreamsAreAligned)
        {
            Notice?.Invoke(this,
                "İki ses akışı hizalı değil, bu kayıtta kimin ne söylediği güvenilir olmayabilir.");
        }

        // Said out loud rather than left to be discovered in an empty transcript. A capture that
        // produces silence looks identical to a successful one from every other angle, and the
        // user has no way to tell the difference until they go looking for a conversation that
        // was never recorded.
        if (result.HasSilentStream) Notice?.Invoke(this, result.AudioSummary);

        // After the short-recording guard, so a row is never pointed at files that were just
        // deleted, and before the state changes to Queued, so the transcriber never sees a call
        // that is ready to process but has no audio attached to it.
        _repository.CompleteCall(
            callId,
            result.MicPath,
            result.FarPath,
            result.Duration,
            callEvent.At,
            $"mic: {result.MicStats}; far: {result.FarStats}");

        _repository.SetCallState(callId, ProcessingState.Queued);

        CallFinished?.Invoke(this, new CallFinished(
            callId,
            result.Duration,
            callEvent.WindowTitle,
            callEvent.App,
            NeedsLabel: _repository.GetCall(callId)?.ContactId is null,
            AudioSummary: result.AudioSummary,
            HasSilentStream: result.HasSilentStream));

        await ProcessAsync(callId, settings);
    }

    private void Discard(long callId, RecordingResult result)
    {
        foreach (var path in new[] { result.MicPath, result.FarPath })
        {
            try
            {
                if (path is not null && File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // Left behind; the retention sweep will get it.
            }
        }

        _repository.SetCallState(callId, ProcessingState.Skipped, "Çok kısa kayıt.");
    }

    private void DiscardRecording()
    {
        if (_recorder is null) return;

        try
        {
            var result = _recorder.Stop();
            if (_currentCallId is { } id) Discard(id, result);
        }
        catch (Exception)
        {
            // Nothing worth recovering from an abandoned ring.
        }
        finally
        {
            _recorder.Dispose();
            _recorder = null;
            _currentCallId = null;
        }
    }

    /// <summary>
    /// Transcribes and analyses one recording.
    ///
    /// Serialised behind a semaphore, and the two stages never overlap. Whisper and the analysis
    /// model together do not fit in 6 GB, so they take the GPU in turn: the worker exits before
    /// the language model is asked for anything, which is also the only way to be certain the
    /// video memory actually came back.
    /// </summary>
    public async Task ProcessAsync(long callId, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var call = _repository.GetCall(callId);
        if (call is null) return;

        if (call.Kind == CallKind.Group && !settings.TranscribeGroupCalls)
        {
            _repository.SetCallState(callId, ProcessingState.Skipped,
                "Grup araması: konuşmacılar ayrıştırılamadığı için yalnızca ses saklandı.");
            return;
        }

        await _gpu.WaitAsync(cancellationToken);
        State = OrchestratorState.Processing;
        StateChanged?.Invoke(this, State);

        try
        {
            // The GPU is still busy finishing the call. Starting now makes the machine throttle.
            if (settings.GpuCooldownSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(settings.GpuCooldownSeconds), cancellationToken);

            await TranscribeAsync(call, settings, cancellationToken);

            if (settings.AnalyseAutomatically)
                await AnalyseAsync(callId, settings, cancellationToken);

            if (settings.ExportToObsidian && !string.IsNullOrWhiteSpace(settings.ObsidianVaultPath))
                Export(callId, settings);

            if (settings.ExportToNotion
                && !string.IsNullOrWhiteSpace(settings.NotionApiKey)
                && !string.IsNullOrWhiteSpace(settings.NotionDatabaseId))
            {
                await ExportToNotionAsync(callId, settings, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _repository.SetCallState(callId, ProcessingState.Queued);
        }
        catch (Exception e)
        {
            _repository.SetCallState(callId, ProcessingState.Failed, e.Message);
            Notice?.Invoke(this, $"İşleme başarısız: {e.Message}");
        }
        finally
        {
            _gpu.Release();
            State = OrchestratorState.Idle;
            StateChanged?.Invoke(this, State);
        }
    }

    /// <summary>
    /// Whether the local model can genuinely run here.
    ///
    /// Asked of the worker rather than inferred from the hardware: a card can be present and
    /// still unusable because a CUDA runtime DLL is missing, and that is precisely the situation
    /// where falling back to the cloud is the right answer. Cached, because it only changes when
    /// drivers do.
    /// </summary>
    private async Task<bool> LocalTranscriptionUsableAsync(CancellationToken cancellationToken)
    {
        if (_localTranscriptionUsable is { } known) return known;

        try
        {
            var hello = await _worker.ProbeAsync(cancellationToken);
            _localTranscriptionUsable = hello.Cuda?.Available == true;
        }
        catch (Exception)
        {
            // The worker itself is unreachable, so local transcription certainly is not.
            _localTranscriptionUsable = false;
        }

        return _localTranscriptionUsable.Value;
    }

    private static string EngineNameFor(AsrModel model) => model.Engine switch
    {
        AsrEngineKind.WhisperCpp => "whisper.cpp",
        AsrEngineKind.CloudOpenAi => "cloud-openai",
        _ => "faster-whisper",
    };

    /// <summary>
    /// Uploads to the configured services in order, moving on when one will not answer.
    ///
    /// A hosted service is a single point of failure on precisely the evening it matters: credit
    /// runs out overnight, a rate limit bites during a long call, or the provider has an outage.
    /// The recording is already on disk at this point, so giving up on the first refusal throws
    /// away a conversation that the second endpoint would have transcribed without anybody
    /// noticing there had been a problem.
    ///
    /// An authentication failure still moves on to the next endpoint rather than stopping: a key
    /// that expired is exactly the case a second endpoint exists for. What the user gets is one
    /// notice naming every service that refused, so the cause is visible without the transcript
    /// being lost.
    /// </summary>
    private async Task<WorkerResult> TranscribeInCloudAsync(
        Call call,
        Core.Asr.AsrModel model,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var endpoints = settings.UsableSttEndpoints;

        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "Yazıya dökme için yapılandırılmış bir servis yok. Ayarlar bölümünden bir " +
                "sağlayıcı ekleyip anahtarını gir.");
        }

        var failures = new List<string>();

        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await _worker.TranscribeAsync(new TranscriptionRequest
                {
                    Id = $"call-{call.Id}",
                    Engine = EngineNameFor(model),
                    ModelRef = endpoint.ToModelRef(),
                    Device = settings.AsrDevice,
                    Language = settings.Language,
                    MicPath = call.MicPath,
                    FarPath = call.FarPath,
                    CacheDir = _paths.Models,
                }, progress: null, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                failures.Add($"{endpoint.ResolvedName}: {e.Message}");

                if (i + 1 < endpoints.Count)
                {
                    Notice?.Invoke(this,
                        $"{endpoint.ResolvedName} yanıt vermedi, {endpoints[i + 1].ResolvedName} deneniyor.");
                }
            }
        }

        throw new InvalidOperationException(
            "Yapılandırılmış servislerin hiçbiri yazıya dökemedi:" + Environment.NewLine
            + string.Join(Environment.NewLine, failures));
    }

    private async Task TranscribeAsync(Call call, AppSettings settings, CancellationToken cancellationToken)
    {
        _repository.SetCallState(call.Id, ProcessingState.Transcribing);

        var model = settings.ResolveAsrModel(await LocalTranscriptionUsableAsync(cancellationToken));

        if (model.SendsAudioOffMachine)
        {
            // Said every time rather than once at setup: the automatic mode can start uploading
            // because a driver broke, and that must never happen quietly.
            Notice?.Invoke(this,
                $"Bu görüşme yazıya dökülmek üzere {model.DisplayName} servisine yükleniyor.");
        }

        var result = model.SendsAudioOffMachine
            ? await TranscribeInCloudAsync(call, model, settings, cancellationToken)
            : await _worker.TranscribeAsync(new TranscriptionRequest
            {
                Id = $"call-{call.Id}",
                Engine = EngineNameFor(model),
                ModelRef = model.ModelRef,
                Device = settings.AsrDevice,
                Language = settings.Language,
                MicPath = call.MicPath,
                FarPath = call.FarPath,
                CacheDir = _paths.Models,
            }, progress: null, cancellationToken);

        _repository.ReplaceSegments(call.Id, result.Segments.Select(s => new CoreSegment
        {
            CallId = call.Id,
            IsMe = s.IsMe,
            StartMs = (int)(s.Start * 1000),
            EndMs = (int)(s.End * 1000),
            Text = s.Text,
            AvgLogprob = s.AvgLogprob,
            NoSpeechProb = s.NoSpeechProb,
            LowConfidence = s.LowConfidence,
            OverlapsOtherSpeaker = s.OverlapsOtherSpeaker,
            SuspectedEcho = s.SuspectedEcho,
        }));

        if (result.Stats?.LikelyNoHeadphones == true)
        {
            Notice?.Invoke(this,
                "Karşı tarafın sesi mikrofona da karışmış. Kulaklık kullanmak konuşmacı ayrımını belirgin " +
                "şekilde iyileştirir.");
        }

        _repository.SetCallState(call.Id, ProcessingState.Transcribed);
    }

    private async Task AnalyseAsync(long callId, AppSettings settings, CancellationToken cancellationToken)
    {
        _repository.SetCallState(callId, ProcessingState.Analysing);

        var client = new OpenAiCompatibleClient(
            _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

        var report = await new AnalysisPipeline(client, _repository).AnalyseAsync(
            callId,
            new AnalysisOptions
            {
                Model = settings.ResolvedModelName,
                // Only a local backend holds the GPU this machine needs back for Whisper.
                UnloadWhenDone = !settings.Provider.SendsDataOffMachine,
            },
            progress: null,
            cancellationToken);

        // A model whose quotes mostly cannot be found is not producing usable evidence, and the
        // user should be told to change it rather than left with a quietly empty ledger.
        if (report.RejectionRate > 0.4)
        {
            Notice?.Invoke(this,
                $"Çözümlemede üretilen alıntıların %{report.RejectionRate * 100:0}'ı metinde bulunamadı. " +
                "Bu model bu iş için uygun olmayabilir.");
        }

        _repository.SetCallState(callId, ProcessingState.Analysed);
    }

    private void Export(long callId, AppSettings settings)
    {
        try
        {
            new ObsidianExporter(_repository, new ObsidianOptions { VaultPath = settings.ObsidianVaultPath! })
                .ExportCall(callId);
        }
        catch (Exception e)
        {
            Notice?.Invoke(this, $"Obsidian dışa aktarımı başarısız: {e.Message}");
        }
    }

    /// <summary>
    /// Sends the summary to Notion, and says so out loud when it fails.
    ///
    /// A silent failure here is worse than elsewhere: the user believes their archive is
    /// mirrored somewhere they can reach from a phone, and finds out it is not at the moment
    /// they actually need it.
    /// </summary>
    private async Task ExportToNotionAsync(long callId, AppSettings settings, CancellationToken ct)
    {
        try
        {
            var exporter = new NotionExporter(
                _repository,
                new NotionOptions
                {
                    ApiKey = settings.NotionApiKey!,
                    DatabaseId = settings.NotionDatabaseId!,
                },
                _http);

            await exporter.ExportCallAsync(callId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Notice?.Invoke(this, $"Notion dışa aktarımı başarısız: {e.Message}");
        }
    }

    /// <summary>Retries every recording that was left queued or failed, oldest first.</summary>
    public async Task ProcessBacklogAsync(CancellationToken cancellationToken = default)
    {
        foreach (var call in _repository.CallsAwaitingProcessing())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessAsync(call.Id, _settings(), cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Cancellation.
        }

        _recorder?.Dispose();
        _sessions.Dispose();
        _cts?.Dispose();
        _gpu.Dispose();
    }
}
