using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>Which slice of the promises is showing.</summary>
public enum PromiseFilter
{
    /// <summary>Open, both directions — the page's ordinary state.</summary>
    Open,
    Overdue,
    ThisWeek,
    Undated,
    Conditional,
    Kept,
    Dismissed,
    All,
}

/// <summary>
/// One promise as the Sözler page shows it: whose, what, by when, the words it rests on, and
/// what the user has done about it.
///
/// Every figure here is either a date arithmetic or a count of the user's own rulings. There is
/// no "kept" the machine decided — <see cref="HintText"/> is the closest it comes, and it is a
/// question.
/// </summary>
public sealed partial class PromiseCard : ObservableObject
{
    private readonly DateOnly _today;

    public PromiseCard(Repository.PromiseRow row, DateOnly today, int callsSince, Repository.FulfilmentHint? hint)
    {
        Commitment = row.Commitment;
        ContactName = row.ContactName;
        CallStartedAt = row.CallStartedAt;
        CallsSince = callsSince;
        Hint = hint;
        _today = today;
    }

    public Commitment Commitment { get; }
    public string ContactName { get; }
    public DateTimeOffset CallStartedAt { get; }

    /// <summary>Calls with this person after the deadline — the "was there a chance" figure.</summary>
    public int CallsSince { get; }

    public Repository.FulfilmentHint? Hint { get; }

    public long Id => Commitment.Id;
    public bool ByMe => Commitment.ByMe;
    public string Initial => ContactName.Length > 0 ? ContactName[..1].ToUpperInvariant() : "?";

    public string Obligation => Commitment.EffectiveObligation;
    public bool IsEdited => Commitment.IsEdited;
    public bool IsConditional => Commitment.IsConditional;

    public bool IsDismissed => Commitment.DismissedByUser;
    public bool IsKept => !IsDismissed && Commitment.Status == CommitmentStatus.Fulfilled;
    public bool IsAbandoned => !IsDismissed && Commitment.Status == CommitmentStatus.Abandoned;
    public bool IsOpen => !IsDismissed && Commitment.Status == CommitmentStatus.Open;

    public DateOnly? Deadline => Commitment.EffectiveDeadline;
    public bool IsUndated => Deadline is null;

    public int DaysLate => IsOpen && !IsConditional && Deadline is { } due && due < _today
        ? _today.DayNumber - due.DayNumber
        : 0;

    public bool IsOverdue => DaysLate > 0;

    public bool IsDueThisWeek => IsOpen && Deadline is { } due && due >= _today && due <= _today.AddDays(7);

    /// <summary>
    /// Past its date, spoken again since, and still open. Not "broken": the user decides that
    /// with "Tutulmadı". Not said at all when the two have not spoken since the date — there was
    /// no chance to keep it.
    /// </summary>
    public bool IsLeftOpen => IsOverdue && DaysLate >= 14 && CallsSince >= 1;

    public bool CanFulfil => IsOpen;
    public bool CanReopen => IsKept || IsAbandoned;
    public bool CanRestore => IsDismissed;
    public bool CanDismiss => !IsDismissed;
    public bool CanRemind => IsOpen && !ByMe;
    public bool CanPostpone => IsOpen;
    public bool HasUserDeadline => Commitment.UserDeadlineDate is not null;

    public string HeadText
    {
        get
        {
            if (IsDismissed) return Localisation.T("promisespage.reddedildi");
            if (IsKept) return string.Format(Localisation.T("promisespage.tutuldu-d"), Stamp(Commitment.FulfilledAt));
            if (IsAbandoned) return Localisation.T("promisespage.tutulmadi");
            if (IsOverdue)
            {
                var late = DaysLate == 1
                    ? Localisation.T("promisespage.1-gun-gecti")
                    : string.Format(Localisation.T("promisespage.n-gun-gecti"), DaysLate);

                return CallsSince > 0
                    ? $"{late} · {string.Format(Localisation.T("promisespage.n-gorusme-oldu"), CallsSince)}"
                    : late;
            }
            if (IsConditional) return Localisation.T("promisespage.kosullu");
            if (Deadline is { } due) return string.Format(Localisation.T("promisespage.vade-d"), Day(due));
            return Localisation.T("promisespage.tarihsiz");
        }
    }

