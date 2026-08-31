using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

public enum AttentionKind
{
    /// <summary>Blocks the application from working at all until it is dealt with.</summary>
    Blocking,

    /// <summary>Needs the user, but nothing is broken.</summary>
    Action,

    /// <summary>Worth knowing.</summary>
    Info,
}

public enum AttentionAction
{
    None,
    ShowUnlabelled,
    RetryFailed,
    OpenSettings,
}

public sealed record AttentionItem(
    AttentionKind Kind,
    string Title,
    string Detail,
    string? ActionLabel = null,
    AttentionAction Action = AttentionAction.None)
{
    public Wpf.Ui.Controls.SymbolRegular Icon => Kind switch
    {
        AttentionKind.Blocking => Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
        AttentionKind.Action => Wpf.Ui.Controls.SymbolRegular.Warning24,
        _ => Wpf.Ui.Controls.SymbolRegular.Info24,
    };

    /// <summary>
    /// Colour follows severity, never decoration.
    ///
    /// Three shades only, and each one means something: red is broken, amber wants a decision,
    /// blue is worth knowing. A palette that is prettier than this makes every notice look
    /// equally urgent, which is the same as none of them being urgent.
    /// </summary>
    public string BrushKey => Kind switch
    {
        AttentionKind.Blocking => "SystemFillColorCriticalBrush",
        AttentionKind.Action => "SystemFillColorCautionBrush",
        _ => "AccentTextFillColorPrimaryBrush",
    };

    public bool HasAction => ActionLabel is not null;
}

public sealed record RecentCall(Call Call, string ContactName)
{
    public string When => Call.StartedAt.ToLocalTime().ToString("d MMMM, HH:mm");

    /// <summary>Which application this came through, for the badge on the row.</summary>
    public string AppName => Call.App.ToString();

    public DateTimeOffset StartedAt => Call.StartedAt;

    public bool IsGroup => Call.Kind == CallKind.Group;
    public string Length => $"{(int)Call.Duration.TotalMinutes:00}:{Call.Duration.Seconds:00}";
    public bool NeedsLabel => Call.ContactId is null;

    /// <summary>
    /// What is happening to this recording, in plain language.
    ///
    /// States that are normal must not read like errors: a group call is deliberately not
    /// transcribed, and a queued call is waiting rather than stuck.
    /// </summary>
    public string Status => Call.Kind == CallKind.Group
        ? "Grup — sadece ses"
        : Call.State switch
        {
            ProcessingState.Recorded or ProcessingState.Queued => "Sırada",
            ProcessingState.Transcribing => "Yazıya dökülüyor",

            // Transcribed means the words are written and the analysis did not run — because no
            // model is configured, or because it was switched off. It is a resting state, not a
            // busy one, and calling it "Çözümleniyor" made a finished recording sit under a
            // spinner indefinitely.
            ProcessingState.Transcribed => "Yazıya döküldü · çözümlenmedi",

            ProcessingState.Analysing => "Çözümleniyor",
            ProcessingState.Analysed => "Hazır",
            ProcessingState.Failed => "Başarısız",
            ProcessingState.Skipped => "Atlandı",
            _ => "",
        };

    /// <summary>
    /// Whether something is actually happening to this recording right now.
    ///
    /// Narrower than it was, and the difference is visible on screen. Queued and Transcribed were
    /// both counted as working, so a list of recordings waiting their turn showed five spinners at
    /// once — which cannot be true, because processing is serialised behind a single semaphore so
    /// that Whisper and the analysis model never share the graphics card. Five things claiming to
    /// be in progress when at most one can be is not a cosmetic problem: it is the screen telling
    /// the user something they can reason their way to knowing is false.
    ///
    /// Queued now reads as waiting, which is what it is, and Transcribed as finished-for-now.
    /// </summary>
    public bool IsWorking => Call.State
        is ProcessingState.Transcribing or ProcessingState.Analysing;

    /// <summary>Waiting for its turn, with nothing happening to it yet.</summary>
    public bool IsWaiting => Call.State is ProcessingState.Recorded or ProcessingState.Queued;

    public bool IsFailed => Call.State == ProcessingState.Failed;
}

/// <summary>
/// The landing screen.
///
/// Deliberately not a list of contacts. Opening the application to a directory of names asks the
/// user to remember what they were looking for; opening it to what needs attention — recordings
/// nobody has named, promises that came due, anything that failed — answers that for them. The
/// archive is still one click away for when they do know.
/// </summary>
public sealed partial class OverviewViewModel(Repository repository, Func<AppSettings> settings, AppPaths paths) : ObservableObject
{
    /// <summary>Raised when a notice needs the shell to do something it cannot do itself.</summary>
    public event EventHandler<AttentionAction>? ActionRequested;

