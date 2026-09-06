using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;
using Wpf.Ui.Controls;

namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// Which slice of the ledger is showing.
///
/// Promises are not a slice any more: they have a page of their own (Sözler), in both
/// directions, with the verbs that belong to them. Two copies of that list disagreed on every
/// filter one of them did not have. What stays here is what went wrong — changed figures and
/// findings — and what the user turned down.
/// </summary>
public enum LedgerFilter
{
    Everything,
    Changes,
    Flags,

    /// <summary>
    /// What the user turned down. A dismissal is a tombstone, not a deletion — the same words
    /// must not be found again on the next run — so the rows are still there to be shown, and
    /// to be brought back when the ruling was a slip.
    /// </summary>
    Dismissed,
}

/// <summary>How the rows are ordered.</summary>
public enum LedgerSort
{
    /// <summary>Late first, own late before anybody else's, then newest.</summary>
    Date,

    /// <summary>By the person, Turkish alphabet; within a person, by date.</summary>
    Contact,

    /// <summary>Changed figures, then findings; within a kind, by date.</summary>
    Kind,
}

/// <summary>
/// Which machinery a row came from. Only findings carry a source; a promise or a changed figure
/// is the per-call analysis's work, so "Kural" keeps them and "Denetim" shows the consistency
/// check's findings alone.
/// </summary>
public enum LedgerSource
{
    All,
    Rule,
    Audit,
}

/// <summary>
/// One line of the ledger, whatever produced it.
///
/// Changed figures and flags are different rows in the database but the same thing to a person
/// reading them: something that was said, by somebody, at a moment they can listen to.
/// Presenting them as one list is what makes the screen readable; keeping the quote and the
/// timestamp on every single one is what makes it fair.
/// </summary>
public sealed partial class LedgerEntry : ObservableObject
{
    public required LedgerFilter Kind { get; init; }
    public required string ContactName { get; init; }
    public long? ContactId { get; init; }
    public long CallId { get; init; }

    /// <summary>What happened, in one line.</summary>
    public required string Headline { get; init; }

    /// <summary>The words it rests on. Never empty — a claim without a quote is an accusation.</summary>
    public required string Quote { get; init; }

    public int QuoteStartMs { get; init; }

    /// <summary>An earlier quote being contradicted, when there is one.</summary>
    public string? CounterQuote { get; init; }

    public int? CounterQuoteStartMs { get; init; }

    public DateTimeOffset When { get; init; }

    /// <summary>Days past the deadline. Zero when there is no deadline or it has not passed.</summary>
    public int DaysLate { get; init; }

    /// <summary>Extra note: "kural tabanlı", "ses net değil", "koşullu".</summary>
    public string? Caveat { get; init; }

    /// <summary>Row identity in its own table, for dismissing.</summary>
    public long SourceId { get; init; }

    /// <summary>True when the words are the user's own; the row says so.</summary>
    public bool ByMe { get; init; }

    /// <summary>The finding behind the row, when it is one. What the verbs act on.</summary>
    public Flag? Flag { get; init; }

    /// <summary>True on the Reddedilenler chip: the row is a tombstone and offers "Geri getir".</summary>
    public bool IsDismissed { get; init; }

    /// <summary>When the user last ruled on it. Null: never.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>Which machinery wrote it — <see cref="Flag.Sources"/>. Promises and figures are the pipeline's.</summary>
    public string Source { get; init; } = Core.Domain.Flag.Sources.Pipeline;

    /// <summary>Ticked in select mode.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>True while the page is in select mode and this row can be picked.</summary>
    [ObservableProperty] private bool _showSelector;

    public string Timestamp => $"{QuoteStartMs / 60000:00}:{QuoteStartMs / 1000 % 60:00}";

    public bool HasCounter => !string.IsNullOrWhiteSpace(CounterQuote);

    public bool HasCaveat => Caveat is not null;

    public bool IsLate => DaysLate > 0;

