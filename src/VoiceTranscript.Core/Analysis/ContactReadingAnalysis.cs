using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Analysis;

/// <summary>
/// One numbered thing the model was shown, with the moment it was said kept here.
/// </summary>
/// <param name="Label">The anchor the model cites it by: "B7" for a ledger row, "A3" for a line.</param>
/// <param name="Line">Exactly the text that went into the packet, anchor and all.</param>
public sealed record ContactReadingExcerpt(
    string Label, string Line, long CallId, int StartMs, bool IsMe, string Quote);

/// <summary>
/// Everything one contact reading is allowed to see.
///
/// Built once and then held, because it is two things at the same time: the prompt, and the list
/// the code checks the answer's anchors against. A model citing [B12] gets a playable row only
/// because the packet that produced the prompt is still here when the answer comes back.
/// </summary>
public sealed record ContactReadingPacket(
    IReadOnlyList<ContactReadingExcerpt> Ledger,
    IReadOnlyList<ContactReadingExcerpt> Excerpts,
    string Figures,
    int CallsCovered,
    long? LatestCallId,
    string InputHash)
{
    public int Count => Ledger.Count + Excerpts.Count;

    public IEnumerable<ContactReadingExcerpt> All => Ledger.Concat(Excerpts);

    /// <summary>Fewer than three conversations, or fewer than twenty anchors: too thin to read.</summary>
    public bool TooThin =>
        CallsCovered < ContactReadingPrompt.MinimumCalls || Count < ContactReadingPrompt.MinimumExcerpts;
}

/// <summary>One anchor that resolved: the words, and the millisecond that plays them.</summary>
public sealed record ContactReadingAnchor(string Label, long CallId, int StartMs, bool IsMe, string Quote);

/// <summary>
/// One line of the panel: prose, and the anchors it survived on.
///
/// An item with an empty anchor list never reaches here — it was dropped and counted.
/// </summary>
public sealed record ContactReadingItem(string Text, IReadOnlyList<ContactReadingAnchor> Anchors);

/// <summary>
/// The model's impression of one person, after code-level enforcement.
/// </summary>
/// <param name="RejectedCount">Items dropped because no anchor of theirs was ever handed over.</param>
public sealed record ContactReadingReport(
    ContactReadingItem GeneralImpression,
    IReadOnlyList<ContactReadingItem> CommunicationStyle,
    IReadOnlyList<ContactReadingItem> Priorities,
    IReadOnlyList<ContactReadingItem> Strengths,
    IReadOnlyList<ContactReadingItem> Weaknesses,
    IReadOnlyList<ContactReadingItem> UnansweredTopics,
    IReadOnlyList<ContactReadingItem> BeforeYouGo,
    IReadOnlyList<ContactReadingItem> NotesForMe,
    string CounterReading,
    int CallsCovered,
    int ExcerptCount,
    int RejectedCount,
    bool Insufficient,
    bool Ok = true,
    string? Problem = null)
{
    public static ContactReadingReport Failed(string problem) =>
        new(new ContactReadingItem("", []), [], [], [], [], [], [], [], "", 0, 0, 0, false, false, problem);

    /// <summary>An empty but honest answer: the archive did not hold enough to read.</summary>
    public static ContactReadingReport TooThin(int calls, int excerpts) =>
        new(new ContactReadingItem("", []), [], [], [], [], [], [], [], "", calls, excerpts, 0, true);

    /// <summary>Every item that survived, so a caller can count what was kept against what was not.</summary>
    public IEnumerable<ContactReadingItem> Items =>
        new[] { GeneralImpression }
            .Concat(CommunicationStyle).Concat(Priorities).Concat(Strengths).Concat(Weaknesses)
            .Concat(UnansweredTopics).Concat(BeforeYouGo).Concat(NotesForMe)
            .Where(i => i.Anchors.Count > 0);

    /// <summary>
    /// The share of the model's items that had to be dropped for want of a real anchor.
    ///
    /// Above 0.4 the panel says the model may not suit the job — the same threshold and the same
    /// sentence the ledger already uses, because it is the same failure: a model inventing the
    /// evidence under its own prose.
    /// </summary>
    public double RejectionRate
    {
        get
        {
            var kept = Items.Count();
            var total = kept + RejectedCount;

            return total == 0 ? 0 : (double)RejectedCount / total;
        }
    }
}

