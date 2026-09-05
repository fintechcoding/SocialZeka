using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

public enum ShellPage
{
    Overview,

    /// <summary>Every call, by day, with filters — the list the first screen's twelve stood in for.</summary>
    Calls,

    /// <summary>
    /// What did not hold, across everybody.
    ///
    /// Second in the list rather than buried inside a contact, because this is the page the
    /// application exists for. Everything else is machinery in service of it.
    /// </summary>
    Ledger,

    /// <summary>The month view: reminders, both sides' promise deadlines, birthdays.</summary>
    Calendar,

    /// <summary>Everything the user has to do, from every source, in one list.</summary>
    Todo,

    /// <summary>Who promised what to whom, by when, and whether the user marked it kept — both directions.</summary>
    Promises,

    Contacts,

    // ShellPage.Processing was here, and removing it is the fix rather than a tidy-up.
    //
    // The processing list is a tab on the Durum page and has been for a long time; nothing in the
    // window was ever bound to a shell page of that name. So the value was a state the shell could
    // enter and no view would answer for — every visibility binding said "not me" and the content
    // area went blank. The first screen's "N görüşme işlenemedi · Göster" navigated there, and the
    // button that promised to show four failures showed an empty screen instead. Twice: the first
    // repair corrected which rows the list would hold without noticing that nobody could reach it.
    //
    // A value that cannot be rendered should not be expressible. See MainWindow.OnAttentionAction,
    // which now goes to Health and selects the tab.

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
        Calls = new CallsViewModel(repository);
        Ledger = new LedgerViewModel(repository);
        Calendar = new CalendarViewModel(repository);
        Todo = new TodoViewModel(repository, showDone: settings().TodoShowDone);
        Promises = new PromisesViewModel(repository);
        Contacts = new ContactsViewModel(repository);
        Processing = new ProcessingViewModel(repository, settings);
        // The status screen is told the route the recorder really takes, rather than assuming
        // local transcription works on this machine.
        AiStatus = new AiStatusViewModel(
            settings, App.HttpClient, repository, () => orchestrator.LocalTranscriptionUsable);

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

        Ledger.OpenRequested += (_, target) => OnUi(() => OpenAt(target.ContactId, target.CallId, target.StartMs, target.IsMe));
        Promises.OpenRequested += (_, target) => OnUi(() => OpenAt(target.ContactId, target.CallId, target.StartMs, target.IsMe));

        // Severity travels WITH the message from here on. Page notices are ordinary news;
        // everything the orchestrator says out loud is a heads-up ("X yanıt vermedi, Y
        // deneniyor", "alıntıların %40'ı bulunamadı") — that is what its Notice event is FOR.
        Ledger.Notice += (_, message) => OnUi(() => Post(message, Services.NoticeSeverity.Info));
        Contacts.Notice += (_, message) => OnUi(() => Post(message, Services.NoticeSeverity.Info));
        Search.OpenRequested += (_, target) => OnUi(() => OpenAt(target.ContactId, target.CallId, target.StartMs, target.IsMe));

        orchestrator.StateChanged += (_, state) => OnUi(() => OnStateChanged(state));
        orchestrator.Notice += (_, message) => OnUi(() => Post(message, Services.NoticeSeverity.Warning));
        orchestrator.CallFinished += (_, _) => OnUi(RefreshAll);
        Services.CallActions.Changed += (_, _) => OnUi(RefreshAll);

        // Straight through to the screen, on the UI thread. The worker reports several times a
        // second while transcribing, so this must not do anything expensive — it sets four fields.
        orchestrator.ProgressChanged += (_, p) => OnUi(() =>
        {
            Processing.ReportProgress(p.CallId, p.Stage, p.Percent, p.Engine);

            // Once per call, not per percent: the first screen's row moves from "Sırada" to
            // "Yazıya dökülüyor" when the worker picks it up, and that is all it needs.
            if (p.CallId != _lastProgressCall)
            {
                _lastProgressCall = p.CallId;
                Overview.Refresh();
            }
        });