    /// <summary>
    /// A changed figure is computed from the claims rather than stored as its own row, so there
    /// is nothing to dismiss; every other row is a table row with a tombstone flag.
    /// </summary>
    public bool CanDismiss => !IsDismissed && Kind != LedgerFilter.Changes;

    public bool CanSelect => CanDismiss;

    /// <summary>
    /// The other half of that same rule: a changed figure has no Reddet, but it does have
    /// Yolculuk.
    ///
    /// The plan draws exactly this pair on the ledger's wireframe, and only the negative half of
    /// it was built — so the one row on the page that cannot be ruled on was also the one row
    /// with nothing to press, and the figure's own history sat on the contact card with no way
    /// in from here. Needs a person: the journey is that person's list of what the number was
    /// each time they named it, and an unattributed call has no such list.
    /// </summary>
    public bool CanShowJourney => Kind == LedgerFilter.Changes && ContactId is not null;

    public string LateText => DaysLate == 1
        ? Localisation.T("ledgerpage.1-gun-gecti")
        : string.Format(Localisation.T("ledgerpage.n-gun-gecti"), DaysLate);

    /// <summary>"reddedildi · 4 Eylül" on a tombstone row.</summary>
    public string DismissedText => DecidedAt is { } at
        ? string.Format(Localisation.T("ledgerpage.reddedildi-tarih"), $"{at.ToLocalTime():d MMMM}")
        : Localisation.T("ledgerpage.reddedildi-tarih-bilinmiyor");

    public SymbolRegular Icon => Kind switch
    {
        LedgerFilter.Changes => SymbolRegular.ArrowSwap24,
        _ => SymbolRegular.Flag24,
    };

    public string KindLabel => Kind switch
    {
        LedgerFilter.Changes => Localisation.T("ledgerpage.tur-degisti"),
        _ => Localisation.T("ledgerpage.tur-dikkat"),
    };

    /// <summary>What the row is in the database: (its kind, its id). Survives a refresh.</summary>
    internal (LedgerFilter Kind, long Id) Key => (Kind, SourceId);
}

/// <summary>
/// The ledger: what did not hold, across everybody.
///
/// This is the screen the application exists for. Everything else — the recording, the
/// transcription, the two separate streams — is machinery in service of somebody being able to
/// open one page and see that a price moved three times, that a promise came due eleven days
/// ago, and that four direct questions went unanswered. So it is a top-level page rather than a
/// tab inside a contact, which is where it used to be.
///
/// It is deliberately a list of facts with quotes attached, not a score. A language model cannot
/// tell whether somebody is lying, and a number claiming otherwise would be both wrong and
/// harmful to a real person. What it can do is find the words and put them side by side.
///
/// The verbs on a row are the user's rulings, and every one can be taken back: Reddet is a
/// tombstone, Geri getir lifts it, and "Geri al" on the notice card undoes whichever was last.
/// Promises are not here at all any more — they are kept, postponed and refused on the Sözler
/// page, in both directions; this page's job is what went wrong.
/// </summary>
public sealed partial class LedgerViewModel(Repository repository) : ObservableObject
{
    private static readonly StringComparer TurkishName =
        StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    /// <summary>Raised when a row wants the shell to open a contact.</summary>
    public event EventHandler<(long? ContactId, long CallId, int StartMs, bool IsMe)>? OpenRequested;

    /// <summary>Raised by [Yolculuk]: show this person's card at the figure journey.</summary>
    public event EventHandler<long>? JourneyRequested;

    public ObservableCollection<LedgerEntry> Entries { get; } = [];

    [ObservableProperty] private LedgerFilter _filter = LedgerFilter.Everything;
    [ObservableProperty] private LedgerSort _sort = LedgerSort.Date;
    [ObservableProperty] private LedgerSource _source = LedgerSource.All;
    [ObservableProperty] private string _contactFilter = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>Select mode: a checkbox on every row that can be dismissed, and one button for all of them.</summary>
    [ObservableProperty] private bool _isSelecting;

