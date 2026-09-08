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

    /// <summary>
    /// Whether the provider behind <see cref="Model"/> sends text off the machine. Read by the
    /// one step that hands the conversation over whole — the summary — to size its window:
    /// a cloud model takes the call entire, a local one takes what its context holds.
    /// </summary>
    public bool SendsDataOffMachine { get; init; }

    /// <summary>Ask the model to adjudicate the contradiction candidates the checks produced.</summary>
    public bool AdjudicateContradictions { get; init; } = true;

    public bool WriteSummary { get; init; } = true;

    /// <summary>
    /// Keep the extraction's "baski_isaretleri" as tactic evidence on the person's card.
    ///
    /// <b>On now, and the measurement it was waiting for turned out to be already built.</b> This
    /// stood off with the note "until the precision is measured", and the pipeline threw the signs
    /// away on every run for months — found, quote-verified, whitelist-checked, discarded. The
    /// thing it was waiting for is the Kalıplar section's own gate: a label the user dismisses
    /// more than three times in ten stops drawing its bar (ContactCardViewModel's
    /// PatternRow.DismissalCeiling = 0.30). That is the hit rate, measured by the one person who
    /// can hear the recording, on the screen where the rows appear. A separate measuring round
    /// would have asked the same question more slowly.
    ///
    /// Two things make it safe to turn on rather than merely tempting. A sign whose quote cannot
    /// be located in the transcript is dropped in code before it reaches the table, so an STT
    /// ghost cannot brand anybody; and a label outside the whitelist is dropped rather than filed
    /// as "diger". What lands is a quote the user can play.
    ///
    /// It also stopped being optional in another sense: until the extraction prompt was taught
    /// what a pressure sign is (v3.5.4), this shelf came back empty on every call while threats
    /// were being filed as promises. Now that the prompt fills it, throwing it away would be
    /// discarding the one part of a conversation the user asked for by name.
    ///
    /// Questions and the opt-in assessment's tactics are unaffected — those are written either way.
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
    /// True when at least one section of the conversation could not be read.
    ///
    /// Not the same as a failure: such a run keeps everything it did read and everything that was
    /// already stored. It is here because the caller has to tell the user, and because what a
    /// partial run deliberately does NOT do — replace the ledger, rewrite the summary — is
    /// invisible on screen unless somebody says it out loud.
    /// </summary>
    public bool Partial { get; init; }

    /// <summary>
    /// What the user has to be told about the summary: that it was written from the two ends
    /// of a call that did not fit one request, or that it could not be written and why. Null
    /// when it was written from the whole conversation, or was not asked for.
    ///
    /// A sentence rather than a flag because it carries numbers — minutes read, minutes
    /// skipped, the provider's reason — and because the failure it replaces was a silent null:
    /// a long call on a small model produced no summary and nothing on screen said so.
    /// </summary>
    public string? SummaryNotice { get; init; }

    /// <summary>
    /// True when not one section of the conversation could be read, so nothing in this report
    /// describes the call and the caller must not file it as analysed.
    ///
    /// Distinct from <see cref="Partial"/>, which keeps what was read. When every section is
    /// refused the report is empty by construction, and an empty report used to be filed exactly
    /// like a clean one: the call went to Analysed with no ledger, the warning reached no screen,
    /// and an account with no credit produced conversations that looked finished.
    /// </summary>
    public bool NothingRead { get; init; }

    /// <summary>
    /// True when the provider refused at least one section because the account is out of money
    /// or quota. The caller remembers it, so the calls queued behind this one are not sent to be
    /// refused the same way.
    /// </summary>
    public bool QuotaExhausted { get; init; }

    /// <summary>
    /// The provider's own sentence for the first section it refused, when nothing was read
    /// because of a refusal rather than answers that would not parse. Null otherwise.
    /// </summary>
    public string? Refusal { get; init; }

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
            var total = QuotesJudged;
            return total == 0 ? 0 : (double)QuotesRejected / total;
        }
    }

    /// <summary>How many extracted items that share was measured over.</summary>
    public int QuotesJudged => CommitmentsFound + ClaimsFound + QuotesRejected;

    /// <summary>
    /// The fewest quotes a run may say anything about the model on.
    ///
    /// A sixteen-second call — "telefondayım, sonra ararım" — gives the extraction almost nothing
    /// to find, and two invented quotes out of two is a hundred per cent. Told as a share, that
    /// reads as a verdict on the model the user just chose, on a sample of two. The floor is the
    /// same idea as the contact reading's "too little on record": a rate over a handful of items
    /// is not a measurement of anything.
    /// </summary>
    public const int SmallestVerdict = 5;

    /// <summary>
    /// Whether this run is grounds to doubt the model rather than a small sample.
    ///
    /// The threshold has not moved. What is new is the denominator it is allowed to run on, and
    /// the rule lives here rather than in the orchestrator so a test can hold it.
    /// </summary>
    public bool ModelLooksUnsuited => QuotesJudged >= SmallestVerdict && RejectionRate > 0.4;
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
    /// The first refusal the provider made in the current run, kept so the run can stop asking.
    ///
    /// Per run, not per instance: one pipeline may analyse several calls, and a refusal on one of
    /// them says nothing about the account by the time the next is reached.
    /// </summary>
    private LlmException? _refusal;

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

        _refusal = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Çözümleniyor {i + 1}/{chunks.Count}");

            // Once the provider has said the account is empty, every further section would be
            // refused the same way. Asking again costs a round trip per section and produces a
            // row of identical failures where one refusal was the whole answer; the sections
            // are filed as unread without being sent.
            if (_refusal is { QuotaExhausted: true })
            {
                warnings.Add($"{i + 1}. bölüm istenmedi: bakiye bitmiş.");
                failedChunks++;
                continue;
            }

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

            return new AnalysisReport(0, 0, rejected, [], null, warnings)
            {
                NothingRead = true,
                QuotaExhausted = _refusal?.QuotaExhausted == true,
                Refusal = _refusal?.Message,
            };
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
        var surviving = repository.SurvivingCommitments(callId);
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

        // Which of this run's readings are the ones the user already ruled on — decided here,
        // once, over the whole list, rather than one row at a time against a set of keys.
        var ruled = Claimed(surviving, commitments);

        var withheld = 0;

        for (var i = 0; i < commitments.Count; i++)
        {
            var commitment = commitments[i];
            var key = (commitment.ByMe, TurkishText.NormalizeForSearch(commitment.Quote));

            if (ruled.Contains(i) || stored.Commitments.Contains(key))
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

            // A finding whose REASON has gone, swept before the answers are written.
            //
            // These three checks are functions of the person's whole stored ledger, not of this
            // conversation's text: they have just read every open commitment and every claim the
            // contact has, and what they returned is the complete, current answer for all three
            // kinds across all of that person's calls. The delete below cannot express that,
            // because it is scoped to the kinds this run PRODUCED — and a row that stopped being
            // produced is precisely the row that needs removing. "Vadesi geçti" written for the
            // first conversation stops being emitted the moment the user marks the promise kept,
            // so nothing deleted it, and it sat on that conversation reading as current until
            // somebody happened to re-analyse it.
            //
            // Sweeping is honest here and only here: the run has an opinion about every row it
            // removes. It does not extend to the scam patterns or the evasion rate, which are
            // read out of a single transcript this run may never have opened, nor to the
            // contradiction judgements, which are paid model calls that can stop halfway.
            // Dismissed rows are tombstones and are left standing, as everywhere else.
            repository.ClearPersonWideFlags(contactId,
            [
                (int)FlagKind.OverdueCommitment,
                (int)FlagKind.MovedDeadline,
                (int)FlagKind.ChangedAmount,
            ]);

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

        // The summary, and only from a run that read the whole conversation.
        //
        // Everything above this line was taught to add rather than replace when a section failed;
        // the summary was not, and SaveSummary replaces. So a provider error on one section of a
        // twelve-section call rewrote a summary made from the whole conversation with one made
        // from the eleven-twelfths that came back — and the new text says nothing about the part
        // it never read, so nobody could tell from the screen that it had shrunk.
        //
        // The request is not made at all, rather than made and then discarded. A summary this run
        // is not allowed to keep is a paid request whose result goes in the bin, and the rule
        // here is that we do not buy those. The older summary stays exactly as it was; the call
        // window keeps showing it, and the warnings say which sections this run could not read.
        if (partial && options.WriteSummary)
        {
            warnings.Add(
                "Konuşmanın tamamı okunamadığı için özet yenilenmedi; önceki özet olduğu gibi duruyor.");
        }

        string? summary = null;
        string? summaryNotice = null;

        if (options.WriteSummary && !partial)
        {
            progress?.Report("Özet yazılıyor");
            var written = await SummariseAsync(commitments, claims, flags, segments, options, cancellationToken);

            summary = written.Summary;
            summaryNotice = written.Notice;

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

        return new AnalysisReport(commitments.Count, claims.Count, rejected, flags, summary, warnings)
        {
            Partial = partial,
            SummaryNotice = summaryNotice,
            QuotaExhausted = _refusal?.QuotaExhausted == true,
        };
    }

    /// <summary>
    /// Which of this run's promises are already accounted for by a row the user ruled on.
    ///
    /// The question looks like a set lookup and is not one. One sentence can carry two promises —
    /// <see cref="QuoteVerifier"/> returns the whole segment for a quote found inside it, so both
    /// readings arrive with the same words, the same speaker and the same millisecond. Asked as
    /// "is this row's key among the survivors", the tombstone of the reading the user turned down
    /// answered yes for the reading they KEPT as well, ClearAnalysis had already deleted that one
    /// because it carried no ruling of its own, and the promise the user chose disappeared from
    /// the ledger. That is the one failure the mechanism must never produce: it exists to protect
    /// the user's decisions, and it was eating them.
    ///
    /// Narrowing the key to include the obligation is not the fix and was rejected: the model
    /// rewords an obligation freely between runs, so a refusal would come back to life whenever
    /// it did, and resurrecting a refusal is the worse failure of the two. So the matching moves
    /// out of the key and into here, where it can be a pairing rather than a test:
    ///
    ///   * first, the same obligation in the same words — a reading the model produced again
    ///     exactly as before is unmistakably the one that was ruled on;
    ///   * then the sentence alone, for whatever is left over, and to the leftover whose wording
    ///     is nearest to the tombstone's, so a reworded refusal still lands on the refusal.
    ///
    /// Either way ONE ruling accounts for ONE reading. That is the whole of the correction: a
    /// tombstone can no longer swallow a second promise it was never about.
    /// </summary>
    /// <returns>Positions in <paramref name="found"/> that must not be written.</returns>
    private static HashSet<int> Claimed(
        IReadOnlyList<SurvivingCommitment> surviving, IReadOnlyList<Commitment> found)
    {
        var taken = new HashSet<int>();
        if (surviving.Count == 0 || found.Count == 0) return taken;

        var folded = found
            .Select(c => (
                c.ByMe,
                Quote: TurkishText.NormalizeForSearch(c.Quote),
                Obligation: TurkishText.NormalizeForSearch(c.Obligation)))
            .ToList();

        var leftOver = new List<SurvivingCommitment>();

        foreach (var row in surviving)
        {
            var hit = -1;

            for (var i = 0; i < folded.Count && hit < 0; i++)
            {
                if (taken.Contains(i)) continue;
                if (folded[i].ByMe != row.ByMe) continue;
                if (folded[i].Quote != row.FoldedQuote) continue;
                if (folded[i].Obligation != row.FoldedObligation) continue;

                hit = i;
            }

            if (hit >= 0) taken.Add(hit);
            else leftOver.Add(row);
        }

        foreach (var row in leftOver)
        {
            var hit = -1;
            var best = -1;

            for (var i = 0; i < folded.Count; i++)
            {
                if (taken.Contains(i)) continue;
                if (folded[i].ByMe != row.ByMe) continue;
                if (folded[i].Quote != row.FoldedQuote) continue;

                var shared = SharedWords(row.FoldedObligation, folded[i].Obligation);
                if (shared <= best) continue;

                best = shared;
                hit = i;
            }

            if (hit >= 0) taken.Add(hit);
        }

        return taken;
    }

    /// <summary>
    /// How many words two obligations have in common — the tie-break when a sentence holds more
    /// than one promise and the model has reworded one of them. Crude on purpose: it decides
    /// which of two readings a tombstone attaches to, never whether it attaches at all, and when
    /// it cannot tell them apart the tombstone still attaches to one of them and only one.
    ///
    /// Split on anything that is not a letter or a digit, because the folding leaves punctuation
    /// where it is and "Whatsapp'tan" has to count as sharing a word with "Whatsapp".
    /// </summary>
    private static int SharedWords(string a, string b)
    {
        var words = Words(b);
        return Words(a).Count(words.Contains);

        static HashSet<string> Words(string text) =>
        [
            .. new string([.. text.Select(c => char.IsLetterOrDigit(c) ? c : ' ')])
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        ];
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
            // Remembered, so the loop can stop asking when the refusal is about money, and so the
            // report can say why nothing was read rather than only that nothing was.
            _refusal ??= e;

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
    /// What the summary step produced: the text, or the sentence the user reads instead of it.
    /// Never both null — a summary that was not written is a notice, not a silence.
    /// </summary>
    private sealed record SummaryResult(string? Summary, string? Notice);

    /// <summary>The summary's token budget, reserved out of the window before the transcript is sized.</summary>
    public const int SummaryAnswerTokens = 512;

    /// <summary>
    /// Writes the readable summary from the extracted structure rather than the raw transcript.
    ///
    /// Summarising structure keeps the summary anchored to things that were already verified to
    /// exist, so it cannot introduce a claim the extraction step rejected.
    /// </summary>
    private async Task<SummaryResult> SummariseAsync(
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

        return await AskForSummaryAsync(
            ExtractionPrompt.SummarySystemPrompt, facts.ToJsonString(), notice: null, options, cancellationToken);
    }

    /// <summary>
    /// Writes "what was this call about" straight from the transcript.
    ///
    /// Used when the extraction step found nothing to verify — which is the ordinary case, not a
    /// failure. Kept separate from the structured summary so the difference is visible in the
    /// code: one is built from quotes that were checked against the transcript, and this one is
    /// the model reading the transcript directly.
    ///
    /// The one step of the pipeline that hands the conversation over whole, so the one that has
    /// to know how much the model holds. Sized from the model's window when it is known and from
    /// the flat local limit when it is not; a call that does not fit is read from its two ends,
    /// and the report carries the sentence that says so. It used to cut at twelve thousand
    /// characters for every model, cloud included, and say nothing.
    /// </summary>
    private async Task<SummaryResult> SummariseConversationAsync(
        IReadOnlyList<Segment> segments,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var budget = PromptBudget.For(options.Model, options.SendsDataOffMachine);
        var limit = budget.CharacterLimit(
            ExtractionPrompt.ConversationSummarySystemPrompt.Length, SummaryAnswerTokens);

        var window = ExtractionPrompt.BuildConversationSummaryWindow(segments, limit);

        if (window.LinesKept == 0)
        {
            // Either no line held any speech, or the window is too small for even one. Both are
            // a sentence, not a null: the second is the refusal the other whole-call reads give.
            return new SummaryResult(null, window.Windowed
                ? budget.Refusal(window.TotalCharacters, limit)
                : Localisation.T("analysispipeline.ozet-metin-bos"));
        }

        var notice = window.Windowed
            ? string.Format(
                Localisation.T("analysispipeline.ozet-pencereden"),
                window.HeadMinutes, window.TailMinutes, window.SkippedMinutes)
            : null;

        return await AskForSummaryAsync(
            ExtractionPrompt.ConversationSummarySystemPrompt, window.Prompt, notice, options, cancellationToken);
    }

    /// <summary>
    /// One summary request, with every way it can fail turned into a sentence.
    ///
    /// Both summary paths used to swallow the provider's exception into a null, and a cut-off
    /// answer into the same null. On a long call against a small model that was the whole
    /// visible symptom: no summary, no reason. The provider's own sentence is already worded
    /// for a person by <see cref="LlmFailureText"/>; it is carried, not rewritten.
    /// </summary>
    /// <param name="notice">What the caller already knows the user must hear about the summary
    /// if it is written — that it came from a window — kept only when the request succeeds.</param>
    private async Task<SummaryResult> AskForSummaryAsync(
        string systemPrompt, string userPrompt, string? notice,
        AnalysisOptions options, CancellationToken cancellationToken)
    {
        LlmResponse response;

        try
        {
            response = await _llm.CompleteAsync(new LlmRequest
            {
                Model = options.Model,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.3,
                MaxTokens = SummaryAnswerTokens,
                UnloadAfterwards = options.UnloadWhenDone,
            }, cancellationToken);
        }
        catch (LlmException e)
        {
            return new SummaryResult(null,
                string.Format(Localisation.T("analysispipeline.ozet-yazilamadi"), e.Message));
        }

        if (!response.CompletedNormally)
            return new SummaryResult(null, Localisation.T("analysispipeline.ozet-yarida-kesildi"));

        return new SummaryResult(response.Content.Trim(), notice);
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
