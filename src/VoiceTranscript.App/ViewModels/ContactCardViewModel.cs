using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>How far back the "Gidişat" sparklines are drawn. The two compared numbers beside them
/// are always <see cref="ContactTrend.WindowMonths"/> against the same again, and the caption says so.</summary>
public enum CardPeriod
{
    Months3,
    Months6,
    Months12,
    All,
}

/// <summary>
/// One metric of "Gidişat": a line over the months, two numbers, and the denominator that says
/// how much of the history actually produced them.
///
/// No colour and no adjective. The arrow is a glyph, not a judgement — which direction is the
/// good one is the reader's to decide, and a product that answered it would be scoring a person.
/// </summary>
/// <param name="Denominator">"N/M görüşmede ölçüldü", or null where every call fed the figure.</param>
/// <param name="Detail">The counted split behind the figure, where there is one ("↓12 ↑7 ?3").</param>
public sealed record TrendRow(
    ContactMetric Metric,
    string Label,
    string Recent,
    string Previous,
    string Arrow,
    string? Denominator,
    string? Detail,
    PointCollection Points)
{
    public bool HasDenominator => Denominator is not null;
    public bool HasDetail => Detail is not null;

    /// <summary>A line needs two points; one measured month is a dot nobody can read.</summary>
    public bool HasLine => Points.Count >= 2;
}

/// <summary>One quote behind a "Kalıplar" row: the words, the moment, and the user's verbs.</summary>
public sealed partial class PatternQuoteRow(Repository.PatternQuote quote, bool isModelLabel) : ObservableObject
{
    public Repository.PatternQuote Quote { get; } = quote;

    /// <summary>True when the row came from <c>tactic_evidence</c> — a model's label, badged as one.</summary>
    public bool IsModelLabel { get; } = isModelLabel;

    public long Id => Quote.Id;
    public long CallId => Quote.CallId;
    public int StartMs => Quote.StartMs;

    /// <summary>A ledger flag never recorded which stream it came from; false is the safe side to play.</summary>
    public bool IsMe => Quote.ByMe ?? false;

    public string Text => Quote.Quote.Trim();

    public bool LowConfidence => Quote.LowConfidence;

    public string When =>
        $"{Quote.CallStartedAt.ToLocalTime():d MMM HH:mm} · {Quote.StartMs / 60000:00}:{Quote.StartMs / 1000 % 60:00}";

    /// <summary>What the user last said about this sentence by ear, or null when nobody listened.</summary>
    [ObservableProperty] private string? _verdictText;
}

/// <summary>
/// One counted pattern: a kind, who counted it, how often, and the quotes underneath.
///
/// The bar is the one thing that can be taken away. Its rules are the plan's: only rows the
/// transcriber was sure of feed it, and a kind the user has turned down more than three times in
/// ten loses it entirely while every quote stays on screen. A count nobody believes is not
/// evidence; the sentences still are.
/// </summary>
public sealed partial class PatternRow : ObservableObject
{
    public PatternRow(Repository.PatternSummary summary, bool isModelLabel, string label, int widest)
    {
        Summary = summary;
        IsModelLabel = isModelLabel;
        Label = label;

        // Only the rows the transcriber was sure of are allowed to size the bar; the uncertain
        // ones are listed in grey underneath and say so.
        Confident = summary.Total - summary.LowConfidence;

        var ruled = summary.Total + summary.Dismissed;
        DismissalRate = ruled == 0 ? 0 : (double)summary.Dismissed / ruled;

        HasBar = DismissalRate <= DismissalCeiling && Confident > 0;
        BarWidth = HasBar && widest > 0 ? 120.0 * Confident / widest : 0;
    }

    /// <summary>Above this share of dismissals the kind loses its bar (§4.4). The quotes remain.</summary>
    public const double DismissalCeiling = 0.30;

    public Repository.PatternSummary Summary { get; }

    /// <summary>True for a <c>tactic_evidence</c> row: a model's label, which wears a badge and
    /// lives under its own source filter. Never added into a ledger count.</summary>
    public bool IsModelLabel { get; }

    public string Label { get; }
    public string Kind => Summary.Kind;
    public string Source => Summary.Source;

    /// <summary>Counted rows the transcriber did not doubt — the only ones the bar draws.</summary>
    public int Confident { get; }

    public double DismissalRate { get; }

    /// <summary>False once the user has turned down more than 30% of this kind.</summary>
    public bool HasBar { get; }

    public double BarWidth { get; }

    public string CountText => Summary.Total.ToString();

    public string CallsText => string.Format(Localisation.T("contactcard.n-gorusmede"), Summary.Calls);

    public string ListenedText =>
        string.Format(Localisation.T("contactcard.n-k-dinlendi"), Summary.Listened, Summary.Total);

    public string LastText => Summary.Last is { } last
        ? string.Format(Localisation.T("contactcard.son-d"), last.ToLocalTime().ToString("d MMM HH:mm"))
        : "";

    public bool HasLast => Summary.Last is not null;

    public bool HasLowConfidence => Summary.LowConfidence > 0;

    public string LowConfidenceText =>
        string.Format(Localisation.T("contactcard.n-kayit-ses-net-degil"), Summary.LowConfidence);

    /// <summary>Said only where it applies: which numbers were dropped, and why the bar went.</summary>
    public string? BarDroppedText => HasBar || Summary.Dismissed == 0
        ? null
        : string.Format(
            Localisation.T("contactcard.ret-orani-yuzde-30-ustu"), Summary.Dismissed, Summary.Total + Summary.Dismissed);

    public bool HasBarDropped => BarDroppedText is not null;

    [ObservableProperty] private bool _isExpanded;

    public ObservableCollection<PatternQuoteRow> Quotes { get; } = [];
}

