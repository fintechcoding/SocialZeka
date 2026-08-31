using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Analysis;

public sealed record AnalysisOptions
{
    public required string Model { get; init; }
    public int ChunkTokens { get; init; } = 2500;

    /// <summary>Release the GPU when the last request finishes, so Whisper can have it back.</summary>
    public bool UnloadWhenDone { get; init; } = true;

    /// <summary>Ask the model to adjudicate the contradiction candidates the checks produced.</summary>
    public bool AdjudicateContradictions { get; init; } = true;

    public bool WriteSummary { get; init; } = true;
}

public sealed record AnalysisReport(
    int CommitmentsFound,
    int ClaimsFound,
    int QuotesRejected,
    IReadOnlyList<Flag> Flags,
    string? Summary,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Share of extracted items whose quote could not be found in the transcript.
    ///
    /// Surfaced rather than swallowed. A model rejected on most of its output is not producing
    /// usable evidence, and the user should be told to switch models rather than left with a
    /// quietly empty ledger.
    /// </summary>
    public double RejectionRate
    {
        get
        {
            var total = CommitmentsFound + ClaimsFound + QuotesRejected;
            return total == 0 ? 0 : (double)QuotesRejected / total;
        }
    }
}

/// <summary>
/// Turns a transcript into the per-contact ledger.
///
/// The shape of this is the whole argument of the product. A model cannot tell whether somebody
/// is lying — the published evidence on text-only deception detection is close to chance, and at
/// a realistic rate of actual deception most "this person is lying" verdicts would be wrong,
/// about the user's own family and colleagues. So nothing here produces a verdict.
///
/// Instead: the model finds and quotes; every quote is verified to exist in the transcript;
/// ordinary code computes what changed, what was promised, what came due and what went
/// unanswered; and the user is shown those with the exact words and a timestamp to listen to.
/// The machine does the remembering, the person does the judging.
/// </summary>
public sealed class AnalysisPipeline(ILlmClient llm, Repository repository)
{
    public async Task<AnalysisReport> AnalyseAsync(
        long callId,
        AnalysisOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var call = repository.GetCall(callId)
            ?? throw new InvalidOperationException($"Call {callId} not found.");

        if (call.Kind == CallKind.Group)
        {
            // Every remote participant arrives mixed into one stream, so "who said this" stops
            // being a fact. Guessing would put words in the wrong mouth, so nothing is analysed.
            return new AnalysisReport(0, 0, 0, [], null,
                ["Grup araması: konuşmacılar ayrıştırılamadığı için çözümleme yapılmadı."]);
        }

        var segments = repository.GetSegments(callId);
        if (segments.Count == 0)
            return new AnalysisReport(0, 0, 0, [], null, ["Bu görüşmenin metni yok."]);

        List<string> warnings = [];
        List<Commitment> commitments = [];
        List<Claim> claims = [];
        List<(string quote, int startMs, bool evaded)> questions = [];
        List<Flag> flags = [];
        var rejected = 0;

        var chunks = TranscriptChunker.Split(segments, options.ChunkTokens);

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Çözümleniyor {i + 1}/{chunks.Count}");

            var chunk = chunks[i];
            var context = i == 0
                ? ""
                : TranscriptChunker.BuildRollingContext(chunks[i - 1].Segments);

            var extraction = await ExtractAsync(chunk, context, options, cancellationToken);
            if (extraction is null)
            {
                warnings.Add($"{i + 1}. bölüm çözümlenemedi.");
                continue;
            }

            Absorb(extraction, callId, call.ContactId, segments, commitments, claims, questions, ref rejected);
        }

        if (rejected > 0)
        {
            warnings.Add(
                $"{rejected} kayıt, alıntısı metinde bulunamadığı için elendi. " +
                "Bunlar model tarafından uydurulmuş olabilir.");
        }

        progress?.Report("Karşılaştırmalar yapılıyor");

        foreach (var commitment in commitments) repository.InsertCommitment(commitment);
        foreach (var claim in claims) repository.InsertClaim(claim);

        flags.AddRange(ScamPatterns.Scan(callId, call.ContactId, segments));

        if (call.ContactId is { } contactId)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var allClaims = repository.GetAllClaims(contactId);
            var openCommitments = repository.GetOpenCommitments(contactId);

            flags.AddRange(DeterministicChecks.OverdueCommitments(openCommitments, today));
            flags.AddRange(DeterministicChecks.MovedDeadlines(openCommitments));
            flags.AddRange(DeterministicChecks.ChangedAmounts(allClaims));

