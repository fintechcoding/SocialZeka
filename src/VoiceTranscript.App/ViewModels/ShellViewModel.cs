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

    /// <summary>
    /// The user's own speaking habits, counted, with the moments behind every figure.
    ///
    /// Its own band on the rail — KOÇLUK — because it is the only page about the user rather than
    /// about somebody they spoke to. Nothing on it describes the other party: the counters read
    /// the user's lines and no others.
    /// </summary>
    Mirror,

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
    /// Held for the two rail badges, which are now counted rather than harvested off two whole
    /// pages. See <see cref="RefreshBadges"/>.
    /// </summary>
    private readonly Repository _repository;

    /// <summary>
    /// Which pages still need re-reading. The rule and the reasons are in <see cref="PageRefresh"/>;
    /// this class supplies the one thing it cannot know, which is how to re-read a page.
    /// </summary>
    private readonly PageRefresh _refresh;

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
        _repository = repository;
        Health = health;

        Overview = new OverviewViewModel(repository, settings, paths);
        Calls = new CallsViewModel(repository);
        Ledger = new LedgerViewModel(repository);
        Calendar = new CalendarViewModel(repository);
        Todo = new TodoViewModel(repository, showDone: settings().TodoShowDone);
        Promises = new PromisesViewModel(repository);
        Mirror = new MirrorViewModel(repository);
        // The contact card's opt-in opinion panel is the one thing on that page that can spend
        // money and switch itself off, so the page is handed the same settings/save pair the
        // update screen uses rather than a second way of reaching them.
        Contacts = new ContactsViewModel(
            repository,
            new Services.ModelAccess(
                settings,
                saved =>
                {
                    App.Settings = saved;
                    saved.Save(paths.SettingsFile);
                },
                App.HttpClient));
        Processing = new ProcessingViewModel(repository, settings);
        // The status screen is told the route the recorder really takes, rather than assuming
        // local transcription works on this machine.
        AiStatus = new AiStatusViewModel(
            settings, App.HttpClient, repository, () => orchestrator.LocalTranscriptionUsable);

        // Built here, after the pages it re-reads and before anything that could ask it to. The
        // first thing that can is the RefreshAll at the end of this constructor.
        _refresh = new PageRefresh(Reload);

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
        Ledger.JourneyRequested += (_, contactId) => OnUi(() => OpenFigureJourney(contactId));
        Promises.OpenRequested += (_, target) => OnUi(() => OpenAt(target.ContactId, target.CallId, target.StartMs, target.IsMe));

        // A dot on the curve and a moment in the list are both "take me to where this was said".
        Mirror.OpenRequested += (_, target) => OnUi(() => OpenAt(target.ContactId, target.CallId, target.StartMs, target.IsMe));

        // Severity travels WITH the message from here on. Page notices are ordinary news;
        // everything the orchestrator says out loud is a heads-up ("X yanıt vermedi, Y
        // deneniyor", "alıntıların %40'ı bulunamadı") — that is what its Notice event is FOR.
        Contacts.Notice += (_, message) => OnUi(() => Post(message, Services.NoticeSeverity.Info));
        Search.OpenRequested += (_, target) => OnUi(() => OpenAt(target.ContactId, target.CallId, target.StartMs, target.IsMe));

        orchestrator.StateChanged += (_, state) => OnUi(() => OnStateChanged(state));
        orchestrator.Notice += (_, message) => OnUi(() => Post(message, Services.NoticeSeverity.Warning));
        orchestrator.CallFinished += (_, _) => OnUi(RefreshAll);
        Services.CallActions.Changed += (_, _) => OnUi(RefreshAll);

        // A ruling on a ledger row — dismissed, kept, brought back — is made on whichever screen
        // shows the row, and every other screen showing it re-reads the same way.
        Services.LedgerActions.Changed += (_, _) => OnUi(RefreshAll);

        // Straight through to the screen, on the UI thread. The worker reports several times a
        // second while transcribing, so this must not do anything expensive — it sets four fields.
        orchestrator.ProgressChanged += (_, p) => OnUi(() =>
        {
            Processing.ReportProgress(p.CallId, p.Stage, p.Percent, p.Engine);

            // Once per call, not per percent: the first screen's row moves from "Sırada" to
            // "Yazıya dökülüyor" when the worker picks it up, and that is all it needs.
            //
            // Touched rather than refreshed outright, because this fires while a transcription is
            // running and the first screen is usually not the one on the user's screen. Nothing
            // the recorder or the worker does waits on this: what is skipped is a redraw of a
            // hidden page, and that page is re-read the moment it is opened.
            if (p.CallId != _lastProgressCall)
            {
                _lastProgressCall = p.CallId;
                Touch(ShellPage.Overview);
            }
        });

        orchestrator.CallProcessed += (_, processed) => OnUi(() =>
        {
            // Unconditional, and not a page read: the progress bar is live state, and leaving it
            // frozen at 80% because Durum happened to be hidden would be a lie on the one screen
            // that answers "is it still working".
            Processing.ClearProgress();

            // Durum's two tabs and the first screen. Same reasoning as above: a call finishing
            // must change what these say, and it does — now, if the user is on them, and on
            // arrival if not. Previously all three were rebuilt whatever the user was looking at.
            Touch(ShellPage.Health);
            Touch(ShellPage.Overview);

            // The badges, though, are counted whatever page is showing. A finished analysis is
            // exactly when new findings and new promises appear, and both numbers are read from
            // the rail on every screen.
            RefreshBadges();

            // "Ne oldu?" — the end of processing told as one sentence, with the suggestion
            // count as a plain number. The summary itself already passed the pipeline's
            // verification; the toast adds no commentary of its own.
            if (processed.Succeeded)
            {
                var actions = repository.ActionsOf(processed.CallId, includeClosed: false).Count;

                // The three numbers, and the way to the rest of them.
                //
                // This toast is where the post-call report is actually delivered: it arrives
                // unprompted, minutes after the conversation, and it is the only moment the user
                // is thinking about the call they just had. Three counts and a click — the whole
                // report is on the window's Aynam tab, and nothing here is a judgement.
                var mirror = MirrorLine(repository, processed.CallId);

                Post(
                    $"{processed.ContactName} görüşmesi işlendi"
                    + (mirror is { Length: > 0 } line ? $" · {line}" : "")
                    + (actions > 0 ? $" · {actions} aksiyon önerildi" : "")
                    + (processed.Summary is { Length: > 0 } s
                        ? $" — {(s.Length <= 120 ? s : s[..117] + "…")}"
                        : "."),
                    Services.NoticeSeverity.Success,
                    () => Views.CallWindow.Show(
                        System.Windows.Application.Current?.MainWindow,
                        processed.CallId,
                        tab: Views.CallTab.Mirror));
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

    /// <summary>
    /// "sen %61 · küfür 3 · 2 açık söz" — the three numbers the post-call notice carries.
    ///
    /// Empty when the conversation has not been counted yet, which is the ordinary state until
    /// the counts are stored: a toast that said "sen %0" because nothing had been computed would
    /// be a wrong number rather than a missing one. Static and given its repository so the
    /// sentence can be checked without a window.
    /// </summary>
    public static string MirrorLine(Repository repository, long callId)
    {
        try
        {
            var parts = new List<string>();

            if (repository.GetHabits(callId) is { } stored
                && Core.Analysis.HabitSnapshot.FromJson(stored.Json) is { } snapshot)
            {
                if (snapshot.Talk.MyShare is { } share) parts.Add($"sen %{share * 100:0}");

                var swears = snapshot.Habits.CountOf(Core.Domain.HabitKind.Profanity).Certain;
                if (swears > 0) parts.Add($"küfür {swears}");
            }

            if (repository.GetCall(callId)?.ContactId is { } contactId)
            {
                var open = repository.PromiseLedger(contactId: contactId).Count;
                if (open > 0) parts.Add($"{open} açık söz");
            }

            return string.Join(" · ", parts);
        }
        catch (Exception e)
        {
            // A toast is not worth failing a completed call over.
            Services.AppLog.Error("aynam", e, "görüşme sonrası sayılar derlenemedi");
            return "";
        }
    }

    public OverviewViewModel Overview { get; }
    public CallsViewModel Calls { get; }
    public LedgerViewModel Ledger { get; }
    public CalendarViewModel Calendar { get; }
    public TodoViewModel Todo { get; }
    public PromisesViewModel Promises { get; }
    public MirrorViewModel Mirror { get; }
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
        ShellPage.Mirror => Localisation.T("mainwindow.aynam"),
        ShellPage.Search => Localisation.T("mainwindow.arama"),
        ShellPage.Ask => Localisation.T("mainwindow.sor"),
        ShellPage.Health => Localisation.T("mainwindow.durum"),
        _ => Core.Configuration.AppPaths.ApplicationName,
    };

    partial void OnPageChanged(ShellPage value)
    {
        OnPropertyChanged(nameof(WindowTitle));

        // Every arrival, whichever route it came by. See Arrive.
        Arrive(value);
    }

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

    /// <summary>
    /// What clicking the current notice does, or null when it does nothing.
    ///
    /// Travels ahead of the message exactly as the severity does, and for the same reason: the
    /// code that raised the notice is the only code that knows where it leads. Set before
    /// <see cref="Notice"/>, because the window reads it when the Notice change lands.
    /// </summary>
    public Action? NoticeAction { get; private set; }

    public bool HasUnseenNotices => UnseenNoticeCount > 0;

    private readonly Services.NoticeRepeatGuard _repeats = new();

    /// <summary>Raises one notice: the toast shows it, the history keeps it.</summary>
    /// <param name="onClick">Where the toast leads when it is clicked. Null for a notice that only says something.</param>
    public void Post(string message, Services.NoticeSeverity severity, Action? onClick = null)
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

        // Severity and destination travel ahead of the message: the snackbar factory reads both
        // when the Notice change lands.
        NoticeSeverity = severity;
        NoticeAction = onClick;
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
        var target = Enum.TryParse<ShellPage>(page, out var parsed) ? parsed : ShellPage.Overview;

        // Whether this is a move or a re-press of the page already open. The arriving is done by
        // OnPageChanged, and that does not fire when nothing changed — but pressing Görüşmeler
        // while on Görüşmeler has always meant "reload this", and still does.
        var alreadyHere = Page == target;

        Page = target;

        if (alreadyHere) Arrive(target);
    }

    // ---- the refresh layer --------------------------------------------------
    //
    // The rule and the reasons behind it live in PageRefresh, which can be built in a test where
    // this class cannot. What is left here is the half only the shell knows: which view model
    // answers for which page, and what a page needs on arrival beyond its own rows.

    /// <summary>
    /// Re-reads one page. The single place that knows which view model answers for which page, so
    /// the mark and the arrival cannot come to disagree about what "Defter" means.
    /// </summary>
    private void Reload(ShellPage page)
    {
        switch (page)
        {
            case ShellPage.Overview: Overview.Refresh(); break;
            case ShellPage.Calls: Calls.Refresh(); break;
            case ShellPage.Ledger: Ledger.Refresh(); break;
            case ShellPage.Calendar: Calendar.Refresh(); break;

            // The to-do page was once the one list RefreshAll did not re-read, so "Yaptım" on a
            // call window or the home screen left it showing the suggestion as still open.
            case ShellPage.Todo: Todo.Refresh(); break;

            case ShellPage.Promises: Promises.Refresh(); break;
            case ShellPage.Mirror: Mirror.Refresh(); break;
            case ShellPage.Contacts: Contacts.Refresh(); break;

            // Durum is one page with two tabs, and both are visible whenever it is — the nearest
            // thing in this window to "visible but not current". Health itself is not re-read
            // here: it reads the disk and starts a Python process, so it is asked on arrival only.
            case ShellPage.Health:
                Processing.Refresh();
                AiStatus.Refresh();
                break;
        }
    }

    /// <summary>One page has news. See <see cref="PageRefresh.Touch"/>.</summary>
    private void Touch(ShellPage page) => _refresh.Touch(page, Page);

    /// <summary>
    /// The user has landed on a page: everything that has to be true before they read it.
    ///
    /// Here rather than in <see cref="Navigate"/> because Navigate is not the only way in. The
    /// rail buttons, the command palette and the digit shortcuts go through it — but OpenContact,
    /// OpenFigureJourney, OpenAt, OpenCall and the two "Sözler sayfasında aç" buttons (the contact
    /// pane's and the contact window's) set Page directly, and so does the first screen's own
    /// "Göster". Wiring the arrival to the command would leave those routes showing yesterday,
    /// and a screen that is silently out of date is worse than a slow one.
    /// </summary>
    private void Arrive(ShellPage page)
    {
        // First, the lists that are not the page's own rows: who can be filtered by, what has
        // been asked before, and a health check that reads the disk. Unconditional, and before
        // the re-read — Aynam's contact list resets the selection, and its figures are read for
        // whoever is selected.
        switch (page)
        {
            // The contact filter has to reflect who exists now, not who existed when the window
            // opened, so that a call labelled five minutes ago is immediately filterable.
            case ShellPage.Search:
                Search.LoadContacts();
                break;

            // Same reason, plus the stored answers: each carries the name of whoever it was
            // narrowed to, and a contact renamed since would still be shown under the old
            // spelling. A database read, and no model is asked anything.
            case ShellPage.Ask:
                Ask.LoadContacts();
                Ask.LoadHistory();
                break;

            case ShellPage.Mirror:
                Mirror.LoadContacts();
                break;

            // Checked on arrival rather than on a timer: the answers involve reading the disk and
            // starting a Python process, which is not something to do every minute in the
            // background of a machine that is also on a call.
            case ShellPage.Health:
                _ = Health.RefreshAsync();
                break;
        }

        // And then the page's own rows, if the mark or the page's standing rule says so.
        _refresh.Arrive(page);
    }

    /// <summary>
    /// Something in the archive changed: re-read what the user is looking at, now, and mark every
    /// other page as needing it. See <see cref="PageRefresh"/> for what this used to cost.
    ///
    /// Still named for what it promises rather than for what it does in this instant, because the
    /// promise is still kept: every page WILL be re-read from the archive before it is next shown.
    /// What it no longer does is rebuild nine hidden screens while the user waits for the row
    /// under their finger to move. That is also why F5 can still be called "Her sayfayı yeniden
    /// yükle" without lying.
    ///
    /// Not an arrival: the extra loading in <see cref="Arrive"/> — contact lists, stored answers,
    /// the health check — is what a page needs when it is opened, and this is not an opening. The
    /// sweep never did it either.
    /// </summary>
    [RelayCommand]
    public void RefreshAll()
    {
        _refresh.Everything(Page);
        RefreshBadges();
    }

    /// <summary>
    /// The two numbers on the rail, counted rather than harvested.
    ///
    /// They used to be a by-product of the sweep: OpenFlagCount was <c>Ledger.FlagCount</c> and
    /// OverduePromiseCount was <c>Promises.OverdueCount</c>, which meant the only way to keep two
    /// small numbers honest was to rebuild both of those pages in full on every ruling made
    /// anywhere. That was a large part of what one tick cost.
    ///
    /// They cannot be allowed to go stale with the pages they came from, because they are read
    /// from every screen: a Defter badge still saying 6 after the sixth finding was refused is the
    /// rail lying about the one thing it is there to say. So they are two counting queries now,
    /// run on every change, whichever page the user happens to be on.
    ///
    /// Two badges, two questions. Defter counts the findings that want a look; Sözler counts the
    /// promises past their date. Each counts what actually needs attention rather than everything
    /// on its page: a badge that never reaches zero stops being read.
    /// </summary>
    private void RefreshBadges()
    {
        OpenFlagCount = _repository.OpenFlagCount();
        OverduePromiseCount = _repository.OverduePromiseCount(DateOnly.FromDateTime(DateTime.Today));
    }

    /// <summary>Opens a contact from anywhere — a search result, or an overview row.</summary>
    public void OpenContact(long contactId, long? callId = null)
    {
        Page = ShellPage.Contacts;
        Contacts.Select(contactId, callId);
    }

    /// <summary>
    /// [Yolculuk] on a changed-figure row: the person's card, at that figure's own history.
    ///
    /// Not merely the card — the section. The card is long, the journey sits near the bottom of
    /// it, and landing at the top of a page of counts after pressing a button named for one
    /// section of it is the kind of near-miss that reads as the button not working.
    /// </summary>
    public void OpenFigureJourney(long contactId)
    {
        OpenContact(contactId);
        Contacts.ShowFigureJourney();
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