/// <summary>One value a figure has held, and the moment it was said.</summary>
public sealed record JourneyStop(Repository.FigureStop Stop, bool IsLast)
{
    public string Value => Stop.Value.Trim();
    public string When => Stop.CallStartedAt.ToLocalTime().ToString("d MMM");
    public long CallId => Stop.CallId;
    public int StartMs => Stop.StartMs;
    public bool LowConfidence => Stop.LowConfidence;

    /// <summary>The arrow between two stops; nothing after the last one.</summary>
    public string Separator => IsLast ? "" : "→";
}

/// <summary>One subject whose stated value moved. Values and dates; no conclusion.</summary>
public sealed record JourneyRow(Repository.FigureJourneyRow Journey, IReadOnlyList<JourneyStop> Stops)
{
    public string Subject => $"{Journey.Entity.Trim()} · {Journey.Attribute.Trim()}";

    public string DistinctText =>
        string.Format(Localisation.T("contactcard.n-farkli-deger"), Journey.DistinctValues);
}

/// <summary>One thing the other person said, dated and playable.</summary>
public sealed record OwnWordRow(Repository.OwnWord Word)
{
    public string Text => Word.Quote.Trim();
    public string When => Word.CallStartedAt.ToLocalTime().ToString("d MMM yyyy");
    public long CallId => Word.CallId;
    public int StartMs => Word.StartMs;
    public bool LowConfidence => Word.LowConfidence;
    public bool IsPromise => Word.IsPromise;

    public string? DeadlineText => Word.Deadline is { } due
        ? string.Format(Localisation.T("contactcard.vade-d"), due.ToDateTime(TimeOnly.MinValue).ToString("d MMM"))
        : null;

    public bool HasDeadline => DeadlineText is not null;
}

/// <summary>The rows about one subject.</summary>
public sealed record OwnWordsRow(string Subject, IReadOnlyList<OwnWordRow> Words);

/// <summary>One anchor under a line of the model's opinion: ▸, the excerpt number, the moment.</summary>
public sealed record OpinionAnchorRow(ContactReadingAnchor Anchor)
{
    public string Label => $"▸ [{Anchor.Label}]";
    public long CallId => Anchor.CallId;
    public int StartMs => Anchor.StartMs;
    public bool IsMe => Anchor.IsMe;

    /// <summary>Shown on hover: the sentence the impression was hung on.</summary>
    public string Quote => Anchor.Quote.Trim();
}

/// <summary>
/// One line of the model's opinion, with the anchors it survived on.
///
/// A line with no anchors never gets here: <see cref="ContactReadingAnalysis"/> drops it and
/// counts it, and the signature line says how many went that way.
/// </summary>
public sealed record OpinionLineRow(string Text, IReadOnlyList<OpinionAnchorRow> Anchors);

/// <summary>One heading of the panel and its lines. Empty sections are not rendered at all.</summary>
public sealed record OpinionSection(string Label, IReadOnlyList<OpinionLineRow> Lines);

/// <summary>One overdue promise as the card's Sözler strip shows it.</summary>
public sealed record CardPromise(Repository.PromiseRow Row, DateOnly Today)
{
    public Commitment Commitment => Row.Commitment;
    public long Id => Commitment.Id;
    public string Obligation => Commitment.EffectiveObligation;
    public string Quote => Commitment.Quote.Trim();
    public long CallId => Commitment.CallId;
    public int StartMs => Commitment.QuoteStartMs;
    public bool ByMe => Commitment.ByMe;

    public int DaysLate => Commitment.EffectiveDeadline is { } due && due < Today
        ? Today.DayNumber - due.DayNumber
        : 0;

    public string HeadText => DaysLate > 0
        ? string.Format(Localisation.T("contactcard.n-gun-gecti"), DaysLate)
        : Commitment.EffectiveDeadline is { } due
            ? string.Format(Localisation.T("contactcard.vade-d"), due.ToDateTime(TimeOnly.MinValue).ToString("d MMM"))
            : Localisation.T("contactcard.tarihsiz");
}

/// <summary>
/// The contact card: everything that has piled up about one person, on one surface, all of it
/// evidence.
///
/// One control, two hosts — the contact window's own tab and the shell's detail pane — because a
/// second implementation of "what do I know about this person" would drift from the first within
/// a release, and the two would then disagree in front of the user.
///
/// Three rules hold the whole thing together and every one of them is testable:
///
/// 1. NO SCORE. Not a trust figure, not a risk level, not a percentage of anything about the
///    person. Counts, dates, quotes and shares of measured quantities — nothing else. The
///    arrows carry no colour, because which direction is better is not the machine's to say.
/// 2. EVIDENCE AND A MODEL'S LABEL NEVER SHARE A NUMBER. Rows out of <c>tactic_evidence</c> wear
///    a badge, sit under their own source filter, and are counted on their own line. They are in
///    the same list because the user reads one list; they are never in the same total.
/// 3. THE DENOMINATOR TRAVELS WITH THE FIGURE. A metric measured on nine of thirty-one
///    conversations says so beside itself. "Not measured" is not zero.
///
/// Everything shown is read through the repository's own queries. There is no arithmetic here
/// that a second surface could get differently — only formatting and layout.
/// </summary>
public sealed partial class ContactCardViewModel : ObservableObject
{
    private readonly Repository _repository;

    /// <summary>
    /// What the opt-in opinion panel needs to spend money, or null when nobody supplied it.
    ///
    /// Null is a real state rather than a test artefact: the card is one control with two hosts,
    /// and a host that cannot run models shows the panel's off line instead of a button that
    /// would fail when pressed.
    /// </summary>
    private readonly Services.ModelAccess? _access;

    /// <summary>Every kept pattern row, before the source filter narrows it.</summary>
    private readonly List<PatternRow> _allPatterns = [];

    public ContactCardViewModel(Repository repository, long contactId, Services.ModelAccess? access = null)
    {
        _repository = repository;
        _access = access;
        ContactId = contactId;

        Undo.Undone += (_, _) => Refresh();

        Refresh();
    }

