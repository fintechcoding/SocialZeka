using System.Net.Http;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>One scripted turn: what the model says, or how the provider fails.</summary>
file sealed record Turn(string? Reply = null, string? Failure = null)
{
    public static Turn Says(string json) => new(Reply: json);

    /// <summary>The provider refusing — a 429, an expired key, an exhausted quota.</summary>
    public static Turn Fails(string message) => new(Failure: message);
}

/// <summary>
/// A model that can also fail the way a real one does.
///
/// The existing scripted client always answers, which is why a provider error partway through a
/// conversation was never exercised — and it is not a rare case: the account behind this build
/// hit insufficient_quota in the middle of a run.
/// </summary>
file sealed class Provider(params Turn[] turns) : ILlmClient
{
    private int _index;

    public List<LlmRequest> Requests { get; } = [];

    public int UnloadCalls { get; private set; }

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        var turn = _index < turns.Length ? turns[_index++] : Turn.Says("{}");

        return turn.Failure is { } why
            ? Task.FromException<LlmResponse>(new LlmException(why))
            : Task.FromResult(new LlmResponse(turn.Reply!, "stop", 100, 50));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task UnloadAsync(string model, CancellationToken cancellationToken = default)
    {
        UnloadCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A model that answers once and then loses the connection.
///
/// The failure the pipeline does NOT absorb: a dropped socket is not a section it can mark
/// unreadable, so it leaves through the caller — which is where the failure gets recorded, and
/// where the tokens already spent have to come from.
/// </summary>
file sealed class Collapsing(params Turn[] turns) : ILlmClient
{
    private readonly Provider _inner = new(turns);
    private int _index;

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => _index++ < turns.Length
            ? _inner.CompleteAsync(request, cancellationToken)
            : Task.FromException<LlmResponse>(new HttpRequestException("bağlantı koptu"));

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// What an analysis costs, and what survives one that goes wrong halfway.
///
/// Two things are being protected here and they pull in the same direction. The usage screen has
/// to state what the database holds — a run that burned twelve paid requests and produced nothing
/// used to leave it reading "0 çalışma, 0 jeton, 0 başarısız", which is a clean history for the
/// one case somebody most needs explained. And paid work must not be thrown away: a provider
/// error on section five used to take sections one to four with it, so the retry after a top-up
/// paid for them again.
/// </summary>
public sealed class AnalysisSpendTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-spend-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public AnalysisSpendTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
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

    /// <summary>
    /// One chunk per line.
    ///
    /// The chunker splits on speaker turns and only when the character budget is exceeded, so a
    /// budget of one token puts every line in its own section — which is how a conversation
    /// becomes several paid requests, and the only way to have one of them fail without the
    /// others.
    /// </summary>
    private static readonly AnalysisOptions Options = new()
    {
        Model = "test-model",
        ChunkTokens = 1,
        AdjudicateContradictions = false,
        WriteSummary = false,
    };

    private long _contact;

    private long Seed(DateTimeOffset startedAt, params (bool me, int ms, string text)[] lines)
    {
        if (_contact == 0) _contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);

        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.Telegram,
            Kind = CallKind.OneToOne,
            StartedAt = startedAt,
            State = ProcessingState.Transcribed,
        });

        _repo.AssignContact(call, _contact);

        _repo.ReplaceSegments(call, lines.Select(l => new Segment
        {
            CallId = call, IsMe = l.me, StartMs = l.ms, EndMs = l.ms + 3000, Text = l.text,
        }));

        return call;
    }

    private static string Promise(string quote, string obligation, string? when = null)
    {
        var date = when is null ? "null" : $"\"{when}\"";

        return $$"""{"taahhutler":[{"alinti":"{{quote}}","yukumluluk":"{{obligation}}","tarih_ham":{{date}},"kosullu":false}],"iddialar":[],"sorular":[],"baski_isaretleri":[]}""";
    }

    /// <summary>A reply that parses and holds one figure, so the run counts as having read its section.</summary>
    private static string Statement(string quote, string entity, string attribute, string value) =>
        $$"""{"taahhutler":[],"iddialar":[{"alinti":"{{quote}}","varlik":"{{entity}}","nitelik":"{{attribute}}","deger":"{{value}}"}],"sorular":[],"baski_isaretleri":[]}""";

    /// <summary>Prose where JSON was asked for — a model that would not take the schema.</summary>
    private const string Prose = "Elbette, bu görüşmede şunlar konuşuldu: evraklar ve fatura.";

    // ---- A: a run that produced nothing still cost something --------------------------------

    /// <summary>
    /// Every section failed, so nothing was written — but three paid requests were made.
    ///
    /// Goes red when the all-failed path returns without recording: the usage screen then reports
    /// zero runs, zero tokens and zero failures about a conversation that has just been billed
    /// for, which is exactly the case the screen exists to describe. It also goes red when that
    /// path stops releasing the model, which used to leave a local backend holding the GPU that
    /// transcription needs back.
    /// </summary>
    [Fact]
    public async Task AnAnalysisWhereEverySectionFailedIsRecordedAsAFailedRunWithItsTokens()
    {
        var call = Seed(DateTimeOffset.UtcNow,
            (true, 0, "Evraklar ne zaman gelir acaba, merak ediyorum."),
            (false, 24_000, "Evrakları cuma günü yollarım, söz veriyorum sana."),
            (false, 48_000, "Faturayı da pazartesi günü keserim, merak etme."));

        var llm = new Provider(Turn.Says(Prose), Turn.Says(Prose), Turn.Says(Prose));

        var report = await new AnalysisPipeline(llm, _repo)
            .AnalyseAsync(call, Options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, llm.Requests.Count);
        Assert.Contains(report.Warnings, w => w.Contains("Hiçbir bölüm çözümlenemedi"));

        var usage = _repo.Usage(ProcessingStage.Analyse);

        Assert.Equal(1, usage.Runs);
        Assert.Equal(1, usage.Failures);
        Assert.Equal(300, usage.PromptTokens);
        Assert.Equal(150, usage.CompletionTokens);

        // The GPU goes back, on this path as on the finished one.
        Assert.Equal(1, llm.UnloadCalls);
    }

    // ---- B: a run that threw still spent something ------------------------------------------

    /// <summary>
    /// A run that dies takes its bookkeeping with it, so the caller records the failure — and it
    /// can only report what the pipeline is willing to tell it.
    ///
    /// The orchestrator files that failure with no token counts at all, which reports everything
    /// spent up to the throw as zero; against a provider that fails intermittently the screen's
    /// total drifts steadily below the real invoice. Goes red when the pipeline stops exposing
    /// what it spent, which is the only honest source for that number — an estimate here would be
    /// a figure nobody could reconcile against a bill.
    /// </summary>
    [Fact]
    public async Task WhatWasSpentBeforeAThrowIsStillReadable()
    {
        var call = Seed(DateTimeOffset.UtcNow,
            (false, 0, "Evrakları cuma günü yollarım, söz veriyorum sana."),
            (false, 24_000, "Faturayı da pazartesi günü keserim, merak etme."));

        // Not an LlmException: a connection dropped mid-run is not a section the pipeline can
        // count as unreadable, so it leaves the way the orchestrator has to catch it.
        var llm = new Collapsing(Turn.Says(Promise("Evrakları cuma günü yollarım", "evrak gönderimi")));
        var pipeline = new AnalysisPipeline(llm, _repo);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => pipeline.AnalyseAsync(call, Options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((100, 50), pipeline.TokensSpent);
    }

    // ---- C: a provider error partway through ------------------------------------------------

    /// <summary>
    /// Section two refused; sections one and three are kept, and all of it is counted.
    ///
    /// Goes red when a provider error is allowed to escape the section loop again — the run then
    /// throws, the two sections already paid for are lost, and the user pays for them a second
    /// time when they retry after topping up their credit.
    /// </summary>
    [Fact]
    public async Task AProviderErrorPartwayThroughKeepsTheSectionsAlreadyPaidFor()
    {
        var call = Seed(DateTimeOffset.UtcNow,
            (false, 0, "Evrakları cuma günü yollarım, söz veriyorum sana."),
            (false, 24_000, "Faturayı da pazartesi günü keserim, merak etme."),
            (false, 48_000, "Parayı ayın onunda hesabına geçireceğim kesinlikle."));

        var llm = new Provider(
            Turn.Says(Promise("Evrakları cuma günü yollarım", "evrak gönderimi")),
            Turn.Fails("insufficient_quota"),
            Turn.Says(Promise("Parayı ayın onunda hesabına geçireceğim", "para transferi")));

        var report = await new AnalysisPipeline(llm, _repo)
            .AnalyseAsync(call, Options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(report.Warnings, w => w.Contains("2. bölüm çözümlenemedi"));

        var stored = _repo.GetOpenCommitments(_contact).Select(c => c.Obligation).ToList();

        Assert.Contains("evrak gönderimi", stored);
        Assert.Contains("para transferi", stored);

        // Two requests were answered and billed; the one that threw reported nothing, and
        // nothing is what it contributes — never a guess.
        var usage = _repo.Usage(ProcessingStage.Analyse);

        Assert.Equal(1, usage.Runs);
        Assert.Equal(200, usage.PromptTokens);
        Assert.Equal(100, usage.CompletionTokens);
    }

    /// <summary>
    /// A partial run adds to the ledger; it never replaces it.
    ///
    /// The first analysis reads all three sections. The second gets a 429 on the middle one, so
    /// the promise that section holds is not in its results — and clearing the call on the
    /// strength of that run would delete a promise nobody has retracted. Goes red both ways:
    /// two rows for one promise means the partial run cleared nothing and de-duplicated nothing,
    /// and a missing promise means it cleared what it could not read.
    /// </summary>
    [Fact]
    public async Task APartialRunNeitherWipesNorDuplicatesACompleteLedger()
    {
        var call = Seed(DateTimeOffset.UtcNow,
            (false, 0, "Evrakları cuma günü yollarım, söz veriyorum sana."),
            (false, 24_000, "Faturayı da pazartesi günü keserim, merak etme."),
            (false, 48_000, "Parayı ayın onunda hesabına geçireceğim kesinlikle."));

        Turn[] whole =
        [
            Turn.Says(Promise("Evrakları cuma günü yollarım", "evrak gönderimi")),
            Turn.Says(Promise("Faturayı da pazartesi günü keserim", "fatura kesimi")),
            Turn.Says(Promise("Parayı ayın onunda hesabına geçireceğim", "para transferi")),
        ];

        await new AnalysisPipeline(new Provider(whole), _repo)
            .AnalyseAsync(call, Options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, _repo.GetOpenCommitments(_contact).Count);

        await new AnalysisPipeline(
                new Provider(whole[0], Turn.Fails("429 rate limit"), whole[2]), _repo)
            .AnalyseAsync(call, Options, cancellationToken: TestContext.Current.CancellationToken);

        var after = _repo.GetOpenCommitments(_contact).Select(c => c.Obligation).OrderBy(o => o).ToList();

        Assert.Equal(["evrak gönderimi", "fatura kesimi", "para transferi"], after);
    }

    // ---- E: a finding belongs to the conversation it was said in -----------------------------

    /// <summary>
    /// The overdue promise, made in the first conversation and dismissed there.
    ///
    /// The deterministic checks read the whole person, so analysing the SECOND conversation with
    /// somebody emits the same overdue finding again — filed against the first, because that is
    /// where the words were said. The dismissal check was scoped to the call being analysed, so
    /// it never saw the user's ruling.
    ///
    /// Goes red when a re-run resurrects it: two rows, or one undismissed row. Either breaks K4,
    /// and the second one is the ledger telling a person something they have already refused.
    /// </summary>
    [Fact]
    public async Task AFindingDismissedOnOneConversationSurvivesAnalysingAnother()
    {
        var first = Seed(new DateTimeOffset(2024, 3, 6, 10, 0, 0, TimeSpan.Zero),
            (false, 0, "Evrakları yarın yollarım, söz veriyorum sana."));

        await new AnalysisPipeline(
                new Provider(Turn.Says(Promise("Evrakları yarın yollarım", "evrak gönderimi", "yarın"))),
                _repo)
            .AnalyseAsync(first, Options, cancellationToken: TestContext.Current.CancellationToken);

        var overdue = Assert.Single(_repo.GetFlags(_contact), f => f.Kind == FlagKind.OverdueCommitment);
        Assert.Equal(first, overdue.CallId);

        _repo.DismissFlag(overdue.Id);

        // A different conversation with the same person, months later. Its own section is read
        // successfully — a run where nothing parsed never reaches the checks at all, and it is
        // the checks that reach across conversations.
        var second = Seed(new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
            (false, 0, "Yeni dairenin kirası on beş bin lira olmuş bu sene."));

        await new AnalysisPipeline(
                new Provider(Turn.Says(Statement("kirası on beş bin lira", "daire", "kira", "15000"))), _repo)
            .AnalyseAsync(second, Options, cancellationToken: TestContext.Current.CancellationToken);

        var flags = _repo.GetFlags(_contact, includeDismissed: true)
            .Where(f => f.Kind == FlagKind.OverdueCommitment)
            .ToList();

        var kept = Assert.Single(flags);
        Assert.True(kept.DismissedByUser);
        Assert.Equal(overdue.Id, kept.Id);
    }

    /// <summary>
    /// The same finding, not dismissed — analysed twice, still one row.
    ///
    /// The delete was scoped to the call being analysed while the finding belongs to another, so
    /// a copy was added on every run and the ledger grew a longer list of the same sentence each
    /// time. Goes red when the per-conversation delete stops running: three analyses, three rows.
    /// </summary>
    [Fact]
    public async Task AnalysingTheSameConversationTwiceLeavesOneCopyOfAnotherCallsFinding()
    {
        var first = Seed(new DateTimeOffset(2024, 3, 6, 10, 0, 0, TimeSpan.Zero),
            (false, 0, "Evrakları yarın yollarım, söz veriyorum sana."));

        await new AnalysisPipeline(
                new Provider(Turn.Says(Promise("Evrakları yarın yollarım", "evrak gönderimi", "yarın"))),
                _repo)
            .AnalyseAsync(first, Options, cancellationToken: TestContext.Current.CancellationToken);

        var second = Seed(new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero),
            (false, 0, "Yeni dairenin kirası on beş bin lira olmuş bu sene."));

        for (var run = 0; run < 2; run++)
        {
            await new AnalysisPipeline(
                    new Provider(Turn.Says(Statement("kirası on beş bin lira", "daire", "kira", "15000"))), _repo)
                .AnalyseAsync(second, Options, cancellationToken: TestContext.Current.CancellationToken);
        }

        var flags = _repo.GetFlags(_contact, includeDismissed: true)
            .Where(f => f.Kind == FlagKind.OverdueCommitment)
            .ToList();

        Assert.Single(flags);
        Assert.Equal(first, flags[0].CallId);
    }
}
