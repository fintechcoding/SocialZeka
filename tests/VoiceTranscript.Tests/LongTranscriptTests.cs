using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>A model that must not be reached: a refusal has to cost nothing.</summary>
file sealed class ForbiddenLlm : ILlmClient
{
    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public int Calls { get; private set; }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException(
            "Sığmayan bir görüşme için modele istek atıldı — ret, istek gönderilmeden verilmeliydi.");
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// A model that answers by the shape of the request — an empty extraction for the ledger, a
/// bare reading or assessment for those schemas, a sentence for prose — and records every
/// request so a test can read what went over the wire.
/// </summary>
file sealed class ScriptedLlm(Func<LlmRequest, LlmResponse>? answer = null) : ILlmClient
{
    public const string Summary = "Elli dakikalık bir sözleşme görüşmesi; fiyat cumaya kaldı.";

    public List<LlmRequest> Requests { get; } = [];

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (answer is not null) return Task.FromResult(answer(request));

        var reply = request.JsonSchema switch
        {
            null => Summary,
            var schema when ReferenceEquals(schema, ReadingPrompt.Schema) =>
                """
                {"genel_yorum":"Sıradan bir pazarlık.","muzakere_durumu":"Fiyat açık.",
                 "uslup_gozlemleri":[],"risk_noktalari":[],"cozulmeyenler":[],
                 "baska_okuma":"Aynı sözler olağan bir erteleme de olabilir.",
                 "sorulacak_sorular":[],"yetersiz":false}
                """,
            var schema when ReferenceEquals(schema, DeceptionPrompt.Schema) =>
                """{"duzey":"yok","degerlendirme":"Belirti görmedim.","taktikler":[],"yetersiz":false}""",
            _ => """{"taahhutler":[],"iddialar":[],"sorular":[],"baski_isaretleri":[]}""",
        };

        return Task.FromResult(new LlmResponse(reply, "stop", 100, 50));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The three reads that hand a call to the model whole — the reading, the opt-in assessment and
/// the conversation summary — against a call of the length the user actually reported: forty-five
/// to fifty minutes, at the density this machine's archive measures. Each either fits under a cap
/// derived from the model's window, or refuses with a sentence that names the size and the fix,
/// or reads a window and says so — and a refusal never costs a request.
///
/// In the interface-language collection because the sentences asserted on come from the
/// dictionary, which the localisation tests swap under a running test.
/// </summary>
[Collection(InterfaceLanguageCollection.Name)]
public sealed class LongTranscriptTests : IDisposable
{
    /// <summary>The two catalogued windows a request can meet, addressed as a request addresses them.</summary>
    private const string SixteenThousand = "Qwen3.5-4B-IQ4_XS.gguf";

    private const string ThirtyTwoThousand = "Qwen3.5-4B-Q6_K.gguf";

    /// <summary>The archive's own density: 69 minutes came to 65,853 characters, 47 to 41,493.</summary>
    private const int CharactersPerMinute = 950;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-long-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public LongTranscriptTests()
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
    /// A call of the measured density: ten lines a minute, ninety-five characters each, the two
    /// sides taking turns. Every line ends with its own number so the head, the middle and the
    /// tail can be told apart in whatever reached the model.
    /// </summary>
    private static List<Segment> SyntheticCall(long callId, int minutes)
    {
        const string sentence =
            "Teklifi cuma gününe kadar değerlendirip size dönüş yapacağım, fiyatı bir daha konuşuruz";

        List<Segment> lines = [];

        for (var i = 0; i < minutes * 10; i++)
        {
            var startMs = i * 6_000;

            lines.Add(new Segment
            {
                CallId = callId,
                IsMe = i % 2 == 0,
                StartMs = startMs,
                EndMs = startMs + 5_000,
                Text = $"{sentence} {i:0000}".PadRight(CharactersPerMinute / 10, '.'),
            });
        }

        return lines;
    }

    private long Seed(int minutes)
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow,
            State = ProcessingState.Transcribed,
        });
        _repo.AssignContact(call, contact);

        var lines = SyntheticCall(call, minutes);
        _repo.ReplaceSegments(call, lines);

        // The density claim, checked rather than assumed: a synthetic call that is quietly half
        // as dense as the archive proves nothing about the archive.
        var characters = lines.Sum(l => l.Text.Length);
        Assert.InRange(characters, minutes * CharactersPerMinute * 95 / 100, minutes * CharactersPerMinute * 105 / 100);

        return call;
    }

    private static AnalysisOptions SummarisingOn(string model) => new()
    {
        Model = model,
        SendsDataOffMachine = false,
        AdjudicateContradictions = false,
        WriteSummary = true,
    };

    private static int SummaryLimitOn(string model) =>
        PromptBudget.For(model, sendsDataOffMachine: false).CharacterLimit(
            ExtractionPrompt.ConversationSummarySystemPrompt.Length, AnalysisPipeline.SummaryAnswerTokens);

    // ---- the estimate and the cap --------------------------------------------

    /// <summary>
    /// The token estimate has to overstate what the archive measured, by a margin.
    ///
    /// Sixty-nine minutes of this machine's archive came to 65,853 characters and about 16k
    /// tokens on the default model's tokenizer — bare text, no labels. The prompt is not bare:
    /// "[mm:ss] SEN: " on every line tokenizes at about two characters a token and pulls a
    /// labelled line to roughly 3.5. Red when the estimate drifts toward the bare-text rate:
    /// then the arithmetic admits a prompt the server refuses, which is the reported fault.
    /// </summary>
    [Fact]
    public void TheTokenEstimateOverstatesWhatTheArchiveMeasured()
    {
        const int measuredCharacters = 65_853;
        const int measuredTokens = 16_000;

        Assert.True(
            PromptBudget.EstimateTokens(measuredCharacters) >= measuredTokens * 5 / 4,
            $"{PromptBudget.EstimateTokens(measuredCharacters)} token tahmini, ölçülen {measuredTokens} üstüne pay bırakmıyor");
    }

    /// <summary>
    /// The cap is the model's window when the catalogue knows it, and the flat limit when not.
    ///
    /// Requests name a local model by file name (llama-server, LM Studio) or by id (Ollama), so
    /// both must resolve; a tag the catalogue never heard of must fall back to the flat limit
    /// rather than to a guess, and a cloud provider must not be looked up at all. Red when the
    /// lookup stops matching how requests name models — every local model then gets the flat
    /// 24 thousand, and the default model's 32k window refuses calls it holds comfortably.
    /// </summary>
    [Fact]
    public void TheCapComesFromTheModelsWindowWhenTheCatalogueKnowsItAndFromTheFlatLimitWhenItDoesNot()
    {
        Assert.Equal(16_384, PromptBudget.For(SixteenThousand, sendsDataOffMachine: false).ContextTokens);
        Assert.Equal(32_768, PromptBudget.For(ThirtyTwoThousand, sendsDataOffMachine: false).ContextTokens);
        Assert.Equal(32_768, PromptBudget.For("qwen3.5-4b-q6k", sendsDataOffMachine: false).ContextTokens);

        var unknown = PromptBudget.For("qwen3.5:4b", sendsDataOffMachine: false);
        Assert.Null(unknown.ContextTokens);
        Assert.Equal(PromptBudget.LocalCharacterLimit, unknown.CharacterLimit(3_000, 2_048));

        var cloud = PromptBudget.For(ThirtyTwoThousand, sendsDataOffMachine: true);
        Assert.Null(cloud.ContextTokens);
        Assert.Equal(PromptBudget.CloudCharacterLimit, cloud.CharacterLimit(3_000, 2_048));
    }

    /// <summary>
    /// The answer and the instructions are paid out of the window before the conversation is.
    ///
    /// A prompt that fills the context leaves the model nothing to answer with — the request
    /// is accepted and the reply is cut off, which the caller reports as "yarıda kesildi" and
    /// the user reads as the model failing. Red when either reservation disappears.
    /// </summary>
    [Fact]
    public void TheCapLeavesRoomForTheInstructionsAndTheAnswer()
    {
        var budget = new PromptBudget(SendsDataOffMachine: false, ContextTokens: 16_384);

        var expected = (16_384 - 2_048 - PromptBudget.EstimateTokens(3_000)) * PromptBudget.CharactersPerToken;

        Assert.Equal(expected, budget.CharacterLimit(overheadCharacters: 3_000, answerTokens: 2_048));
        Assert.True(expected < 16_384 * PromptBudget.CharactersPerToken);
    }

    // ---- the reading -----------------------------------------------------------

    /// <summary>
    /// A fifty-minute call on a 16k-token model is refused before anything is paid for, and the
    /// refusal names the size and the fix.
    ///
    /// This is the reported defect: the whole transcript went to the server, overflowed it, and
    /// came back as an error about tokens — or, on a backend that truncates silently, as a
    /// reading of the second half presented as the whole. Red when the reading stops checking,
    /// when the check runs after the request, or when the sentence loses its numbers.
    /// </summary>
    [Fact]
    public async Task AFiftyMinuteCallIsRefusedByTheReadingOnASixteenThousandTokenModelWithoutARequest()
    {
        var call = Seed(50);
        var llm = new ForbiddenLlm();

        var report = await new ReadingAnalysis(llm, _repo).RunAsync(
            call, SixteenThousand, sendsDataOffMachine: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Ok);
        Assert.Equal(0, llm.Calls);

        var prompt = ReadingPrompt.BuildUserPrompt(_repo.GetSegments(call), "Serdal");
        Assert.Contains($"{prompt.Length / 1000} bin karakter", report.Problem!);
        Assert.Contains("Ayarlar", report.Problem!);
        Assert.Contains("gönderilmedi", report.Problem!);
    }

    /// <summary>
    /// The same call fits the default model's 32k window, and fits the cloud.
    ///
    /// Red when the cap is no longer derived from the window — the flat 24 thousand would
    /// refuse a call the default model holds with room to spare, and the user's fifty-minute
    /// calls would be unreadable on the recommended setup.
    /// </summary>
    [Fact]
    public async Task AFiftyMinuteCallFitsTheReadingOnTheDefaultModelAndInTheCloud()
    {
        var call = Seed(50);
        var llm = new ScriptedLlm();

        var local = await new ReadingAnalysis(llm, _repo).RunAsync(
            call, ThirtyTwoThousand, sendsDataOffMachine: false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(local.Ok, local.Problem);

        var cloud = await new ReadingAnalysis(llm, _repo).RunAsync(
            call, "claude-sonnet-5", sendsDataOffMachine: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloud.Ok, cloud.Problem);

        Assert.Equal(2, llm.Requests.Count);
    }

    // ---- the assessment --------------------------------------------------------

    /// <summary>
    /// The opt-in assessment refuses the same call on the same model, for the same reason, and
    /// pays nothing. Red when it stops checking or checks after the request.
    /// </summary>
    [Fact]
    public async Task AFiftyMinuteCallIsRefusedByTheAssessmentOnASixteenThousandTokenModelWithoutARequest()
    {
        var call = Seed(50);
        var llm = new ForbiddenLlm();

        var report = await new DeceptionAnalysis(llm, _repo).RunAsync(
            call, SixteenThousand, sendsDataOffMachine: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Ok);
        Assert.Equal(0, llm.Calls);

        var prompt = DeceptionPrompt.BuildUserPrompt(_repo.GetSegments(call));
        Assert.Contains($"{prompt.Length / 1000} bin karakter", report.Problem!);
        Assert.Contains("Ayarlar", report.Problem!);
    }

    /// <summary>The assessment fits the default model and the cloud. Red for the reading's reason.</summary>
    [Fact]
    public async Task AFiftyMinuteCallFitsTheAssessmentOnTheDefaultModelAndInTheCloud()
    {
        var call = Seed(50);
        var llm = new ScriptedLlm();

        var local = await new DeceptionAnalysis(llm, _repo).RunAsync(
            call, ThirtyTwoThousand, sendsDataOffMachine: false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(local.Ok, local.Problem);

        var cloud = await new DeceptionAnalysis(llm, _repo).RunAsync(
            call, "gpt-6", sendsDataOffMachine: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(cloud.Ok, cloud.Problem);

        Assert.Equal(2, llm.Requests.Count);
    }

    // ---- the summary -----------------------------------------------------------

    /// <summary>
    /// A call that does not fit the summary's window is summarised from its two ends, under the
    /// cap, and the report says so in minutes.
    ///
    /// Red in three ways, each a regression the user would see: the request grows past the cap
    /// (the overflow is back), the middle is sent anyway or an end is dropped (the summary
    /// describes a different call), or the notice goes null (a summary of a window is shown as
    /// a summary of the call, which is the silent failure this replaces).
    /// </summary>
    [Fact]
    public async Task TheSummaryOfACallThatDoesNotFitIsWrittenFromItsTwoEndsAndSaysSo()
    {
        var call = Seed(50);
        var llm = new ScriptedLlm();

        var report = await new AnalysisPipeline(llm, _repo).AnalyseAsync(
            call, SummarisingOn(SixteenThousand), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ScriptedLlm.Summary, report.Summary);
        Assert.NotNull(_repo.GetSummary(call));

        var request = Assert.Single(llm.Requests, r => r.JsonSchema is null);
        Assert.True(request.UserPrompt.Length <= SummaryLimitOn(SixteenThousand),
            $"{request.UserPrompt.Length} karakter, sınır {SummaryLimitOn(SixteenThousand)}");

        Assert.Contains(ExtractionPrompt.SkippedMiddleMarker, request.UserPrompt);
        Assert.Contains("0000", request.UserPrompt);
        Assert.Contains("0499", request.UserPrompt);

        // The sentence carries the window in minutes, and the minutes add up to the call.
        Assert.NotNull(report.SummaryNotice);
        var minutes = System.Text.RegularExpressions.Regex.Matches(report.SummaryNotice, @"\d+")
            .Select(m => int.Parse(m.Value)).ToList();
        Assert.Equal(3, minutes.Count);
        Assert.InRange(minutes.Sum(), 49, 51);
        Assert.True(minutes[2] > 0, "sığmayan bir görüşmede atlanan bölüm sıfır dakika olamaz");
        Assert.True(minutes[1] > minutes[0], "son bölüm baştan büyük olmalı: anlaşmalar sonda");
        Assert.Equal(
            string.Format(Localisation.T("analysispipeline.ozet-pencereden"), minutes[0], minutes[1], minutes[2]),
            report.SummaryNotice);

        // And the minutes are true of the prompt: the line in the middle of the stretch the
        // sentence calls skipped is not there. Ten lines a minute, so the head ends at line
        // head×10 and the skipped stretch's midpoint is half its minutes further on.
        var skippedLine = minutes[0] * 10 + minutes[2] * 10 / 2;
        Assert.DoesNotContain($"{skippedLine:0000}", request.UserPrompt);
    }

    /// <summary>
    /// The same call fits the default model's window whole, and nothing is said about it.
    ///
    /// Red when the window is sized from the flat limit or the old twelve thousand rather than
    /// from the model: a call the model holds would be cut, and the user told about a cut that
    /// need not have happened.
    /// </summary>
    [Fact]
    public async Task TheSummaryOfACallThatFitsIsWrittenWholeWithNoNotice()
    {
        var call = Seed(50);
        var llm = new ScriptedLlm();

        var report = await new AnalysisPipeline(llm, _repo).AnalyseAsync(
            call, SummarisingOn(ThirtyTwoThousand), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ScriptedLlm.Summary, report.Summary);
        Assert.Null(report.SummaryNotice);

        var request = Assert.Single(llm.Requests, r => r.JsonSchema is null);
        Assert.DoesNotContain(ExtractionPrompt.SkippedMiddleMarker, request.UserPrompt);
        Assert.Contains("0000", request.UserPrompt);
        Assert.Contains("0250", request.UserPrompt);
        Assert.Contains("0499", request.UserPrompt);
    }

    /// <summary>
    /// A summary the provider refused is a sentence on the report, never a silent null.
    ///
    /// Both summary paths swallowed the provider's exception. On a long call against a small
    /// model that was the entire visible symptom: no summary and no reason, while the ledger
    /// looked complete. Red when the exception is swallowed again — the report then carries a
    /// null summary and no notice, and the orchestrator has nothing to say.
    /// </summary>
    [Fact]
    public async Task ASummaryTheProviderRefusedIsASentenceNotASilentNull()
    {
        var call = Seed(5);

        const string reason = "Görüşme metni bu modelin alabileceğinden uzun.";
        var llm = new ScriptedLlm(request => request.JsonSchema is null
            ? throw new LlmException(reason)
            : new LlmResponse("""{"taahhutler":[],"iddialar":[],"sorular":[],"baski_isaretleri":[]}""", "stop", 10, 5));

        var report = await new AnalysisPipeline(llm, _repo).AnalyseAsync(
            call, SummarisingOn(ThirtyTwoThousand), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(report.Summary);
        Assert.Null(_repo.GetSummary(call));
        Assert.Equal(string.Format(Localisation.T("analysispipeline.ozet-yazilamadi"), reason), report.SummaryNotice);

        // The ledger half of the run is untouched by the summary's failure.
        Assert.False(report.Partial);
    }

    // ---- the catalogue -----------------------------------------------------------

    /// <summary>
    /// What a fifty-minute call does on every model in the catalogue, pinned.
    ///
    /// The recommended model has to take the user's own calls whole — that was the complaint —
    /// and every 16k model has to refuse rather than be sent a request it cannot hold. Red when
    /// the catalogue's windows change, or the estimate does, in a way that moves either answer;
    /// the message says which model moved.
    /// </summary>
    [Fact]
    public void WhatAFiftyMinuteCallDoesOnEveryCataloguedLocalModel()
    {
        var segments = SyntheticCall(callId: 1, minutes: 50);
        var prompt = ReadingPrompt.BuildUserPrompt(segments, "Serdal");
        var overhead = ReadingPrompt.BuildSystemPrompt("Serdal").Length + ReadingPrompt.Schema.ToJsonString().Length;

        foreach (var model in LocalLlmCatalog.All)
        {
            var name = model.FileName.Length > 0 ? model.FileName : model.Id;
            var budget = PromptBudget.For(name, sendsDataOffMachine: false);
            var fits = budget.Refuse(prompt, overhead, ReadingAnalysis.AnswerTokens) is null;

            Assert.Equal(model.ContextTokens, budget.ContextTokens);

            if (model.Id == LocalLlmCatalog.DefaultModelId)
                Assert.True(fits, $"{model.DisplayName}: varsayılan model elli dakikalık görüşmeyi tek istekte almalı");
            else if (model.ContextTokens <= 16_384)
                Assert.False(fits, $"{model.DisplayName}: 16 bin bağlama elli dakika sığdırılmamalı");
        }
    }

}