    public long ContactId { get; }

    /// <summary>Raised when a ▸ wants the conversation opened at the moment. The host decides how.</summary>
    public event EventHandler<(long CallId, int StartMs, bool IsMe)>? OpenRequested;

    /// <summary>Raised by "Sözler sayfasında aç". The host navigates; a view model does not.</summary>
    public event EventHandler? PromisesRequested;

    /// <summary>
    /// Raised when somebody arrived here to read one figure's history — the ledger's [Yolculuk].
    ///
    /// Only the control knows where its own sections are, so the card asks and the view scrolls.
    /// </summary>
    public event EventHandler? JourneyRequested;

    /// <summary>Asks the card to bring the "Rakam yolculuğu" section into view.</summary>
    public void RequestJourney() => JourneyRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>What was just ruled on, and the way back — the same quiet card as everywhere else.</summary>
    public UndoSlot Undo { get; } = new();

    // ---- the source filter -------------------------------------------------------------------
    //
    // Four values, and the split is the plan's rule about grounds made operable: "Kural" and
    // "Denetim" are deterministic checks over the transcript, "Değerlendirme" is every row a
    // model labelled. Choosing one of the first two makes it impossible for a model's count to
    // be on screen at all; choosing the third shows nothing but badged rows.

    public const string SourceAll = "Hepsi";
    public const string SourceRule = "Kural";
    public const string SourceAudit = "Denetim";
    public const string SourceAssessment = "Degerlendirme";

    [ObservableProperty] private string _sourceFilter = SourceAll;

    partial void OnSourceFilterChanged(string value) => ApplySourceFilter();

    [RelayCommand]
    private void SetSource(string source) => SourceFilter = source;

    [ObservableProperty] private CardPeriod _period = CardPeriod.Months12;

    partial void OnPeriodChanged(CardPeriod value) => LoadTrend();

    [RelayCommand]
    private void SetPeriod(string period) =>
        Period = Enum.TryParse<CardPeriod>(period, out var parsed) ? parsed : CardPeriod.Months12;

    // ---- Gidişat -----------------------------------------------------------------------------

    public ObservableCollection<TrendRow> Trend { get; } = [];

    public bool HasTrend => Trend.Count > 0;

    /// <summary>"N grup görüşmesi sayılmadı", or null when there were none.</summary>
    [ObservableProperty] private string? _groupCallsNote;

    // ---- Sözler ------------------------------------------------------------------------------

    [ObservableProperty] private string _promiseTally = "";

    public ObservableCollection<CardPromise> Promises { get; } = [];

    public bool HasPromises => Promises.Count > 0;

    // ---- Kalıplar ----------------------------------------------------------------------------

    public ObservableCollection<PatternRow> Patterns { get; } = [];

    public bool HasPatterns => Patterns.Count > 0;

    /// <summary>"Reddettiklerin (3) sayılmaz", or null when nothing was turned down.</summary>
    [ObservableProperty] private string? _dismissedNote;

    // ---- Rakam yolculuğu ---------------------------------------------------------------------

    public ObservableCollection<JourneyRow> Journeys { get; } = [];

    public bool HasJourneys => Journeys.Count > 0;

    // ---- Elindeki kayıtlar -------------------------------------------------------------------

    public ObservableCollection<OwnWordsRow> OwnWords { get; } = [];

    public bool HasOwnWords => OwnWords.Count > 0;

    // ---- reading -----------------------------------------------------------------------------

    /// <summary>Re-reads every section. Called on construction, after a ruling, and by [Yenile].</summary>
    [RelayCommand]
    public void Refresh()
    {
        LoadTrend();
        LoadPromises();
        LoadPatterns();
        LoadJourneys();
        LoadOwnWords();
        LoadOpinion();
    }

    /// <summary>
    /// The conversations that count towards this person, and the group calls that do not.
    ///
    /// A group call arrives as one mixed far stream: every remote voice in it is the same
    /// channel, so nothing in it can be attributed to this contact. Counting them in the
    /// frequency line would make somebody look more present than they were. They are excluded
    /// and their number is printed, because a silent exclusion is its own kind of lie.
    /// </summary>
    private (List<Repository.ContactCallPoint> Calls, int GroupCalls) OneToOneCalls()
    {
        var series = _repository.ContactSeries(ContactId);

        var group = _repository
            .ListCalls(ContactId, limit: int.MaxValue)
            .Where(c => c.Kind == CallKind.Group)
            .Select(c => c.Id)
            .ToHashSet();

        return ([.. series.Where(p => !group.Contains(p.CallId))],
            series.Count(p => group.Contains(p.CallId)));
    }

    private void LoadTrend()
    {
        Trend.Clear();

        var (calls, groupCalls) = OneToOneCalls();

        GroupCallsNote = groupCalls == 0
            ? null
            : string.Format(Localisation.T("contactcard.n-grup-gorusmesi-sayilmadi"), groupCalls);

        var questions = _repository.SpeechActs(ContactId);
        var kept = calls.Select(c => c.CallId).ToHashSet();

        // The question counts carry one row per conversation, group calls included; the same
        // exclusion has to reach them or the denominator would count calls the series dropped.
        var scoped = new Repository.SpeechActSummary(
            [.. questions.Calls.Where(c => kept.Contains(c.CallId))]);

        var promises = _repository.PromiseLedger(contactId: ContactId, includeClosed: true);

        var report = ContactTrend.Build(calls, scoped, promises, DateTimeOffset.Now);

        var months = Visible(report.Months);

        foreach (var change in report.Changes)
        {
            if (change.Metric == ContactMetric.TheirPromises) continue;   // Sözler says it better.

            Trend.Add(Row(change, months));
        }

        OnPropertyChanged(nameof(HasTrend));
    }