    /// <summary>Recordings nobody has named yet, for the labelling flow.</summary>
    public IReadOnlyList<Call> Unlabelled() => repository.UnlabelledCalls();

    /// <summary>
    /// Puts failed recordings back in the queue.
    ///
    /// Worth offering rather than making the user re-record: the audio is on disk and intact,
    /// and most failures here are a transient device or driver problem that a retry clears.
    /// </summary>
    public int RequeueFailed()
    {
        var failed = repository.FailedCalls(limit: 100);

        foreach (var call in failed)
            repository.SetCallState(call.Id, ProcessingState.Queued);

        Refresh();
        return failed.Count;
    }

    public ObservableCollection<AttentionItem> Attention { get; } = [];
    public ObservableCollection<RecentCall> Recent { get; } = [];
    public ObservableCollection<OverdueItem> Overdue { get; } = [];

    [ObservableProperty] private int _totalCalls;
    [ObservableProperty] private int _totalContacts;
    [ObservableProperty] private string _totalRecorded = "0 dk";
    [ObservableProperty] private int _pendingWork;

    /// <summary>Transcribed but never analysed. Not a backlog — see Repository.UnanalysedCount.</summary>
    [ObservableProperty] private int _unanalysed;

    /// <summary>
    /// What is on the board, in one line: "Bakılacak 3 · Bende 1".
    ///
    /// The board is the one part of this archive a person arranged themselves, so it is the part
    /// worth putting on the screen they see first. Empty until they put something there, and it
    /// says so rather than drawing four empty columns.
    /// </summary>
    [ObservableProperty] private string _boardSummary = "";

    public bool HasBoard => !string.IsNullOrWhiteSpace(BoardSummary);

    /// <summary>
    /// Cards whose reminder has come due.
    ///
    /// The only thing on this screen that asks for something back. Kept short and kept honest: a
    /// reminder somebody set themselves, on a conversation they chose, arriving on the day they
    /// asked for it. Nothing here is invented by the application, which is why it can be shown
    /// every day without becoming wallpaper.
    /// </summary>
    public ObservableCollection<DueCard> Due { get; } = [];

    public bool HasDue => Due.Count > 0;
    [ObservableProperty] private bool _hasAnyData;

    public sealed record OverdueItem(Commitment Commitment, string ContactName)
    {
        public int DaysLate =>
            DateOnly.FromDateTime(DateTime.Now).DayNumber - (Commitment.DeadlineDate?.DayNumber ?? 0);

        public string Line => $"{ContactName}: {Commitment.Obligation}";
        public string Quote => Commitment.Quote.Trim();
    }

    /// <summary>True when the worked example is in the archive.</summary>
    [ObservableProperty] private bool _hasSample;

    /// <summary>
    /// Loads a worked example so the product can be understood on the first day.
    ///
    /// The value of this application is not visible in any single conversation. It shows up
    /// three calls later, when a price has moved twice and a promise has come due — and asking
    /// somebody to record their calls for a month before they can see that is asking a lot. The
    /// example is written into the same tables as a real conversation and removes with the same
    /// delete, so it is a demonstration rather than a special mode.
    /// </summary>
    [RelayCommand]
    private void LoadSample()
    {
        Core.Storage.SampleData.Load(repository, paths);
        Refresh();
    }

    [RelayCommand]
    private void RemoveSample()
    {
        Core.Storage.SampleData.Remove(repository);
        Refresh();
    }

    [RelayCommand]
    private void RunAction(AttentionItem item) => ActionRequested?.Invoke(this, item.Action);

    [RelayCommand]
    public void Refresh()
    {
        HasSample = Core.Storage.SampleData.IsLoaded(repository);

        var (calls, contacts, recorded) = repository.Totals();

        TotalCalls = calls;
        TotalContacts = contacts;
        TotalRecorded = recorded.TotalHours >= 1
            ? $"{(int)recorded.TotalHours} sa {recorded.Minutes} dk"
            : $"{(int)recorded.TotalMinutes} dk";
        PendingWork = repository.PendingWorkCount();
        Unanalysed = repository.UnanalysedCount();

        LoadBoard();

        // Most serious first, which the code said it was doing and was not: the items were added
        // in the order they happened to be computed, so a blocking fault could sit below a
        // suggestion. AttentionKind is declared in severity order, so ordering by it is enough.
        var ordered = Attention.OrderBy(i => i.Kind).ToList();

        Attention.Clear();
        foreach (var item in ordered) Attention.Add(item);
        HasAnyData = calls > 0;

        Recent.Clear();
        foreach (var call in repository.ListCalls(limit: 12))
        {
            var name = call.ContactId is { } id ? repository.GetContact(id)?.Name : null;
            Recent.Add(new RecentCall(call, name ?? "İsimsiz"));
        }

        Overdue.Clear();
        foreach (var (commitment, name) in repository.OverdueCommitments(DateOnly.FromDateTime(DateTime.Now)))
            Overdue.Add(new OverdueItem(commitment, name));

        RebuildAttention();
    }

