using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

file sealed class ScriptedLlm(params string[] replies) : ILlmClient
{
    private int _next;

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        return Task.FromResult(new LlmResponse(reply, "stop", 100, 50));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The reading is the product's one deliberately subjective surface, so what these tests pin
/// down is not its content but its containment: accusation-adjacent lines die without a
/// verifiable quote, the risk list stays short, and what is stored is the enforced shape —
/// a dropped row can never come back on reopen.
/// </summary>
public sealed class ReadingAnalysisTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-read-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public ReadingAnalysisTests()
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

    private long Seed(params (bool me, int ms, string text)[] lines)
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

        return call;
    }

    private static string Reply(
        string riskler = "", string uslup = "", string cozulmeyen = "", string sorular = "",
        string yorum = "Genel izlenimim: iş sözlü olarak ilerliyor.",
        string karsi = "Aynı sözler sıradan bir erteleme de olabilir.") =>
        $$"""
        {"genel_yorum":"{{yorum}}",
         "muzakere_durumu":"Fiyat açık, hamle sende.",
         "uslup_gozlemleri":[{{uslup}}],
         "risk_noktalari":[{{riskler}}],
         "cozulmeyenler":[{{cozulmeyen}}],
         "baska_okuma":"{{karsi}}",
         "sorulacak_sorular":[{{sorular}}],
         "yetersiz":false}
        """;

    private Task<ReadingReport> Run(long call, string reply) =>
        new ReadingAnalysis(new ScriptedLlm(reply), _repo)
            .RunAsync(call, "test-model", TestContext.Current.CancellationToken);

    [Fact]
    public async Task ARiskWithoutAVerifiableQuoteDiesAndAVerifiedOneKeepsItsTimestamp()
    {
        var call = Seed((false, 6_000, "Sözleşmeyi sonra konuşuruz, önce kaporayı yatır."));

        var report = await Run(call, Reply(riskler:
            """
            {"okuma":"Kapora sözleşmeden önce isteniyor","dayanak":"Sıra ters","alinti":"önce kaporayı yatır"},
            {"okuma":"Uydurma risk","dayanak":"Yok","alinti":"bu cümle hiç kurulmadı"}
            """));

        Assert.True(report.Ok, report.Problem);
        var risk = Assert.Single(report.RiskPoints);
        Assert.Equal(6_000, risk.StartMs);
        Assert.NotNull(risk.Quote);
        Assert.Equal(1, report.RejectedCount);

        // The subjective paragraphs pass through; the mandatory counter-reading is present.
        Assert.NotEqual("", report.GeneralReading);
        Assert.NotEqual("", report.CounterReading);
    }

    [Fact]
    public async Task WhatIsStoredIsTheEnforcedShapeSoDroppedRowsStayDroppedOnReopen()
    {
        var call = Seed((false, 6_000, "Sözleşmeyi sonra konuşuruz, önce kaporayı yatır."));

        await Run(call, Reply(riskler:
            """
            {"okuma":"Uydurma risk","dayanak":"Yok","alinti":"bu cümle hiç kurulmadı"}
            """));

        var stored = _repo.GetReading(call);
        Assert.NotNull(stored);
        Assert.Equal("test-model", stored.Value.ModelUsed);

        var reopened = ReadingAnalysis.FromStored(stored.Value.Json);
        Assert.NotNull(reopened);
        Assert.Empty(reopened.RiskPoints);
        Assert.Equal(1, reopened.RejectedCount);
        Assert.NotEqual("", reopened.GeneralReading);
    }

    [Fact]
    public async Task TheRiskListIsCappedAtThreeInCode()
    {
        var call = Seed(
            (false, 2_000, "Kapora bir."), (false, 4_000, "Kapora iki."),
            (false, 6_000, "Kapora üç."), (false, 8_000, "Kapora dört."));

        var report = await Run(call, Reply(riskler: string.Join(",",
            Enumerable.Range(1, 4).Select(i =>
                $$"""{"okuma":"Risk {{i}}","dayanak":"test","alinti":"Kapora {{i switch { 1 => "bir", 2 => "iki", 3 => "üç", _ => "dört" }}}"}"""))));

        Assert.Equal(ReadingAnalysis.MaxRisks, report.RiskPoints.Count);
    }

    [Fact]
    public async Task UnresolvedTopicsDegradeToPlainProseWhenTheQuoteDoesNotLocate()
    {
        var call = Seed((false, 3_000, "Tapu konusu açıldı ama kapanmadı."));

        var report = await Run(call, Reply(cozulmeyen:
            """
            {"konu":"Tapu devri netleşmedi","alinti":"Tapu konusu açıldı ama kapanmadı"},
            {"konu":"Komisyon hiç konuşulmadı","alinti":"olmayan cümle"}
            """));

        Assert.Equal(2, report.Unresolved.Count);
        Assert.NotNull(report.Unresolved[0].Quote);
        Assert.Equal(3_000, report.Unresolved[0].StartMs);

        // Observation without a locatable quote survives — but with nothing to play.
        Assert.Null(report.Unresolved[1].Quote);
        Assert.Null(report.Unresolved[1].StartMs);
    }

    [Fact]
    public async Task ARunBooksItsSpendUnderItsOwnStage()
    {
        var call = Seed((false, 3_000, "Kısa bir görüşme."));

        await Run(call, Reply());

        Assert.NotNull(_repo.LastRun(call, ProcessingStage.Reading));
    }
}