    /// <summary>The months the chip asked for. The chip draws the line; it never moves the pair.</summary>
    private IReadOnlyList<ContactMonth> Visible(IReadOnlyList<ContactMonth> months)
    {
        var take = Period switch
        {
            CardPeriod.Months3 => 3,
            CardPeriod.Months6 => 6,
            CardPeriod.Months12 => 12,
            _ => months.Count,
        };

        return months.Count <= take ? months : [.. months.Skip(months.Count - take)];
    }

    private static TrendRow Row(ContactChange change, IReadOnlyList<ContactMonth> months)
    {
        var series = months.Select(m => Value(change.Metric, m)).ToList();

        return new TrendRow(
            change.Metric,
            Localisation.T(LabelKey(change.Metric)),
            Format(change.Metric, change.Recent),
            Format(change.Metric, change.Previous),
            Arrow(change.Recent, change.Previous),
            Denominator(change),
            Detail(change.Metric, months),
            Sparkline(series));
    }

    private static string LabelKey(ContactMetric metric) => metric switch
    {
        ContactMetric.Calls => "contactcard.gorusme-sikligi",
        ContactMetric.IncomingShare => "contactcard.kim-aradi",
        ContactMetric.TalkShare => "contactcard.konusma-payin",
        ContactMetric.TheirInterruptions => "contactcard.soz-kesme-o",
        ContactMetric.UnansweredQuestions => "contactcard.cevapsiz-soru-o",
        _ => "contactcard.gorusme-sikligi",
    };

    private static double? Value(ContactMetric metric, ContactMonth month) => metric switch
    {
        ContactMetric.Calls => month.Calls,
        ContactMetric.IncomingShare => month.IncomingShare,
        ContactMetric.TalkShare => month.MeanTalkShare,
        ContactMetric.TheirInterruptions => month.TheirInterruptionsPer10Min,
        ContactMetric.UnansweredQuestions => month.UnansweredRate,
        _ => null,
    };

    /// <summary>The figure in its own unit, or the word for "nobody measured this".</summary>
    private static string Format(ContactMetric metric, double? value)
    {
        if (value is not { } number) return Localisation.T("contactcard.olculmedi");

        return metric switch
        {
            ContactMetric.Calls => string.Format(Localisation.T("contactcard.n-gorusme"), (int)Math.Round(number)),
            ContactMetric.IncomingShare or ContactMetric.TalkShare or ContactMetric.UnansweredQuestions =>
                string.Format(Localisation.T("contactcard.yuzde-n"), (int)Math.Round(number * 100)),
            ContactMetric.TheirInterruptions =>
                string.Format(Localisation.T("contactcard.n-on-dakikada"), number.ToString("0.#")),
            _ => number.ToString("0.#"),
        };
    }

    /// <summary>
    /// ▲, ▼ or —, and no colour anywhere near it.
    ///
    /// The glyph says which way the number moved and stops there. Painting it green or red would
    /// be the product deciding that talking less, or being interrupted more, is a good or a bad
    /// thing about somebody — which is exactly the judgement this card refuses to make.
    /// </summary>
    private static string Arrow(double? recent, double? previous)
    {
        if (recent is not { } now || previous is not { } before) return "—";

        // A hair of movement in a mean is not movement. One percent of the smaller of the two.
        var slack = Math.Abs(before) * 0.01;

        if (now > before + slack) return "▲";
        if (now < before - slack) return "▼";

        return "—";
    }

    /// <summary>"N/M görüşmede ölçüldü" — said only where some conversation failed to produce it.</summary>
    private static string? Denominator(ContactChange change) =>
        change.RecentMeasured >= change.RecentTotal
            ? null
            : string.Format(
                Localisation.T("contactcard.n-m-gorusmede-olculdu"), change.RecentMeasured, change.RecentTotal);

    /// <summary>Who called, counted rather than shared: "↓12 ↑7 ?3". Unknown is shown and excluded.</summary>
    private static string? Detail(ContactMetric metric, IReadOnlyList<ContactMonth> months)
    {
        if (metric != ContactMetric.IncomingShare) return null;

        return string.Format(
            Localisation.T("contactcard.gelen-giden-bilinmeyen"),
            months.Sum(m => m.Incoming),
            months.Sum(m => m.Outgoing),
            months.Sum(m => m.DirectionUnknown));
    }

    /// <summary>The width of the drawn sparkline, in device-independent pixels.</summary>
    public const double SparklineWidth = 96;

    /// <summary>Its height. Small on purpose: a shape beside a number, not a chart.</summary>
    public const double SparklineHeight = 18;

    /// <summary>
    /// The months as a polyline. Pure layout: in go the values, out come points.
    ///
    /// Months nothing measured are LEFT OUT rather than plotted at zero — a month with no talk
    /// statistics is not a month of silence, and a line dipping to the floor would say it was.
    /// The caption beside the strip says the gaps are gaps.
    ///
    /// A flat series draws down the middle instead of dividing by a zero range.
    /// </summary>
    public static PointCollection Sparkline(
        IReadOnlyList<double?> values, double width = SparklineWidth, double height = SparklineHeight)
    {
        var measured = values
            .Select((value, index) => (value, index))
            .Where(pair => pair.value is not null)
            .Select(pair => (Value: pair.value!.Value, pair.index))
            .ToList();

        var points = new PointCollection();
        if (measured.Count == 0) return points;

        var min = measured.Min(p => p.Value);
        var max = measured.Max(p => p.Value);
        var range = max - min;

        var steps = Math.Max(1, values.Count - 1);

        foreach (var (value, index) in measured)
        {
            var x = values.Count == 1 ? width / 2 : width * index / steps;
            var y = range <= 0 ? height / 2 : height - (value - min) / range * height;

            points.Add(new Point(x, y));
        }

        return points;
    }

    // ---- Sözler ------------------------------------------------------------------------------

