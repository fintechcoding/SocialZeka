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
/// The opt-in assessment is allowed to state an opinion — what these tests pin down is what
/// it is NOT allowed to do: assert a tactic without a verifiable quote, keep an elevated
/// level standing on tactics that all failed verification, or come back different on reopen
/// than it was on screen.
/// </summary>
public sealed class DeceptionAnalysisTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-dec-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public DeceptionAnalysisTests()
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

    private static string Reply(string duzey, string taktikler = "") =>
        $$"""
        {"duzey":"{{duzey}}",
         "degerlendirme":"Görüşüm: aceleye getirme örüntüsü görüyorum.",
         "taktikler":[{{taktikler}}],
         "yetersiz":false}
        """;

    private Task<DeceptionReport> Run(long call, string reply) =>
        new DeceptionAnalysis(new ScriptedLlm(reply), _repo)
            .RunAsync(call, "test-model", TestContext.Current.CancellationToken);

    [Fact]
    public async Task AVerifiedTacticKeepsItsTimestampAndTheSpendIsBookedUnderItsOwnStage()
    {
        var call = Seed((false, 7_000, "Bugün karar vermezsen bu fiyat yarın yok."));

        var report = await Run(call, Reply("orta",
            """
            {"taktik":"aciliyet","konusan":"KARSI","alinti":"Bugün karar vermezsen bu fiyat yarın yok","gerekce":"Yapay zaman baskısı kuruyor"}
            """));

        Assert.True(report.Ok, report.Problem);
        Assert.Equal("orta", report.Level);
        var tactic = Assert.Single(report.Tactics);
        Assert.Equal(7_000, tactic.StartMs);
        Assert.False(tactic.IsMe);
        Assert.NotNull(_repo.LastRun(call, ProcessingStage.Deception));
    }

    [Fact]
    public async Task ATacticWithoutAVerifiableQuoteDiesAndDragsAnElevatedLevelDownWithIt()
    {
        var call = Seed((false, 4_000, "Fiyatı sonra konuşuruz."));

        var report = await Run(call, Reply("yuksek",
            """
            {"taktik":"tehdit_imasi","konusan":"KARSI","alinti":"bu cümle hiç kurulmadı","gerekce":"Uydurma"}
            """));

        Assert.Empty(report.Tactics);
        Assert.Equal(1, report.RejectedCount);

        // "Yüksek şüphe" standing on zero surviving evidence is an opinion with no argument —
        // it is demoted in code, not trusted.
        Assert.Equal("dusuk", report.Level);
    }

    [Fact]
    public async Task WhatIsStoredIsTheEnforcedShape()
    {
        var call = Seed((false, 4_000, "Fiyatı sonra konuşuruz."));

        await Run(call, Reply("yuksek",
            """
            {"taktik":"baski","konusan":"KARSI","alinti":"olmayan cümle","gerekce":"Uydurma"}
            """));

        var stored = _repo.GetDeception(call);
        Assert.NotNull(stored);

        var reopened = DeceptionAnalysis.FromStored(stored.Value.Json);
        Assert.NotNull(reopened);
        Assert.Empty(reopened.Tactics);
        Assert.Equal("dusuk", reopened.Level);
        Assert.Equal(1, reopened.RejectedCount);
    }

    [Fact]
    public async Task ACleanConversationIsAllowedToBeClean()
    {
        var call = Seed((false, 3_000, "Belgeleri aldım, teşekkürler, iyi günler."));

        var report = await Run(call, Reply("yok"));

        Assert.Equal("yok", report.Level);
        Assert.False(report.IsElevated);
        Assert.Empty(report.Tactics);
    }

    [Fact]
    public async Task TheSeventhTacticIsCroppedInCode()
    {
        var lines = Enumerable.Range(1, 7)
            .Select(i => (false, i * 2_000, $"Madde {i} için hemen karar ver."))
            .ToArray();
        var call = Seed(lines);

        var report = await Run(call, Reply("yuksek", string.Join(",", Enumerable.Range(1, 7).Select(i =>
            $$"""{"taktik":"baski","konusan":"KARSI","alinti":"Madde {{i}} için hemen karar ver","gerekce":"Baskı"}"""))));

        Assert.Equal(DeceptionAnalysis.MaxTactics, report.Tactics.Count);
    }
}
