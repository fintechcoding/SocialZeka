using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;
using Wpf.Ui.Controls;

namespace VoiceTranscript.App.ViewModels;

/// <summary>Which slice of the ledger is showing.</summary>
public enum LedgerFilter
{
    Everything,
    Overdue,
    Promises,

    /// <summary>
    /// The user's OWN open promises. Their own words, on their own page — because people
    /// forget what they promised too, and an archive that only watches the other side is
    /// a grievance list, not a ledger. Overdue ones graduate to Overdue, badged SEN.
    /// </summary>
    MyPromises,

    Changes,
    Flags,
}

/// <summary>
/// One line of the ledger, whatever produced it.
///
/// Promises, changed figures and flags are different rows in the database but the same thing to
/// a person reading them: something that was said, by somebody, at a moment they can listen to.
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

    /// <summary>True when the words are the user's own — the row wears a SEN badge and the
    /// obligation reads as theirs to keep, not as a complaint about somebody.</summary>
    public bool ByMe { get; init; }

    public string Timestamp => $"{QuoteStartMs / 60000:00}:{QuoteStartMs / 1000 % 60:00}";

    public bool HasCounter => !string.IsNullOrWhiteSpace(CounterQuote);

    public bool HasCaveat => Caveat is not null;

    public bool IsLate => DaysLate > 0;

    public string LateText => DaysLate == 1
        ? Localisation.T("ledgerpage.1-gun-gecti")
        : string.Format(Localisation.T("ledgerpage.n-gun-gecti"), DaysLate);

    public SymbolRegular Icon => Kind switch
    {
        LedgerFilter.Overdue => SymbolRegular.Clock24,
        LedgerFilter.Promises => SymbolRegular.ClipboardTaskListLtr24,
        LedgerFilter.MyPromises => SymbolRegular.Person24,
        LedgerFilter.Changes => SymbolRegular.ArrowSwap24,
        _ => SymbolRegular.Flag24,
    };

    public string KindLabel => Kind switch
    {
        LedgerFilter.Overdue => Localisation.T("ledgerpage.tur-vadesi-gecti"),
        LedgerFilter.Promises => Localisation.T("ledgerpage.tur-soz"),
        LedgerFilter.MyPromises => Localisation.T("ledgerpage.tur-senin-sozun"),
        LedgerFilter.Changes => Localisation.T("ledgerpage.tur-degisti"),
        _ => Localisation.T("ledgerpage.tur-dikkat"),
    };
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
/// </summary>
public sealed partial class LedgerViewModel(Repository repository) : ObservableObject
{
    /// <summary>Raised when a row wants the shell to open a contact.</summary>
    public event EventHandler<(long? ContactId, long CallId, int StartMs, bool IsMe)>? OpenRequested;

    /// <summary>Raised when something needs saying to the user.</summary>
    public event EventHandler<string>? Notice;

    public ObservableCollection<LedgerEntry> Entries { get; } = [];

    [ObservableProperty] private LedgerFilter _filter = LedgerFilter.Everything;
    [ObservableProperty] private string _contactFilter = "";
    [ObservableProperty] private bool _isLoading;

    /// <summary>Counts for the filter chips, so the numbers are visible before clicking.</summary>
    [ObservableProperty] private int _overdueCount;
    [ObservableProperty] private int _promiseCount;
    [ObservableProperty] private int _myPromiseCount;
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private int _flagCount;

    public bool HasEntries => Entries.Count > 0;

    public bool HasAnything =>
        OverdueCount + PromiseCount + MyPromiseCount + ChangeCount + FlagCount > 0;

    partial void OnFilterChanged(LedgerFilter value) => Refresh();

    partial void OnContactFilterChanged(string value) => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        IsLoading = true;

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var all = new List<LedgerEntry>();

            all.AddRange(Commitments(today));
            all.AddRange(Changes());
            all.AddRange(Flags());

            OverdueCount = all.Count(e => e.Kind == LedgerFilter.Overdue);
            PromiseCount = all.Count(e => e.Kind == LedgerFilter.Promises);
            MyPromiseCount = all.Count(e => e.Kind == LedgerFilter.MyPromises);
            ChangeCount = all.Count(e => e.Kind == LedgerFilter.Changes);
            FlagCount = all.Count(e => e.Kind == LedgerFilter.Flags);

            var name = ContactFilter.Trim();

            var shown = all
                .Where(e => Filter == LedgerFilter.Everything || e.Kind == Filter)
                .Where(e => name.Length == 0
                            || Core.Text.TurkishText.NormalizeForSearch(e.ContactName)
                                .Contains(Core.Text.TurkishText.NormalizeForSearch(name), StringComparison.Ordinal))
                // Overdue first — and among the overdue, the user's OWN broken promises before
                // anybody else's: the page's job is catching what went wrong, and the wrong the
                // user can actually fix this minute is their own. Then by how late, then newest.
                .OrderByDescending(e => e.IsLate)
                .ThenByDescending(e => e.IsLate && e.ByMe)
                .ThenByDescending(e => e.DaysLate)
                .ThenByDescending(e => e.When);