    /// <summary>
    /// The promise line and the two or three most overdue rows.
    ///
    /// Three counts and never a ratio: "tutulan 4/9" would write the user's own habit of marking
    /// things onto the other person, which §7-9 forbids. The machine has no opinion about whether
    /// anything was kept; "işaretlediklerin" says whose figure it is.
    /// </summary>
    private void LoadPromises()
    {
        Promises.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = _repository.PromiseLedger(contactId: ContactId, includeClosed: true);

        var live = rows.Where(r => !r.Commitment.DismissedByUser).ToList();

        var theirs = live.Where(r => !r.Commitment.ByMe).ToList();
        var mine = live.Where(r => r.Commitment.ByMe).ToList();

        var theirsOpen = theirs.Where(r => r.Commitment.Status == CommitmentStatus.Open).ToList();

        PromiseTally = string.Format(
            Localisation.T("contactcard.soz-ozeti"),
            theirsOpen.Count,
            theirsOpen.Count(r => Overdue(r, today)),
            mine.Count(r => r.Commitment.Status == CommitmentStatus.Open),
            live.Count(r => r.Commitment.Status == CommitmentStatus.Fulfilled));

        foreach (var row in theirsOpen
                     .Select(r => new CardPromise(r, today))
                     .Where(p => p.DaysLate > 0)
                     .OrderByDescending(p => p.DaysLate)
                     .Take(3))
        {
            Promises.Add(row);
        }

        OnPropertyChanged(nameof(HasPromises));
    }

    private static bool Overdue(Repository.PromiseRow row, DateOnly today) =>
        row.Commitment.Status == CommitmentStatus.Open
        && !row.Commitment.IsConditional
        && row.Commitment.EffectiveDeadline is { } due
        && due < today;

    [RelayCommand]
    private void Fulfil(CardPromise? promise)
    {
        if (promise is null) return;

        Undo.Offer(Services.LedgerActions.Fulfil(_repository, promise.Commitment));
        Refresh();
    }

    [RelayCommand]
    private void DismissPromise(CardPromise? promise)
    {
        if (promise is null) return;

        Undo.Offer(Services.LedgerActions.Dismiss(_repository, promise.Commitment));
        Refresh();
    }

    [RelayCommand]
    private void OpenPromises() => PromisesRequested?.Invoke(this, EventArgs.Empty);

    // ---- Kalıplar ----------------------------------------------------------------------------

    /// <summary>
    /// Every counted pattern, evidence and model label together in one list and never in one
    /// number.
    ///
    /// Which table a row came from is decided the same way the repository decides it: a kind that
    /// parses as a <see cref="FlagKind"/> is the ledger's, and anything else is a tactic label.
    /// One rule, in one place, so the badge and the filter can never disagree with the query that
    /// fetches the quotes.
    /// </summary>
    private void LoadPatterns()
    {
        // A ruling changes the counts above the quotes, not what the user was reading. The rows
        // they had opened stay open, and their quotes are re-read — otherwise turning one
        // sentence down would close the list it was in.
        var expanded = _allPatterns
            .Where(p => p.IsExpanded)
            .Select(p => (p.Kind, p.Source))
            .ToHashSet();

        _allPatterns.Clear();

        var summaries = _repository.ContactPatterns(ContactId);

        // The bar is sized against the widest CONFIDENT count, so a row leaning on audio the
        // transcriber doubted cannot set the scale everything else is drawn against.
        var widest = summaries
            .Select(s => s.Total - s.LowConfidence)
            .DefaultIfEmpty(0)
            .Max();

        foreach (var summary in summaries.OrderByDescending(s => s.Total).ThenBy(s => s.Kind, StringComparer.Ordinal))
        {
            var model = IsModelLabelKind(summary.Kind);
            _allPatterns.Add(new PatternRow(summary, model, KindLabel(summary.Kind), widest));
        }

        foreach (var row in _allPatterns.Where(p => expanded.Contains((p.Kind, p.Source))))
        {
            row.IsExpanded = true;
            LoadQuotes(row);
        }

        var dismissed = summaries.Sum(s => s.Dismissed);

        DismissedNote = dismissed == 0
            ? null
            : string.Format(Localisation.T("contactcard.reddettiklerin-n-sayilmaz"), dismissed);

        ApplySourceFilter();
    }

    /// <summary>
    /// True when the row is a model's label rather than a deterministic finding.
    ///
    /// The same test the repository makes: <see cref="Repository.PatternRows"/> reads the flag
    /// table for a kind that names a <see cref="FlagKind"/> and the tactic table for anything
    /// else. Both tables can call their source "pipeline", so the source alone cannot tell them
    /// apart — which is precisely why the badge is computed from the kind.
    /// </summary>
    public static bool IsModelLabelKind(string kind) =>
        !Enum.TryParse<FlagKind>(kind, ignoreCase: false, out _);

    private void ApplySourceFilter()
    {
        Patterns.Clear();

        foreach (var row in _allPatterns.Where(Matches)) Patterns.Add(row);

        OnPropertyChanged(nameof(HasPatterns));
    }

    private bool Matches(PatternRow row) => SourceFilter switch
    {
        SourceRule => !row.IsModelLabel && row.Source == Flag.Sources.Pipeline,
        SourceAudit => !row.IsModelLabel && row.Source == Flag.Sources.Consistency,
        SourceAssessment => row.IsModelLabel,
        _ => true,
    };

    /// <summary>
    /// The words for a kind, in the user's language.
    ///
    /// Localised here rather than borrowed from the ledger's own hard-coded Turkish, and both
    /// vocabularies live side by side: a ledger kind and a tactic label are counted apart, so
    /// they are named apart too. An unknown label prints itself, which is visible; a fallback
    /// like "diğer" would quietly file it under a heading nobody chose.
    /// </summary>
    public static string KindLabel(string kind)
    {
        var key = "contactcard.kalip-" + Slug(kind);
        var text = Localisation.T(key);

        return text == key ? kind : text;
    }

