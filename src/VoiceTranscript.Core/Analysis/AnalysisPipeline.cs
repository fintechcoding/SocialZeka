using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

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

    /// <summary>
    /// Keep the extraction's "baski_isaretleri" as tactic evidence on the person's card.
    ///
    /// OFF until the precision is measured. The field has been in the schema all along and the
    /// pipeline has always thrown it away, so nobody knows how many of these signs survive quote
    /// verification — let alone how many a person listening would call correct. Turning it on
    /// before that would fill a card with a kind of row nobody has ever checked, and the whole
    /// argument of the card is that every row on it can be checked.
    ///
    /// What it waits for: run it over a handful of conversations, listen to what comes out, and
    /// keep it only if the hit rate holds up. Questions and the opt-in assessment's tactics are
    /// unaffected — those are written either way.
    /// </summary>
    public bool WritePressureSigns { get; init; }
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
    /// <summary>
    /// Counts what analysing one call actually spent.
    ///
    /// A wrapper rather than a counter at each call site because there are four of them —
    /// extraction, its retry, the summary and the conversation fallback — and a token total that
    /// silently omits one of them is worse than no total at all: it would read as accurate.
    /// </summary>
    private sealed class Metered(ILlmClient inner) : ILlmClient
    {
        private long _prompt;
        private long _completion;

        public LlmProviderKind Kind => inner.Kind;

        public (long Prompt, long Completion) Reading =>
            (Interlocked.Read(ref _prompt), Interlocked.Read(ref _completion));

        public async Task<LlmResponse> CompleteAsync(
            LlmRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.CompleteAsync(request, cancellationToken);

            // Not every provider reports usage, and a missing count is left out rather than
            // guessed from the text length — an invented number here would be spent money the
            // user could not reconcile against their bill.
            if (response.PromptTokens is { } prompt) Interlocked.Add(ref _prompt, prompt);
            if (response.CompletionTokens is { } completion) Interlocked.Add(ref _completion, completion);

            return response;
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            inner.IsAvailableAsync(cancellationToken);

        public Task UnloadAsync(string model, CancellationToken cancellationToken = default) =>
            inner.UnloadAsync(model, cancellationToken);
    }

    private readonly Metered _llm = new(llm);

    /// <summary>
    /// What this pipeline has spent so far, prompt and completion.
    ///
    /// Public because a run that throws never reaches its own bookkeeping: the caller records
    /// that failure, and it could only ever report zeros. Everything burned before the throw
    /// then read as free, and against a provider that fails intermittently the usage screen's
    /// total drifted steadily below the invoice — which is the one thing that screen must not do.
    /// </summary>
    public (long Prompt, long Completion) TokensSpent => _llm.Reading;

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

        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var before = _llm.Reading;

        List<string> warnings = [];
        List<Commitment> commitments = [];
        List<Claim> claims = [];
        List<(string quote, int startMs, bool evaded)> questions = [];
        List<SpeechAct> speechActs = [];
        List<TacticEvidence> pressureSigns = [];
        List<Flag> flags = [];
        var rejected = 0;

        // Relative dates in the extraction ("cuma", "yarın") are resolved against the day of the
        // call, never against today — re-analysing a three-week-old call must not move its
        // deadlines into the current week.
        var spokenOn = DateOnly.FromDateTime(call.StartedAt.LocalDateTime);

        var chunks = TranscriptChunker.Split(segments, options.ChunkTokens);
        var failedChunks = 0;

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
                failedChunks++;
                continue;
            }

            Absorb(
                extraction, callId, call.ContactId, spokenOn, segments,
                commitments, claims, questions, speechActs, pressureSigns, ref rejected);
        }

        if (rejected > 0)
        {
            warnings.Add(
                $"{rejected} kayıt, alıntısı metinde bulunamadığı için elendi. " +
                "Bunlar model tarafından uydurulmuş olabilir.");
        }

        progress?.Report("Karşılaştırmalar yapılıyor");

        // What this run cost, filed however it ends.
        //
        // A local function rather than one line at the bottom, because there are now two ways
        // out of here and only one of them used to write anything down. Differenced rather than
        // read absolutely: one pipeline instance can analyse several calls, and attributing the
        // running total to whichever call happened to be last would make the per-call figures
        // nonsense.
        void RecordSpend(bool succeeded)
        {
            clock.Stop();

            var after = _llm.Reading;

            repository.RecordRun(
                callId,
                ProcessingStage.Analyse,
                options.Model,
                startedAt,
                clock.Elapsed,
                audio: TimeSpan.Zero,
                promptTokens: (int)(after.Prompt - before.Prompt),
                completionTokens: (int)(after.Completion - before.Completion),
                succeeded: succeeded);
        }

        // Whatever a previous analysis of this call left behind goes first.
        //
        // Without this, analysing a call twice appended a second full copy of the person's
        // commitments and claims — and reprocessing is not a rare path: it is offered on two
        // screens, it is the whole point of the "retry everything" button, and a timeout used to
        // requeue a call silently on every startup. So the ordinary way to use the product was
        // also the way to corrupt its ledger, and the corruption compounds: the deterministic
        // checks then report contradictions between a statement and its own duplicate.
        // Nothing parsed at all is a failed run, not an empty result.
        //
        // Clearing first is right when the run produced something: the replacement is better than
        // what was there. But when every chunk failed — the model was unreachable, the endpoint
        // returned prose, the key expired — the clear ran anyway and the ledger for that call was
        // replaced with nothing. A conversation's promises and figures vanished because a server
        // was down, and the screen showed an empty ledger rather than an error.
        if (chunks.Count > 0 && failedChunks == chunks.Count)
        {
            warnings.Add(
                "Hiçbir bölüm çözümlenemedi; önceki defter olduğu gibi korundu. " +
                "Model ya da servis erişilebilir olduğunda yeniden deneyebilirsin.");

            // Nothing usable came back, but the requests were made and the money is spent.
            //
            // This path used to return here without writing anything down, so a twelve-section
            // conversation that had just burned twelve paid requests left the usage screen
            // reading "0 çalışma, 0 jeton, 0 başarısız" — a clean history, for exactly the case
            // the screen exists to describe. A model that refuses the schema, or thinks and
            // returns nothing, is when the user most needs to be told what it cost.
            //
            // The unload goes with it. It was skipped along with the bookkeeping, so a local
            // backend kept the GPU that Whisper needs back after a run that produced nothing.
            if (options.UnloadWhenDone)
                await _llm.UnloadAsync(options.Model, cancellationToken);

            RecordSpend(succeeded: false);

            return new AnalysisReport(0, 0, rejected, [], null, warnings);
        }

        // Some sections were read and some were not — a partial reading of the conversation.
        //
        // A provider error partway through used to escape the loop entirely and throw away the
        // sections already paid for; now it counts as a section that would not parse, and what
        // the others produced is kept. But a partial reading must not be allowed to replace a
        // complete one: the promises and figures of the sections this run never saw are only in
        // the database, and clearing on the strength of a run that did not read them would
        // delete them. So a partial run adds instead of replacing, and compares what it found
        // against what is stored so nothing is written twice.
        var partial = failedChunks > 0;

        // One conversation, one entry per thing said.
        //
        // The model repeats itself, especially when the schema was refused and it is answering
        // in prose: a real call produced the same promise four times, identical quote, identical
        // text, four rows in the ledger and four lines on screen. Nothing downstream could tell
        // them apart, and the deterministic checks then compared a statement against its own
        // copy. Deduplicated here rather than at the screen, because a ledger that holds a thing
        // twice is wrong even when nobody is looking at it.
        var duplicates = commitments.Count + claims.Count;

        commitments = [.. commitments
            .GroupBy(c => (c.ByMe, Text: c.Obligation.Trim(), c.Quote), TupleComparer)
            .Select(g => g.First())];

        claims = [.. claims
            .GroupBy(c => (c.Entity, c.Attribute, c.Value, c.Quote), ClaimComparer)
            .Select(g => g.First())];

        duplicates -= commitments.Count + claims.Count;

        if (duplicates > 0)
            CoreLog.Write("cozumleme", $"gorusme #{callId}: {duplicates} tekrar eden defter satiri elendi");

        // What the user ruled on, dismissed or edited survives ClearAnalysis — and the same
        // words must not then be written a second time as a fresh, unruled row. The K4 rule, the
        // way the consistency check already applies it to flags; before this every re-run put a
        // kept promise back on the open list and a dismissed one back undismissed.
        var surviving = repository.SurvivingCommitmentKeys(callId);
        var dismissedFlags = repository.DismissedFlagKeys(callId);

        // The clear belongs to a run that read the whole conversation, and only to that run.
        //
        // Clearing is right when the replacement is better than what was there. After a provider
        // error partway through it is not: the sections that failed were never read, so their
        // rows are not in the lists below and the clear would delete them for good. A partial run
        // therefore keeps everything and treats what is already stored as its own de-duplication
        // key — the ledger grows by what this run managed to read and by nothing else.
        var stored = partial ? repository.LedgerKeysOf(callId) : StoredLedgerKeys.None;

        if (!partial) repository.ClearAnalysis(callId);

        var withheld = 0;

        foreach (var commitment in commitments)
        {
            var key = (commitment.ByMe, TurkishText.NormalizeForSearch(commitment.Quote));

            if (surviving.Contains(key) || stored.Commitments.Contains(key))
            {
                withheld++;
                continue;
            }

            repository.InsertCommitment(commitment);
        }

        if (withheld > 0)
            CoreLog.Write("cozumleme", $"gorusme #{callId}: {withheld} soz kullanicinin kararini tasiyor, yeniden yazilmadi");

        foreach (var claim in claims)
        {
            var key = (
                TurkishText.NormalizeForSearch(claim.Entity),
                TurkishText.NormalizeForSearch(claim.Attribute),
                TurkishText.NormalizeForSearch(claim.Value),
                TurkishText.NormalizeForSearch(claim.Quote));

            if (stored.Claims.Contains(key)) continue;

            repository.InsertClaim(claim);
        }

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

        // A finding belongs to the conversation it was said in, not to the one being analysed.
        //
        // The deterministic checks read the whole person: an overdue promise is filed against the
        // call where it was made, a moved deadline and a changed figure against the call where
        // the newer words were said. So analysing the second conversation with somebody emits
        // rows that belong to the first — and both the dismissal check and the delete were scoped
        // to the call being analysed. The user's ruling on the first conversation was undone, and
        // because the delete never reached that call a second copy of the same row was added
        // every time, compounding silently. K4: a re-run never touches a row the user ruled on.
        //
        // So each conversation's findings are settled against that conversation — its own
        // dismissals, and its own delete. The delete is narrowed to the kinds this run is
        // actually replacing, so a finding read from the other call's own transcript (a scam
        // pattern, an evasion rate) is not removed by a run that never looked at it.
        foreach (var group in flags.GroupBy(f => f.CallId))
        {
            var dismissed = group.Key == callId
                ? dismissedFlags
                : repository.DismissedFlagKeys(group.Key);

            // ClearAnalysis has already emptied this call's own pipeline findings — but only
            // when it ran, and a partial run does not let it run.
            if (group.Key != callId || partial)
                repository.ClearPipelineFlags(group.Key, [.. group.Select(f => (int)f.Kind).Distinct()]);

            foreach (var flag in group)
            {
                // A finding the user dismissed is a tombstone the delete leaves in place; the
                // same words found again must not come back beside it as a new, undismissed row.
                if (dismissed.Contains(((int)flag.Kind, TurkishText.NormalizeForSearch(flag.Quote)))) continue;

                repository.InsertFlag(flag);
            }
        }

        // The questions, kept past the end of this run. Written AFTER ClearAnalysis, which
        // emptied the table for this call — written before it, they would be deleted by the run
        // that produced them.
        //
        // A partial run merges instead: the questions of the sections it could not read are
        // already stored and are not in its list, and replacing would shrink the denominator the
        // contact card divides by ("7 görüşmede ölçüldü") because a server returned 429.
        repository.ReplaceSpeechActs(
            callId, partial ? Merge(repository.SpeechActsOf(callId), speechActs) : speechActs);

        // And the pressure signs, only where the user has turned the gate on. Left off, nothing
        // is written and ClearAnalysis has already removed whatever an earlier run with the gate
        // on left behind — except the rows the user dismissed, which are tombstones.
        if (options.WritePressureSigns)
        {
            var signs = partial
                ? Merge(repository.TacticEvidenceOf(callId), pressureSigns)
                : pressureSigns;

            repository.ReplaceTacticEvidence(callId, TacticEvidence.Sources.Pipeline, signs);
        }

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
            await _llm.UnloadAsync(options.Model, cancellationToken);

        // A partial run still counts as a run that produced something: it built a ledger and the
        // warnings say which sections are missing from it. "Başarısız" is kept for the run that
        // produced nothing at all, so the failure counter on the usage screen keeps one meaning.
        RecordSpend(succeeded: true);

        return new AnalysisReport(commitments.Count, claims.Count, rejected, flags, summary, warnings);
    }

    /// <summary>
    /// What is stored plus what this run found, minus the overlap.
    ///
    /// Folded on (whose, kind, quote) with the same normalisation the ledger de-duplicates with,
    /// so a question a partial run re-read is recognised as the one already on file rather than
    /// written beside it.
    /// </summary>
    private static List<SpeechAct> Merge(IReadOnlyList<SpeechAct> stored, List<SpeechAct> found)
    {
        var seen = stored
            .Select(a => (a.ByMe, a.Kind, Quote: TurkishText.NormalizeForSearch(a.Quote)))
            .ToHashSet();

        var merged = new List<SpeechAct>(stored);

        foreach (var act in found)
        {
            if (seen.Add((act.ByMe, act.Kind, TurkishText.NormalizeForSearch(act.Quote))))
                merged.Add(act);
        }

        return merged;
    }

    /// <summary>
    /// The same merge for the pressure signs. Only this machinery's own rows are carried over —
    /// the opt-in assessment's were paid for by a different button and ReplaceTacticEvidence
    /// leaves them alone, so folding them in here would file them twice under the wrong source.
    /// </summary>
    private static List<TacticEvidence> Merge(
        IReadOnlyList<TacticEvidence> stored, List<TacticEvidence> found)
    {
        var mine = stored.Where(t => t.Source == TacticEvidence.Sources.Pipeline).ToList();

        var seen = mine
            .Select(t => (t.Tactic, Quote: TurkishText.NormalizeForSearch(t.Quote)))
            .ToHashSet();

        var merged = new List<TacticEvidence>(mine);

        foreach (var sign in found)
        {
            if (seen.Add((sign.Tactic, TurkishText.NormalizeForSearch(sign.Quote))))
                merged.Add(sign);
        }

        return merged;
    }

    private async Task<JsonNode?> ExtractAsync(
        TranscriptChunk chunk, string context, AnalysisOptions options, CancellationToken cancellationToken)
    {
        LlmResponse response;

        try
        {
            response = await _llm.CompleteAsync(new LlmRequest
            {
                Model = options.Model,
                SystemPrompt = ExtractionPrompt.SystemPrompt,
                UserPrompt = ExtractionPrompt.BuildUserPrompt(chunk.Segments, context),
                JsonSchema = ExtractionPrompt.Schema,
                Temperature = 0.2,
                MaxTokens = 2048,
            }, cancellationToken);
        }
        catch (LlmException e)
        {
            // A provider error costs this section, not the sections already paid for.
            //
            // Uncaught, a 429 or an insufficient_quota on section five escaped the loop and took
            // sections one to four with it — read, parsed, verified, and never written. On the
            // retry after a top-up the user pays for those four a second time. This is not
            // hypothetical: the account behind this build hit insufficient_quota mid-run.
            //
            // Treated as a section that would not parse, which is what it is from here: the loop
            // counts it failed, the others keep what they produced, and the tokens spent are
            // recorded either way. Cancellation is not an LlmException, so stopping still stops.
            CoreLog.Write("çözümleme", $"bölüm istenemedi ({e.Message}) — bölüm atlanıyor");
            return null;
        }

        // A schema guarantees the shape of what was produced, not that generation finished.
        // Output cut off at the token limit is valid so far and still unparseable.
        if (!response.CompletedNormally)
        {
            CoreLog.Write("çözümleme",
                $"yanıt normal bitmedi (bitiş={response.FinishReason ?? "?"}, "
                + $"{response.Content.Length} karakter) — bölüm atlanıyor");
            return null;
        }

        try
        {
            var root = JsonNode.Parse(response.Content);
            var coerced = CoerceToObject(root);

            // What actually arrived, structurally. Key names come from the schema, not from the
            // conversation, so they are safe for the shareable log — and they are exactly what
            // is needed to see why an extraction produced nothing.
            if (coerced is null || !ReferenceEquals(coerced, root))
            {
                CoreLog.Write("çözümleme",
                    $"yanıt kökü {Describe(root)} — "
                    + (coerced is null
                        ? "içinden nesne çıkarılamadı, bölüm atlanıyor"
                        : $"içinden nesne çıkarıldı ({Describe(coerced)})"));
            }

            return coerced;
        }
        catch (JsonException)
        {
            var head = response.Content.TrimStart();
            CoreLog.Write("çözümleme",
                $"yanıt JSON değil ({response.Content.Length} karakter, "
                + $"ilk karakter '{(head.Length > 0 ? head[0] : ' ')}') — bölüm atlanıyor");
            return null;
        }
    }

    /// <summary>One JSON node, described without quoting it: its kind, and for objects its keys.</summary>
    private static string Describe(JsonNode? node) => node switch
    {
        JsonObject obj => $"nesne[{string.Join(",", obj.Select(p => Clip(p.Key)).Take(8))}]",
        JsonArray arr => $"dizi({arr.Count} öğe)",
        JsonValue value when value.TryGetValue<string>(out var s) => $"metin({s.Length} karakter)",
        JsonValue => "sayı/boole",
        null => "boş",
        _ => node.GetType().Name,
    };

    private static string Clip(string key) => key.Length <= 24 ? key : key[..24] + "…";

    /// <summary>
    /// Digs the extraction object out of whatever valid JSON the model wrapped it in.
    ///
    /// A schema promises the shape only on the happy path. On the fallback path — and on
    /// providers that half-honour response_format — models return the same data double-encoded
    /// as a JSON string, or boxed in a one-element array. Both parse cleanly, and both used to
    /// crash the pipeline one call later with "The node must be of type 'JsonObject'", which
    /// told the user nothing. Unwrap what can be unwrapped; anything else becomes the ordinary
    /// "bölüm çözümlenemedi" warning instead of an exception.
    /// </summary>
    internal static JsonNode? CoerceToObject(JsonNode? node)
    {
        for (var depth = 0; depth < 3; depth++)
        {
            switch (node)
            {
                case JsonObject:
                    return node;

                case JsonArray items:
                    node = items.FirstOrDefault(n => n is JsonObject);
                    break;

                case JsonValue value when value.TryGetValue<string>(out var text):
                    try
                    {
                        node = JsonNode.Parse(text);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }

                    break;

                default:
                    return null;
            }
        }

        return node as JsonObject;
    }

    private static void Absorb(
        JsonNode extraction,
        long callId,
        long? contactId,
        DateOnly spokenOn,
        IReadOnlyList<Segment> segments,
        List<Commitment> commitments,
        List<Claim> claims,
        List<(string quote, int startMs, bool evaded)> questions,
        List<SpeechAct> speechActs,
        List<TacticEvidence> pressureSigns,
        ref int rejected)
    {
        foreach (var node in Array(extraction, "taahhutler"))
        {
            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            if (located is null) { rejected++; continue; }

            // A promise nobody can state is not a promise.
            //
            // The schema requires "yukumluluk", but the schema is not always applied: a model that
            // refuses response_format sends the pipeline down an unconstrained path, and there the
            // field can simply be absent. It was, on every call — seventy-nine commitments reached
            // the ledger holding a quote and an empty obligation, which reads on screen as a
            // bullet with a person's name and nothing after it.
            //
            // Counted as rejected rather than dropped silently, because "1 alıntı reddedildi" in
            // the log is how anybody would find out this is happening again.
            var obligation = Str(node, "yukumluluk")?.Trim();
            if (string.IsNullOrEmpty(obligation)) { rejected++; continue; }

            commitments.Add(new Commitment
            {
                CallId = callId,
                ContactId = contactId,
                // Read off the audio, not off the model.
                //
                // located.IsMe comes from which of the two recorded streams the quote was found
                // in, and that is the one thing this product knows for certain — it is why the
                // microphone and the speaker are captured separately at all. Taking the speaker
                // from the model's "konusan" field threw that certainty away and replaced it with
                // a guess, so a promise could be recorded against whichever party the model
                // happened to name. The whole ledger rests on who said what.
                ByMe = located.IsMe,
                Quote = located.Text,
                QuoteStartMs = located.StartMs,
                Obligation = obligation,
                DeadlineRaw = Str(node, "tarih_ham"),
                DeadlineDate = TurkishDates.TryResolve(Str(node, "tarih_ham"), spokenOn),
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
                // Read off the audio, not off the model.
                //
                // located.IsMe comes from which of the two recorded streams the quote was found
                // in, and that is the one thing this product knows for certain — it is why the
                // microphone and the speaker are captured separately at all. Taking the speaker
                // from the model's "konusan" field threw that certainty away and replaced it with
                // a guess, so a promise could be recorded against whichever party the model
                // happened to name. The whole ledger rests on who said what.
                ByMe = located.IsMe,
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

            // The same question, kept.
            //
            // The list above lives for the length of this run and produces one ratio for this
            // call; the row below is what lets the person's card say "measured in 7 of 31
            // conversations" instead of quietly computing a rate over whichever calls happen to
            // have been analysed since this was written.
            speechActs.Add(new SpeechAct
            {
                CallId = callId,
                ContactId = contactId,
                // Read off the recorded stream, never off the model's "soran" field: whose
                // question it was decides whose answering is being counted.
                ByMe = located.IsMe,
                Kind = SpeechAct.Kinds.Question,
                AnswerStatus = SpeechAct.Statuses.Recognise(status),
                Quote = located.Text,
                QuoteStartMs = located.StartMs,
                LowConfidence = located.LowConfidence,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        // The pressure signs the extraction has always been asked for and never kept.
        //
        // Collected on every run and written only behind AnalysisOptions.WritePressureSigns, so
        // the precision can be measured on real conversations before anything appears on a card.
        // A sign whose label this build does not know is dropped here rather than filed under a
        // catch-all, the same rule the assessment's tactics follow.
        foreach (var node in Array(extraction, "baski_isaretleri"))
        {
            var located = QuoteVerifier.Locate(Str(node, "alinti"), segments);
            if (located is null) { rejected++; continue; }

            if (TacticEvidence.Recognise(Str(node, "tur")) is not { } tactic) continue;

            pressureSigns.Add(new TacticEvidence
            {
                CallId = callId,
                ContactId = contactId,
                Source = TacticEvidence.Sources.Pipeline,
                Tactic = tactic,
                ByMe = located.IsMe,
                Quote = located.Text,
                QuoteStartMs = located.StartMs,
                LowConfidence = located.LowConfidence,
                CreatedAt = DateTimeOffset.UtcNow,
            });
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
                response = await _llm.CompleteAsync(new LlmRequest
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

            // Coerced like every other reply in this file.
            //
            // Models return the object double-encoded, or wrapped in an array, or fenced in a
            // code block — which is why CoerceToObject exists. This one site parsed raw, so a
            // reply the rest of the pipeline handles routinely threw here instead, and the
            // exception escaped the JsonException catch and abandoned the whole adjudication
            // pass: one awkwardly-shaped answer cost every remaining contradiction check.
            JsonNode? verdict;
            try
            {
                verdict = CoerceToObject(JsonNode.Parse(response.Content));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                continue;
            }

            if (verdict is null) continue;

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
            var response = await _llm.CompleteAsync(new LlmRequest
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
            var response = await _llm.CompleteAsync(new LlmRequest
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

    /// <summary>
    /// Case-insensitive in Turkish, because "Tamam ayarlarım" and "tamam ayarlarım" are the same
    /// sentence and the model does not reliably pick one of them.
    /// </summary>
    private static readonly IEqualityComparer<(bool ByMe, string Text, string Quote)> TupleComparer =
        new CommitmentKeyComparer();

    private static readonly IEqualityComparer<(string Entity, string Attribute, string Value, string Quote)>
        ClaimComparer = new ClaimKeyComparer();

    private sealed class CommitmentKeyComparer : IEqualityComparer<(bool ByMe, string Text, string Quote)>
    {
        public bool Equals((bool ByMe, string Text, string Quote) a, (bool ByMe, string Text, string Quote) b) =>
            a.ByMe == b.ByMe
            && TurkishText.NormalizeForSearch(a.Text) == TurkishText.NormalizeForSearch(b.Text)
            && TurkishText.NormalizeForSearch(a.Quote) == TurkishText.NormalizeForSearch(b.Quote);

        public int GetHashCode((bool ByMe, string Text, string Quote) key) =>
            HashCode.Combine(key.ByMe, TurkishText.NormalizeForSearch(key.Text));
    }

    private sealed class ClaimKeyComparer
        : IEqualityComparer<(string Entity, string Attribute, string Value, string Quote)>
    {
        public bool Equals(
            (string Entity, string Attribute, string Value, string Quote) a,
            (string Entity, string Attribute, string Value, string Quote) b) =>
            TurkishText.NormalizeForSearch(a.Entity) == TurkishText.NormalizeForSearch(b.Entity)
            && TurkishText.NormalizeForSearch(a.Attribute) == TurkishText.NormalizeForSearch(b.Attribute)
            && TurkishText.NormalizeForSearch(a.Value) == TurkishText.NormalizeForSearch(b.Value)
            && TurkishText.NormalizeForSearch(a.Quote) == TurkishText.NormalizeForSearch(b.Quote);

        public int GetHashCode((string Entity, string Attribute, string Value, string Quote) key) =>
            HashCode.Combine(
                TurkishText.NormalizeForSearch(key.Entity),
                TurkishText.NormalizeForSearch(key.Attribute));
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
