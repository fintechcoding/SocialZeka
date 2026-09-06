using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>Records every request, so a test can ask what the models were actually shown.</summary>
file sealed class ScriptedLlm(params string[] replies) : ILlmClient
{
    private int _next;

    public List<LlmRequest> Requests { get; } = [];

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        return Task.FromResult(new LlmResponse(reply, "stop", 100, 50));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The rule change of Paket E, and its four fences.
///
/// The opt-in assessment used to be a dead end in every direction; one thing now leaves it — a
/// tactic quote the machine verified — so that a person's card can count the sentence. These
/// tests are what keeps that from becoming a leak: the level and the argument stay behind, an
/// unrecognised label is dropped rather than filed as "other", a row the user rejected does not
/// come back, each machinery owns its own rows, and nothing in the table is ever shown to a
/// model.
/// </summary>
public sealed class TacticEvidenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-tac-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public TacticEvidenceTests()
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
        var contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
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

    private static string Reply(string duzey, string degerlendirme, string taktikler) =>
        $$"""
        {"duzey":"{{duzey}}","degerlendirme":"{{degerlendirme}}",
         "taktikler":[{{taktikler}}],"yetersiz":false}
        """;

    private Task<DeceptionReport> Assess(long call, string reply) =>
        new DeceptionAnalysis(new ScriptedLlm(reply), _repo)
            .RunAsync(call, "test-model", TestContext.Current.CancellationToken);

    /// <summary>
    /// Only the quote travels.
    ///
    /// Red means the assessment's suspicion level or its written argument has found a way into
    /// the evidence table — which would turn one model's opinion about a person into a row the
    /// rest of the product counts as a fact about them.
    /// </summary>
    [Fact]
    public async Task TheLevelAndTheAssessmentAreNeverCopiedToTheEvidence()
    {
        var (call, _) = Seed((false, 7_000, "Bugün karar vermezsen bu fiyat yarın yok."));

        var report = await Assess(call, Reply(
            "yuksek",
            "Bence açıkça yalan söylüyor ve beni kandırmaya çalışıyor.",
            """
            {"taktik":"aciliyet","konusan":"KARSI","alinti":"Bugün karar vermezsen bu fiyat yarın yok","gerekce":"Yapay zaman baskısı"}
            """));

        Assert.Equal("yuksek", report.Level);

        var row = Assert.Single(_repo.TacticEvidenceOf(call));
        Assert.Equal("aciliyet", row.Tactic);
        Assert.Equal("Bugün karar vermezsen bu fiyat yarın yok.", row.Quote);
        Assert.Equal(7_000, row.QuoteStartMs);
        Assert.False(row.ByMe);
        Assert.Equal(TacticEvidence.Sources.Deception, row.Source);
        Assert.Equal("test-model", row.ModelUsed);

        // Nothing in the row carries the opinion: not the level word, not a sentence of it, and
        // not the model's reasoning either.
        var stored = string.Join(" ", _repo.TacticEvidenceOf(call).Select(r => $"{r.Tactic} {r.Quote}"));
        Assert.DoesNotContain("yuksek", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kandırmaya", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Yapay zaman baskısı", stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// A label this build does not know is dropped, and the drop is counted.
    ///
    /// Red means an unrecognised word has become a row — the "diger" bucket the schema
    /// deliberately no longer offers — and somebody's card is counting whatever a model typed as
    /// a pattern in their behaviour. It also goes red if the drop is silent, which would look on
    /// screen exactly like a person with nothing against them.
    /// </summary>
    [Fact]
    public async Task AnUnknownTacticIsDroppedFromTheEvidenceAndCounted()
    {
        var (call, _) = Seed(
            (false, 3_000, "Bunu sonra konuşalım, şimdi vaktim yok abi."),
            (false, 9_000, "Zaten sana hep yardım ettim, unuttun mu?"));

        var report = await Assess(call, Reply("orta", "Görüşüm.",
            """
            {"taktik":"diger","konusan":"KARSI","alinti":"Bunu sonra konuşalım, şimdi vaktim yok abi","gerekce":"Bilinmeyen"},
            {"taktik":"sucluluk","konusan":"KARSI","alinti":"Zaten sana hep yardım ettim, unuttun mu","gerekce":"Suçluluk yükleme"}
            """));

        // The note keeps both lines: it is the model's opinion and it is stored as given.
        Assert.Equal(2, report.Tactics.Count);
        Assert.Equal(1, report.EvidenceDropped);

        // The card gets only the one with a label it can name.
        var row = Assert.Single(_repo.TacticEvidenceOf(call));
        Assert.Equal("sucluluk", row.Tactic);
    }

    /// <summary>
    /// A dismissed row is a tombstone.
    ///
    /// Red means running the assessment again resurrects a sentence the user has explicitly
    /// rejected — the failure that makes a ledger stop being read, here reproduced on the
    /// contact card.
    /// </summary>
    [Fact]
    public async Task ARowTheUserDismissedDoesNotComeBackOnTheNextRun()
    {
        var (call, _) = Seed((false, 5_000, "Bugün imzalamazsan bu iş biter, başkası alır."));

        const string reply = """
            {"taktik":"baski","konusan":"KARSI","alinti":"Bugün imzalamazsan bu iş biter, başkası alır","gerekce":"Dayatma"}
            """;

        await Assess(call, Reply("orta", "Görüşüm.", reply));
        var first = Assert.Single(_repo.TacticEvidenceOf(call));

        _repo.DismissTacticEvidence(first.Id);
        Assert.Empty(_repo.TacticEvidenceOf(call));

        await Assess(call, Reply("orta", "Görüşüm.", reply));

        // Still nothing on the card, and still exactly one row underneath: the tombstone.
        Assert.Empty(_repo.TacticEvidenceOf(call));
        Assert.Single(_repo.TacticEvidenceOf(call, includeDismissed: true));

        // And the user can take the dismissal back.
        _repo.RestoreTacticEvidence(first.Id);
        Assert.Single(_repo.TacticEvidenceOf(call));
    }

    /// <summary>
    /// Each machinery clears only its own rows.
    ///
    /// Red means a ledger rebuild has erased the opt-in assessment's evidence — work the user
    /// paid a model for, thrown away by a button that was supposed to touch the extraction's
    /// rows — or the other way round.
    /// </summary>
    [Fact]
    public void ClearAnalysisTakesThePipelinesRowsAndLeavesTheAssessments()
    {
        var (call, _) = Seed((false, 2_000, "Bir cümle."));

        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "değerlendirmeden gelen", QuoteStartMs = 1000 },
        ]);

        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Pipeline,
        [
            new TacticEvidence
            {
                CallId = call, Source = TacticEvidence.Sources.Pipeline,
                Tactic = "otorite", Quote = "boru hattından gelen", QuoteStartMs = 2000,
            },
        ]);

        var pipelineRow = _repo.TacticEvidenceOf(call)
            .Single(r => r.Source == TacticEvidence.Sources.Pipeline);
        _repo.DismissTacticEvidence(pipelineRow.Id);

        _repo.ClearAnalysis(call);

        // The assessment's row survives; the pipeline's undismissed row does not; and the
        // pipeline's dismissed row stays, because a tombstone that is deleted stops working.
        var left = _repo.TacticEvidenceOf(call, includeDismissed: true);
        Assert.Equal(2, left.Count);
        Assert.Contains(left, r => r.Source == TacticEvidence.Sources.Deception && !r.DismissedByUser);
        Assert.Contains(left, r => r.Source == TacticEvidence.Sources.Pipeline && r.DismissedByUser);
    }

    /// <summary>
    /// An unrecognised label cannot be written even by a caller that asks for it directly.
    ///
    /// Red means the whitelist lives in one place only — the analysis — and any future writer
    /// that forgets it puts free text onto a person's card.
    /// </summary>
    [Fact]
    public void TheRepositoryRefusesALabelThatIsNotOnTheWhitelist()
    {
        var (call, _) = Seed((false, 2_000, "Bir cümle."));

        var written = _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "diger", Quote = "bir alıntı" },
            new TacticEvidence { CallId = call, Tactic = "her ne ise", Quote = "başka bir alıntı" },
            new TacticEvidence { CallId = call, Tactic = "kacamak", Quote = "geçerli olan" },
        ]);

        Assert.Equal(1, written);
        Assert.Equal("kacamak", Assert.Single(_repo.TacticEvidenceOf(call)).Tactic);
    }

    /// <summary>
    /// The table is a dead end towards every model.
    ///
    /// A sentence that exists ONLY as tactic evidence is planted, and then everything that talks
    /// to a model is run over the same person: the extraction, its summary, the consistency
    /// check with its ledger context, and the assessment itself. Red means one of those prompts
    /// now carries it — which is a run building its own case on its own earlier labels instead
    /// of on what was said, and the reason §7.10 forbids it.
    /// </summary>
    [Fact]
    public async Task NothingInTheEvidenceTableEverReachesAPrompt()
    {
        const string marker = "ZEBRAKODU";

        var (earlier, contact) = Seed((false, 4_000, "Eski görüşmenin bir cümlesi."));

        _repo.ReplaceTacticEvidence(earlier, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence
            {
                CallId = earlier,
                Tactic = "baski",
                Quote = $"{marker} bu cümle yalnız kanıt tablosunda duruyor",
                QuoteStartMs = 4_000,
                ModelUsed = marker,
            },
        ]);

        var later = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow.AddDays(1),
            State = ProcessingState.Transcribed,
        });
        _repo.AssignContact(later, contact);
        _repo.ReplaceSegments(later,
        [
            new Segment { CallId = later, IsMe = true, StartMs = 0, EndMs = 3000, Text = "Sözleşme ne zaman gelir?" },
            new Segment { CallId = later, IsMe = false, StartMs = 6000, EndMs = 9000, Text = "Sözleşmeyi cuma günü yollarım." },
        ]);

        var pipeline = new ScriptedLlm(
            """
            {"taahhutler":[{"konusan":"KARSI","alinti":"Sözleşmeyi cuma günü yollarım","yukumluluk":"sözleşme gönderimi","tarih_ham":"cuma günü","kosullu":false}],
             "iddialar":[],"sorular":[],"baski_isaretleri":[]}
            """,
            "Özet.");

        await new AnalysisPipeline(pipeline, _repo).AnalyseAsync(
            later,
            new AnalysisOptions { Model = "test-model", AdjudicateContradictions = false, WriteSummary = true },
            cancellationToken: TestContext.Current.CancellationToken);

        var consistency = new ScriptedLlm(
            """
            {"bulgular":[],"tutarli_gozlemler":[],"genel_uyari":"","yetersiz":false}
            """);

        await new ConsistencyAnalysis(consistency, _repo).RunAsync(
            later, "test-model", cancellationToken: TestContext.Current.CancellationToken);

        var assessment = new ScriptedLlm(Reply("yok", "Temiz.", ""));
        await new DeceptionAnalysis(assessment, _repo).RunAsync(
            later, "test-model", TestContext.Current.CancellationToken);

        var everything = pipeline.Requests.Concat(consistency.Requests).Concat(assessment.Requests).ToList();

        // A test that inspected nothing would pass for the wrong reason.
        Assert.True(everything.Count >= 3, $"yalnız {everything.Count} istek görüldü");

        foreach (var request in everything)
        {
            Assert.DoesNotContain(marker, request.UserPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, request.SystemPrompt, StringComparison.Ordinal);
        }
    }
}