    private static string Slug(string kind) => kind switch
    {
        nameof(FlagKind.OverdueCommitment) => "vadesi-gecti",
        nameof(FlagKind.MovedDeadline) => "tarih-kaydi",
        nameof(FlagKind.ChangedAmount) => "rakam-degisti",
        nameof(FlagKind.Contradiction) => "celiski",
        nameof(FlagKind.EvadedQuestion) => "cevapsiz-soru",
        nameof(FlagKind.PressureTactic) => "baski-isareti",
        nameof(FlagKind.ScamPattern) => "dolandiricilik-kalibi",
        nameof(FlagKind.TimelineMismatch) => "zaman-uyumsuzlugu",
        nameof(FlagKind.VagueShift) => "belirsizlesme",
        _ => kind.Replace('_', '-'),
    };

    [RelayCommand]
    private void ToggleQuotes(PatternRow? row)
    {
        if (row is null) return;

        row.IsExpanded = !row.IsExpanded;
        if (!row.IsExpanded) return;

        LoadQuotes(row);
    }

    private void LoadQuotes(PatternRow row)
    {
        row.Quotes.Clear();

        var quotes = _repository.PatternRows(ContactId, row.Kind, row.Source);

        // One read per conversation, not per quote: a well-used contact's row can hold dozens of
        // sentences out of a handful of calls.
        var verdicts = new Dictionary<(long CallId, string Folded), VerdictValue>();

        foreach (var callId in quotes.Select(q => q.CallId).Distinct())
        {
            foreach (var verdict in _repository.Verdicts(callId, VerdictKind.Pattern))
                verdicts[(callId, verdict.QuoteFolded)] = verdict.Value;
        }

        foreach (var quote in quotes)
        {
            // The user's own ruling, found by the words rather than by a row id a re-run moves.
            var key = (quote.CallId, TurkishText.NormalizeForSearch(quote.Quote));

            row.Quotes.Add(new PatternQuoteRow(quote, row.IsModelLabel)
            {
                VerdictText = verdicts.TryGetValue(key, out var value) ? VerdictText(value) : null,
            });
        }
    }

    private static string VerdictText(VerdictValue value) => value switch
    {
        VerdictValue.Correct => Localisation.T("contactcard.dinledin-dogru"),
        VerdictValue.Misheard => Localisation.T("contactcard.dinledin-yanlis-duyulmus"),
        VerdictValue.NotThat => Localisation.T("contactcard.dinledin-bu-o-degil"),
        _ => Localisation.T("contactcard.dinledin-dogru"),
    };

    /// <summary>
    /// The user turns a counted row down, through the one verb set every ledger surface uses.
    ///
    /// A flag and a tactic quote live in different tables and are dismissed by different calls,
    /// but both are tombstones and both come back — so both go through
    /// <see cref="Services.LedgerActions"/>, which announces the ruling to every other screen
    /// showing the row.
    /// </summary>
    [RelayCommand]
    private void DismissQuote(PatternQuoteRow? quote)
    {
        if (quote is null) return;

        if (quote.IsModelLabel)
        {
            Undo.Offer(Services.LedgerActions.DismissTactic(_repository, quote.Id, quote.Text));
        }
        else
        {
            var flag = _repository
                .GetFlags(ContactId, includeDismissed: true)
                .FirstOrDefault(f => f.Id == quote.Id);

            if (flag is null) return;

            Undo.Offer(Services.LedgerActions.Dismiss(_repository, flag));
        }

        Refresh();
    }

    [RelayCommand]
    private void Correct(PatternQuoteRow? quote) => Rule(quote, VerdictValue.Correct);

    [RelayCommand]
    private void Misheard(PatternQuoteRow? quote) => Rule(quote, VerdictValue.Misheard);

    [RelayCommand]
    private void NotThat(PatternQuoteRow? quote) => Rule(quote, VerdictValue.NotThat);

    /// <summary>
    /// What the user heard when they played the moment. USER DATA: nothing in the pipeline
    /// writes one and no re-run deletes one, which is why every "M/N dinlendi" figure on this
    /// card is honest.
    /// </summary>
    private void Rule(PatternQuoteRow? quote, VerdictValue value)
    {
        if (quote is null) return;

        _repository.SaveVerdict(new Verdict
        {
            CallId = quote.CallId,
            Kind = VerdictKind.Pattern,
            TargetId = quote.Id,
            QuoteFolded = TurkishText.NormalizeForSearch(quote.Text),
            StartMs = quote.StartMs,
            Value = value,
            DecidedAt = DateTimeOffset.Now,
        });

        quote.VerdictText = VerdictText(value);

        // "M/N dinlendi" on the row above just moved. The open lists stay open.
        LoadPatterns();
    }

    // ---- Rakam yolculuğu ---------------------------------------------------------------------

    private void LoadJourneys()
    {
        Journeys.Clear();

        foreach (var journey in _repository.FigureJourney(ContactId))
        {
            var stops = journey.Stops
                .Select((stop, index) => new JourneyStop(stop, index == journey.Stops.Count - 1))
                .ToList();

            Journeys.Add(new JourneyRow(journey, stops));
        }

        OnPropertyChanged(nameof(HasJourneys));
    }

    // ---- Elindeki kayıtlar -------------------------------------------------------------------

    private void LoadOwnWords()
    {
        OwnWords.Clear();

        foreach (var group in _repository.OwnWords(ContactId))
        {
            OwnWords.Add(new OwnWordsRow(
                group.Subject.Trim(), [.. group.Words.Select(w => new OwnWordRow(w))]));
        }

        OnPropertyChanged(nameof(HasOwnWords));
    }