/// <summary>
/// Produces and stores the model's impression of a PERSON — the contact card's opt-in bottom panel.
///
/// The reading of one call is this feature's parent (<see cref="ReadingAnalysis"/>) and the rules
/// are the same, hardened for the larger claim being made. Three of them are code, not prompt:
///
/// 1. WHAT GOES IN. Ledger rows the machine verified (claims, promises, deterministic and
///    consistency flags) and transcript lines, each numbered. <b>deception_note,
///    tactic_evidence and call_summary never enter the packet</b> — the first two because a run
///    must not read its own earlier suspicion back (§7-10), the third because it is the one
///    stored text in the archive that was never quote-verified.
/// 2. WHAT COMES OUT. Every item must cite an anchor the packet actually contained. One that
///    cites nothing, or cites a number nobody handed over, is DROPPED and counted; the general
///    impression is held to the same rule.
/// 3. WHAT IT CANNOT SAY. No score at any level, no psychological or emotional state, and no
///    "arguments you can use" — refused in the instructions, said out loud in the panel, and
///    kept out of this file's vocabulary so it cannot arrive by accident.
///
/// The result is a dead end: <c>contact_reading</c> is joined by nothing and fed to no prompt.
/// </summary>
public sealed class ContactReadingAnalysis(ILlmClient llm, Repository repository)
{
    /// <summary>Ledger anchors: claims, promises and flags, newest first, capped separately.</summary>
    public const int MaxClaims = 20;

    public const int MaxPromises = 20;
    public const int MaxFlags = 20;

    /// <summary>Transcript anchors: the newest lines of the newest conversations.</summary>
    public const int MaxExcerpts = 40;

    /// <summary>The smaller packet, for a context window that cannot hold the full one.</summary>
    public const int SmallLedger = 30;

    public const int SmallExcerpts = 20;

    /// <summary>
    /// Packet size caps, in characters, exactly as the consistency check reasons about them: no
    /// chunking, because half a person's history read as the whole is a different reading, and a
    /// truncated packet would cut a quote in half and then anchor an impression to the fragment.
    /// A local server's window is small, so the smaller packet is tried before giving up — and
    /// when even that does not fit, the honest answer is a refusal with the number in it.
    /// </summary>
    public const int CloudCharacterLimit = 400_000;

    public const int LocalCharacterLimit = 24_000;

    /// <summary>The user pressed [Katılmıyorum]. The only value <c>user_verdict</c> ever holds.</summary>
    public const int Disagree = 1;

    /// <summary>How many people in a row have to disagree before the feature turns itself off.</summary>
    public const int NegativeStreak = 3;

    /// <summary>
    /// The acceptance rule, as a function of the verdicts rather than of the screen.
    ///
    /// Newest first, one per person. Three consecutive people whose reading the user rejected is
    /// the measurement failing, and the plan's answer to that is not an argument — it is the
    /// feature switching itself off and saying so on the settings card.
    /// </summary>
    public static bool MeasurementIsNegative(IReadOnlyList<int?> verdicts) =>
        verdicts.Count >= NegativeStreak && verdicts.Take(NegativeStreak).All(v => v == Disagree);