    /// <summary>
    /// Assembles the things worth interrupting the user about, most serious first.
    ///
    /// Kept short on purpose. A list of fifteen notices is a list nobody reads, and the whole
    /// point is that anything appearing here is worth acting on.
    /// </summary>
    private void RebuildAttention()
    {
        Attention.Clear();
        var current = settings();

        var unlabelled = repository.UnlabelledCalls();
        if (unlabelled.Count > 0)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Action,
                $"{unlabelled.Count} görüşme isimlendirilmemiş",
                "İsimsiz bir görüşme kişi geçmişinde görünmez. Kime ait olduğunu söylersen " +
                "defterine de işlenir.",
                "İsimlendir",
                AttentionAction.ShowUnlabelled));
        }

        var failed = repository.FailedCalls();
        if (failed.Count > 0)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Blocking,
                $"{failed.Count} görüşme işlenemedi",
                Core.Asr.FailureText.Summarise(failed[0].FailureReason),
                "Tekrar dene",
                AttentionAction.RetryFailed));
        }

        if (Overdue.Count > 0)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Action,
                $"{Overdue.Count} sözün tarihi geçti",
                string.Join(" · ", Overdue.Take(2).Select(o => o.Line))));
        }

        // Said on the main screen rather than buried in settings: in automatic mode a broken
        // driver silently turns into call audio being uploaded, and that should never be a
        // surprise.
        if (current.AudioMayLeaveTheMachine)
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Info,
                current.AsrMode == TranscriptionMode.CloudOnly
                    ? "Yazıya dökme buluta gönderiliyor"
                    : "Yerel çalışmazsa buluta gönderilecek",
                $"Ses {current.CloudAsrModel.DisplayName} servisine yükleniyor. " +
                "Bu, görüşme sesinin makineden çıkması demek."));
        }

        if (current.ExportToObsidian && string.IsNullOrWhiteSpace(current.ObsidianVaultPath))
        {
            Attention.Add(new AttentionItem(
                AttentionKind.Blocking,
                "Obsidian klasörü seçilmemiş",
                "Dışa aktarma açık ama nereye yazılacağı belli değil.",
                "Ayarlar",
                AttentionAction.OpenSettings));
        }
    }

    /// <summary>
    /// The board's state, and anything it says is due today.
    ///
    /// Both are cheap counts. Neither invents anything: the summary is what the user filed, and
    /// the due list is reminders they set themselves on conversations they chose. That is what
    /// makes this safe to show every single day — an application that starts with Windows and
    /// suggests things of its own accord becomes wallpaper within a week.
    /// </summary>
    private void LoadBoard()
    {
        var counts = repository.BoardCounts();

        BoardSummary = string.Join(" · ", BoardLane.All
            .Where(lane => lane != BoardLane.Done && counts.GetValueOrDefault(lane) > 0)
            .Select(lane => $"{BoardLane.NameOf(lane)} {counts[lane]}"));

        Due.Clear();

        foreach (var card in repository.DueCards())
        {
            var call = repository.GetCall(card.CallId);
            if (call is null) continue;

            var name = call.ContactId is { } id ? repository.GetContact(id)?.Name : null;

            Due.Add(new DueCard(
                card.CallId,
                string.IsNullOrWhiteSpace(card.Title)
                    ? $"{name ?? "İsimsiz"} · {call.StartedAt.ToLocalTime():d MMM}"
                    : card.Title!,
                BoardLane.NameOf(card.Lane),
                card.RemindOn!.Value));
        }

        OnPropertyChanged(nameof(HasBoard));
        OnPropertyChanged(nameof(HasDue));
    }
}

/// <summary>One reminder that has come due, as the first screen lists it.</summary>
public sealed record DueCard(long CallId, string Title, string Lane, DateOnly Day)
{
    public string When => Day == DateOnly.FromDateTime(DateTime.Now)
        ? "bugün"
        : Day < DateOnly.FromDateTime(DateTime.Now)
            ? $"{(DateOnly.FromDateTime(DateTime.Now).DayNumber - Day.DayNumber)} gün geçti"
            : Day.ToString("d MMM");
}