            Entries.Clear();
            foreach (var entry in shown) Entries.Add(entry);

            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(HasAnything));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private IEnumerable<LedgerEntry> Commitments(DateOnly today)
    {
        foreach (var (commitment, contactName) in repository.AllOpenCommitments())
        {
            // Both sides now. The user's own promise is still not a grievance — it gets its own
            // chip, a SEN badge and obligation language — but hiding it entirely taught nothing:
            // people forget what THEY promised, and this page is where a forgotten promise
            // should be caught, not where it should be invisible.
            var late = commitment.DeadlineDate is { } due && due < today
                ? today.DayNumber - due.DayNumber
                : 0;

            var caveats = new List<string>();
            if (commitment.IsConditional) caveats.Add("koşullu");
            if (commitment.DeadlineDate is null && commitment.DeadlineRaw is { } raw)
                caveats.Add($"tarih net değil: {raw}");

            yield return new LedgerEntry
            {
                Kind = late > 0 ? LedgerFilter.Overdue
                    : commitment.ByMe ? LedgerFilter.MyPromises
                    : LedgerFilter.Promises,
                ByMe = commitment.ByMe,
                ContactName = contactName,
                ContactId = commitment.ContactId,
                CallId = commitment.CallId,
                Headline = commitment.Obligation,
                Quote = commitment.Quote.Trim(),
                QuoteStartMs = commitment.QuoteStartMs,
                When = DateTimeOffset.Now,
                DaysLate = late,
                Caveat = caveats.Count > 0 ? string.Join(", ", caveats) : null,
                SourceId = commitment.Id,
            };
        }
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
        {
            var caveats = new List<string>();
            if (flag.IsHeuristic) caveats.Add("kural tabanlı");
            if (flag.LowConfidence) caveats.Add("ses net değil");

            yield return new LedgerEntry
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
            };
        }
    }

    /// <summary>Opens the conversation this line came from, at the right moment.</summary>
    [RelayCommand]
    private void Open(LedgerEntry entry)
    {
        OpenRequested?.Invoke(this, (entry.ContactId, entry.CallId, entry.QuoteStartMs, entry.ByMe));
    }

    /// <summary>
    /// Silences a line without deleting the words behind it.
    ///
    /// Extraction is not perfect, and a wrong entry that cannot be dismissed accumulates until
    /// the page is noise and nobody reads it. The quote stays in the transcript; only the ledger
    /// line goes.
    /// </summary>
    [RelayCommand]
    private void Dismiss(LedgerEntry entry)
    {
        switch (entry.Kind)
        {
            // MyPromises belongs here too. Own promises are rows in the same commitment table
            // with the same SourceId, but they fell through to the default branch — so
            // dismissing one left it on screen and answered with a sentence about changed
            // figures, which has nothing to do with what was clicked.
            case LedgerFilter.Overdue or LedgerFilter.Promises or LedgerFilter.MyPromises:
                repository.DismissCommitment(entry.SourceId);
                break;

            case LedgerFilter.Flags:
                repository.DismissFlag(entry.SourceId);
                break;

            default:
                // A changed figure is derived from the claims rather than stored as its own row,
                // so there is nothing to mark. Saying so is better than a button that silently
                // does nothing.
                Notice?.Invoke(this, Localisation.T("ledgerpage.degisen-rakamlar-tek-tek-reddedilemez"));
                return;
        }

        Entries.Remove(entry);
        OnPropertyChanged(nameof(HasEntries));

        Notice?.Invoke(this, Localisation.T("ledgerpage.reddedildi-alinti-metinde-duruyor"));
    }

    /// <summary>Marks a promise as kept.</summary>
    [RelayCommand]
    private void Fulfil(LedgerEntry entry)
    {
        // Same table, same identifier — and "Tutuldu olarak işaretle" on your own promises was
        // simply dead: the guard returned before doing anything and said nothing either.
        if (entry.Kind is not (LedgerFilter.Overdue or LedgerFilter.Promises or LedgerFilter.MyPromises))
            return;

        repository.FulfilCommitment(entry.SourceId);

        Entries.Remove(entry);
        OnPropertyChanged(nameof(HasEntries));

        Notice?.Invoke(this, Localisation.T("ledgerpage.tutuldu-olarak-isaretlendi"));
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
            Notice?.Invoke(this, Localisation.T("ledgerpage.temizlenecek-bir-sey-yok"));
            return;
        }

        Refresh();

        Notice?.Invoke(this,
            string.Format(Localisation.T("ledgerpage.n-kayit-kaldirildi"), swept.Total, swept.Hollow, swept.Duplicates));
    }

    [RelayCommand]
    private void SetFilter(string filter)
        => Filter = Enum.TryParse<LedgerFilter>(filter, out var parsed) ? parsed : LedgerFilter.Everything;
}