    // ---- the model's opinion (opt-in, its own ground) ------------------------------------------
    //
    // The one part of this card that is not evidence, and everything here exists to keep the two
    // apart. It sits below every counted thing, on its own surface, under a heading that says
    // whose opinion it is; it is off unless somebody turned it on; it is signed by the model and
    // dated; and its two written boundaries — no psychological or emotional state, no "arguments
    // you can use" — are in the footer rather than only in the prompt, because a refusal nobody
    // can see reads as an oversight and gets "fixed" by the next person.
    //
    // Nothing here feeds anything. The stored reading is a dead end: no prompt receives it, no
    // count above it moves because of it, and no figure on this card is computed from it.

    /// <summary>The sections in the order the panel shows them, empty ones omitted.</summary>
    public ObservableCollection<OpinionSection> Opinion { get; } = [];

    [ObservableProperty] private string? _opinionSignature;
    [ObservableProperty] private string? _opinionCounterReading;
    [ObservableProperty] private string? _opinionProblem;

    /// <summary>"Alıntıların çoğu bulunamadı" — the ledger's own sentence, same threshold.</summary>
    [ObservableProperty] private string? _opinionNotice;

    [ObservableProperty] private bool _opinionIsStale;
    [ObservableProperty] private bool _opinionIsThin;
    [ObservableProperty] private bool _opinionIsRunning;

    /// <summary>True once the user has pressed [Katılmıyorum] on the reading now on screen.</summary>
    [ObservableProperty] private bool _opinionRejected;

    /// <summary>The row the verdict would be written to. Null while no reading is stored.</summary>
    private long? _opinionId;

    /// <summary>Whether the panel exists at all. Off is one line saying where the switch is.</summary>
    public bool OpinionEnabled => _access?.Settings().ContactReadingEnabled ?? false;

    /// <summary>The numbers behind a refusal, when the refusal was made just now.</summary>
    [ObservableProperty] private string? _opinionThinDetail;

    /// <summary>True when there is something to show — a thin answer is not one.</summary>
    public bool HasOpinion => Opinion.Count > 0;

    /// <summary>
    /// Whether a reading was ever stored for this person, which is a different question from
    /// whether anything of it is on screen.
    ///
    /// The card used to ask only <see cref="HasOpinion"/>. So a stored reading that came back
    /// "yetersiz", or one whose every item was dropped for want of an anchor, printed "bu kişi
    /// için henüz bir okuma istenmedi" directly underneath its own model-and-date signature —
    /// two sentences on one card contradicting each other, each answering a different question.
    /// </summary>
    public bool HasStoredOpinion => _opinionId is not null;

    /// <summary>
    /// Nothing has ever been asked for this person. The two refusals are not this: a reading
    /// declined for want of record, and one that survived nothing, each say so in their own words.
    /// </summary>
    public bool OpinionNotAsked => !HasStoredOpinion && !OpinionIsThin;

    /// <summary>
    /// A reading was paid for and nothing survived it: every item cited an anchor that was never
    /// handed over. Distinct from <see cref="OpinionIsThin"/>, which is refused before a model is
    /// asked anything at all.
    /// </summary>
    public bool OpinionIsEmpty => HasStoredOpinion && !HasOpinion && !OpinionIsThin;

    /// <summary>Can be asked only where a model can actually be reached.</summary>
    public bool CanAskOpinion => OpinionEnabled && _access is not null && !OpinionIsRunning;

    private void LoadOpinion()
    {
        Opinion.Clear();

        OpinionSignature = null;
        OpinionCounterReading = null;
        OpinionNotice = null;
        OpinionThinDetail = null;
        OpinionIsStale = false;
        OpinionIsThin = false;
        OpinionRejected = false;
        _opinionId = null;

        Announce();

        if (!OpinionEnabled) return;

        if (_repository.LatestContactReading(ContactId) is not { } stored) return;
        if (ContactReadingAnalysis.FromStored(stored.Json) is not { } report) return;

        _opinionId = stored.Id;
        OpinionRejected = stored.UserVerdict == ContactReadingAnalysis.Disagree;
        OpinionIsThin = report.Insufficient;

        OpinionSignature = string.Format(
            Localisation.T("contactcard.okuma-imzasi"),
            stored.ModelUsed ?? "model",
            stored.CreatedAt.ToLocalTime().ToString("d MMM yyyy"),
            stored.CallsCovered,
            stored.ExcerptCount,
            stored.RejectedCount);

        // "Have there been conversations since?" — asked of the calls, not of the text, so a
        // reading is old the moment the history moved under it.
        OpinionIsStale = stored.InputHash != CurrentInputHash();

        // The same threshold and the same sentence the ledger uses when a model's quotes mostly
        // cannot be found: it is the same failure, made about a person instead of a call.
        OpinionNotice = report.RejectionRate > 0.4
            ? Localisation.T("contactcard.okuma-model-uygun-olmayabilir")
            : null;

        if (!report.Insufficient) Fill(report);

        Announce();
    }

    /// <summary>The fingerprint of today's history, to compare with the one stored beside a reading.</summary>
    private string CurrentInputHash() =>
        ContactReadingAnalysis.InputHash(OneToOneCalls().Calls.Select(c => c.CallId));