    /// <summary>
    /// A fingerprint of the conversations a reading was built from.
    ///
    /// Not of the prompt: the point is to answer "has anything happened since", and a new call
    /// with this person changes the answer whether or not it changed a single excerpt. Stored
    /// beside the reading, recomputed when the card opens, and the panel says the reading is old
    /// when the two differ.
    /// </summary>
    public static string InputHash(IEnumerable<long> callIds)
    {
        var joined = string.Join(",", callIds.OrderBy(id => id));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    public async Task<ContactReadingReport> RunAsync(
        long contactId,
        string model,
        string? preferredName = null,
        bool sendsDataOffMachine = true,
        CancellationToken cancellationToken = default)
    {
        var contact = repository.GetContact(contactId);
        if (contact is null) return ContactReadingReport.Failed("Kişi bulunamadı.");

        var packet = BuildPacket(contactId, MaxClaims, MaxPromises, MaxFlags, MaxExcerpts);

        // Refused before a request is paid for rather than after. "Fewer than three conversations"
        // is not a thin reading, it is no reading: the panel says so and nothing is stored.
        if (packet.TooThin)
            return ContactReadingReport.TooThin(packet.CallsCovered, packet.Count);

        var limit = sendsDataOffMachine ? CloudCharacterLimit : LocalCharacterLimit;
        var prompt = ContactReadingPrompt.BuildUserPrompt(packet);

        if (prompt.Length > limit)
        {
            // The smaller packet is a different question honestly asked, not the same one
            // truncated: fewer rows, all of them whole.
            packet = BuildPacket(contactId, SmallLedger / 3, SmallLedger / 3, SmallLedger / 3, SmallExcerpts);
            prompt = ContactReadingPrompt.BuildUserPrompt(packet);

            if (packet.TooThin)
                return ContactReadingReport.TooThin(packet.CallsCovered, packet.Count);
        }

        if (prompt.Length > limit)
        {
            return ContactReadingReport.Failed(
                $"Bu kişinin kayıtları tek istekte okunamayacak kadar uzun ({prompt.Length / 1000} bin karakter). "
                + (sendsDataOffMachine
                    ? "Okuma parçalanarak çalışamaz; küçültülmüş paket de sığmadı."
                    : "Yerel modelin bağlam penceresi yetmez; Ayarlar'dan bulut tabanlı bir model seçilebilir."));
        }

        var startedAt = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        LlmResponse response;
        try
        {
            response = await llm.CompleteAsync(new LlmRequest
            {
                Model = model,
                SystemPrompt = ContactReadingPrompt.BuildSystemPrompt(contact.Name, preferredName),
                UserPrompt = prompt,
                JsonSchema = ContactReadingPrompt.Schema,

                // The reading's temperature: an impression wants a voice, and extraction
                // temperatures read like minutes of a meeting.
                Temperature = 0.3,
                MaxTokens = 3072,
                UnloadAfterwards = true,
            }, cancellationToken);
        }
        catch (LlmException e)
        {
            clock.Stop();

            // Billed against no call: this run belongs to a person, not to a recording.
            repository.RecordRun(callId: null, ProcessingStage.ContactReading, model, startedAt,
                clock.Elapsed, audio: TimeSpan.Zero, succeeded: false);

            return ContactReadingReport.Failed($"Modele ulaşılamadı: {e.Message}");
        }

        clock.Stop();
        repository.RecordRun(callId: null, ProcessingStage.ContactReading, model, startedAt,
            clock.Elapsed, audio: TimeSpan.Zero, response.PromptTokens, response.CompletionTokens);

        if (!response.CompletedNormally)
            return ContactReadingReport.Failed("Model cevabı yarıda kesildi.");

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
            return ContactReadingReport.Failed("Model geçerli bir okuma döndürmedi.");

        var report = Shape(root, packet);

        // Stored as the ENFORCED shape, never the raw reply: what comes back when the card is
        // reopened is exactly what was shown, and a dropped item stays dropped.
        repository.SaveContactReading(
            contactId, Serialize(report), model, packet.CallsCovered, packet.LatestCallId,
            packet.InputHash, packet.Count, report.RejectedCount);

        return report;
    }

    /// <summary>Rebuilds a stored reading for display. Null when the row cannot be read.</summary>
    public static ContactReadingReport? FromStored(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ContactReadingReport>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Serialize(ContactReadingReport report) => JsonSerializer.Serialize(report);

    /// <summary>
    /// The packet, and the whole of what a contact reading is ever shown.
    ///
    /// Group calls are excluded from both halves. A group recording's far channel is every remote
    /// voice mixed into one stream, so nothing in it can be attributed to this person — counting
    /// it would put somebody else's sentences under their name (§7-14).
    ///
    /// What is NOT here is the point of the method: no <c>deception_note</c> level or paragraph,
    /// no <c>tactic_evidence</c> row, and no <c>call_summary</c>. The first two would be a model
    /// reading its own earlier suspicion back; the third is the one stored text nobody verified
    /// against the transcript.
    /// </summary>
    public ContactReadingPacket BuildPacket(
        long contactId, int maxClaims, int maxPromises, int maxFlags, int maxExcerpts)
    {
        var calls = repository.ListCalls(contactId, limit: int.MaxValue);
        var group = calls.Where(c => c.Kind == CallKind.Group).Select(c => c.Id).ToHashSet();
        var counted = calls.Where(c => !group.Contains(c.Id)).ToList();

        var when = calls.ToDictionary(c => c.Id, c => c.StartedAt);

        List<ContactReadingExcerpt> ledger = [];
        var number = 1;

        void Ledger(long callId, int startMs, bool isMe, string quote, string body)
        {
            var date = when.TryGetValue(callId, out var at) ? at.ToLocalTime().ToString("d MMM yyyy") : "?";
            var who = isMe ? "BEN" : "KARSI";

            ledger.Add(new ContactReadingExcerpt(
                $"B{number}", $"[B{number}] {date} · {body} — {who}: \"{quote}\"", callId, startMs, isMe, quote));

            number++;
        }

        foreach (var claim in repository.GetAllClaims(contactId)
                     .Where(c => !group.Contains(c.CallId))
                     .OrderByDescending(c => c.Id)
                     .Take(maxClaims))
        {
            Ledger(claim.CallId, claim.QuoteStartMs, claim.ByMe, claim.Quote,
                $"{claim.Entity} · {claim.Attribute}: {claim.Value}");
        }

        foreach (var promise in repository.PromiseLedger(contactId: contactId, includeClosed: true)
                     .Where(p => !group.Contains(p.Commitment.CallId))
                     .Where(p => !p.Commitment.DismissedByUser)
                     .Take(maxPromises))
        {
            var commitment = promise.Commitment;

            Ledger(commitment.CallId, commitment.QuoteStartMs, commitment.ByMe, commitment.Quote,
                $"söz: {commitment.EffectiveObligation}");
        }

        // The flag table only — deterministic checks and the consistency audit. The tactic table
        // sits beside it on the card and is never read here.
        foreach (var flag in repository.GetFlags(contactId)
                     .Where(f => !group.Contains(f.CallId))
                     .Take(maxFlags))
        {
            Ledger(flag.CallId, flag.QuoteStartMs, isMe: false, flag.Quote, $"işaret: {flag.Summary}");
        }

        List<ContactReadingExcerpt> excerpts = [];
        number = 1;

        // Fetched wide and then narrowed, because the query cannot filter group calls itself and
        // a person's newest conversation may well have been one.
        foreach (var hit in repository
                     .RecentSegments(contactId, limit: Math.Max(maxExcerpts * 4, 80))
                     .Where(h => !group.Contains(h.CallId))
                     .Take(maxExcerpts))
        {
            var date = hit.CallStartedAt.ToLocalTime().ToString("d MMM yyyy");
            var who = hit.IsMe ? "BEN" : "KARSI";

            excerpts.Add(new ContactReadingExcerpt(
                $"A{number}", $"[A{number}] {date} · {who}: {hit.Text.Trim()}",
                hit.CallId, hit.StartMs, hit.IsMe, hit.Text.Trim()));

            number++;
        }

        return new ContactReadingPacket(
            ledger,
            excerpts,
            Figures(counted, ledger.Count, excerpts.Count),
            counted.Count,
            counted.Count == 0 ? null : counted.MaxBy(c => c.StartedAt)?.Id,
            InputHash(counted.Select(c => c.Id)));
    }

    /// <summary>
    /// One line of countable facts. Counts and dates only — nothing here ranks anybody, and the
    /// prompt is told in as many words that this is a summary rather than a reading.
    /// </summary>
    private static string Figures(
        IReadOnlyList<Call> calls, int ledgerCount, int excerptCount)
    {
        if (calls.Count == 0) return "Bu kişiyle kayıtlı görüşme yok.";

        var first = calls.Min(c => c.StartedAt).ToLocalTime().ToString("d MMM yyyy");
        var last = calls.Max(c => c.StartedAt).ToLocalTime().ToString("d MMM yyyy");

        var incoming = calls.Count(c => c.Direction == CallDirection.Incoming);
        var outgoing = calls.Count(c => c.Direction == CallDirection.Outgoing);

        return $"{calls.Count} görüşme · ilk {first} · son {last} · "
             + $"gelen {incoming} / giden {outgoing} / yön bilinmeyen {calls.Count - incoming - outgoing} · "
             + $"{ledgerCount} defter satırı · {excerptCount} görüşme satırı verildi.";
    }

    /// <summary>
    /// Turns the model's answer into what the panel may show, dropping what it cannot.
    ///
    /// One rule, applied identically to every list and to the general impression: the item keeps
    /// the anchors that resolve, and an item with none left is not softened, it is removed and
    /// counted. That count is on the signature line, so a model whose citations are mostly
    /// invented is visible rather than merely quiet.
    /// </summary>
    private static ContactReadingReport Shape(JsonNode root, ContactReadingPacket packet)
    {
        var byLabel = packet.All.ToDictionary(e => e.Label, StringComparer.Ordinal);
        var rejected = 0;

        ContactReadingItem One(JsonNode? node)
        {
            var text = (Str(node, "metin") ?? "").Trim();
            var anchors = Anchors(node, byLabel);

            if (anchors.Count == 0 || text.Length == 0)
            {
                rejected++;
                return new ContactReadingItem("", []);
            }

            return new ContactReadingItem(text, anchors);
        }

        List<ContactReadingItem> List(string name)
        {
            List<ContactReadingItem> kept = [];

            foreach (var node in root[name] is JsonArray items ? items.OfType<JsonObject>() : [])
            {
                var item = One(node);
                if (item.Anchors.Count > 0) kept.Add(item);
            }

            return kept;
        }

        // The general impression is held to the rule too: the broadest sentence on the panel is
        // exactly the one that must not float free of the record.
        var impression = One(root["genel_izlenim"]);

        var style = List("iletisim_tarzi");
        var priorities = List("oncelikler");
        var strengths = List("guclu_yanlar");
        var weaknesses = List("zayif_yanlar");
        var unanswered = List("cevapsiz_kalan_konular");
        var before = List("gorusmeye_giderken");
        var mine = List("ben_icin_notlar");

        bool insufficient;
        try
        {
            insufficient = root["yetersiz"]?.GetValue<bool>() ?? false;
        }
        catch (Exception)
        {
            insufficient = string.Equals(root["yetersiz"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        return new ContactReadingReport(
            impression, style, priorities, strengths, weaknesses, unanswered, before, mine,
            (Str(root, "baska_okuma") ?? "").Trim(),
            packet.CallsCovered,
            packet.Count,
            rejected,
            insufficient);
    }

    /// <summary>
    /// The anchors of one item, in the order the model wrote them, invented numbers removed.
    ///
    /// Tolerant about spelling and strict about existence: "[B7]", "b7" and "B7, A3" all resolve,
    /// and a number the packet never carried resolves to nothing at all. This is the archive
    /// questions' citation check, applied to two numbering spaces instead of one.
    /// </summary>
    private static List<ContactReadingAnchor> Anchors(
        JsonNode? node, IReadOnlyDictionary<string, ContactReadingExcerpt> byLabel)
    {
        List<ContactReadingAnchor> anchors = [];

        if (node?["dayanaklar"] is not JsonArray list) return anchors;

        foreach (var entry in list)
        {
            var raw = entry?.ToString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            foreach (Match match in Regex.Matches(raw.ToUpperInvariant(), "[AB][0-9]+"))
            {
                if (!byLabel.TryGetValue(match.Value, out var excerpt)) continue;
                if (anchors.Any(a => a.Label == excerpt.Label)) continue;

                anchors.Add(new ContactReadingAnchor(
                    excerpt.Label, excerpt.CallId, excerpt.StartMs, excerpt.IsMe, excerpt.Quote));
            }
        }

        return anchors;
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
