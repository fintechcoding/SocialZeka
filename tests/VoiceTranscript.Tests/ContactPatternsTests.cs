using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// "Kalıplar": everything the archive has counted against one person, by kind and by who counted
/// it.
///
/// The card puts a deterministic check and a model's label in the same list, so the properties
/// that keep it honest are the ones under test here: the source is never pooled away, a
/// dismissed row leaves the count without disappearing, an uncertain quote is counted apart, and
/// the "M/N dinlendi" figure finds the user's verdicts by the words rather than by a row id that
/// a re-run has already changed.
/// </summary>
public sealed class ContactPatternsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-pat-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;
    private readonly long _contact;

    public ContactPatternsTests()
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

    private long Call(DateTimeOffset at)
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = at,
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);
        return call;
    }

    private long Flag(long call, FlagKind kind, string quote, int ms, string source, bool lowConfidence = false) =>
        _repo.InsertFlag(new Flag
        {
            CallId = call,
            ContactId = _contact,
            Kind = kind,
            Summary = "özet",
            Quote = quote,
            QuoteStartMs = ms,
            Source = source,
            LowConfidence = lowConfidence,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private void Listened(long call, string quote, int ms, VerdictValue value) =>
        _repo.SaveVerdict(new Verdict
        {
            CallId = call,
            Kind = VerdictKind.Pattern,
            QuoteFolded = TurkishText.NormalizeForSearch(quote),
            StartMs = ms,
            Value = value,
            DecidedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>
    /// One row per kind AND per source; the flags and the tactic quotes share a list without
    /// sharing a total.
    ///
    /// Red means the card has started pooling a deterministic count with a model's label, which
    /// lets one borrow the other's standing — the reason the source filter exists at all.
    /// </summary>
    [Fact]
    public void KindAndSourceAreCountedApart()
    {
        var first = Call(DateTimeOffset.UtcNow.AddDays(-3));
        var second = Call(DateTimeOffset.UtcNow.AddDays(-1));

        Flag(first, FlagKind.EvadedQuestion, "Onu sonra konuşuruz abi", 12_000, Core.Domain.Flag.Sources.Pipeline);
        Flag(second, FlagKind.EvadedQuestion, "Şimdi ona girmeyelim", 3_000, Core.Domain.Flag.Sources.Pipeline);
        Flag(second, FlagKind.PressureTactic, "Bugün karar vermen lazım", 30_000, Core.Domain.Flag.Sources.Consistency);

        _repo.ReplaceTacticEvidence(second, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = second, Tactic = "kacamak", Quote = "Onu sonra konuşuruz abi", QuoteStartMs = 40_000 },
        ]);

        var patterns = _repo.ContactPatterns(_contact);

        var evaded = patterns.Single(p => p.Kind == nameof(FlagKind.EvadedQuestion));
        Assert.Equal(Core.Domain.Flag.Sources.Pipeline, evaded.Source);
        Assert.Equal(2, evaded.Total);
        Assert.Equal(2, evaded.Calls);

        var pressure = patterns.Single(p => p.Kind == nameof(FlagKind.PressureTactic));
        Assert.Equal(Core.Domain.Flag.Sources.Consistency, pressure.Source);
        Assert.Equal(1, pressure.Total);

        // The same sentence under a model's label is its own row, not added to the ledger's.
        var tactic = patterns.Single(p => p.Kind == "kacamak");
        Assert.Equal(TacticEvidence.Sources.Deception, tactic.Source);
        Assert.Equal(1, tactic.Total);
    }

    /// <summary>
    /// A dismissed row leaves the bar and stays visible as a number.
    ///
    /// Red either way: counted, and the card is showing a finding the user rejected; vanished,
    /// and "reddettiklerin (3) sayılmaz" has nothing to put in the brackets.
    /// </summary>
    [Fact]
    public void DismissedRowsAreOutOfTheTotalAndCountedSeparately()
    {
        var call = Call(DateTimeOffset.UtcNow);

        var kept = Flag(call, FlagKind.ScamPattern, "Hesabınız kapanacak, hemen arayın", 1_000, Core.Domain.Flag.Sources.Pipeline);
        Flag(call, FlagKind.ScamPattern, "Bankadan arıyorum efendim", 5_000, Core.Domain.Flag.Sources.Pipeline);

        _repo.DismissFlag(kept);

        var row = Assert.Single(_repo.ContactPatterns(_contact));
        Assert.Equal(1, row.Total);
        Assert.Equal(1, row.Dismissed);

        // And the quotes behind the row follow the same rule, unless asked otherwise.
        Assert.Single(_repo.PatternRows(_contact, nameof(FlagKind.ScamPattern), Core.Domain.Flag.Sources.Pipeline));
        Assert.Equal(2, _repo.PatternRows(
            _contact, nameof(FlagKind.ScamPattern), Core.Domain.Flag.Sources.Pipeline, includeDismissed: true).Count);
    }

    /// <summary>
    /// A quote from audio the transcriber doubted is counted, and marked.
    ///
    /// Red means the card can no longer tell a clear sentence from a possibly misheard one, and
    /// the grey row that says so has nothing to filter on.
    /// </summary>
    [Fact]
    public void UncertainAudioIsCountedButFlaggedAsUncertain()
    {
        var call = Call(DateTimeOffset.UtcNow);

        Flag(call, FlagKind.VagueShift, "Yani şey işte, bakarız", 1_000, Core.Domain.Flag.Sources.Consistency);
        Flag(call, FlagKind.VagueShift, "Bir şekilde hallolur", 9_000, Core.Domain.Flag.Sources.Consistency, lowConfidence: true);

        var row = Assert.Single(_repo.ContactPatterns(_contact));
        Assert.Equal(2, row.Total);
        Assert.Equal(1, row.LowConfidence);

        var quotes = _repo.PatternRows(_contact, nameof(FlagKind.VagueShift), Core.Domain.Flag.Sources.Consistency);
        Assert.Single(quotes, q => q.LowConfidence);

        // A flag has never recorded which stream its quote came from, and the card says so
        // rather than guessing a side.
        Assert.All(quotes, q => Assert.Null(q.ByMe));
    }

    /// <summary>
    /// "7/9 dinlendi" is matched by the words and the millisecond, never by a row id.
    ///
    /// Red means a re-run that renumbered the rows has detached the user's own verdicts from the
    /// sentences they were about — the failure the verdict table was keyed this way to avoid.
    /// </summary>
    [Fact]
    public void TheListeningFiguresSurviveARenumbering()
    {
        var call = Call(DateTimeOffset.UtcNow);

        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "Bugün imzalaman lazım", QuoteStartMs = 10_000 },
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "Yarın fiyat değişir", QuoteStartMs = 20_000 },
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "Başkası alacak sonra", QuoteStartMs = 30_000 },
        ]);

        // Listened to two of the three: one confirmed, one refused. The third was never heard.
        Listened(call, "Bugün imzalaman lazım", 10_400, VerdictValue.Correct);
        Listened(call, "Yarın fiyat değişir", 19_800, VerdictValue.NotThat);

        // A verdict about a different sentence at the same second is not this row's verdict.
        Listened(call, "başka bir cümle tamamen", 30_100, VerdictValue.Correct);

        // Rewriting the evidence gives every row a new id; the verdicts stay attached.
        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "Bugün imzalaman lazım", QuoteStartMs = 10_000 },
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "Yarın fiyat değişir", QuoteStartMs = 20_000 },
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "Başkası alacak sonra", QuoteStartMs = 30_000 },
        ]);

        var row = Assert.Single(_repo.ContactPatterns(_contact));
        Assert.Equal(3, row.Total);
        Assert.Equal(2, row.Listened);
        Assert.Equal(1, row.Correct);
    }

    /// <summary>
    /// The date beside a row is the last conversation it came out of, and the quotes behind it
    /// come newest first.
    ///
    /// Red means "son: 02 Eyl 06:41" is naming some other moment than the one clicking it plays.
    /// </summary>
    [Fact]
    public void TheDateIsTheMostRecentConversationTheRowCameFrom()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var recent = DateTimeOffset.UtcNow.AddDays(-2);

        var older = Call(old);
        var newer = Call(recent);

        Flag(older, FlagKind.MovedDeadline, "Haftaya kesin gönderirim", 1_000, Core.Domain.Flag.Sources.Pipeline);
        Flag(newer, FlagKind.MovedDeadline, "Bu hafta olmadı, gelecek hafta", 2_000, Core.Domain.Flag.Sources.Pipeline);

        var row = Assert.Single(_repo.ContactPatterns(_contact));
        Assert.NotNull(row.Last);
        Assert.Equal(recent.ToUnixTimeSeconds(), row.Last!.Value.ToUnixTimeSeconds());

        var quotes = _repo.PatternRows(_contact, nameof(FlagKind.MovedDeadline), Core.Domain.Flag.Sources.Pipeline);
        Assert.Equal(newer, quotes[0].CallId);
        Assert.Equal(older, quotes[1].CallId);
    }

    /// <summary>A person with nothing counted against them gets an empty list, not a row of zeros.</summary>
    [Fact]
    public void APersonWithNoFindingsHasNoRows()
    {
        Call(DateTimeOffset.UtcNow);
        Assert.Empty(_repo.ContactPatterns(_contact));
    }
}
