using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Analysis;

/// <summary>One quote-bearing line of the reading, playable when the quote verified.</summary>
/// <param name="StartMs">The verified moment, or null when the quote was not located.</param>
public sealed record ReadingLine(string Text, string? Quote, int? StartMs, bool IsMe);

/// <summary>The model's reading of one conversation, after code-level enforcement.</summary>
public sealed record ReadingReport(
    string GeneralReading,
    string NegotiationState,
    IReadOnlyList<ReadingLine> StyleObservations,
    IReadOnlyList<ReadingLine> RiskPoints,
    IReadOnlyList<ReadingLine> Unresolved,
    string CounterReading,
    IReadOnlyList<ReadingLine> SuggestedQuestions,
    int RejectedCount,
    bool Insufficient,
    bool Ok = true,
    string? Problem = null)
{
    public static ReadingReport Failed(string problem) =>
        new("", "", [], [], [], "", [], 0, false, false, problem);
}

/// <summary>
/// Produces and stores the model's free reading of one conversation.
///
/// This is the product's one deliberately subjective surface — the user chose it knowingly.
/// What stays law regardless: risk items and style observations lose their rows when their
/// quotes cannot be located (an accusation-adjacent line with no verifiable words is not
/// shown); risks are capped at three; the counter-reading is required; and the stored JSON is
/// a dead end — nothing joins on it, no other prompt receives it, no other screen shows it.
/// </summary>
public sealed class ReadingAnalysis(ILlmClient llm, Repository repository)
{
    public const int MaxRisks = 3;
    public const int MaxQuestions = 3;

    public async Task<ReadingReport> RunAsync(
        long callId, string model, string? preferredName = null,
        CancellationToken cancellationToken = default)
    {
        var segments = repository.GetSegments(callId);
        if (segments.Count == 0)
            return ReadingReport.Failed("Bu görüşmenin metni yok — önce yazıya dökülmesi gerekir.");

        // Who this conversation was with, as the user filed it. Null when the call has not been
        // named yet, and the prompt then says "karşı taraf" rather than guessing.
        var otherParty = repository.GetCall(callId)?.ContactId is { } contactId
            ? repository.GetContact(contactId)?.Name
            : null;

        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        LlmResponse response;
        try
        {
            response = await llm.CompleteAsync(new LlmRequest
            {
                Model = model,
                SystemPrompt = ReadingPrompt.BuildSystemPrompt(otherParty, preferredName),
                UserPrompt = ReadingPrompt.BuildUserPrompt(segments, otherParty, preferredName),
                JsonSchema = ReadingPrompt.Schema,

                // A reading wants a voice; extraction temperatures read like meeting minutes.
                Temperature = 0.3,
                MaxTokens = 2048,
                UnloadAfterwards = true,
            }, cancellationToken);
        }
        catch (LlmException e)
        {
            clock.Stop();
            repository.RecordRun(callId, ProcessingStage.Reading, model, startedAt, clock.Elapsed,
                audio: TimeSpan.Zero, succeeded: false);

            return ReadingReport.Failed($"Modele ulaşılamadı: {e.Message}");
        }

        clock.Stop();
        repository.RecordRun(callId, ProcessingStage.Reading, model, startedAt, clock.Elapsed,
            audio: TimeSpan.Zero, response.PromptTokens, response.CompletionTokens);

        if (!response.CompletedNormally)
            return ReadingReport.Failed("Model cevabı yarıda kesildi.");

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
            return ReadingReport.Failed("Model geçerli bir okuma döndürmedi.");

        var report = Shape(root, segments);

        // Stored as the ENFORCED shape, not the raw reply — what comes back on reopen is
        // exactly what was shown, dropped rows staying dropped.
        repository.SaveReading(callId, Serialize(report), model);

        return report;
    }

    /// <summary>Rebuilds a stored reading for display. Null when none was ever produced.</summary>
    public static ReadingReport? FromStored(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReadingReport>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Serialize(ReadingReport report) => JsonSerializer.Serialize(report);

    private ReadingReport Shape(JsonNode root, IReadOnlyList<Segment> segments)
    {
        var rejected = 0;

        // Style observations and risks live or die by their quotes — these are the lines
        // closest to accusations, and an unverifiable one is not softened, it is removed.
        List<ReadingLine> style = [];
        foreach (var node in Items(root, "uslup_gozlemleri"))
        {
            if (QuoteVerifier.Locate(Str(node, "alinti"), segments) is { } q)
                style.Add(new ReadingLine(Str(node, "gozlem") ?? "", q.Text, q.StartMs, q.IsMe));
            else rejected++;
        }

        List<ReadingLine> risks = [];
        foreach (var node in Items(root, "risk_noktalari"))
        {
            if (risks.Count >= MaxRisks) break;

            if (QuoteVerifier.Locate(Str(node, "alinti"), segments) is { } q)
            {
                var text = Str(node, "okuma") ?? "";
                var basis = Str(node, "dayanak");
                if (!string.IsNullOrWhiteSpace(basis)) text = $"{text} — {basis}";

                risks.Add(new ReadingLine(text.Trim(), q.Text, q.StartMs, q.IsMe));
            }
            else rejected++;
        }

        // Unresolved topics and questions degrade gracefully: quote verified → playable,
        // otherwise plain prose. They observe, they do not accuse.
        List<ReadingLine> unresolved = [];
        foreach (var node in Items(root, "cozulmeyenler"))
        {
            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            unresolved.Add(new ReadingLine(
                Str(node, "konu") ?? "", located?.Text, located?.StartMs, located?.IsMe ?? false));
        }

        List<ReadingLine> questions = [];
        foreach (var node in Items(root, "sorulacak_sorular"))
        {
            if (questions.Count >= MaxQuestions) break;

            var text = Str(node, "soru") ?? "";
            var why = Str(node, "neden");
            if (!string.IsNullOrWhiteSpace(why)) text = $"{text} — {why}";

            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            questions.Add(new ReadingLine(text.Trim(), located?.Text, located?.StartMs, located?.IsMe ?? false));
        }

        bool insufficient;
        try
        {
            insufficient = root["yetersiz"]?.GetValue<bool>() ?? false;
        }
        catch (Exception)
        {
            insufficient = string.Equals(root["yetersiz"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        return new ReadingReport(
            (Str(root, "genel_yorum") ?? "").Trim(),
            (Str(root, "muzakere_durumu") ?? "").Trim(),
            style,
            risks,
            unresolved,
            (Str(root, "baska_okuma") ?? "").Trim(),
            questions,
            rejected,
            insufficient);
    }

    private static IEnumerable<JsonObject> Items(JsonNode root, string name)
        => root[name] is JsonArray items ? items.OfType<JsonObject>() : [];

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