    /// <summary>"vade: 1 Eyl (cuma) · söylendi 28 Ağu" — or what the words said when no date came out of them.</summary>
    public string DeadlineText
    {
        get
        {
            var said = string.Format(Localisation.T("promisespage.soylendi-d"), Day(DateOnly.FromDateTime(CallStartedAt.LocalDateTime)));

            if (Deadline is { } due)
            {
                var when = $"{Day(due)} ({due.ToDateTime(TimeOnly.MinValue):dddd})";
                var edited = HasUserDeadline ? $" · {Localisation.T("promisespage.senin-tarihin")}" : "";
                return $"{string.Format(Localisation.T("promisespage.vade-d"), when)}{edited} · {said}";
            }

            if (Commitment.DeadlineRaw is { Length: > 0 } raw)
                return $"{string.Format(Localisation.T("promisespage.tarih-net-degil"), raw)} · {said}";

            return said;
        }
    }

    public string Timestamp => $"{Commitment.QuoteStartMs / 60000:00}:{Commitment.QuoteStartMs / 1000 % 60:00}";
    public string Quote => Commitment.Quote.Trim();

    public string? HintText => Hint is { } hint && IsOpen
        ? string.Format(Localisation.T("promisespage.tutuldu-mu-onerisi"), Day(DateOnly.FromDateTime(hint.CallStartedAt.LocalDateTime)))
        : null;

    public bool HasHint => HintText is not null;

    public string LeftOpenText => Localisation.T("promisespage.acik-kaldi");

    // ---- postponing, inline --------------------------------------------------------------

    [ObservableProperty] private bool _isPostponing;
    [ObservableProperty] private DateTime? _postponeTo;

    private static string Day(DateOnly day) => day.ToDateTime(TimeOnly.MinValue).ToString("d MMM");

    private static string Stamp(DateTimeOffset? at) => at is { } when ? when.ToLocalTime().ToString("d MMM") : "";
}

/// <summary>
/// The Sözler page: who promised what to whom, by when, and whether the user marked it kept.
///
/// Both directions on one page, in two columns, because a ledger that only watches the other
/// side is a grievance list. The rows come from one query (<see cref="Repository.PromiseLedger"/>)
/// so this page, the calendar, the caller strip and the home screen cannot disagree; the verbs
/// are the user's, and each can be taken back for as long as the notice is on screen.
///
/// There is no kept-ratio anywhere here on purpose: "tutulan 4/9" would write the user's own
/// marking habits onto the other person. Three counts instead — kept, overdue, unmarked.
/// </summary>
public sealed partial class PromisesViewModel(Repository repository) : ObservableObject
{
    /// <summary>Raised when a card's ▸ wants its conversation opened at the moment; the shell does it.</summary>
    public event EventHandler<(long? ContactId, long CallId, int StartMs, bool IsMe)>? OpenRequested;

    /// <summary>Raised when "Hatırlat" is pressed; the page opens the reminder dialog (a VM cannot).</summary>
    public event EventHandler<PromiseCard>? RemindRequested;

    /// <summary>Raised when ✎ is pressed; the page opens the edit dialog (wording and date, the user's own).</summary>
    public event EventHandler<PromiseCard>? EditRequested;

    public ObservableCollection<PromiseCard> Mine { get; } = [];
    public ObservableCollection<PromiseCard> Theirs { get; } = [];

    [ObservableProperty] private PromiseFilter _filter = PromiseFilter.Open;
    [ObservableProperty] private string _personFilter = "";

    [ObservableProperty] private int _allCount;
    [ObservableProperty] private int _overdueCount;
    [ObservableProperty] private int _thisWeekCount;
    [ObservableProperty] private int _undatedCount;
    [ObservableProperty] private int _conditionalCount;
    [ObservableProperty] private int _keptCount;
    [ObservableProperty] private int _dismissedCount;

    [ObservableProperty] private string _mineTally = "";
    [ObservableProperty] private string _theirsTally = "";

    public bool HasMine => Mine.Count > 0;
    public bool HasTheirs => Theirs.Count > 0;
    public bool IsEmpty => !HasMine && !HasTheirs;

    public string MineHeader => string.Format(Localisation.T("promisespage.senin-verdiklerin-n"), Mine.Count);
    public string TheirsHeader => string.Format(Localisation.T("promisespage.sana-verilenler-n"), Theirs.Count);

    /// <summary>What just happened, and the way back — the same quiet pattern as the to-do page.</summary>
    [ObservableProperty] private string? _notice;

    private Action? _undo;

    public bool CanUndo => _undo is not null;

