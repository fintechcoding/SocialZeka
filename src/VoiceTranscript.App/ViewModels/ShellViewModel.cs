using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

public enum ShellPage
{
    Overview,

    /// <summary>
    /// What did not hold, across everybody.
    ///
    /// Second in the list rather than buried inside a contact, because this is the page the
    /// application exists for. Everything else is machinery in service of it.
    /// </summary>
    Ledger,

    Contacts,

    /// <summary>
    /// What has been processed, what has not, and what went wrong.
    ///
    /// Its own page rather than a strip on the first screen, because the question it answers —
    /// "is the transcription actually happening" — is asked while looking at a list of recordings,
    /// and answering it needs room for a reason beside each one. On a machine without a usable GPU
    /// this is the difference between an application that is working slowly and one that appears
    /// to have hung.
    /// </summary>
    Processing,

    Search,

    /// <summary>
    /// A question put to the whole archive, answered with quotes.
    ///
    /// Kept apart from Search because the two questions are different. Search answers "find me
    /// the word" and a result list is the right shape for that. This answers "what happened
    /// about this", where what is wanted is a sentence with the evidence underneath it.
    /// </summary>
    Ask,

    /// <summary>Is any of this actually working. See <see cref="HealthViewModel"/>.</summary>
    Health,
}

/// <summary>
/// Holds the window together: which page is showing, what the recorder is doing, and any notice
/// that needs saying.
///
/// The status is deliberately prominent. This application spends almost all of its time doing
/// nothing visible, and the one question the user has when they glance at it is whether it is
/// actually watching — a question the old layout answered with a small grey label that said the
/// same thing whether capture was working or had failed on startup.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly CallOrchestrator _orchestrator;

    /// <summary>
    /// The UI thread, captured at construction.
    ///
    /// The recorder watches audio sessions on a background thread, so every event it raises
    /// arrives off the UI thread. Updating an observable collection from there throws — WPF
    /// refuses changes to a bound collection from anywhere but the dispatcher — and the failure
    /// surfaces as a detection error rather than as what it is, which makes it look like the
    /// recorder itself broke.
    /// </summary>
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();

    public ShellViewModel(
        Repository repository,
        CallOrchestrator orchestrator,
        Func<AppSettings> settings,
        HealthViewModel health,
        AppPaths paths)
    {
        _orchestrator = orchestrator;
        Health = health;

        Overview = new OverviewViewModel(repository, settings, paths);
        Ledger = new LedgerViewModel(repository);
        Contacts = new ContactsViewModel(repository);
        Processing = new ProcessingViewModel(repository, settings);
        AiStatus = new AiStatusViewModel(settings, App.HttpClient, repository);

        // The service is fetched through a function rather than captured, because it is built
        // after the window exists — the startup check runs on a delay so it cannot hold up the
        // thing people actually opened the application for.
        Update = new UpdateViewModel(
            () => App.Updates,
            settings,
            saved =>
            {
                App.Settings = saved;
                saved.Save(paths.SettingsFile);
            });

        // The screen can requeue work but cannot run it; the orchestrator is held here.
        //
        // Each identity is enqueued with the route the user chose, rather than a blanket backlog
        // scan. The scan would pick the settings again — the one route already known to have
        // failed, since that is why the recording is being retried.
        Processing.ReprocessRequested += (_, request) =>
        {
            foreach (var id in request.Ids)
                orchestrator.EnqueueWith(id, request.AsrModelId, request.AnalyseOnly, request.LlmModel);
        };
        Search = new SearchViewModel(repository);

        Ask = new AskViewModel(App.HttpClient, repository, settings);
        Ask.OpenRequested += (_, target) => OpenCall(target.CallId, target.StartMs);

        Ledger.OpenRequested += (_, target) => OnUi(() => OpenContact(target.ContactId, target.CallId));
        Ledger.Notice += (_, message) => OnUi(() => Notice = message);
        Contacts.Notice += (_, message) => OnUi(() => Notice = message);
        Search.OpenRequested += (_, target) => OnUi(() => OpenContact(target.ContactId, target.CallId));

        orchestrator.StateChanged += (_, state) => OnUi(() => OnStateChanged(state));
        orchestrator.Notice += (_, message) => OnUi(() => Notice = message);
        orchestrator.CallFinished += (_, _) => OnUi(RefreshAll);

        // Straight through to the screen, on the UI thread. The worker reports several times a
        // second while transcribing, so this must not do anything expensive — it sets four fields.
        orchestrator.ProgressChanged += (_, p) =>
            OnUi(() => Processing.ReportProgress(p.CallId, p.Stage, p.Percent));

        orchestrator.CallProcessed += (_, _) => OnUi(() =>
        {
            Processing.ClearProgress();
            Processing.Refresh();
        AiStatus.Refresh();
        });
        orchestrator.LevelChanged += (_, levels) => OnUi(() => SetLevels(levels.Mic, levels.Far));

        RefreshAll();
    }

    public OverviewViewModel Overview { get; }
    public LedgerViewModel Ledger { get; }
    public ContactsViewModel Contacts { get; }
    public ProcessingViewModel Processing { get; }
    public AiStatusViewModel AiStatus { get; }
    public UpdateViewModel Update { get; }
    public SearchViewModel Search { get; }
    public AskViewModel Ask { get; }
    public HealthViewModel Health { get; }

    [ObservableProperty] private ShellPage _page = ShellPage.Overview;
    [ObservableProperty] private string _statusText = "İzleniyor";
    [ObservableProperty] private string _statusDetail = "Arama başlayınca otomatik kaydedilecek";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _notice;

    /// <summary>True when something is wrong and the status dot should say so.</summary>
    [ObservableProperty] private bool _hasProblem;

    /// <summary>Live loudness of each stream during a call, 0-1. Drives the two meters.</summary>
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private double _farLevel;

    /// <summary>A warning when one side has been silent for a while. Null when both are fine.</summary>
    [ObservableProperty] private string? _levelHint;

    /// <summary>Open ledger items across every contact. Shown as a badge on the navigation.</summary>
    [ObservableProperty] private int _openFlagCount;

    /// <summary>Ticks since a stream last carried sound, at ten per second.</summary>
    private int _micQuietTicks;
    private int _farQuietTicks;

    /// <summary>
    /// Roughly -40 dBFS. Below this there is nothing a listener would call sound: a live
    /// microphone still shows a noise floor well above it, whereas a broken capture path sits
    /// at or near zero.
    /// </summary>
    private const double AudibleLevel = 0.01;

    /// <summary>Fifteen seconds. Long enough that an ordinary pause in a conversation is not a warning.</summary>
    private const int QuietTicksBeforeWarning = 150;

    /// <summary>
    /// Updates the meters and decides whether one side has gone suspiciously quiet.
    ///
    /// The warning matters more than the bars. Somebody watching a moving meter draws the right
    /// conclusion immediately, but somebody who glances at the window once needs to be told —
    /// and a capture that produces silence is indistinguishable from a working one in every
    /// other respect until the transcript comes back empty.
    /// </summary>
    private void SetLevels(double mic, double far)
    {
        MicLevel = mic;
        FarLevel = far;

        _micQuietTicks = mic >= AudibleLevel ? 0 : _micQuietTicks + 1;
        _farQuietTicks = far >= AudibleLevel ? 0 : _farQuietTicks + 1;

        LevelHint = (_micQuietTicks > QuietTicksBeforeWarning, _farQuietTicks > QuietTicksBeforeWarning) switch
        {
            (true, true) => "İki akıştan da ses gelmiyor. Kayıt sessiz olacak.",
            (true, false) => "Mikrofondan ses gelmiyor. Senin söylediklerin kayda geçmiyor.",
            (false, true) => "Karşı taraftan ses gelmiyor. Çıkış cihazı değişmiş olabilir.",
            _ => null,
        };
    }

    private void OnStateChanged(OrchestratorState state)
    {
        IsRecording = state == OrchestratorState.Recording;
        IsBusy = state == OrchestratorState.Processing;
        HasProblem = false;

        if (!IsRecording)
        {
            // Reset rather than leave the last reading frozen on screen: a meter stuck at
            // half-height after a call reads as though something is still being recorded.
            MicLevel = FarLevel = 0;
            _micQuietTicks = _farQuietTicks = 0;
            LevelHint = null;
        }

        (StatusText, StatusDetail) = state switch
        {
            OrchestratorState.Ringing => ("Arama çalıyor", "Cevaplanırsa kayıt başlayacak"),
            OrchestratorState.Recording => ("Kaydediliyor", "İki ses akışı ayrı ayrı yazılıyor"),
            OrchestratorState.Processing => ("İşleniyor", "Yazıya dökülüyor ve çözümleniyor"),
            _ => ("İzleniyor", "Arama başlayınca otomatik kaydedilecek"),
        };

        if (state == OrchestratorState.Idle) RefreshAll();
    }

    partial void OnNoticeChanged(string? value)
    {
        // A notice about a failure should also change the status dot, so the problem is visible
        // even after the message bar is dismissed.
        if (value is not null && (value.Contains("başlatılamadı") || value.Contains("hata")))
            HasProblem = true;
    }

    /// <summary>Runs an action on the UI thread, or straight away if already there.</summary>
    private void OnUi(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        Page = Enum.TryParse<ShellPage>(page, out var parsed) ? parsed : ShellPage.Overview;

        // The contact filter has to reflect who exists now, not who existed when the window
        // opened. Refreshing on arrival is cheap and means a call labelled five minutes ago is
        // immediately filterable.
        if (Page == ShellPage.Search) Search.LoadContacts();

        // Same reason as Search: somebody labelled five minutes ago has to be selectable now.
        if (Page == ShellPage.Ask) Ask.LoadContacts();

        // Checked on arrival rather than on a timer: the answers involve reading the disk and
        // starting a Python process, which is not something to do every minute in the background
        // of a machine that is also on a call.
        if (Page == ShellPage.Health) _ = Health.RefreshAsync();
    }

    [RelayCommand]
    public void RefreshAll()
    {
        Overview.Refresh();
        Ledger.Refresh();
        Contacts.Refresh();
        Processing.Refresh();
        AiStatus.Refresh();

        // The badge counts what actually needs attention rather than everything in the ledger:
        // a badge that never reaches zero stops being read.
        OpenFlagCount = Ledger.OverdueCount + Ledger.FlagCount;
    }

    /// <summary>Opens a contact from anywhere — a search result, or an overview row.</summary>
    public void OpenContact(long contactId, long? callId = null)
    {
        Page = ShellPage.Contacts;
        Contacts.Select(contactId, callId);
    }

    /// <summary>
    /// Opens the conversation a quoted line came from, at the moment it was said.
    ///
    /// Reached from a citation on the Ask page, which knows the call but not the contact — so
    /// the contact is looked up here rather than carried through every layer that does not need
    /// it. An unattributed call has no contact page to open; landing on the ledger is better
    /// than doing nothing when a row was clicked.
    /// </summary>
    public void OpenCall(long callId, int startMs)
    {
        var contactId = Contacts.ContactIdOf(callId);

        if (contactId is null) return;

        OpenContact(contactId.Value, callId);
        Contacts.SeekTo(startMs);
    }

    /// <summary>True while a hand-started recording is running, which swaps the button.</summary>
    [ObservableProperty] private bool _isManualRecording;

    /// <summary>
    /// Starts recording without waiting for a call to be detected.
    ///
    /// Detection is good but not perfect, and a conversation that was not recorded cannot be
    /// recorded later. One button that always works is worth more than any amount of confidence
    /// in the heuristic.
    /// </summary>
    [RelayCommand]
    private async Task StartManualRecordingAsync()
    {
        try
        {
            await _orchestrator.StartManualRecordingAsync();
            IsManualRecording = _orchestrator.IsManualRecording;
        }
        catch (Exception e)
        {
            Notice = $"Kayıt başlatılamadı: {e.Message}";
            HasProblem = true;
        }
    }

    [RelayCommand]
    private async Task StopManualRecordingAsync()
    {
        try
        {
            await _orchestrator.StopManualRecordingAsync();
        }
        catch (Exception e)
        {
            Notice = $"Kayıt durdurulamadı: {e.Message}";
        }
        finally
        {
            IsManualRecording = _orchestrator.IsManualRecording;
            RefreshAll();
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    public void Dispose() => Contacts.Dispose();
}
