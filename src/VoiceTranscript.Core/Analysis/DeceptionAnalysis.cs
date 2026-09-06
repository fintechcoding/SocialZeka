using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Analysis;

/// <summary>One asserted tactic, playable because its quote verified against the transcript.</summary>
/// <param name="LowConfidence">
/// Carried from the located line, so the card can grey a sentence the transcriber doubted
/// instead of counting it as if it had been heard clearly. False on rows stored before this
/// existed, which is the honest reading: nothing recorded it then.
/// </param>
public sealed record DeceptionLine(
    string Tactic, bool IsMe, string Reason, string Quote, int StartMs, bool LowConfidence = false)
{
    public string TacticLabel => Tactic switch
    {
        "baski" => "Baskı",
        "sucluluk" => "Suçluluk yükleme",
        "kacamak" => "Kaçamak",
        "geri_yazim" => "Geriye yazım",
        "asiri_iltifat" => "Aşırı iltifat",
        "aciliyet" => "Yapay aciliyet",
        "tehdit_imasi" => "Tehdit iması",
        "celiski_ortme" => "Çelişki örtme",
        _ => "Diğer",
    };

    public string SpeakerLabel => IsMe ? "SEN" : "O";
}

/// <summary>The model's deception assessment for one call, after code-level enforcement.</summary>
/// <param name="RejectedCount">Lines whose quote could not be located in the transcript.</param>
/// <param name="EvidenceDropped">
/// Verified lines that were kept in this note but NOT copied to the person's card, because the
/// label was not one of the eight. Counted rather than swallowed: a model whose labels are
/// routinely thrown away is one to stop using for this, and a silent drop would look like a
/// person with no patterns.
/// </param>
public sealed record DeceptionReport(
    string Level,
    string Assessment,
    IReadOnlyList<DeceptionLine> Tactics,
    int RejectedCount,
    bool Insufficient,
    bool Ok = true,
    string? Problem = null,
    int EvidenceDropped = 0)
{
    public static DeceptionReport Failed(string problem) =>
        new("yok", "", [], 0, false, false, problem);

    /// <summary>True when the stated level warrants a place on the attention strip.</summary>
    public bool IsElevated => Level is "orta" or "yuksek";
}

/// <summary>
/// Produces and stores the opt-in deception/manipulation assessment.
///
/// The user chose to hear an explicit opinion, so the opinion is delivered plainly — but the
/// evidence-fidelity law survives untouched: a tactic whose quote cannot be located in the
/// transcript is dropped in code, because an STT ghost must never brand anyone. The stored
/// JSON is the enforced shape and a dead end — no other prompt receives it, nothing joins it.
///
/// One thing now leaves this run, and only one: a VERIFIED tactic quote is copied to
/// tactic_evidence so it can be counted on the person's card. The level and the assessment
/// paragraph do not go with it, and nothing in that table is ever fed back to a model — what
/// travels is a machine-verified sentence with a label, which is the same class of thing the
/// consistency check has always written to the ledger. The judgement stays here.
/// </summary>
public sealed class DeceptionAnalysis(ILlmClient llm, Repository repository)
{
    public const int MaxTactics = 6;

