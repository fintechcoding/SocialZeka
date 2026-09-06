using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
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
/// One transcript line from around the moment a promise was said.
///
/// Raw transcript and nothing else: who spoke, when, and the words. No label, no reading, no
/// verdict on whether the promise is one — the page shows the conversation and the user rules.
/// </summary>
public sealed record PromiseLine(long? ContactId, long CallId, int StartMs, bool IsMe, string Text)
{
    /// <summary>Long lines are cut so the card cannot grow taller than the promise it is about.</summary>
    public const int MaxLength = 140;

    public string Timestamp => PromiseCard.Clock(StartMs);

    /// <summary>Who said it. Two words, because the transcript knows only which file the audio was in.</summary>
    public string Speaker => IsMe ? Localisation.T("promisespage.sen") : Localisation.T("promisespage.o");
}

/// <summary>
/// One promise as the Sözler page shows it: whose, what, by when, the words it rests on, the
/// lines around those words, and what the user has done about it.
///
/// Every figure here is either a date arithmetic or a count of the user's own rulings. There is
/// no "kept" the machine decided — <see cref="HintText"/> is the closest it comes, and it is a
/// question.
/// </summary>
public sealed partial class PromiseCard : ObservableObject
{
    private readonly DateOnly _today;

    public PromiseCard(
        Repository.PromiseRow row,
        DateOnly today,
        int callsSince,
        Repository.FulfilmentHint? hint,
        IReadOnlyList<PromiseLine> around,
        VerdictValue? judgement,
        bool keepsUndated)
    {
        Commitment = row.Commitment;
        ContactName = row.ContactName;
        CallStartedAt = row.CallStartedAt;
        CallsSince = callsSince;
        Hint = hint;
        Around = around;
        Judgement = judgement;
        KeepsUndated = keepsUndated;
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

    // ---- S1: the words around the words -------------------------------------------------------

    /// <summary>Two lines before and two after, from the same call. Empty when there are none.</summary>
    public IReadOnlyList<PromiseLine> Around { get; }

    public bool HasAround => Around.Count > 0;

    /// <summary>Folded away until asked for: the card is a list item, not a transcript.</summary>
    [ObservableProperty] private bool _isAroundOpen;

    // ---- S4: the user's ear on the moment -----------------------------------------------------

    /// <summary>What the user said this moment is, if they have listened and ruled.</summary>
    public VerdictValue? Judgement { get; }

    /// <summary>
    /// The user said the sentence is not a promise. The row leaves every count of promises and
    /// lives under Reddedilenler, where it can be brought back.
    /// </summary>
    public bool IsNotAPromise => Judgement == VerdictValue.NotThat;

    public bool IsJudgedCorrect => Judgement == VerdictValue.Correct;
    public bool IsMisheard => Judgement == VerdictValue.Misheard;
    public bool IsJudged => Judgement is not null;

    /// <summary>What the user's ruling says, for the badge under the card. Null while they have not given one.</summary>
    public string? JudgementText => Judgement switch
    {
        VerdictValue.Correct => Localisation.T("promisespage.dogru-dedin"),
        VerdictValue.Misheard => Localisation.T("promisespage.yanlis-duyulmus-dedin"),
        VerdictValue.NotThat => Localisation.T("promisespage.soz-degil-dedin"),
        _ => null,
    };

    // ---- what the row is ----------------------------------------------------------------------

    public bool IsDismissed => Commitment.DismissedByUser;

    /// <summary>Turned down, either as a row or as a reading of the moment. Out of every promise count.</summary>
    public bool IsRefused => IsDismissed || IsNotAPromise;

    public bool IsKept => !IsRefused && Commitment.Status == CommitmentStatus.Fulfilled;
    public bool IsAbandoned => !IsRefused && Commitment.Status == CommitmentStatus.Abandoned;
    public bool IsOpen => !IsRefused && Commitment.Status == CommitmentStatus.Open;

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

    public bool CanFulfil => IsOpen && !IsGrouped;
    public bool CanReopen => IsKept || IsAbandoned;
    public bool CanRestore => IsRefused;
    public bool CanDismiss => !IsRefused && !IsGrouped;
    public bool CanRemind => IsOpen && !ByMe && !IsGrouped;
    public bool CanPostpone => IsOpen && !IsGrouped;
    public bool CanEdit => IsOpen && !IsGrouped;
    public bool HasUserDeadline => Commitment.UserDeadlineDate is not null;

    /// <summary>The three ear buttons: only where there is still a promise to rule on.</summary>
    public bool CanJudge => !IsDismissed && !IsGrouped;

    // ---- S2: one sentence, two promises -------------------------------------------------------

    private IReadOnlyList<PromiseCard> _candidates = [];

    /// <summary>
    /// Every reading the pipeline drew from this one sentence, this card included, oldest row
    /// first. One entry — the ordinary case — means there is nothing to choose between.
    /// </summary>
    public IReadOnlyList<PromiseCard> Candidates => _candidates;

    public bool IsGrouped => _candidates.Count > 1;

    /// <summary>True on every member of a group except the one that carries the card.</summary>
    public bool IsFollower => IsGrouped && !ReferenceEquals(_candidates[0], this);

    /// <summary>Set on every member of a group by the page, once the rows are read.</summary>
    public void SetCandidates(IReadOnlyList<PromiseCard> members) => _candidates = members;

    /// <summary>
    /// The user answered this sentence's question by turning another reading of it down — so
    /// what stands here is their choice, not the machine's only offer. A badge, below the card.
    /// </summary>
    public bool IsChosen { get; private set; }

    public void MarkChosen() => IsChosen = true;

    public string ChosenText => Localisation.T("promisespage.senin-secimin");

    /// <summary>
    /// Whether anything under this card is the user's own writing rather than the machine's
    /// reading. The two grounds share a card only with a rule between them, so the badges live
    /// below a line and nothing above it moves when one appears.
    /// </summary>
    public bool HasUserMark => IsChosen || IsJudged;

    public string CandidateQuestion => Localisation.T("promisespage.bu-cumlede-hangisi");

    // ---- S3: "ne zamana?" ---------------------------------------------------------------------

    /// <summary>The user said the promise has no date and that is the answer. The strip stops asking.</summary>
    public bool KeepsUndated { get; }

    /// <summary>
    /// The strip goes under every open, undated card. A conditional promise is excluded: its
    /// date is not missing, it is waiting on something, and asking "ne zamana?" would be asking
    /// the wrong question.
    /// </summary>
    public bool NeedsDeadline => IsOpen && IsUndated && !IsConditional && !KeepsUndated && !IsGrouped;

    public bool IsKeptUndated => IsOpen && IsUndated && KeepsUndated;

    public string UndatedText => Localisation.T("promisespage.tarihsiz-kalsin-dedin");

    // ---- what the head says -------------------------------------------------------------------

    public string HeadText
    {
        get
        {
            if (IsNotAPromise) return Localisation.T("promisespage.soz-degil-dedin");
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

    public string Timestamp => Clock(Commitment.QuoteStartMs);
    public string Quote => Commitment.Quote.Trim();

    public string? HintText => Hint is { } hint && IsOpen
        ? string.Format(Localisation.T("promisespage.tutuldu-mu-onerisi"), Day(DateOnly.FromDateTime(hint.CallStartedAt.LocalDateTime)))
        : null;

    public bool HasHint => HintText is not null;

    public string LeftOpenText => Localisation.T("promisespage.acik-kaldi");

    // ---- postponing, inline --------------------------------------------------------------

    [ObservableProperty] private bool _isPostponing;
    [ObservableProperty] private DateTime? _postponeTo;

    /// <summary>mm:ss, the one place this page turns a millisecond into a time.</summary>
    internal static string Clock(int ms) => $"{ms / 60000:00}:{ms / 1000 % 60:00}";

    private static string Day(DateOnly day) => day.ToDateTime(TimeOnly.MinValue).ToString("d MMM");

    private static string Stamp(DateTimeOffset? at) => at is { } when ? when.ToLocalTime().ToString("d MMM") : "";
}

/// <summary>
/// The Sözler page: who promised what to whom, by when, and whether the user marked it kept.
///
/// Both directions on one page, in two columns, because a ledger that only watches the other
/// side is a grievance list. The rows come from one query (<see cref="Repository.PromiseLedger"/>)
/// so this page, the calendar, the caller strip and the home screen cannot disagree; the verbs
/// are the user's, they all go through <see cref="LedgerActions"/>, and each can be taken back
/// for as long as the notice is on screen.
///
/// There is no kept-ratio anywhere here on purpose: "tutulan 4/9" would write the user's own
/// marking habits onto the other person. Three counts instead — kept, overdue, unmarked — and,
/// under each column, the number of conversations they were drawn from, so the counts are read
/// against a denominator rather than as a verdict on a person.
/// </summary>
public sealed partial class PromisesViewModel(Repository repository) : ObservableObject
{
    /// <summary>
    /// The other side's column has to be this many times the user's own, and this many rows
    /// clear of it, before the page says the difference may be the extraction's.
    ///
    /// Two conditions rather than one because both failure modes are real at this size. A ratio
    /// alone fires on 0-against-2, which is a coin-flip run and not a shape. A gap alone fires on
    /// 40-against-43, which is nothing at all. Twice-and-three-clear is the smallest rule that
    /// catches today's archive — three of the user's own promises against ten of the other
    /// side's — and stays quiet at parity, which is the only calibration point that exists.
    /// </summary>
    private const int AsymmetryFactor = 2;

    private const int AsymmetryGap = 3;

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

    /// <summary>Every row the ledger still calls a promise — the "Hepsi" chip.</summary>
    [ObservableProperty] private int _allCount;

    /// <summary>Only the open ones — the "Açık" chip, which used to show the total of everything.</summary>
    [ObservableProperty] private int _openCount;

    [ObservableProperty] private int _overdueCount;
    [ObservableProperty] private int _thisWeekCount;
    [ObservableProperty] private int _undatedCount;
    [ObservableProperty] private int _conditionalCount;
    [ObservableProperty] private int _keptCount;
    [ObservableProperty] private int _dismissedCount;

    [ObservableProperty] private string _mineTally = "";
    [ObservableProperty] private string _theirsTally = "";

    /// <summary>"Bu sütun 52 görüşmeden çıkarıldı." — the denominator, under each column.</summary>
    [ObservableProperty] private string _sourceLine = "";

    /// <summary>The sentence that says a lopsided pair of columns may be the extraction's doing. Null while it is not.</summary>
    [ObservableProperty] private string? _asymmetryNote;

    public bool HasAsymmetryNote => AsymmetryNote is not null;

    public bool HasMine => Mine.Count > 0;
    public bool HasTheirs => Theirs.Count > 0;
    public bool IsEmpty => !HasMine && !HasTheirs;

    public string MineHeader => string.Format(Localisation.T("promisespage.senin-verdiklerin-n"), Mine.Count);
    public string TheirsHeader => string.Format(Localisation.T("promisespage.sana-verilenler-n"), Theirs.Count);

    /// <summary>What just happened, and the way back — the same quiet pattern as the to-do page.</summary>
    [ObservableProperty] private string? _notice;

    private PendingUndo? _pending;

    public bool CanUndo => _pending is not null;

    partial void OnFilterChanged(PromiseFilter value) => Refresh();
    partial void OnPersonFilterChanged(string value) => Refresh();
    partial void OnAsymmetryNoteChanged(string? value) => OnPropertyChanged(nameof(HasAsymmetryNote));

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = repository.PromiseLedger(includeClosed: true);

        // The user's rulings on the moments, one query per conversation that holds a promise.
        // Read before the cards are built, because "bu söz değil" changes what a row IS rather
        // than how it is drawn.
        var rulings = rows
            .Select(r => r.Commitment.CallId)
            .Distinct()
            .ToDictionary(id => id, id => repository.Verdicts(id));

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

            var around = repository
                .SegmentsAround(c.CallId, c.QuoteStartMs)
                .Select(s => new PromiseLine(c.ContactId, s.CallId, s.StartMs, s.IsMe, Clip(s.Text)))
                .ToList();

            var folded = TurkishText.NormalizeForSearch(c.Quote);
            var given = rulings[c.CallId];

            cards.Add(new PromiseCard(
                row, today, callsSince, hint, around,
                Ruling(given, VerdictKind.Promise, folded, c.QuoteStartMs),
                Ruling(given, VerdictKind.PromiseDeadline, folded, c.QuoteStartMs) is not null));
        }

        Group(cards);

        // "Hepsi" is every row the ledger still calls a promise; a moment the user said was not
        // one is gone from this number as from all the others, and is reached under its own chip.
        var live = cards.Where(k => !k.IsNotAPromise).ToList();

        AllCount = live.Count;
        OpenCount = live.Count(k => k.IsOpen);
        OverdueCount = live.Count(k => k.IsOverdue);
        ThisWeekCount = live.Count(k => k.IsDueThisWeek);
        UndatedCount = live.Count(k => k.IsOpen && k.IsUndated);
        ConditionalCount = live.Count(k => k.IsOpen && k.IsConditional);
        KeptCount = live.Count(k => k.IsKept);
        DismissedCount = cards.Count(k => k.IsRefused);

        var person = TurkishText.NormalizeForSearch(PersonFilter.Trim());

        var shown = cards
            // A group is one card; its other members are inside it, not beside it.
            .Where(k => !k.IsFollower)
            .Where(k => Filter switch
            {
                PromiseFilter.Open => k.IsOpen,
                PromiseFilter.Overdue => k.IsOverdue,
                PromiseFilter.ThisWeek => k.IsDueThisWeek,
                PromiseFilter.Undated => k.IsOpen && k.IsUndated,
                PromiseFilter.Conditional => k.IsOpen && k.IsConditional,
                PromiseFilter.Kept => k.IsKept,
                PromiseFilter.Dismissed => k.IsRefused,
                _ => !k.IsNotAPromise,
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

        MineTally = Tally(live.Where(k => k.ByMe));
        TheirsTally = Tally(live.Where(k => !k.ByMe));

        BuildHonestyLines(live);

        OnPropertyChanged(nameof(HasMine));
        OnPropertyChanged(nameof(HasTheirs));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(MineHeader));
        OnPropertyChanged(nameof(TheirsHeader));
    }

    /// <summary>The user's ruling of one kind on one moment, matched by the folded words and the millisecond.</summary>
    private static VerdictValue? Ruling(IReadOnlyList<Verdict> given, string kind, string folded, int startMs)
    {
        foreach (var verdict in given)
        {
            if (verdict.Kind == kind && verdict.QuoteFolded == folded && verdict.StartMs == startMs)
                return verdict.Value;
        }

        return null;
    }

    /// <summary>
    /// S2 — one sentence, two promises.
    ///
    /// <c>QuoteVerifier.Locate</c> hands back the whole segment when it finds a quote inside one,
    /// and the pipeline's de-duplication keys on (whose, obligation, quote) — so two readings of
    /// one sentence both survive and land on the page as two promises the user never made twice.
    /// Grouping them is a view decision and nothing else: no row is written, no text is changed,
    /// and the counts under the columns still count rows.
    ///
    /// Rows already turned down are left out of the grouping, which is what makes a picked
    /// candidate stand alone again on the next refresh. A group whose moment the user has
    /// confirmed ("ikisi de kalsın") is left alone too: the question has an answer.
    /// </summary>
    private static void Group(IReadOnlyList<PromiseCard> cards)
    {
        var standing = cards.Where(k => !k.IsRefused).ToList();

        foreach (var group in standing.GroupBy(Moment))
        {
            var members = group.OrderBy(k => k.Id).ToList();
            if (members.Count < 2) continue;
            if (members.Any(k => k.IsJudgedCorrect)) continue;

            foreach (var member in members) member.SetCandidates(members);
        }

        // And the other half of the same fact: a row that stands where a sibling reading was
        // turned down is the user's choice, not the machine's only offer. Derived rather than
        // recorded — the tombstone beside it already says it.
        var refused = cards.Where(k => k.IsRefused).Select(Moment).ToHashSet();

        foreach (var card in standing)
        {
            if (refused.Contains(Moment(card))) card.MarkChosen();
        }
    }

    /// <summary>The sentence a promise was drawn from: one call, one side, one millisecond, one wording.</summary>
    private static (long CallId, bool ByMe, int StartMs, string Folded) Moment(PromiseCard card) =>
        (card.Commitment.CallId, card.ByMe, card.Commitment.QuoteStartMs,
         TurkishText.NormalizeForSearch(card.Commitment.Quote));

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

    /// <summary>
    /// Where the columns came from, and what a lopsided pair of them might mean.
    ///
    /// The denominator is every conversation in the archive rather than every analysed one, on
    /// purpose: it is the number the user can count for themselves on the Görüşmeler screen. A
    /// figure only this page could produce would not do the job this sentence exists for.
    ///
    /// The second sentence is the more important one. Three of the user's own promises against
    /// ten of the other side's reads as a person who keeps their word talking to people who do
    /// not, and most of that shape is the extraction's: a promise in one's own speech is hedged,
    /// half-said and interrupted, and the model finds fewer of them. Saying so is the only thing
    /// on the page that defends the other person.
    /// </summary>
    private void BuildHonestyLines(IReadOnlyList<PromiseCard> live)
    {
        SourceLine = string.Format(
            Localisation.T("promisespage.bu-sutun-n-gorusmeden"), repository.Totals().Calls);

        var mine = live.Count(k => k.ByMe && !k.IsDismissed);
        var theirs = live.Count(k => !k.ByMe && !k.IsDismissed);

        AsymmetryNote = theirs >= mine * AsymmetryFactor + AsymmetryGap
            ? Localisation.T("promisespage.fark-cikarimdan-da-olabilir")
            : null;
    }

    /// <summary>A transcript line, cut so one long sentence cannot make the card taller than the page.</summary>
    private static string Clip(string text)
    {
        var flat = text.Trim();
        return flat.Length <= PromiseLine.MaxLength ? flat : flat[..(PromiseLine.MaxLength - 1)].TrimEnd() + "…";
    }

    [RelayCommand]
    private void SetFilter(string filter) =>
        Filter = Enum.TryParse<PromiseFilter>(filter, out var parsed) ? parsed : PromiseFilter.Open;

    // ---- the user's verbs --------------------------------------------------------------------

    [RelayCommand]
    private void Fulfil(PromiseCard? card)
    {
        if (card is null || !card.CanFulfil) return;

        // Which conversation closed it, when the page has an idea: the "tutuldu mu?" line is the
        // only thing on the page that points at one, and without it fulfilled_by_call_id was
        // never written by any path in the product.
        Offer(LedgerActions.Fulfil(repository, card.Commitment, card.Hint?.CallId));
    }

    [RelayCommand]
    private void Abandon(PromiseCard? card)
    {
        if (card is null || !card.CanFulfil) return;

        Offer(LedgerActions.Abandon(repository, card.Commitment));
    }

    [RelayCommand]
    private void Reopen(PromiseCard? card)
    {
        if (card is null || !card.CanReopen) return;

        Offer(LedgerActions.Reopen(repository, card.Commitment));
    }

    [RelayCommand]
    private void Dismiss(PromiseCard? card)
    {
        if (card is null || !card.CanDismiss) return;

        Offer(LedgerActions.Dismiss(repository, card.Commitment));
    }

    [RelayCommand]
    private void Restore(PromiseCard? card)
    {
        if (card is null || !card.CanRestore) return;

        // A refusal is either a tombstone on the row or a ruling on the moment; one button lifts
        // whichever one is standing.
        Offer(card.IsNotAPromise
            ? LedgerActions.ClearPromiseJudgement(repository, card.Commitment)
            : LedgerActions.Restore(repository, card.Commitment));
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

        Offer(LedgerActions.SetUserDeadline(repository, card.Commitment, DateOnly.FromDateTime(picked)));
    }

    /// <summary>Back to the spoken date. The machine's column was never touched; only the user's is cleared.</summary>
    [RelayCommand]
    private void ClearPostpone(PromiseCard? card)
    {
        if (card is null || !card.HasUserDeadline) return;

        Offer(LedgerActions.SetUserDeadline(repository, card.Commitment, null));
    }

    // ---- S3: "ne zamana?" ---------------------------------------------------------------------

    /// <summary>
    /// The four dates the strip offers, and the answer that is not a date.
    ///
    /// Every one of them writes <c>user_deadline_date</c> and nothing else: what the words said
    /// stays in <c>deadline_date</c>, because the consistency check reads that column to see
    /// whether the OTHER person moved a deadline, and a date the user typed must never be held
    /// against them.
    /// </summary>
    [RelayCommand]
    private void DeadlineThisWeek(PromiseCard? card) => SetDeadline(card, EndOfWeek(Today));

    [RelayCommand]
    private void DeadlineNextWeek(PromiseCard? card) => SetDeadline(card, EndOfWeek(Today).AddDays(7));

    [RelayCommand]
    private void DeadlineThisMonth(PromiseCard? card) => SetDeadline(card, EndOfMonth(Today));

    /// <summary>
    /// "Tarihsiz kalsın" — the point of the strip. Twelve of the archive's thirteen promises have
    /// no date, and until now that was a hole in the page rather than something the user could
    /// answer. Recorded as a verdict on the moment, so the strip stops asking.
    /// </summary>
    [RelayCommand]
    private void KeepUndated(PromiseCard? card)
    {
        if (card is null || !card.NeedsDeadline) return;

        Offer(LedgerActions.KeepUndated(repository, card.Commitment));
    }

    /// <summary>Takes "tarihsiz kalsın" back: the strip asks again.</summary>
    [RelayCommand]
    private void AskAgainForDeadline(PromiseCard? card)
    {
        if (card is null || !card.KeepsUndated) return;

        Offer(LedgerActions.ClearPromiseJudgement(repository, card.Commitment, VerdictKind.PromiseDeadline));
    }

    private void SetDeadline(PromiseCard? card, DateOnly day)
    {
        if (card is null || !card.CanPostpone) return;

        Offer(LedgerActions.SetUserDeadline(repository, card.Commitment, day));
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>The coming Sunday, or today when today is one. "Bu hafta" means before the week is out.</summary>
    private static DateOnly EndOfWeek(DateOnly today) =>
        today.AddDays(((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7);

    private static DateOnly EndOfMonth(DateOnly today) =>
        new(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

    // ---- S4: the user's ear on a promise ------------------------------------------------------

    /// <summary>The words are that promise. On a grouped card this is also "ikisi de kalsın".</summary>
    [RelayCommand]
    private void JudgeCorrect(PromiseCard? card) => Judge(card, VerdictValue.Correct);

    /// <summary>The transcript misheard it: those words were not said.</summary>
    [RelayCommand]
    private void JudgeMisheard(PromiseCard? card) => Judge(card, VerdictValue.Misheard);

    /// <summary>The words were said, but they are not a promise. The row leaves every promise count.</summary>
    [RelayCommand]
    private void JudgeNotAPromise(PromiseCard? card) => Judge(card, VerdictValue.NotThat);

    private void Judge(PromiseCard? card, VerdictValue value)
    {
        if (card is null || card.IsDismissed) return;

        Offer(LedgerActions.JudgePromise(repository, card.Commitment, value));
    }

    // ---- S2: one sentence, two promises -------------------------------------------------------

    /// <summary>
    /// "Bu cümlede gerçekten verdiğin söz hangisi?" — the answer. The other readings of the
    /// sentence become tombstones, one "Geri al" brings all of them back, and nothing is written
    /// to the one that stands.
    /// </summary>
    [RelayCommand]
    private void PickCandidate(PromiseCard? card)
    {
        if (card is null || !card.IsGrouped) return;

        Offer(LedgerActions.PickCommitment(
            repository, card.Commitment, [.. card.Candidates.Select(k => k.Commitment)]));
    }

    /// <summary>The sentence really did carry both. Recorded, so the question is not asked again.</summary>
    [RelayCommand]
    private void KeepAllCandidates(PromiseCard? card)
    {
        if (card is null || !card.IsGrouped) return;

        Offer(LedgerActions.KeepAllCandidates(repository, card.Commitment));
    }

    // ---- listening -----------------------------------------------------------------------------

    [RelayCommand]
    private void Remind(PromiseCard? card)
    {
        if (card is not null) RemindRequested?.Invoke(this, card);
    }

    [RelayCommand]
    private void Edit(PromiseCard? card)
    {
        if (card is not null && card.CanEdit) EditRequested?.Invoke(this, card);
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

    /// <summary>▸ on a line from around the promise: the conversation, at the second it was said.</summary>
    [RelayCommand]
    private void OpenLine(PromiseLine? line)
    {
        if (line is null) return;
        OpenRequested?.Invoke(this, (line.ContactId, line.CallId, line.StartMs, line.IsMe));
    }

    /// <summary>Shows or folds away the two lines either side of the promise.</summary>
    [RelayCommand]
    private void ToggleAround(PromiseCard? card)
    {
        if (card is not null) card.IsAroundOpen = !card.IsAroundOpen;
    }

    // ---- the notice ----------------------------------------------------------------------------

    [RelayCommand]
    private void Undo()
    {
        if (_pending is not { } pending) return;

        _pending = null;
        Notice = null;

        pending.Undo();

        OnPropertyChanged(nameof(CanUndo));
        Refresh();
        CallActions.NotifyChanged();
    }

    [RelayCommand]
    private void ClearNotice()
    {
        _pending = null;
        Notice = null;
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>
    /// Shows what a verb did and keeps its inverse ready. Every verb on this page hands one of
    /// these back — including the edit dialog's, whose undo used to be dropped on the floor and
    /// left ✎ as the only ruling here that could not be taken back.
    /// </summary>
    public void Offer(PendingUndo undo)
    {
        _pending = undo;
        Notice = undo.Sentence;

        OnPropertyChanged(nameof(CanUndo));
        Refresh();

        // Every other list holding promises — the calendar, the home screen, the caller strip's
        // next appearance — learns of the ruling the way it learns of a deleted call.
        CallActions.NotifyChanged();
    }
}
