using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The Aynam page: the filters, the two windows behind "önceki", the user's rulings, and the
/// figures it refuses to show.
///
/// Every one of these is a way the page could quietly mislead. A period chip that narrowed the
/// list but not the figures would show three months of moments under a year's rate. A "önceki"
/// taken from the whole archive would improve on its own as the archive grew. A verdict that
/// removed a moment from the list instead of from the count would hide the user's own correction
/// from them. And a dialect card would put a number on the engine's normalisation and call it
/// the speaker.
/// </summary>
public sealed class MirrorPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-aynam-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;

    /// <summary>Stems invented for the test. The real ones live in the dictionary and nowhere else — see HabitLexicon.</summary>
    private const string Swear = "zaark";

    private const string Filler = "yaani";

    public MirrorPageTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);

        _repo.UpsertLexeme(HabitKind.Profanity, Swear, ["dim"]);
        _repo.UpsertLexeme(HabitKind.Filler, Filler);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private long Contact(string name) => _repo.UpsertContact(name, CallApp.WhatsApp);

    /// <summary>
    /// One counted conversation: the user's lines, the other party's, a transcript version to
    /// hang the engine on, and the counts stored the way a recount stores them. Each of the
    /// user's lines lasts six seconds, so a hit on one line is ten a minute.
    /// </summary>
    private long Call(
        long contact, DateTimeOffset at, string engine, string[] mine, bool noHeadphones = false)
    {
        const int LineMs = 6000;

        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = at,
            Duration = TimeSpan.FromMinutes(5),
            State = ProcessingState.Analysed,
            LikelyNoHeadphones = noHeadphones,
        });

        _repo.AssignContact(call, contact);

        var lines = new List<Segment>();

        for (var i = 0; i < mine.Length; i++)
        {
            lines.Add(new Segment
            {
                CallId = call, IsMe = true,
                StartMs = i * LineMs * 2, EndMs = i * LineMs * 2 + LineMs, Text = mine[i],
            });

            lines.Add(new Segment
            {
                CallId = call, IsMe = false,
                StartMs = i * LineMs * 2 + LineMs, EndMs = i * LineMs * 2 + LineMs * 2, Text = "Anladım.",
            });
        }

        _repo.ReplaceSegments(call, lines);
        _repo.SaveTranscriptVersion(call, engine, 0.95, lines);

        HabitRecount.Run(_repo, call);

        return call;
    }

    private MirrorViewModel Page()
    {
        var page = new MirrorViewModel(_repo);
        page.Refresh();
        return page;
    }

    /// <summary>The cards stand in the metric's own order, which is what lets a test name one.</summary>
    private static MirrorStat CardFor(MirrorViewModel page, HabitMetric metric) => page.Stats[(int)metric];

    private static string Rate(double perMinute) => MirrorViewModel.Format(HabitMetric.Profanity, perMinute);

    /// <summary>
    /// Goes red when a period chip narrows the moments but not the figures, or the other way
    /// round — the state where a rate on a card describes a different set of conversations from
    /// the list underneath it.
    /// </summary>
    [Fact]
    public void ThePeriodNarrowsTheFiguresAndTheMoments()
    {
        var gurhan = Contact("Gürhan");

        Call(gurhan, DateTimeOffset.Now.AddDays(-10), "nova-3", [$"{Swear} dedim ona"]);
        Call(gurhan, DateTimeOffset.Now.AddMonths(-8), "nova-3", [$"{Swear} olmuş", $"{Swear} yine"]);

        var page = Page();

        Assert.Equal(1, page.CallCount);
        Assert.Single(page.Moments);

        page.Period = MirrorPeriod.All;

        Assert.Equal(2, page.CallCount);
        Assert.Equal(3, page.Moments.Count);
    }

    /// <summary>
    /// Goes red when "önceki" is taken from the whole archive rather than from the window before
    /// this one — a comparison that would improve by itself as the archive grew.
    /// </summary>
    [Fact]
    public void ThePreviousFigureIsThePreviousWindowNotTheWholeArchive()
    {
        var gurhan = Contact("Gürhan");

        // This window: one hit in six seconds — ten a minute. The window before: two hits in
        // twelve seconds — ten a minute as well, but from twice the speech. Long before both:
        // twenty hits in six seconds, which must reach neither figure.
        Call(gurhan, DateTimeOffset.Now.AddDays(-20), "nova-3", [$"{Swear} dedim"]);
        Call(gurhan, DateTimeOffset.Now.AddMonths(-4), "nova-3", [$"{Swear} {Swear} dedim"]);
        Call(gurhan, DateTimeOffset.Now.AddMonths(-20), "nova-3",
            [string.Join(" ", Enumerable.Repeat(Swear, 20))]);

        var page = Page();
        var card = CardFor(page, HabitMetric.Profanity);

        Assert.Equal(Rate(10), card.Value);
        Assert.Equal(string.Format(Localisation.T("mirrorpage.onceki-d"), Rate(20)), card.Previous);
        Assert.Equal("▼", card.Arrow);
    }

    /// <summary>Goes red when "Hepsi" invents a previous window out of the archive it has just shown.</summary>
    [Fact]
    public void TheWholeArchiveHasNoPreviousWindow()
    {
        var gurhan = Contact("Gürhan");
        Call(gurhan, DateTimeOffset.Now.AddDays(-3), "nova-3", [$"{Swear} dedim"]);

        var page = Page();
        page.Period = MirrorPeriod.All;

        var card = CardFor(page, HabitMetric.Profanity);

        Assert.Equal("", card.Arrow);
        Assert.Equal(Localisation.T("mirrorpage.onceki-donem-yok"), card.Previous);
    }

    /// <summary>Goes red when the person filter stops narrowing — "Gürhan'la 0,9; herkesle 0,4" is the whole point of it.</summary>
    [Fact]
    public void ThePersonFilterNarrows()
    {
        var gurhan = Contact("Gürhan");
        var uliana = Contact("Uliana");

        Call(gurhan, DateTimeOffset.Now.AddDays(-4), "nova-3", [$"{Swear} dedim", $"{Swear} yine"]);
        Call(uliana, DateTimeOffset.Now.AddDays(-3), "nova-3", ["Sakin bir konuşma oldu"]);

        var page = Page();
        Assert.Equal(2, page.CallCount);

        page.SelectedContact = page.ContactChoices.Single(c => c.Name == "Uliana");

        Assert.Equal(1, page.CallCount);
        Assert.Empty(page.Moments);

        page.SelectedContact = page.ContactChoices.Single(c => c.Name == "Gürhan");

        Assert.Equal(1, page.CallCount);
        Assert.Equal(2, page.Moments.Count);
    }

    /// <summary>
    /// Goes red when the engine filter offers an engine with nothing behind it, or stops
    /// narrowing. Engines do not count alike, so a figure pooled across two of them is two
    /// different measurements added together.
    /// </summary>
    [Fact]
    public void TheEngineFilterOffersWhatWasSeenAndNarrowsToIt()
    {
        var gurhan = Contact("Gürhan");

        Call(gurhan, DateTimeOffset.Now.AddDays(-5), "nova-3", [$"{Swear} dedim"]);
        Call(gurhan, DateTimeOffset.Now.AddDays(-4), "large-v3", [$"{Swear} dedim", $"{Swear} yine"]);

        var page = Page();

        Assert.Equal([MirrorViewModel.AllEngines, "large-v3", "nova-3"], page.EngineChoices);

        page.EngineChoice = "large-v3";

        Assert.Equal(1, page.CallCount);
        Assert.Equal(2, page.Moments.Count);
    }

    /// <summary>
    /// Goes red when a mishearing the user reported still counts — the failure that would make
    /// every figure on the page unfalsifiable — or when it disappears from the list instead, so
    /// the user cannot see their own correction.
    /// </summary>
    [Fact]
    public void AMishearingLeavesTheCountAndStaysOnTheList()
    {
        var gurhan = Contact("Gürhan");
        Call(gurhan, DateTimeOffset.Now.AddDays(-2), "nova-3", [$"{Swear} dedim", $"{Swear} yine"]);

        var page = Page();

        Assert.Equal(2, page.Moments.Count);
        Assert.Equal(Rate(10), CardFor(page, HabitMetric.Profanity).Value);

        page.MisheardCommand.Execute(page.Moments[0]);

        Assert.Equal(2, page.Moments.Count);
        Assert.Single(page.Moments, m => m.IsDismissed);
        Assert.Equal(Rate(5), CardFor(page, HabitMetric.Profanity).Value);
    }

    /// <summary>Goes red when the "not listened to" filter shows moments the user has already ruled on.</summary>
    [Fact]
    public void OnlyUnheardHidesWhatWasAlreadyRuledOn()
    {
        var gurhan = Contact("Gürhan");
        Call(gurhan, DateTimeOffset.Now.AddDays(-2), "nova-3", [$"{Swear} dedim", $"{Swear} yine"]);

        var page = Page();
        page.CorrectCommand.Execute(page.Moments[0]);

        Assert.Single(page.Moments, m => m.IsHeard);

        page.OnlyUnheard = true;

        Assert.Single(page.Moments);
        Assert.DoesNotContain(page.Moments, m => m.IsHeard);
    }

    /// <summary>
    /// Goes red when the page draws its own curve instead of the one HabitTrend and
    /// HabitTrendLayout worked out — which is what makes the shape of the chart checkable with
    /// no window open at all.
    /// </summary>
    [Fact]
    public void TheDotsFollowTheTrend()
    {
        var gurhan = Contact("Gürhan");

        Call(gurhan, DateTimeOffset.Now.AddDays(-40), "nova-3", [$"{Swear} dedim"]);
        Call(gurhan, DateTimeOffset.Now.AddDays(-20), "nova-3", [$"{Swear} dedim", $"{Swear} yine"]);
        Call(gurhan, DateTimeOffset.Now.AddDays(-5), "large-v3", [$"{Swear} dedim"], noHeadphones: true);

        var page = Page();

        var samples = _repo.HabitSeries(DateOnly.MinValue)
            .Select(row =>
            {
                var snapshot = HabitSnapshot.FromJson(row.Json)!;

                return new HabitSample(
                    row.CallId, row.StartedAt.ToLocalTime(), row.ContactId, row.Engine,
                    snapshot.Habits, snapshot.Talk, row.LikelyNoHeadphones);
            })
            .ToList();

        var series = HabitTrend.Build(HabitMetric.Profanity, samples);
        var layout = HabitTrendLayout.Place(series, MirrorViewModel.CurveWidth, MirrorViewModel.CurveHeight);

        Assert.Equal(layout.Dots.Count, page.Dots.Count);
        Assert.Equal(layout.Runs.Count, page.Runs.Count);
        Assert.Equal(layout.BreakXs.Count, page.Breaks.Count);
        Assert.Equal(layout.MonthTicks.Count, page.MonthTicks.Count);

        // The engine changed once, so the curve is two runs and is never drawn across the change.
        Assert.Equal(2, page.Runs.Count);
        Assert.Single(page.Breaks);

        // The call likely made without headphones is drawn hollow: its attribution is not trusted.
        Assert.Single(page.Dots, d => d.Hollow);
    }

    /// <summary>
    /// Goes red when a dialect card appears — or a role-play or an emotion one. The transcribers
    /// normalise speech towards written Turkish, so a dialect figure would measure the engine's
    /// day rather than the speaker; the other two are not measurable at all. The page carries the
    /// six it can count and says the rest out loud instead of leaving the absence unexplained.
    /// </summary>
    [Fact]
    public void DialectIsNeverACard()
    {
        var gurhan = Contact("Gürhan");
        Call(gurhan, DateTimeOffset.Now.AddDays(-2), "nova-3", [$"{Swear} dedim"]);

        var page = Page();

        // Six cards, and they are exactly the six figures this product can count.
        Assert.Equal(6, page.Stats.Count);
        Assert.Equal(Enum.GetValues<HabitMetric>().Length, page.Stats.Count);
        Assert.DoesNotContain(page.Stats, s => s.Label.Contains(HabitKind.Dialect, StringComparison.OrdinalIgnoreCase));

        // And is named among the measures deliberately left out, with a reason behind it.
        Assert.NotEqual("mirrorpage.sive-olculmuyor", Localisation.T("mirrorpage.sive-olculmuyor"));
        Assert.NotEqual("mirrorpage.neden-sive", Localisation.T("mirrorpage.neden-sive"));
    }

    /// <summary>
    /// Goes red when a figure with no denominator shows as a zero. "0 kelime/dk" is a claim about
    /// how the user spoke; a dash with a reason under it is the truth.
    /// </summary>
    [Fact]
    public void AFigureWithNoDenominatorIsADashWithAReason()
    {
        var gurhan = Contact("Gürhan");
        Call(gurhan, DateTimeOffset.Now.AddDays(-2), "nova-3", [$"{Swear} dedim"]);

        var page = Page();
        var rate = CardFor(page, HabitMetric.SpeechRate);

        // No line in this archive carries word timings, so the speech rate has nothing to divide.
        Assert.Equal("—", rate.Value);
        Assert.True(rate.HasCaption);
    }

    /// <summary>Goes red when the honesty line stops counting the user's own rulings.</summary>
    [Fact]
    public void ThePrecisionLineCountsTheRulings()
    {
        var gurhan = Contact("Gürhan");
        Call(gurhan, DateTimeOffset.Now.AddDays(-2), "nova-3", [$"{Swear} dedim", $"{Swear} yine"]);

        var page = Page();
        Assert.Equal(string.Format(Localisation.T("mirrorpage.sayim-dinlendi-dogru"), 2, 0, 0), page.PrecisionLine);

        page.CorrectCommand.Execute(page.Moments[0]);

        Assert.Equal(string.Format(Localisation.T("mirrorpage.sayim-dinlendi-dogru"), 2, 1, 1), page.PrecisionLine);
    }

    /// <summary>
    /// Goes red when an uncertain moment is counted. It is listed so the user can hear it and
    /// rule on it; counting it would put the engine's own doubt inside a figure that claims to
    /// be a fact.
    /// </summary>
    [Fact]
    public void AnUncertainMomentIsListedAndNotCounted()
    {
        var gurhan = Contact("Gürhan");

        var call = _repo.InsertCall(new Call
        {
            ContactId = gurhan, App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddDays(-1),
            Duration = TimeSpan.FromMinutes(1), State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, gurhan);

        List<Segment> lines =
        [
            new()
            {
                CallId = call, IsMe = true, StartMs = 0, EndMs = 6000,
                Text = $"{Swear} dedim", LowConfidence = true,
            },
        ];

        _repo.ReplaceSegments(call, lines);
        _repo.SaveTranscriptVersion(call, "nova-3", 0.9, lines);
        HabitRecount.Run(_repo, call);

        var page = Page();

        Assert.Single(page.Moments);
        Assert.True(page.Moments[0].IsUncertain);
        Assert.Equal(Rate(0), CardFor(page, HabitMetric.Profanity).Value);
        Assert.NotNull(page.UncertainNote);
    }

    /// <summary>
    /// Goes red when a figure this page has no moments for shows an empty list rather than
    /// saying the figure comes from the conversation as a whole.
    /// </summary>
    [Fact]
    public void AFigureWithoutMomentsSaysSo()
    {
        var gurhan = Contact("Gürhan");
        Call(gurhan, DateTimeOffset.Now.AddDays(-2), "nova-3", [$"{Swear} dedim"]);

        var page = Page();
        page.Metric = HabitMetric.TalkShare;

        Assert.Empty(page.Moments);
        Assert.NotNull(page.MomentsNote);
    }

    /// <summary>
    /// Goes red when the fillers card is counted per minute instead of per hundred words — the
    /// wrong denominator was the first design and it made a short call look like a habit.
    /// </summary>
    [Fact]
    public void FillersAreCountedPerHundredWords()
    {
        var gurhan = Contact("Gürhan");

        // Ten words, one of them a filler: one per hundred words is ten.
        Call(gurhan, DateTimeOffset.Now.AddDays(-2), "nova-3",
            [$"{Filler} bir iki üç dört beş altı yedi sekiz dokuz"]);

        var page = Page();

        Assert.Equal(MirrorViewModel.Format(HabitMetric.Filler, 10), CardFor(page, HabitMetric.Filler).Value);
    }
}
