using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Analysis;

/// <summary>One asserted tactic, playable because its quote verified against the transcript.</summary>
public sealed record DeceptionLine(string Tactic, bool IsMe, string Reason, string Quote, int StartMs)
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
public sealed record DeceptionReport(
    string Level,
    string Assessment,
    IReadOnlyList<DeceptionLine> Tactics,
    int RejectedCount,
    bool Insufficient,
    bool Ok = true,
    string? Problem = null)
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

        // The enforced shape is what is stored — dropped tactics stay dropped on reopen.
        repository.SaveDeception(callId, JsonSerializer.Serialize(report), model);

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
                string.Equals(Str(node, "konusan"), "BEN", StringComparison.OrdinalIgnoreCase),
                Str(node, "gerekce") ?? "",
                located.Text,
                located.StartMs));
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