        orchestrator.CallProcessed += (_, processed) => OnUi(() =>
        {
            Processing.ClearProgress();
            Processing.Refresh();
            AiStatus.Refresh();
            Overview.Refresh();

            // "Ne oldu?" — the end of processing told as one sentence, with the suggestion
            // count as a plain number. The summary itself already passed the pipeline's
            // verification; the toast adds no commentary of its own.
            if (processed.Succeeded)
            {
                var actions = repository.ActionsOf(processed.CallId, includeClosed: false).Count;

                Post(
                    $"{processed.ContactName} görüşmesi işlendi"
                    + (actions > 0 ? $" · {actions} aksiyon önerildi" : "")
                    + (processed.Summary is { Length: > 0 } s
                        ? $" — {(s.Length <= 120 ? s : s[..117] + "…")}"
                        : "."),
                    Services.NoticeSeverity.Success);
            }
            else if (processed.Failure is { } failure)
            {
                Post(
                    $"{processed.ContactName} görüşmesi işlenemedi: "
                    + Core.Asr.FailureText.Summarise(failure),
                    Services.NoticeSeverity.Error);
            }
        });
        orchestrator.LevelChanged += (_, levels) => OnUi(() => SetLevels(levels.Mic, levels.Far));

        RefreshAll();
    }

    private long _lastProgressCall = -1;

    public OverviewViewModel Overview { get; }
    public CallsViewModel Calls { get; }
    public LedgerViewModel Ledger { get; }
    public CalendarViewModel Calendar { get; }
    public TodoViewModel Todo { get; }
    public PromisesViewModel Promises { get; }
    public ContactsViewModel Contacts { get; }
    public ProcessingViewModel Processing { get; }
    public AiStatusViewModel AiStatus { get; }
    public UpdateViewModel Update { get; }
    public SearchViewModel Search { get; }
    public AskViewModel Ask { get; }
    public HealthViewModel Health { get; }

    [ObservableProperty] private ShellPage _page = ShellPage.Overview;

    /// <summary>
    /// What the taskbar and Alt-Tab say. "VoiceTranscript" alone told the user neither which page
    /// they left open nor whether a call was being recorded; both are things they look for there.
    /// </summary>
    public string WindowTitle => IsRecording
        ? $"Kaydediliyor — {Core.Configuration.AppPaths.ApplicationName}"
        : IsBusy
            ? $"İşleniyor — {Core.Configuration.AppPaths.ApplicationName}"
            : $"{PageName(Page)} — {Core.Configuration.AppPaths.ApplicationName}";

    /// <summary>The tray's hover text: the state and what it means, not just a word.</summary>
    public string TrayText => $"{StatusText} · {StatusDetail}";

    private static string PageName(ShellPage page) => page switch
    {
        ShellPage.Overview => Localisation.T("mainwindow.genel-bakis"),
        ShellPage.Calls => Localisation.T("mainwindow.gorusmeler"),
        ShellPage.Ledger => Localisation.T("mainwindow.defter"),
        ShellPage.Calendar => Localisation.T("mainwindow.takvim"),
        ShellPage.Todo => Localisation.T("mainwindow.yapilacaklar"),
        ShellPage.Promises => Localisation.T("mainwindow.sozler"),
        ShellPage.Contacts => Localisation.T("mainwindow.kisiler"),
        ShellPage.Search => Localisation.T("mainwindow.arama"),
        ShellPage.Ask => Localisation.T("mainwindow.sor"),
        ShellPage.Health => Localisation.T("mainwindow.durum"),
        _ => Core.Configuration.AppPaths.ApplicationName,
    };

    partial void OnPageChanged(ShellPage value) => OnPropertyChanged(nameof(WindowTitle));
    [ObservableProperty] private string _statusText = Localisation.T("mainwindow.izleniyor");
    [ObservableProperty] private string _statusDetail = Localisation.T("mainwindow.gorusme-baslayinca-otomatik-kaydedilecek");
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

    /// <summary>Findings needing attention across every contact. Shown as a badge on Defter.</summary>
    [ObservableProperty] private int _openFlagCount;

    /// <summary>Promises past their date, both directions. Shown as a badge on Sözler.</summary>
    [ObservableProperty] private int _overduePromiseCount;

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
            OrchestratorState.Ringing => (Localisation.T("mainwindow.gelen-cagri"), Localisation.T("mainwindow.cevaplanirsa-kayit-baslayacak")),
            OrchestratorState.Recording => (Localisation.T("mainwindow.kaydediliyor"), Localisation.T("mainwindow.iki-ses-akisi-ayri-ayri-yaziliyor")),
            OrchestratorState.Processing => (Localisation.T("mainwindow.isleniyor"), Localisation.T("mainwindow.yaziya-dokuluyor-ve-cozumleniyor")),
            _ => (Localisation.T("mainwindow.izleniyor"), Localisation.T("mainwindow.gorusme-baslayinca-otomatik-kaydedilecek")),
        };

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TrayText));

        // Idle and Processing both re-read: a row that says "Sırada" for hours while the queue
        // works through it was the first screen not being told anything until the very end.
        if (state is OrchestratorState.Idle or OrchestratorState.Processing) RefreshAll();
    }

    // ---- the keyboard layer -------------------------------------------------

    /// <summary>Raised when Ctrl+K asks for the palette; the window opens it (a VM cannot).</summary>
    public event EventHandler? PaletteRequested;

    public IRelayCommand OpenPaletteCommand => _openPalette ??= new RelayCommand(
        () => PaletteRequested?.Invoke(this, EventArgs.Empty));

    private IRelayCommand? _openPalette;

    /// <summary>Raised by Ctrl+? — the window shows the cheatsheet.</summary>
    public event EventHandler? ShortcutsRequested;

    public IRelayCommand ShowShortcutsCommand => _showShortcuts ??= new RelayCommand(
        () => ShortcutsRequested?.Invoke(this, EventArgs.Empty));

    private IRelayCommand? _showShortcuts;

    // ---- the notice layer ---------------------------------------------------
    //
    // Typed at the source. Severity used to be guessed downstream by substring-matching the
    // Turkish message text; now the code that knows what happened says how loud it is, the
    // history keeps the last fifty so a missed toast is recoverable, and the bell's badge
    // counts what arrived while nobody was looking.

    public System.Collections.ObjectModel.ObservableCollection<Services.Notice> NoticeHistory { get; } = [];

    [ObservableProperty] private Services.NoticeSeverity _noticeSeverity;
    [ObservableProperty] private int _unseenNoticeCount;

    public bool HasUnseenNotices => UnseenNoticeCount > 0;

    private readonly Services.NoticeRepeatGuard _repeats = new();

    /// <summary>Raises one notice: the toast shows it, the history keeps it.</summary>
    public void Post(string message, Services.NoticeSeverity severity)
    {
        // Said once per burst. An error still marks the session as having a problem, because that
        // flag is about the state of things rather than about whether this sentence is new.
        if (!_repeats.ShouldSay(message, DateTimeOffset.Now))
        {
            if (severity == Services.NoticeSeverity.Error) HasProblem = true;
            return;
        }

        NoticeHistory.Insert(0, new Services.Notice(severity, message, DateTimeOffset.Now));
        while (NoticeHistory.Count > 50) NoticeHistory.RemoveAt(NoticeHistory.Count - 1);

        UnseenNoticeCount++;
        OnPropertyChanged(nameof(HasUnseenNotices));

        // Severity travels ahead of the message: the snackbar factory reads it when the
        // Notice change lands.
        NoticeSeverity = severity;
        Notice = message;

        if (severity == Services.NoticeSeverity.Error) HasProblem = true;
    }

    /// <summary>The bell was opened; everything in it has now been seen.</summary>
    public void MarkNoticesSeen()
    {
        UnseenNoticeCount = 0;
        OnPropertyChanged(nameof(HasUnseenNotices));
    }

    /// <summary>
    /// The morning brief: one deterministic sentence at startup about what today already holds.
    ///
    /// No model anywhere near it — these are counts from the user's own reminders, promises and
    /// the suggestion list, and a brief that could hallucinate would poison the one message that
    /// greets the day. Quiet days stay quiet: nothing due means no notice at all.
    /// </summary>
    public void PostMorningBrief(Core.Storage.Repository repository)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            var reminders = repository.RemindersBetween(today, today).Count;
            var overdueMine = repository.OverdueCommitments(today).Count(o => o.Commitment.ByMe);
            var overdueTheirs = repository.OverdueCommitments(today).Count(o => !o.Commitment.ByMe);
            var actions = repository.OpenActions(today).Count;

            var parts = new List<string>();
            if (reminders > 0) parts.Add($"{reminders} hatırlatıcı bugün");
            if (overdueMine > 0) parts.Add($"SENİN {overdueMine} sözünün tarihi geçti");
            if (overdueTheirs > 0) parts.Add($"{overdueTheirs} sözün tarihi geçti");
            if (actions > 0) parts.Add($"{actions} açık aksiyon önerisi");

            if (parts.Count == 0) return;

            Post($"Günün brifi: {string.Join(" · ", parts)}.",
                overdueMine + overdueTheirs > 0
                    ? Services.NoticeSeverity.Warning
                    : Services.NoticeSeverity.Info);
        }
        catch (Exception e)
        {
            Services.AppLog.Error("brif", e, "sabah brifi derlenemedi");
        }
    }

    partial void OnNoticeChanged(string? value)
    {
        // Direct assignments (start/stop failures below) still pass through here; they are
        // errors by construction.
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

        // Re-read on arrival: a reminder set moments ago must already be on the month.
        if (Page == ShellPage.Calendar) Calendar.Refresh();

        // The archive re-reads on arrival too: it is the cheapest way to be current.
        if (Page == ShellPage.Calls) Calls.Refresh();

        // Same: a suggestion produced a minute ago belongs on the list now.
        if (Page == ShellPage.Todo) Todo.Refresh();

        // And a promise marked from a call window a moment ago.
        if (Page == ShellPage.Promises) Promises.Refresh();

        // Checked on arrival rather than on a timer: the answers involve reading the disk and
        // starting a Python process, which is not something to do every minute in the background
        // of a machine that is also on a call.
        if (Page == ShellPage.Health) _ = Health.RefreshAsync();
    }

    [RelayCommand]
    public void RefreshAll()
    {
        Overview.Refresh();
        Calls.Refresh();
        Ledger.Refresh();
        Calendar.Refresh();
        // The to-do page was the one list this did not re-read, so "Yaptım" on a call window or
        // the home screen left it showing the suggestion as still open.
        Todo.Refresh();
        Promises.Refresh();
        Contacts.Refresh();
        Processing.Refresh();
        AiStatus.Refresh();

        // Two badges, two questions. Defter counts the findings that want a look; Sözler counts
        // the promises past their date. Each counts what actually needs attention rather than
        // everything on its page: a badge that never reaches zero stops being read.
        OpenFlagCount = Ledger.FlagCount;
        OverduePromiseCount = Promises.OverdueCount;
    }

    /// <summary>Opens a contact from anywhere — a search result, or an overview row.</summary>
    public void OpenContact(long contactId, long? callId = null)
    {
        Page = ShellPage.Contacts;
        Contacts.Select(contactId, callId);
    }

    /// <summary>
    /// Opens a quoted line where it was said: the contact page seeking to the moment, or — for a
    /// call nobody has named yet — the call window itself, so the click is never dead.
    /// </summary>
    public void OpenAt(long? contactId, long callId, int startMs, bool isMe)
    {
        if (contactId is { } id)
        {
            OpenContact(id, callId);
            Contacts.SeekTo(startMs, isMe);
            return;
        }

        Views.CallWindow.Show(System.Windows.Application.Current?.MainWindow, callId, startMs, isMe);
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
            // Posted, not assigned. A direct write leaves NoticeSeverity at whatever the last
            // message set, so a recording failure could appear in the green of the "Tamamlandı"
            // that preceded it — the colour saying the opposite of the words.
            Post($"Kayıt başlatılamadı: {e.Message}", Services.NoticeSeverity.Error);
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
            Post($"Kayıt durdurulamadı: {e.Message}", Services.NoticeSeverity.Error);
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