    /// <summary>
    /// What was just done, said in-page rather than as a toast, and undoable for as long as the
    /// line is on screen. Null when nothing is being said.
    /// </summary>
    [ObservableProperty] private string? _notice;

    private PendingUndo? _pending;

    /// <summary>Counts for the filter chips, so the numbers are visible before clicking.</summary>
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private int _flagCount;
    [ObservableProperty] private int _dismissedCount;

    public bool HasEntries => Entries.Count > 0;

    public bool HasAnything => ChangeCount + FlagCount > 0;

    /// <summary>True while the last ruling can still be taken back.</summary>
    public bool CanUndo => _pending is not null;

    public int SelectedCount => Entries.Count(e => e.IsSelected);

    public string DismissSelectedText =>
        string.Format(Localisation.T("ledgerpage.secilenleri-reddet-n"), SelectedCount);

    partial void OnFilterChanged(LedgerFilter value) => Refresh();

    partial void OnSortChanged(LedgerSort value) => Refresh();

    partial void OnSourceChanged(LedgerSource value) => Refresh();

    partial void OnContactFilterChanged(string value) => Refresh();

    partial void OnIsSelectingChanged(bool value)
    {
        foreach (var entry in Entries)
        {
            entry.ShowSelector = value && entry.CanSelect;
            if (!value) entry.IsSelected = false;
        }

        SelectionChanged();
    }

