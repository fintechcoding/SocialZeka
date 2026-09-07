using Dapper;
using Microsoft.Data.Sqlite;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Storage;

/// <summary>
/// Carrying the user's decisions across, for conversations that exist on BOTH machines.
///
/// <see cref="Repository.MergeArchive"/> leaves a call that is already here completely alone, and
/// for the transcript that is right: two recordings of one conversation interleaved is not a
/// better archive. It was wrong for everything the user WROTE on top of that conversation. The
/// copy reaches only the children of NEW calls, so on the archive somebody actually carries
/// between two computers — where most conversations exist on both sides — the promise rulings,
/// the notes, the tags and the ear verdicts made over there never arrived at all. Not refused,
/// not reported: absent.
///
/// This class is the other half of the merge, and it works one decision at a time under the rule
/// §7.2 of the two-machine plan writes down:
///
///     WRITING INTO AN EMPTY PLACE IS A MOVE, NOT A MERGE.
///
/// Nothing here overwrites anything. A ruling arriving into a place this machine left blank is
/// simply applied — nothing is displaced, so there is nothing to ask about, and that silences the
/// large majority. Where both machines ruled and the rulings differ, the LOCAL one stands and the
/// incoming one becomes a row in <c>import_leftover</c>. Quietly picking a winner between two
/// things a person decided is precisely the loss this operation exists to prevent, and so is
/// quietly discarding the loser.
///
/// THE ARITHMETIC IS THE POINT. Every incoming decision is counted, and every one of them ends in
/// exactly one of three places — carried, already the same, or left in the list. §7.3 makes that
/// sum a hard invariant, and <see cref="Repository.MergeArchive"/> refuses to commit a merge that
/// cannot make it. There is no fourth destination and in particular there is no "dropped".
///
/// WHAT CANNOT CROSS, named here rather than discovered: a ruling is matched to its local twin
/// through the words it hangs on, folded. If the conversation was transcribed again on one of the
/// two machines the quote changed, the match misses, and the ruling has nowhere to land. §7.4
/// calls this the weakest joint in the design and it is not fixed here — but such a ruling is NOT
/// lost either. It becomes a leftover with nothing on the local side, which is the difference
/// between a known gap and a silent one.
/// </summary>
internal sealed class DecisionMerge(
    SqliteConnection connection,
    SqliteTransaction transaction,
    string? sourceArchiveId,
    DateTimeOffset now)
{
    private int _seen;
    private int _carried;
    private int _same;

    /// <summary>Held rather than inserted as they are found, so one pass writes them all.</summary>
    private readonly List<Pending> _left = [];

    private sealed record Pending(
        string Fingerprint, string Kind, long? CallId, long? ContactId,
        string Field, string? Mine, string? Theirs, string? Quote);

    public DecisionCounts Counts => new(_seen, _carried, _same, _left.Count);

    /// <summary>Shared calls: the ones this machine and the archive both have.</summary>
    private const string OfSharedCalls = "k.old NOT IN (SELECT old FROM new_call)";

    /// <summary>The same, as a set of local call ids, for a query that has no map_call join.</summary>
    private const string SharedCallIds =
        "SELECT new FROM map_call k WHERE k.old NOT IN (SELECT old FROM new_call)";

    // ---- person cards -------------------------------------------------------
    //
    // These are NOTICED here and APPLIED by Repository.MergeFields, which implements the same
    // blank-here rule column by column. Two pieces of code agreeing on one rule is a real risk,
    // and the alternative was worse: MergeFields is a single UPSERT over a column list read at
    // run time, and taking it apart to make it count would cost the property that makes it right
    // — that a column added by the next schema step travels without anybody remembering.
    //
    // Must run BEFORE MergeFields, because afterwards a place this machine left blank is no
    // longer blank and the classification would call every move an "already the same".

    /// <summary>
    /// What the other machine knows about people this one also knows.
    ///
    /// Only people on both sides. A person the archive introduces arrives whole, with no local
    /// value to displace, so there is nothing to count and nothing to ask.
    /// </summary>
    public void NoticePersonCards()
    {
        NoticeProfileColumns();
        NoticeContactNotes();
    }

    private void NoticeProfileColumns()
    {
        var mine = ColumnsOf("main", "contact_profile");
        var theirs = ColumnsOf("gelen", "contact_profile");

        // The stamp is bookkeeping, not content: MergeFields takes the later of the two, and two
        // machines writing the same birthday at different moments is not a disagreement.
        var shared = mine
            .Where(theirs.Contains)
            .Where(c => c is not ("contact_id" or "id" or "updated_at"))
            .ToList();

        if (shared.Count == 0) return;

        var columns = string.Join(", ", shared.Select(c =>
            $"s.\"{c}\" AS \"t_{c}\", p.\"{c}\" AS \"m_{c}\""));

        var rows = connection.Query(
            $"""
             SELECT k.new AS contact_id, c.name_normalised AS folded, c.app AS app, {columns}
               FROM gelen.contact_profile s
               JOIN map_contact k ON k.old = s.contact_id
               JOIN main.contact c ON c.id = k.new
               LEFT JOIN main.contact_profile p ON p.contact_id = k.new
              WHERE k.old NOT IN (SELECT old FROM new_contact);
             """,
            transaction: transaction);

        foreach (var row in rows)
        {
            var fields = (IDictionary<string, object>)row;

            var contactId = Convert.ToInt64(fields["contact_id"]);
            var anchor = LeftoverFingerprint.ContactAnchor(
                Convert.ToString(fields["folded"]) ?? "", Convert.ToInt64(fields["app"]));

            foreach (var column in shared)
            {
                Classify(
                    LeftoverKinds.Person, anchor, column,
                    Text(fields[$"m_{column}"]), Text(fields[$"t_{column}"]),
                    callId: null, contactId: contactId, quote: null,
                    apply: null,
                    // MergeFields does the writing. Saying "applied" here is not a guess: this
                    // runs before it, and its rule for a blank column is unconditional.
                    appliedElsewhere: true);
            }
        }
    }

    private void NoticeContactNotes()
    {
        var rows = connection.Query<NoteRow>(
            """
            SELECT k.new AS contact_id, c.name_normalised AS folded, c.app AS app,
                   c.notes AS mine, s.notes AS theirs
              FROM gelen.contact s
              JOIN map_contact k ON k.old = s.id
              JOIN main.contact c ON c.id = k.new
             WHERE k.old NOT IN (SELECT old FROM new_contact);
            """,
            transaction: transaction);

        foreach (var row in rows)
        {
            Classify(
                LeftoverKinds.Person,
                LeftoverFingerprint.ContactAnchor(row.folded, row.app),
                "notes", Text(row.mine), Text(row.theirs),
                callId: null, contactId: row.contact_id, quote: null,
                apply: null, appliedElsewhere: true);
        }
    }

    private sealed class NoteRow
    {
        public long contact_id { get; set; }
        public string folded { get; set; } = "";
        public long app { get; set; }
        public string? mine { get; set; }
        public string? theirs { get; set; }
    }

    // ---- decisions on a shared conversation ---------------------------------

    /// <summary>
    /// Everything the user decided on a conversation both machines have.
    ///
    /// <paramref name="apply"/> false is the rollback switch §7.3 asks for: the carrying stops
    /// and the COUNTING AND THE LIST DO NOT. Every decision that would have been applied becomes
    /// a leftover instead, so switching the feature off degrades it to "you decide" rather than
    /// back to the silent loss it replaced.
    /// </summary>
    public void CarryCallDecisions(bool apply)
    {
        CarryPromiseRulings(apply);
        CarryCallNotes(apply);
        CarryCallTags(apply);
        CarryVerdicts(apply);
        CarrySuggestionRulings(apply);
        CarryBoardCards(apply);
    }

    private void CarryPromiseRulings(bool apply)
    {
        var incoming = connection.Query<PromiseRow>(
            $"""
             SELECT k.new AS call_id, mc.started_at AS started_at, mc.app AS app,
                    s.by_me, s.quote, s.quote_start_ms,
                    s.status, s.dismissed_by_user,
                    s.fulfilled_at, s.decided_at,
                    s.user_deadline_date, s.user_obligation, s.edited_at,
                    (SELECT new FROM map_call WHERE old = s.fulfilled_by_call_id) AS fulfilled_by
               FROM gelen.commitment s
               JOIN map_call k ON k.old = s.call_id
               JOIN main.call mc ON mc.id = k.new
              WHERE {OfSharedCalls}
                AND (s.decided_at IS NOT NULL OR s.edited_at IS NOT NULL);
             """,
            transaction: transaction).ToList();

        if (incoming.Count == 0) return;

        var here = connection.Query<PromiseRow>(
            $"""
             SELECT c.id, c.call_id, c.by_me, c.quote, c.quote_start_ms,
                    c.status, c.dismissed_by_user,
                    c.fulfilled_at, c.decided_at,
                    c.user_deadline_date, c.user_obligation, c.edited_at
               FROM main.commitment c
              WHERE c.call_id IN ({SharedCallIds});
             """,
            transaction: transaction).ToList();

        var byKey = here
            .GroupBy(r => (r.call_id, r.by_me, TurkishText.NormalizeForSearch(r.quote)))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var row in incoming)
        {
            var key = (row.call_id, row.by_me, TurkishText.NormalizeForSearch(row.quote));

            // Several promises can share one sentence; the millisecond breaks the tie. It is not
            // part of the key because a re-transcription moves it by a few frames, and a ruling
            // that missed over four milliseconds would be a leftover for no reason at all.
            var match = byKey.TryGetValue(key, out var candidates)
                ? candidates.OrderBy(c => Math.Abs(c.quote_start_ms - row.quote_start_ms)).First()
                : null;

            var anchor = LeftoverFingerprint.CallAnchor(
                ParseIso(row.started_at), row.app, $"{row.by_me} {row.quote}");

            Classify(
                LeftoverKinds.Promise, anchor, "karar",
                match is null ? null : Describe(match),
                Describe(row),
                callId: row.call_id, contactId: null, quote: row.quote,
                apply: match is null || !apply ? null : () => connection.Execute(
                    """
                    UPDATE main.commitment
                       SET status = @status,
                           dismissed_by_user = @dismissed,
                           fulfilled_at = @fulfilledAt,
                           decided_at = @decidedAt,
                           user_deadline_date = @userDeadline,
                           user_obligation = @userObligation,
                           edited_at = @editedAt,
                           fulfilled_by_call_id = @fulfilledBy
                     WHERE id = @id;
                    """,
                    new
                    {
                        id = match.id,
                        status = row.status,
                        dismissed = row.dismissed_by_user,
                        fulfilledAt = row.fulfilled_at,
                        decidedAt = row.decided_at,
                        userDeadline = row.user_deadline_date,
                        userObligation = row.user_obligation,
                        editedAt = row.edited_at,
                        fulfilledBy = row.fulfilled_by,
                    },
                    transaction),
                blankHere: match is null || match.IsUnruled);
        }
    }

    /// <summary>
    /// A ruling reduced to what it says, with the stamps left out.
    ///
    /// Two machines that agree the promise was kept will not agree on the millisecond the button
    /// was pressed, and comparing the stamps would make every agreement look like a conflict and
    /// fill the user's list with questions that have one answer.
    /// </summary>
    private static string Describe(PromiseRow row) =>
        $"durum={row.status}"
        + $" · susturuldu={row.dismissed_by_user}"
        + $" · tarih={row.user_deadline_date ?? "-"}"
        + $" · söz={row.user_obligation ?? "-"}";

    private sealed class PromiseRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long app { get; set; }
        public long by_me { get; set; }
        public string quote { get; set; } = "";
        public long quote_start_ms { get; set; }
        public long status { get; set; }
        public long dismissed_by_user { get; set; }
        public string? fulfilled_at { get; set; }
        public string? decided_at { get; set; }
        public string? user_deadline_date { get; set; }
        public string? user_obligation { get; set; }
        public string? edited_at { get; set; }
        public long? fulfilled_by { get; set; }

        /// <summary>Nothing the user ever said about this one — the empty place a move fills.</summary>
        public bool IsUnruled =>
            decided_at is null && edited_at is null && status == 0 && dismissed_by_user == 0
            && user_deadline_date is null && user_obligation is null;
    }

    private void CarryCallNotes(bool apply)
    {
        var rows = connection.Query<CallTextRow>(
            $"""
             SELECT k.new AS call_id, mc.started_at AS started_at, mc.app AS app,
                    s.note AS theirs, n.note AS mine, s.updated_at AS stamp
               FROM gelen.call_note s
               JOIN map_call k ON k.old = s.call_id
               JOIN main.call mc ON mc.id = k.new
               LEFT JOIN main.call_note n ON n.call_id = k.new
              WHERE {OfSharedCalls};
             """,
            transaction: transaction);

        foreach (var row in rows)
        {
            Classify(
                LeftoverKinds.Note,
                LeftoverFingerprint.CallAnchor(ParseIso(row.started_at), row.app),
                "not", Text(row.mine), Text(row.theirs),
                callId: row.call_id, contactId: null, quote: null,
                apply: !apply ? null : () => connection.Execute(
                    """
                    INSERT INTO main.call_note (call_id, note, updated_at)
                    VALUES (@callId, @note, @stamp)
                    ON CONFLICT(call_id) DO UPDATE SET
                        note = excluded.note, updated_at = excluded.updated_at;
                    """,
                    new { callId = row.call_id, note = row.theirs, stamp = row.stamp ?? Iso(now) },
                    transaction));
        }
    }

    private sealed class CallTextRow
    {
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long app { get; set; }
        public string? mine { get; set; }
        public string? theirs { get; set; }
        public string? stamp { get; set; }
    }

    /// <summary>
    /// The labels the user put on a conversation.
    ///
    /// The one kind here that can never produce a leftover, and that is a property of tags rather
    /// than a shortcut: a tag is present or it is not, so an incoming one either already exists —
    /// nothing to do — or lands where there was nothing. Two machines cannot disagree about a
    /// tag; they can only each know one the other does not.
    /// </summary>
    private void CarryCallTags(bool apply)
    {
        var rows = connection.Query<TagRow>(
            $"""
             SELECT k.new AS call_id, mc.started_at AS started_at, mc.app AS app,
                    s.tag, s.tag_folded, s.created_at,
                    (SELECT t.tag FROM main.call_tag t
                      WHERE t.call_id = k.new AND t.tag_folded = s.tag_folded) AS mine
               FROM gelen.call_tag s
               JOIN map_call k ON k.old = s.call_id
               JOIN main.call mc ON mc.id = k.new
              WHERE {OfSharedCalls};
             """,
            transaction: transaction);

        foreach (var row in rows)
        {
            Classify(
                LeftoverKinds.Tag,
                LeftoverFingerprint.CallAnchor(ParseIso(row.started_at), row.app, row.tag_folded),
                "etiket", Text(row.mine), row.tag,
                callId: row.call_id, contactId: null, quote: null,
                apply: !apply ? null : () => connection.Execute(
                    """
                    INSERT OR IGNORE INTO main.call_tag (call_id, tag, tag_folded, created_at)
                    VALUES (@callId, @tag, @folded, @createdAt);
                    """,
                    new
                    {
                        callId = row.call_id, tag = row.tag,
                        folded = row.tag_folded, createdAt = row.created_at,
                    },
                    transaction),
                // Two spellings of one tag are one tag, so the presence of the folded form decides
                // sameness. Without this, "Önemli" here and "ONEMLI" there would be filed as a
                // disagreement the user has to rule on, which is not a disagreement at all.
                sameWhen: (m, _) => m is not null);
        }
    }

    private sealed class TagRow
    {
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long app { get; set; }
        public string tag { get; set; } = "";
        public string tag_folded { get; set; } = "";
        public string created_at { get; set; } = "";
        public string? mine { get; set; }
    }

    /// <summary>
    /// What the user heard when they listened back and said whether the machine got it right.
    ///
    /// The most expensive decision in the archive to reproduce — it costs somebody listening to a
    /// recording again — and until now it never crossed for a conversation both machines had.
    /// </summary>
    private void CarryVerdicts(bool apply)
    {
        var rows = connection.Query<VerdictRow>(
            $"""
             SELECT k.new AS call_id, mc.started_at AS started_at, mc.app AS app,
                    s.kind, s.target_id, s.quote_folded, s.start_ms, s.verdict, s.decided_at,
                    (SELECT v.verdict FROM main.verdict v
                      WHERE v.call_id = k.new AND v.kind = s.kind
                        AND v.quote_folded = s.quote_folded AND v.start_ms = s.start_ms) AS mine
               FROM gelen.verdict s
               JOIN map_call k ON k.old = s.call_id
               JOIN main.call mc ON mc.id = k.new
              WHERE {OfSharedCalls};
             """,
            transaction: transaction);

        foreach (var row in rows)
        {
            Classify(
                LeftoverKinds.Verdict,
                LeftoverFingerprint.CallAnchor(
                    ParseIso(row.started_at), row.app,
                    $"{row.kind} {row.quote_folded} {row.start_ms}"),
                row.kind,
                row.mine?.ToString(), row.verdict.ToString(),
                callId: row.call_id, contactId: null, quote: row.quote_folded,
                apply: !apply ? null : () => connection.Execute(
                    """
                    INSERT INTO main.verdict
                        (call_id, kind, target_id, quote_folded, start_ms, verdict, decided_at)
                    VALUES (@callId, @kind, @targetId, @folded, @startMs, @verdict, @decidedAt);
                    """,
                    new
                    {
                        callId = row.call_id,
                        kind = row.kind,

                        // Carried raw, exactly as the ordinary copy carries it for a new call.
                        // The schema says out loud that target_id means nothing after a merge and
                        // has no foreign key for that reason; the words and the millisecond are
                        // the identity, and those survive.
                        targetId = row.target_id,

                        folded = row.quote_folded,
                        startMs = row.start_ms,
                        verdict = row.verdict,
                        decidedAt = row.decided_at,
                    },
                    transaction));
        }
    }

    private sealed class VerdictRow
    {
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long app { get; set; }
        public string kind { get; set; } = "";
        public long? target_id { get; set; }
        public string quote_folded { get; set; } = "";
        public long start_ms { get; set; }
        public long verdict { get; set; }
        public string decided_at { get; set; } = "";
        public long? mine { get; set; }
    }

    /// <summary>
    /// What the user did with a suggested next move: done, hidden, routed to a reminder.
    ///
    /// Matched on the same pair the re-run uses to keep a hidden suggestion hidden — the folded
    /// action and the folded quote — so the two cannot disagree about what counts as the same
    /// suggestion.
    /// </summary>
    private void CarrySuggestionRulings(bool apply)
    {
        var incoming = connection.Query<ActionRow>(
            $"""
             SELECT k.new AS call_id, mc.started_at AS started_at, mc.app AS app,
                    s.action, s.quote, s.status, s.routed_note, s.decided_at
               FROM gelen.action_item s
               JOIN map_call k ON k.old = s.call_id
               JOIN main.call mc ON mc.id = k.new
              WHERE {OfSharedCalls} AND s.decided_at IS NOT NULL;
             """,
            transaction: transaction).ToList();

        if (incoming.Count == 0) return;

        var here = connection.Query<ActionRow>(
            $"""
             SELECT a.id, a.call_id, a.action, a.quote, a.status, a.routed_note, a.decided_at
               FROM main.action_item a
              WHERE a.call_id IN ({SharedCallIds});
             """,
            transaction: transaction).ToList();

        var byKey = here.ToLookup(r => (
            r.call_id,
            TurkishText.NormalizeForSearch(r.action),
            TurkishText.NormalizeForSearch(r.quote)));

        foreach (var row in incoming)
        {
            var match = byKey[(
                row.call_id,
                TurkishText.NormalizeForSearch(row.action),
                TurkishText.NormalizeForSearch(row.quote))].FirstOrDefault();

            Classify(
                LeftoverKinds.Suggestion,
                LeftoverFingerprint.CallAnchor(
                    ParseIso(row.started_at), row.app, $"{row.action} {row.quote}"),
                "karar",
                match is null ? null : Describe(match),
                Describe(row),
                callId: row.call_id, contactId: null, quote: row.quote,
                apply: match is null || !apply ? null : () => connection.Execute(
                    "UPDATE main.action_item SET status = @status, routed_note = @routed, "
                    + "decided_at = @decidedAt WHERE id = @id;",
                    new
                    {
                        id = match.id, status = row.status,
                        routed = row.routed_note, decidedAt = row.decided_at,
                    },
                    transaction),
                blankHere: match is null || match.IsUnruled);
        }
    }

    private static string Describe(ActionRow row) =>
        $"durum={row.status}" + (row.routed_note is null ? "" : $" · {row.routed_note}");

    private sealed class ActionRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long app { get; set; }
        public string action { get; set; } = "";
        public string quote { get; set; } = "";
        public long status { get; set; }
        public string? routed_note { get; set; }
        public string? decided_at { get; set; }

        public bool IsUnruled => decided_at is null && status == 0;
    }

    /// <summary>Where the user filed the conversation on the board.</summary>
    private void CarryBoardCards(bool apply)
    {
        var rows = connection.Query<BoardRow>(
            $"""
             SELECT k.new AS call_id, mc.started_at AS started_at, mc.app AS app,
                    s.lane, s.position, s.title, s.remind_on, s.created_at,
                    b.lane AS my_lane, b.title AS my_title, b.remind_on AS my_remind
               FROM gelen.board_card s
               JOIN map_call k ON k.old = s.call_id
               JOIN main.call mc ON mc.id = k.new
               LEFT JOIN main.board_card b ON b.call_id = k.new
              WHERE {OfSharedCalls};
             """,
            transaction: transaction);

        foreach (var row in rows)
        {
            Classify(
                LeftoverKinds.Board,
                LeftoverFingerprint.CallAnchor(ParseIso(row.started_at), row.app),
                "pano",
                row.my_lane is null ? null : Describe(row.my_lane, row.my_title, row.my_remind),
                Describe(row.lane, row.title, row.remind_on),
                callId: row.call_id, contactId: null, quote: null,
                apply: !apply ? null : () => connection.Execute(
                    """
                    INSERT OR IGNORE INTO main.board_card
                        (call_id, lane, position, title, remind_on, created_at)
                    VALUES (@callId, @lane, @position, @title, @remindOn, @createdAt);
                    """,
                    new
                    {
                        callId = row.call_id, lane = row.lane, position = row.position,
                        title = row.title, remindOn = row.remind_on, createdAt = row.created_at,
                    },
                    transaction));
        }
    }

    private static string Describe(string lane, string? title, string? remindOn) =>
        lane + (title is null ? "" : $" · {title}") + (remindOn is null ? "" : $" · {remindOn}");

    private sealed class BoardRow
    {
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long app { get; set; }
        public string lane { get; set; } = "";
        public long position { get; set; }
        public string? title { get; set; }
        public string? remind_on { get; set; }
        public string created_at { get; set; } = "";
        public string? my_lane { get; set; }
        public string? my_title { get; set; }
        public string? my_remind { get; set; }
    }

    // ---- the one rule, in one place -----------------------------------------

    /// <summary>
    /// Decides where one incoming decision goes, and sends it there.
    ///
    /// Every kind above funnels through here so the rule cannot drift between them: the day
    /// somebody adds a seventh kind of decision, the arithmetic is already correct for it.
    ///
    /// <paramref name="apply"/> null means the decision cannot be applied here — there was no
    /// local row to write it into (a re-transcribed conversation whose quotes no longer match),
    /// or the carrying is switched off. Either way the decision is not lost: it goes into the
    /// list with nothing on the local side, which says what actually happened.
    ///
    /// <paramref name="appliedElsewhere"/> is for the person card, whose writing belongs to
    /// <c>MergeFields</c>. It counts as carried because it will be, by the statement that runs
    /// next, under the same rule.
    /// </summary>
    private void Classify(
        string kind,
        string anchor,
        string field,
        string? mine,
        string? theirs,
        long? callId,
        long? contactId,
        string? quote,
        Action? apply,
        bool? blankHere = null,
        bool appliedElsewhere = false,
        Func<string?, string?, bool>? sameWhen = null)
    {
        // Nothing was decided over there. Not a decision, so not counted: a merge reporting
        // "412 kararın 0'ı getirildi" for an archive where nobody wrote anything would be noise
        // in the one sentence that has to be readable.
        if (string.IsNullOrWhiteSpace(theirs)) return;

        _seen++;

        var empty = blankHere ?? string.IsNullOrWhiteSpace(mine);

        if (!empty)
        {
            var same = sameWhen is not null
                ? sameWhen(mine, theirs)
                : string.Equals(mine?.Trim(), theirs.Trim(), StringComparison.Ordinal);

            if (same)
            {
                _same++;
                return;
            }

            // Both machines wrote, and they differ. What is here stands; the other value waits.
            Leave(kind, anchor, field, mine, theirs, callId, contactId, quote);
            return;
        }

        if (appliedElsewhere)
        {
            _carried++;
            return;
        }

        if (apply is null)
        {
            // An empty place and no way to write into it.
            Leave(kind, anchor, field, mine: null, theirs, callId, contactId, quote);
            return;
        }

        apply();
        _carried++;
    }

    private void Leave(
        string kind, string anchor, string field,
        string? mine, string? theirs, long? callId, long? contactId, string? quote)
    {
        _left.Add(new Pending(
            LeftoverFingerprint.Of(kind, anchor, field, theirs),
            kind, callId, contactId, field, mine, theirs, quote));
    }

    /// <summary>
    /// Writes the list.
    ///
    /// INSERT OR IGNORE on the fingerprint, which is what makes a second import of the same file
    /// change nothing and — more importantly — ask nothing. A question the user has already
    /// answered keeps its answer, because the answered row is still there to collide with. The
    /// designer's warning was specific: without this identity, "burada kalsın" would be asked
    /// again on every future backup, forever.
    /// </summary>
    /// <returns>How many rows this import actually added, which is not how many it left.</returns>
    public int Flush()
    {
        var written = 0;

        foreach (var row in _left)
        {
            written += connection.Execute(
                """
                INSERT OR IGNORE INTO main.import_leftover
                    (fingerprint, source_archive_id, kind, call_id, contact_id,
                     field, mine, theirs, quote, noticed_at)
                VALUES (@fingerprint, @source, @kind, @callId, @contactId,
                        @field, @mine, @theirs, @quote, @noticedAt);
                """,
                new
                {
                    fingerprint = row.Fingerprint,
                    source = sourceArchiveId,
                    kind = row.Kind,
                    callId = row.CallId,
                    contactId = row.ContactId,
                    field = row.Field,
                    mine = row.Mine,
                    theirs = row.Theirs,
                    quote = row.Quote,
                    noticedAt = Iso(now),
                },
                transaction);
        }

        return written;
    }

    private HashSet<string> ColumnsOf(string schema, string table) =>
        [.. connection
            .Query<ColumnName>($"PRAGMA {schema}.table_info(\"{table}\");", transaction: transaction)
            .Select(r => r.name)];

    private sealed class ColumnName
    {
        public string name { get; set; } = "";
    }

    private static string? Text(object? value) =>
        value is null or DBNull ? null : Convert.ToString(value);

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
            | System.Globalization.DateTimeStyles.AdjustToUniversal);
}
