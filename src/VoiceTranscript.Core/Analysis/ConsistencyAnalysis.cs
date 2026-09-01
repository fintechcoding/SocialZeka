using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Analysis;

/// <summary>One numbered prior statement handed to the model, with its exact anchor kept here.</summary>
/// <param name="Number">The [B#] the model refers to it by.</param>
public sealed record PriorStatement(
    int Number, long CallId, int QuoteStartMs, string Quote, string Line);

/// <summary>What one consistency run produced, after verification.</summary>
/// <param name="Findings">The findings that survived quote verification, already persisted.</param>
/// <param name="Warning">The justified warning note — null when the evidence did not earn one.</param>
/// <param name="Observations">The balancing list: what held together.</param>
/// <param name="RejectedCount">Findings dropped because their quotes were not in the transcript.</param>
public sealed record ConsistencyReport(
    IReadOnlyList<Flag> Findings,
    string? Warning,
    IReadOnlyList<string> Observations,
    int RejectedCount,
    bool Insufficient,
    bool Ok = true,
    string? Problem = null)
{
    public static ConsistencyReport Failed(string problem) => new([], null, [], 0, false, false, problem);
}

/// <summary>
/// Reads one conversation for what can be SHOWN: contradictions, evaded questions, timelines
/// that do not add up, sudden vagueness, pressure patterns — each carried by verbatim quotes.
///
/// Deliberately not a lie detector, and the code enforces what the prompt requests. A finding
/// whose quote cannot be located in the transcript is dropped, exactly as the ledger drops
/// invented quotes; and the overall warning note survives only when at least one finding did —
/// a model cannot warn the user about evidence it failed to produce. The prompt bans verdict
/// language; the product's honesty does not depend on the model obeying, because everything
/// shown is either a located quote or discarded.
///
/// Findings persist as flags with <see cref="Flag.Sources.Consistency"/>, so they live where
/// every other piece of evidence lives, dismissals stick, and the ledger rebuild cannot erase
/// what this (separately paid) run found.
/// </summary>
public sealed class ConsistencyAnalysis(ILlmClient llm, Repository repository)
{
    /// <summary>Prior-context lines offered to the model, newest first.</summary>
    private const int MaxPriorStatements = 30;

    /// <summary>
    /// Transcript size caps, in characters. No chunking, ever: a contradiction is precisely a
    /// long-range dependency, and analysing half a conversation for consistency is analysing a
    /// different conversation. Cloud models hold multi-hour calls whole; a local server does
    /// not, and pretending otherwise would silently truncate evidence.
    /// </summary>
    public const int CloudCharacterLimit = 400_000;

    public const int LocalCharacterLimit = 24_000;

    public async Task<ConsistencyReport> RunAsync(
        long callId,
        string model,
        bool useLedgerContext = true,
        bool otherPartyOnly = false,
        bool sendsDataOffMachine = true,
        CancellationToken cancellationToken = default)
    {
        var call = repository.GetCall(callId);
        if (call is null) return ConsistencyReport.Failed("Görüşme bulunamadı.");

        var segments = repository.GetSegments(callId);
        if (segments.Count == 0)
            return ConsistencyReport.Failed("Bu görüşmenin metni yok — önce yazıya dökülmesi gerekir.");

        var transcript = BuildTranscript(segments);
        var limit = sendsDataOffMachine ? CloudCharacterLimit : LocalCharacterLimit;

        if (transcript.Length > limit)
        {
            return ConsistencyReport.Failed(
                $"Bu görüşme tek istekte denetlenemeyecek kadar uzun ({transcript.Length / 1000} bin karakter). "
                + (sendsDataOffMachine
                    ? "Tutarlılık denetimi parçalayarak çalışamaz — çelişki, görüşmenin iki ucu arasındadır."
                    : "Yerel modelin bağlam penceresi yetmez; Ayarlar'dan bulut tabanlı bir model seçilebilir."));
        }

        var priors = useLedgerContext && call.ContactId is { } contactId
            ? BuildPriorStatements(contactId, callId)
            : [];

        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        LlmResponse response;
        try
        {
            response = await llm.CompleteAsync(new LlmRequest
            {
                Model = model,
                SystemPrompt = ConsistencyPrompt.SystemPrompt,
                UserPrompt = ConsistencyPrompt.BuildUserPrompt(transcript, priors, otherPartyOnly),
                JsonSchema = ConsistencyPrompt.Schema,

                // Creativity here means invented doubt about a real person.
                Temperature = 0.1,
                MaxTokens = 4096,
                UnloadAfterwards = true,
            }, cancellationToken);
        }
        catch (LlmException e)
        {
            clock.Stop();
            repository.RecordRun(callId, ProcessingStage.Consistency, model, startedAt, clock.Elapsed,
                audio: TimeSpan.Zero, succeeded: false);

            return ConsistencyReport.Failed($"Modele ulaşılamadı: {e.Message}");
        }

        clock.Stop();
        repository.RecordRun(callId, ProcessingStage.Consistency, model, startedAt, clock.Elapsed,
            audio: TimeSpan.Zero, response.PromptTokens, response.CompletionTokens);

        if (!response.CompletedNormally)
            return ConsistencyReport.Failed("Model cevabı yarıda kesildi.");

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
            return ConsistencyReport.Failed("Model geçerli bir çözümleme döndürmedi.");

        return Absorb(root, call, segments, priors, model);
    }