    [RelayCommand]
    public void Refresh()
    {
        IsLoading = true;

        try
        {
            var all = new List<LedgerEntry>();

            all.AddRange(Changes());
            all.AddRange(Flags());

            ChangeCount = all.Count(e => e.Kind == LedgerFilter.Changes);
            FlagCount = all.Count(e => e.Kind == LedgerFilter.Flags);

            // The tombstones are shown only on their own chip; their count is on the chip
            // regardless, because "how much did I turn down" is a fair question.
            var dismissed = Dismissed().ToList();
            DismissedCount = dismissed.Count;

            var name = ContactFilter.Trim();
            var folded = TurkishText.NormalizeForSearch(name);

            var pool = Filter == LedgerFilter.Dismissed
                ? dismissed
                : all.Where(e => Filter == LedgerFilter.Everything || e.Kind == Filter);

            var shown = pool
                .Where(e => name.Length == 0
                            || TurkishText.NormalizeForSearch(e.ContactName).Contains(folded, StringComparison.Ordinal))
                .Where(e => Source switch
                {
                    LedgerSource.Audit => e.Source == Flag.Sources.Consistency,
                    LedgerSource.Rule => e.Source != Flag.Sources.Consistency,
                    _ => true,
                });

            // A refresh must not throw away what the user has ticked: the shell re-reads every
            // page whenever a call finishes, and select mode would otherwise empty itself while
            // somebody was halfway through it.
            var selected = Entries.Where(e => e.IsSelected).Select(e => e.Key).ToHashSet();

            foreach (var entry in Entries) entry.PropertyChanged -= OnEntryChanged;
            Entries.Clear();

            foreach (var entry in Order(shown))
            {
                entry.ShowSelector = IsSelecting && entry.CanSelect;
                entry.IsSelected = selected.Contains(entry.Key);
                entry.PropertyChanged += OnEntryChanged;
                Entries.Add(entry);
            }

            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(HasAnything));
            SelectionChanged();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Date order is newest first. The other two orders group first — by person in the Turkish
    /// alphabet, or by kind with changed figures before findings — and keep newest-first inside
    /// each group.
    /// </summary>
    private IEnumerable<LedgerEntry> Order(IEnumerable<LedgerEntry> entries)
    {
        IOrderedEnumerable<LedgerEntry> grouped = Sort switch
        {
            LedgerSort.Contact => entries.OrderBy(e => e.ContactName, TurkishName),
            LedgerSort.Kind => entries.OrderBy(e => KindRank(e.Kind)),
            _ => entries.OrderBy(_ => 0),
        };

        return grouped.ThenByDescending(e => e.When);
    }

    private static int KindRank(LedgerFilter kind) => kind switch
    {
        LedgerFilter.Changes => 0,
        _ => 1,
    };

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LedgerEntry.IsSelected)) SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(DismissSelectedText));
        DismissSelectedCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<LedgerEntry> Changes()
    {
        foreach (var (contactName, contactId, subject, series) in repository.ChangedAmounts())
        {
            var first = series[0];
            var last = series[^1];

            // Stated as a sequence, not as a verdict. "Was 12,000 and is now 18,000" is a fact
            // the user can check by listening; "he raised the price on you" is not ours to say.
            var figures = string.Join(" → ", series
                .Select(c => c.NumericValue is { } v ? $"{v:N0} {c.Unit}".Trim() : c.Value)
                .Distinct());

            yield return new LedgerEntry
            {
                Kind = LedgerFilter.Changes,
                ContactName = contactName,
                ContactId = contactId,
                CallId = last.CallId,
                Headline = $"{subject}: {figures}",
                Quote = last.Quote.Trim(),
                QuoteStartMs = last.QuoteStartMs,
                CounterQuote = first.Quote.Trim(),
                CounterQuoteStartMs = first.QuoteStartMs,
                When = DateTimeOffset.Now,
                Caveat = series.Any(c => c.LowConfidence) ? "ses net değil" : null,
                SourceId = last.Id,
            };
        }
    }

    private IEnumerable<LedgerEntry> Flags()
    {
        foreach (var (flag, contactName) in repository.RecentFlags(limit: 200))
            yield return Entry(flag, contactName);
    }

    private static LedgerEntry Entry(Flag flag, string contactName, bool dismissed = false)
    {
        var caveats = new List<string>();
        if (flag.IsHeuristic) caveats.Add("kural tabanlı");
        if (flag.LowConfidence) caveats.Add("ses net değil");

        return new LedgerEntry
        {
            Kind = LedgerFilter.Flags,
            ContactName = contactName,
            ContactId = flag.ContactId,
            CallId = flag.CallId,
            Headline = flag.Summary,
            Quote = flag.Quote.Trim(),
            QuoteStartMs = flag.QuoteStartMs,
            CounterQuote = flag.CounterQuote?.Trim(),
            CounterQuoteStartMs = flag.CounterQuoteStartMs,
            When = flag.CreatedAt,
            Caveat = caveats.Count > 0 ? string.Join(", ", caveats) : null,
            SourceId = flag.Id,
            Flag = flag,
            IsDismissed = dismissed,
            DecidedAt = flag.DecidedAt,
            Source = flag.Source,
        };
    }

    /// <summary>The tombstones, newest ruling first. Dismissed promises are the Sözler page's.</summary>
    private IEnumerable<LedgerEntry> Dismissed()
    {
        foreach (var (flag, contactName) in repository.DismissedFlags())
            yield return Entry(flag, contactName, dismissed: true);
    }

    /// <summary>Opens the conversation this line came from, at the right moment.</summary>
    [RelayCommand]
    private void Open(LedgerEntry entry)
    {
        OpenRequested?.Invoke(this, (entry.ContactId, entry.CallId, entry.QuoteStartMs, entry.ByMe));
    }

    /// <summary>
    /// [Yolculuk]. Opens the person's card at the figure's own history.
    ///
    /// "15.000 → 18.000 → 20.000" on this page is a headline; the journey is every value with
    /// the date it was named and the second it can be heard at. The row that most needs that is
    /// the one that cannot be refused, and it was the only row here with no way to reach it.
    /// </summary>
    [RelayCommand]
    private void Journey(LedgerEntry entry)
    {
        if (!entry.CanShowJourney || entry.ContactId is not { } contactId) return;

        JourneyRequested?.Invoke(this, contactId);
    }

    /// <summary>
    /// Turns a line down without deleting the words behind it.
    ///
    /// Extraction is not perfect, and a wrong entry that cannot be dismissed accumulates until
    /// the page is noise and nobody reads it. The quote stays in the transcript; the row becomes
    /// a tombstone, listed under Reddedilenler, and the notice card offers the way back.
    /// </summary>
    [RelayCommand]
    private void Dismiss(LedgerEntry entry)
    {
        PendingUndo undo;

        if (entry.Flag is { } flag)
            undo = LedgerActions.Dismiss(repository, flag);
        else
        {
            // A changed figure is derived from the claims rather than stored as its own row,
            // so there is nothing to mark. Saying so is better than a button that silently
            // does nothing — and the button is not drawn on those rows.
            Say(Localisation.T("ledgerpage.degisen-rakamlar-tek-tek-reddedilemez"));
            return;
        }

        Entries.Remove(entry);
        OnPropertyChanged(nameof(HasEntries));

        Offer(undo);
    }

    /// <summary>Lifts a tombstone — the Reddedilenler chip's verb.</summary>
    [RelayCommand]
    private void Restore(LedgerEntry entry)
    {
        if (entry.Flag is not { } flag) return;

        var undo = LedgerActions.Restore(repository, flag);

        Entries.Remove(entry);
        OnPropertyChanged(nameof(HasEntries));

        Offer(undo);
    }

    private bool HasSelection => SelectedCount > 0;

    /// <summary>Every ticked row at once. One ruling, one "Geri al".</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DismissSelected()
    {
        var picked = Entries.Where(e => e.IsSelected && e.CanDismiss).ToList();
        if (picked.Count == 0) return;

        var undo = LedgerActions.DismissMany(
            repository,
            [],
            picked.Where(e => e.Flag is not null).Select(e => e.SourceId).ToList());

        IsSelecting = false;
        Refresh();

        Offer(undo);
    }

    /// <summary>Takes the last ruling back, whatever it was.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_pending is not { } pending) return;

        _pending = null;
        Notice = null;
        OnPropertyChanged(nameof(CanUndo));

        pending.Undo();
        Refresh();
    }

    [RelayCommand]
    private void ClearNotice()
    {
        _pending = null;
        Notice = null;
        OnPropertyChanged(nameof(CanUndo));
    }

    private void Offer(PendingUndo undo)
    {
        _pending = undo;
        Notice = undo.Sentence;
        OnPropertyChanged(nameof(CanUndo));
    }

    private void Say(string sentence)
    {
        _pending = null;
        Notice = sentence;
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>
    /// Clears out the entries that carry nothing, on demand.
    ///
    /// The same sweep the application runs once at startup, offered as a button because the
    /// person looking at a ledger full of repeated lines should not have to restart to be rid of
    /// them — and because a cleanup that only ever happens invisibly is one nobody can trust.
    ///
    /// Only two populations go: an entry with no obligation text, which is a promise the archive
    /// cannot state and can never close, and exact duplicates of another entry. A ruling the user
    /// made is never touched.
    /// </summary>
    [RelayCommand]
    private void Sweep()
    {
        var swept = repository.SweepLedger();

        if (swept.Total == 0)
        {
            Say(Localisation.T("ledgerpage.temizlenecek-bir-sey-yok"));
            return;
        }

        Refresh();
        LedgerActions.NotifyChanged();

        Say(string.Format(Localisation.T("ledgerpage.n-kayit-kaldirildi"), swept.Total, swept.Hollow, swept.Duplicates));
    }

    [RelayCommand]
    private void SetFilter(string filter)
        => Filter = Enum.TryParse<LedgerFilter>(filter, out var parsed) ? parsed : LedgerFilter.Everything;

    [RelayCommand]
    private void SetSort(string sort)
        => Sort = Enum.TryParse<LedgerSort>(sort, out var parsed) ? parsed : LedgerSort.Date;

    [RelayCommand]
    private void SetSource(string source)
        => Source = Enum.TryParse<LedgerSource>(source, out var parsed) ? parsed : LedgerSource.All;
}