    partial void OnFilterChanged(PromiseFilter value) => Refresh();
    partial void OnPersonFilterChanged(string value) => Refresh();

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = repository.PromiseLedger(includeClosed: true);

        var cards = new List<PromiseCard>(rows.Count);

        foreach (var row in rows)
        {
            var c = row.Commitment;
            var open = !c.DismissedByUser && c.Status == CommitmentStatus.Open;

            // The two lookups that cost a query each are made only where they can change what
            // the card says: calls since the date for a promise past it, and a "tutuldu mu?"
            // line for an open, unconditional one.
            var callsSince = open && !c.IsConditional && c.EffectiveDeadline is { } due && due < today && c.ContactId is { } contactId
                ? repository.CountCallsSince(contactId, due)
                : 0;

            var hint = open && !c.IsConditional ? repository.SuggestFulfilment(c.Id) : null;

            cards.Add(new PromiseCard(row, today, callsSince, hint));
        }

        AllCount = cards.Count;
        OverdueCount = cards.Count(k => k.IsOverdue);
        ThisWeekCount = cards.Count(k => k.IsDueThisWeek);
        UndatedCount = cards.Count(k => k.IsOpen && k.IsUndated);
        ConditionalCount = cards.Count(k => k.IsOpen && k.IsConditional);
        KeptCount = cards.Count(k => k.IsKept);
        DismissedCount = cards.Count(k => k.IsDismissed);

        var person = TurkishText.NormalizeForSearch(PersonFilter.Trim());

        var shown = cards
            .Where(k => Filter switch
            {
                PromiseFilter.Open => k.IsOpen,
                PromiseFilter.Overdue => k.IsOverdue,
                PromiseFilter.ThisWeek => k.IsDueThisWeek,
                PromiseFilter.Undated => k.IsOpen && k.IsUndated,
                PromiseFilter.Conditional => k.IsOpen && k.IsConditional,
                PromiseFilter.Kept => k.IsKept,
                PromiseFilter.Dismissed => k.IsDismissed,
                _ => true,
            })
            .Where(k => person.Length == 0 || TurkishText.NormalizeForSearch(k.ContactName).Contains(person, StringComparison.Ordinal))
            // Overdue first and the most overdue on top; then by date, the dateless last; then
            // the newest conversation.
            .OrderByDescending(k => k.DaysLate)
            .ThenBy(k => k.Deadline ?? DateOnly.MaxValue)
            .ThenByDescending(k => k.CallStartedAt)
            .ToList();

        Mine.Clear();
        Theirs.Clear();

        foreach (var card in shown)
        {
            if (card.ByMe) Mine.Add(card);
            else Theirs.Add(card);
        }

        MineTally = Tally(cards.Where(k => k.ByMe));
        TheirsTally = Tally(cards.Where(k => !k.ByMe));

