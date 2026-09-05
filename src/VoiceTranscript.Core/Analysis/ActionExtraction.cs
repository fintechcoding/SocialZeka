using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Analysis;

/// <summary>What one action-extraction run produced, after verification and dedup.</summary>
public sealed record ActionReport(
    IReadOnlyList<ActionItem> Actions,
    int RejectedCount,
    bool Ok = true,
    string? Problem = null)
{
    public static ActionReport Failed(string problem) => new([], 0, false, problem);
}

/// <summary>
/// Extracts the user's proposed next moves from one conversation.
///
/// Follows the consistency check's law to the letter: quotes are verified in code and an
/// unanchored suggestion is dropped; a re-run replaces only OPEN suggestions (done, hidden
/// and routed rows are the user's history with the list); a hidden suggestion's identity is
/// remembered and never resurrected; and a suggestion that merely restates an
/// already-recorded commitment is discarded — the ledger owns what was said, this owns what
/// to do about it.
/// </summary>
public sealed class ActionExtraction(ILlmClient llm, Repository repository)
{
    /// <summary>Suggestions per conversation, capped in code — few and sharp beats many.</summary>
    public const int MaxActions = 5;

    public async Task<ActionReport> RunAsync(
        long callId, string model, CancellationToken cancellationToken = default)
    {
        var call = repository.GetCall(callId);
        if (call is null) return ActionReport.Failed("Görüşme bulunamadı.");

        var segments = repository.GetSegments(callId);
        if (segments.Count == 0)
            return ActionReport.Failed("Bu görüşmenin metni yok.");

        // This call's own recorded commitments — both the prompt's "don't repeat these" block
        // and the code-level dedup below read from this list.
        var commitments = call.ContactId is { } contactId
            ? [.. repository.GetOpenCommitments(contactId).Where(c => c.CallId == callId)]
            : new List<Commitment>();

        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        LlmResponse response;
        try
        {
            response = await llm.CompleteAsync(new LlmRequest
            {
                Model = model,
                SystemPrompt = ActionPrompt.SystemPrompt,
                UserPrompt = ActionPrompt.BuildUserPrompt(segments, commitments),
                JsonSchema = ActionPrompt.Schema,
                Temperature = 0.2,
                MaxTokens = 1024,
                UnloadAfterwards = true,
            }, cancellationToken);
        }
        catch (LlmException e)
        {
            clock.Stop();
            repository.RecordRun(callId, ProcessingStage.Action, model, startedAt, clock.Elapsed,
                audio: TimeSpan.Zero, succeeded: false);

            return ActionReport.Failed($"Modele ulaşılamadı: {e.Message}");
        }

        clock.Stop();
        repository.RecordRun(callId, ProcessingStage.Action, model, startedAt, clock.Elapsed,
            audio: TimeSpan.Zero, response.PromptTokens, response.CompletionTokens);

        if (!response.CompletedNormally)
            return ActionReport.Failed("Model cevabı yarıda kesildi.");

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
            return ActionReport.Failed("Model geçerli bir çıktı döndürmedi.");

        return Absorb(root, call, segments, commitments, model);
    }

    private ActionReport Absorb(
        JsonNode root, Call call, IReadOnlyList<Segment> segments,
        IReadOnlyList<Commitment> commitments, string model)
    {
        var hidden = repository.HiddenActionKeys(call.Id);

        // Deadlines are counted from the day of the call, not from today: a suggestion re-run
        // on an old call keeps the date the speaker meant.
        var spokenOn = DateOnly.FromDateTime(call.StartedAt.LocalDateTime);

        // Commitment quotes, folded — a suggestion anchored to the same words as a recorded
        // promise is the promise restated, unless it is explicitly a follow-up on it.
        var commitmentQuotes = commitments
            .Select(c => TurkishText.NormalizeForSearch(c.Quote))
            .ToHashSet(StringComparer.Ordinal);

        // Replace-then-insert, open rows only: the user's decisions on old suggestions
        // (done / hidden / routed) are history, not machine output, and stay.
        repository.ClearOpenActions(call.Id);

        List<ActionItem> kept = [];
        var rejected = 0;

        foreach (var node in root["aksiyonlar"] is JsonArray items ? items.OfType<JsonObject>() : [])
        {
            if (kept.Count >= MaxActions) break;

            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            if (located is null) { rejected++; continue; }

            var action = Str(node, "eylem")?.Trim();
            if (string.IsNullOrWhiteSpace(action)) { rejected++; continue; }

            var stated = Str(node, "tur");
            var kind = stated is "yazili_teyit" or "gonderme" or "soru" or "takip" or "hazirlik"
                ? stated
                : "diger";

            // "Send the document" while "I'll send the document" is already a recorded promise
            // is a copy; "chase it on Friday if it doesn't arrive" is a follow-up and stands.
            if (kind != "takip"
                && commitmentQuotes.Contains(TurkishText.NormalizeForSearch(located.Text)))
            {
                continue;
            }

            if (hidden.Contains((
                    TurkishText.NormalizeForSearch(action),
                    TurkishText.NormalizeForSearch(located.Text))))
            {
                continue;
            }

            var deadlineRaw = Str(node, "tarih_ham");

            var item = new ActionItem
            {
                CallId = call.Id,
                ContactId = call.ContactId,
                Action = action,
                Reason = Str(node, "neden"),
                Kind = kind,
                Quote = located.Text,
                QuoteStartMs = located.StartMs,
                QuoteIsMe = located.IsMe,
                DeadlineRaw = deadlineRaw,
                DeadlineDate = TurkishDates.TryResolve(deadlineRaw, spokenOn),
                ModelUsed = model,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            kept.Add(item with { Id = repository.InsertAction(item) });
        }

        return new ActionReport(kept, rejected);
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
