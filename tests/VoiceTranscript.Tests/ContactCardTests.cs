using System.Reflection;
using System.Text.Json;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The contact card: what one screen may say about a person, and what it may not.
///
/// The repository's own queries are tested elsewhere (<see cref="ContactPatternsTests"/>,
/// <see cref="FigureJourneyTests"/>, <see cref="ContactTrendTests"/>). What is under test here is
/// the card's own conduct — the four rules that make it evidence rather than an opinion:
///
/// 1. every figure carries the denominator that produced it;
/// 2. a kind the user has turned down too often loses its bar and keeps its quotes;
/// 3. a model's label wears a badge, filters apart, and is never pooled with a rule's count;
/// 4. nothing on it is a score.
///
/// Each of these is a way the screen could quietly start asserting more than it knows.
/// </summary>
public sealed class ContactCardTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-card-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;
    private readonly long _contact;

    public ContactCardTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private long Call(int daysAgo, CallKind kind = CallKind.OneToOne, CallDirection direction = CallDirection.Incoming)
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddDays(-daysAgo),
            Kind = kind,
            Direction = direction,
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);
        return call;
    }

    /// <summary>Stored talk figures for one call — what makes it a "measured" one.</summary>
    private void Measured(long call, int mineMs, int theirsMs, int theirCuts = 0) =>
        _repo.SaveHabits(call, 1, new HabitSnapshot(
            new HabitReport(), new TalkStats(mineMs, theirsMs, 0, theirCuts, 0, 0, null, null, 0)).ToJson());

    private long Flag(long call, FlagKind kind, string quote, int ms, string source) =>
        _repo.InsertFlag(new Flag
        {
            CallId = call,
            ContactId = _contact,
            Kind = kind,
            Summary = "özet",
            Quote = quote,
            QuoteStartMs = ms,
            Source = source,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private void Claim(long call, string entity, string attribute, string value, decimal? numeric, int ms) =>
        _repo.InsertClaim(new Claim
        {
            CallId = call,
            ContactId = _contact,
            ByMe = false,
            Quote = $"{entity} {value}",
            QuoteStartMs = ms,
            Entity = entity,
            Attribute = attribute,
            Value = value,
            NumericValue = numeric,
            Unit = numeric is null ? null : "TL",
        });

    private ContactCardViewModel Card() => new(_repo, _contact);

    /// <summary>The card with a model behind it, so the opt-in panel is reachable.</summary>
    private ContactCardViewModel Card(VoiceTranscript.App.Services.ModelAccess access) =>
        new(_repo, _contact, access);

    // ---- Gidişat -----------------------------------------------------------------------------

    /// <summary>
    /// A metric measured on some of the conversations says so, in the same row.
    ///
    /// Red means the card is showing "%68" over two calls of which one was never counted, with
    /// nothing on screen to say the other was not — a figure that claims to speak for a history
    /// it never read. "Not measured" is not zero, and the denominator is how the card says so.
    /// </summary>
    [Fact]
    public void ATrendRowCarriesTheDenominatorThatProducedIt()
    {
        var measured = Call(daysAgo: 3);
        Call(daysAgo: 5);                       // analysed before the counts existed

        Measured(measured, mineMs: 60_000, theirsMs: 40_000);

        var talk = Card().Trend.Single(r => r.Metric == ContactMetric.TalkShare);

        Assert.True(talk.HasDenominator);
        Assert.Contains("1/2", talk.Denominator);

        // The count of the calls themselves needs no denominator: every call is one.
        var calls = Card().Trend.Single(r => r.Metric == ContactMetric.Calls);
        Assert.False(calls.HasDenominator);
    }

    /// <summary>
    /// Group calls are left out of the series, and their number is printed.
    ///
    /// Red means a recording in which every remote voice arrived on one mixed channel is being
    /// counted as a conversation with this person (§7-14) — or is being dropped in silence,
    /// which makes somebody look less present than they were with no way to tell.
    /// </summary>
    [Fact]
    public void GroupCallsAreExcludedAndSaidSo()
    {
        Call(daysAgo: 2);
        Call(daysAgo: 4);
        Call(daysAgo: 6, kind: CallKind.Group);

        var card = Card();

        Assert.NotNull(card.GroupCallsNote);
        Assert.Contains("1", card.GroupCallsNote);

        var calls = card.Trend.Single(r => r.Metric == ContactMetric.Calls);
        Assert.Equal("2 görüşme", calls.Recent);
    }

    /// <summary>
    /// The line skips the months nobody measured rather than drawing them at the floor.
    ///
    /// Red means a gap in the history has become a visible collapse to zero — the same lie the
    /// denominator exists to prevent, told in a shape instead of a number.
    /// </summary>
    [Fact]
    public void TheSparklineLeavesUnmeasuredMonthsOutAndPutsTheHighestOnTop()
    {
        var points = ContactCardViewModel.Sparkline([1.0, null, 3.0], width: 90, height: 20);

        Assert.Equal(2, points.Count);
        Assert.Equal(0, points[0].X);
        Assert.Equal(20, points[0].Y);        // the smallest value sits at the bottom
        Assert.Equal(90, points[1].X);
        Assert.Equal(0, points[1].Y);         // and the largest at the top

        // A flat series divides by no range; it runs down the middle.
        var flat = ContactCardViewModel.Sparkline([2.0, 2.0], width: 90, height: 20);
        Assert.All(flat, p => Assert.Equal(10, p.Y));
    }

    // ---- Kalıplar ----------------------------------------------------------------------------

    /// <summary>
    /// A kind the user has turned down more than three times in ten loses its bar. Every quote
    /// it rests on stays on screen.
    ///
    /// Red one way and the card is drawing a count of findings its own reader has rejected as
    /// though the count still meant something. Red the other way and the sentences went with the
    /// bar — but the sentences were said, whatever the label was worth, and they are the only
    /// part of the row the machine did not decide.
    /// </summary>
    [Fact]
    public void AKindPastTheDismissalCeilingLosesItsBarAndKeepsItsQuotes()
    {
        var call = Call(daysAgo: 1);

        var first = Flag(call, FlagKind.ScamPattern, "Hesabınız kapanacak", 1_000, Core.Domain.Flag.Sources.Pipeline);
        var second = Flag(call, FlagKind.ScamPattern, "Bankadan arıyorum", 5_000, Core.Domain.Flag.Sources.Pipeline);
        Flag(call, FlagKind.ScamPattern, "Kartı okutmanız gerek", 9_000, Core.Domain.Flag.Sources.Pipeline);

        Flag(call, FlagKind.EvadedQuestion, "Onu sonra konuşuruz", 20_000, Core.Domain.Flag.Sources.Pipeline);

        _repo.DismissFlag(first);
        _repo.DismissFlag(second);

        var card = Card();

        // Two of three turned down: past the ceiling, so no bar — and the count is still said.
        var scam = card.Patterns.Single(p => p.Kind == nameof(FlagKind.ScamPattern));
        Assert.False(scam.HasBar);
        Assert.True(scam.DismissalRate > PatternRow.DismissalCeiling);
        Assert.NotNull(scam.BarDroppedText);

        card.ToggleQuotesCommand.Execute(scam);
        Assert.NotEmpty(scam.Quotes);

        // Nothing turned down: the bar stands.
        var evaded = card.Patterns.Single(p => p.Kind == nameof(FlagKind.EvadedQuestion));
        Assert.True(evaded.HasBar);
        Assert.Null(evaded.BarDroppedText);

        Assert.Contains("2", card.DismissedNote);
    }

    /// <summary>
    /// A model's label is badged, filters apart from a deterministic finding, and never joins its
    /// count.
    ///
    /// This is §3.1 made operable: the two grounds may sit on one screen and never inside one
    /// number. Red means the card has started pooling a rule's count with an assessment's — at
    /// which point one is borrowing the other's standing and no reader can tell which.
    /// </summary>
    [Fact]
    public void ATacticRowIsBadgedAndFiltersApartFromARuleFlag()
    {
        var call = Call(daysAgo: 1);

        Flag(call, FlagKind.PressureTactic, "Bugün karar vermen lazım", 1_000, Core.Domain.Flag.Sources.Pipeline);

        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "Yarın fiyat değişir", QuoteStartMs = 8_000 },
        ]);

        var card = Card();

        Assert.Equal(2, card.Patterns.Count);
        Assert.All(card.Patterns, p => Assert.Equal(1, p.Summary.Total));

        var rule = card.Patterns.Single(p => p.Kind == nameof(FlagKind.PressureTactic));
        var model = card.Patterns.Single(p => p.Kind == "baski");

        Assert.False(rule.IsModelLabel);
        Assert.True(model.IsModelLabel);

        // "Kural" cannot put a model's label on the screen at all.
        card.SetSourceCommand.Execute(ContactCardViewModel.SourceRule);
        Assert.Equal(nameof(FlagKind.PressureTactic), Assert.Single(card.Patterns).Kind);

        // And "Değerlendirme" shows nothing else.
        card.SetSourceCommand.Execute(ContactCardViewModel.SourceAssessment);
        Assert.True(Assert.Single(card.Patterns).IsModelLabel);

        card.SetSourceCommand.Execute(ContactCardViewModel.SourceAll);
        Assert.Equal(2, card.Patterns.Count);
    }

    /// <summary>
    /// Turning a quote down removes it; the notice's "Geri al" brings it back.
    ///
    /// Red means either that a refusal did not take — the user rejected a finding and it is still
    /// being counted at them — or that it cannot be taken back, which makes a one-click judgement
    /// permanent. Both are the reason every ruling in this product is a tombstone.
    /// </summary>
    [Fact]
    public void DismissingAQuoteRemovesItAndTheUndoBringsItBack()
    {
        var call = Call(daysAgo: 1);

        Flag(call, FlagKind.EvadedQuestion, "Onu sonra konuşuruz", 1_000, Core.Domain.Flag.Sources.Pipeline);
        Flag(call, FlagKind.EvadedQuestion, "Şimdi ona girmeyelim", 5_000, Core.Domain.Flag.Sources.Pipeline);

        var card = Card();
        var row = Assert.Single(card.Patterns);

        card.ToggleQuotesCommand.Execute(row);
        Assert.Equal(2, row.Quotes.Count);

        card.DismissQuoteCommand.Execute(row.Quotes.First(q => q.Text == "Onu sonra konuşuruz"));

        // The row is re-read and stays open; the sentence is gone from it and from the count.
        var after = Assert.Single(card.Patterns);
        Assert.True(after.IsExpanded);
        Assert.Equal(1, after.Summary.Total);
        Assert.DoesNotContain(after.Quotes, q => q.Text == "Onu sonra konuşuruz");
        Assert.NotNull(card.Undo.Notice);
        Assert.True(card.Undo.CanUndo);

        card.Undo.UndoCommand.Execute(null);

        var restored = Assert.Single(card.Patterns);
        Assert.Equal(2, restored.Summary.Total);
        Assert.Null(card.Undo.Notice);
    }

    /// <summary>
    /// The same two verbs reach a model's label, which lives in another table.
    ///
    /// Red means the card grew a dismissal of its own that no other screen hears about, and a
    /// row the user turned down here is still counted wherever else it appears.
    /// </summary>
    [Fact]
    public void ATacticQuoteIsDismissedAndRestoredThroughTheSameVerbSet()
    {
        var call = Call(daysAgo: 1);

        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "kacamak", Quote = "Onu sonra konuşuruz", QuoteStartMs = 1_000 },
            new TacticEvidence { CallId = call, Tactic = "kacamak", Quote = "Şimdi ona girmeyelim", QuoteStartMs = 5_000 },
        ]);

        var card = Card();
        var row = Assert.Single(card.Patterns);

        card.ToggleQuotesCommand.Execute(row);
        card.DismissQuoteCommand.Execute(row.Quotes[0]);

        Assert.Equal(1, Assert.Single(card.Patterns).Summary.Total);

        card.Undo.UndoCommand.Execute(null);
        Assert.Equal(2, Assert.Single(card.Patterns).Summary.Total);
    }

    /// <summary>
    /// Listening to a moment and ruling on it moves "M/N dinlendi" and leaves the list open.
    ///
    /// Red means the user's own verdict — the only figure on this card nobody but them can
    /// produce — did not reach the row it was about, or closed the list they were working
    /// through to reach it.
    /// </summary>
    [Fact]
    public void AListeningVerdictIsCountedOnTheRowAndKeepsTheListOpen()
    {
        var call = Call(daysAgo: 1);
        Flag(call, FlagKind.EvadedQuestion, "Onu sonra konuşuruz", 1_000, Core.Domain.Flag.Sources.Pipeline);

        var card = Card();
        var row = Assert.Single(card.Patterns);

        card.ToggleQuotesCommand.Execute(row);
        Assert.Equal(0, row.Summary.Listened);

        card.CorrectCommand.Execute(row.Quotes[0]);

        var after = Assert.Single(card.Patterns);
        Assert.True(after.IsExpanded);
        Assert.Equal(1, after.Summary.Listened);
        Assert.Equal(1, after.Summary.Correct);
        Assert.NotNull(after.Quotes[0].VerdictText);
    }

    // ---- Rakam yolculuğu ---------------------------------------------------------------------

    /// <summary>
    /// Only the subjects that were given more than one answer appear.
    ///
    /// Red means the card is presenting a figure somebody stated consistently as a "journey" —
    /// the shape of a changed story, drawn over a story that never changed.
    /// </summary>
    [Fact]
    public void TheFigureJourneyListsOnlySubjectsWithTwoOrMoreValues()
    {
        var first = Call(daysAgo: 30);
        var second = Call(daysAgo: 10);

        Claim(first, "kira", "tutar", "15.000", 15000m, 1_000);
        Claim(second, "kira", "tutar", "18.000", 18000m, 2_000);

        // Said twice, the same both times: not a journey.
        Claim(first, "depozito", "tutar", "5.000", 5000m, 3_000);
        Claim(second, "depozito", "tutar", "5.000", 5000m, 4_000);

        var journey = Assert.Single(Card().Journeys);

        Assert.Equal("kira · tutar", journey.Subject);
        Assert.Equal(2, journey.Journey.DistinctValues);
        Assert.Equal(2, journey.Stops.Count);

        // The arrow sits between the stops and not after the last one.
        Assert.Equal("→", journey.Stops[0].Separator);
        Assert.Equal("", journey.Stops[1].Separator);
    }

    // ---- what the card refuses to say --------------------------------------------------------

    /// <summary>
    /// Nothing the card exposes is a score.
    ///
    /// Checked against the public surface rather than the screen, because that is where such a
    /// thing would be added: one property called "TrustScore" or "RiskLevel" and every binding
    /// that wants it is a line of markup away. §7-1 and §7-4 are absolute and apply at the
    /// person level too, so the vocabulary is refused outright — in the members and in both
    /// dictionaries.
    ///
    /// Red means somebody added a figure that ranks a human being. Shares of measured quantities
    /// — of speaking time, of answered questions, of the user's own rulings — are not that, and
    /// each of them arrives with the count it was computed over.
    /// </summary>
    [Fact]
    public void NothingOnTheCardIsAScore()
    {
        string[] forbidden =
        [
            "score", "skor", "puan", "trust", "guven", "güven", "risk",
            "reliab", "rating", "grade", "severity", "danger", "tehlike",
        ];

        Type[] surfaces =
        [
            typeof(ContactCardViewModel), typeof(PatternRow), typeof(PatternQuoteRow),
            typeof(TrendRow), typeof(JourneyRow), typeof(JourneyStop),
            typeof(OwnWordRow), typeof(OwnWordsRow), typeof(CardPromise),
        ];

        var offenders = surfaces
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => $"{t.Name}.{m.Name}"))
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, "Kişi kartında puan görünümlü üye: " + string.Join(", ", offenders));

        // And not smuggled in through the words instead of the members.
        foreach (var code in new[] { "tr", "en" })
        {
            var path = Path.Combine(
                Root(), "src", "VoiceTranscript.Core", "Resources", $"strings.{code}.json");

            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

            var wording = strings
                .Where(pair => pair.Key.StartsWith("contactcard.", StringComparison.Ordinal))
                .Where(pair => forbidden.Any(word => pair.Value.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Select(pair => $"{code}:{pair.Key}")
                .ToList();

            Assert.True(wording.Count == 0, "Kişi kartı metninde puan dili: " + string.Join(", ", wording));
        }
    }

    /// <summary>
    /// The card says out loud that there is no "findings per month" series, where a reader would
    /// look for one.
    ///
    /// Red means §7-11's refusal has become invisible: a missing line reads as an oversight, and
    /// the next person to notice it adds the series back.
    /// </summary>
    [Fact]
    public void TheAbsentFindingDensitySeriesIsExplainedRatherThanMerelyMissing()
    {
        Assert.DoesNotContain(
            Card().Trend, r => r.Label.Contains("yoğunluk", StringComparison.OrdinalIgnoreCase));

        var caption = Core.Text.Localisation.T("contactcard.bulgu-yogunlugu-serisi-yok");

        Assert.NotEqual("contactcard.bulgu-yogunlugu-serisi-yok", caption);
        Assert.Contains("denetim", caption, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the model's opinion (opt-in) ----------------------------------------------------------

    private static VoiceTranscript.App.Services.ModelAccess Access(
        Core.Configuration.AppSettings settings, Action<Core.Configuration.AppSettings>? save = null)
    {
        var current = settings;

        return new VoiceTranscript.App.Services.ModelAccess(
            () => current,
            saved => { current = saved; save?.Invoke(saved); },
            new HttpClient());
    }

    /// <summary>One stored reading, written the way the analysis writes it.</summary>
    private long StoreOpinion(long contactId, string text, string anchorLabel, int startMs, string hash)
    {
        var report = new ContactReadingReport(
            new ContactReadingItem(text, [new ContactReadingAnchor(anchorLabel, 1, startMs, false, "alıntı")]),
            [], [], [], [], [], [], [],
            "Aynı kayıtlar sıradan bir iş yoğunluğuyla da açıklanabilir.",
            CallsCovered: 3, ExcerptCount: 24, RejectedCount: 1, Insufficient: false);

        return _repo.SaveContactReading(
            contactId, JsonSerializer.Serialize(report), "qwen-test", 3, null, hash, 24, 1);
    }

    /// <summary>
    /// Switched off, the panel is one line saying where the switch is — not silence.
    ///
    /// Red means either that the panel appeared without anybody asking for it, which is the one
    /// thing an opt-in surface may never do, or that "off" renders as nothing at all: a reader who
    /// has heard the panel exists then reads the gap as a feature that failed to load, and the
    /// card loses the chance to say that the ground below the evidence is a different one.
    /// </summary>
    [Fact]
    public void TheOpinionPanelIsOffUntilSomebodyTurnsItOn()
    {
        StoreOpinion(_contact, "Bir izlenim.", "A1", 1_000, "hash");

        // No model behind the card at all, and with a model but the switch off.
        Assert.False(Card().OpinionEnabled);
        Assert.Empty(Card().Opinion);

        var off = Card(Access(new Core.Configuration.AppSettings()));
        Assert.False(off.OpinionEnabled);
        Assert.Empty(off.Opinion);
        Assert.Null(off.OpinionSignature);

        // The off line says where to turn it on — read from the dictionaries rather than through
        // the ambient language, which another test may have switched.
        foreach (var (code, word) in new[] { ("tr", "Ayarlar"), ("en", "Settings") })
        {
            Assert.Contains(word, Strings(code)["contactcard.modelin-gorusu-kapali"], StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>One dictionary, straight off disk: language-independent, unlike Localisation.T.</summary>
    private static Dictionary<string, string> Strings(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(
                Root(), "src", "VoiceTranscript.Core", "Resources", $"strings.{code}.json")))!;

    /// <summary>
    /// Turned on, the panel shows the stored reading, signed and dated, with its anchors playable
    /// and its staleness visible.
    ///
    /// Red means one of the three things that keep this from being a machine asserting facts about
    /// a person: the signature (which model, when, over how much, how much was dropped), the
    /// anchors (every line playable back to the moment it rests on), or the staleness note that
    /// stops an old impression being read as a current one.
    /// </summary>
    [Fact]
    public void AStoredOpinionIsSignedAnchoredAndKnowsWhenItWentOld()
    {
        var call = Call(daysAgo: 2);
        StoreOpinion(_contact, "Konuyu tarihe bağlamadan bırakma izlenimi veriyor.", "A1", 12_000, "eski-hash");

        var card = Card(Access(new Core.Configuration.AppSettings { ContactReadingEnabled = true }));

        Assert.True(card.OpinionEnabled);
        Assert.True(card.HasOpinion);

        var line = Assert.Single(Assert.Single(card.Opinion).Lines);
        Assert.Equal("Konuyu tarihe bağlamadan bırakma izlenimi veriyor.", line.Text);

        var anchor = Assert.Single(line.Anchors);
        Assert.Contains("A1", anchor.Label, StringComparison.Ordinal);
        Assert.Equal(12_000, anchor.StartMs);

        // model · date · calls · excerpts · dropped.
        Assert.Contains("qwen-test", card.OpinionSignature!, StringComparison.Ordinal);
        Assert.Contains("24", card.OpinionSignature!, StringComparison.Ordinal);

        // The stored fingerprint is not this archive's, so the panel says the reading is old.
        Assert.True(card.OpinionIsStale);
        Assert.NotNull(card.OpinionCounterReading);

        // And with the hash of the history it actually covers, it is not.
        StoreOpinion(_contact, "Aynı izlenim.", "A1", 12_000, ContactReadingAnalysis.InputHash([call]));
        Assert.False(Card(Access(new Core.Configuration.AppSettings { ContactReadingEnabled = true })).OpinionIsStale);
    }

    /// <summary>
    /// [Katılmıyorum] is written down, and three people in a row switch the feature off.
    ///
    /// This is the package's own rollback condition, made operable. Red one way and a rejection
    /// does not stick, so the feature can never fail its own measurement; red the other way and it
    /// switches itself off over a single bad reading, which would take a working feature away from
    /// somebody who never asked for that.
    /// </summary>
    [Fact]
    public void ThreeRejectedReadingsInARowSwitchTheFeatureOff()
    {
        Core.Configuration.AppSettings? saved = null;
        var access = Access(new Core.Configuration.AppSettings { ContactReadingEnabled = true }, s => saved = s);

        StoreOpinion(_contact, "Bir izlenim.", "A1", 1_000, "hash");

        var first = Card(access);
        first.DisagreeWithOpinionCommand.Execute(null);

        Assert.True(first.OpinionRejected);
        Assert.Equal(ContactReadingAnalysis.Disagree, _repo.LatestContactReading(_contact)!.UserVerdict);

        // One person is one opinion: the feature stays on.
        Assert.Null(saved);

        foreach (var name in new[] { "Avukat", "Uliana" })
        {
            var other = _repo.UpsertContact(name, CallApp.WhatsApp);
            StoreOpinion(other, "Bir izlenim.", "A1", 1_000, "hash");

            new ContactCardViewModel(_repo, other, access).DisagreeWithOpinionCommand.Execute(null);
        }

        Assert.NotNull(saved);
        Assert.False(saved!.ContactReadingEnabled);
        Assert.True(saved.ContactReadingMeasuredNegative);

        // And the panel is gone the next time the card is built.
        Assert.False(Card(access).OpinionEnabled);
    }

    /// <summary>
    /// The panel refuses the same three vocabularies in its surface that the card refuses in its
    /// figures: a score, a psychological or emotional state, and arguments to use on somebody.
    ///
    /// Member names rather than markup, for the same reason <see cref="NothingOnTheCardIsAScore"/>
    /// checks members: one property called <c>MoodLine</c> or <c>PersuasionTips</c> and every
    /// binding that wants it is one line of XAML away. The two boundaries are also asserted to be
    /// SAID — a refusal nobody can read is the kind that gets "fixed" by the next person who
    /// notices the gap.
    ///
    /// Red means the opinion panel has started offering what the user themselves excluded when
    /// they allowed impressions (§7-1, §7-4, §7-5).
    /// </summary>
    [Fact]
    public void TheOpinionPanelOffersNoStateAndNoArguments()
    {
        string[] forbidden =
        [
            "score", "skor", "puan", "trust", "guven", "risk", "rating", "grade",
            "psycholog", "psikolojik", "emotion", "duygu", "mood", "ruhhal",
            "argument", "arguman", "persuad", "ikna", "manipul", "leverage", "tactic",
        ];

        Type[] surfaces =
        [
            typeof(OpinionSection), typeof(OpinionLineRow), typeof(OpinionAnchorRow),
            typeof(ContactReadingReport), typeof(ContactReadingItem), typeof(ContactReadingAnchor),
        ];

        var offenders = surfaces
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => $"{t.Name}.{m.Name}"))
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, "Görüş panelinde yasak sözcük taşıyan üye: " + string.Join(", ", offenders));

        // Also on the view model's own surface: the panel's properties all begin with "Opinion".
        var panel = typeof(ContactCardViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(panel.Count == 0, "Kart yüzeyinde yasak sözcük: " + string.Join(", ", panel));

        // And the two boundaries are written where the reader is, in both dictionaries.
        foreach (var (code, state, argument, pointer) in new[]
                 {
                     ("tr", "psikolojik", "argüman", "Elindeki kayıtlar"),
                     ("en", "psychological", "argument", "What you have on record"),
                 })
        {
            var strings = Strings(code);

            Assert.Contains(state, strings["contactcard.okuma-psikolojik-durum-verilmiyor"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(argument, strings["contactcard.okuma-argumanlar-yazilmiyor"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(pointer, strings["contactcard.okuma-argumanlar-yazilmiyor"], StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