        OnPropertyChanged(nameof(HasMine));
        OnPropertyChanged(nameof(HasTheirs));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(MineHeader));
        OnPropertyChanged(nameof(TheirsHeader));
    }

    /// <summary>Kept · overdue · unmarked. Counts of rulings and dates; no ratio.</summary>
    private static string Tally(IEnumerable<PromiseCard> cards)
    {
        var list = cards.ToList();

        return string.Format(
            Localisation.T("promisespage.isaretledin"),
            list.Count(k => k.IsKept),
            list.Count(k => k.IsOverdue),
            list.Count(k => k.IsOpen && !k.IsOverdue));
    }

    [RelayCommand]
    private void SetFilter(string filter) =>
        Filter = Enum.TryParse<PromiseFilter>(filter, out var parsed) ? parsed : PromiseFilter.Open;

    // ---- the user's verbs --------------------------------------------------------------------

    [RelayCommand]
    private void Fulfil(PromiseCard? card)
    {
        if (card is null || !card.CanFulfil) return;

        repository.FulfilCommitment(card.Id);
        Done(string.Format(Localisation.T("promisespage.tutuldu-olarak-isaretlendi-n"), Shorten(card.Obligation)),
            () => repository.ReopenCommitment(card.Id));
    }

    [RelayCommand]
    private void Abandon(PromiseCard? card)
    {
        if (card is null || !card.CanFulfil) return;

        repository.AbandonCommitment(card.Id);
        Done(string.Format(Localisation.T("promisespage.tutulmadi-olarak-isaretlendi-n"), Shorten(card.Obligation)),
            () => repository.ReopenCommitment(card.Id));
    }

    [RelayCommand]
    private void Reopen(PromiseCard? card)
    {
        if (card is null || !card.CanReopen) return;

        var wasKept = card.IsKept;

        repository.ReopenCommitment(card.Id);
        Done(string.Format(Localisation.T("promisespage.yeniden-acildi-n"), Shorten(card.Obligation)),
            () => { if (wasKept) repository.FulfilCommitment(card.Id); else repository.AbandonCommitment(card.Id); });
    }

    [RelayCommand]
    private void Dismiss(PromiseCard? card)
    {
        if (card is null || !card.CanDismiss) return;

        repository.DismissCommitment(card.Id);
        Done(string.Format(Localisation.T("promisespage.reddedildi-n"), Shorten(card.Obligation)),
            () => repository.RestoreCommitment(card.Id));
    }

    [RelayCommand]
    private void Restore(PromiseCard? card)
    {
        if (card is null || !card.CanRestore) return;

        repository.RestoreCommitment(card.Id);
        Done(string.Format(Localisation.T("promisespage.geri-getirildi-n"), Shorten(card.Obligation)),
            () => repository.DismissCommitment(card.Id));
    }

    [RelayCommand]
    private void BeginPostpone(PromiseCard? card)
    {
        if (card is null || !card.CanPostpone) return;

        card.PostponeTo = (card.Deadline ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
        card.IsPostponing = true;
    }

    [RelayCommand]
    private void CancelPostpone(PromiseCard? card)
    {
        if (card is not null) card.IsPostponing = false;
    }

    [RelayCommand]
    private void ApplyPostpone(PromiseCard? card)
    {
        if (card is null || card.PostponeTo is not { } picked) return;

        var before = card.Commitment.UserDeadlineDate;
        var day = DateOnly.FromDateTime(picked);

        repository.SetUserDeadline(card.Id, day);
        Done(string.Format(Localisation.T("promisespage.ertelendi-n"), day.ToDateTime(TimeOnly.MinValue).ToString("d MMM")),
            () => repository.SetUserDeadline(card.Id, before));
    }

    /// <summary>Back to the spoken date. The machine's column was never touched; only the user's is cleared.</summary>
    [RelayCommand]
    private void ClearPostpone(PromiseCard? card)
    {
        if (card is null || !card.HasUserDeadline) return;

        var before = card.Commitment.UserDeadlineDate;

        repository.SetUserDeadline(card.Id, null);
        Done(Localisation.T("promisespage.soylenen-tarihe-donuldu"), () => repository.SetUserDeadline(card.Id, before));
    }

    [RelayCommand]
    private void Remind(PromiseCard? card)
    {
        if (card is not null) RemindRequested?.Invoke(this, card);
    }

    [RelayCommand]
    private void Edit(PromiseCard? card)
    {
        if (card is not null && card.IsOpen) EditRequested?.Invoke(this, card);
    }

    [RelayCommand]
    private void Open(PromiseCard? card)
    {
        if (card is null) return;
        OpenRequested?.Invoke(this, (card.Commitment.ContactId, card.Commitment.CallId, card.Commitment.QuoteStartMs, card.ByMe));
    }

    /// <summary>Opens the later line the "tutuldu mu?" offer points at, so the user can hear it before marking anything.</summary>
    [RelayCommand]
    private void OpenHint(PromiseCard? card)
    {
        if (card?.Hint is not { } hint) return;
        OpenRequested?.Invoke(this, (card.Commitment.ContactId, hint.CallId, hint.StartMs, hint.IsMe));
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo is null) return;

        var undo = _undo;
        _undo = null;
        Notice = null;

        undo();

        OnPropertyChanged(nameof(CanUndo));
        Refresh();
        Services.CallActions.NotifyChanged();
    }

    [RelayCommand]
    private void ClearNotice()
    {
        _undo = null;
        Notice = null;
        OnPropertyChanged(nameof(CanUndo));
    }

    private void Done(string notice, Action undo)
    {
        _undo = undo;
        Notice = notice;

        OnPropertyChanged(nameof(CanUndo));
        Refresh();

        // Every other list holding promises — the calendar, the home screen, the caller strip's
        // next appearance — learns of the ruling the way it learns of a deleted call.
        Services.CallActions.NotifyChanged();
    }

    private static string Shorten(string text) =>
        text.Length <= 46 ? text : text[..45].TrimEnd() + "…";
}