    /// <summary>
    /// Turns the model's output into persisted evidence, dropping what cannot be shown.
    /// </summary>
    private ConsistencyReport Absorb(
        JsonNode root, Call call, IReadOnlyList<Segment> segments,
        IReadOnlyList<PriorStatement> priors, string model)
    {
        var dismissed = repository.DismissedFlagKeys(call.Id);

        // Replace-then-insert: this run's truth supersedes the last run's, except what the
        // user explicitly rejected, which nothing may bring back.
        repository.ClearConsistency(call.Id);

        List<Flag> kept = [];
        var rejected = 0;

        foreach (var node in root["bulgular"] is JsonArray items ? items.OfType<JsonObject>() : [])
        {
            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            if (located is null) { rejected++; continue; }

            var kind = Str(node, "tur") switch
            {
                "celiski" => FlagKind.Contradiction,
                "zaman_celiskisi" => FlagKind.TimelineMismatch,
                "kacamak" => FlagKind.EvadedQuestion,
                "belirsizlesme" => FlagKind.VagueShift,
                "baski" => FlagKind.PressureTactic,
                _ => FlagKind.Contradiction,
            };

            if (dismissed.Contains(((int)kind, Text.TurkishText.NormalizeForSearch(located.Text))))
                continue;

            var stated = Str(node, "guven");
            var confidence = stated is "yuksek" or "orta" or "dusuk" ? stated : "dusuk";

            // The verifier's speaker is the authoritative one — it read the audio's own file
            // labels. A model that disagrees about who spoke is a model to trust less.
            if (Str(node, "konusan") is { } spoken
                && (spoken == "BEN") != located.IsMe)
            {
                confidence = "dusuk";
            }

            // The counter side: same-call verbatim quote, or a numbered prior statement whose
            // anchor we already hold. An out-of-range number loses the anchor, not the finding.
            string? counterQuote = null;
            long? counterCallId = null;
            int? counterStartMs = null;

            if (QuoteVerifier.Locate(Str(node, "karsi_alinti"), segments) is { } counter)
            {
                counterQuote = counter.Text;
                counterCallId = call.Id;
                counterStartMs = counter.StartMs;
            }
            else if (Int(node, "onceki_baglam_no") is { } n
                     && priors.FirstOrDefault(p => p.Number == n) is { } prior)
            {
                counterQuote = prior.Quote;
                counterCallId = prior.CallId;
                counterStartMs = prior.QuoteStartMs;
            }

            var summary = Str(node, "aciklama") ?? "";
            var reason = Str(node, "gerekce");
            if (!string.IsNullOrWhiteSpace(reason)) summary = $"{summary} — {reason}";

            var flag = new Flag
            {
                CallId = call.Id,
                ContactId = call.ContactId,
                Kind = kind,
                Summary = summary.Trim(),
                Quote = located.Text,
                QuoteStartMs = located.StartMs,
                CounterQuote = counterQuote,
                CounterCallId = counterCallId,
                CounterQuoteStartMs = counterStartMs,
                LowConfidence = located.LowConfidence,
                Source = Flag.Sources.Consistency,
                Confidence = located.LowConfidence ? "dusuk" : confidence,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            kept.Add(flag with { Id = repository.InsertFlag(flag) });
        }

        // Balancing observations render as text; their quotes are verified where possible but
        // an observation is not an accusation, so an unlocatable one degrades to prose.
        List<string> observations = [];
        foreach (var node in root["tutarli_gozlemler"] is JsonArray obs ? obs.OfType<JsonObject>() : [])
        {
            var text = Str(node, "aciklama");
            if (string.IsNullOrWhiteSpace(text)) continue;

            observations.Add(QuoteVerifier.Locate(Str(node, "alinti"), segments) is { } q
                ? $"{text} — \"{q.Text}\""
                : text!);
        }

        // The warning stands only on surviving evidence. The model wrote it before the quotes
        // were checked; if nothing survived, there is nothing to warn about.
        var warning = Str(root, "genel_uyari");
        warning = string.IsNullOrWhiteSpace(warning) || kept.Count == 0 ? null : warning.Trim();

        if (warning is not null) repository.SaveConsistencyNote(call.Id, warning, model);

        bool insufficient;
        try
        {
            insufficient = root["yetersiz"]?.GetValue<bool>() ?? false;
        }
        catch (Exception)
        {
            insufficient = string.Equals(root["yetersiz"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        return new ConsistencyReport(kept, warning, observations, rejected, insufficient);
    }

    private static string BuildTranscript(IReadOnlyList<Segment> segments)
    {
        var builder = new StringBuilder();

        foreach (var segment in segments)
        {
            var speaker = segment.IsMe ? "BEN" : "KARSI";

            // The transcriber's own doubt, visible to the model: findings built on unclear
            // audio are capped at low confidence by the prompt.
            var marker = segment.LowConfidence ? " (ses net değil)" : "";

            builder.AppendLine(
                $"[{Timestamp(segment.StartMs)}] {speaker}{marker}: {segment.Text.Trim()}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The contact's ledger as numbered context: what they said before, each line anchored to
    /// its call and millisecond here — so the model cites [B3] and we cite the recording.
    /// </summary>
    private IReadOnlyList<PriorStatement> BuildPriorStatements(long contactId, long currentCallId)
    {
        List<PriorStatement> priors = [];
        var number = 1;

        var claims = repository.GetAllClaims(contactId)
            .Where(c => c.CallId != currentCallId)
            .OrderByDescending(c => c.Id)
            .Take(MaxPriorStatements);

        foreach (var claim in claims)
        {
            var when = repository.GetCall(claim.CallId)?.StartedAt.ToLocalTime().ToString("d MMM yyyy") ?? "?";
            var who = claim.ByMe ? "BEN" : "KARSI";

            priors.Add(new PriorStatement(
                number, claim.CallId, claim.QuoteStartMs, claim.Quote,
                $"[B{number}] {when} · {claim.Entity} · {claim.Attribute}: {claim.Value} — {who}: \"{claim.Quote}\""));

            number++;
        }

        foreach (var commitment in repository.GetOpenCommitments(contactId)
                     .Where(c => c.CallId != currentCallId)
                     .Take(Math.Max(0, MaxPriorStatements - priors.Count)))
        {
            var when = repository.GetCall(commitment.CallId)?.StartedAt.ToLocalTime().ToString("d MMM yyyy") ?? "?";
            var who = commitment.ByMe ? "BEN" : "KARSI";

            priors.Add(new PriorStatement(
                number, commitment.CallId, commitment.QuoteStartMs, commitment.Quote,
                $"[B{number}] {when} · söz: {commitment.Obligation} — {who}: \"{commitment.Quote}\""));

            number++;
        }

        return priors;
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

    private static int? Int(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return null;

        try
        {
            return value.GetValue<int>();
        }
        catch (Exception)
        {
            return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }
    }

    private static string Timestamp(int milliseconds)
    {
        var total = milliseconds / 1000;
        return $"{total / 60:00}:{total % 60:00}";
    }
}
