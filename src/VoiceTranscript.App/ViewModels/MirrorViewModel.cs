using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>How far back the page looks. The window before it, of the same length, is the comparison.</summary>
public enum MirrorPeriod
{
    ThreeMonths,
    SixMonths,
    OneYear,

    /// <summary>Everything counted. Has no previous window, and the cards say so instead of inventing one.</summary>
    All,
}

/// <summary>
/// One card: a figure, its label, and the same figure over the window before this one.
///
/// The arrow is a glyph and nothing else. There is no colour on this page and no word for
/// "better" — swearing less is not a virtue the machine gets to award, and a green number
/// beside a habit is exactly the judgement the product rule forbids. Up, down, or unchanged.
/// </summary>
/// <param name="Caption">Why the figure is a dash, when it is. Null when there is a figure.</param>
public sealed record MirrorStat(string Label, string Value, string Previous, string Arrow, string? Caption)
{
    public bool HasCaption => Caption is not null;
}

/// <summary>
/// One counted moment as the list shows it: when, with whom, at which second, and the words.
///
/// The token is what was matched, folded; the context is the line it came from, so the user can
/// see whether the count is right before they play it. An uncertain moment is listed and not
/// counted, and wears a pill saying so — a figure that quietly swallowed the engine's doubt
/// would be the same lie as a figure that quietly swallowed a mishearing.
/// </summary>
public sealed partial class MirrorMoment : ObservableObject
{
    public MirrorMoment(
        long callId, long? contactId, string contactName, DateTimeOffset at,
        string kind, string lexeme, string quoteFolded, int startMs,
        string context, HabitBucket bucket, VerdictValue? verdict)
    {
        CallId = callId;
        ContactId = contactId;
        ContactName = contactName;
        At = at;
        Kind = kind;
        Lexeme = lexeme;
        QuoteFolded = quoteFolded;
        StartMs = startMs;
        Context = context;
        Bucket = bucket;
        Verdict = verdict;
    }

    public long CallId { get; }
    public long? ContactId { get; }
    public string ContactName { get; }
    public DateTimeOffset At { get; }

    /// <summary><see cref="HabitKind"/> for a counted word, <see cref="DisclosureKind"/> for a disclosure.</summary>
    public string Kind { get; }

    public string Lexeme { get; }
    public string QuoteFolded { get; }
    public int StartMs { get; }
    public string Context { get; }
    public HabitBucket Bucket { get; }

    /// <summary>The user's ruling, when they have given one. Null is "not listened to yet".</summary>
    public VerdictValue? Verdict { get; }

    public bool IsHeard => Verdict is not null;
    public bool IsUncertain => Bucket == HabitBucket.Uncertain;
    public bool IsDismissed => Bucket == HabitBucket.Dismissed;

    /// <summary>"04 Eyl · Gürhan · 12:41" — the three facts that place a moment.</summary>
    public string Head =>
        $"{At.ToLocalTime():d MMM} · {ContactName} · {Timestamp}";

