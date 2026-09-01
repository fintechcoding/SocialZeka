using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

file sealed class ScriptedLlm(params string[] replies) : ILlmClient
{
    private int _next;

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public int Calls { get; private set; }
    public string? LastUserPrompt { get; private set; }
    public string? LastSystemPrompt { get; private set; }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastUserPrompt = request.UserPrompt;
        LastSystemPrompt = request.SystemPrompt;

        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        return Task.FromResult(new LlmResponse(reply, "stop", 100, 50));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The consistency check, held to the product's own rule: nothing reaches the user that cannot
/// be shown. These tests are the rule's enforcement — the prompt asks the model nicely, but
/// what these pin down is what happens when the model does not comply.
/// </summary>
public sealed class ConsistencyAnalysisTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-cons-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public ConsistencyAnalysisTests()
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

    private (long callId, long contactId) Seed(params (bool me, int ms, string text)[] lines)
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow,
            State = ProcessingState.Analysed,
        });
        _repo.AssignContact(call, contact);

        _repo.ReplaceSegments(call, lines.Select(l => new Segment
        {
            CallId = call, IsMe = l.me, StartMs = l.ms, EndMs = l.ms + 3000, Text = l.text,
        }));

        return (call, contact);
    }

    private static string Reply(string bulgular, string uyari = "\"\"") =>
        $$"""
        {"bulgular":[{{bulgular}}],"tutarli_gozlemler":[],"genel_uyari":{{uyari}},"yetersiz":false}
        """;

    [Fact]
    public async Task AVerifiedFindingIsPersistedWithItsRealTimestampAndSource()
    {
        var (call, contact) = Seed(
            (false, 4_000, "Kira on beş bin lira."),
            (false, 20_000, "Kira dediğim gibi yirmi bin."));

        var llm = new ScriptedLlm(Reply(
            """
            {"tur":"celiski","konusan":"KARSI","alinti":"Kira on beş bin lira",
             "karsi_alinti":"Kira dediğim gibi yirmi bin",
             "aciklama":"Kira iki farklı rakamla söylendi","gerekce":"On beş bin ile yirmi bin aynı anda doğru olamaz","guven":"yuksek"}
            """,
            uyari: "\"Kira rakamı görüşme içinde değişti; yazılı teyit istenebilir.\""));

        var report = await new ConsistencyAnalysis(llm, _repo).RunAsync(
            call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(FlagKind.Contradiction, finding.Kind);
        Assert.Equal(4_000, finding.QuoteStartMs);
        Assert.Equal(20_000, finding.CounterQuoteStartMs);
        Assert.Equal(call, finding.CounterCallId);
        Assert.Equal(Flag.Sources.Consistency, finding.Source);
        Assert.Equal("yuksek", finding.Confidence);
        Assert.NotNull(report.Warning);

        // Persisted, not just returned — and readable back with its source intact.
        var stored = Assert.Single(_repo.FlagsOf(call));
        Assert.Equal(Flag.Sources.Consistency, stored.Source);
        Assert.NotNull(_repo.GetConsistencyNote(call));
        _ = contact;
    }

    [Fact]
    public async Task AFabricatedQuoteKillsTheFindingAndAnUnsupportedWarningDies()
    {
        var (call, _) = Seed((false, 0, "Bugün hava güzeldi."));

        var llm = new ScriptedLlm(Reply(
            """
            {"tur":"celiski","konusan":"KARSI","alinti":"Sana asla söylemedim öyle bir şey",
             "aciklama":"Uydurulmuş çelişki","gerekce":"...","guven":"yuksek"}
            """,
            uyari: "\"Bu kişiye dikkat et.\""));

        var report = await new ConsistencyAnalysis(llm, _repo).RunAsync(
            call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(report.Findings);
        Assert.Equal(1, report.RejectedCount);

        // The warning stood on nothing, so it does not stand at all.
        Assert.Null(report.Warning);
        Assert.Null(_repo.GetConsistencyNote(call));
    }

    [Fact]
    public async Task APriorStatementNumberAnchorsTheCounterToTheOldCall()
    {
        var (call, contact) = Seed((false, 6_000, "Kiram yirmi bin."));

        // An earlier call whose claim is in the ledger — the [B1] the model will cite.
        var earlier = _repo.InsertCall(new Call
        {
            ContactId = contact, App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-30), State = ProcessingState.Analysed,
        });
        _repo.InsertClaim(new Claim
        {
            CallId = earlier, ContactId = contact, ByMe = false,
            Quote = "Kira on beş bin", QuoteStartMs = 9_000,
            Entity = "kira", Attribute = "tutar", Value = "15000",
        });

        var llm = new ScriptedLlm(Reply(
            """
            {"tur":"celiski","konusan":"KARSI","alinti":"Kiram yirmi bin",
             "onceki_baglam_no":1,
             "aciklama":"Kira önceki görüşmeyle çelişiyor","gerekce":"15 bin ile 20 bin aynı anda doğru olamaz","guven":"orta"}
            """));

        var report = await new ConsistencyAnalysis(llm, _repo).RunAsync(
            call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(earlier, finding.CounterCallId);
        Assert.Equal(9_000, finding.CounterQuoteStartMs);
        Assert.Equal("Kira on beş bin", finding.CounterQuote);

        // And the [B1] line really went to the model.
        Assert.Contains("[B1]", llm.LastUserPrompt!);
    }

    [Fact]
    public async Task AnOutOfRangePriorNumberLosesTheAnchorNotTheFinding()
    {
        var (call, _) = Seed((false, 0, "Kiram yirmi bin."));

        var llm = new ScriptedLlm(Reply(
            """
            {"tur":"celiski","konusan":"KARSI","alinti":"Kiram yirmi bin",
             "onceki_baglam_no":99,
             "aciklama":"...","gerekce":"...","guven":"dusuk"}
            """));

        var report = await new ConsistencyAnalysis(llm, _repo).RunAsync(
            call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        var finding = Assert.Single(report.Findings);
        Assert.Null(finding.CounterQuote);
        Assert.Null(finding.CounterCallId);
    }

    [Fact]
    public async Task ARerunReplacesItsOwnRowsAndADismissedFindingStaysDead()
    {
        var (call, _) = Seed(
            (false, 4_000, "Kira on beş bin lira."),
            (false, 20_000, "Kira dediğim gibi yirmi bin."));

        var reply = Reply(
            """
            {"tur":"celiski","konusan":"KARSI","alinti":"Kira on beş bin lira",
             "aciklama":"Kira çelişkisi","gerekce":"...","guven":"orta"}
            """);

        var service = new ConsistencyAnalysis(new ScriptedLlm(reply), _repo);
        await service.RunAsync(call, "test-model", cancellationToken: TestContext.Current.CancellationToken);
        await new ConsistencyAnalysis(new ScriptedLlm(reply), _repo)
            .RunAsync(call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        // Two runs, one row — replace, not accumulate.
        Assert.Single(_repo.FlagsOf(call));

        // Dismiss it; the same finding coming back from a third run must not resurrect.
        _repo.DismissFlag(_repo.FlagsOf(call)[0].Id);

        var third = await new ConsistencyAnalysis(new ScriptedLlm(reply), _repo)
            .RunAsync(call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(third.Findings);
        Assert.Empty(_repo.FlagsOf(call)); // undismissed view stays empty
    }

    [Fact]
    public async Task TheLedgerRebuildAndTheConsistencyRunCannotEraseEachOther()
    {
        var (call, contact) = Seed(
            (false, 4_000, "Kira on beş bin lira."),
            (false, 20_000, "Kira dediğim gibi yirmi bin."));

        // One pipeline flag and one consistency flag on the same call.
        _repo.InsertFlag(new Flag
        {
            CallId = call, ContactId = contact, Kind = FlagKind.PressureTactic,
            Summary = "pipeline bulgusu", Quote = "Kira on beş bin lira", QuoteStartMs = 4_000,
        });

        await new ConsistencyAnalysis(new ScriptedLlm(Reply(
                """
                {"tur":"kacamak","konusan":"KARSI","alinti":"Kira dediğim gibi yirmi bin",
                 "aciklama":"...","gerekce":"...","guven":"dusuk"}
                """)), _repo)
            .RunAsync(call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _repo.FlagsOf(call).Count);

        // The ledger rebuild clears only pipeline rows.
        _repo.ClearAnalysis(call);
        var afterRebuild = Assert.Single(_repo.FlagsOf(call));
        Assert.Equal(Flag.Sources.Consistency, afterRebuild.Source);

        // And the consistency clear leaves pipeline rows alone.
        _repo.InsertFlag(new Flag
        {
            CallId = call, ContactId = contact, Kind = FlagKind.PressureTactic,
            Summary = "pipeline bulgusu", Quote = "Kira on beş bin lira", QuoteStartMs = 4_000,
        });
        _repo.ClearConsistency(call);

        var afterConsistencyClear = Assert.Single(_repo.FlagsOf(call));
        Assert.Equal(Flag.Sources.Pipeline, afterConsistencyClear.Source);
    }

    [Fact]
    public async Task ATranscriptOverTheLimitIsRefusedNotTruncated()
    {
        var lines = Enumerable.Range(0, 400)
            .Select(i => (false, i * 1000, new string('a', 100)))
            .ToArray();

        var (call, _) = Seed(lines);

        var llm = new ScriptedLlm("{}");

        var report = await new ConsistencyAnalysis(llm, _repo).RunAsync(
            call, "test-model", sendsDataOffMachine: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Ok);
        Assert.Contains("uzun", report.Problem!);
        Assert.Equal(0, llm.Calls); // refused before spending anything
    }

    [Fact]
    public async Task UsageIsRecordedUnderItsOwnStage()
    {
        var (call, _) = Seed((false, 0, "Bugün hava güzeldi."));

        await new ConsistencyAnalysis(new ScriptedLlm(Reply("")), _repo)
            .RunAsync(call, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        var engines = _repo.UsageByEngine(ProcessingStage.Consistency);
        Assert.Contains(engines, e => e.Engine == "test-model");
    }

    [Fact]
    public async Task TheOtherPartyOnlyScopeIsToldToTheModel()
    {
        var (call, _) = Seed((false, 0, "Bugün hava güzeldi."));

        var llm = new ScriptedLlm(Reply(""));

        await new ConsistencyAnalysis(llm, _repo).RunAsync(
            call, "test-model", otherPartyOnly: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Yalnızca KARSI", llm.LastUserPrompt!);
        Assert.Contains("KONUSMA_BASLANGIC", llm.LastUserPrompt!);
    }
}