            if (options.AdjudicateContradictions)
            {
                await foreach (var flag in AdjudicateAsync(allClaims, options, cancellationToken))
                    flags.Add(flag);
            }
        }

        if (DeterministicChecks.EvasionRate(callId, call.ContactId, questions) is { } evasion)
            flags.Add(evasion);

        foreach (var flag in flags) repository.InsertFlag(flag);

        string? summary = null;
        if (options.WriteSummary)
        {
            progress?.Report("Özet yazılıyor");
            summary = await SummariseAsync(commitments, claims, flags, segments, options, cancellationToken);

            if (summary is not null)
            {
                repository.SaveSummary(new CallSummary
                {
                    CallId = callId,
                    Summary = summary,
                    ModelUsed = options.Model,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        if (options.UnloadWhenDone)
            await llm.UnloadAsync(options.Model, cancellationToken);

        return new AnalysisReport(commitments.Count, claims.Count, rejected, flags, summary, warnings);
    }

    private async Task<JsonNode?> ExtractAsync(
        TranscriptChunk chunk, string context, AnalysisOptions options, CancellationToken cancellationToken)
    {
        var response = await llm.CompleteAsync(new LlmRequest
        {
            Model = options.Model,
            SystemPrompt = ExtractionPrompt.SystemPrompt,
            UserPrompt = ExtractionPrompt.BuildUserPrompt(chunk.Segments, context),
            JsonSchema = ExtractionPrompt.Schema,
            Temperature = 0.2,
            MaxTokens = 2048,
        }, cancellationToken);

        // A schema guarantees the shape of what was produced, not that generation finished.
        // Output cut off at the token limit is valid so far and still unparseable.
        if (!response.CompletedNormally) return null;

        try
        {
            return JsonNode.Parse(response.Content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Absorb(
        JsonNode extraction,
        long callId,
        long? contactId,
        IReadOnlyList<Segment> segments,
        List<Commitment> commitments,
        List<Claim> claims,
        List<(string quote, int startMs, bool evaded)> questions,
        ref int rejected)
    {
        foreach (var node in Array(extraction, "taahhutler"))
        {
            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            if (located is null) { rejected++; continue; }

            commitments.Add(new Commitment
            {
                CallId = callId,
                ContactId = contactId,
                ByMe = Str(node, "konusan") == "BEN",
                Quote = located.Text,
                QuoteStartMs = located.StartMs,
                Obligation = Str(node, "yukumluluk") ?? "",
                DeadlineRaw = Str(node, "tarih_ham"),
                DeadlineDate = TurkishDates.TryResolve(Str(node, "tarih_ham")),
                Amount = Num(node, "tutar"),
                Currency = Str(node, "para_birimi") is { } c && c != "BILINMIYOR" ? c : null,
                IsConditional = Bool(node, "kosullu"),
                Status = CommitmentStatus.Open,
            });
        }

        foreach (var node in Array(extraction, "iddialar"))
        {
            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            if (located is null) { rejected++; continue; }

            claims.Add(new Claim
            {
                CallId = callId,
                ContactId = contactId,
                ByMe = Str(node, "konusan") == "BEN",
                Quote = located.Text,
                QuoteStartMs = located.StartMs,
                Entity = Str(node, "varlik") ?? "",
                Attribute = Str(node, "nitelik") ?? "",
                Value = Str(node, "deger") ?? "",
                NumericValue = Num(node, "sayisal_deger"),
                Unit = Str(node, "birim"),
                // Carried from the audio, so uncertain speech never feeds automatic detection.
                LowConfidence = located.LowConfidence,
            });
        }

        foreach (var node in Array(extraction, "sorular"))
        {
            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            if (located is null) { rejected++; continue; }

            var status = Str(node, "cevap_durumu");
            questions.Add((located.Text, located.StartMs, status is "kacamak" or "savusturuldu"));
        }
    }

    private async IAsyncEnumerable<Flag> AdjudicateAsync(
        IReadOnlyList<Claim> claims,
        AnalysisOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Only the candidates the deterministic pass produced reach the model, and each is a
        // bounded two-quote judgement — a task a small model does well, unlike anything
        // resembling "is this person honest".
        foreach (var (earlier, later) in DeterministicChecks.ContradictionCandidates(claims).Take(10))
        {
            cancellationToken.ThrowIfCancellationRequested();

            LlmResponse response;
            try
            {
                response = await llm.CompleteAsync(new LlmRequest
                {
                    Model = options.Model,
                    SystemPrompt = "Sen tarafsız bir çözümleyicisin. Sadece istenen sınıflandırmayı yap.",
                    UserPrompt = ExtractionPrompt.BuildContradictionPrompt(
                        later.Entity, later.Attribute, earlier.Quote, later.Quote),
                    JsonSchema = ExtractionPrompt.ContradictionSchema,
                    Temperature = 0.1,
                    MaxTokens = 256,
                }, cancellationToken);
            }
            catch (LlmException)
            {
                continue;
            }

            if (!response.CompletedNormally) continue;

            JsonNode? verdict;
            try
            {
                verdict = JsonNode.Parse(response.Content);
            }
            catch (JsonException)
            {
                continue;
            }

            if (Str(verdict, "sonuc") != "celiski") continue;

            yield return new Flag
            {
                CallId = later.CallId,
                ContactId = later.ContactId,
                Kind = FlagKind.Contradiction,
                Summary = Str(verdict, "gerekce") ?? $"{later.Entity} / {later.Attribute} hakkında çelişki",
                Quote = later.Quote,
                QuoteStartMs = later.QuoteStartMs,
                CounterQuote = earlier.Quote,
                CounterCallId = earlier.CallId,
                CounterQuoteStartMs = earlier.QuoteStartMs,
                LowConfidence = earlier.LowConfidence || later.LowConfidence,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>
    /// Writes the readable summary from the extracted structure rather than the raw transcript.
    ///
    /// Summarising structure keeps the summary anchored to things that were already verified to
    /// exist, so it cannot introduce a claim the extraction step rejected.
    /// </summary>
    private async Task<string?> SummariseAsync(
        List<Commitment> commitments,
        List<Claim> claims,
        List<Flag> flags,
        IReadOnlyList<Segment> segments,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        // A conversation with nothing extracted still gets a summary, from the transcript.
        //
        // This used to return null here, and that made the product silent about most of its own
        // archive: promises, prices and dates are the exception, and an ordinary call — asking how
        // somebody is, arranging to speak later, talking about nothing in particular — produced no
        // summary at all. The user was left with a recording, a transcript, and no answer to the
        // only question they asked afterwards, which is what the call was about.
        //
        // Summarised from the transcript rather than from structure, because there is no structure
        // to summarise. That means the quote verification the extraction step performs does not
        // apply to it, which is why the prompt is emphatic about inventing nothing — and why this
        // path is used only when the verified one has nothing to say.
        if (commitments.Count == 0 && claims.Count == 0 && flags.Count == 0)
            return await SummariseConversationAsync(segments, options, cancellationToken);

        var facts = new JsonObject
        {
            ["taahhutler"] = new JsonArray([.. commitments.Select(c => (JsonNode)new JsonObject
            {
                ["kim"] = c.ByMe ? "ben" : "karsi",
                ["ne"] = c.Obligation,
                ["tarih"] = c.DeadlineRaw,
                ["tutar"] = c.Amount is { } a ? a.ToString(CultureInfo.InvariantCulture) : null,
            })]),
            ["iddialar"] = new JsonArray([.. claims.Take(20).Select(c => (JsonNode)new JsonObject
            {
                ["konu"] = c.Entity,
                ["nitelik"] = c.Attribute,
                ["deger"] = c.Value,
            })]),
            ["bayraklar"] = new JsonArray([.. flags.Select(f => (JsonNode)f.Summary)]),
        };

        try
        {
            var response = await llm.CompleteAsync(new LlmRequest
            {
                Model = options.Model,
                SystemPrompt = ExtractionPrompt.SummarySystemPrompt,
                UserPrompt = facts.ToJsonString(),
                Temperature = 0.3,
                MaxTokens = 512,
                UnloadAfterwards = options.UnloadWhenDone,
            }, cancellationToken);

            return response.CompletedNormally ? response.Content.Trim() : null;
        }
        catch (LlmException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes "what was this call about" straight from the transcript.
    ///
    /// Used when the extraction step found nothing to verify — which is the ordinary case, not a
    /// failure. Kept separate from the structured summary so the difference is visible in the
    /// code: one is built from quotes that were checked against the transcript, and this one is
    /// the model reading the transcript directly.
    /// </summary>
    private async Task<string?> SummariseConversationAsync(
        IReadOnlyList<Segment> segments,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var transcript = ExtractionPrompt.BuildConversationSummaryPrompt(segments);
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        try
        {
            var response = await llm.CompleteAsync(new LlmRequest
            {
                Model = options.Model,
                SystemPrompt = ExtractionPrompt.ConversationSummarySystemPrompt,
                UserPrompt = transcript,
                Temperature = 0.3,
                MaxTokens = 512,
                UnloadAfterwards = options.UnloadWhenDone,
            }, cancellationToken);

            return response.CompletedNormally ? response.Content.Trim() : null;
        }
        catch (LlmException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonNode> Array(JsonNode? root, string name)
        => root?[name]?.AsArray().Where(n => n is not null).Select(n => n!) ?? [];

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

    private static decimal? Num(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return null;

        try
        {
            return value.GetValue<decimal>();
        }
        catch (Exception)
        {
            return decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
    }

    private static bool Bool(JsonNode? node, string name)
    {
        var value = node?[name];
        if (value is null) return false;

        try
        {
            return value.GetValue<bool>();
        }
        catch (Exception)
        {
            return bool.TryParse(value.ToString(), out var parsed) && parsed;
        }
    }
}