    public async Task<DeceptionReport> RunAsync(
        long callId, string model, CancellationToken cancellationToken = default)
    {
        var segments = repository.GetSegments(callId);
        if (segments.Count == 0)
            return DeceptionReport.Failed("Bu görüşmenin metni yok — önce yazıya dökülmesi gerekir.");

        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        LlmResponse response;
        try
        {
            response = await llm.CompleteAsync(new LlmRequest
            {
                Model = model,
                SystemPrompt = DeceptionPrompt.SystemPrompt,
                UserPrompt = DeceptionPrompt.BuildUserPrompt(segments),
                JsonSchema = DeceptionPrompt.Schema,
                Temperature = 0.2,
                MaxTokens = 1536,
                UnloadAfterwards = true,
            }, cancellationToken);
        }
        catch (LlmException e)
        {
            clock.Stop();
            repository.RecordRun(callId, ProcessingStage.Deception, model, startedAt, clock.Elapsed,
                audio: TimeSpan.Zero, succeeded: false);

            return DeceptionReport.Failed($"Modele ulaşılamadı: {e.Message}");
        }

        clock.Stop();
        repository.RecordRun(callId, ProcessingStage.Deception, model, startedAt, clock.Elapsed,
            audio: TimeSpan.Zero, response.PromptTokens, response.CompletionTokens);

        if (!response.CompletedNormally)
            return DeceptionReport.Failed("Model cevabı yarıda kesildi.");

        JsonNode? root;
        try
        {
            root = AnalysisPipeline.CoerceToObject(JsonNode.Parse(response.Content));
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
            return DeceptionReport.Failed("Model geçerli bir değerlendirme döndürmedi.");

        var report = Shape(root, segments);

        // What goes onto the person's card, and what does not.
        //
        // ONLY THE VERIFIED QUOTE TRAVELS. The suspicion level and the assessment paragraph are
        // the opinion, and they stay in this note — nothing below reads them, and no prompt ever
        // reads what is written here. A tactic whose label is not one of the eight is dropped
        // rather than filed as "diger": the card counts these rows as patterns in somebody's
        // history, and a bucket named "other" would fill up with whatever a model typed.
        var evidence = report.Tactics
            .Where(line => TacticEvidence.Recognise(line.Tactic) is not null)
            .Select(line => new TacticEvidence
            {
                CallId = callId,
                Source = TacticEvidence.Sources.Deception,
                Tactic = line.Tactic,
                ByMe = line.IsMe,
                Quote = line.Quote,
                QuoteStartMs = line.StartMs,
                LowConfidence = line.LowConfidence,
                ModelUsed = model,
                CreatedAt = DateTimeOffset.UtcNow,
            })
            .ToList();

        report = report with { EvidenceDropped = report.Tactics.Count - evidence.Count };

        // The enforced shape is what is stored — dropped tactics stay dropped on reopen.
        repository.SaveDeception(callId, JsonSerializer.Serialize(report), model);

        var written = repository.ReplaceTacticEvidence(callId, TacticEvidence.Sources.Deception, evidence);

        if (report.EvidenceDropped > 0 || written != evidence.Count)
        {
            CoreLog.Write("cozumleme",
                $"gorusme #{callId}: {written} taktik alintisi kisi kartina yazildi; "
                + $"{report.EvidenceDropped} bilinmeyen etiket dustu, "
                + $"{evidence.Count - written} kullanicinin reddettigi satir geri gelmedi");
        }

        return report;
    }

    /// <summary>Rebuilds a stored assessment for display. Null when none was ever produced.</summary>
    public static DeceptionReport? FromStored(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DeceptionReport>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DeceptionReport Shape(JsonNode root, IReadOnlyList<Segment> segments)
    {
        var rejected = 0;

        List<DeceptionLine> tactics = [];
        foreach (var node in root["taktikler"] is JsonArray items ? items.OfType<JsonObject>() : [])
        {
            if (tactics.Count >= MaxTactics) break;

            // The one law the opt-in does not loosen: an accusation-shaped row without a
            // verifiable quote is removed, not softened.
            if (QuoteVerifier.Locate(Str(node, "alinti"), segments) is not { } located)
            {
                rejected++;
                continue;
            }

            tactics.Add(new DeceptionLine(
                Str(node, "taktik") ?? "diger",
                // The stream the quote was found in, not the model's opinion of who spoke.
                //
                // This is the assessment that names a person as manipulative. Pinning it on the
                // wrong party is the worst single output this application can produce, and the
                // speaker was being taken from a free-text field the model filled in.
                located.IsMe,
                Str(node, "gerekce") ?? "",
                located.Text,
                located.StartMs,
                // Carried from the audio, so a sentence the transcriber was unsure about is
                // shown as uncertain wherever it is counted rather than silently equal.
                located.LowConfidence));
        }

        var stated = Str(root, "duzey");
        var level = stated is "yok" or "dusuk" or "orta" or "yuksek" ? stated : "yok";

        // A level built on tactics that all failed verification is a level built on nothing.
        if (tactics.Count == 0 && level is "orta" or "yuksek") level = "dusuk";

        bool insufficient;
        try
        {
            insufficient = root["yetersiz"]?.GetValue<bool>() ?? false;
        }
        catch (Exception)
        {
            insufficient = string.Equals(root["yetersiz"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        return new DeceptionReport(
            level,
            (Str(root, "degerlendirme") ?? "").Trim(),
            tactics,
            rejected,
            insufficient);
    }

    private static string? Str(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return null;

        try
        {
            return value.GetValue<string>();
        }
        catch (Exception)
        {
            return value.ToString();
        }
    }
}