    private void Fill(ContactReadingReport report)
    {
        void Section(string key, IReadOnlyList<ContactReadingItem> items)
        {
            if (items.Count == 0) return;

            Opinion.Add(new OpinionSection(
                Localisation.T(key),
                [.. items.Select(i => new OpinionLineRow(
                    i.Text, [.. i.Anchors.Select(a => new OpinionAnchorRow(a))]))]));
        }

        // The general impression is a section of one so that it renders like everything else —
        // with its anchors beside it, which is the whole point of holding it to the same rule.
        if (report.GeneralImpression.Anchors.Count > 0)
            Section("contactcard.genel-izlenim", [report.GeneralImpression]);

        Section("contactcard.iletisim-tarzi", report.CommunicationStyle);
        Section("contactcard.oncelikler", report.Priorities);
        Section("contactcard.guclu-yanlar", report.Strengths);
        Section("contactcard.zayif-yanlar", report.Weaknesses);
        Section("contactcard.cevapsiz-kalan-konular", report.UnansweredTopics);
        Section("contactcard.gorusmeye-giderken", report.BeforeYouGo);
        Section("contactcard.ben-icin-notlar", report.NotesForMe);

        OpinionCounterReading = string.IsNullOrWhiteSpace(report.CounterReading)
            ? null
            : report.CounterReading.Trim();
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(OpinionEnabled));
        OnPropertyChanged(nameof(HasOpinion));
        OnPropertyChanged(nameof(HasStoredOpinion));
        OnPropertyChanged(nameof(OpinionNotAsked));
        OnPropertyChanged(nameof(OpinionIsEmpty));
        OnPropertyChanged(nameof(CanAskOpinion));
    }

    /// <summary>The thin flag decides which of the three empty sentences the card shows.</summary>
    partial void OnOpinionIsThinChanged(bool value) => Announce();

    /// <summary>
    /// [Yeniden sor]. Runs the packet, stores the answer, and shows what survived.
    ///
    /// Deliberately never automatic: it costs money and it is an opinion about a person, so it
    /// happens when somebody asks for it and at no other moment.
    /// </summary>
    [RelayCommand]
    private async Task AskOpinionAsync(CancellationToken cancellationToken)
    {
        if (OpinionIsRunning || _access is null) return;

        var settings = _access.Settings();
        if (!settings.ContactReadingEnabled) return;

        if (!settings.LlmReachableInPrinciple)
        {
            OpinionProblem = Localisation.T("contactcard.okuma-servis-yok");
            return;
        }

        OpinionIsRunning = true;
        OpinionProblem = null;
        Announce();

        try
        {
            var client = Core.Llm.LlmClientFactory.Create(
                _access.Http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

            var report = await new ContactReadingAnalysis(client, _repository).RunAsync(
                ContactId,
                settings.ResolvedConsistencyModel,
                settings.PreferredName,
                settings.Provider.SendsDataOffMachine,
                cancellationToken);

            if (!report.Ok)
            {
                OpinionProblem = report.Problem;
                return;
            }

            // Re-read rather than rendered from the returned object: what the panel shows is
            // what was stored, so reopening the card can never show something different.
            LoadOpinion();

            // A refusal is an answer, and on this archive it is the answer most people get —
            // most of the nine contacts have fewer than three conversations. Nothing was stored,
            // because nothing was asked of a model: the packet was too thin to pay for. So the
            // re-read above finds no row and clears the panel, and without these two lines
            // [Yeniden sor] did nothing at all that anyone could see, on exactly the cards where
            // it is pressed most.
            if (report.Insufficient)
            {
                OpinionIsThin = true;
                OpinionThinDetail = string.Format(
                    Localisation.T("contactcard.okuma-yetersiz-simdi"),
                    report.CallsCovered, report.ExcerptCount);
            }
        }
        catch (Exception e)
        {
            OpinionProblem = string.Format(Localisation.T("contactcard.okuma-tamamlanamadi"), e.Message);
        }
        finally
        {
            OpinionIsRunning = false;
            Announce();
        }
    }

    /// <summary>
    /// [Katılmıyorum]. The user's column, and the feature's own measurement.
    ///
    /// Three people in a row whose reading was rejected is the acceptance rule failing, and the
    /// answer is not a defence: the switch goes off, the settings card grows an "ölçüm olumsuz"
    /// badge, and the fact is written to the log so the negative result can be reported rather
    /// than rediscovered. Turning the switch back on is the user overruling that knowingly.
    /// </summary>
    [RelayCommand]
    private void DisagreeWithOpinion()
    {
        if (_opinionId is not { } id) return;

        _repository.SetContactReadingVerdict(id, ContactReadingAnalysis.Disagree);
        OpinionRejected = true;

        if (_access is null) return;

        var verdicts = _repository.RecentContactReadingVerdicts(ContactReadingAnalysis.NegativeStreak);
        if (!ContactReadingAnalysis.MeasurementIsNegative(verdicts)) return;

        var settings = _access.Settings();
        if (!settings.ContactReadingEnabled) return;

        _access.Save(settings with
        {
            ContactReadingEnabled = false,
            ContactReadingMeasuredNegative = true,
        });

        Services.AppLog.Write("kişi",
            $"kişi kartı modelin görüşü: üst üste {ContactReadingAnalysis.NegativeStreak} kişide "
            + "[Katılmıyorum] işaretlendi; özellik kendini kapattı");

        LoadOpinion();
    }

    // ---- playing -----------------------------------------------------------------------------

    [RelayCommand]
    private void PlayAnchor(OpinionAnchorRow? anchor)
    {
        if (anchor is not null) OpenRequested?.Invoke(this, (anchor.CallId, anchor.StartMs, anchor.IsMe));
    }

    [RelayCommand]
    private void PlayQuote(PatternQuoteRow? quote)
    {
        if (quote is not null) OpenRequested?.Invoke(this, (quote.CallId, quote.StartMs, quote.IsMe));
    }

    [RelayCommand]
    private void PlayStop(JourneyStop? stop)
    {
        if (stop is not null) OpenRequested?.Invoke(this, (stop.CallId, stop.StartMs, false));
    }

    [RelayCommand]
    private void PlayWord(OwnWordRow? word)
    {
        if (word is not null) OpenRequested?.Invoke(this, (word.CallId, word.StartMs, false));
    }

    [RelayCommand]
    private void PlayPromise(CardPromise? promise)
    {
        if (promise is not null) OpenRequested?.Invoke(this, (promise.CallId, promise.StartMs, promise.ByMe));
    }
}