    public string Timestamp
    {
        get
        {
            var t = TimeSpan.FromMilliseconds(StartMs);

            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    /// <summary>What kind of moment this is, in the user's words. A disclosure says its shape, never its value.</summary>
    public string KindText => Kind switch
    {
        HabitKind.Profanity => Localisation.T("mirrorpage.metrik-kufur"),
        HabitKind.Filler => Localisation.T("mirrorpage.metrik-dolgu"),
        DisclosureKind.Iban => Localisation.T("mirrorpage.bilgi-iban"),
        DisclosureKind.Phone => Localisation.T("mirrorpage.bilgi-telefon"),
        DisclosureKind.Amount => Localisation.T("mirrorpage.bilgi-tutar"),
        DisclosureKind.Date => Localisation.T("mirrorpage.bilgi-tarih"),
        _ => Kind,
    };

    /// <summary>A disclosure has no token to rule on: only its shape was counted, and the value was never stored.</summary>
    public bool IsDisclosure => Kind is DisclosureKind.Iban or DisclosureKind.Phone
        or DisclosureKind.Amount or DisclosureKind.Date;

    public string? Pill => IsDismissed
        ? Localisation.T("mirrorpage.dusuruldu")
        : IsUncertain
            ? Localisation.T("mirrorpage.belirsiz")
            : IsHeard
                ? Localisation.T("mirrorpage.dinlendi")
                : null;

    public bool HasPill => Pill is not null;
}

/// <summary>One call's dot, placed. The page turns these into ellipses on a canvas and nothing more.</summary>
public sealed record MirrorDot(long CallId, long? ContactId, double Left, double Top, bool Hollow, string Tip)
{
    /// <summary>The diameter of a dot. Left/Top are already the top-left corner, so the page never does arithmetic.</summary>
    public const double Size = 9;

    public double Diameter => Size;
}

/// <summary>One stretch of the curve: the dots of a single engine, as a polyline's points.</summary>
public sealed record MirrorRun(PointCollection Points);

/// <summary>A dashed vertical line where the engine changed.</summary>
public sealed record MirrorBreak(double X, double Height, string Tip);

/// <summary>An axis label and where it sits.</summary>
public sealed record MirrorTick(double Left, double Top, string Text);

/// <summary>
/// Aynam: what the user does while talking, counted, with the moments behind every number.
///
/// Everything here is a count or a rate over a denominator that is stated — the user's own
/// speaking minutes, their own hundred words. Nothing is scored, ranked or coloured, and no
/// figure describes the other party: the counters never read their lines, and the page says so.
///
/// Three things this page refuses to show, each with its reason one click away: dialect (the
/// transcribers normalise speech towards written Turkish, so a dialect counter would measure the
/// engine's day rather than the speaker), role-play (intent is not measurable; the intent card
/// is what stands in its place) and emotion (speech emotion recognition is unvalidated in
/// Turkish). They are absent from the cards and named in the footnote, because a measure left
/// out silently reads as a measure nobody thought of.
/// </summary>
public sealed partial class MirrorViewModel : ObservableObject
{
    private readonly Repository _repository;

    /// <summary>The box the curve is drawn into. Fixed so the layout can be checked without a window.</summary>
    public const double CurveWidth = 900;

    public const double CurveHeight = 170;

    /// <summary>How many moments the list builds. Each one costs a look at its conversation's lines.</summary>
    private const int MomentLimit = 60;

    /// <summary>How many are considered before the "not listened to" filter narrows them.</summary>
    private const int MomentCandidates = 400;

    /// <summary>The engine choice that means "do not narrow by engine".</summary>
    public static string AllEngines => Localisation.T("mirrorpage.hepsi");

    public MirrorViewModel(Repository repository)
    {
        _repository = repository;
        _engineChoice = AllEngines;

        // The filters, not the figures. The shell refreshes every page once it is built, and
        // reading a year of counts twice at start-up is a second's delay nobody asked for.
        LoadContacts();
    }

    /// <summary>Raised when a dot or a moment wants its conversation opened; the shell does it.</summary>
    public event EventHandler<(long? ContactId, long CallId, int StartMs, bool IsMe)>? OpenRequested;

    /// <summary>Raised by a "neden ▸": the page opens the dialog, because a view model cannot.</summary>
    public event EventHandler<(string Title, string Body)>? ExplainRequested;

    // ---- the filters ---------------------------------------------------------------------

    [ObservableProperty] private MirrorPeriod _period = MirrorPeriod.ThreeMonths;

    public ObservableCollection<ContactChoice> ContactChoices { get; } = [];

    [ObservableProperty] private ContactChoice? _selectedContact;

    /// <summary>"Hepsi" and then one entry per engine that actually produced a counted transcript.</summary>
    public ObservableCollection<string> EngineChoices { get; } = [];

    [ObservableProperty] private string _engineChoice;

    /// <summary>Which figure the curve and the moment list are about.</summary>
    [ObservableProperty] private HabitMetric _metric = HabitMetric.Profanity;

    /// <summary>Narrows the moments to the ones the user has not ruled on — the work queue for listening.</summary>
    [ObservableProperty] private bool _onlyUnheard;

    /// <summary>Set while the filters are being rebuilt, so writing one does not start a second refresh.</summary>
    private bool _loading;

    partial void OnPeriodChanged(MirrorPeriod value) => Refresh();
    partial void OnSelectedContactChanged(ContactChoice? value) => Refresh();
    partial void OnEngineChoiceChanged(string value) => Refresh();
    partial void OnMetricChanged(HabitMetric value) => Refresh();
    partial void OnOnlyUnheardChanged(bool value) => Refresh();

    // ---- what the page shows -------------------------------------------------------------

    /// <summary>The six cards, in the order of the wireframe. Always six, even when every one is a dash.</summary>
    public ObservableCollection<MirrorStat> Stats { get; } = [];

    public ObservableCollection<MirrorMoment> Moments { get; } = [];

    public ObservableCollection<MirrorDot> Dots { get; } = [];
    public ObservableCollection<MirrorRun> Runs { get; } = [];
    public ObservableCollection<MirrorBreak> Breaks { get; } = [];
    public ObservableCollection<MirrorTick> MonthTicks { get; } = [];
    public ObservableCollection<MirrorTick> ValueTicks { get; } = [];

    /// <summary>"14 sayımın 11'i dinlendi, 10'u doğru" — the honesty figure, over the user's own rulings.</summary>
    [ObservableProperty] private string _precisionLine = "";

    /// <summary>How many moments are listed but not counted, said out loud rather than left to be inferred.</summary>
    [ObservableProperty] private string? _uncertainNote;

    /// <summary>Why the curve is empty, when it is.</summary>
    [ObservableProperty] private string? _curveNote;

    /// <summary>Why the moment list is empty, when the metric simply has no moments.</summary>
    [ObservableProperty] private string? _momentsNote;

    /// <summary>How many calls fed the figures — a denominator the reader can check the rest against.</summary>
    [ObservableProperty] private int _callCount;

    public bool HasDots => Dots.Count > 0;
    public bool HasMoments => Moments.Count > 0;

    public string MetricName => NameOf(Metric);

    public string MomentsHeader => string.Format(
        Localisation.T("mirrorpage.anlar-n"), MetricName, Moments.Count);

    public string PeriodSummary => string.Format(Localisation.T("mirrorpage.n-gorusme-sayildi"), CallCount);

    // ---- reading ---------------------------------------------------------------------------

    /// <summary>The people who can be filtered by. Re-read on arrival: somebody named five minutes ago must be selectable.</summary>
    public void LoadContacts()
    {
        var previous = SelectedContact?.Id;

        _loading = true;

        ContactChoices.Clear();
        ContactChoices.Add(new ContactChoice(null, Localisation.T("mirrorpage.herkes")));

        foreach (var contact in _repository.ListContacts())
            ContactChoices.Add(new ContactChoice(contact.Id, contact.Name));

        SelectedContact = ContactChoices.FirstOrDefault(c => c.Id == previous) ?? ContactChoices[0];

        _loading = false;
    }

    public void Refresh()
    {
        if (_loading) return;

        var now = DateTimeOffset.Now;
        var months = Period switch
        {
            MirrorPeriod.ThreeMonths => 3,
            MirrorPeriod.SixMonths => 6,
            MirrorPeriod.OneYear => 12,
            _ => 0,
        };

        // Two windows are read in one query: the one on screen and the one before it, which is
        // the only honest thing "önceki" can mean. A figure compared against the whole archive
        // would improve simply by the archive growing.
        DateTimeOffset? from = months > 0 ? now.AddMonths(-months) : null;
        DateTimeOffset? previousFrom = months > 0 ? now.AddMonths(-months * 2) : null;

        var since = previousFrom is { } start
            ? DateOnly.FromDateTime(start.LocalDateTime)
            : DateOnly.MinValue;

        var names = _repository.ListContacts().ToDictionary(c => c.Id, c => c.Name);

        // The engine is filtered here rather than in SQL so the dropdown can be built from the
        // same rows: a filter offering an engine with no calls behind it is a dead choice.
        var all = _repository.HabitSeries(since, SelectedContact?.Id)
            .Select(row => (row, snapshot: HabitSnapshot.FromJson(row.Json)))
            .Where(x => x.snapshot is not null)
            .Select(x => new HabitSample(
                x.row.CallId,
                x.row.StartedAt.ToLocalTime(),
                x.row.ContactId,
                x.row.Engine,
                x.snapshot!.Habits,
                x.snapshot.Talk,
                x.row.LikelyNoHeadphones))
            .ToList();

        RebuildEngineChoices(all);

        if (EngineChoice != AllEngines)
            all = [.. all.Where(s => string.Equals(s.Engine, EngineChoice, StringComparison.Ordinal))];

        var current = from is { } cut ? all.Where(s => s.StartedAt >= cut).ToList() : all;

        var previous = from is { } cut2 && previousFrom is { } earlier
            ? all.Where(s => s.StartedAt >= earlier && s.StartedAt < cut2).ToList()
            : [];

        CallCount = current.Count;
        OnPropertyChanged(nameof(PeriodSummary));

        BuildStats(current, previous, hasPreviousWindow: months > 0);
        BuildCurve(current);
        BuildMoments(current, names);
        BuildPrecision(current);
    }

    private void RebuildEngineChoices(IReadOnlyList<HabitSample> samples)
    {
        var wanted = EngineChoice;

        var engines = samples
            .Select(s => s.Engine)
            .Where(e => e is { Length: > 0 })
            .Select(e => e!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        if (EngineChoices.Count == engines.Count + 1
            && EngineChoices.Skip(1).SequenceEqual(engines, StringComparer.Ordinal))
        {
            return;
        }

        _loading = true;

        EngineChoices.Clear();
        EngineChoices.Add(AllEngines);
        foreach (var engine in engines) EngineChoices.Add(engine);

        // A choice that no longer has calls behind it falls back rather than filtering to nothing.
        EngineChoice = EngineChoices.Contains(wanted, StringComparer.Ordinal) ? wanted : AllEngines;

        _loading = false;
    }

    // ---- the six cards ----------------------------------------------------------------------

    private void BuildStats(
        IReadOnlyList<HabitSample> current, IReadOnlyList<HabitSample> previous, bool hasPreviousWindow)
    {
        Stats.Clear();

        Stats.Add(Card(HabitMetric.Profanity, current, previous, hasPreviousWindow));
        Stats.Add(Card(HabitMetric.Filler, current, previous, hasPreviousWindow));
        Stats.Add(Card(HabitMetric.SpeechRate, current, previous, hasPreviousWindow));
        Stats.Add(Card(HabitMetric.TalkShare, current, previous, hasPreviousWindow));
        Stats.Add(Card(HabitMetric.Interruptions, current, previous, hasPreviousWindow));
        Stats.Add(Card(HabitMetric.Disclosures, current, previous, hasPreviousWindow));
    }

    private static MirrorStat Card(
        HabitMetric metric,
        IReadOnlyList<HabitSample> current,
        IReadOnlyList<HabitSample> previous,
        bool hasPreviousWindow)
    {
        var now = Figure(metric, current);
        var before = Figure(metric, previous);

        var label = LabelOf(metric);

        if (now is not { } value)
        {
            return new MirrorStat(label, "—", "", "", CaptionFor(metric, current));
        }

        var previousText = !hasPreviousWindow
            ? Localisation.T("mirrorpage.onceki-donem-yok")
            : before is { } was
                ? string.Format(Localisation.T("mirrorpage.onceki-d"), Format(metric, was))
                : Localisation.T("mirrorpage.onceki-sayim-yok");

        // Up, down, level. A glyph and no colour: the direction is a measurement, the meaning is
        // the user's.
        var arrow = before is { } compared
            ? value > compared ? "▲" : value < compared ? "▼" : "—"
            : "";

        return new MirrorStat(label, Format(metric, value), previousText, arrow, null);
    }

    /// <summary>
    /// The window's figure, pooled rather than averaged: the window's hits over the window's
    /// minutes. Averaging each call's rate would let a two-minute call with one hit speak for a
    /// month.
    /// </summary>
    public static double? Figure(HabitMetric metric, IReadOnlyList<HabitSample> samples)
    {
        if (samples.Count == 0) return null;

        // A count, not a rate: how many times a shaped thing was read out, over the window.
        if (metric == HabitMetric.Disclosures)
            return samples.Sum(s => s.Report.Disclosures.Count);

        double numerator = 0;
        double denominator = 0;
        var any = false;

        foreach (var sample in samples)
        {
            if (HabitTrend.Fraction(metric, sample.Report, sample.Talk) is not { Denominator: > 0 } fraction)
                continue;

            numerator += fraction.Numerator;
            denominator += fraction.Denominator;
            any = true;
        }

        return any && denominator > 0 ? numerator / denominator : null;
    }

    private static string CaptionFor(HabitMetric metric, IReadOnlyList<HabitSample> samples) =>
        samples.Count == 0
            ? Localisation.T("mirrorpage.bu-donemde-sayim-yok")
            : metric == HabitMetric.SpeechRate
                ? Localisation.T("mirrorpage.hiz-kelime-zamani-yok")
                : Localisation.T("mirrorpage.paydasi-yok");

    private static string LabelOf(HabitMetric metric) => metric switch
    {
        HabitMetric.Profanity => Localisation.T("mirrorpage.kart-kufur"),
        HabitMetric.Filler => Localisation.T("mirrorpage.kart-dolgu"),
        HabitMetric.SpeechRate => Localisation.T("mirrorpage.kart-hiz"),
        HabitMetric.TalkShare => Localisation.T("mirrorpage.kart-pay"),
        HabitMetric.Interruptions => Localisation.T("mirrorpage.kart-kesme"),
        _ => Localisation.T("mirrorpage.kart-bilgi"),
    };

    public static string NameOf(HabitMetric metric) => metric switch
    {
        HabitMetric.Profanity => Localisation.T("mirrorpage.metrik-kufur"),
        HabitMetric.Filler => Localisation.T("mirrorpage.metrik-dolgu"),
        HabitMetric.SpeechRate => Localisation.T("mirrorpage.metrik-hiz"),
        HabitMetric.TalkShare => Localisation.T("mirrorpage.metrik-pay"),
        HabitMetric.Interruptions => Localisation.T("mirrorpage.metrik-kesme"),
        _ => Localisation.T("mirrorpage.metrik-bilgi"),
    };

    /// <summary>Each metric in its own units. A share is a percentage; a rate keeps the decimal its size needs.</summary>
    public static string Format(HabitMetric metric, double value) => metric switch
    {
        HabitMetric.Profanity => value.ToString("0.00"),
        HabitMetric.Filler => value.ToString("0.0"),
        HabitMetric.SpeechRate => value.ToString("0"),
        HabitMetric.TalkShare => $"%{value * 100:0}",
        HabitMetric.Interruptions => value.ToString("0.0"),
        _ => value.ToString("0.#"),
    };

    // ---- the curve ---------------------------------------------------------------------------

    private void BuildCurve(IReadOnlyList<HabitSample> samples)
    {
        Dots.Clear();
        Runs.Clear();
        Breaks.Clear();
        MonthTicks.Clear();
        ValueTicks.Clear();

        var series = HabitTrend.Build(Metric, samples);
        var layout = HabitTrendLayout.Place(series, CurveWidth, CurveHeight);

        foreach (var dot in layout.Dots)
        {
            var point = series.Calls[dot.Index];

            var tip = $"{point.At.ToLocalTime():d MMM yyyy} · {Format(Metric, point.Value)}"
                      + (point.Engine is { Length: > 0 } engine ? $" · {engine}" : "")
                      + (dot.Hollow ? $" · {Localisation.T("mirrorpage.kulaklik-yok")}" : "");

            Dots.Add(new MirrorDot(
                point.CallId,
                samples.FirstOrDefault(s => s.CallId == point.CallId)?.ContactId,
                dot.X - MirrorDot.Size / 2,
                dot.Y - MirrorDot.Size / 2,
                dot.Hollow,
                tip));
        }

        foreach (var run in layout.Runs)
        {
            // A run of one is a dot with no line through it; a polyline of one point draws nothing,
            // which is exactly right.
            var points = new PointCollection(run.Select(i => new System.Windows.Point(layout.Dots[i].X, layout.Dots[i].Y)));
            points.Freeze();
            Runs.Add(new MirrorRun(points));
        }

        for (var i = 0; i < layout.BreakXs.Count; i++)
        {
            var change = i < series.Breaks.Count ? series.Breaks[i] : null;

            Breaks.Add(new MirrorBreak(
                layout.BreakXs[i],
                CurveHeight,
                change is null
                    ? Localisation.T("mirrorpage.motor-degisti")
                    : string.Format(
                        Localisation.T("mirrorpage.motor-degisti-d"),
                        change.From ?? "?", change.To ?? "?")));
        }

        foreach (var tick in layout.MonthTicks)
        {
            MonthTicks.Add(new MirrorTick(
                tick.X, CurveHeight + 4,
                new DateTime(tick.Year, tick.Month, 1).ToString("MMM")));
        }

        foreach (var tick in layout.ValueTicks)
            ValueTicks.Add(new MirrorTick(0, tick.Y - 8, Format(Metric, tick.Value)));

        CurveNote = Dots.Count == 0 ? Localisation.T("mirrorpage.egri-icin-sayim-yok") : null;

        OnPropertyChanged(nameof(HasDots));
    }

    // ---- the moments -------------------------------------------------------------------------

    private void BuildMoments(IReadOnlyList<HabitSample> samples, IReadOnlyDictionary<long, string> names)
    {
        Moments.Clear();

        MomentsNote = Metric switch
        {
            HabitMetric.Profanity or HabitMetric.Filler or HabitMetric.Disclosures => null,
            _ => Localisation.T("mirrorpage.bu-olcunun-ani-yok"),
        };

        if (MomentsNote is null)
        {
            var candidates = Candidates(samples).Take(MomentCandidates).ToList();

            // The verdicts of the conversations actually on the list, one query each. Wanted
            // before the filter, because "yalnız dinlenmemişler" is a question about them.
            var verdicts = candidates
                .Select(c => c.CallId)
                .Distinct()
                .ToDictionary(id => id, id => _repository.Verdicts(id));

            var shown = candidates
                .Select(c => (Candidate: c, Ruling: Ruling(verdicts[c.CallId], c)))
                .Where(x => !OnlyUnheard || x.Ruling is null)
                .Take(MomentLimit)
                .ToList();

            // The lines the moments came from, one query per conversation on the list — which is
            // why the list is capped rather than unbounded.
            var lines = shown
                .Select(x => x.Candidate.CallId)
                .Distinct()
                .ToDictionary(id => id, id => _repository.GetSegments(id));

            foreach (var (candidate, ruling) in shown)
            {
                Moments.Add(new MirrorMoment(
                    candidate.CallId,
                    candidate.ContactId,
                    candidate.ContactId is { } id && names.TryGetValue(id, out var name)
                        ? name
                        : Localisation.T("mirrorpage.isimsiz"),
                    candidate.At,
                    candidate.Kind,
                    candidate.Lexeme,
                    candidate.QuoteFolded,
                    candidate.StartMs,
                    Context(lines[candidate.CallId], candidate.StartMs),
                    candidate.Bucket,
                    ruling));
            }
        }

        var uncertain = Moments.Count(m => m.IsUncertain);

        UncertainNote = uncertain == 0
            ? null
            : string.Format(Localisation.T("mirrorpage.n-belirsiz-sayilmadi"), uncertain);

        OnPropertyChanged(nameof(HasMoments));
        OnPropertyChanged(nameof(MomentsHeader));
        OnPropertyChanged(nameof(MetricName));
    }

    /// <summary>One moment before it has been given a name and a context. Newest first.</summary>
    private sealed record Candidate(
        long CallId, long? ContactId, DateTimeOffset At,
        string Kind, string Lexeme, string QuoteFolded, int StartMs, HabitBucket Bucket);

    private IEnumerable<Candidate> Candidates(IReadOnlyList<HabitSample> samples)
    {
        List<Candidate> found = [];

        foreach (var sample in samples)
        {
            if (Metric == HabitMetric.Disclosures)
            {
                // The kind and the millisecond, and deliberately nothing else: the IBAN itself
                // was never stored, and this page is not the place it starts being.
                foreach (var disclosure in sample.Report.Disclosures)
                {
                    found.Add(new Candidate(
                        sample.CallId, sample.ContactId, sample.StartedAt,
                        disclosure.Kind, "", "", disclosure.StartMs, HabitBucket.Certain));
                }

                continue;
            }

            var kind = Metric == HabitMetric.Profanity ? HabitKind.Profanity : HabitKind.Filler;

            foreach (var moment in sample.Report.Moments.Where(m => m.Kind == kind))
            {
                found.Add(new Candidate(
                    sample.CallId, sample.ContactId, sample.StartedAt,
                    moment.Kind, moment.Lexeme, moment.QuoteFolded, moment.StartMs, moment.Bucket));
            }
        }

        return found.OrderByDescending(c => c.At).ThenByDescending(c => c.StartMs);
    }

    /// <summary>
    /// The user's ruling on one moment, matched the way the counter matches it: by the words and
    /// the millisecond, within <see cref="SpeechHabits.VerdictWindowMs"/>. A disclosure has no
    /// words, so it is matched by kind and time alone.
    /// </summary>
    private static VerdictValue? Ruling(IReadOnlyList<Verdict> verdicts, Candidate candidate)
    {
        var kind = VerdictKindFor(candidate.Kind);

        return verdicts
            .Where(v => v.Kind == kind
                        && (candidate.QuoteFolded.Length == 0 || v.QuoteFolded == candidate.QuoteFolded)
                        && Math.Abs(v.StartMs - candidate.StartMs) <= SpeechHabits.VerdictWindowMs)
            .OrderBy(v => Math.Abs(v.StartMs - candidate.StartMs))
            .Select(v => (VerdictValue?)v.Value)
            .FirstOrDefault();
    }

    /// <summary>Which verdict a moment's ruling is filed under. The counted kinds share their names with it.</summary>
    public static string VerdictKindFor(string kind) => kind switch
    {
        HabitKind.Profanity => VerdictKind.Profanity,
        HabitKind.Filler => VerdictKind.Filler,
        _ => VerdictKind.Disclosure,
    };

    /// <summary>
    /// The line a moment was said in, shortened around it. The words are the evidence; without
    /// them the list is a column of timestamps somebody has to click to understand.
    /// </summary>
    public static string Context(IReadOnlyList<Segment> segments, int startMs)
    {
        var line = segments
            .Where(s => s.IsMe && s.StartMs <= startMs && startMs <= s.EndMs)
            .OrderBy(s => s.EndMs - s.StartMs)
            .FirstOrDefault()
            ?? segments
                .Where(s => s.IsMe)
                .OrderBy(s => Math.Abs(s.StartMs - startMs))
                .FirstOrDefault();

        if (line is null) return "";

        var text = line.Text.Trim();
        return text.Length <= 160 ? text : text[..159].TrimEnd() + "…";
    }

    // ---- the precision line -------------------------------------------------------------------

    private void BuildPrecision(IReadOnlyList<HabitSample> samples)
    {
        var listed = samples.Sum(s =>
            s.Report.Moments.Count(m => m.Kind is HabitKind.Profanity or HabitKind.Filler)
            + s.Report.Disclosures.Count);

        var contact = SelectedContact?.Id;

        var heard = 0;
        var correct = 0;

        foreach (var kind in new[] { VerdictKind.Profanity, VerdictKind.Filler, VerdictKind.Disclosure })
        {
            var (listened, right) = _repository.VerdictTally(kind, contact);
            heard += listened;
            correct += right;
        }

        PrecisionLine = string.Format(
            Localisation.T("mirrorpage.sayim-dinlendi-dogru"), listed, heard, correct);
    }

    // ---- the user's verbs -----------------------------------------------------------------------

    [RelayCommand]
    private void SetPeriod(string period) =>
        Period = Enum.TryParse<MirrorPeriod>(period, out var parsed) ? parsed : MirrorPeriod.ThreeMonths;

    [RelayCommand]
    private void SetMetric(string metric) =>
        Metric = Enum.TryParse<HabitMetric>(metric, out var parsed) ? parsed : HabitMetric.Profanity;

    /// <summary>The user listened and the count was right.</summary>
    [RelayCommand]
    private void Correct(MirrorMoment? moment) => Judge(moment, VerdictValue.Correct);

    /// <summary>The words were never said: the transcript misheard them.</summary>
    [RelayCommand]
    private void Misheard(MirrorMoment? moment) => Judge(moment, VerdictValue.Misheard);

    /// <summary>The words were said, but they are not that.</summary>
    [RelayCommand]
    private void NotThat(MirrorMoment? moment) => Judge(moment, VerdictValue.NotThat);

    /// <summary>
    /// Records the ruling and counts the conversation again, so the figure on the card moves with
    /// it. The recount is what applies the verdict: the stored report was counted against the
    /// verdicts that existed then, and a page that filtered the moments itself would be a second
    /// implementation of a rule that already has one.
    /// </summary>
    private void Judge(MirrorMoment? moment, VerdictValue value)
    {
        if (moment is null) return;

        _repository.SaveVerdict(new Verdict
        {
            CallId = moment.CallId,
            Kind = VerdictKindFor(moment.Kind),
            QuoteFolded = moment.QuoteFolded,
            StartMs = moment.StartMs,
            Value = value,
            DecidedAt = DateTimeOffset.UtcNow,
        });

        Services.HabitRecount.Run(_repository, moment.CallId);
        Refresh();
    }

    /// <summary>▸ on a moment: the conversation, at the second it was said. Always the user's own side.</summary>
    [RelayCommand]
    private void Play(MirrorMoment? moment)
    {
        if (moment is null) return;
        OpenRequested?.Invoke(this, (moment.ContactId, moment.CallId, moment.StartMs, true));
    }

    /// <summary>A dot on the curve is one conversation; clicking it opens that conversation.</summary>
    [RelayCommand]
    private void OpenDot(MirrorDot? dot)
    {
        if (dot is null) return;
        OpenRequested?.Invoke(this, (dot.ContactId, dot.CallId, 0, true));
    }

    /// <summary>
    /// "neden ▸" — why a measure the user might expect is not on this page.
    ///
    /// Written out rather than left implicit: a measure that is simply absent reads as one nobody
    /// thought of, and the reasons here are the product's reasoning, not an apology.
    /// </summary>
    [RelayCommand]
    private void Explain(string which)
    {
        var (title, body) = which switch
        {
            "sive" => (Localisation.T("mirrorpage.neden-sive-baslik"), Localisation.T("mirrorpage.neden-sive")),
            "rol" => (Localisation.T("mirrorpage.neden-rol-baslik"), Localisation.T("mirrorpage.neden-rol")),
            _ => (Localisation.T("mirrorpage.neden-duygu-baslik"), Localisation.T("mirrorpage.neden-duygu")),
        };

        ExplainRequested?.Invoke(this, (title, body));
    }
}
