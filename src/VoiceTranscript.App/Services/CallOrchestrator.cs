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
/// A recording has been through transcription and analysis, for better or worse.
///
/// Raised so that finishing is something the user is <i>told</i> about rather than something they
/// have to go and check. Until this existed, the end of processing changed a state field and
/// nothing else: the summary was written to the database and displayed on the contact's page, and
/// the only way to discover it was there was to navigate to that page and select the call. Asked
/// afterwards what the conversation was about, the product had an answer and no way to say it.
/// </summary>
/// <param name="ContactName">Who it was with, or a placeholder when nobody has said yet.</param>
/// <param name="Summary">The written summary, when one was produced.</param>
/// <param name="Failure">Why it did not finish, when it did not.</param>
public sealed record CallProcessed(
    long CallId,
    string ContactName,
    TimeSpan Duration,
    string? Summary,
    bool Succeeded,
    string? Failure);

/// <summary>Where the recording being processed has got to.</summary>
/// <param name="CallId">Which recording, so a screen can put the bar on the right row.</param>
/// <param name="Stage">What is happening, in Turkish, ready to show.</param>
/// <param name="Percent">0 to 1 when it is known. Null for work that cannot report a fraction.</param>
public sealed record CallProgress(long CallId, string Stage, double? Percent);

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
    /// <summary>
    /// The Python worker, asked for each time rather than captured once.
    ///
    /// The wizard rebuilds it: after Python and the packages are installed, the application
    /// points a new host at the environment that now exists. Captured by value at startup, the
    /// orchestrator went on holding the host built before any of that — pointed at a Python that
    /// was not there — so on a fresh machine the setup could complete perfectly and every call
    /// would still fail to transcribe until the application was restarted.
    /// </summary>
    private readonly Func<PythonWorkerHost> _worker;
    private readonly HttpClient _http;

    private readonly AudioSessionWatcher _sessions = new();
    private readonly CallDetector _detector = new();
    private readonly SemaphoreSlim _gpu = new(1, 1);

    private CallRecorder? _recorder;
    private bool? _localTranscriptionUsable;

    /// <summary>
    /// Whether local transcription is known to work here — null until the first probe.
    ///
    /// Exposed so the status screen shows the route calls actually take. It used to assume
    /// local, so on a machine that uploads every call to a cloud service the screen listing
    /// "what leaves this machine" said nothing did.
    /// </summary>
    public bool? LocalTranscriptionUsable => _localTranscriptionUsable;
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
        Func<PythonWorkerHost> worker,
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

    /// <summary>Raised when a recording has finished being transcribed and analysed, or has failed.</summary>
    public event EventHandler<CallProcessed>? CallProcessed;

    /// <summary>
    /// How far along the recording currently being processed is.
    ///
    /// This was produced end to end and thrown away. The Python worker emits a stage and a
    /// percentage for every chunk, the protocol parses it, and the host and the analysis pipeline
    /// both accept an <see cref="IProgress{T}"/> — and all three call sites passed null, so none of
    /// it reached a screen.
    ///
    /// That is worst exactly where it is needed most. Without a usable graphics card, transcription
    /// runs several times slower than real time: a long conversation is worked on for the better
    /// part of an hour, and with nothing on screen saying so, an application that is working looks
    /// identical to one that has hung.
    /// </summary>
    public event EventHandler<CallProgress>? ProgressChanged;

    /// <summary>
    /// Turns the worker's stage name into something worth putting on a screen.
    ///
    /// The worker speaks in the terms it works in — "mic", "far", "merge" — which are meaningful
    /// to whoever wrote it and to nobody else. What the user needs to know is which half of the
    /// conversation is being worked on, because that is what tells them roughly how much is left.
    /// </summary>
    private static string StageName(string stage) => stage switch
    {
        "loading" => "Model yükleniyor",
        "mic" => "Senin sesin yazıya dökülüyor",
        "far" => "Karşı tarafın sesi yazıya dökülüyor",
        "merge" => "Birleştiriliyor",
        "download" => "Model indiriliyor",
        "indiriliyor" => "Model indiriliyor",
        _ => "Yazıya dökülüyor",
    };

    private void Report(long callId, string stage, double? percent = null)
    {
        try
        {
            ProgressChanged?.Invoke(this, new CallProgress(callId, stage, percent));
        }
        catch (Exception e)
        {
            AppLog.Error("işleme", e, "ilerleme bildirilemedi");
        }
    }

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

    /// <summary>
    /// Detected events on their way to the worker that acts on them.
    /// </summary>
    /// <remarks>
    /// Unbounded because dropping one would mean losing a conversation, and because the producer
    /// emits at most one event per second while the consumer takes milliseconds per event except
    /// when it is deliberately waiting — which is exactly the case this channel exists to absorb.
    /// </remarks>
    private readonly System.Threading.Channels.Channel<CallEvent> _work =
        System.Threading.Channels.Channel.CreateUnbounded<CallEvent>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

    /// <summary>Recordings waiting to be transcribed and analysed.</summary>
    private readonly System.Threading.Channels.Channel<long> _processing =
        System.Threading.Channels.Channel.CreateUnbounded<long>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

    private Task? _recordingWorker;
    private Task? _processor;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();

        _loop = Task.Run(() => WatchAsync(_cts.Token));
        _recordingWorker = Task.Run(() => WorkAsync(_cts.Token));
        _processor = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    /// <summary>
    /// Samples the world once a second and writes what it finds to the work channel.
    ///
    /// <b>This loop must never wait for anything, and that is the whole point of it.</b> It used
    /// to do the work itself, and the work included opening audio devices, writing to SQLite —
    /// which blocks for up to five seconds when another writer holds the lock — and, through an
    /// event subscriber, showing a modal dialog. While any of that ran, no sample was taken. A
    /// dialog left on screen therefore froze detection completely: a call made during that time
    /// produced no row, no file and no recording at all, and the audio was gone for good.
    ///
    /// Note that making the dialog asynchronous would not have been enough. The database wait and
    /// the device open are synchronous and would stall sampling on their own. The invariant has to
    /// be that this loop samples and does nothing else.
    /// </summary>
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

    /// <summary>The last detector state written to the log, so each transition is written once.</summary>
    private CallState _lastLoggedState = CallState.Idle;

    private void Tick()
    {
        var settings = _settings();
        var sample = _sessions.Sample(DateTimeOffset.Now);

        // Every sample reaches the detector, including ones from an application the user has
        // switched off.
        //
        // Dropping them here starved the state machine: a call already being recorded from a
        // watched application could miss the very samples that would end it, because in between
        // the other messenger made a noise and the whole poll was discarded. The per-app choice
        // belongs where a recording is started, not on the observations.
        // Which signal moved the state, written to the log and nowhere else.
        //
        // This went through Notice at first, which was wrong twice over: Notice raises a toast,
        // so a diagnostic line meant for a file was popping up over the user's desktop saying
        // "Tespit: Idle (hoparlör=False…)" — a sentence written for whoever reads the log, not
        // for the person using the application. Notices are for things somebody must act on.
        //
        // Never the window title: the log is meant to be shareable and the title is a contact's
        // name. The flags are enough to tell a real call from a false start, which is the only
        // question this line exists to answer.
        if (settings.VerboseLog && _detector.State != _lastLoggedState)
        {
            _lastLoggedState = _detector.State;

            AppLog.Write("tespit",
                $"{_detector.State} (hoparlör={sample.Rendering}, mikrofon={sample.Capturing}, " +
                $"uygulama={sample.AppWindowPresent}, arama penceresi={sample.CallWindowPresent}, " +
                $"isim={(sample.WindowTitle is null ? "yok" : "var")})");
        }

        var callEvent = _detector.Observe(sample);

        if (callEvent?.Kind == CallEventKind.Started && !IsWatched(callEvent.App, settings))
        {
            UpdateState();
            return;
        }
        UpdateState();

        // Handing the event over is a queue write and returns immediately. Everything that can
        // block — devices, database, dialogs — happens on the worker.
        if (callEvent is not null) _work.Writer.TryWrite(callEvent);
    }

    /// <summary>
    /// Acts on detected events, one at a time, off the sampling loop.
    ///
    /// Serialised on purpose. Recording has a single recorder and a single current call, and the
    /// previous arrangement started each event on a thread-pool thread with no ordering at all —
    /// so a call ending could overlap the next one starting and the two would write over each
    /// other's fields. One consumer makes the ordering a property of the design rather than of
    /// timing.
    /// </summary>
    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var callEvent in _work.Reader.ReadAllAsync(cancellationToken))
            {
                var settings = _settings();

                try
                {
                    await ApplyDetectedEventAsync(callEvent, settings);
                }
                catch (Exception e)
                {
                    // The worker must outlive any single event. If it died, every later call would
                    // be detected and then silently ignored — the recorder would look alive and
                    // record nothing.
                    AppLog.Error("kayıt", e, $"{callEvent.Kind} işlenemedi");
                    Notice?.Invoke(this, $"Kayıt işlenemedi: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Transcribes and analyses finished recordings, one at a time.
    ///
    /// Separate from the recording worker because the two have wildly different durations and must
    /// not share a queue: transcription and analysis take minutes, and while they ran nothing else
    /// could happen — not the next call's recording, and not the question asking who the last one
    /// was with. Recording is now never behind processing.
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var callId in _processing.Reader.ReadAllAsync(cancellationToken))
            {
                // Released as work starts, not as it ends: from here on the recording is being
                // processed, and a user asking for a retry after a failure is a new request.
                _inQueue.TryRemove(callId, out _);

                // Each job gets its own cancellation, linked to shutdown's, so the Durdur button
                // can end this one recording's work without touching the queue behind it.
                using var job = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _currentJob = job;
                _currentJobCallId = callId;

                try
                {
                    await ProcessAsync(callId, _settings(), job.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    // Shutting down, and something this loop depends on has already gone. Not a
                    // fault of the recording: leaving it Queued is what lets the next start pick
                    // it up. Reported as a failure it would be abandoned instead.
                    throw new OperationCanceledException();
                }
                catch (Exception e)
                {
                    AppLog.Error("işleme", e, $"görüşme #{callId} işlenemedi");
                }
                finally
                {
                    _currentJob = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>The running job's own cancellation, and whose recording it is working on.</summary>
    private volatile CancellationTokenSource? _currentJob;
    private long _currentJobCallId;

    /// <summary>Recordings the user stopped by hand, so the cancel is not mistaken for shutdown.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _stopRequested = new();

    /// <summary>
    /// Ends the job being worked on right now, at the user's request.
    ///
    /// The distinction from shutdown matters: a shutdown puts the recording back in the queue to
    /// be resumed, which for a deliberate stop would mean the work restarts on the next launch —
    /// the exact opposite of what the button promised. A stopped recording is parked as Skipped
    /// with the reason on it, and "Yeniden işle" remains one click away.
    /// </summary>
    public void StopCurrent()
    {
        if (_currentJob is not { } job) return;

        _stopRequested[_currentJobCallId] = 1;

        try
        {
            job.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job finished between the check and the cancel. Nothing left to stop.
            _stopRequested.TryRemove(_currentJobCallId, out _);
        }
    }

    /// <summary>Recordings already waiting, so pressing retry twice does not queue a copy.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _inQueue = new();

    /// <summary>
    /// Puts a recording in the queue to be transcribed and analysed. Idempotent.
    ///
    /// It has to be: every retry button eventually lands here, and none of them checked whether
    /// the recording was already waiting. A real night's log showed what that becomes — two
    /// failing calls retried over thirty times between midnight and half past, one queued copy
    /// per earlier button press, each attempt burning a minute of cooldown and an upload. The
    /// queue owning the rule is what makes it impossible to reintroduce from a new button.
    /// </summary>
    public void Enqueue(long callId)
    {
        if (!_inQueue.TryAdd(callId, 0)) return;

        _processing.Writer.TryWrite(callId);
    }

    /// <summary>
    /// Which engine to use for one queued recording, overriding the setting.
    ///
    /// Held beside the queue rather than in it, because the queue carries identities and putting a
    /// second value in it would mean changing the channel, the backlog scan and every caller for
    /// something only the retry path uses.
    ///
    /// The reason this exists: a recording that failed on one route is exactly the recording
    /// somebody wants to try on another — the local model when the upload keeps breaking, or a
    /// hosted one when the processor would take four hours. Before this, retrying could only
    /// repeat the route that had already failed, so the button was useless precisely when it was
    /// needed.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _engineOverride = new();

    /// <summary>
    /// Recordings to analyse again without transcribing them again.
    ///
    /// The expensive half is transcription — hours on a machine without a usable GPU — and it is
    /// the half that most often does not need repeating. Somebody who connects a model after the
    /// fact has thirteen finished transcripts and wants the ledger built from them; making them
    /// pay for the audio a second time is the difference between a minute and an afternoon.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _analyseOnly = new();

    /// <summary>The analysis model chosen for one recording, overriding the setting.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _llmOverride = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        long, (LlmProviderKind Kind, string BaseUrl)> _llmRouteOverride = new();

    /// <summary>
    /// Queues a recording to be redone.
    /// </summary>
    /// <param name="asrModelId">An engine from the ASR catalogue, or null to follow the setting.</param>
    /// <param name="analyseOnly">Keep the existing transcript and only run the analysis again.</param>
    /// <param name="llmModel">An analysis model, or null to follow the setting.</param>
    /// <param name="llmRouteKind">A provider to use instead of the configured one, this run only —
    /// the picker offers the local server beside a cloud configuration.</param>
    /// <param name="llmRouteUrl">That provider's endpoint.</param>
    public void EnqueueWith(
        long callId, string? asrModelId = null, bool analyseOnly = false, string? llmModel = null,
        LlmProviderKind? llmRouteKind = null, string? llmRouteUrl = null)
    {
        if (string.IsNullOrWhiteSpace(asrModelId)) _engineOverride.TryRemove(callId, out _);
        else _engineOverride[callId] = asrModelId;

        if (string.IsNullOrWhiteSpace(llmModel)) _llmOverride.TryRemove(callId, out _);
        else _llmOverride[callId] = llmModel;

        if (llmRouteKind is { } routeKind && !string.IsNullOrWhiteSpace(llmRouteUrl))
            _llmRouteOverride[callId] = (routeKind, llmRouteUrl);
        else _llmRouteOverride.TryRemove(callId, out _);

        if (analyseOnly) _analyseOnly[callId] = 1;
        else _analyseOnly.TryRemove(callId, out _);

        Enqueue(callId);
    }

    private static bool IsWatched(CallApp app, AppSettings settings) => app switch
    {
        CallApp.WhatsApp => settings.RecordWhatsApp,
        CallApp.Telegram => settings.RecordTelegram,
        CallApp.Signal => settings.RecordSignal,
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

    /// <summary>
    /// Carries out one detected event.
    ///
    /// Lifted out of the consumer loop so it can be driven directly by a test. Every branch here
    /// can destroy a recording, and none of them was reachable from a test before — which is how
    /// an unguarded Abandoned arm survived long enough to delete a real conversation.
    /// </summary>
    private async Task ApplyDetectedEventAsync(CallEvent callEvent, AppSettings settings)
    {
            switch (callEvent.Kind)
            {
                case CallEventKind.Started:
                    // The switch is honoured here rather than by stopping the watcher, so
                    // that the status card can still say "a call is happening and I am
                    // deliberately not recording it" — which is the reassurance somebody
                    // who turned it off wants.
                    if (!settings.RecordAutomatically)
                    {
                        Notice?.Invoke(this, "Otomatik kayıt kapalı — bu görüşme kaydedilmiyor.");
                        break;
                    }

                    // A recording already running keeps running.
                    //
                    // Ended and Abandoned were guarded and this was not, which left the worst of
                    // the three open: the recorder was replaced mid-recording without being
                    // stopped. The old one kept its devices and kept writing to files nothing
                    // pointed at any more, its row stayed at Recorded with no paths — so it was
                    // re-queued on every launch and rejected by the worker for ever — and up to
                    // thirty seconds of audio was lost with the header the checkpoint timer had
                    // last written.
                    //
                    // The likeliest way in is the case the manual button exists for: somebody
                    // presses "Kaydı başlat" because detection has not latched yet, and a second
                    // later it latches.
                    if (_recorder is not null)
                    {
                        Notice?.Invoke(this, IsManualRecording
                            ? "Elle kayıt sürüyor; algılanan görüşme ayrıca kaydedilmedi."
                            : "Zaten bir kayıt sürüyor; yeni görüşme ayrıca kaydedilmedi.");

                        break;
                    }

                    await BeginRecordingAsync(callEvent, settings);
                    break;

                // Neither of these touches a hand-started recording.
                //
                // The detector watches WhatsApp and Telegram audio sessions and knows
                // nothing about the button. While a manual recording is running, an
                // unrelated call ending — or a two-second notification tone that goes
                // quiet — used to end or destroy it.
                //
                // Abandoned was the worse of the two. It calls DiscardRecording, which
                // deletes both WAV files and stores "Çok kısa kayıt" over the row; a
                // forty-minute meeting recorded on purpose was unrecoverable, and the
                // trigger was an incoming call the user simply ignored. Ended merely cut
                // the recording short, which is bad enough.
                //
                // The rule is the one the button implies: a recording somebody started
                // ends when somebody stops it.
                case CallEventKind.Ended:
                    if (!IsManualRecording) await FinishRecordingAsync(callEvent, settings);
                    break;

                case CallEventKind.Abandoned:
                    if (!IsManualRecording) DiscardRecording();
                    break;
            }
    }

    /// <summary>
    /// Drives one detected event synchronously. Production goes through the channel.
    ///
    /// Public so it can be tested. Every branch it reaches can destroy a recording and none was
    /// reachable from a test before, which is how an unguarded "abandoned" arm survived long
    /// enough to delete a conversation somebody had deliberately recorded.
    /// </summary>
    public void HandleDetectedEventForTests(CallEvent callEvent) =>
        ApplyDetectedEventAsync(callEvent, _settings()).GetAwaiter().GetResult();

    /// <summary>
    /// Stops a hand-started recording and puts it through the usual processing.
    ///
    /// The flag is cleared before the recorder is checked, and that order is the point. Cleared
    /// after, any path that disposed the recorder without clearing it left this method returning
    /// at the guard for ever — so the button stayed on "Kaydı durdur" with nothing behind it, and
    /// pressing it did nothing, permanently, until the application was restarted.
    /// </summary>
    public async Task StopManualRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsManualRecording) return;

        IsManualRecording = false;

        if (_recorder is null)
        {
            // The recorder went away underneath a running manual recording. Nothing to finish,
            // but the interface has to come back to a state somebody can act from.
            UpdateState();
            return;
        }

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
    public Task ReprocessAsync(long callId, CancellationToken cancellationToken = default)
    {
        _repository.SetCallState(callId, ProcessingState.Queued);
        Enqueue(callId);

        return Task.CompletedTask;
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
                Direction = callEvent.Direction,
                ObservedTitle = callEvent.WindowTitle,
            });

            var directory = _paths.RecordingDirectoryFor(callEvent.At);
            var backend = CreateBackend(settings);

            _recorder = new CallRecorder(backend);
            _recorder.Interrupted += (_, reason) => Notice?.Invoke(this, reason);
            _recorder.LevelChanged += (_, levels) => LevelChanged?.Invoke(this, levels);

            // Per-application capture needs to be told which application.
            //
            // ProcessLoopbackCaptureBackend throws when it is not, and it was not: every call
            // failed to record with "Kayıt başlatılamadı" for anybody who turned on "Uygulama
            // bazlı yakalama". The setting looked like a preference and was a switch that broke
            // recording outright.
            await _recorder.StartAsync(
                directory,
                $"call-{_currentCallId}",
                backend.IsProcessIsolated ? TargetProcessFor(callEvent.App) : null);
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

    /// <summary>
    /// The process whose audio per-application capture should follow.
    ///
    /// The root process of the application on the call, so the loopback client covers its whole
    /// tree — WhatsApp runs its surface in WebView2 children and Telegram spawns helpers, and
    /// following only the child that happened to be found would capture the wrong half or
    /// nothing at all.
    ///
    /// Null when the application cannot be found, which the caller turns into device capture:
    /// recording the whole device picks up the same conversation with anything else that is
    /// playing, and that is a far smaller price than not recording it.
    /// </summary>
    private int? TargetProcessFor(CallApp app)
    {
        if (app == CallApp.Unknown) return null;

        try
        {
            foreach (var (pid, owner) in _sessions.Targets.Resolve(DateTimeOffset.Now))
                if (owner == app) return pid;
        }
        catch (Exception)
        {
            // Process enumeration can fail transiently. Device capture is the answer either way.
        }

        return null;
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

    /// <summary>
    /// Closes off a finished recording: stops the devices, writes the row, and queues the work.
    ///
    /// <b>Every statement here is inside one error gate</b>, deliberately and symmetrically with
    /// <see cref="BeginRecordingAsync"/>. Only <c>recorder.Stop()</c> used to be guarded, and
    /// because an <c>async Task</c> method never throws synchronously, the <c>try/catch</c> around
    /// the caller could not see anything the rest of the body threw — it looked like a safety net
    /// and caught nothing. A failure writing the row therefore lost the call in the worst possible
    /// way: the audio was on disk, the row pointed nowhere, no question was asked, no error was
    /// shown, and nothing was ever queued.
    ///
    /// <b>Processing is queued rather than awaited.</b> It used to be awaited right here, which
    /// chained "ask who this was with" to "transcribe it" — so a failure in one took the other
    /// with it, and a dialog left open stopped transcription entirely.
    /// </summary>
    private Task FinishRecordingAsync(CallEvent callEvent, AppSettings settings)
    {
        if (_recorder is null || _currentCallId is not { } callId) return Task.CompletedTask;

        var recorder = _recorder;
        _recorder = null;
        _currentCallId = null;

        try
        {
            RecordingResult result;
            try
            {
                result = recorder.Stop();
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

                // Said rather than done silently. Deleting a recording is not reversible, and a
                // user who has just watched the strip appear and vanish deserves to know that the
                // decision was made and why — otherwise a genuine short call looks like the
                // recorder simply failing.
                Notice?.Invoke(this,
                    $"{result.Duration.TotalSeconds:0} saniyelik kayıt silindi — bağlanmamış arama sayıldı.");

                return Task.CompletedTask;
            }

            if (!result.StreamsAreAligned)
            {
                Notice?.Invoke(this,
                    "İki ses akışı hizalı değil, bu kayıtta kimin ne söylediği güvenilir olmayabilir.");
            }

            // Said out loud rather than left to be discovered in an empty transcript. A capture
            // that produces silence looks identical to a successful one from every other angle,
            // and the user has no way to tell the difference until they go looking for a
            // conversation that was never recorded.
            if (result.HasSilentStream) Notice?.Invoke(this, result.AudioSummary);

            // After the short-recording guard, so a row is never pointed at files that were just
            // deleted, and before the state changes to Queued, so the transcriber never sees a
            // call that is ready to process but has no audio attached to it.
            _repository.CompleteCall(
                callId,
                result.MicPath,
                result.FarPath,
                result.Duration,
                callEvent.At,
                $"mic: {result.MicStats}; far: {result.FarStats}");

            _repository.SetCallState(callId, ProcessingState.Queued);

            RaiseCallFinished(new CallFinished(
                callId,
                result.Duration,
                callEvent.WindowTitle,
                callEvent.App,
                NeedsLabel: _repository.GetCall(callId)?.ContactId is null,
                AudioSummary: result.AudioSummary,
                HasSilentStream: result.HasSilentStream));

            Enqueue(callId);
        }
        catch (Exception e)
        {
            AppLog.Error("kayıt", e, $"görüşme #{callId} sonlandırılamadı");
            Notice?.Invoke(this, $"Kayıt sonlandırılamadı: {e.Message}");

            try
            {
                _repository.SetCallState(callId, ProcessingState.Failed, e.Message);
            }
            catch (Exception inner)
            {
                // The database is the thing that failed. Nothing else can be done here, but the
                // fault has to be written down or it is invisible.
                AppLog.Error("kayıt", inner, $"görüşme #{callId} başarısız işaretlenemedi");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Tells every listener that a call finished, and does not let one of them silence the rest.
    ///
    /// A multicast delegate stops at the first subscriber that throws. There are three here — the
    /// log, the window that asks who the call was with, and the list refresh — and they are
    /// invoked in subscription order, so an exception in the first would have taken the labelling
    /// dialog and the refresh with it. They are independent concerns and one failing is not a
    /// reason for the others not to happen.
    /// </summary>
    private void RaiseCallFinished(CallFinished finished)
    {
        if (CallFinished is not { } handlers) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<CallFinished>)handler)(this, finished);
            }
            catch (Exception e)
            {
                AppLog.Error("kayıt", e, "görüşme bitti bildirimi bir dinleyicide hata verdi");
            }
        }
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

            // Whatever route got here, the button must not be left claiming a recording that no
            // longer exists.
            IsManualRecording = false;
            UpdateState();
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
            //
            // Only when the GPU is about to be used, though. This wait used to apply to cloud
            // uploads too, where no local model runs at all — so every cloud attempt, including
            // every retry of a failing one, sat idle for a minute doing nothing anyone wanted.
            if (settings.GpuCooldownSeconds > 0 && MightUseGpu(callId, settings))
                await Task.Delay(TimeSpan.FromSeconds(settings.GpuCooldownSeconds), cancellationToken);

            // Analysing again does not mean transcribing again.
            //
            // Transcription is the expensive half — hours on a machine without a usable GPU — and
            // it is the half that usually does not need repeating. Somebody who connects a model
            // after the fact has finished transcripts already and wants the ledger built from
            // them; charging them the audio a second time is the difference between a minute and
            // an afternoon. The request is consumed here, so it applies once.
            var analyseRequested = _analyseOnly.TryRemove(call.Id, out _);
            var keepTranscript = analyseRequested && _repository.CountSegments(call.Id) > 0;

            if (!keepTranscript) await TranscribeAsync(call, settings, cancellationToken);
            else _repository.SetCallState(call.Id, ProcessingState.Transcribed);

            // Analysis is attempted only when there is something to analyse with.
            //
            // Without this the pipeline walked into the worst kind of stall. An unconfigured or
            // unreachable model still gets a request per transcript chunk, and the shared
            // HttpClient waits ten minutes before giving up — so a twelve-chunk conversation hung
            // for two hours, holding the processing slot the whole time, and every recording made
            // meanwhile queued up behind it. The user sees "Çözümleniyor" and nothing else, for
            // the rest of the day.
            //
            // The transcript is already written and saved at this point, so declining to analyse
            // costs the ledger entries and the summary, not the conversation.
            // An explicit "çözümle" wins over the automatic-analysis switch being off. The
            // switch governs what happens on its own after a call; it must not swallow a request
            // the user just made by hand — which it did, silently: "yalnızca yeniden çözümle"
            // with the switch off consumed the request, changed nothing and said nothing.
            if (settings.AnalyseAutomatically || analyseRequested)
            {
                if (await AnalysisServiceReachableAsync(settings, cancellationToken))
                {
                    await AnalyseAsync(callId, settings, cancellationToken);
                }
                else
                {
                    _repository.SetCallState(callId, ProcessingState.Transcribed,
                        "Çözümleme yapılmadı: çalışan bir yapay zekâ servisi bulunamadı. "
                        + "Ayarlardan bir sağlayıcı seçip çalıştırdığında bu görüşme yeniden "
                        + "çözümlenebilir — metin duruyor, yeniden yazıya dökmek gerekmiyor.");

                    Notice?.Invoke(this,
                        "Görüşme yazıya döküldü. Özet çıkarılmadı — çalışan bir yapay zekâ servisi yok.");

                    return;
                }
            }

            // With the words safely out of the audio and into the archive, the dead air can go —
            // and then the rest can shrink.
            if (settings.TrimSilenceAfterProcessing) TrimSilence(callId);
            if (settings.CompressAudioAfterProcessing) CompressAudio(callId);

            if (settings.ExportToObsidian && !string.IsNullOrWhiteSpace(settings.ObsidianVaultPath))
                Export(callId, settings);

            if (settings.ExportToNotion
                && !string.IsNullOrWhiteSpace(settings.NotionApiKey)
                && !string.IsNullOrWhiteSpace(settings.NotionDatabaseId))
            {
                await ExportToNotionAsync(callId, settings, cancellationToken);
            }
        }
        // Only a real shutdown puts the call back in the queue.
        //
        // This used to catch every OperationCanceledException, and an HttpClient timeout throws
        // exactly that — so a hung endpoint silently returned the call to Queued rather than
        // marking it failed. It then never appeared under "işlenemedi", never offered a retry, and
        // was tried again on every single startup, forever. With the analysis writes being
        // additive, each of those retries also appended another full copy of the person's
        // commitments and claims to their ledger.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_stopRequested.TryRemove(callId, out _))
            {
                // The user's stop, not a shutdown. Parked rather than requeued: putting it back
                // in the queue would restart the very work the button just ended.
                _repository.SetCallState(callId, ProcessingState.Skipped,
                    "Kullanıcı durdurdu. Yeniden işle ile istediğin zaman tekrar denenebilir.");

                Notice?.Invoke(this, "İşlem durduruldu. Kayıt duruyor, yeniden işlenebilir.");
            }
            else
            {
                _repository.SetCallState(callId, ProcessingState.Queued);
            }
        }
        catch (OperationCanceledException e)
        {
            // Cancelled by something other than us: a timeout. That is a failure, and it has to
            // look like one.
            _repository.SetCallState(callId, ProcessingState.Failed,
                "Servis zaman aşımına uğradı: " + e.Message);

            Notice?.Invoke(this, "İşleme zaman aşımına uğradı. Görüşme kaydı duruyor, tekrar denenebilir.");
        }
        catch (Exception e)
        {
            _repository.SetCallState(callId, ProcessingState.Failed, e.Message);
            Notice?.Invoke(this, $"İşleme başarısız: {e.Message}");
        }
        finally
        {
            // Whatever way the job ended, a stop request for it is spent.
            //
            // It was only consumed on the cancellation path. A job that finished normally in the
            // race between StopCurrent and the cancel landing — or that failed, or timed out —
            // left its entry behind, and the next shutdown of that same call id then read as
            // "the user stopped this": parked as Skipped, never resumed, for a recording nobody
            // had asked to stop.
            _stopRequested.TryRemove(callId, out _);

            _gpu.Release();
            State = OrchestratorState.Idle;
            StateChanged?.Invoke(this, State);

            AnnounceResult(callId);
        }
    }

    /// <summary>
    /// Says what became of a recording, once there is something to say.
    ///
    /// Read back from the database rather than accumulated in memory, so the announcement matches
    /// what the archive actually holds — including when the work was done by an earlier run and
    /// picked up from the backlog. Nothing here may throw: this is in a <c>finally</c>, and an
    /// exception would replace whatever real fault was being reported.
    /// </summary>
    private void AnnounceResult(long callId)
    {
        try
        {
            var call = _repository.GetCall(callId);
            if (call is null) return;

            var name = call.ContactId is { } id ? _repository.GetContact(id)?.Name : null;
            var summary = _repository.GetSummary(callId)?.Summary;

            var processed = new CallProcessed(
                callId,
                name ?? "İsimsiz",
                call.Duration,
                summary,
                Succeeded: call.State is ProcessingState.Analysed or ProcessingState.Transcribed,
                Failure: call.State == ProcessingState.Failed ? call.FailureReason : null);

            foreach (var handler in CallProcessed?.GetInvocationList() ?? [])
            {
                try
                {
                    ((EventHandler<CallProcessed>)handler)(this, processed);
                }
                catch (Exception e)
                {
                    AppLog.Error("işleme", e, "sonuç bildirimi bir dinleyicide hata verdi");
                }
            }
        }
        catch (Exception e)
        {
            AppLog.Error("işleme", e, $"görüşme #{callId} sonucu bildirilemedi");
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
            var hello = await _worker().ProbeAsync(cancellationToken);

            // Usable, not Available. The count comes from the driver and is 1 on any machine with
            // a working card, including one whose cuBLAS cannot load — which is the machine that
            // then routes every call to the local engine and fails each one after it has ended.
            _localTranscriptionUsable = hello.Cuda?.Usable == true;
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

            // The address, in the log, before the attempt. A night of "OpenAI: 404: Invalid URL"
            // was undiagnosable precisely because nothing recorded where "OpenAI" actually
            // pointed — it was a leftover custom base URL, and the label said otherwise.
            AppLog.Write("kayıt",
                $"deneniyor: {endpoint.ResolvedName} @ {endpoint.ResolvedBaseUrl} "
                + $"· model {endpoint.ResolvedModel}");

            try
            {
                return await _worker().TranscribeAsync(new TranscriptionRequest
                {
                    Id = $"call-{call.Id}",
                    Engine = EngineNameFor(model),
                    ModelRef = endpoint.ToModelRef(),
                    Device = settings.AsrDevice,
                    Language = settings.Language,
                    // The worker reads PCM; a compressed archive is expanded into the cache first.
                    MicPath = AudioMaterialiser.EnsurePcm(call.MicPath),
                    FarPath = AudioMaterialiser.EnsurePcm(call.FarPath),
                    CacheDir = _paths.Models,
                }, progress: new Progress<Core.Asr.WorkerProgress>(p =>
                {
                    Report(call.Id, StageName(p.Stage), p.Percent / 100.0);

                    // How far it got before it died is the line a failed transcription is
                    // diagnosed from; the stage name is the worker's own, never a path.
                    if (settings.VerboseLog) AppLog.Write("çeviri", $"  {p.Stage} %{p.Percent:0}");
                }), cancellationToken);
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

    /// <summary>
    /// Removes the dead air from a processed recording — the answer to "çok yer kaplıyor".
    ///
    /// Runs only after transcription has what it needs, only once per recording, and only on
    /// moments that are silent in BOTH streams at once: the streams share a clock, and cutting
    /// them anywhere but together would slide one against the other. Both files are written
    /// beside themselves and swapped only when both succeeded; every stored timestamp moves in
    /// one transaction with the trim stamp. A failure here is logged and ignored — losing disk
    /// economy is nothing, losing a recording would be everything.
    /// </summary>
    /// <summary>
    /// Replaces a processed call's PCM streams with Opus, one stream at a time.
    ///
    /// Each stream is encoded to a sibling file, decoded again to prove it holds every frame,
    /// and only then does the WAV go and the row learn the new path — so a crash anywhere leaves
    /// either the original or a verified replacement, never neither. The two streams are never
    /// mixed: the separation is what makes speaker attribution a fact, and it survives.
    /// </summary>
    private void CompressAudio(long callId)
    {
        try
        {
            var call = _repository.GetCall(callId);
            if (call is null) return;

            long before = 0, after = 0;
            var mic = CompressStream(call.MicPath, ref before, ref after);
            var far = CompressStream(call.FarPath, ref before, ref after);

            if (mic == call.MicPath && far == call.FarPath) return;

            _repository.SetAudioPaths(callId, mic, far);

            // The mixed copy was built from the PCM names; the next play rebuilds it from the
            // decoded cache.
            if (call.MicPath is { } oldMic)
            {
                var mix = ConversationMix.PathFor(oldMic);
                if (File.Exists(mix)) File.Delete(mix);
            }

            AppLog.Write("veri",
                $"sıkıştırıldı: görüşme #{callId} · {before / 1_048_576.0:0.0} MB → {after / 1_048_576.0:0.0} MB");
        }
        catch (Exception e)
        {
            // Nothing is lost when this fails: the original is still there and the row still
            // points at it. Worth a line, not a notice.
            AppLog.Error("veri", e, $"görüşme #{callId} sıkıştırılamadı");
        }
    }

    /// <summary>One stream. Returns the path the row should hold afterwards.</summary>
    private static string? CompressStream(string? wavPath, ref long before, ref long after)
    {
        if (wavPath is null || AudioMaterialiser.IsCompressed(wavPath) || !File.Exists(wavPath)) return wavPath;

        var oggPath = OpusArchive.CompressedPathFor(wavPath);
        var frames = OpusArchive.Encode(wavPath, oggPath);

        // Verified by decoding, not by trusting the encoder: a truncated file decodes short.
        var decoded = OpusArchive.CountFrames(oggPath, AudioFormat.WhisperPcm.SampleRate);
        var tolerance = AudioFormat.WhisperPcm.SampleRate / 10; // 100 ms of codec padding

        if (Math.Abs(decoded - frames) > tolerance)
        {
            File.Delete(oggPath);
            throw new InvalidDataException($"sıkıştırılan ses eksik çözüldü ({decoded}/{frames} örnek)");
        }

        before += new FileInfo(wavPath).Length;
        after += new FileInfo(oggPath).Length;

        File.Delete(wavPath);
        return oggPath;
    }

    /// <summary>
    /// Shrinks the recordings that were already on disk before compression existed, one at a
    /// time, at the lowest priority, off the processing queue.
    ///
    /// This is where the disk actually comes back: the user who asked has months of PCM. It is
    /// a background thread rather than queue work because it must never delay a transcription,
    /// and lowest priority because it must never be felt during a call.
    /// </summary>
    private void CompressBacklog()
    {
        if (!_settings().CompressAudioAfterProcessing) return;

        var thread = new Thread(() =>
        {
            try
            {
                var calls = _repository.CallsWithUncompressedAudio();
                if (calls.Count == 0) return;

                AppLog.Write("veri", $"sıkıştırma birikimi: {calls.Count} görüşme");

                foreach (var call in calls)
                {
                    if (_disposed || _cts?.IsCancellationRequested == true) return;

                    // Not while the processor has it open, and not once the switch is off.
                    if (_currentJobCallId == call.Id) continue;
                    if (!_settings().CompressAudioAfterProcessing) return;

                    CompressAudio(call.Id);
                }
            }
            catch (Exception e)
            {
                AppLog.Error("veri", e, "sıkıştırma birikimi yarıda kaldı");
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "ses-sikistirma",
        };

        thread.Start();
    }

    private void TrimSilence(long callId)
    {
        try
        {
            var call = _repository.GetCall(callId);

            if (call?.MicPath is not { } mic || call.FarPath is not { } far) return;
            if (call.TrimmedAt is not null) return;

            // Trimming rewrites the PCM in place. A compressed archive is not PCM, and the
            // cut plan would have run over the decoded copy and then moved a WAV onto an
            // .ogg path. Compression runs after trimming, never before it, so this only
            // guards a recording compressed by an earlier run.
            if (AudioMaterialiser.IsCompressed(mic) || AudioMaterialiser.IsCompressed(far)) return;
            if (!File.Exists(mic) || !File.Exists(far)) return;

            var cuts = Core.Audio.SilenceTrimmer.PlanCuts(mic, far);
            if (cuts.Count == 0) return;

            var removedMs = cuts.Sum(c => c.Frames) * 1000
                            / Core.Audio.AudioFormat.WhisperPcm.SampleRate;

            var before = new FileInfo(mic).Length + new FileInfo(far).Length;

            Core.Audio.SilenceTrimmer.Apply(mic, mic + ".trim", cuts);
            Core.Audio.SilenceTrimmer.Apply(far, far + ".trim", cuts);

            // Both written whole before either original is touched — and then swapped as one.
            //
            // Two independent moves left a window where the first had succeeded and the second
            // had not. The microphone stream was trimmed and the speaker stream was not, so the
            // two were offset against each other by the length of every silence removed — and
            // since the timestamps were never shifted either, every line was now attributed to
            // the wrong moment, and the next run trimmed the microphone a second time. The
            // originals are kept aside until both replacements are in place, and put back if
            // either fails.
            var micOld = mic + ".old";
            var farOld = far + ".old";

            File.Move(mic, micOld, overwrite: true);
            File.Move(far, farOld, overwrite: true);

            try
            {
                File.Move(mic + ".trim", mic, overwrite: true);
                File.Move(far + ".trim", far, overwrite: true);
            }
            catch
            {
                if (!File.Exists(mic) && File.Exists(micOld)) File.Move(micOld, mic);
                else if (File.Exists(micOld)) File.Move(micOld, mic, overwrite: true);

                if (File.Exists(farOld)) File.Move(farOld, far, overwrite: true);

                foreach (var leftover in new[] { mic + ".trim", far + ".trim" })
                    if (File.Exists(leftover)) File.Delete(leftover);

                throw;
            }

            File.Delete(micOld);
            File.Delete(farOld);

            // The mixed copy, if one was made, is on the old clock now. Deleted rather than
            // rebuilt: it is derived, and the next export rebuilds it from the trimmed streams.
            var mix = Core.Audio.ConversationMix.PathFor(mic);
            if (File.Exists(mix)) File.Delete(mix);

            _repository.ApplyTrim(
                callId,
                Core.Audio.SilenceTrimmer.ToMilliseconds(cuts),
                call.Duration - TimeSpan.FromMilliseconds(removedMs));

            var after = new FileInfo(mic).Length + new FileInfo(far).Length;

            AppLog.Write("veri",
                $"sessizlik kırpıldı: görüşme #{callId} · {cuts.Count} aralık · "
                + $"{TimeSpan.FromMilliseconds(removedMs):mm\\:ss} ölü hava · "
                + $"{before / 1024 / 1024} MB → {after / 1024 / 1024} MB");
        }
        catch (Exception e)
        {
            AppLog.Error("veri", e, $"görüşme #{callId} sessizliği kırpılamadı — kayıt olduğu gibi duruyor");
        }
    }

    /// <summary>
    /// Whether processing this recording could touch the graphics card.
    ///
    /// Peeked, not consumed — the override still belongs to <see cref="TranscribeAsync"/>. Errs
    /// on the side of true: an unnecessary cooldown wastes a minute, a missing one throttles the
    /// machine mid-call, and only one of those is felt while somebody is still talking.
    /// </summary>
    private bool MightUseGpu(long callId, AppSettings settings)
    {
        if (_engineOverride.TryGetValue(callId, out var chosen)
            && AsrCatalog.TryGet(chosen, out var picked))
        {
            return !picked.SendsAudioOffMachine;
        }

        return settings.AsrMode != TranscriptionMode.CloudOnly;
    }

    private async Task TranscribeAsync(Call call, AppSettings settings, CancellationToken cancellationToken)
    {
        _repository.SetCallState(call.Id, ProcessingState.Transcribing);

        // An engine chosen for this one recording wins over the setting, and is consumed as it is
        // read: a retry that picked the cloud must not silently become the standing choice for
        // everything queued behind it.
        var model =
            _engineOverride.TryRemove(call.Id, out var chosen)
            && AsrCatalog.TryGet(chosen, out var picked)
                ? picked
                : settings.ResolveAsrModel(await LocalTranscriptionUsableAsync(cancellationToken));

        if (model.SendsAudioOffMachine)
        {
            // Said every time rather than once at setup: the automatic mode can start uploading
            // because a driver broke, and that must never happen quietly.
            Notice?.Invoke(this,
                $"Bu görüşme yazıya dökülmek üzere {model.DisplayName} servisine yükleniyor.");
        }

        // Wall clock rather than the worker's own reported figure, and for both routes. What
        // somebody wants to know is how long their machine takes to turn a call into text, which
        // includes loading the model and reading the files — the parts that dominate on a machine
        // without a usable GPU, and exactly the parts the worker's internal timer excludes.
        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        WorkerResult result;

        try
        {
            result = model.SendsAudioOffMachine
                ? await TranscribeInCloudAsync(call, model, settings, cancellationToken)
                : await _worker().TranscribeAsync(new TranscriptionRequest
                {
                    Id = $"call-{call.Id}",
                    Engine = EngineNameFor(model),
                    ModelRef = model.ModelRef,
                    Device = settings.AsrDevice,
                    Language = settings.Language,
                    // The worker reads PCM; a compressed archive is expanded into the cache first.
                    MicPath = AudioMaterialiser.EnsurePcm(call.MicPath),
                    FarPath = AudioMaterialiser.EnsurePcm(call.FarPath),
                    CacheDir = _paths.Models,
                }, progress: new Progress<Core.Asr.WorkerProgress>(p =>
                {
                    Report(call.Id, StageName(p.Stage), p.Percent / 100.0);

                    // How far it got before it died is the line a failed transcription is
                    // diagnosed from; the stage name is the worker's own, never a path.
                    if (settings.VerboseLog) AppLog.Write("çeviri", $"  {p.Stage} %{p.Percent:0}");
                }), cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failure is a run too, and until this existed the usage screen could only ever
            // report zero of them. Somebody whose transcription had been failing for two days —
            // which happened, with a missing Python package — saw a spotless history.
            //
            // Shutdown is not a failure, hence the filter: the application closing mid-transcript
            // is not evidence about the engine.
            clock.Stop();

            _repository.RecordRun(
                call.Id,
                ProcessingStage.Transcribe,
                engine: EngineNameFor(model),
                startedAt,
                clock.Elapsed,
                call.Duration,
                succeeded: false);

            throw;
        }

        clock.Stop();

        _repository.RecordRun(
            call.Id,
            ProcessingStage.Transcribe,
            // Scrubbed: the worker echoes the full "url|key|model" reference, and recording it
            // verbatim put a live API key into the database and onto the provenance line.
            engine: Core.Asr.SttEndpoint.ScrubRef(result.ModelRef ?? result.Engine ?? model.DisplayName),
            startedAt,
            clock.Elapsed,
            call.Duration);

        // A result with no lines in it is not a transcript, and must not be written over one.
        //
        // Both routes can return this as an ordinary success: the worker merges two possibly
        // empty streams and emits a result, and a cloud endpoint answering 200 with a body in a
        // shape this code does not recognise yields an empty list. ReplaceSegments deletes every
        // existing row before inserting, so an empty result deleted the only text record of the
        // conversation, dropped it from the search index, and left the ledger quoting lines that
        // no longer existed anywhere — after which the call was marked Transcribed and announced
        // as a success.
        if (result.Segments.Count == 0)
        {
            var existing = _repository.GetSegments(call.Id).Count;

            throw new InvalidOperationException(existing > 0
                ? "Yazıya dökme boş sonuç döndürdü. Var olan döküm korundu — modeli ya da " +
                  "servisi değiştirip yeniden deneyebilirsin."
                : "Yazıya dökme boş sonuç döndürdü: konuşma bulunamadı. Ses kaydı duruyor.");
        }

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

    /// <summary>
    /// Whether there is a model running that analysis can actually talk to.
    ///
    /// Transcription and analysis are separate jobs and only one of them is the point of the
    /// recorder. Writing down what was said needs nothing but this machine; turning that into a
    /// summary and a ledger needs a language model, and there frequently is not one. When there is
    /// not, the right outcome is a finished transcript and a plain sentence saying the summary was
    /// skipped — not a failure, and above all not a wait.
    ///
    /// <b>Asked rather than assumed, and this is the part that was missing.</b> The settings alone
    /// cannot answer it: the default provider is a local server, so a machine that has never run
    /// one still looks configured. Analysis then went ahead, and every transcript chunk waited on
    /// a request to a port with nothing behind it. The processing slot was held the whole time and
    /// every recording made meanwhile queued up behind it.
    ///
    /// One short probe, and the answer is remembered for a few minutes: this is asked once per
    /// recording, recordings arrive minutes apart, and starting a model between two of them is
    /// something the user would do deliberately and can wait a moment for.
    /// </summary>
    private async Task<bool> AnalysisServiceReachableAsync(
        AppSettings settings, CancellationToken cancellationToken)
    {
        // Nothing to ask when there is nothing configured to ask. Logged, because from the
        // outside this case and a crash look identical: the user asked for analysis and nothing
        // happened. The log has to say which one it was.
        if (!settings.LlmReachableInPrinciple)
        {
            AppLog.Write("çözümleme",
                $"atlanıyor: yapılandırılmış bir yapay zekâ servisi yok "
                + $"(sağlayıcı={settings.Provider.DisplayName}, model={settings.ResolvedModelName})");
            return false;
        }

        // A hosted service with a key is taken at its word. Probing it costs a request against a
        // metered account to answer a question its first real call answers anyway.
        if (settings.Provider.SendsDataOffMachine) return true;

        if (_analysisReachable is { } remembered
            && DateTimeOffset.UtcNow - _analysisCheckedAt < TimeSpan.FromMinutes(5))
        {
            return remembered;
        }

        var client = LlmClientFactory.Create(
            _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        bool reachable;

        try
        {
            reachable = await client.IsAvailableAsync(deadline.Token);
        }
        catch (Exception)
        {
            // Unreachable is the answer, not an error to report. A local server that is not
            // running is the ordinary case, not a fault.
            reachable = false;
        }

        _analysisReachable = reachable;
        _analysisCheckedAt = DateTimeOffset.UtcNow;

        AppLog.Write("çözümleme", reachable
            ? $"servis hazır: {settings.ResolvedBaseUrl}"
            : $"servis yanıt vermiyor, çözümleme atlanıyor: {settings.ResolvedBaseUrl}");

        return reachable;
    }

    private bool? _analysisReachable;
    private DateTimeOffset _analysisCheckedAt;

    private async Task AnalyseAsync(long callId, AppSettings settings, CancellationToken cancellationToken)
    {
        _repository.SetCallState(callId, ProcessingState.Analysing);

        // A route chosen in the picker wins over the configured provider, and like the model
        // override it is consumed as it is read — one deliberate detour, never a standing change.
        var route = _llmRouteOverride.TryRemove(callId, out var chosenRoute)
            ? chosenRoute
            : (Kind: settings.LlmProvider, BaseUrl: settings.ResolvedBaseUrl);

        var routeProvider = LlmProviders.Get(route.Kind);

        var client = LlmClientFactory.Create(
            _http, route.Kind, route.BaseUrl,
            route.Kind == settings.LlmProvider ? settings.LlmApiKey : null);

        // Every fact needed to reconstruct a failed run from the log alone: which recording, how
        // much text, which provider at which address, which model. "Çözümleme çalışmıyor" with an
        // empty log is undiagnosable from a distance — this line is what makes the next such
        // report carry its own answer.
        AppLog.Write("çözümleme",
            $"başlıyor: görüşme #{callId} · {_repository.CountSegments(callId)} satır · "
            + $"{routeProvider.DisplayName} @ {route.BaseUrl} · model {settings.ResolvedModelName}");

        // The pipeline records its own successful run, tokens and all. A failure has to be
        // recorded from out here, because the pipeline throws rather than returning — and without
        // this the usage screen could only ever report zero failures, which reads as a clean
        // history rather than as a counter that was never wired to anything.
        var analysisStartedAt = DateTimeOffset.UtcNow;
        var analysisClock = System.Diagnostics.Stopwatch.StartNew();

        AnalysisReport report;

        try
        {
            report = await new AnalysisPipeline(client, _repository).AnalyseAsync(
                callId,
                new AnalysisOptions
                {
                    // A model chosen for this one recording wins, and is consumed as it is read:
                    // trying a stronger model on one difficult conversation must not silently
                    // become the standing choice for everything queued behind it.
                    Model = _llmOverride.TryRemove(callId, out var chosenLlm)
                        ? chosenLlm
                        : settings.ResolvedModelName,
                    // Only a local backend holds the GPU this machine needs back for Whisper —
                    // judged by the route actually used, not the configured one.
                    UnloadWhenDone = !routeProvider.SendsDataOffMachine,
                },
                progress: new Progress<string>(stage => Report(callId, stage)),
                cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            analysisClock.Stop();

            AppLog.Error("çözümleme", e,
                $"görüşme #{callId} çözümlenemedi ({analysisClock.Elapsed.TotalSeconds:0} sn sonra)");

            _repository.RecordRun(
                callId,
                ProcessingStage.Analyse,
                settings.ResolvedModelName,
                analysisStartedAt,
                analysisClock.Elapsed,
                audio: TimeSpan.Zero,
                succeeded: false);

            throw;
        }

        AppLog.Write("çözümleme",
            $"bitti: görüşme #{callId} · {analysisClock.Elapsed.TotalSeconds:0} sn · "
            + $"{report.CommitmentsFound} söz, {report.ClaimsFound} iddia, "
            + $"{report.QuotesRejected} alıntı reddedildi"
            + (report.Warnings.Count > 0 ? $" · {report.Warnings.Count} uyarı" : ""));

        // A model whose quotes mostly cannot be found is not producing usable evidence, and the
        // user should be told to change it rather than left with a quietly empty ledger.
        if (report.RejectionRate > 0.4)
        {
            Notice?.Invoke(this,
                $"Çözümlemede üretilen alıntıların %{report.RejectionRate * 100:0}'ı metinde bulunamadı. " +
                "Bu model bu iş için uygun olmayabilir.");
        }

        _repository.SetCallState(callId, ProcessingState.Analysed);

        // The user's proposed next moves — cheap, quote-anchored, suggestions only. Failure
        // never fails the call: the ledger is already saved.
        if (settings.ExtractActions)
        {
            try
            {
                var actions = await new Core.Analysis.ActionExtraction(client, _repository).RunAsync(
                    callId, settings.ResolvedModelName, cancellationToken);

                AppLog.Write("aksiyon", actions.Ok
                    ? $"görüşme #{callId} · {actions.Actions.Count} öneri"
                      + (actions.RejectedCount > 0 ? $" · {actions.RejectedCount} alıntı elendi" : "")
                    : $"görüşme #{callId} · çıkarılamadı: {actions.Problem}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                AppLog.Error("aksiyon", e, $"görüşme #{callId} aksiyon çıkarımı başarısız");
            }
        }

        // The consistency check, when the user opted into paying for it on every analysis.
        // A failure here never fails the call: the ledger is already built and saved, and a
        // secondary read that could not run is a notice, not a processing failure.
        if (settings.ConsistencyAutomatically)
        {
            try
            {
                var consistency = await new Core.Analysis.ConsistencyAnalysis(client, _repository).RunAsync(
                    callId,
                    settings.ResolvedConsistencyModel,
                    useLedgerContext: settings.ConsistencyUsesLedgerContext,
                    otherPartyOnly: settings.ConsistencyOtherPartyOnly,
                    sendsDataOffMachine: settings.Provider.SendsDataOffMachine,
                    cancellationToken);

                AppLog.Write("tutarlılık", consistency.Ok
                    ? $"görüşme #{callId} · {consistency.Findings.Count} bulgu"
                      + (consistency.RejectedCount > 0 ? $" · {consistency.RejectedCount} alıntı elendi" : "")
                      + (consistency.Warning is not null ? " · uyarı notu yazıldı" : "")
                    : $"görüşme #{callId} · koşulamadı: {consistency.Problem}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                AppLog.Error("tutarlılık", e, $"görüşme #{callId} tutarlılık denetimi başarısız");
            }
        }

        // The opt-in assessment rides with the check it belongs beside. A failure here is its
        // own failure — the ledger and the consistency findings above are already saved.
        if (settings.DeceptionEnabled)
        {
            try
            {
                var deception = await new Core.Analysis.DeceptionAnalysis(client, _repository).RunAsync(
                    callId, settings.ResolvedConsistencyModel, cancellationToken);

                AppLog.Write("değerlendirme", deception.Ok
                    ? $"görüşme #{callId} · düzey {deception.Level} · {deception.Tactics.Count} taktik"
                      + (deception.RejectedCount > 0 ? $" · {deception.RejectedCount} alıntı elendi" : "")
                    : $"görüşme #{callId} · koşulamadı: {deception.Problem}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                AppLog.Error("değerlendirme", e, $"görüşme #{callId} değerlendirme başarısız");
            }
        }
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
            // The exception message is not forwarded: it names the vault file, and the vault file
            // is named after the person. Every Notice is written to a log the user is invited to
            // send to somebody else, under a header promising it carries no contact names.
            Notice?.Invoke(this,
                $"Obsidian dışa aktarımı başarısız ({e.GetType().Name}). Kasa yolunu ve yazma " +
                "iznini kontrol et.");
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

    /// <summary>
    /// Queues every recording that was left waiting by a crash or a shutdown, oldest first.
    ///
    /// Queues rather than runs. This is called during startup, and processing each one inline
    /// meant the backlog held the single processing slot for as long as it took — so a call made
    /// shortly after opening the application waited behind however many recordings had piled up
    /// while it was closed. Through the same queue, a live call's recording is never behind them.
    /// </summary>
    public Task ProcessBacklogAsync(CancellationToken cancellationToken = default)
    {
        ReclaimStrandedRecordings();
        CompressBacklog();

        foreach (var call in _repository.CallsAwaitingProcessing())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enqueue(call.Id);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reattaches recordings that a crash left on disk with nobody pointing at them.
    ///
    /// A row is inserted when recording starts and learns where its audio is only when the
    /// recording ends properly. Dispose covers a normal quit; a crash, a power cut or a kill from
    /// Task Manager covers nothing, and the next start found a waiting call with no audio, could
    /// not process it, and marked it Failed — while the two WAV files sat intact in the month's
    /// folder under the name the recorder gave them. The recording was on disk and the archive
    /// said it was lost.
    ///
    /// The files are where the recorder put them, named after the call, so they can be found
    /// without guessing. Their headers say they are empty, because the length is patched in on
    /// close; WavRepair reads the real length back. Only a row with nothing attached is touched.
    /// </summary>
    private void ReclaimStrandedRecordings()
    {
        foreach (var call in _repository.CallsWithoutAudio())
        {
            var directory = _paths.RecordingDirectoryFor(call.StartedAt);
            var mic = Path.Combine(directory, $"call-{call.Id}-mic.wav");
            var far = Path.Combine(directory, $"call-{call.Id}-far.wav");

            var micLength = File.Exists(mic) ? WavRepair.Finalise(mic) : null;
            var farLength = File.Exists(far) ? WavRepair.Finalise(far) : null;

            if (micLength is null && farLength is null) continue;

            var duration = micLength > farLength ? micLength.Value : farLength ?? micLength!.Value;

            _repository.CompleteCall(
                call.Id,
                micLength is null ? null : mic,
                farLength is null ? null : far,
                duration,
                call.StartedAt + duration,
                "kurtarıldı: kayıt bitmeden kapanmış");

            _repository.SetCallState(call.Id, ProcessingState.Queued);

            AppLog.Write("kayıt",
                $"görüşme #{call.Id} kurtarıldı: {duration.TotalSeconds:0} sn · "
                + (micLength is null ? "mikrofon yok" : "mikrofon var") + " · "
                + (farLength is null ? "karşı taraf yok" : "karşı taraf var"));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // A recording in progress is finished properly rather than abandoned.
        //
        // Disposing the recorder writes correct WAV headers but throws away the result, so the
        // row never learned where its audio was, no question was asked, and the next start found
        // a Queued call with null paths and marked it permanently Failed. Quitting during a call
        // therefore lost that conversation while the audio sat intact on disk.
        try
        {
            FinishRecordingAsync(
                new CallEvent(CallEventKind.Ended, DateTimeOffset.Now, CallApp.Unknown, null, TimeSpan.Zero),
                _settings()).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception e)
        {
            AppLog.Error("kayıt", e, "kapanışta kayıt sonlandırılamadı");
        }

        _cts?.Cancel();
        _work.Writer.TryComplete();
        _processing.Writer.TryComplete();

        try
        {
            // The processor is not waited on: it may be minutes into transcribing, and holding
            // shutdown for that would look like the application hanging. Its work is durable —
            // the row stays Queued and ProcessBacklogAsync picks it up on the next start.
            Task[] running = [.. new[] { _loop, _recordingWorker }.Where(t => t is not null).Select(t => t!)];
            if (running.Length > 0) Task.WaitAll(running, TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Cancellation.
        }

        _recorder?.Dispose();
        _sessions.Dispose();

        // The token source and the processing slot are deliberately NOT disposed.
        //
        // The processor above is not waited on, on purpose — it may be minutes into transcribing
        // and holding shutdown for that looks like a hang. But disposing what it is still using
        // turns that considered decision into a bug: the queue consumer was either parked on
        // _gpu.WaitAsync or about to Release it, and both then threw ObjectDisposedException.
        //
        // That exception is not an OperationCanceledException, so the consumer's cancellation
        // guard did not catch it and every queued recording was logged as "işlenemedi" and left
        // marked Failed. Observed in a real log: closing the application failed calls #9 through
        // #13 in one burst, none of which had anything wrong with them. The comment above promised
        // "the row stays Queued and ProcessBacklogAsync picks it up on the next start", and this
        // is what broke that promise.
        //
        // Leaking them is correct rather than merely expedient: the process is exiting, neither
        // holds an operating-system handle here, and the alternative is corrupting the state of
        // work that was going to be resumed.
        _cts?.Cancel();
    }
}
