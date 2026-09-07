using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Storage;

/// <summary>
/// What a complete deletion actually managed to do.
///
/// Carries the failures because they matter more than the successes: a file that could not be
/// removed is a recording of a real person still sitting on disk after they were told it was
/// gone, and the interface has to be able to say so.
/// </summary>
public sealed record DeletionResult(int FilesRemoved, IReadOnlyList<string> FilesLeftBehind)
{
    public bool IsComplete => FilesLeftBehind.Count == 0;
}

/// <summary>One call that an import brought in, before its audio has been put in place.</summary>
/// <param name="Id">The identifier it was given here, which is not the one it had in the archive.</param>
/// <param name="MicPath">The path the archive recorded, on the machine that wrote it.</param>
public sealed record ImportedCall(long Id, string? MicPath, string? FarPath, DateTimeOffset StartedAt);

/// <summary>What a merge actually added, for the sentence the user reads afterwards.</summary>
/// <param name="Decisions">
/// What happened to the rulings on conversations both machines already had. Carried, already the
/// same, or left in a list — and the three add up to what was seen, always.
/// </param>
public sealed record MergeCounts(
    int Contacts,
    int Calls,
    int Segments,
    int AlreadyHere,
    IReadOnlyList<ImportedCall> NewCalls,
    DecisionCounts Decisions);

public sealed record SearchHit(
    long CallId,
    long SegmentId,
    long? ContactId,
    string? ContactName,
    DateTimeOffset CallStartedAt,
    bool IsMe,
    int StartMs,
    string Text);

/// <summary>
/// One conversation's stored ledger, reduced to the keys the pipeline compares on.
///
/// Folded rather than raw, because "Tamam ayarlarım" and "tamam ayarlarım" are the same sentence
/// and a model does not reliably pick one of them — the same normalisation the pipeline's own
/// de-duplication uses, so the two cannot disagree about what counts as already written.
/// </summary>
public sealed record StoredLedgerKeys(
    IReadOnlySet<(bool ByMe, string FoldedQuote)> Commitments,
    IReadOnlySet<(string Entity, string Attribute, string Value, string FoldedQuote)> Claims)
{
    /// <summary>Nothing stored — what a run that is about to clear everything compares against.</summary>
    public static readonly StoredLedgerKeys None = new(
        new HashSet<(bool ByMe, string FoldedQuote)>(),
        new HashSet<(string Entity, string Attribute, string Value, string FoldedQuote)>());
}

/// <summary>
/// One promise a re-analysis is not allowed to touch, and enough of it to recognise which of the
/// run's findings it is.
///
/// A list rather than a set of keys, and the obligation is carried beside the quote, because one
/// sentence can hold two promises: <see cref="QuoteVerifier"/> hands back the whole segment for a
/// quote found inside it, so both readings arrive with the same words and the same millisecond.
/// Reduced to (whose, quote) they are indistinguishable, and the pipeline used the tombstone of
/// the one the user turned down to withhold the one they kept.
///
/// The obligation stored here is the MACHINE's — the model's own wording. A rewording the user
/// typed is theirs and lives in another column; matching a model's next reading against it would
/// be comparing the model with the user.
/// </summary>
public sealed record SurvivingCommitment(bool ByMe, string FoldedQuote, string FoldedObligation);

/// <summary>
/// All database access.
///
/// Normalised columns are filled here and nowhere else. If a caller could write `text` without
/// `text_normalised`, the search index would quietly disagree with the visible data — and
/// because FTS5 returns no error for a miss, that would look like missing data rather than a bug.
/// </summary>
public sealed class Repository(Database database)
{
    private SqliteConnection Open() => database.Open();

    // ---- contacts -----------------------------------------------------------

    public long UpsertContact(string name, CallApp app, string? handle = null)
    {
        var cleaned = TurkishText.StripFormatting(name);
        if (cleaned.Length == 0) throw new ArgumentException("Contact name cannot be empty.", nameof(name));

        var normalised = TurkishText.NormalizeForSearch(cleaned);

        using var connection = Open();

        var existing = connection.QueryFirstOrDefault<long?>(
            "SELECT id FROM contact WHERE name_normalised = @normalised AND app = @app;",
            new { normalised, app = (int)app });

        if (existing is { } id)
        {
            if (handle is not null)
            {
                connection.Execute(
                    "UPDATE contact SET handle = COALESCE(handle, @handle) WHERE id = @id;",
                    new { handle, id });
            }

            return id;
        }

        return connection.ExecuteScalar<long>(
            """
            INSERT INTO contact (name, name_normalised, app, handle, created_at, call_count)
            VALUES (@name, @normalised, @app, @handle, @createdAt, 0)
            RETURNING id;
            """,
            new
            {
                name = cleaned,
                normalised,
                app = (int)app,
                handle,
                createdAt = Iso(DateTimeOffset.UtcNow),
            });
    }

    public Contact? GetContact(long id)
    {
        using var connection = Open();
        return connection.QueryFirstOrDefault<ContactRow>(
            "SELECT * FROM contact WHERE id = @id;", new { id })?.ToModel();
    }

    public IReadOnlyList<Contact> ListContacts()
    {
        using var connection = Open();
        return [.. connection.Query<ContactRow>(
            "SELECT * FROM contact ORDER BY COALESCE(last_call_at, created_at) DESC;")
            .Select(r => r.ToModel())];
    }

    /// <summary>Finds contacts by any spelling of the name, including partial words.</summary>
    public IReadOnlyList<Contact> FindContacts(string query)
    {
        var normalised = TurkishText.NormalizeForSearch(query);
        if (normalised.Length == 0) return [];

        using var connection = Open();
        return [.. connection.Query<ContactRow>(
            "SELECT * FROM contact WHERE name_normalised LIKE @pattern ORDER BY call_count DESC LIMIT 50;",
            new { pattern = $"%{normalised}%" })
            .Select(r => r.ToModel())];
    }

    /// <summary>The five most recently contacted people, for the post-call labelling prompt.</summary>
    public IReadOnlyList<Contact> RecentContacts(int limit = 5)
    {
        using var connection = Open();
        return [.. connection.Query<ContactRow>(
            "SELECT * FROM contact WHERE last_call_at IS NOT NULL ORDER BY last_call_at DESC LIMIT @limit;",
            new { limit })
            .Select(r => r.ToModel())];
    }

    // ---- learned title bindings --------------------------------------------

    /// <summary>
    /// Remembers that a window title belongs to a contact.
    ///
    /// Telegram gives the counterpart's name in the call window title. WhatsApp titles its
    /// window "WhatsApp" and nothing more, so the user labels the call once and this makes the
    /// next one automatic — accuracy grows with use instead of depending on a fragile scrape.
    /// </summary>
    /// <summary>
    /// Remembers that a window title belongs to a contact, and notices when it cannot.
    ///
    /// Returns true when the binding was kept, false when this title has now been shown to
    /// identify nobody — the caller says so, because the user has just been promised that
    /// labelling once means never being asked again.
    ///
    /// The rebind that used to happen here is what made every WhatsApp conversation "Uliana".
    /// A title bound to one person and later offered for another was simply reassigned, so the
    /// pattern went on capturing calls — it just captured them for whoever was named most
    /// recently. Two different people behind one title is not a conflict to resolve, it is proof
    /// the title means nothing, and the only correct response is to stop using it.
    /// </summary>
    public bool RememberTitle(string title, long contactId, CallApp app)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (pattern.Length == 0) return false;

        // "Voice call" identifies nobody. Bound once, it would file every later call under this
        // contact; refused here so that the promise "I will not ask again" is never made on it.
        if (Detection.GenericTitles.IsGeneric(pattern)) return false;

        using var connection = Open();

        var existing = connection.QueryFirstOrDefault<long?>(
            "SELECT contact_id FROM title_binding WHERE title_pattern = @pattern AND app = @app;",
            new { pattern, app = (int)app });

        if (existing is { } bound && bound != contactId)
        {
            connection.Execute(
                """
                UPDATE title_binding
                   SET unreliable = 1, last_used_at = @now
                 WHERE title_pattern = @pattern AND app = @app;
                """,
                new { pattern, app = (int)app, now = Iso(DateTimeOffset.UtcNow) });

            return false;
        }

        connection.Execute(
            """
            INSERT INTO title_binding (title_pattern, contact_id, app, times_used, last_used_at)
            VALUES (@pattern, @contactId, @app, 1, @now)
            ON CONFLICT(title_pattern, app) DO UPDATE SET
                times_used   = title_binding.times_used + 1,
                last_used_at = excluded.last_used_at;
            """,
            new { pattern, contactId, app = (int)app, now = Iso(DateTimeOffset.UtcNow) });

        return true;
    }

    /// <summary>
    /// Whether remembering this title could ever identify somebody — asked before the offer is
    /// made rather than after it has been accepted.
    ///
    /// <see cref="RememberTitle"/> refuses a title that is generic or already claimed by a
    /// different contact, and the labelling window used to report that refusal in a message box
    /// after the fact: "kaydedildi, ama başlık hatırlanmadı". Correct, and the wrong shape. The
    /// promise is made by a ticked checkbox, so the honest place to withdraw it is the checkbox —
    /// before it is ticked, not in an apology afterwards. In an archive where one "Voice call"
    /// title is spread across eight contacts, that apology arrives on nearly every call.
    ///
    /// False here means the same thing RememberTitle would have returned: this title names the
    /// chat window that happened to be open, not the person on the other end.
    /// </summary>
    public bool CanRememberTitle(string? title, CallApp app, long forContactId)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (Detection.GenericTitles.IsGeneric(pattern)) return false;

        using var connection = Open();

        var existing = connection.QueryFirstOrDefault<(long ContactId, long Unreliable)?>(
            """
            SELECT contact_id AS ContactId, unreliable AS Unreliable
            FROM title_binding
            WHERE title_pattern = @pattern AND app = @app;
            """,
            new { pattern, app = (int)app });

        // Free, or already this person's. Anything else is a title two people answer to.
        return existing is not { } bound || (bound.Unreliable == 0 && bound.ContactId == forContactId);
    }

    public long? ResolveTitle(string? title, CallApp app)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (pattern.Length == 0) return null;
        if (Detection.GenericTitles.IsGeneric(pattern)) return null;

        using var connection = Open();

        // Patterns known to identify nobody are not consulted. Filing a call under a name on
        // this evidence is worse than leaving it unnamed: an unnamed call asks a question, and a
        // wrongly named one quietly corrupts two people's histories at once.
        return connection.QueryFirstOrDefault<long?>(
            """
            SELECT contact_id FROM title_binding
             WHERE title_pattern = @pattern AND app = @app AND unreliable = 0;
            """,
            new { pattern, app = (int)app });
    }

    /// <summary>Forgets a learned title, so the next call with it asks again.</summary>
    public void ForgetTitle(string title, CallApp app)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (pattern.Length == 0) return;

        using var connection = Open();
        connection.Execute(
            "DELETE FROM title_binding WHERE title_pattern = @pattern AND app = @app;",
            new { pattern, app = (int)app });
    }

    // ---- calls --------------------------------------------------------------

    public long InsertCall(Call call)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO call (contact_id, app, direction, kind, started_at, ended_at, duration_ms,
                              mic_path, far_path, state, failure_reason, observed_title,
                              capture_stats, likely_no_headphones, is_pinned, audio_sha256)
            VALUES (@ContactId, @App, @Direction, @Kind, @StartedAt, @EndedAt, @DurationMs,
                    @MicPath, @FarPath, @State, @FailureReason, @ObservedTitle,
                    @CaptureStats, @LikelyNoHeadphones, @IsPinned, @AudioSha256)
            RETURNING id;
            """,
            new
            {
                call.ContactId,
                App = (int)call.App,
                Direction = (int)call.Direction,
                Kind = (int)call.Kind,
                StartedAt = Iso(call.StartedAt),
                EndedAt = call.EndedAt is { } e ? Iso(e) : null,
                DurationMs = (long)call.Duration.TotalMilliseconds,
                call.MicPath,
                call.FarPath,
                State = (int)call.State,
                call.FailureReason,
                call.ObservedTitle,
                call.CaptureStats,
                LikelyNoHeadphones = call.LikelyNoHeadphones ? 1 : 0,
                IsPinned = call.IsPinned ? 1 : 0,
                call.AudioSha256,
            });
    }

    /// <summary>
    /// Writes what a finished recording actually produced.
    ///
    /// The row is inserted when a call is detected, before anything has been recorded, so at that
    /// moment there is no duration and there are no file paths — they do not exist yet. This is
    /// the call that fills them in, and for a long time it did not exist at all.
    ///
    /// What that cost is worth spelling out, because none of it announced itself. Transcription
    /// was handed a null path and could never run. The waveform player read two null paths and
    /// silently declined to load, so it appeared only over the sample data, where the paths are
    /// written at insert. Every duration in the archive was zero. And deleting a contact looked
    /// for their recordings with "mic_path IS NOT NULL", found none, and left hours of somebody
    /// talking on disk after telling the user it was gone.
    /// </summary>
    public void CompleteCall(
        long callId,
        string? micPath,
        string? farPath,
        TimeSpan duration,
        DateTimeOffset endedAt,
        string? captureStats = null)
    {
        using var connection = Open();

        connection.Execute(
            """
            UPDATE call
               SET mic_path      = @micPath,
                   far_path      = @farPath,
                   duration_ms   = @durationMs,
                   ended_at      = @endedAt,
                   capture_stats = COALESCE(@captureStats, capture_stats)
             WHERE id = @callId;
            """,
            new
            {
                callId,
                micPath,
                farPath,
                durationMs = (long)duration.TotalMilliseconds,
                endedAt = endedAt.ToString("o"),
                captureStats,
            });
    }

    public void SetCallState(long callId, ProcessingState state, string? failureReason = null)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE call SET state = @state, failure_reason = @failureReason WHERE id = @callId;",
            new { state = (int)state, failureReason, callId });
    }

    /// <summary>
    /// Puts a call under a contact, moving everything the call produced along with it.
    ///
    /// <b>Everything, and in one transaction.</b> A call is not just a row: the commitments,
    /// claims and flags extracted from it each carry their own <c>contact_id</c>, and moving only
    /// the call would leave the promise filed under one person and the conversation it was made in
    /// under another. That corrupts two histories at once and does it invisibly — both look
    /// complete, and the ledger simply stops noticing that a price moved or a deadline slipped.
    ///
    /// <b>Both contacts are recounted, not just the new one.</b> The counters are derived from the
    /// calls rather than incremented, but only the destination used to be recalculated — so the
    /// contact a call was moved <i>away</i> from kept claiming it. That did not show while the only
    /// caller assigned a previously unassigned call; it appears the moment moving is possible,
    /// which is what this method exists for.
    ///
    /// Safe to call when the call is already under <paramref name="contactId"/>, and safe when it
    /// had no contact at all — that is how a newly recorded call is labelled for the first time.
    /// </summary>
    /// <returns>The contact the call was taken from, if it had one.</returns>
    public long? AssignContact(long callId, long contactId, ContactSource source = ContactSource.User)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var previous = connection.QueryFirstOrDefault<long?>(
            "SELECT contact_id FROM call WHERE id = @callId;", new { callId }, transaction);

        connection.Execute(
            "UPDATE call SET contact_id = @contactId, contact_source = @source WHERE id = @callId;",
            new { contactId, callId, source = source.Wire() }, transaction);

        // The ledger entries this call produced travel with it. Scoped by call_id rather than by
        // the old contact, so a call that had no contact yet is handled by the same statement.
        foreach (var table in LedgerTables)
        {
            connection.Execute(
                $"UPDATE {table} SET contact_id = @contactId WHERE call_id = @callId;",
                new { contactId, callId }, transaction);
        }

        Recount(connection, transaction, contactId);
        if (previous is { } from && from != contactId) Recount(connection, transaction, from);

        transaction.Commit();

        return previous == contactId ? null : previous;
    }

    /// <summary>
    /// Every table whose rows belong to a contact and must follow them. Listed once so that
    /// adding another derived table is a single edit rather than a bug that only appears after
    /// somebody moves a call. (verdict is not here: it has no contact_id and follows its call.)
    ///
    /// action_item is in this list because its contact_id is ON DELETE CASCADE: left behind by a
    /// merge, the rows were destroyed the moment the absorbed contact was deleted. Merging two
    /// spellings of one person therefore threw away half their outstanding actions, silently,
    /// as part of an operation whose whole purpose is to lose nothing.
    ///
    /// ask_exchange is here for the second of those two reasons rather than the first. Its rows
    /// are not the ledger's and nothing counts them, but its contact_id is what a stored answer
    /// was narrowed BY — so a call moved to another person must take its questions with it, and a
    /// merge must carry "what I asked about Ahmet" onto the surviving Ahmet. Left out, the scope
    /// line under a stored answer would go on naming a person the conversation is no longer with.
    ///
    /// import_leftover is here for the action_item reason, sharpened. Its contact_id is ON DELETE
    /// CASCADE, so a leftover filed against a person would be destroyed the moment two spellings
    /// of that person were merged — and a leftover is by construction a decision the archive has
    /// promised not to lose. Losing one inside an operation whose whole purpose is to lose nothing
    /// would be the package's own invariant broken by the feature next door.
    /// </summary>
    private static readonly string[] LedgerTables =
    [
        "commitment", "claim", "flag", "action_item", "tactic_evidence", "speech_act",
        "ask_exchange", "import_leftover",
    ];

    /// <summary>
    /// How many ledger rows a call produced.
    ///
    /// Shown before a move is confirmed. A call is not one row — the promises, figures and flags
    /// taken out of it are filed against the same person and travel with it — and somebody moving
    /// a conversation to a different contact is also moving those, which is not obvious and is
    /// worth saying before rather than after.
    ///
    /// Its own list rather than <see cref="LedgerTables"/>, and the difference is speech_act.
    /// Those rows follow the call like the rest, but a conversation with forty questions in it
    /// is not "43 kayıt" to a person about to move it: the sentence is meant to say how much of
    /// the ledger they are moving, and a count dominated by every question anybody asked would
    /// make it meaningless in exactly the situation it exists for.
    ///
    /// ask_exchange is out for the same reason and one more: those rows are not extracted from the
    /// conversation at all — they are what the user asked about it — so counting them would inflate
    /// a figure that claims to describe what the analysis produced.
    /// </summary>
    private static readonly string[] CountedLedgerTables =
        ["commitment", "claim", "flag", "action_item", "tactic_evidence"];

    public int CountLedgerEntriesForCall(long callId)
    {
        using var connection = Open();

        return CountedLedgerTables.Sum(table => connection.ExecuteScalar<int>(
            $"SELECT COUNT(*) FROM {table} WHERE call_id = @callId;", new { callId }));
    }

    /// <summary>
    /// Recomputes every contact's counters from the calls themselves.
    ///
    /// Run once at startup, because these counters can already be wrong on a database that exists
    /// today. Until recently, moving a call between contacts recalculated only the destination, so
    /// the contact it was taken from went on counting it. A contact row saying "1 görüşme" above a
    /// list of nine is not a cosmetic problem: it is the archive stating something the user can
    /// see is false, and that costs them their trust in everything else on the screen.
    ///
    /// Cheap enough to be unconditional. The call table is small by construction — a few thousand
    /// rows after years of use — and this is one grouped scan of it.
    /// </summary>
    /// <returns>How many contacts were actually wrong.</returns>
    public int RecountAllContacts()
    {
        using var connection = Open();

        return connection.Execute(
            """
            UPDATE contact SET
                call_count   = (SELECT COUNT(*)         FROM call WHERE call.contact_id = contact.id),
                last_call_at = (SELECT MAX(started_at)  FROM call WHERE call.contact_id = contact.id)
            WHERE call_count   IS NOT (SELECT COUNT(*)        FROM call WHERE call.contact_id = contact.id)
               OR last_call_at IS NOT (SELECT MAX(started_at) FROM call WHERE call.contact_id = contact.id);
            """);
    }

    /// <summary>Recomputes a contact's denormalised counters from the calls themselves.</summary>
    private static void Recount(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        long contactId)
    {
        connection.Execute(
            """
            UPDATE contact SET
                call_count   = (SELECT COUNT(*) FROM call WHERE contact_id = @contactId),
                last_call_at = (SELECT MAX(started_at) FROM call WHERE contact_id = @contactId)
            WHERE id = @contactId;
            """,
            new { contactId }, transaction);
    }

    /// <summary>
    /// Forgets a learned title-to-contact pairing.
    ///
    /// The reason a call lands under the wrong person is usually not a one-off mistake: the
    /// labelling dialog offers to remember the window title, that box is ticked by default, and a
    /// title that was not really a name — the conversation that happened to be open, an unread
    /// badge — gets bound to whoever was chosen. Every later call showing that title then resolves
    /// to the same wrong contact, and because the contact now looks known the dialog stops
    /// appearing, so nobody is ever asked again.
    ///
    /// Moving the call fixes the past. Removing the binding is what stops it happening again, and
    /// one without the other is half a repair.
    /// </summary>
    public int ForgetTitleBinding(string title, CallApp app)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (pattern.Length == 0) return 0;

        using var connection = Open();
        return connection.Execute(
            "DELETE FROM title_binding WHERE title_pattern = @pattern AND app = @app;",
            new { pattern, app = (int)app });
    }

    /// <summary>Every learned title pairing, newest first, so they can be reviewed and removed.</summary>
    public IReadOnlyList<(long Id, string Title, long ContactId, string ContactName, CallApp App, int TimesUsed)>
        TitleBindings()
    {
        using var connection = Open();

        return [.. connection.Query<(long, string, long, string, int, int)>(
                """
                SELECT b.id, b.title_pattern, b.contact_id, c.name, b.app, b.times_used
                FROM title_binding b
                JOIN contact c ON c.id = b.contact_id
                ORDER BY b.last_used_at DESC;
                """)
            .Select(r => (r.Item1, r.Item2, r.Item3, r.Item4, (CallApp)r.Item5, r.Item6))];
    }

    /// <summary>
    /// Folds one contact into another and removes the empty one.
    ///
    /// One person routinely ends up as two rows here, and the causes are ordinary rather than
    /// exotic: a window title that was not a name created a contact, the same name was typed with
    /// a different capitalisation, or the two arrived from different applications — contacts are
    /// keyed on <c>(name, app)</c>, so "Ahmet" on WhatsApp and "Ahmet" on Telegram are already two
    /// people as far as the archive is concerned.
    ///
    /// The cost of leaving them split is not cosmetic. Everything this product is for — noticing
    /// that a price moved between two calls, that a promise came due, that an account of events
    /// changed — is computed per contact, and a split history makes both halves look complete
    /// while the comparison across them silently never happens.
    ///
    /// Everything moves: calls, the ledger rows those calls produced, imported messages, and the
    /// learned title bindings. Bindings that would collide are dropped rather than merged, because
    /// a pattern can only point at one contact and the surviving one is the destination by
    /// definition.
    /// </summary>
    /// <returns>How many calls were moved.</returns>
    public int MergeContacts(long fromContactId, long intoContactId)
    {
        if (fromContactId == intoContactId) return 0;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var moved = connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM call WHERE contact_id = @fromContactId;",
            new { fromContactId }, transaction);

        connection.Execute(
            "UPDATE call SET contact_id = @intoContactId WHERE contact_id = @fromContactId;",
            new { intoContactId, fromContactId }, transaction);

        foreach (var table in LedgerTables)
        {
            connection.Execute(
                $"UPDATE {table} SET contact_id = @intoContactId WHERE contact_id = @fromContactId;",
                new { intoContactId, fromContactId }, transaction);
        }

        connection.Execute(
            "UPDATE message SET contact_id = @intoContactId WHERE contact_id = @fromContactId;",
            new { intoContactId, fromContactId }, transaction);

        // A title can only be bound to one contact, and (title_pattern, app) is unique. Where both
        // contacts learned the same pattern, the destination's binding is the one that survives.
        connection.Execute(
            """
            DELETE FROM title_binding
            WHERE contact_id = @fromContactId
              AND EXISTS (
                  SELECT 1 FROM title_binding other
                  WHERE other.contact_id    = @intoContactId
                    AND other.title_pattern = title_binding.title_pattern
                    AND other.app           = title_binding.app);
            """,
            new { fromContactId, intoContactId }, transaction);

        connection.Execute(
            "UPDATE title_binding SET contact_id = @intoContactId WHERE contact_id = @fromContactId;",
            new { intoContactId, fromContactId }, transaction);

        // A merge can clear the distrust it caused.
        //
        // A title is marked unreliable when it is claimed by two contacts, because that normally
        // proves the title identifies nobody. There is one innocent way to reach that state: the
        // same person entered twice under two spellings, each learning the title honestly. The
        // merge is the evidence that they were one person all along, so the contradiction is
        // gone and the binding is worth trusting again.
        //
        // Only for the surviving contact's own patterns, and only where no other contact still
        // claims them — a title genuinely shared by two different people stays distrusted.
        connection.Execute(
            """
            UPDATE title_binding
               SET unreliable = 0
             WHERE contact_id = @intoContactId
               AND NOT EXISTS (
                   SELECT 1 FROM call
                    WHERE call.observed_title = title_binding.title_pattern
                      AND call.contact_id IS NOT NULL
                      AND call.contact_id <> @intoContactId);
            """,
            new { intoContactId }, transaction);

        // Profile facts follow the person. Fields simply move; for the profile row the
        // destination's entries win where both wrote one — a merge must never overwrite what the
        // user typed on the contact they are keeping.
        connection.Execute(
            "UPDATE contact_field SET contact_id = @intoContactId WHERE contact_id = @fromContactId;",
            new { intoContactId, fromContactId }, transaction);

        connection.Execute(
            """
            INSERT INTO contact_profile (contact_id, photo_file, birth_date, circle_folded, updated_at)
            SELECT @intoContactId, photo_file, birth_date, circle_folded, updated_at
            FROM contact_profile WHERE contact_id = @fromContactId
            ON CONFLICT(contact_id) DO UPDATE SET
                photo_file = COALESCE(contact_profile.photo_file, excluded.photo_file),
                birth_date = COALESCE(contact_profile.birth_date, excluded.birth_date),
                -- The circle follows the person too. Two people turning out to be one keeps the
                -- circle the surviving contact was already in; the other's is used only where
                -- nobody had said anything, which is a move into an empty place rather than a
                -- decision taken on the user's behalf.
                circle_folded = COALESCE(contact_profile.circle_folded, excluded.circle_folded);
            """,
            new { intoContactId, fromContactId }, transaction);

        // The model's readings follow the person as well, and the NEWEST of the combined history
        // is the one the card then shows — which is what "newest wins" means for a table that is
        // a history rather than a row per person.
        //
        // Nothing is deleted here on purpose. Each of these rows is a paid request and, more to
        // the point, may carry a [Katılmıyorum] the user pressed; throwing the older ones away
        // during an operation whose whole purpose is to lose nothing would also quietly rewrite
        // the measurement that decides whether this feature stays switched on.
        connection.Execute(
            "UPDATE contact_reading SET contact_id = @intoContactId WHERE contact_id = @fromContactId;",
            new { intoContactId, fromContactId }, transaction);

        // Deleted last, once nothing points at it. ON DELETE CASCADE would otherwise take the
        // rows that were just moved.
        connection.Execute("DELETE FROM contact WHERE id = @fromContactId;", new { fromContactId }, transaction);

        Recount(connection, transaction, intoContactId);

        transaction.Commit();

        return moved;
    }

    /// <summary>
    /// Changes a contact's name in place.
    ///
    /// Needed as its own operation because <see cref="UpsertContact"/> matches on the normalised
    /// name: passing a new one there creates a second person rather than renaming the first, which
    /// is the opposite of what somebody correcting a spelling wants.
    /// </summary>
    /// <returns>False when another contact of the same application already holds that name.</returns>
    public bool RenameContact(long contactId, string name)
    {
        var trimmed = TurkishText.StripFormatting(name);
        if (trimmed.Length == 0) return false;

        using var connection = Open();

        var app = connection.QueryFirstOrDefault<int?>(
            "SELECT app FROM contact WHERE id = @contactId;", new { contactId });

        if (app is null) return false;

        // The same folding UpsertContact uses, so "Işık" and "isik" are recognised as the same
        // person here too. Renaming into a name that already exists must be caught by the same
        // rule that would have matched them on the way in.
        var normalised = TurkishText.NormalizeForSearch(trimmed);

        var taken = connection.ExecuteScalar<long?>(
            """
            SELECT id FROM contact
            WHERE name_normalised = @normalised AND app = @app AND id <> @contactId
            LIMIT 1;
            """,
            new { normalised, app, contactId });

        // Refused rather than silently merged. Two people with one name is a decision the user has
        // to make, and merging is available for when that is what they meant.
        if (taken is not null) return false;

        connection.Execute(
            "UPDATE contact SET name = @trimmed, name_normalised = @normalised WHERE id = @contactId;",
            new { trimmed, normalised, contactId });

        return true;
    }

    public Call? GetCall(long id)
    {
        using var connection = Open();
        return connection.QueryFirstOrDefault<CallRow>("SELECT * FROM call WHERE id = @id;", new { id })?.ToModel();
    }

    /// <summary>
    /// The newest calls, optionally one person's and optionally one circle's.
    ///
    /// The circle goes into the SQL rather than being applied to what comes back, and that is the
    /// whole reason this parameter exists. The first screen asks for twelve rows: THE CUT HAPPENS
    /// BEFORE ANY FILTER. Narrowing those twelve in memory would show two rows on the "Aile" tab
    /// of an archive holding forty-one family conversations, and the honest reading of that screen
    /// is "my family calls are gone". Each tab therefore asks the database for its own newest
    /// twelve.
    ///
    /// A circle nobody has defined any more does not narrow anything, and the people in it fall
    /// back into "Çevresiz" — see <see cref="CallCountsByCircle"/> for why that is the only
    /// arrangement in which every conversation is reachable from some tab.
    /// </summary>
    public IReadOnlyList<Call> ListCalls(long? contactId = null, int limit = 200, CircleFilter? circle = null)
    {
        using var connection = Open();

        var conditions = new List<string>();

        if (contactId is not null) conditions.Add("c.contact_id = @contactId");

        if (circle is { Without: true })
        {
            // No profile row, no circle written, or a circle whose definition is gone. All three
            // are the same thing on screen: this person is in no circle the user can point at.
            // An unnamed recording has no person at all and lands here too, which is right — it
            // belongs to nobody, and the tab that can never be removed is where it must be found.
            conditions.Add(
                """
                NOT EXISTS (
                    SELECT 1 FROM contact_profile p
                      JOIN contact_circle d ON d.circle_folded = p.circle_folded
                     WHERE p.contact_id = c.contact_id)
                """);
        }
        else if (circle?.Folded is { } folded)
        {
            conditions.Add(
                """
                EXISTS (
                    SELECT 1 FROM contact_profile p
                      JOIN contact_circle d ON d.circle_folded = p.circle_folded
                     WHERE p.contact_id = c.contact_id AND d.circle_folded = @folded)
                """);
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions) + " ";

        var sql = $"SELECT c.* FROM call c {where}ORDER BY c.started_at DESC LIMIT @limit;";

        return
        [
            .. connection
                .Query<CallRow>(sql, new { contactId, limit, folded = circle?.Folded })
                .Select(r => r.ToModel()),
        ];
    }

    /// <summary>
    /// Recordings that still need work, including ones a crash left mid-flight.
    ///
    /// States 2 and 4 — Transcribing and Analysing — mean "a worker is busy with this", and after
    /// a crash or a power cut that is no longer true of anybody. Nothing requeued them, so the
    /// recording sat there for ever while every screen showed it as work in progress: a spinner
    /// that would never stop, on a conversation that was never going to be transcribed.
    ///
    /// Safe to reclaim precisely because this is only ever called at startup: the process that
    /// might have been holding them is the one that just died.
    /// </summary>
    // ---- voiceprints ------------------------------------------------------------

    /// <summary>
    /// Every voice the application knows, for matching one recording against all of them.
    ///
    /// Read whole rather than one at a time because that is the actual question — "who is this"
    /// is asked against everybody at once, and a dot product over a few hundred contacts is
    /// cheaper than a few hundred round trips to SQLite.
    /// </summary>
    public IReadOnlyList<(Voiceprint Print, string Name)> Voiceprints(string model)
    {
        using var connection = Open();

        var rows = connection.Query<VoiceRow>(
            """
            SELECT v.contact_id AS ContactId, v.vector AS Vector, v.model AS Model,
                   v.calls_used AS CallsUsed, v.speech_seconds AS SpeechSeconds,
                   v.updated_at AS UpdatedAt, c.name AS Name
            FROM contact_voice v
            JOIN contact c ON c.id = v.contact_id
            WHERE v.model = @model;
            """,
            new { model });

        return [.. rows.Select(r => (r.ToModel(), r.Name ?? ""))];
    }

    public Voiceprint? GetVoiceprint(long contactId)
    {
        using var connection = Open();

        return connection.QueryFirstOrDefault<VoiceRow>(
            """
            SELECT contact_id AS ContactId, vector AS Vector, model AS Model,
                   calls_used AS CallsUsed, speech_seconds AS SpeechSeconds, updated_at AS UpdatedAt
            FROM contact_voice WHERE contact_id = @contactId;
            """,
            new { contactId })?.ToModel();
    }

    public void SaveVoiceprint(Voiceprint print)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO contact_voice (contact_id, vector, model, calls_used, speech_seconds, updated_at)
            VALUES (@contactId, @vector, @model, @callsUsed, @speechSeconds, @updatedAt)
            ON CONFLICT(contact_id) DO UPDATE SET
                vector         = excluded.vector,
                model          = excluded.model,
                calls_used     = excluded.calls_used,
                speech_seconds = excluded.speech_seconds,
                updated_at     = excluded.updated_at;
            """,
            new
            {
                contactId = print.ContactId,
                vector = JsonSerializer.Serialize(print.Vector),
                model = print.Model,
                callsUsed = print.CallsUsed,
                speechSeconds = print.SpeechSeconds,
                updatedAt = Iso(print.UpdatedAt),
            });
    }

    /// <summary>Forgets one voice — used when a contact's calls turn out not to agree with each other.</summary>
    public void DeleteVoiceprint(long contactId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM contact_voice WHERE contact_id = @contactId;", new { contactId });
    }

    /// <summary>
    /// Forgets every voice. The undo for a feature that collects biometric data, and the settings
    /// screen offers it beside the switch that turns collection on.
    /// </summary>
    public int DeleteAllVoiceprints()
    {
        using var connection = Open();
        return connection.Execute("DELETE FROM contact_voice;");
    }

    /// <summary>
    /// The recordings a person's voiceprint may be built from: their own calls, filed by the user,
    /// newest first.
    ///
    /// <b>Only <see cref="ContactSource.User"/>.</b> A call the voice itself filed must never
    /// enrol a voice, or one wrong match becomes the evidence for the next one. This project has
    /// already run that loop once — the vocabulary miner read its own bad output back in and got
    /// worse every round — and the fix there was the same fix as here: the machine's own output is
    /// not evidence about the machine.
    /// </summary>
    public IReadOnlyList<(long CallId, string FarPath)> VoiceEnrolmentCalls(long contactId, int limit = 5)
    {
        using var connection = Open();

        return
        [
            .. connection.Query<(long, string)>(
                """
                SELECT id, far_path
                FROM call
                WHERE contact_id = @contactId
                  AND far_path IS NOT NULL AND far_path <> ''
                  AND COALESCE(contact_source, 'user') = 'user'
                ORDER BY started_at DESC
                LIMIT @limit;
                """,
                new { contactId, limit }),
        ];
    }

    /// <summary>Contacts with at least one call the voice could be learned from.</summary>
    public IReadOnlyList<long> ContactsWorthEnrolling()
    {
        using var connection = Open();

        return
        [
            .. connection.Query<long>(
                """
                SELECT DISTINCT contact_id
                FROM call
                WHERE contact_id IS NOT NULL
                  AND far_path IS NOT NULL AND far_path <> ''
                  AND COALESCE(contact_source, 'user') = 'user'
                  AND duration_ms > 30000;
                """),
        ];
    }

    private sealed class VoiceRow
    {
        public long ContactId { get; set; }
        public string Vector { get; set; } = "[]";
        public string Model { get; set; } = "";
        public int CallsUsed { get; set; }
        public double SpeechSeconds { get; set; }
        public string UpdatedAt { get; set; } = "";
        public string? Name { get; set; }

        public Voiceprint ToModel() => new()
        {
            ContactId = ContactId,
            Vector = JsonSerializer.Deserialize<float[]>(Vector) ?? [],
            Model = Model,
            CallsUsed = CallsUsed,
            SpeechSeconds = SpeechSeconds,
            UpdatedAt = ParseIso(UpdatedAt),
        };
    }

    // ---- to-do ------------------------------------------------------------------

    public long AddTodo(string text, DateOnly? due, long? contactId = null, long? callId = null)
    {
        using var connection = Open();

        return connection.ExecuteScalar<long>(
            """
            INSERT INTO todo (text, due_date, contact_id, call_id, created_at)
            VALUES (@text, @due, @contactId, @callId, @createdAt);
            SELECT last_insert_rowid();
            """,
            new
            {
                text = text.Trim(),
                due = due?.ToString("yyyy-MM-dd"),
                contactId,
                callId,
                createdAt = DateTimeOffset.Now.ToString("o"),
            });
    }

    /// <summary>The user's own list, open items first by due date; done ones only when asked.</summary>
    public IReadOnlyList<Todo> ListTodos(bool includeDone = false)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<(long Id, string Text, string? Due, string? DoneAt, long? ContactId, string? Name, long? CallId, string CreatedAt)>(
                    """
                    SELECT t.id, t.text, t.due_date, t.done_at, t.contact_id, ct.name, t.call_id, t.created_at
                    FROM todo t
                    LEFT JOIN contact ct ON ct.id = t.contact_id
                    WHERE @includeDone = 1 OR t.done_at IS NULL
                    ORDER BY t.done_at IS NOT NULL, t.due_date IS NULL, t.due_date, t.created_at;
                    """,
                    new { includeDone = includeDone ? 1 : 0 })
                .Select(r => new Todo
                {
                    Id = r.Id,
                    Text = r.Text,
                    DueDate = r.Due is null ? null : DateOnly.Parse(r.Due),
                    DoneAt = r.DoneAt is null ? null : DateTimeOffset.Parse(r.DoneAt),
                    ContactId = r.ContactId,
                    ContactName = r.Name,
                    CallId = r.CallId,
                    CreatedAt = DateTimeOffset.Parse(r.CreatedAt),
                }),
        ];
    }

    public void SetTodoDone(long todoId, bool done)
    {
        using var connection = Open();

        connection.Execute(
            "UPDATE todo SET done_at = @doneAt WHERE id = @todoId;",
            new { todoId, doneAt = done ? DateTimeOffset.Now.ToString("o") : null });
    }

    public void DeleteTodo(long todoId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM todo WHERE id = @todoId;", new { todoId });
    }

    /// <summary>Every suggested step still open, across all calls — the to-do page's second source.</summary>
    /// <summary>
    /// The suggestions the user has ticked off, most recent deadline first.
    ///
    /// Needed because "Yaptım" used to be a disappearance. The suggestion left the first screen,
    /// left the to-do list, and turned up nowhere — not even under "Bitenler", which is the one
    /// place somebody looks to check whether they really did the thing. A list that can only
    /// lose items teaches people not to tick anything.
    /// </summary>
    public IReadOnlyList<(ActionItem Action, string ContactName)> AllDoneActions()
    {
        using var connection = Open();

        var rows = connection.Query<ActionRow, string?, (ActionRow, string?)>(
            """
            SELECT a.*, ct.name
            FROM action_item a
            JOIN call c          ON c.id = a.call_id
            LEFT JOIN contact ct ON ct.id = a.contact_id
            WHERE a.status = 1
            ORDER BY a.deadline_date IS NULL, a.deadline_date DESC, c.started_at DESC;
            """,
            (action, name) => (action, name),
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "İsimsiz"))];
    }

    public IReadOnlyList<(ActionItem Action, string ContactName)> AllOpenActions()
    {
        using var connection = Open();

        var rows = connection.Query<ActionRow, string?, (ActionRow, string?)>(
            """
            SELECT a.*, ct.name
            FROM action_item a
            JOIN call c          ON c.id = a.call_id
            LEFT JOIN contact ct ON ct.id = a.contact_id
            WHERE a.status = 0
            ORDER BY a.deadline_date IS NULL, a.deadline_date, c.started_at DESC;
            """,
            (action, name) => (action, name),
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "İsimsiz"))];
    }

    /// <summary>Every call that still has audio on the row, for the checks that read the files.</summary>
    public IReadOnlyList<Call> CallsWithAudio()
    {
        using var connection = Open();
        return [.. connection.Query<CallRow>(
            "SELECT * FROM call WHERE mic_path IS NOT NULL OR far_path IS NOT NULL ORDER BY id;")
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// Corrects how long a recording is said to be.
    ///
    /// Separate from <see cref="CompleteCall"/> because this is not the recorder speaking: it is
    /// a repair, made when the number on the row turns out not to describe the audio the row
    /// points at.
    /// </summary>
    public void SetDuration(long callId, TimeSpan duration)
    {
        using var connection = Open();

        connection.Execute(
            "UPDATE call SET duration_ms = @durationMs WHERE id = @callId;",
            new { callId, durationMs = (long)duration.TotalMilliseconds });
    }

    /// <summary>Points a call at new audio files — after compression, when the bytes moved but nothing else did.</summary>
    public void SetAudioPaths(long callId, string? micPath, string? farPath)
    {
        using var connection = Open();

        connection.Execute(
            "UPDATE call SET mic_path = @micPath, far_path = @farPath WHERE id = @callId;",
            new { callId, micPath, farPath });
    }

    /// <summary>
    /// Re-roots recording paths written on another machine onto this one.
    ///
    /// The paths in the archive are absolute, and a backup carries them exactly as they were
    /// written. Restoring onto a different computer — a new laptop, a rebuilt one, the same
    /// person under a different Windows account — is the case the whole backup feature exists
    /// for, and it is the case that failed: the audio is unpacked into the right folder, but
    /// every row still points at C:\Users\{somebody else}\…, so the application says the
    /// recording is gone. Not for one call — for the whole archive at once, which is exactly the
    /// moment somebody has nothing else left.
    ///
    /// Rows already under <paramref name="recordingsRoot"/> are skipped without touching the
    /// disk, so an ordinary start pays nothing at all. A row is rewritten only when the rebased
    /// file is really there: audio the retention sweep deleted stays deleted, and a path is
    /// never invented for something that is not on this machine.
    /// </summary>
    /// <returns>How many calls were pointed back at their audio.</returns>
    public int RebaseRecordingPaths(string recordingsRoot)
    {
        var root = Path.GetFullPath(recordingsRoot).TrimEnd(Path.DirectorySeparatorChar);

        using var connection = Open();

        var rows = connection.Query<CallRow>(
            "SELECT id, mic_path, far_path FROM call WHERE mic_path IS NOT NULL OR far_path IS NOT NULL;");

        List<(long Id, string? Mic, string? Far)> repaired = [];

        foreach (var row in rows)
        {
            var mic = RebaseRecordingPath(row.mic_path, root);
            var far = RebaseRecordingPath(row.far_path, root);

            if (mic is null && far is null) continue;

            repaired.Add((row.id, mic ?? row.mic_path, far ?? row.far_path));
        }

        if (repaired.Count == 0) return 0;

        using var transaction = connection.BeginTransaction();

        foreach (var (id, mic, far) in repaired)
        {
            connection.Execute(
                "UPDATE call SET mic_path = @mic, far_path = @far WHERE id = @id;",
                new { id, mic, far },
                transaction);
        }

        transaction.Commit();

        CoreLog.Write("veri", $"{repaired.Count} gorusmenin ses yolu bu makineye gore yeniden koklendi");

        return repaired.Count;
    }

    /// <summary>
    /// The same recording under <paramref name="root"/>, or null when the stored path needs no
    /// change — either it is already ours, or the file is genuinely not on this disk.
    /// </summary>
    internal static string? RebaseRecordingPath(string? stored, string root)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        // Already ours. Whether the file is still there is a different question and not this
        // one's business: audio removed by the retention sweep is supposed to be missing.
        if (stored.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = stored.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        // Everything below the archive's own recordings folder — normally 2026-08\call-5-mic.ogg,
        // so the month grouping survives. A path from somewhere else keeps only its file name.
        var start = Array.FindLastIndex(
            segments, s => s.Equals("recordings", StringComparison.OrdinalIgnoreCase)) + 1;

        var tail = start > 0 && start < segments.Length ? segments[start..] : [segments[^1]];

        var candidate = Path.Combine(root, string.Join(Path.DirectorySeparatorChar, tail));

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Finished calls whose audio is still PCM on disk.
    ///
    /// Finished means the words are already in the archive — a call with no transcript keeps its
    /// original, because for that call the audio is the whole record and a codec, however good,
    /// is not something to put between a person and the only copy. Calls the processor is busy
    /// with, or about to be, are left alone as well; they are read from while they are worked on.
    /// </summary>
    public IReadOnlyList<Call> CallsWithUncompressedAudio()
    {
        using var connection = Open();
        return [.. connection.Query<CallRow>(
            """
            SELECT c.* FROM call c
            WHERE c.state NOT IN (0, 1, 2, 4)
              AND (c.mic_path LIKE '%.wav' OR c.far_path LIKE '%.wav')
              AND EXISTS (SELECT 1 FROM segment s WHERE s.call_id = c.id)
            ORDER BY c.started_at ASC;
            """)
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// Calls that were being recorded when the process died: still marked as fresh recordings,
    /// with no audio attached, because the paths are only written when a recording ends properly.
    /// </summary>
    public IReadOnlyList<Call> CallsWithoutAudio()
    {
        using var connection = Open();
        return [.. connection.Query<CallRow>(
            "SELECT * FROM call WHERE state IN (0, 1) AND mic_path IS NULL AND far_path IS NULL ORDER BY started_at ASC;")
            .Select(r => r.ToModel())];
    }

    public IReadOnlyList<Call> CallsAwaitingProcessing()
    {
        using var connection = Open();

        connection.Execute(
            """
            UPDATE call
               SET state = 1
             WHERE state IN (2, 4);
            """);

        return [.. connection.Query<CallRow>(
            "SELECT * FROM call WHERE state IN (0, 1) ORDER BY started_at ASC;")
            .Select(r => r.ToModel())];
    }

    // ---- merging another archive into this one ------------------------------

    /// <summary>Column names a table has in one of the attached databases.</summary>
    private sealed class ColumnRow
    {
        public string name { get; set; } = "";
    }

    /// <summary>
    /// Adds everything from another archive to this one, keeping what is already here.
    ///
    /// The difference from a restore is the whole point. A restore answers "the laptop died":
    /// it replaces the archive wholesale, which is why it has to wait for a restart and why it
    /// moves the current data aside first. That is the wrong operation for the far more common
    /// case — the same person, two machines, or a backup from last month next to three weeks of
    /// newer conversations — where replacing means deliberately discarding one of the two halves.
    ///
    /// So this merges, in one transaction, and nothing is overwritten:
    ///
    ///   * A contact already here, matched on the folded name and the app, keeps their row and
    ///     collects the incoming calls. Otherwise the contact is created.
    ///   * A call is the same call when it started at the same instant in the same app. One that
    ///     is already here is left completely alone — its transcript, its ledger and its notes
    ///     stay as they are, and the incoming copy is dropped rather than merged row by row,
    ///     because two transcripts of one conversation interleaved is not a better archive.
    ///   * Everything hanging off a genuinely new call — segments, ledger, suggestions, notes,
    ///     tags, runs — comes with it, with the identifiers rewritten.
    ///   * The DECISIONS on a call that is already here cross too, one at a time, under the rule
    ///     "writing into an empty place is a move, not a merge". That is <see cref="DecisionMerge"/>
    ///     and it is the half that used to be missing entirely: the promise rulings, notes, tags
    ///     and ear verdicts made on the other computer never reached a shared conversation, which
    ///     on a two-machine archive is most of them. Nothing is overwritten there either — where
    ///     both machines decided differently the local value stands and the incoming one becomes
    ///     a row in <c>import_leftover</c>, so no decision is dropped in silence.
    ///
    /// The columns are read from both databases and intersected at run time rather than listed
    /// here. An archive written by an older build is missing columns this one has; one written by
    /// a newer build has columns this one has never heard of. Naming them by hand would mean an
    /// import that throws on the first schema change nobody remembered to come back and update.
    ///
    /// Settings are deliberately not merged. They carry this machine's audio devices and this
    /// machine's API keys, and importing somebody's conversations must not quietly repoint the
    /// recorder or replace a working key.
    ///
    /// Three tables are deliberately absent from every copy below, and their absence is load
    /// bearing. <c>archive_identity</c> is who THIS computer is; copied, two archives would claim
    /// the same name and neither could say where a backup came from. <c>archive_link</c> is who
    /// this computer has heard from directly; copied, it would let the archive claim to have met
    /// a machine it has never seen. <c>import_leftover</c> is this computer's own queue of
    /// unanswered questions; copied, the other machine's would arrive as if they were the user's
    /// to answer here, twice.
    /// </summary>
    /// <param name="importedDatabaseFile">
    /// The archive's database, already brought up to the current schema by the caller.
    /// </param>
    /// <param name="carryDecisions">
    /// The rollback switch §7.3 asks for. False stops the rulings on shared conversations being
    /// applied — and does NOT stop them being counted and listed, so turning the feature off
    /// degrades it to "you decide" rather than back to the silent loss it replaced.
    /// </param>
    /// <param name="sourceArchiveId">
    /// Which archive this file came from, when it said so. Written onto the leftovers so a screen
    /// can name the other machine; null when the backup carried no manifest.
    /// </param>
    public MergeCounts MergeArchive(
        string importedDatabaseFile,
        bool carryDecisions = true,
        string? sourceArchiveId = null)
    {
        using var connection = Open();

        // ATTACH cannot run inside a transaction, so the two are ordered rather than nested.
        connection.Execute("ATTACH DATABASE @path AS gelen;", new { path = importedDatabaseFile });

        try
        {
            using var transaction = connection.BeginTransaction();

            connection.Execute(
                """
                CREATE TEMP TABLE map_contact (old INTEGER PRIMARY KEY, new INTEGER NOT NULL);
                CREATE TEMP TABLE map_call    (old INTEGER PRIMARY KEY, new INTEGER NOT NULL);
                CREATE TEMP TABLE new_call    (old INTEGER PRIMARY KEY);
                CREATE TEMP TABLE new_contact (old INTEGER PRIMARY KEY);
                CREATE TEMP TABLE map_version (old INTEGER PRIMARY KEY, new INTEGER NOT NULL);
                """,
                transaction: transaction);

            // Which calls are new is decided BEFORE anything is inserted. Asked afterwards, every
            // call would look like one that was already here — because it would be.
            connection.Execute(
                """
                INSERT INTO new_call (old)
                SELECT s.id FROM gelen.call s
                WHERE NOT EXISTS (
                    SELECT 1 FROM main.call m
                     WHERE m.started_at = s.started_at AND m.app = s.app);
                """,
                transaction: transaction);

            // The same question for people, and asked for the same reason. It is what separates
            // "this person's card arrived whole" from "two computers each filled in half of it",
            // and it has to be asked before the contact copy below makes every one of them look
            // like somebody who was always here.
            connection.Execute(
                """
                INSERT INTO new_contact (old)
                SELECT s.id FROM gelen.contact s
                WHERE NOT EXISTS (
                    SELECT 1 FROM main.contact m
                     WHERE m.name_normalised = s.name_normalised AND m.app = s.app);
                """,
                transaction: transaction);

            var alreadyHere = connection.ExecuteScalar<int>(
                "SELECT (SELECT COUNT(*) FROM gelen.call) - (SELECT COUNT(*) FROM new_call);",
                transaction: transaction);

            var decisions = new DecisionMerge(
                connection, transaction, sourceArchiveId, DateTimeOffset.UtcNow);

            var contacts = Copy(connection, transaction, "contact", where:
                """
                NOT EXISTS (
                    SELECT 1 FROM main.contact m
                     WHERE m.name_normalised = s.name_normalised AND m.app = s.app)
                """);

            connection.Execute(
                """
                INSERT OR IGNORE INTO map_contact (old, new)
                SELECT s.id, m.id
                  FROM gelen.contact s
                  JOIN main.contact m ON m.name_normalised = s.name_normalised AND m.app = s.app;
                """,
                transaction: transaction);

            // Counted here, before MergeFields below writes anything into a person's card. After
            // it, a field this machine had left blank is no longer blank, and every move would be
            // miscounted as an agreement.
            decisions.NoticePersonCards();

            var toContact = new Dictionary<string, string> { ["contact_id"] = "map_contact" };

            // The call's transcript pointer is a row id in the OTHER database. Copied raw it
            // would point at whatever row happens to have that id here — or at none, which the
            // foreign key refuses, aborting the whole import. So it is remapped through
            // map_version, which is still empty at this point (the transcripts are copied after
            // the calls they belong to) and therefore yields NULL; the pointers are filled in
            // below, once the transcripts are here and the map is built.
            var calls = Copy(
                connection, transaction, "call",
                new Dictionary<string, string>(toContact) { ["transcript_version_id"] = "map_version" },
                "s.id IN (SELECT old FROM new_call)");

            connection.Execute(
                """
                INSERT OR IGNORE INTO map_call (old, new)
                SELECT s.id, m.id
                  FROM gelen.call s
                  JOIN main.call m ON m.started_at = s.started_at AND m.app = s.app;
                """,
                transaction: transaction);

            // Children of the calls that were actually new. A call already here keeps what it has.
            const string ofNewCalls = "s.call_id IN (SELECT old FROM new_call)";

            var toCall = new Dictionary<string, string> { ["call_id"] = "map_call" };

            var toCallAndContact = new Dictionary<string, string>
            {
                ["call_id"] = "map_call",
                ["contact_id"] = "map_contact",
            };

            var segments = Copy(connection, transaction, "segment", toCall, ofNewCalls);

            // Transcripts first, then the map from their old ids to their new ones. A stored
            // transcript has no key of its own beyond (call, engine, moment written), and that is
            // enough: two transcripts of one call by one engine at the same instant do not exist.
            Copy(connection, transaction, "transcript_version", toCall, ofNewCalls);

            connection.Execute(
                """
                INSERT OR IGNORE INTO map_version (old, new)
                SELECT s.id, m.id
                  FROM gelen.transcript_version s
                  JOIN map_call mc ON mc.old = s.call_id
                  JOIN main.transcript_version m
                    ON m.call_id = mc.new AND m.engine = s.engine AND m.created_at = s.created_at;

                UPDATE main.call
                   SET transcript_version_id = (
                       SELECT mv.new
                         FROM map_call mc
                         JOIN gelen.call s ON s.id = mc.old
                         JOIN map_version mv ON mv.old = s.transcript_version_id
                        WHERE mc.new = main.call.id)
                 WHERE transcript_version_id IS NULL
                   AND id IN (SELECT mc.new FROM map_call mc JOIN new_call nc ON nc.old = mc.old);
                """,
                transaction: transaction);

            var toCallAndVersion = new Dictionary<string, string>(toCall) { ["transcript_version_id"] = "map_version" };

            foreach (var table in new[] { "call_summary", "consistency_note", "reading_note", "deception_note", "speech_habit" })
            {
                Copy(connection, transaction, table, toCallAndVersion, ofNewCalls);
            }

            foreach (var table in new[] { "call_note", "call_tag", "board_card", "processing_run", "verdict", "call_intent" })
            {
                Copy(connection, transaction, table, toCall, ofNewCalls);
            }

            Copy(connection, transaction, "claim", toCallAndContact, ofNewCalls);

            Copy(
                connection, transaction, "action_item",
                new Dictionary<string, string>(toCallAndContact) { ["transcript_version_id"] = "map_version" },
                ofNewCalls);

            Copy(
                connection, transaction, "commitment",
                new Dictionary<string, string>(toCallAndContact) { ["fulfilled_by_call_id"] = "map_call" },
                ofNewCalls);

            // Three identifiers, not two: the call, the person, and the transcript the quote was
            // located in. Copied raw, that last one would point at whichever transcript happens
            // to hold the id here — and a finding filed under a stranger's text reads as stale
            // or as current entirely by accident.
            Copy(
                connection, transaction, "flag",
                new Dictionary<string, string>(toCallAndContact)
                {
                    ["counter_call_id"] = "map_call",
                    ["transcript_version_id"] = "map_version",
                },
                ofNewCalls);

            // The contact card's evidence. Both are filed against a person as well as a call, so
            // both go through toCallAndContact — left on the plain call map their contact_id
            // would point at a stranger here, and the card would count one person's sentences on
            // another's. tactic_evidence also remembers which transcript verified its quote.
            Copy(
                connection, transaction, "tactic_evidence",
                new Dictionary<string, string>(toCallAndContact) { ["transcript_version_id"] = "map_version" },
                ofNewCalls);

            Copy(connection, transaction, "speech_act", toCallAndContact, ofNewCalls);

            // Measured from the audio, so they travel with the call that carries it. The events
            // remember which transcript reported them; the measurement remembers which recording
            // it was made from, and that key is a file name and a length — it survives the move
            // because the recording does.
            Copy(connection, transaction, "prosody", toCall, ofNewCalls);

            Copy(
                connection, transaction, "audio_event",
                new Dictionary<string, string>(toCall) { ["transcript_version_id"] = "map_version" },
                ofNewCalls);

            // What the user wrote about a person, field by field rather than row by row.
            //
            // This table is keyed by the contact, so the ordinary INSERT OR IGNORE meant that a
            // person known to both machines kept this one's row and the incoming one was dropped
            // WHOLE — the birthday typed on the other computer, the photo chosen there, and (once
            // circles land in this table) which circle that machine put them in. The user moves
            // archives between two computers, so this was not a theoretical loss; it happened on
            // every import, silently, to every person who exists on both sides.
            //
            // The rule is the one §7.2 of the two-machine plan writes down: WRITING INTO AN EMPTY
            // PLACE IS A MOVE, NOT A MERGE. Nothing here is a judgement about which machine is
            // right, so nothing here needs the user to arbitrate: a field this machine left blank
            // takes the incoming value, and a field this machine has filled keeps it and is never
            // overwritten. Two filled fields that disagree are not reconciled — the local one
            // stands, because a merge that silently replaces what somebody typed is the failure
            // this whole operation exists to avoid.
            //
            // Column by column at run time rather than by name, for the same reason Copy reads
            // its columns: the next schema step adds a field here, and a hand-written list is a
            // field that quietly stops travelling between the two machines.
            MergeFields(connection, transaction, "contact_profile", "contact_id", toContact,
                stamps: ["updated_at"]);

            // The voiceprint is not merged the same way and stays "what is here wins" whole. It
            // is not something the user typed: it is measured from this machine's own recordings,
            // and half of one machine's vector beside half of another's is not a voice.
            Copy(connection, transaction, "contact_voice", toContact);

            // The note about the person, which lives on the contact row itself rather than in the
            // profile table. Same rule, same reason: a contact already here keeps their row, so
            // before this the note written on the other machine never arrived. Filled only where
            // there is nothing here to lose.
            connection.Execute(
                """
                UPDATE main.contact
                   SET notes = (
                       SELECT s.notes
                         FROM gelen.contact s
                         JOIN map_contact mc ON mc.old = s.id
                        WHERE mc.new = main.contact.id
                          AND s.notes IS NOT NULL AND TRIM(s.notes) <> '')
                 WHERE (notes IS NULL OR TRIM(notes) = '')
                   AND EXISTS (
                       SELECT 1
                         FROM gelen.contact s
                         JOIN map_contact mc ON mc.old = s.id
                        WHERE mc.new = main.contact.id
                          AND s.notes IS NOT NULL AND TRIM(s.notes) <> '');
                """,
                transaction: transaction);

            // The model's readings of a person. Filed against the contact, and pointing at the
            // newest call they covered, so both identifiers are rewritten — left on the contact
            // map alone the pointer would name whichever conversation happens to hold that id
            // here. A reading already on this machine wins, matched on the moment it was written:
            // it is the one whose [Katılmıyorum] the user may already have pressed.
            Copy(
                connection, transaction, "contact_reading",
                new Dictionary<string, string>(toContact) { ["latest_call_id"] = "map_call" },
                """
                NOT EXISTS (
                    SELECT 1 FROM main.contact_reading r
                     WHERE r.contact_id = (SELECT new FROM map_contact WHERE old = s.contact_id)
                       AND r.created_at = s.created_at)
                """);

            // The questions and their answers. The only table here whose rows come in two shapes,
            // so it is copied once with a clause that admits both: one asked of a conversation
            // travels with that conversation, one asked of the archive belongs to no call and
            // would be dropped by the ofNewCalls filter every other derived table uses.
            //
            // All three identifiers are rewritten — the call it was asked of, the person it was
            // narrowed to, and the transcript its quotes came from. The archive-wide half is
            // deduplicated on the question and the instant it was asked, which is what makes a
            // second import of the same file add nothing: a call-scoped row cannot collide
            // because its call would not be new.
            Copy(
                connection, transaction, "ask_exchange",
                new Dictionary<string, string>(toCallAndContact) { ["transcript_version_id"] = "map_version" },
                $"""
                 ({ofNewCalls} OR s.call_id IS NULL)
                 AND NOT EXISTS (
                     SELECT 1 FROM main.ask_exchange a
                      WHERE a.asked_at = s.asked_at AND a.question = s.question)
                 """);

            Copy(connection, transaction, "contact_field", toContact, where:
                """
                NOT EXISTS (
                    SELECT 1 FROM main.contact_field f
                     WHERE f.contact_id = (SELECT new FROM map_contact WHERE old = s.contact_id)
                       AND f.label = s.label AND f.value = s.value)
                """);

            Copy(connection, transaction, "title_binding", toContact, where:
                """
                NOT EXISTS (
                    SELECT 1 FROM main.title_binding t
                     WHERE t.title_pattern = s.title_pattern AND t.app = s.app)
                """);

            Copy(connection, transaction, "message", toContact, where:
                """
                NOT EXISTS (
                    SELECT 1 FROM main.message m
                     WHERE m.contact_id = (SELECT new FROM map_contact WHERE old = s.contact_id)
                       AND m.sent_at = s.sent_at AND m.text = s.text)
                """);

            Copy(
                connection, transaction, "todo",
                new Dictionary<string, string> { ["contact_id"] = "map_contact", ["call_id"] = "map_call" },
                """
                NOT EXISTS (
                    SELECT 1 FROM main.todo t
                     WHERE t.text = s.text AND IFNULL(t.due_date, '') = IFNULL(s.due_date, ''))
                """);

            // Tag looks are keyed by the folded tag, so a definition already here is kept.
            Copy(connection, transaction, "tag_def");

            // The habit dictionary likewise: a stem already here, by kind and folded spelling,
            // keeps its endings and its spelling; the archive's other stems are added.
            Copy(connection, transaction, "habit_lexicon");

            // Circles, on exactly the same terms: keyed by the folded word, so a circle defined
            // on both computers keeps this one's colour and the other machine's circles arrive
            // beside it. This is the half of the concept that needs carrying by hand — WHICH
            // circle each person is in rides across inside contact_profile, which MergeFields
            // already merges column by column, reading its columns at run time.
            Copy(connection, transaction, "contact_circle");

            // And now the half the copy above cannot reach: what the user DECIDED on a
            // conversation that exists on both machines. Every rule and every count is in
            // <see cref="DecisionMerge"/>; this is only where it runs, and it runs last because
            // it needs both maps and every row those maps point at.
            decisions.CarryCallDecisions(carryDecisions);
            var written = decisions.Flush();

            var counts = decisions.Counts;

            // §7.3's first measure, as an assertion rather than as a hope.
            //
            // "Kayıp yok: düşen sayısı, uygulanan artı listeye girene eşit — sert değişmez, her
            // zaman." A decision that is neither applied, nor already here, nor in the list has
            // been dropped, and dropping one is the single thing this whole package exists to
            // stop. So the merge is REFUSED rather than committed: the transaction is abandoned
            // untouched, the file the user chose is still on disk, and they can import it again
            // once the fault is fixed. A loud failure is recoverable; a quiet one is not.
            if (!counts.Balances)
            {
                CoreLog.Write("veri",
                    $"ice aktarma geri alindi — karar sayimi tutmadi: gorulen {counts.Seen} != "
                    + $"getirilen {counts.Carried} + ayni {counts.AlreadySame} + listede {counts.Left}");

                throw new InvalidOperationException(
                    "İçe aktarma geri alındı: getirilen kararların sayımı tutmadı, "
                    + $"bu yüzden hiçbir şey yazılmadı ({counts.Seen} karar görüldü, "
                    + $"{counts.Carried + counts.AlreadySame + counts.Left} tanesi yerine kondu). "
                    + "Arşivine dokunulmadı, dosya duruyor.");
            }

            var arrived = connection.Query<ImportedCallRow>(
                """
                SELECT m.id, m.mic_path, m.far_path, m.started_at
                  FROM new_call n
                  JOIN map_call k ON k.old = n.old
                  JOIN main.call m ON m.id = k.new;
                """,
                transaction: transaction).ToList();

            // The counters are denormalised, and every incoming call landed on somebody.
            connection.Execute(
                """
                UPDATE contact SET
                    call_count   = (SELECT COUNT(*)        FROM call WHERE call.contact_id = contact.id),
                    last_call_at = (SELECT MAX(started_at) FROM call WHERE call.contact_id = contact.id);
                """,
                transaction: transaction);

            transaction.Commit();

            CoreLog.Write("veri",
                $"ice aktarma: {calls} gorusme, {contacts} kisi, {segments} satir eklendi; "
                + $"{alreadyHere} gorusme zaten vardi");

            // Its own line, because it answers a different question and one that had no answer at
            // all before: of the decisions made on the conversations both machines already had,
            // how many crossed. "listede" counts what is waiting for the user; "yeni satir" is how
            // many of those were not already waiting from a previous import of the same file.
            CoreLog.Write("veri",
                $"kararlar: {counts.Seen} gorulen · {counts.Carried} getirildi · "
                + $"{counts.AlreadySame} zaten ayni · {counts.Left} listede ({written} yeni satir)"
                + (carryDecisions ? "" : " · karar birlestirme KAPALI"));

            return new MergeCounts(
                contacts, calls, segments, alreadyHere,
                [.. arrived.Select(r => new ImportedCall(
                    r.id, r.mic_path, r.far_path, ParseIso(r.started_at)))],
                counts);
        }
        finally
        {
            // Temp tables belong to the connection, and the connection goes back to the pool.
            // Left behind, the next import would fail on CREATE rather than on anything real.
            connection.Execute(
                """
                DROP TABLE IF EXISTS map_contact;
                DROP TABLE IF EXISTS map_call;
                DROP TABLE IF EXISTS new_call;
                DROP TABLE IF EXISTS new_contact;
                DROP TABLE IF EXISTS map_version;
                """);

            connection.Execute("DETACH DATABASE gelen;");
        }
    }

    private sealed class ImportedCallRow
    {
        public long id { get; set; }
        public string? mic_path { get; set; }
        public string? far_path { get; set; }
        public string started_at { get; set; } = "";
    }

    /// <summary>
    /// Copies one table out of the attached archive, rewriting the identifiers named in
    /// <paramref name="remap"/> and skipping rows the <paramref name="where"/> clause rejects.
    ///
    /// The column list is the intersection of what both databases have, minus the surrogate key,
    /// so an archive from a different version of the application still merges: a column only one
    /// side knows about is left behind instead of failing the import.
    ///
    /// INSERT OR IGNORE throughout, because several of these tables are keyed by something real —
    /// a folded tag, a call, a contact — and for a merge the right answer to a collision is that
    /// what is already here wins. Foreign key violations are NOT ignored by that clause, so a
    /// genuine mapping mistake still stops the transaction instead of quietly dropping rows.
    /// </summary>
    private static int Copy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyDictionary<string, string>? remap = null,
        string? where = null)
    {
        var mine = ColumnsOf(connection, transaction, "main", table);
        var theirs = ColumnsOf(connection, transaction, "gelen", table);

        var shared = mine.Where(theirs.Contains).Where(c => c != "id").ToList();
        if (shared.Count == 0) return 0;

        var values = shared.Select(c =>
            remap is not null && remap.TryGetValue(c, out var map)
                ? $"(SELECT new FROM {map} WHERE old = s.\"{c}\")"
                : $"s.\"{c}\"");

        var sql =
            $"INSERT OR IGNORE INTO main.\"{table}\" ({string.Join(", ", shared.Select(c => $"\"{c}\""))}) "
            + $"SELECT {string.Join(", ", values)} FROM gelen.\"{table}\" s"
            + (where is null ? ";" : $" WHERE {where};");

        return connection.Execute(sql, transaction: transaction);
    }

    /// <summary>
    /// Copies one table whose key is a thing rather than a row — a person — so that a row present
    /// on both machines is combined instead of one of the two being thrown away.
    ///
    /// <see cref="Copy"/> answers a collision with INSERT OR IGNORE, and for the tables it is used
    /// on that is right: they are keyed by a call, a folded tag, a quote, and a collision there
    /// means the same thing is already recorded. It is wrong for a table keyed by the CONTACT,
    /// because the two rows are not two copies of one fact — they are the halves of one person's
    /// card, filled in on two different computers.
    ///
    /// So the row is merged column by column, and the rule is asymmetric on purpose:
    ///
    ///   * blank here → the incoming value is written. Writing into an empty place is a move, not
    ///     a merge: nothing is displaced, so there is nothing to ask the user about.
    ///   * filled here → what is here stays, whatever the other machine says. Two filled fields
    ///     that disagree are a conflict, and quietly picking a winner is how a merge loses data
    ///     that somebody typed. The local value is kept and the difference is not acted on.
    ///
    /// Blank means NULL or empty text, because the two are the same thing on a screen and only
    /// one of them is what a never-filled field actually holds. NULLIF against '' is a no-op on a
    /// numeric column, so the rule reads the same for every column type.
    ///
    /// <paramref name="stamps"/> names the columns that are bookkeeping rather than content —
    /// "when was this row last written" — and they take the later of the two, because after this
    /// statement the row genuinely carries what both machines knew.
    /// </summary>
    /// <returns>How many rows were inserted or merged.</returns>
    private static int MergeFields(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string key,
        IReadOnlyDictionary<string, string> remap,
        IReadOnlyCollection<string> stamps)
    {
        var mine = ColumnsOf(connection, transaction, "main", table);
        var theirs = ColumnsOf(connection, transaction, "gelen", table);

        var shared = mine.Where(theirs.Contains).Where(c => c != "id" || c == key).ToList();
        if (!shared.Contains(key)) return 0;

        string Source(string column) =>
            remap.TryGetValue(column, out var map)
                ? $"(SELECT new FROM {map} WHERE old = s.\"{column}\")"
                : $"s.\"{column}\"";

        var assignments = shared
            .Where(c => c != key)
            .Select(c => stamps.Contains(c)
                ? $"\"{c}\" = MAX(\"{table}\".\"{c}\", excluded.\"{c}\")"
                : $"\"{c}\" = COALESCE(NULLIF(\"{table}\".\"{c}\", ''), excluded.\"{c}\", \"{table}\".\"{c}\")")
            .ToList();

        // Nothing to merge into: the table is a key and nothing else. Then a plain copy is the
        // whole of the operation.
        var update = assignments.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", assignments);

        // A contact that has no mapping would insert a NULL key, and on this table the key is the
        // rowid — SQLite would invent an id and file somebody's birthday against nobody.
        var sql =
            $"INSERT INTO main.\"{table}\" ({string.Join(", ", shared.Select(c => $"\"{c}\""))}) "
            + $"SELECT {string.Join(", ", shared.Select(Source))} FROM gelen.\"{table}\" s "
            + $"WHERE {Source(key)} IS NOT NULL "
            + $"ON CONFLICT(\"{key}\") {update};";

        return connection.Execute(sql, transaction: transaction);
    }

    private static HashSet<string> ColumnsOf(
        SqliteConnection connection, SqliteTransaction transaction, string schema, string table) =>
        [.. connection
            .Query<ColumnRow>($"PRAGMA {schema}.table_info(\"{table}\");", transaction: transaction)
            .Select(r => r.name)];

    // ---- two machines, one person -------------------------------------------

    /// <summary>
    /// This archive's own name, generating it the first time it is asked for.
    ///
    /// Generated here rather than in the migration on purpose. A migration runs against whatever
    /// file it is pointed at, including the COPY an import unpacks and brings up to date — and an
    /// id minted there would make the incoming archive claim to be this computer. This runs only
    /// against the live archive, on start, once.
    ///
    /// INSERT OR IGNORE against a table whose primary key is CHECKed to be 1, so two callers
    /// racing produce one identity rather than two.
    /// </summary>
    public ArchiveIdentity EnsureArchiveIdentity()
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT OR IGNORE INTO archive_identity (id, archive_id, label, created_at)
            VALUES (1, @id, NULL, @now);
            """,
            new { id = Guid.NewGuid().ToString("N"), now = Iso(DateTimeOffset.UtcNow) });

        return ReadIdentity(connection)!;
    }

    /// <summary>This archive's name, or null if it has never been asked for one.</summary>
    public ArchiveIdentity? ArchiveIdentityOrNull()
    {
        using var connection = Open();
        return ReadIdentity(connection);
    }

    private static ArchiveIdentity? ReadIdentity(SqliteConnection connection)
    {
        var row = connection.QueryFirstOrDefault<IdentityRow>(
            "SELECT archive_id, label, created_at FROM archive_identity WHERE id = 1;");

        return row is null ? null : new ArchiveIdentity(row.archive_id, row.label, ParseIso(row.created_at));
    }

    /// <summary>
    /// What the user calls this computer. The only column of the identity they write.
    ///
    /// Blank clears it back to unnamed rather than storing an empty string: "" and "not named"
    /// look the same on a screen and only one of them is what a never-filled field holds.
    ///
    /// Creates the identity if there is none. A bare UPDATE would have silently done nothing on an
    /// archive that had never been backed up — the label typed, accepted, and gone.
    /// </summary>
    public void SetArchiveLabel(string? label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

        EnsureArchiveIdentity();

        using var connection = Open();
        connection.Execute(
            "UPDATE archive_identity SET label = @text WHERE id = 1;", new { text });
    }

    private sealed class IdentityRow
    {
        public string archive_id { get; set; } = "";
        public string? label { get; set; }
        public string created_at { get; set; } = "";
    }

    /// <summary>
    /// Records that this archive heard from another one, now.
    ///
    /// Written only when an import actually succeeded. <paramref name="writtenAt"/> null means the
    /// file carried no manifest, and it stays null: there is no honest date to put there, and a
    /// screen must be able to say "bilinmiyor" rather than name a day this machine invented.
    /// </summary>
    public void RecordArchiveLink(
        string archiveId, string? label, DateTimeOffset? writtenAt, DateTimeOffset importedAt)
    {
        if (string.IsNullOrWhiteSpace(archiveId)) return;

        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO archive_link (archive_id, label, written_at, imported_at)
            VALUES (@archiveId, @label, @writtenAt, @importedAt)
            ON CONFLICT(archive_id) DO UPDATE SET
                -- A machine that has since been given a name should be shown by it; one whose
                -- backup no longer says gives up nothing it already had.
                label       = COALESCE(excluded.label, archive_link.label),
                written_at  = COALESCE(excluded.written_at, archive_link.written_at),
                imported_at = excluded.imported_at;
            """,
            new
            {
                archiveId,
                label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                writtenAt = writtenAt is { } w ? Iso(w) : null,
                importedAt = Iso(importedAt),
            });
    }

    /// <summary>The other archives this one has met, most recently heard from first.</summary>
    public IReadOnlyList<ArchiveLink> ArchiveLinks()
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<LinkRow>(
                    "SELECT archive_id, label, written_at, imported_at FROM archive_link "
                    + "ORDER BY imported_at DESC;")
                .Select(r => new ArchiveLink(
                    r.archive_id, r.label,
                    r.written_at is null ? null : ParseIso(r.written_at),
                    ParseIso(r.imported_at))),
        ];
    }

    private sealed class LinkRow
    {
        public string archive_id { get; set; } = "";
        public string? label { get; set; }
        public string? written_at { get; set; }
        public string imported_at { get; set; } = "";
    }

    /// <summary>
    /// What the imports could not carry, newest first.
    ///
    /// Open rows by default, because that is the list with a question in it. The answered ones are
    /// kept forever — a resolution is what stops the same question being asked on the next round
    /// trip — and are read back only when somebody wants to see what they already decided.
    /// </summary>
    public IReadOnlyList<ImportLeftover> Leftovers(bool includeResolved = false)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<LeftoverRow>(
                    "SELECT id, fingerprint, source_archive_id, kind, call_id, contact_id, "
                    + "field, mine, theirs, quote, noticed_at, resolution, resolved_at "
                    + "FROM import_leftover "
                    + (includeResolved ? "" : "WHERE resolution IS NULL ")
                    + "ORDER BY noticed_at DESC, id DESC;")
                .Select(r => r.ToModel()),
        ];
    }

    /// <summary>How many questions are still waiting. What a badge on the screen reads.</summary>
    public int OpenLeftoverCount()
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM import_leftover WHERE resolution IS NULL;");
    }

    /// <summary>
    /// Closes one question with the user's answer.
    ///
    /// Closing is all this does. APPLYING the answer — writing the other machine's value over the
    /// local one, or keeping both — belongs to whichever screen offered the choice, because only
    /// it knows what "keep both" means for a tag as against a promise. Recording the ruling and
    /// carrying it out are separate on purpose: a row must never end up marked answered because
    /// something failed halfway through acting on it.
    /// </summary>
    /// <returns>False when the row is gone or the resolution is not one of the three.</returns>
    public bool ResolveLeftover(long id, string resolution)
    {
        if (!LeftoverResolutions.IsKnown(resolution)) return false;

        using var connection = Open();

        return connection.Execute(
            "UPDATE import_leftover SET resolution = @resolution, resolved_at = @now WHERE id = @id;",
            new { id, resolution, now = Iso(DateTimeOffset.UtcNow) }) > 0;
    }

    /// <summary>
    /// Asks the question again, because the answer was taken back.
    ///
    /// The counterpart of <see cref="ResolveLeftover"/> and, like it, only bookkeeping: putting
    /// back what the answer wrote belongs to <see cref="LeftoverResolution.Undo"/>. The stamp goes
    /// with the answer — a row that is open again was never decided, and leaving the old date on
    /// it would make the list say somebody ruled on something they are still being asked about.
    /// </summary>
    public bool ReopenLeftover(long id)
    {
        using var connection = Open();

        return connection.Execute(
            "UPDATE import_leftover SET resolution = NULL, resolved_at = NULL WHERE id = @id;",
            new { id }) > 0;
    }

    /// <summary>
    /// Every promise of one conversation, ruled on or not.
    ///
    /// Its own query because the only other way in is the whole ledger, which reads every
    /// commitment in the archive to answer a question about one call — and the caller here is
    /// resolving a leftover, where the point of the read is to find out whether exactly ONE
    /// promise carries the words a ruling arrived under.
    /// </summary>
    public IReadOnlyList<Commitment> CommitmentsOnCall(long callId)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<CommitmentRow>(
                    "SELECT * FROM commitment WHERE call_id = @callId ORDER BY id;",
                    new { callId })
                .Select(r => r.ToModel()),
        ];
    }

    /// <summary>How much this archive holds, for the manifest a backup carries.</summary>
    public (int Calls, int Contacts) ArchiveSize()
    {
        using var connection = Open();

        return (
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM call;"),
            connection.ExecuteScalar<int>("SELECT COUNT(*) FROM contact;"));
    }

    private sealed class LeftoverRow
    {
        public long id { get; set; }
        public string fingerprint { get; set; } = "";
        public string? source_archive_id { get; set; }
        public string kind { get; set; } = "";
        public long? call_id { get; set; }
        public long? contact_id { get; set; }
        public string field { get; set; } = "";
        public string? mine { get; set; }
        public string? theirs { get; set; }
        public string? quote { get; set; }
        public string noticed_at { get; set; } = "";
        public string? resolution { get; set; }
        public string? resolved_at { get; set; }

        public ImportLeftover ToModel() => new(
            id, fingerprint, source_archive_id, kind, call_id, contact_id,
            field, mine, theirs, quote, ParseIso(noticed_at), resolution,
            resolved_at is null ? null : ParseIso(resolved_at));
    }

    // ---- what this call has been transcribed as -----------------------------

    /// <summary>How many transcripts one call keeps before the oldest is let go.</summary>
    private const int KeptTranscripts = 10;

    /// <summary>
    /// Files a transcript under the engine that produced it, beside the ones before it.
    ///
    /// Every run used to overwrite the last, which made the one question worth asking about two
    /// engines — which of them heard this conversation better — answerable only by re-running one
    /// of them by hand and hoping the audio had not changed underneath. It had: a step that
    /// rewrote the recording between two runs made the comparison meaningless without anybody
    /// being able to see that from the transcripts.
    ///
    /// The figures are computed here and stored, because a list of engine names with nothing
    /// beside them is not a comparison.
    /// </summary>
    public long SaveTranscriptVersion(
        long callId, string engine, double? speechCoverage, IReadOnlyList<Segment> segments)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var payload = JsonSerializer.Serialize(segments.Select(s => new StoredLine
        {
            IsMe = s.IsMe,
            StartMs = s.StartMs,
            EndMs = s.EndMs,
            Text = s.Text,
            AvgLogprob = s.AvgLogprob,
            NoSpeechProb = s.NoSpeechProb,
            LowConfidence = s.LowConfidence,
            OverlapsOtherSpeaker = s.OverlapsOtherSpeaker,
            SuspectedEcho = s.SuspectedEcho,
            Words = SegmentWords.Write(s.Words),
        }));

        // The same engine, hearing the same thing it heard before, is not a new transcription.
        //
        // The history exists to answer one question — which engine heard this conversation better
        // — and a second identical row by the same engine answers nothing while pushing a genuine
        // comparison out of the ten the call keeps. It also puts a fresh "yeniden döküldü" date on
        // text that has not moved, which makes every derived note look stale and sends the user
        // to re-run analyses that were already current.
        //
        // The engine is part of the identity, and deliberately so: two engines that agree word for
        // word is the most interesting row the comparison can hold, and folding them together
        // would delete exactly the finding the table was built for.
        var unchanged = connection.ExecuteScalar<long?>(
            """
            SELECT v.id
              FROM transcript_version v
              JOIN call c ON c.transcript_version_id = v.id
             WHERE c.id = @callId AND v.engine = @engine AND v.segments = @segments;
            """,
            new { callId, engine, segments = payload },
            transaction);

        if (unchanged is { } already)
        {
            transaction.Commit();
            return already;
        }

        var id = connection.ExecuteScalar<long>(
            """
            INSERT INTO transcript_version
                (call_id, engine, created_at, speech_coverage,
                 segment_count, word_count, low_confidence, spoken_ms, segments)
            VALUES
                (@callId, @engine, @createdAt, @speechCoverage,
                 @segmentCount, @wordCount, @lowConfidence, @spokenMs, @segments)
            RETURNING id;
            """,
            new
            {
                callId,
                engine,
                createdAt = Iso(DateTimeOffset.UtcNow),
                speechCoverage,
                segmentCount = segments.Count,
                wordCount = segments.Sum(s => s.Words.Count > 0
                    ? s.Words.Count
                    : s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                lowConfidence = segments.Count(s => s.LowConfidence),
                spokenMs = segments.Sum(s => Math.Max(0, s.EndMs - s.StartMs)),
                segments = payload,
            },
            transaction);

        // The call now shows these lines, so it points at this version. Everything that asks
        // "which engine produced the text on screen" reads the pointer rather than guessing from
        // the last run.
        connection.Execute(
            "UPDATE call SET transcript_version_id = @id WHERE id = @callId;",
            new { id, callId }, transaction);

        // Bounded, because these are kept for comparison and not as an archive of their own: the
        // recording is the archive. Ten is more engines than anybody will try on one call.
        //
        // Never the one on screen, whatever its age. The sweep would otherwise be able to delete
        // the transcript the call is currently showing, and the strip would go back to saying
        // nothing about where its own text came from.
        connection.Execute(
            """
            DELETE FROM transcript_version
             WHERE call_id = @callId
               AND id <> @id
               AND id NOT IN (SELECT id FROM transcript_version
                               WHERE call_id = @callId
                               ORDER BY id DESC LIMIT @keep);
            """,
            new { callId, id, keep = KeptTranscripts }, transaction);

        transaction.Commit();
        return id;
    }

    /// <summary>
    /// The stored transcript the call is showing, or null when nothing recorded it.
    ///
    /// Null happens for calls transcribed before the pointer existed, and that is the honest
    /// answer for them: the engine that produced their lines was never written down.
    /// </summary>
    public TranscriptVersion? CurrentTranscriptVersion(long callId)
    {
        using var connection = Open();

        var row = connection.QueryFirstOrDefault<TranscriptRow>(
            """
            SELECT v.id, v.call_id, v.engine, v.created_at, v.speech_coverage,
                   v.segment_count, v.word_count, v.low_confidence, v.spoken_ms
              FROM transcript_version v
              JOIN call c ON c.transcript_version_id = v.id
             WHERE c.id = @callId;
            """,
            new { callId });

        return row is null ? null : row.ToModel(current: true);
    }

    /// <summary>
    /// Every transcript this call has had, newest first, without the lines themselves.
    ///
    /// Which one is current comes from the call's own pointer rather than from position in the
    /// list. It used to be "the newest, by construction", and construction is what made it wrong:
    /// restoring an older transcript had to file a duplicate copy of it to become the newest, so
    /// pressing "use this one" four times left four identical rows in what is supposed to be a
    /// history of transcriptions.
    /// </summary>
    public IReadOnlyList<TranscriptVersion> ListTranscriptVersions(long callId)
    {
        using var connection = Open();

        var current = connection.QueryFirstOrDefault<long?>(
            "SELECT transcript_version_id FROM call WHERE id = @callId;", new { callId });

        var rows = connection.Query<TranscriptRow>(
            """
            SELECT id, call_id, engine, created_at, speech_coverage,
                   segment_count, word_count, low_confidence, spoken_ms
              FROM transcript_version
             WHERE call_id = @callId
             ORDER BY id DESC;
            """,
            new { callId }).ToList();

        // No pointer means a call from before it existed: the newest is the best guess available,
        // and it was right for every call that never had a transcript restored.
        return [.. rows.Select((r, i) => r.ToModel(
            current: current is { } id ? r.id == id : i == 0))];
    }

    /// <summary>The lines of one stored transcript, or an empty list when it is gone.</summary>
    public IReadOnlyList<Segment> GetTranscriptVersion(long versionId)
    {
        using var connection = Open();

        var row = connection.QueryFirstOrDefault<(long CallId, string Segments)>(
            "SELECT call_id, segments FROM transcript_version WHERE id = @versionId;",
            new { versionId });

        if (row.Segments is null) return [];

        var stored = JsonSerializer.Deserialize<List<StoredLine>>(row.Segments) ?? [];

        return [.. stored.Select(s => new Segment
        {
            CallId = row.CallId,
            IsMe = s.IsMe,
            StartMs = s.StartMs,
            EndMs = s.EndMs,
            Text = s.Text,
            AvgLogprob = s.AvgLogprob,
            NoSpeechProb = s.NoSpeechProb,
            LowConfidence = s.LowConfidence,
            OverlapsOtherSpeaker = s.OverlapsOtherSpeaker,
            SuspectedEcho = s.SuspectedEcho,
            Words = SegmentWords.Read(s.Words),
        })];
    }

    /// <summary>
    /// Puts a stored transcript back as the call's own.
    ///
    /// It used to file a second copy of itself so that "newest" would mean "current". That was a
    /// workaround for the call not recording which transcript it was showing, and it cost the
    /// thing the list exists for: four presses of "use this one" left four identical rows and
    /// evicted real transcriptions from a history capped at ten. The call points at a version
    /// now, so restoring moves the pointer and writes nothing.
    ///
    /// That a restore happened is still worth knowing, and it goes in the log — a list of
    /// transcripts is the wrong place to record a reading decision.
    ///
    /// The ledger is NOT rebuilt: it quotes the transcript it was made from, and silently
    /// repointing quotes at different words is the one thing this product must not do. The
    /// caller says so; see the window that offers this.
    /// </summary>
    public bool RestoreTranscriptVersion(long versionId)
    {
        var lines = GetTranscriptVersion(versionId);
        if (lines.Count == 0) return false;

        using var connection = Open();

        var row = connection.QueryFirstOrDefault<(long CallId, string Engine)>(
            "SELECT call_id, engine FROM transcript_version WHERE id = @versionId;",
            new { versionId });

        if (row.Engine is null) return false;

        ReplaceSegments(row.CallId, lines);

        connection.Execute(
            "UPDATE call SET transcript_version_id = @versionId WHERE id = @callId;",
            new { versionId, callId = row.CallId });

        return true;
    }

    /// <summary>One line as it is stored inside a version. Short names: this is written per line.</summary>
    private sealed class StoredLine
    {
        public bool IsMe { get; set; }
        public int StartMs { get; set; }
        public int EndMs { get; set; }
        public string Text { get; set; } = "";
        public double? AvgLogprob { get; set; }
        public double? NoSpeechProb { get; set; }
        public bool LowConfidence { get; set; }
        public bool OverlapsOtherSpeaker { get; set; }
        public bool SuspectedEcho { get; set; }
        public string? Words { get; set; }
    }

    private sealed class TranscriptRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public string engine { get; set; } = "";
        public string created_at { get; set; } = "";
        public double? speech_coverage { get; set; }
        public long segment_count { get; set; }
        public long word_count { get; set; }
        public long low_confidence { get; set; }
        public long spoken_ms { get; set; }

        public TranscriptVersion ToModel(bool current) => new()
        {
            Id = id,
            CallId = call_id,
            Engine = engine,
            CreatedAt = ParseIso(created_at),
            SpeechCoverage = speech_coverage,
            SegmentCount = (int)segment_count,
            WordCount = (int)word_count,
            LowConfidenceCount = (int)low_confidence,
            SpokenMs = (int)spoken_ms,
            IsCurrent = current,
        };
    }

    // ---- segments -----------------------------------------------------------

    public void ReplaceSegments(long callId, IEnumerable<Segment> segments)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute("DELETE FROM segment WHERE call_id = @callId;", new { callId }, transaction);

        foreach (var segment in segments)
        {
            connection.Execute(
                """
                INSERT INTO segment (call_id, is_me, start_ms, end_ms, text, text_normalised,
                                     avg_logprob, no_speech_prob, low_confidence,
                                     overlaps_other_speaker, suspected_echo, words)
                VALUES (@callId, @isMe, @startMs, @endMs, @text, @normalised,
                        @avgLogprob, @noSpeechProb, @lowConfidence, @overlaps, @echo, @words);
                """,
                new
                {
                    callId,
                    isMe = segment.IsMe ? 1 : 0,
                    startMs = segment.StartMs,
                    endMs = segment.EndMs,
                    text = segment.Text,
                    // Filled here so the index can never disagree with the visible text.
                    normalised = TurkishText.NormalizeForSearch(segment.Text),
                    avgLogprob = segment.AvgLogprob,
                    noSpeechProb = segment.NoSpeechProb,
                    lowConfidence = segment.LowConfidence ? 1 : 0,
                    overlaps = segment.OverlapsOtherSpeaker ? 1 : 0,
                    echo = segment.SuspectedEcho ? 1 : 0,
                    words = SegmentWords.Write(segment.Words),
                },
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>
    /// How many transcript lines a call has.
    ///
    /// Counted rather than loaded, because the processing screen asks this for every call in the
    /// archive at once and reading the text of all of them to find out whether there is any would
    /// be several megabytes to answer a yes-or-no question.
    ///
    /// It also answers that question better than the state field does. A call can be marked Failed
    /// and still have a full transcript — the transcription succeeded and the analysis afterwards
    /// did not — and telling the user "başarısız" about a conversation they can already read is
    /// both wrong and alarming.
    /// </summary>
    public int CountSegments(long callId)
    {
        using var connection = Open();

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM segment WHERE call_id = @callId;", new { callId });
    }

    public IReadOnlyList<Segment> GetSegments(long callId)
    {
        using var connection = Open();
        return [.. connection.Query<SegmentRow>(
            "SELECT * FROM segment WHERE call_id = @callId ORDER BY start_ms;", new { callId })
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// The lines around a moment in one conversation — what was said just before it and just
    /// after, in the order they were said.
    ///
    /// The Sözler page's "sözün etrafı". A sentence quoted alone reads as a commitment far more
    /// often than it is one, because the sentence that provoked it is missing: "yav bir kulaklık
    /// alacağım güzel ya" is a promise on a card and an answer to "sesim çok kötü geliyor" in the
    /// conversation. Two lines either side is enough to tell the two apart, and it is raw
    /// transcript — no label, no reading, nothing this method decides.
    ///
    /// The moment's own line is left out: the card already carries the quote. Both halves are
    /// ordered walks over <c>ix_segment_call(call_id, start_ms)</c> with a LIMIT, so a call with
    /// two thousand lines costs what one with ten costs, and a moment at either edge of the call
    /// simply returns fewer rows than were asked for.
    /// </summary>
    /// <param name="startMs">The moment. Lines starting exactly here are the quote's own.</param>
    public IReadOnlyList<Segment> SegmentsAround(long callId, int startMs, int before = 2, int after = 2)
    {
        if (before <= 0 && after <= 0) return [];

        using var connection = Open();

        IEnumerable<SegmentRow> earlier = before <= 0
            ? []
            : connection.Query<SegmentRow>(
                """
                SELECT * FROM segment
                 WHERE call_id = @callId AND start_ms < @startMs
                 ORDER BY start_ms DESC
                 LIMIT @before;
                """,
                new { callId, startMs, before }).Reverse();

        IEnumerable<SegmentRow> later = after <= 0
            ? []
            : connection.Query<SegmentRow>(
                """
                SELECT * FROM segment
                 WHERE call_id = @callId AND start_ms > @startMs
                 ORDER BY start_ms
                 LIMIT @after;
                """,
                new { callId, startMs, after });

        return [.. earlier.Concat(later).Select(r => r.ToModel())];
    }

    // ---- search -------------------------------------------------------------

    /// <summary>
    /// Full-text search across every transcript.
    ///
    /// The query goes through the same fold as the index, and each term gets a prefix operator:
    /// Turkish is agglutinative, so a search for "kitap" must also reach "kitabı" and
    /// "kitaptan". Without that, an exact-token search finds almost nothing.
    /// </summary>
    /// <summary>
    /// Where a word was said, most relevant first.
    ///
    /// <b>The filters are applied in SQL, and that is the whole point of them being here.</b> The
    /// search screen used to fetch the best five hundred matches from the entire archive and then
    /// narrow them to a person, a speaker or a date range in memory. On a common word, one
    /// person's matches sit below the global five hundred and are thrown away before the filter
    /// ever sees them — so the screen reported "sonuç yok" for something that was said, in a
    /// sentence confident enough to be believed. Telling somebody a conversation did not happen
    /// when it did is the worst answer this product can give.
    ///
    /// The hazard was already written down, on <see cref="CallsMentioning"/>, which exists partly
    /// to avoid it. The search screen did it anyway.
    /// </summary>
    /// <param name="isMe">true for the user's own lines, false for the other party's, null for both.</param>
    public IReadOnlyList<SearchHit> Search(
        string query,
        int limit = 100,
        long? contactId = null,
        bool? isMe = null,
        DateTimeOffset? since = null,
        string? tag = null)
    {
        var match = TurkishText.ToMatchQuery(query);
        if (match.Length == 0) return [];

        // The tag filter narrows to conversations the USER labelled — the one filter here whose
        // vocabulary is theirs rather than the transcript's.
        var tagFolded = string.IsNullOrWhiteSpace(tag)
            ? null
            : TurkishText.NormalizeForSearch(tag.Trim());

        using var connection = Open();

        return [.. connection.Query<SearchHitRow>(
            """
            SELECT s.call_id      AS CallId,
                   s.id           AS SegmentId,
                   c.contact_id   AS ContactId,
                   ct.name        AS ContactName,
                   c.started_at   AS CallStartedAt,
                   s.is_me        AS IsMe,
                   s.start_ms     AS StartMs,
                   s.text         AS Text
            FROM segment_fts f
            JOIN segment s  ON s.id = f.rowid
            JOIN call    c  ON c.id = s.call_id
            LEFT JOIN contact ct ON ct.id = c.contact_id
            WHERE segment_fts MATCH @match
              AND (@contactId IS NULL OR c.contact_id = @contactId)
              AND (@isMe      IS NULL OR s.is_me      = @isMe)
              AND (@since     IS NULL OR c.started_at >= @since)
              AND (@tagFolded IS NULL OR EXISTS (
                  SELECT 1 FROM call_tag t
                  WHERE t.call_id = c.id AND t.tag_folded = @tagFolded))
            ORDER BY rank
            LIMIT @limit;
            """,
            new
            {
                match,
                limit,
                contactId,
                isMe = isMe is null ? (int?)null : isMe.Value ? 1 : 0,
                since = since is { } s ? Iso(s) : null,
                tagFolded,
            })
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// Which of one contact's calls contain a word, by call identity.
    ///
    /// Separate from <see cref="Search"/> because the question is different. Search asks "where
    /// was this said" and wants every matching line; this asks "which of these conversations was
    /// it in" and wants to narrow a list. Running the full search and grouping its hits would
    /// scan the whole archive to filter one person's calls, and would silently lose calls beyond
    /// the hit limit — a contact with two hundred conversations would appear to have none.
    /// </summary>
    public IReadOnlySet<long> CallsMentioning(long contactId, string query)
    {
        var match = TurkishText.ToMatchQuery(query);
        if (match.Length == 0) return new HashSet<long>();

        using var connection = Open();

        return connection.Query<long>(
            """
            SELECT DISTINCT s.call_id
            FROM segment_fts f
            JOIN segment s ON s.id = f.rowid
            JOIN call    c ON c.id = s.call_id
            WHERE segment_fts MATCH @match
              AND c.contact_id = @contactId;
            """,
            new { match, contactId }).ToHashSet();
    }

    /// <summary>
    /// Recordings that were kept but never attributed to anybody.
    ///
    /// These are the ones that need the user: an unlabelled call is invisible in the per-contact
    /// history, so it may as well not have been recorded. Surfacing them on the first screen is
    /// what stops them accumulating unnoticed.
    /// </summary>
    public IReadOnlyList<Call> UnlabelledCalls(int limit = 50)
    {
        using var connection = Open();
        return [.. connection.Query<CallRow>(
            """
            SELECT * FROM call
            WHERE contact_id IS NULL AND state NOT IN (7)
            ORDER BY started_at DESC LIMIT @limit;
            """, new { limit })
            .Select(r => r.ToModel())];
    }

    public IReadOnlyList<Call> FailedCalls(int limit = 20)
    {
        using var connection = Open();
        return [.. connection.Query<CallRow>(
            "SELECT * FROM call WHERE state = 6 ORDER BY started_at DESC LIMIT @limit;", new { limit })
            .Select(r => r.ToModel())];
    }

    /// <summary>Promises past their date across every contact, soonest overdue first.</summary>
    public IReadOnlyList<(Commitment Commitment, string ContactName)> OverdueCommitments(DateOnly today)
    {
        using var connection = Open();

        var rows = connection.Query<CommitmentRow, string?, (CommitmentRow, string?)>(
            """
            SELECT cm.*, ct.name
            FROM commitment cm
            LEFT JOIN contact ct ON ct.id = cm.contact_id
            WHERE cm.status = 0
              AND cm.dismissed_by_user = 0
              AND cm.is_conditional = 0
              AND COALESCE(cm.user_deadline_date, cm.deadline_date) IS NOT NULL
              AND COALESCE(cm.user_deadline_date, cm.deadline_date) < @today
            ORDER BY COALESCE(cm.user_deadline_date, cm.deadline_date);
            """,
            (commitment, name) => (commitment, name),
            new { today = today.ToString("yyyy-MM-dd") },
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "Bilinmeyen"))];
    }

    /// <summary>
    /// How many promises are past their date — the number on the Sözler badge, and nothing else.
    ///
    /// It exists because that badge used to be a by-product. The shell read the whole Sözler page
    /// — every promise in the ledger, a verdict query per conversation, the lines around every
    /// quote — and then took <c>OverdueCount</c> off the rebuilt page, on every ruling made
    /// anywhere in the application. The badge is read from every screen, so it cannot be allowed
    /// to go stale with the page it came from; this is the count on its own.
    ///
    /// The conditions are the card's, one for one: still open, not turned down, not conditional,
    /// carrying a date, and that date already past — the effective date, so a promise the user
    /// postponed counts from the day they moved it to. The join onto <c>call</c> is the ledger's
    /// own: a promise whose conversation is gone is not on the page, so it is not in the badge.
    ///
    /// The last condition cannot be asked in SQL. "Bu söz değil" is recorded against the folded
    /// quote, and Turkish folding is not something SQLite can do, so those rulings are read and
    /// matched here. That set is small by nature — it is the moments the user has personally
    /// struck out, not a table that grows with the archive.
    /// </summary>
    public int OverduePromiseCount(DateOnly today)
    {
        using var connection = Open();

        var candidates = connection.Query<(long CallId, string Quote, int StartMs)>(
            """
            SELECT cm.call_id, cm.quote, cm.quote_start_ms
            FROM commitment cm
            JOIN call c ON c.id = cm.call_id
            WHERE cm.status = 0
              AND cm.dismissed_by_user = 0
              AND cm.is_conditional = 0
              AND COALESCE(cm.user_deadline_date, cm.deadline_date) IS NOT NULL
              AND COALESCE(cm.user_deadline_date, cm.deadline_date) < @today;
            """,
            new { today = today.ToString("yyyy-MM-dd") })
            .ToList();

        if (candidates.Count == 0) return 0;

        // Same connection, so the whole badge costs one trip to the archive rather than two.
        var struckOut = connection
            .Query<(long CallId, string QuoteFolded, int StartMs)>(
                """
                SELECT call_id, quote_folded, start_ms
                FROM verdict
                WHERE kind = @kind AND verdict = @value;
                """,
                new { kind = VerdictKind.Promise, value = (int)VerdictValue.NotThat })
            .Select(v => (v.CallId, v.QuoteFolded, v.StartMs))
            .ToHashSet();

        return candidates.Count(c =>
            !struckOut.Contains((c.CallId, TurkishText.NormalizeForSearch(c.Quote), c.StartMs)));
    }

    /// <summary>
    /// Every promise still outstanding, across everybody.
    ///
    /// The per-contact version answers "what does Ahmet owe me"; this one answers the question
    /// people actually open the application with, which is "what is anybody supposed to be doing
    /// for me". Conditional promises are included but marked, because "if the shipment arrives I
    /// will call you" is a real commitment and treating it as one is not the same as treating it
    /// as unconditional.
    /// </summary>
    public IReadOnlyList<(Commitment Commitment, string ContactName)> AllOpenCommitments(int limit = 500)
    {
        using var connection = Open();

        var rows = connection.Query<CommitmentRow, string?, (CommitmentRow, string?)>(
            """
            SELECT cm.*, ct.name
            FROM commitment cm
            LEFT JOIN contact ct ON ct.id = cm.contact_id
            WHERE cm.status = 0
              AND cm.dismissed_by_user = 0
            ORDER BY
              CASE WHEN COALESCE(cm.user_deadline_date, cm.deadline_date) IS NULL THEN 1 ELSE 0 END,
              COALESCE(cm.user_deadline_date, cm.deadline_date),
              cm.id DESC
            LIMIT @limit;
            """,
            (commitment, name) => (commitment, name),
            new { limit },
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "Bilinmeyen"))];
    }

    /// <summary>
    /// Stops a commitment being counted, without deleting the words that produced it.
    ///
    /// Needed because the extraction is not perfect and a wrong entry that cannot be silenced
    /// accumulates forever until the ledger is noise. The quote stays in the transcript; only the
    /// ledger line goes.
    /// </summary>
    public void DismissCommitment(long commitmentId)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE commitment SET dismissed_by_user = 1, decided_at = @now WHERE id = @commitmentId;",
            new { commitmentId, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>Several at once — the ledger's select mode. One transaction, one ruling each.</summary>
    public int DismissCommitments(IEnumerable<long> commitmentIds)
    {
        var ids = commitmentIds.Distinct().ToList();
        if (ids.Count == 0) return 0;

        using var connection = Open();
        return connection.Execute(
            "UPDATE commitment SET dismissed_by_user = 1, decided_at = @now WHERE id IN @ids;",
            new { ids, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>
    /// Takes a dismissal back. The row was never deleted — a dismissal is a tombstone, so the
    /// same words are not found again on the next run — so bringing it back is one flag.
    /// </summary>
    public void RestoreCommitment(long commitmentId)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE commitment SET dismissed_by_user = 0, decided_at = @now WHERE id = @commitmentId;",
            new { commitmentId, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>
    /// Marks a promise as kept, so it stops appearing as outstanding. Stamped: "tutuldu" was a
    /// status with no date, and a list that says "işaretledin: 4 tutuldu" needs to know when.
    /// </summary>
    public void FulfilCommitment(long commitmentId, long? byCallId = null, DateTimeOffset? at = null)
    {
        using var connection = Open();
        var when = Iso(at ?? DateTimeOffset.UtcNow);

        connection.Execute(
            """
            UPDATE commitment
               SET status = 1, fulfilled_by_call_id = @byCallId, fulfilled_at = @when, decided_at = @when
             WHERE id = @commitmentId;
            """,
            new { commitmentId, byCallId, when });
    }

    /// <summary>Undoes "tutuldu" (or "tutulmadı"): back to open, the stamps cleared.</summary>
    public void ReopenCommitment(long commitmentId)
    {
        using var connection = Open();
        connection.Execute(
            """
            UPDATE commitment
               SET status = 0, fulfilled_by_call_id = NULL, fulfilled_at = NULL, decided_at = @now
             WHERE id = @commitmentId;
            """,
            new { commitmentId, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>
    /// The user says it was not kept. Only the user: the machine never concludes that from a
    /// silence, and "open past its date" is a different, weaker statement that the screens make
    /// on their own.
    /// </summary>
    public void AbandonCommitment(long commitmentId)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE commitment SET status = 3, decided_at = @now WHERE id = @commitmentId;",
            new { commitmentId, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>
    /// The user's own deadline. The spoken one stays in deadline_date: a postponement must not
    /// read as the other person having moved a date, which is what the deterministic check
    /// looks for. Null clears it; edited_at follows whichever user column is still set.
    /// </summary>
    public void SetUserDeadline(long commitmentId, DateOnly? deadline)
    {
        using var connection = Open();
        connection.Execute(
            """
            UPDATE commitment
               SET user_deadline_date = @deadline,
                   edited_at = CASE WHEN @deadline IS NULL AND user_obligation IS NULL THEN NULL ELSE @now END
             WHERE id = @commitmentId;
            """,
            new { commitmentId, deadline = deadline?.ToString("yyyy-MM-dd"), now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>The user's rewording of what was promised. The quote is never touched.</summary>
    public void SetUserObligation(long commitmentId, string? obligation)
    {
        var text = string.IsNullOrWhiteSpace(obligation) ? null : obligation.Trim();

        using var connection = Open();
        connection.Execute(
            """
            UPDATE commitment
               SET user_obligation = @text,
                   edited_at = CASE WHEN @text IS NULL AND user_deadline_date IS NULL THEN NULL ELSE @now END
             WHERE id = @commitmentId;
            """,
            new { commitmentId, text, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>
    /// Every promise, both directions, one query — what the Sözler page, the calendar, the
    /// caller strip and the home screen all read, so "four copies of the same list" cannot
    /// disagree. Status is judged in code from the row; this only narrows.
    /// </summary>
    public IReadOnlyList<PromiseRow> PromiseLedger(DateOnly? since = null, long? contactId = null, bool includeClosed = false)
    {
        using var connection = Open();

        var rows = connection.Query<CommitmentRow, string?, string, (CommitmentRow, string?, string)>(
            """
            SELECT cm.*, ct.name, c.started_at
            FROM commitment cm
            JOIN call c ON c.id = cm.call_id
            LEFT JOIN contact ct ON ct.id = cm.contact_id
            WHERE (@includeClosed = 1 OR (cm.status = 0 AND cm.dismissed_by_user = 0))
              AND (@contactId IS NULL OR cm.contact_id = @contactId)
              AND (@since IS NULL OR c.started_at >= @since)
            ORDER BY c.started_at DESC, cm.id;
            """,
            (commitment, name, startedAt) => (commitment, name, startedAt),
            new
            {
                includeClosed = includeClosed ? 1 : 0,
                contactId,
                since = since is { } day ? Iso(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)) : null,
            },
            splitOn: "name,started_at");

        return [.. rows.Select(r => new PromiseRow(r.Item1.ToModel(), r.Item2 ?? "Bilinmeyen", ParseIso(r.Item3)))];
    }

    /// <summary>
    /// How many calls with this person started after a day.
    ///
    /// The "was there a chance" half of "açık kaldı": a promise past its date is only called
    /// left open once the two have spoken again since — silence is not a broken promise, and a
    /// screen that said so would be accusing people of things that never came up.
    /// </summary>
    public int CountCallsSince(long contactId, DateOnly after)
    {
        using var connection = Open();

        var from = new DateTimeOffset(after.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM call WHERE contact_id = @contactId AND started_at >= @since;",
            new { contactId, since = Iso(from) });
    }

    /// <summary>One later line that sounds like the promise being kept or discussed — an offer, never a mark.</summary>
    public sealed record FulfilmentHint(long CallId, DateTimeOffset CallStartedAt, int StartMs, bool IsMe, string Quote);

    /// <summary>
    /// Looks through the calls with the same person after a promise for a line that shares at
    /// least two meaningful words with what was promised, and offers it as "tutuldu mu?".
    ///
    /// Deliberately an offer and nothing more. The machine cannot hear whether a promise was
    /// kept — "gönderdim" and "göndereceğim" share every word that matters — so the row under
    /// the card asks, and only the user's click marks anything. The words are folded the way
    /// the archive questions fold theirs, so both sides of the comparison agree on what a word
    /// is; a few calls are enough, because the point is the next conversation, not the year.
    /// </summary>
    public FulfilmentHint? SuggestFulfilment(long commitmentId, int maxCalls = 5)
    {
        using var connection = Open();

        var promise = connection.QueryFirstOrDefault<(long? ContactId, string? StartedAt, string? Obligation, string? UserObligation)>(
            """
            SELECT cm.contact_id, c.started_at, cm.obligation, cm.user_obligation
              FROM commitment cm
              JOIN call c ON c.id = cm.call_id
             WHERE cm.id = @commitmentId;
            """,
            new { commitmentId });

        if (promise.StartedAt is null || promise.ContactId is not { } contactId) return null;

        var terms = Analysis.ArchiveQuestions.Terms(promise.UserObligation ?? promise.Obligation ?? "");
        if (terms.Length < 2) return null;

        var wanted = new HashSet<string>(terms, StringComparer.Ordinal);

        var later = connection.Query<(long Id, string StartedAt)>(
            """
            SELECT id, started_at FROM call
             WHERE contact_id = @contactId AND started_at > @after
             ORDER BY started_at
             LIMIT @maxCalls;
            """,
            new { contactId, after = promise.StartedAt, maxCalls }).ToList();

        foreach (var call in later)
        {
            var lines = connection.Query<(long StartMs, long IsMe, string Text)>(
                "SELECT start_ms, is_me, text FROM segment WHERE call_id = @id ORDER BY start_ms;",
                new { id = call.Id });

            foreach (var line in lines)
            {
                var shared = Analysis.ArchiveQuestions.Terms(line.Text).Count(wanted.Contains);
                if (shared < 2) continue;

                return new FulfilmentHint(call.Id, ParseIso(call.StartedAt), (int)line.StartMs, line.IsMe != 0, line.Text.Trim());
            }
        }

        return null;
    }

    /// <summary>The promises the user turned down, newest ruling first — the "Reddedilenler" chip.</summary>
    public IReadOnlyList<(Commitment Commitment, string ContactName)> DismissedCommitments(int limit = 500)
    {
        using var connection = Open();

        var rows = connection.Query<CommitmentRow, string?, (CommitmentRow, string?)>(
            """
            SELECT cm.*, ct.name
            FROM commitment cm
            LEFT JOIN contact ct ON ct.id = cm.contact_id
            WHERE cm.dismissed_by_user = 1
            ORDER BY cm.decided_at DESC, cm.id DESC
            LIMIT @limit;
            """,
            (commitment, name) => (commitment, name),
            new { limit },
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "Bilinmeyen"))];
    }

    /// <summary>
    /// Amounts said about the same thing over time, per contact.
    ///
    /// This is the check the product exists for. A price that moved between three calls is not an
    /// accusation and is not presented as one — it is a sequence, with each figure attached to the
    /// words that were said and the moment they were said, so it can be listened to.
    /// </summary>
    public IReadOnlyList<(string ContactName, long ContactId, string Subject, IReadOnlyList<Claim> Series)>
        ChangedAmounts(int minimumChanges = 2)
    {
        using var connection = Open();

        var rows = connection.Query<ClaimRow, string?, (ClaimRow, string?)>(
            """
            SELECT c.*, ct.name
            FROM claim c
            LEFT JOIN contact ct ON ct.id = c.contact_id
            WHERE c.contact_id IS NOT NULL
              AND c.numeric_value IS NOT NULL
            ORDER BY c.contact_id, c.entity, c.attribute, c.id;
            """,
            (claim, name) => (claim, name),
            splitOn: "name");

        var grouped = rows
            .Select(r => (Claim: r.Item1.ToModel(), Name: r.Item2 ?? "Bilinmeyen"))
            .Where(r => r.Claim.ContactId is not null)
            .GroupBy(r => (r.Claim.ContactId!.Value, r.Name, r.Claim.Entity, r.Claim.Attribute));

        var result = new List<(string, long, string, IReadOnlyList<Claim>)>();

        foreach (var group in grouped)
        {
            var series = group.Select(g => g.Claim).OrderBy(c => c.Id).ToList();

            // Only report when the figure actually moved. Two identical quotes about the same
            // price are a person repeating themselves, not a change.
            var distinct = series.Select(c => c.NumericValue).Distinct().Count();
            if (distinct < minimumChanges) continue;

            var (contactId, name, entity, attribute) = group.Key;
            var subject = string.IsNullOrWhiteSpace(attribute) ? entity : $"{entity} — {attribute}";

            result.Add((name, contactId, subject, series));
        }

        return result;
    }

    /// <summary>How many recordings are waiting to be processed or are being processed now.</summary>
    /// <summary>
    /// Recordings with work still to do: recorded, queued, or being worked on.
    ///
    /// <b>Transcribed (3) is deliberately not counted.</b> It is a resting state, not a queue: with
    /// no analysis model configured every call finishes there and stays, so counting it made the
    /// figure equal to the total number of calls and unable to ever fall. On a real screen that
    /// read "13 görüşme … 13 işlem bekliyor" — the same number twice, one of them presented as a
    /// backlog that would never clear.
    ///
    /// The view model already knew this: it describes Transcribed as a resting state and excludes
    /// it from "is working". This query never got the same correction.
    /// </summary>
    public int PendingWorkCount()
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM call WHERE state IN (0,1,2,4);");
    }

    /// <summary>
    /// What the user has written about a person.
    ///
    /// The column has existed since the first schema and nothing ever wrote to it — it was read
    /// into the model on every load and then discarded. Using it now costs nothing and works on
    /// every database that already exists, which a new column would not: Migrate() has no ALTER
    /// TABLE machinery.
    ///
    /// Distinct from a call note. A call note is about one conversation; this is about the person,
    /// and it survives every reprocess, rename and merge — it is the one thing here a machine did
    /// not produce.
    /// </summary>
    public void SaveContactNote(long contactId, string? note)
    {
        using var connection = Open();

        connection.Execute(
            "UPDATE contact SET notes = @note WHERE id = @contactId;",
            new { contactId, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
    }

    /// <summary>
    /// How much there is of one person, counted rather than sampled.
    ///
    /// Deliberately its own query. Deriving these from ListCalls would silently under-report for
    /// exactly the people this matters most for: that call caps at 200 rows, so somebody with a
    /// long history would be told they had two hundred conversations and however many hours those
    /// happened to be.
    /// </summary>
    public (int Calls, TimeSpan Recorded, DateTimeOffset? First, DateTimeOffset? Last) ContactTotals(long contactId)
    {
        using var connection = Open();

        var row = connection.QueryFirstOrDefault<(int Calls, long Ms, string? First, string? Last)>(
            """
            SELECT COUNT(*)                       AS Calls,
                   COALESCE(SUM(duration_ms), 0)  AS Ms,
                   MIN(started_at)                AS First,
                   MAX(started_at)                AS Last
            FROM call WHERE contact_id = @contactId;
            """,
            new { contactId });

        return (
            row.Calls,
            TimeSpan.FromMilliseconds(row.Ms),
            row.First is null ? null : DateTimeOffset.Parse(row.First),
            row.Last is null ? null : DateTimeOffset.Parse(row.Last));
    }

    /// <summary>
    /// Recordings that have text but were never analysed.
    ///
    /// Worth its own figure rather than being folded into the queue. It is not a fault and not a
    /// backlog: it is what the archive looks like when no model is connected, and it becomes
    /// actionable the moment one is — the text is kept, so only the analysis is repeated.
    /// </summary>
    public int UnanalysedCount()
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM call WHERE state = 3;");
    }

    public (int Calls, int Contacts, TimeSpan Recorded) Totals()
    {
        using var connection = Open();

        var calls = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM call;");
        var contacts = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM contact;");
        var ms = connection.ExecuteScalar<long?>("SELECT SUM(duration_ms) FROM call;") ?? 0;

        return (calls, contacts, TimeSpan.FromMilliseconds(ms));
    }

    /// <summary>Undismissed flags across every contact, newest first, with who they belong to.</summary>
    public IReadOnlyList<(Flag Flag, string ContactName)> RecentFlags(int limit = 20)
    {
        using var connection = Open();

        var rows = connection.Query<FlagRow, string?, (FlagRow, string?)>(
            """
            SELECT f.*, ct.name
            FROM flag f
            LEFT JOIN contact ct ON ct.id = f.contact_id
            WHERE f.dismissed_by_user = 0
            ORDER BY f.created_at DESC
            LIMIT @limit;
            """,
            (flag, name) => (flag, name),
            new { limit },
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "Bilinmeyen"))];
    }

    /// <summary>
    /// How many findings are still waiting to be looked at — the number on the Defter badge.
    ///
    /// The counterpart of <see cref="OverduePromiseCount"/>, and there for the same reason: the
    /// badge used to be <c>Ledger.FlagCount</c>, which meant the only way to keep one small
    /// number honest was to rebuild the whole Defter page — two hundred findings, every tombstone
    /// beside them — on every ruling made anywhere. A badge that is read from every screen must
    /// not depend on a page nobody is looking at being rebuilt.
    ///
    /// The limit is the page's own, and matching it is the point rather than an oversight: Defter
    /// builds its count from the newest two hundred findings, so a badge that counted further
    /// would promise a number the page cannot show.
    /// </summary>
    public int OpenFlagCount(int limit = 200)
    {
        using var connection = Open();

        return connection.ExecuteScalar<int>(
            """
            SELECT COUNT(*) FROM (
                SELECT f.id
                FROM flag f
                WHERE f.dismissed_by_user = 0
                ORDER BY f.created_at DESC
                LIMIT @limit
            );
            """,
            new { limit });
    }

    // ---- analysis -----------------------------------------------------------

    /// <summary>
    /// Clears what a previous analysis of this call produced.
    ///
    /// Called before writing a fresh analysis, and the reason is a fault that corrupts the thing
    /// this product exists for. The three writes below are plain inserts with no uniqueness
    /// constraint behind them, so analysing a call a second time appended a second full copy of
    /// that person's commitments, claims and flags. Every retry doubled the ledger.
    ///
    /// It was not a rare path either. Reprocessing is offered on the contact page and on the
    /// processing screen, a timeout used to requeue a call silently on every startup, and the whole
    /// point of the "retry everything" button is to run after a fixed configuration — so the
    /// ordinary way to use the product was also the way to corrupt it. A person would appear to
    /// have promised the same thing three times, and the deterministic checks that compare
    /// commitments against each other would then report contradictions between a statement and
    /// itself.
    ///
    /// Delete-then-insert rather than an upsert, matching <see cref="ReplaceSegments"/>. An
    /// extraction is not an accumulation of facts: it is one model's reading of one conversation,
    /// and a second reading replaces the first rather than adding to it.
    ///
    /// Dismissed flags are deliberately not resurrected — see the flag write itself.
    /// </summary>
    public void ClearAnalysis(long callId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // A promise the user has already ruled on keeps their ruling.
        //
        // The same protection as the flags below, and it was missing here — so reprocessing a
        // call deleted every commitment including the ones marked kept and the ones dismissed.
        // Those are the only rows in this table a person wrote: everything else the analysis
        // produces is replaced on every run and nothing is lost, but a judgement thrown away is
        // work the user has to do again, and doing it again is how a ledger stops being trusted.
        //
        // status 0 is the untouched default; anything else is somebody's decision. So is a
        // postponed date or a reworded obligation: the user columns are the user's.
        connection.Execute(
            """
            DELETE FROM commitment
             WHERE call_id = @callId AND status = 0 AND dismissed_by_user = 0
               AND edited_at IS NULL AND user_deadline_date IS NULL;
            """,
            new { callId }, transaction);

        connection.Execute("DELETE FROM claim WHERE call_id = @callId;", new { callId }, transaction);

        // A flag the user has already dismissed stays dismissed. Reprocessing must not bring back
        // a judgement they have explicitly rejected — that is how a ledger stops being read.
        //
        // And only the pipeline's own flags: the consistency check's findings were paid for
        // separately and belong to a different button — rebuilding the ledger must not erase them.
        connection.Execute(
            "DELETE FROM flag WHERE call_id = @callId AND dismissed_by_user = 0 AND source = @source;",
            new { callId, source = Flag.Sources.Pipeline }, transaction);

        // The questions are wholly the extraction's: it finds every one of them on every run,
        // there is nothing a user can rule on, and a run that appended instead of replacing
        // would double the denominator the contact card divides by.
        connection.Execute("DELETE FROM speech_act WHERE call_id = @callId;", new { callId }, transaction);

        // The tactic quotes, but only the ones this machinery wrote. The assessment's rows were
        // paid for by a separate button and a ledger rebuild must not erase them — the same
        // ownership rule as the flags above — and a dismissed row is a tombstone that has to
        // outlive the re-run, or the next one puts the same sentence back.
        connection.Execute(
            """
            DELETE FROM tactic_evidence
             WHERE call_id = @callId AND source = @source AND dismissed_by_user = 0;
            """,
            new { callId, source = TacticEvidence.Sources.Pipeline }, transaction);

        transaction.Commit();
    }

    /// <summary>
    /// Clears one conversation's consistency findings and note before a re-run — its own rows
    /// only, dismissed ones kept, the ledger untouched. The mirror image of ClearAnalysis.
    /// </summary>
    public void ClearConsistency(long callId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            "DELETE FROM flag WHERE call_id = @callId AND dismissed_by_user = 0 AND source = @source;",
            new { callId, source = Flag.Sources.Consistency }, transaction);

        connection.Execute(
            "DELETE FROM consistency_note WHERE call_id = @callId;", new { callId }, transaction);

        transaction.Commit();
    }

    /// <summary>
    /// Deletes one conversation's own pipeline findings of the named kinds, dismissals kept.
    ///
    /// Narrower than <see cref="ClearAnalysis"/> in both directions, and both directions matter.
    /// It reaches a call OTHER than the one being analysed, because the deterministic checks read
    /// the whole person and file each finding against the conversation it was said in — so a run
    /// over one call rewrites rows belonging to another, and scoping the delete to the analysed
    /// call added a duplicate row on every run. And it touches only the kinds that run is
    /// actually replacing, so a finding read from the other call itself — a scam pattern, an
    /// evasion rate — is not deleted by a run that never looked at that call's transcript.
    /// </summary>
    /// <returns>How many rows were removed.</returns>
    public int ClearPipelineFlags(long callId, IReadOnlyCollection<int> kinds)
    {
        if (kinds.Count == 0) return 0;

        using var connection = Open();

        return connection.Execute(
            """
            DELETE FROM flag
             WHERE call_id = @callId AND dismissed_by_user = 0
               AND source = @source AND kind IN @kinds;
            """,
            new { callId, source = Flag.Sources.Pipeline, kinds });
    }

    /// <summary>
    /// Deletes one PERSON's pipeline findings of the named kinds, across every conversation with
    /// them, dismissals kept. What the deterministic checks clear before writing their answer.
    ///
    /// The kinds this reaches are the ones computed from the whole person rather than from one
    /// transcript — an overdue promise, a deadline that moved, a figure that changed. Those
    /// checks read every open commitment and every claim of the contact, so a run that performs
    /// them has an opinion about every row of those kinds the person has, on every one of their
    /// calls. Scoping the delete to the kinds a run happened to PRODUCE was the hole: a finding
    /// that stops being produced — the overdue promise the user has since marked kept — is
    /// exactly the row with no group to be deleted by, so it survived on the other conversation
    /// until that conversation was itself re-analysed, and it read as current the whole time.
    ///
    /// It is not widened past that. A scam pattern or an evasion rate is read out of one call's
    /// own transcript, and a run that never opened that transcript has no opinion about it; the
    /// contradiction check is a paid model call that can fail halfway, so a run that ended early
    /// would erase judgements it simply never got to. Those keep the per-call delete.
    /// </summary>
    /// <returns>How many rows were removed.</returns>
    public int ClearPersonWideFlags(long contactId, IReadOnlyCollection<int> kinds)
    {
        if (kinds.Count == 0) return 0;

        using var connection = Open();

        // Both halves of "belongs to this person", because they can disagree. The flag carries a
        // contact of its own, and a call carries one too; a conversation whose person was named
        // after it was analysed has findings filed under nobody.
        return connection.Execute(
            """
            DELETE FROM flag
             WHERE dismissed_by_user = 0
               AND source = @source AND kind IN @kinds
               AND (contact_id = @contactId
                    OR call_id IN (SELECT id FROM call WHERE contact_id = @contactId));
            """,
            new { contactId, source = Flag.Sources.Pipeline, kinds });
    }

    /// <summary>
    /// The dismissed findings' identities for one conversation: (kind, folded quote) pairs.
    /// What a consistency re-run checks before inserting, so a judgement the user rejected
    /// once is never resurrected by the next run finding the same thing.
    /// </summary>
    public IReadOnlySet<(int Kind, string FoldedQuote)> DismissedFlagKeys(long callId)
    {
        using var connection = Open();

        return connection
            .Query<(long Kind, string Quote)>(
                "SELECT kind, quote FROM flag WHERE call_id = @callId AND dismissed_by_user = 1;",
                new { callId })
            .Select(r => ((int)r.Kind, Text.TurkishText.NormalizeForSearch(r.Quote)))
            .ToHashSet();
    }

    /// <summary>
    /// The promises of one conversation that ClearAnalysis leaves standing — ruled on, dismissed,
    /// or edited. The K4 rule for commitments: the pipeline compares its findings against these
    /// before inserting, so a promise the user marked kept is not written a second time as a
    /// fresh open one, and a dismissed one does not return undismissed. Before this, every re-run
    /// did both.
    ///
    /// Rows rather than a set of keys, and the obligation comes with them. See
    /// <see cref="SurvivingCommitment"/> for why: two promises out of one sentence share a key,
    /// and a set cannot tell the pipeline that one tombstone accounts for exactly one of them.
    /// </summary>
    public IReadOnlyList<SurvivingCommitment> SurvivingCommitments(long callId)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<(long ByMe, string Quote, string Obligation)>(
                    """
                    SELECT by_me, quote, obligation FROM commitment
                     WHERE call_id = @callId
                       AND (status <> 0 OR dismissed_by_user = 1
                            OR edited_at IS NOT NULL OR user_deadline_date IS NOT NULL);
                    """,
                    new { callId })
                .Select(r => new SurvivingCommitment(
                    r.ByMe != 0,
                    Text.TurkishText.NormalizeForSearch(r.Quote),
                    Text.TurkishText.NormalizeForSearch(r.Obligation))),
        ];
    }

    /// <summary>
    /// Every promise and figure already stored for one conversation, folded the way the pipeline
    /// de-duplicates.
    ///
    /// Read by an analysis that could not read the whole conversation. Such a run must not clear
    /// — the sections it never saw are only in the database — so it adds, and to add without
    /// adding a second copy of what an earlier fuller run wrote it has to know what is there.
    /// </summary>
    public StoredLedgerKeys LedgerKeysOf(long callId)
    {
        using var connection = Open();

        var commitments = connection
            .Query<(long ByMe, string Quote)>(
                "SELECT by_me, quote FROM commitment WHERE call_id = @callId;", new { callId })
            .Select(r => (r.ByMe != 0, Text.TurkishText.NormalizeForSearch(r.Quote)))
            .ToHashSet();

        var claims = connection
            .Query<(string Entity, string Attribute, string Value, string Quote)>(
                "SELECT entity, attribute, value, quote FROM claim WHERE call_id = @callId;",
                new { callId })
            .Select(r => (
                Text.TurkishText.NormalizeForSearch(r.Entity),
                Text.TurkishText.NormalizeForSearch(r.Attribute),
                Text.TurkishText.NormalizeForSearch(r.Value),
                Text.TurkishText.NormalizeForSearch(r.Quote)))
            .ToHashSet();

        return new StoredLedgerKeys(commitments, claims);
    }

    /// <summary>
    /// Which transcript each derived note of a call was written from, against the one the call
    /// shows now. Read by the call window to label a note stale rather than pass it off as
    /// current; see <see cref="Domain.DerivedFreshness"/>.
    /// </summary>
    public DerivedFreshness DerivedFreshness(long callId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<FreshnessRow>(
            """
            SELECT c.transcript_version_id AS current_version,
                   (SELECT COUNT(*) FROM call_summary WHERE call_id = c.id) AS summary_count,
                   (SELECT transcript_version_id FROM call_summary WHERE call_id = c.id) AS summary_version,
                   (SELECT COUNT(*) FROM reading_note WHERE call_id = c.id) AS reading_count,
                   (SELECT transcript_version_id FROM reading_note WHERE call_id = c.id) AS reading_version,
                   (SELECT COUNT(*) FROM deception_note WHERE call_id = c.id) AS deception_count,
                   (SELECT transcript_version_id FROM deception_note WHERE call_id = c.id) AS deception_version,
                   (SELECT COUNT(*) FROM consistency_note WHERE call_id = c.id) AS consistency_count,
                   (SELECT transcript_version_id FROM consistency_note WHERE call_id = c.id) AS consistency_version,
                   (SELECT COUNT(*) FROM action_item WHERE call_id = c.id AND status = 0) AS action_count,
                   (SELECT COUNT(*) FROM action_item WHERE call_id = c.id AND status = 0
                                                       AND transcript_version_id IS NOT NULL) AS action_known,
                   (SELECT COUNT(*) FROM action_item WHERE call_id = c.id AND status = 0
                                                       AND transcript_version_id IS NOT NULL
                                                       AND transcript_version_id <> c.transcript_version_id) AS action_stale,

                   -- The findings themselves, and not only the note over them. A run that
                   -- produced contradictions but no justified warning writes no note row at
                   -- all, so judging consistency by that row alone left those findings — quoted
                   -- from sentences a re-transcription has taken off the screen — looking
                   -- current, with nothing anywhere to say otherwise.
                   (SELECT COUNT(*) FROM flag WHERE call_id = c.id AND source = @consistency) AS flag_count,
                   (SELECT COUNT(*) FROM flag WHERE call_id = c.id AND source = @consistency
                                                AND transcript_version_id IS NOT NULL) AS flag_known,
                   (SELECT COUNT(*) FROM flag WHERE call_id = c.id AND source = @consistency
                                                AND transcript_version_id IS NOT NULL
                                                AND transcript_version_id <> c.transcript_version_id) AS flag_stale
              FROM call c
             WHERE c.id = @callId;
            """,
            new { callId, consistency = Flag.Sources.Consistency });

        if (row is null) return new DerivedFreshness(null, Staleness.Absent, Staleness.Absent, Staleness.Absent, Staleness.Absent, Staleness.Absent);

        var current = row.current_version;

        // Open suggestions are many rows: stale if any was drawn from another transcript,
        // unknown if none of them recorded one, fresh otherwise.
        var actions = Domain.DerivedFreshness.JudgeMany(
            row.action_count, row.action_known, row.action_stale, current);

        // The consistency tab shows two things a run produced, and either can outlive its text:
        // the warning note (one row) and the findings (many). Judged together, worst first.
        var consistency = Domain.DerivedFreshness.Worst(
            Domain.DerivedFreshness.Judge(row.consistency_count, row.consistency_version, current),
            Domain.DerivedFreshness.JudgeMany(row.flag_count, row.flag_known, row.flag_stale, current));

        return new DerivedFreshness(
            current,
            Domain.DerivedFreshness.Judge(row.summary_count, row.summary_version, current),
            Domain.DerivedFreshness.Judge(row.reading_count, row.reading_version, current),
            Domain.DerivedFreshness.Judge(row.deception_count, row.deception_version, current),
            consistency,
            actions);
    }

    // ---- verdicts (user data) -----------------------------------------------------------

    /// <summary>
    /// Records what the user heard. One verdict per moment: giving a second on the same words
    /// at the same millisecond replaces the first, which is what "I changed my mind" means.
    /// </summary>
    public long SaveVerdict(Verdict verdict)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            DELETE FROM verdict
             WHERE call_id = @CallId AND kind = @Kind AND quote_folded = @QuoteFolded AND start_ms = @StartMs;
            """,
            new { verdict.CallId, verdict.Kind, verdict.QuoteFolded, verdict.StartMs }, transaction);

        var id = connection.ExecuteScalar<long>(
            """
            INSERT INTO verdict (call_id, kind, target_id, quote_folded, start_ms, verdict, decided_at)
            VALUES (@CallId, @Kind, @TargetId, @QuoteFolded, @StartMs, @Value, @DecidedAt)
            RETURNING id;
            """,
            new
            {
                verdict.CallId,
                verdict.Kind,
                verdict.TargetId,
                verdict.QuoteFolded,
                verdict.StartMs,
                Value = (int)verdict.Value,
                DecidedAt = Iso(verdict.DecidedAt == default ? DateTimeOffset.UtcNow : verdict.DecidedAt),
            }, transaction);

        transaction.Commit();
        return id;
    }

    /// <summary>The verdicts of one conversation, optionally of one kind, in the order they were about.</summary>
    public IReadOnlyList<Verdict> Verdicts(long callId, string? kind = null)
    {
        using var connection = Open();

        return [.. connection
            .Query<VerdictRow>(
                """
                SELECT * FROM verdict
                 WHERE call_id = @callId AND (@kind IS NULL OR kind = @kind)
                 ORDER BY start_ms, id;
                """,
                new { callId, kind })
            .Select(r => r.ToModel())];
    }

    /// <summary>How many verdicts of a kind exist, and how many of them say "correct" — the precision figure's two numbers.</summary>
    public (int Listened, int Correct) VerdictTally(string kind, long? contactId = null)
    {
        using var connection = Open();

        return connection.QuerySingle<(int Listened, int Correct)>(
            """
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN v.verdict = 1 THEN 1 ELSE 0 END), 0)
              FROM verdict v
              JOIN call c ON c.id = v.call_id
             WHERE v.kind = @kind AND (@contactId IS NULL OR c.contact_id = @contactId);
            """,
            new { kind, contactId });
    }

    public void DeleteVerdict(long verdictId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM verdict WHERE id = @verdictId;", new { verdictId });
    }

    /// <summary>Flags for one conversation, oldest first — the order the evidence happened in.</summary>
    public IReadOnlyList<Flag> FlagsOf(long callId, bool includeDismissed = false)
    {
        using var connection = Open();

        var sql = includeDismissed
            ? "SELECT * FROM flag WHERE call_id = @callId ORDER BY quote_start_ms, id;"
            : "SELECT * FROM flag WHERE call_id = @callId AND dismissed_by_user = 0 ORDER BY quote_start_ms, id;";

        return [.. connection.Query<FlagRow>(sql, new { callId }).Select(r => r.ToModel())];
    }

    // ---- speech habits: Aynam ------------------------------------------------------------
    //
    // Three tables, two owners. speech_habit is the machine's cache and is rewritten by every
    // recount; habit_lexicon and call_intent are the user's and no recount touches them.
    // ClearAnalysis leaves all three alone — the first because it is not the ledger's, the other
    // two because they are not the machine's.

    /// <summary>What is stored for one call: the payload, the dictionary it was counted with, the transcript it was counted from.</summary>
    public sealed record StoredHabits(string Json, int LexiconVersion, long? TranscriptVersionId, DateTimeOffset CreatedAt);

    /// <summary>One call's stored report as the trend reads it: whose, when, from which engine.</summary>
    /// <param name="Engine">Of the transcript the report was counted FROM, or null when that transcript is gone or was never recorded.</param>
    public sealed record HabitSeriesRow(
        long CallId,
        DateTimeOffset StartedAt,
        long? ContactId,
        string? Engine,
        bool LikelyNoHeadphones,
        int LexiconVersion,
        string Json);

    /// <summary>
    /// Saves the counts for a conversation, replacing any earlier ones. Filed under the transcript
    /// the call shows, like SaveReading, so a re-transcription can be told from a recount.
    /// </summary>
    public void SaveHabits(long callId, int lexiconVersion, string json)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO speech_habit (call_id, transcript_version_id, lexicon_version, json, created_at)
            VALUES (@callId, (SELECT transcript_version_id FROM call WHERE id = @callId),
                    @lexiconVersion, @json, @now)
            ON CONFLICT(call_id) DO UPDATE SET
                transcript_version_id = excluded.transcript_version_id,
                lexicon_version = excluded.lexicon_version,
                json = excluded.json,
                created_at = excluded.created_at;
            """,
            new { callId, lexiconVersion, json, now = Iso(DateTimeOffset.UtcNow) });
    }

    // ---- how it was said, and what was not a word ---------------------------------------

    /// <summary>A stored measurement and the audio it was made from.</summary>
    public sealed record StoredProsody(string Json, string AudioKey, DateTimeOffset CreatedAt);

    /// <summary>
    /// Saves one call's level and pitch measurement, replacing any earlier one.
    ///
    /// Filed under the AUDIO rather than the transcript: nothing here came from the words, so a
    /// re-transcription leaves it valid and re-measuring would be a minute of CPU spent to
    /// rediscover the same numbers. What invalidates it is the recording changing, which is what
    /// the key describes.
    /// </summary>
    public void SaveProsody(long callId, string audioKey, string json)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO prosody (call_id, audio_key, json, created_at)
            VALUES (@callId, @audioKey, @json, @now)
            ON CONFLICT(call_id) DO UPDATE SET
                audio_key = excluded.audio_key,
                json = excluded.json,
                created_at = excluded.created_at;
            """,
            new { callId, audioKey, json, now = Iso(DateTimeOffset.UtcNow) });
    }

    public StoredProsody? GetProsody(long callId)
    {
        using var connection = Open();

        return connection.QueryFirstOrDefault<ProsodyRow>(
            "SELECT json, audio_key, created_at FROM prosody WHERE call_id = @callId;",
            new { callId })?.ToModel();
    }

    public void DeleteProsody(long callId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM prosody WHERE call_id = @callId;", new { callId });
    }

    /// <summary>One thing the transcription service heard that was not a word.</summary>
    public sealed record AudioEvent(long Id, long CallId, string Channel, int StartMs, int EndMs, string Kind);

    /// <summary>
    /// Replaces a call's non-word events with what the transcript just reported.
    ///
    /// Replace rather than merge: these belong to one transcript, and a second engine's laughter
    /// beside a first engine's would be two readings of one moment presented as two moments.
    /// </summary>
    public int ReplaceAudioEvents(long callId, IReadOnlyList<(string Channel, int StartMs, int EndMs, string Kind)> events)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute("DELETE FROM audio_event WHERE call_id = @callId;", new { callId }, transaction);

        var written = 0;

        foreach (var (channel, startMs, endMs, kind) in events)
        {
            if (string.IsNullOrWhiteSpace(kind)) continue;

            written += connection.Execute(
                """
                INSERT INTO audio_event (call_id, transcript_version_id, channel, start_ms, end_ms, kind)
                VALUES (@callId, (SELECT transcript_version_id FROM call WHERE id = @callId),
                        @channel, @startMs, @endMs, @kind);
                """,
                new { callId, channel, startMs, endMs, kind }, transaction);
        }

        transaction.Commit();
        return written;
    }

    public IReadOnlyList<AudioEvent> AudioEventsOf(long callId)
    {
        using var connection = Open();

        return [.. connection.Query<AudioEventRow>(
            "SELECT id, call_id, channel, start_ms, end_ms, kind FROM audio_event WHERE call_id = @callId ORDER BY start_ms;",
            new { callId }).Select(r => r.ToModel())];
    }

    public StoredHabits? GetHabits(long callId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<HabitRow>(
            "SELECT json, lexicon_version, transcript_version_id, created_at FROM speech_habit WHERE call_id = @callId;",
            new { callId });

        return row?.ToModel();
    }

    public void DeleteHabits(long callId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM speech_habit WHERE call_id = @callId;", new { callId });
    }

    /// <summary>
    /// Every stored report since a date, oldest first, with what the trend needs beside it — one
    /// SELECT for a year, which is the budget the mirror page has.
    ///
    /// The engine is the one the report was counted FROM, not the one the call shows now: the
    /// counts belong to that text, and a call re-transcribed since keeps its dot under the old
    /// engine until it is recounted. The date is compared at UTC midnight; a call in the small
    /// hours of the first day may fall on either side, which for a trend over months is nothing.
    /// </summary>
    public IReadOnlyList<HabitSeriesRow> HabitSeries(DateOnly since, long? contactId = null, string? engine = null)
    {
        using var connection = Open();

        var from = Iso(new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        return [.. connection
            .Query<HabitSeriesRaw>(
                """
                SELECT h.call_id, c.started_at, c.contact_id, c.likely_no_headphones,
                       tv.engine, h.lexicon_version, h.json
                  FROM speech_habit h
                  JOIN call c ON c.id = h.call_id
                  LEFT JOIN transcript_version tv ON tv.id = h.transcript_version_id
                 WHERE c.started_at >= @from
                   AND (@contactId IS NULL OR c.contact_id = @contactId)
                   AND (@engine IS NULL OR tv.engine = @engine)
                 ORDER BY c.started_at, h.call_id;
                """,
                new { from, contactId, engine })
            .Select(r => r.ToModel())];
    }

    /// <summary>The dictionary, in the user's order within each kind.</summary>
    public IReadOnlyList<HabitLexeme> Lexicon()
    {
        using var connection = Open();

        return [.. connection
            .Query<LexemeRow>("SELECT * FROM habit_lexicon ORDER BY kind, position, lexeme_folded;")
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// Creates or updates a stem. Identity is the kind and the folded stem, so two spellings of
    /// one word are one row; the endings are folded here too, because the matcher compares them
    /// against folded text and a stored ending with a Turkish letter in it would never match.
    /// Returns the row's id.
    /// </summary>
    public long UpsertLexeme(string kind, string lexeme, IReadOnlyList<string>? suffixes = null, int position = 0)
    {
        var trimmed = lexeme.Trim();
        var folded = TurkishText.NormalizeForSearch(trimmed);
        if (folded.Length == 0) throw new ArgumentException("Lexeme cannot be empty.", nameof(lexeme));

        var endings = (suffixes ?? [])
            .Select(TurkishText.NormalizeForSearch)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        using var connection = Open();

        return connection.ExecuteScalar<long>(
            """
            INSERT INTO habit_lexicon (kind, lexeme_folded, suffixes, lexeme, position)
            VALUES (@kind, @folded, @suffixes, @lexeme, @position)
            ON CONFLICT(kind, lexeme_folded) DO UPDATE SET
                suffixes = excluded.suffixes, lexeme = excluded.lexeme, position = excluded.position
            RETURNING id;
            """,
            new
            {
                kind,
                folded,
                suffixes = endings.Count == 0 ? null : JsonSerializer.Serialize(endings),
                lexeme = trimmed,
                position,
            });
    }

    public void DeleteLexeme(long id)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM habit_lexicon WHERE id = @id;", new { id });
    }

    /// <summary>What the user wrote they meant to do in a conversation, and when they last changed it.</summary>
    public (string Text, DateTimeOffset UpdatedAt)? GetCallIntent(long callId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<(string Text, string UpdatedAt)>(
            "SELECT text, updated_at FROM call_intent WHERE call_id = @callId;",
            new { callId });

        return row == default ? null : (row.Text, ParseIso(row.UpdatedAt));
    }

    /// <summary>Saves the intent, replacing any earlier one. Blank text removes it: an empty card is no card.</summary>
    public void SaveCallIntent(long callId, string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            DeleteCallIntent(callId);
            return;
        }

        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO call_intent (call_id, text, updated_at)
            VALUES (@callId, @text, @now)
            ON CONFLICT(call_id) DO UPDATE SET text = excluded.text, updated_at = excluded.updated_at;
            """,
            new { callId, text = trimmed, now = Iso(DateTimeOffset.UtcNow) });
    }

    public void DeleteCallIntent(long callId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM call_intent WHERE call_id = @callId;", new { callId });
    }

    // ---- the contact card: tactic quotes, questions, and the reads that count them ---------
    //
    // Everything here is evidence about one person, accumulated over their calls: a label with a
    // verbatim sentence under it, a question and what happened to it, a figure that moved. No
    // row carries a score, and no read returns one — the numbers are counts of things somebody
    // said, each with the millisecond that plays it.
    //
    // The rule that governs the whole section: NOTHING IN tactic_evidence OR speech_act IS EVER
    // PUT INTO A PROMPT. The reads below feed screens. The moment one of them feeds a model, a
    // run starts building on its own earlier labels instead of on the conversation.

    /// <summary>
    /// Replaces one machinery's tactic quotes for a call.
    ///
    /// Delete-then-insert scoped by source, like the flags: a ledger rebuild rewrites the
    /// pipeline's rows and leaves the opt-in assessment's standing, and a fresh assessment
    /// replaces its own rows rather than adding a second copy of the same sentences.
    ///
    /// Two things are refused rather than written. A label this build does not recognise —
    /// filing it as "diger" would put whatever a model typed onto somebody's card as a pattern.
    /// And a sentence the user has already dismissed for this call: that row is a tombstone, and
    /// re-inserting the same words beside it is how a rejected finding comes back.
    ///
    /// The contact and the transcript are read off the call rather than taken from the caller,
    /// so a quote cannot be filed against a person the call does not belong to.
    /// </summary>
    /// <returns>How many rows were actually written.</returns>
    public int ReplaceTacticEvidence(long callId, string source, IReadOnlyList<TacticEvidence> lines)
    {
        var dismissed = DismissedTacticKeys(callId);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            DELETE FROM tactic_evidence
             WHERE call_id = @callId AND source = @source AND dismissed_by_user = 0;
            """,
            new { callId, source }, transaction);

        var written = 0;

        foreach (var line in lines)
        {
            if (TacticEvidence.Recognise(line.Tactic) is not { } tactic) continue;
            if (dismissed.Contains((tactic, TurkishText.NormalizeForSearch(line.Quote)))) continue;

            connection.Execute(
                """
                INSERT INTO tactic_evidence
                    (call_id, contact_id, transcript_version_id, source, tactic, by_me,
                     quote, quote_start_ms, low_confidence, model_used, created_at)
                VALUES
                    (@callId,
                     (SELECT contact_id FROM call WHERE id = @callId),
                     (SELECT transcript_version_id FROM call WHERE id = @callId),
                     @source, @tactic, @byMe, @quote, @startMs, @lowConfidence, @modelUsed, @now);
                """,
                new
                {
                    callId,
                    source,
                    tactic,
                    byMe = line.ByMe ? 1 : 0,
                    quote = line.Quote,
                    startMs = line.QuoteStartMs,
                    lowConfidence = line.LowConfidence ? 1 : 0,
                    modelUsed = line.ModelUsed,
                    now = Iso(line.CreatedAt == default ? DateTimeOffset.UtcNow : line.CreatedAt),
                },
                transaction);

            written++;
        }

        transaction.Commit();
        return written;
    }

    /// <summary>
    /// The tactic quotes the user has turned down for one call, as (tactic, folded quote) pairs.
    /// The DismissedFlagKeys rule applied to this table: a judgement rejected once is not
    /// resurrected by the next run finding the same sentence.
    /// </summary>
    public IReadOnlySet<(string Tactic, string FoldedQuote)> DismissedTacticKeys(long callId)
    {
        using var connection = Open();

        return connection
            .Query<(string Tactic, string Quote)>(
                "SELECT tactic, quote FROM tactic_evidence WHERE call_id = @callId AND dismissed_by_user = 1;",
                new { callId })
            .Select(r => (r.Tactic, TurkishText.NormalizeForSearch(r.Quote)))
            .ToHashSet();
    }

    /// <summary>One conversation's tactic quotes, in the order they were said.</summary>
    public IReadOnlyList<TacticEvidence> TacticEvidenceOf(long callId, bool includeDismissed = false)
    {
        using var connection = Open();

        return [.. connection
            .Query<TacticRow>(
                """
                SELECT * FROM tactic_evidence
                 WHERE call_id = @callId AND (@includeDismissed = 1 OR dismissed_by_user = 0)
                 ORDER BY quote_start_ms, id;
                """,
                new { callId, includeDismissed = includeDismissed ? 1 : 0 })
            .Select(r => r.ToModel())];
    }

    public void DismissTacticEvidence(long id)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE tactic_evidence SET dismissed_by_user = 1 WHERE id = @id;", new { id });
    }

    /// <summary>Takes a dismissal back; the row was a tombstone, never gone.</summary>
    public void RestoreTacticEvidence(long id)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE tactic_evidence SET dismissed_by_user = 0 WHERE id = @id;", new { id });
    }

    /// <summary>
    /// Replaces one conversation's questions. Wholly the extraction's — there is nothing here a
    /// user has ruled on, so the whole call is rewritten rather than filtered by owner.
    /// </summary>
    public int ReplaceSpeechActs(long callId, IReadOnlyList<SpeechAct> acts)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute("DELETE FROM speech_act WHERE call_id = @callId;", new { callId }, transaction);

        var written = 0;

        foreach (var act in acts)
        {
            connection.Execute(
                """
                INSERT INTO speech_act
                    (call_id, contact_id, by_me, kind, answer_status, quote,
                     quote_start_ms, low_confidence, created_at)
                VALUES
                    (@callId, (SELECT contact_id FROM call WHERE id = @callId), @byMe, @kind,
                     @answerStatus, @quote, @startMs, @lowConfidence, @now);
                """,
                new
                {
                    callId,
                    byMe = act.ByMe ? 1 : 0,
                    kind = act.Kind,
                    answerStatus = SpeechAct.Statuses.Recognise(act.AnswerStatus),
                    quote = act.Quote,
                    startMs = act.QuoteStartMs,
                    lowConfidence = act.LowConfidence ? 1 : 0,
                    now = Iso(act.CreatedAt == default ? DateTimeOffset.UtcNow : act.CreatedAt),
                },
                transaction);

            written++;
        }

        transaction.Commit();
        return written;
    }

    /// <summary>One conversation's questions, in the order they were asked.</summary>
    public IReadOnlyList<SpeechAct> SpeechActsOf(long callId)
    {
        using var connection = Open();

        return [.. connection
            .Query<SpeechActRow>(
                "SELECT * FROM speech_act WHERE call_id = @callId ORDER BY quote_start_ms, id;",
                new { callId })
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// One "Kalıplar" row: a kind of finding from one source, counted over a person's calls.
    /// </summary>
    /// <param name="Kind">A <see cref="FlagKind"/> name, or a tactic label.</param>
    /// <param name="Source">Which machinery produced it: pipeline, consistency, or deception.</param>
    /// <param name="Total">Rows the user has not dismissed.</param>
    /// <param name="Calls">How many distinct conversations those came from.</param>
    /// <param name="LowConfidence">How many of them rest on audio the transcriber doubted.</param>
    /// <param name="Dismissed">Rows the user turned down. Not part of <paramref name="Total"/>.</param>
    /// <param name="Listened">How many of the counted rows the user has ruled on by ear.</param>
    /// <param name="Correct">How many of those they confirmed. Never divided here — the screen decides.</param>
    /// <param name="Last">When the most recent counted row's conversation began.</param>
    public sealed record PatternSummary(
        string Kind,
        string Source,
        int Total,
        int Calls,
        int LowConfidence,
        int Dismissed,
        int Listened,
        int Correct,
        DateTimeOffset? Last);

    /// <summary>
    /// Everything the archive has counted against one person, by kind and by who counted it.
    ///
    /// The union of the ledger's flags and the tactic quotes, because the card shows them in one
    /// list and the source filter is what keeps them honest: a deterministic check and a model's
    /// label are both evidence, and a screen that pooled them would let one borrow the other's
    /// standing. Dismissed rows are counted separately rather than dropped, so the card can say
    /// "reddettiklerin (3) sayılmaz" instead of quietly shrinking.
    ///
    /// The listening figures are matched the way <see cref="Domain.Verdict"/> says they must be —
    /// by the folded words and the millisecond, never by a row id — so a re-run that moved every
    /// id keeps the user's own verdicts attached to the sentences they were about.
    /// </summary>
    public IReadOnlyList<PatternSummary> ContactPatterns(long contactId)
    {
        using var connection = Open();

        var raw = connection.Query<PatternRaw>(
            """
            SELECT k.is_flag, k.kind, k.source, k.id, k.call_id, k.quote, k.start_ms,
                   k.low_confidence, k.dismissed, c.started_at,
                   v.quote_folded AS verdict_quote, v.verdict AS verdict_value, v.decided_at AS verdict_at
              FROM (
                    SELECT 1 AS is_flag, CAST(f.kind AS TEXT) AS kind, f.source AS source,
                           f.id AS id, f.call_id AS call_id, f.quote AS quote,
                           f.quote_start_ms AS start_ms, f.low_confidence AS low_confidence,
                           f.dismissed_by_user AS dismissed
                      FROM flag f
                     WHERE f.contact_id = @contactId
                    UNION ALL
                    SELECT 0, t.tactic, t.source, t.id, t.call_id, t.quote,
                           t.quote_start_ms, t.low_confidence, t.dismissed_by_user
                      FROM tactic_evidence t
                     WHERE t.contact_id = @contactId
                   ) k
              JOIN call c ON c.id = k.call_id
              LEFT JOIN verdict v
                     ON v.call_id = k.call_id
                    AND v.kind IN (@flagKind, @patternKind)
                    AND ABS(v.start_ms - k.start_ms) <= @window;
            """,
            new
            {
                contactId,
                flagKind = VerdictKind.Flag,
                patternKind = VerdictKind.Pattern,
                window = Analysis.SpeechHabits.VerdictWindowMs,
            }).ToList();

        List<PatternSummary> summaries = [];

        // One row of evidence may meet several verdicts inside the window; the words decide
        // which of them is about it, and the newest ruling is the one that stands.
        foreach (var kindGroup in raw
                     .GroupBy(r => (r.is_flag, r.kind, r.source))
                     .OrderBy(g => g.Key.kind, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.source, StringComparer.Ordinal))
        {
            var rows = kindGroup.GroupBy(r => r.id).ToList();

            var kept = rows.Where(r => r.First().dismissed == 0).ToList();

            var listened = 0;
            var correct = 0;

            foreach (var row in kept)
            {
                var folded = TurkishText.NormalizeForSearch(row.First().quote);

                var verdict = row
                    .Where(r => r.verdict_quote is not null && r.verdict_quote == folded)
                    .OrderByDescending(r => r.verdict_at, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (verdict is null) continue;

                listened++;
                if (verdict.verdict_value == (long)VerdictValue.Correct) correct++;
            }

            var name = kindGroup.Key.is_flag != 0 && int.TryParse(kindGroup.Key.kind, out var number)
                ? ((FlagKind)number).ToString()
                : kindGroup.Key.kind;

            summaries.Add(new PatternSummary(
                name,
                kindGroup.Key.source,
                kept.Count,
                kept.Select(r => r.First().call_id).Distinct().Count(),
                kept.Count(r => r.First().low_confidence != 0),
                rows.Count - kept.Count,
                listened,
                correct,
                kept.Count == 0
                    ? null
                    : kept.Max(r => ParseIso(r.First().started_at))));
        }

        return summaries;
    }

    /// <summary>One quote behind a "Kalıplar" row, playable.</summary>
    /// <param name="ByMe">Null for a ledger flag: the flag table has never recorded which stream its quote came from.</param>
    public sealed record PatternQuote(
        long Id,
        string Kind,
        string Source,
        long CallId,
        DateTimeOffset CallStartedAt,
        int StartMs,
        bool? ByMe,
        bool LowConfidence,
        bool Dismissed,
        string Quote,
        string Summary,
        DateTimeOffset? DecidedAt);

    /// <summary>
    /// The quotes behind one <see cref="PatternSummary"/> row, newest conversation first.
    ///
    /// The kind decides which table is read: a <see cref="FlagKind"/> name is the ledger's, and
    /// anything else is a tactic label. Both sources can be called "pipeline", so the source
    /// alone could not tell them apart.
    /// </summary>
    public IReadOnlyList<PatternQuote> PatternRows(
        long contactId, string kind, string source, bool includeDismissed = false, int limit = 200)
    {
        using var connection = Open();

        if (Enum.TryParse<FlagKind>(kind, ignoreCase: false, out var flagKind))
        {
            return [.. connection
                .Query<PatternQuoteRaw>(
                    """
                    SELECT f.id, f.call_id, c.started_at, f.quote_start_ms AS start_ms,
                           NULL AS by_me, f.low_confidence, f.dismissed_by_user AS dismissed,
                           f.quote, f.summary, f.decided_at
                      FROM flag f
                      JOIN call c ON c.id = f.call_id
                     WHERE f.contact_id = @contactId AND f.kind = @kind AND f.source = @source
                       AND (@includeDismissed = 1 OR f.dismissed_by_user = 0)
                     ORDER BY c.started_at DESC, f.quote_start_ms
                     LIMIT @limit;
                    """,
                    new
                    {
                        contactId,
                        kind = (int)flagKind,
                        source,
                        includeDismissed = includeDismissed ? 1 : 0,
                        limit,
                    })
                .Select(r => r.ToModel(kind, source))];
        }

        return [.. connection
            .Query<PatternQuoteRaw>(
                """
                SELECT t.id, t.call_id, c.started_at, t.quote_start_ms AS start_ms,
                       t.by_me, t.low_confidence, t.dismissed_by_user AS dismissed,
                       t.quote, '' AS summary, NULL AS decided_at
                  FROM tactic_evidence t
                  JOIN call c ON c.id = t.call_id
                 WHERE t.contact_id = @contactId AND t.tactic = @kind AND t.source = @source
                   AND (@includeDismissed = 1 OR t.dismissed_by_user = 0)
                 ORDER BY c.started_at DESC, t.quote_start_ms
                 LIMIT @limit;
                """,
                new { contactId, kind, source, includeDismissed = includeDismissed ? 1 : 0, limit })
            .Select(r => r.ToModel(kind, source))];
    }

    /// <summary>One value on a figure's journey, with the moment it was said.</summary>
    public sealed record FigureStop(
        string Value,
        decimal? NumericValue,
        string? Unit,
        long CallId,
        DateTimeOffset CallStartedAt,
        int StartMs,
        string Quote,
        bool LowConfidence);

    /// <summary>A subject whose stated value moved, and every value it has held.</summary>
    public sealed record FigureJourneyRow(
        string Entity, string Attribute, int DistinctValues, IReadOnlyList<FigureStop> Stops);

    /// <summary>
    /// Every subject this person has given more than one answer about, with all of the answers.
    ///
    /// Grouped exactly the way <see cref="Analysis.DeterministicChecks.ChangedAmounts"/> groups
    /// its claims — by folded entity and attribute, over the other party's lines only — so the
    /// card's journey and the ledger's "değişen rakam" row can never disagree about what
    /// changed. Two differences, both deliberate: non-numeric values count too ("cuma" becoming
    /// "gelecek hafta" is the same movement and the same evidence), and low-confidence lines are
    /// listed rather than dropped, carrying their mark so the screen can grey them. A flag would
    /// be an accusation and is held to the stricter rule; this is a list of what was said.
    /// </summary>
    public IReadOnlyList<FigureJourneyRow> FigureJourney(long contactId)
    {
        using var connection = Open();

        var rows = connection.Query<FigureRaw>(
            """
            SELECT cl.entity, cl.attribute, cl.value, cl.numeric_value, cl.unit,
                   cl.call_id, c.started_at, cl.quote_start_ms AS start_ms,
                   cl.quote, cl.low_confidence, cl.id
              FROM claim cl
              JOIN call c ON c.id = cl.call_id
             WHERE cl.contact_id = @contactId AND cl.by_me = 0
             ORDER BY c.started_at, cl.quote_start_ms, cl.id;
            """,
            new { contactId }).ToList();

        List<FigureJourneyRow> journeys = [];

        foreach (var group in rows.GroupBy(r => (
                     Entity: TurkishText.NormalizeForSearch(r.entity),
                     Attribute: TurkishText.NormalizeForSearch(r.attribute))))
        {
            var stops = group.Select(r => r.ToStop()).ToList();

            // What counts as "a different answer": the number when both are numbers, the folded
            // words otherwise — the same comparison the contradiction check makes.
            var distinct = stops
                .Select(s => s.NumericValue is { } number
                    ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : TurkishText.NormalizeForSearch(s.Value))
                .Where(key => key.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count();

            if (distinct < 2) continue;

            journeys.Add(new FigureJourneyRow(
                group.First().entity, group.First().attribute, distinct, stops));
        }

        return [.. journeys.OrderByDescending(j => j.Stops[^1].CallStartedAt)];
    }

    /// <summary>One thing the other person said, dated and playable.</summary>
    public sealed record OwnWord(
        bool IsPromise,
        string Subject,
        string Attribute,
        string Value,
        long CallId,
        DateTimeOffset CallStartedAt,
        int StartMs,
        string Quote,
        bool LowConfidence,
        CommitmentStatus? Status,
        DateOnly? Deadline);

    /// <summary>The rows about one subject, newest first.</summary>
    public sealed record OwnWordsGroup(string Subject, IReadOnlyList<OwnWord> Words);

    /// <summary>
    /// "Elindeki kayıtlar": what this person has said, in their own words, grouped by subject.
    ///
    /// This is the evidence-side answer to "give me arguments I can use against them", which is
    /// a prompt this product does not write. What it can honestly hand somebody before a
    /// conversation is the record: the claims and the promises the other party made, dated, with
    /// the sentence and the millisecond that plays it. No model reads this and none writes it.
    ///
    /// The other party's lines only — the user's own words are not evidence about them — and
    /// dismissed promises are left out, because the user has already said those were not
    /// promises. The user's own rewording and their postponed date win where they set one, the
    /// same way every other promise surface reads them.
    /// </summary>
    public IReadOnlyList<OwnWordsGroup> OwnWords(long contactId, int limit = 200)
    {
        using var connection = Open();

        var rows = connection.Query<OwnWordRaw>(
            """
            SELECT 0 AS is_promise, cl.entity AS subject, cl.attribute AS attribute,
                   cl.value AS value, cl.call_id AS call_id, c.started_at AS started_at,
                   cl.quote_start_ms AS start_ms, cl.quote AS quote,
                   cl.low_confidence AS low_confidence, NULL AS status, NULL AS deadline
              FROM claim cl
              JOIN call c ON c.id = cl.call_id
             WHERE cl.contact_id = @contactId AND cl.by_me = 0

            UNION ALL

            SELECT 1, COALESCE(NULLIF(TRIM(cm.user_obligation), ''), cm.obligation), '', '',
                   cm.call_id, c.started_at, cm.quote_start_ms, cm.quote, 0,
                   cm.status, COALESCE(cm.user_deadline_date, cm.deadline_date)
              FROM commitment cm
              JOIN call c ON c.id = cm.call_id
             WHERE cm.contact_id = @contactId AND cm.by_me = 0 AND cm.dismissed_by_user = 0

             ORDER BY started_at DESC, start_ms DESC
             LIMIT @limit;
            """,
            new { contactId, limit }).ToList();

        return [.. rows
            .Select(r => r.ToModel())
            .GroupBy(w => TurkishText.NormalizeForSearch(w.Subject))
            .Select(g => new OwnWordsGroup(g.First().Subject, [.. g]))
            .OrderByDescending(g => g.Words[0].CallStartedAt)];
    }

    /// <summary>
    /// One conversation's questions, counted.
    /// </summary>
    /// <param name="Measured">
    /// True when this call has any question rows at all. False means nobody looked, which is a
    /// different thing from "nothing was asked" and must never be averaged in as a zero.
    /// </param>
    /// <param name="Asked">Questions the user put to this person.</param>
    public sealed record SpeechActCounts(
        long CallId,
        DateTimeOffset CallStartedAt,
        bool Measured,
        int Asked,
        int Answered,
        int Partial,
        int Evaded,
        int Deflected)
    {
        /// <summary>The two statuses that mean the question got no real answer.</summary>
        public int Unanswered => Evaded + Deflected;
    }

    /// <summary>Every call of a contact, so the honest denominator is in the same object as the counts.</summary>
    public sealed record SpeechActSummary(IReadOnlyList<SpeechActCounts> Calls)
    {
        /// <summary>The N of "N/M görüşmede ölçüldü".</summary>
        public int CallsMeasured => Calls.Count(c => c.Measured);

        /// <summary>The M: every conversation with this person, measured or not.</summary>
        public int CallsTotal => Calls.Count;
    }

    /// <summary>
    /// The question counts for one person, one row per conversation — including the
    /// conversations nobody counted, which is the whole point.
    ///
    /// Every call is returned, not only the ones with rows, because the honest sentence on the
    /// card is "N/M görüşmede ölçüldü". Older calls were analysed before questions were kept,
    /// and a rate computed over the calls that happen to have rows would silently claim to speak
    /// for the whole history.
    ///
    /// Only questions the USER asked are counted: "was this person answering you" is a question
    /// about their answers, and their own questions belong to a different measurement.
    /// </summary>
    public SpeechActSummary SpeechActs(long contactId)
    {
        using var connection = Open();

        var rows = connection.Query<SpeechActCountRaw>(
            """
            SELECT c.id AS call_id, c.started_at AS started_at,
                   EXISTS (SELECT 1 FROM speech_act s WHERE s.call_id = c.id) AS measured,
                   COUNT(a.id) AS asked,
                   COALESCE(SUM(CASE WHEN a.answer_status = @answered     THEN 1 ELSE 0 END), 0) AS answered,
                   COALESCE(SUM(CASE WHEN a.answer_status = @partial      THEN 1 ELSE 0 END), 0) AS partial,
                   COALESCE(SUM(CASE WHEN a.answer_status = @evasive      THEN 1 ELSE 0 END), 0) AS evaded,
                   COALESCE(SUM(CASE WHEN a.answer_status = @deflected    THEN 1 ELSE 0 END), 0) AS deflected
              FROM call c
              LEFT JOIN speech_act a
                     ON a.call_id = c.id AND a.kind = @kind AND a.by_me = 1
             WHERE c.contact_id = @contactId
             GROUP BY c.id
             ORDER BY c.started_at, c.id;
            """,
            new
            {
                contactId,
                kind = SpeechAct.Kinds.Question,
                answered = SpeechAct.Statuses.Answered,
                partial = SpeechAct.Statuses.Partial,
                evasive = SpeechAct.Statuses.Evasive,
                deflected = SpeechAct.Statuses.Deflected,
            });

        return new SpeechActSummary([.. rows.Select(r => r.ToModel())]);
    }

    /// <summary>One conversation as the "Gidişat" series reads it.</summary>
    /// <param name="Talk">Null when this call has no stored counts: not measured, never zero.</param>
    public sealed record ContactCallPoint(
        long CallId,
        DateTimeOffset StartedAt,
        CallDirection Direction,
        Analysis.TalkStats? Talk);

    /// <summary>
    /// A person's calls in time order, each with its talk statistics when they were counted.
    ///
    /// One SELECT for the whole history, like <see cref="HabitSeries"/>: the card draws months
    /// and a recent-versus-previous pair, and doing that with a query per call would make
    /// opening a well-used contact a hundred round trips.
    ///
    /// A call with no stored counts comes back with a null <see cref="ContactCallPoint.Talk"/>
    /// rather than an empty one. The difference is the whole honesty of the series: "not
    /// measured" is not a talk share of zero, and the trend counts its own denominator.
    /// </summary>
    public IReadOnlyList<ContactCallPoint> ContactSeries(long contactId)
    {
        using var connection = Open();

        return [.. connection
            .Query<ContactSeriesRaw>(
                """
                SELECT c.id AS call_id, c.started_at, c.direction, h.json
                  FROM call c
                  LEFT JOIN speech_habit h ON h.call_id = c.id
                 WHERE c.contact_id = @contactId
                 ORDER BY c.started_at, c.id;
                """,
                new { contactId })
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// Which stored transcript each of a person's conversations shows right now, call id →
    /// version id (null where the call never recorded one).
    ///
    /// The second half of a contact reading's fingerprint. A reading is not only OF a set of
    /// conversations, it is of the words those conversations showed while it was being written;
    /// transcribing one of them again moves the anchors every line of it hangs on, and the card
    /// went on printing a current-looking stamp over quotes whose moment had shifted.
    /// </summary>
    public IReadOnlyDictionary<long, long?> TranscriptVersionsOf(long contactId)
    {
        using var connection = Open();

        return connection
            .Query<(long call_id, long? version)>(
                "SELECT id, transcript_version_id FROM call WHERE contact_id = @contactId;",
                new { contactId })
            .ToDictionary(r => r.call_id, r => r.version);
    }

    // ---- action suggestions ------------------------------------------------
    //
    // Machine-owned rows. Routing into the user's spaces happens only via their click, and a
    // hidden suggestion is a judgement the user made once — re-runs must respect it.

    public long InsertAction(ActionItem action)
    {
        using var connection = Open();

        return connection.ExecuteScalar<long>(
            """
            INSERT INTO action_item (call_id, contact_id, action, reason, kind, quote,
                                     quote_start_ms, quote_is_me, deadline_raw, deadline_date,
                                     status, routed_note, model_used, created_at,
                                     transcript_version_id)
            VALUES (@CallId, @ContactId, @Action, @Reason, @Kind, @Quote,
                    @QuoteStartMs, @QuoteIsMe, @DeadlineRaw, @DeadlineDate,
                    @Status, @RoutedNote, @ModelUsed, @CreatedAt,
                    (SELECT transcript_version_id FROM call WHERE id = @CallId))
            RETURNING id;
            """,
            new
            {
                action.CallId,
                action.ContactId,
                action.Action,
                action.Reason,
                action.Kind,
                action.Quote,
                action.QuoteStartMs,
                QuoteIsMe = action.QuoteIsMe ? 1 : 0,
                action.DeadlineRaw,
                DeadlineDate = action.DeadlineDate?.ToString("yyyy-MM-dd"),
                Status = (int)action.Status,
                action.RoutedNote,
                action.ModelUsed,
                CreatedAt = Iso(action.CreatedAt == default ? DateTimeOffset.UtcNow : action.CreatedAt),
            });
    }

    /// <summary>One conversation's suggestions, open first, then in spoken order.</summary>
    public IReadOnlyList<ActionItem> ActionsOf(long callId, bool includeClosed = true)
    {
        using var connection = Open();

        var sql = includeClosed
            ? "SELECT * FROM action_item WHERE call_id = @callId ORDER BY status, quote_start_ms, id;"
            : "SELECT * FROM action_item WHERE call_id = @callId AND status = 0 ORDER BY quote_start_ms, id;";

        return [.. connection.Query<ActionRow>(sql, new { callId }).Select(r => r.ToModel())];
    }

    /// <summary>
    /// The suggestions on many conversations at once, each list in the order
    /// <see cref="ActionsOf(long, bool)"/> would have returned it.
    ///
    /// For screens that show a person's whole history: the contact window's flow used to ask per
    /// conversation, which is one connection and one pragma batch per row of a timeline that
    /// exists precisely because the person has a lot of them.
    /// </summary>
    public IReadOnlyDictionary<long, IReadOnlyList<ActionItem>> ActionsOf(
        IEnumerable<long> callIds, bool includeClosed = true)
    {
        var ids = callIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, IReadOnlyList<ActionItem>>();

        using var connection = Open();

        var sql = includeClosed
            ? "SELECT * FROM action_item WHERE call_id IN @ids ORDER BY status, quote_start_ms, id;"
            : "SELECT * FROM action_item WHERE call_id IN @ids AND status = 0 ORDER BY quote_start_ms, id;";

        // Grouping keeps the order the rows arrived in, which is the order the single-call
        // version promises.
        return connection.Query<ActionRow>(sql, new { ids })
            .GroupBy(r => r.call_id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ActionItem>)[.. g.Select(r => r.ToModel())]);
    }

    /// <summary>
    /// The home screen's list: open suggestions whose deadline has arrived, plus recent
    /// undated ones — capped, newest conversations first.
    /// </summary>
    public IReadOnlyList<(ActionItem Action, string ContactName)> OpenActions(
        DateOnly today, int recentDays = 3, int limit = 5)
    {
        using var connection = Open();

        var rows = connection.Query<ActionRow, string?, (ActionRow, string?)>(
            """
            SELECT a.*, ct.name
            FROM action_item a
            JOIN call c          ON c.id = a.call_id
            LEFT JOIN contact ct ON ct.id = a.contact_id
            WHERE a.status = 0
              AND (
                    (a.deadline_date IS NOT NULL AND a.deadline_date <= @today)
                 OR (a.deadline_date IS NULL AND c.started_at >= @since)
              )
            ORDER BY a.deadline_date IS NULL, a.deadline_date, c.started_at DESC
            LIMIT @limit;
            """,
            (action, name) => (action, name),
            new
            {
                today = today.ToString("yyyy-MM-dd"),
                since = today.AddDays(-recentDays).ToString("yyyy-MM-dd"),
                limit,
            },
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "İsimsiz görüşme"))];
    }

    public void SetActionStatus(long actionId, ActionStatus status, string? routedNote = null)
    {
        using var connection = Open();

        // Stamped when it is a ruling; cleared when the row is reopened, because "open" is the
        // absence of one.
        connection.Execute(
            "UPDATE action_item SET status = @status, routed_note = @routedNote, decided_at = @decidedAt WHERE id = @actionId;",
            new
            {
                actionId,
                status = (int)status,
                routedNote,
                decidedAt = status == ActionStatus.Open ? null : Iso(DateTimeOffset.UtcNow),
            });
    }

    /// <summary>Hidden suggestions' identities: (folded action, folded quote) — never resurrected.</summary>
    public IReadOnlySet<(string Action, string Quote)> HiddenActionKeys(long callId)
    {
        using var connection = Open();

        return connection
            .Query<(string Action, string Quote)>(
                "SELECT action, quote FROM action_item WHERE call_id = @callId AND status = 2;",
                new { callId })
            .Select(r => (
                Text.TurkishText.NormalizeForSearch(r.Action),
                Text.TurkishText.NormalizeForSearch(r.Quote)))
            .ToHashSet();
    }

    /// <summary>A re-run replaces open suggestions only: done, hidden and routed rows are the
    /// user's history with the list and stay.</summary>
    public void ClearOpenActions(long callId)
    {
        using var connection = Open();
        connection.Execute(
            "DELETE FROM action_item WHERE call_id = @callId AND status = 0;", new { callId });
    }

    // ---- the model's stored reading -----------------------------------------

    /// <summary>Saves the reading for a conversation, replacing any earlier one.</summary>
    public void SaveReading(long callId, string json, string? modelUsed)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO reading_note (call_id, json, model_used, created_at, transcript_version_id)
            VALUES (@callId, @json, @modelUsed, @now,
                    (SELECT transcript_version_id FROM call WHERE id = @callId))
            ON CONFLICT(call_id) DO UPDATE SET
                json = excluded.json, model_used = excluded.model_used, created_at = excluded.created_at,
                transcript_version_id = excluded.transcript_version_id;
            """,
            new { callId, json, modelUsed, now = Iso(DateTimeOffset.UtcNow) });
    }

    public (string Json, string? ModelUsed, DateTimeOffset CreatedAt)? GetReading(long callId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<(string Json, string? ModelUsed, string CreatedAt)>(
            "SELECT json, model_used, created_at FROM reading_note WHERE call_id = @callId;",
            new { callId });

        return row == default ? null : (row.Json, row.ModelUsed, ParseIso(row.CreatedAt));
    }

    public void DeleteReading(long callId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM reading_note WHERE call_id = @callId;", new { callId });
    }

    // ---- the opt-in deception assessment -------------------------------------
    //
    // Same contract as the reading: one row per call, enforced shape in, dead end after.

    public void SaveDeception(long callId, string json, string? modelUsed)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO deception_note (call_id, json, model_used, created_at, transcript_version_id)
            VALUES (@callId, @json, @modelUsed, @now,
                    (SELECT transcript_version_id FROM call WHERE id = @callId))
            ON CONFLICT(call_id) DO UPDATE SET
                json = excluded.json, model_used = excluded.model_used, created_at = excluded.created_at,
                transcript_version_id = excluded.transcript_version_id;
            """,
            new { callId, json, modelUsed, now = Iso(DateTimeOffset.UtcNow) });
    }

    public (string Json, string? ModelUsed, DateTimeOffset CreatedAt)? GetDeception(long callId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<(string Json, string? ModelUsed, string CreatedAt)>(
            "SELECT json, model_used, created_at FROM deception_note WHERE call_id = @callId;",
            new { callId });

        return row == default ? null : (row.Json, row.ModelUsed, ParseIso(row.CreatedAt));
    }

    public void DeleteDeception(long callId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM deception_note WHERE call_id = @callId;", new { callId });
    }

    // ---- the model's stored reading of a PERSON -------------------------------
    //
    // The contact card's opt-in bottom panel. Same contract as the two above and one more: this
    // one is HISTORY. A reading is dated and the previous one is kept, because the feature ships
    // with its own measurement — "did the user disagree" — and a table that overwrote itself
    // could only ever answer that about the last person.
    //
    // Nothing joins on it and NO PROMPT IS EVER SHOWN A ROW. contact_profile, which holds what
    // the user typed about the same person, is not touched by any of this.

    /// <summary>One stored reading, exactly as it was written.</summary>
    /// <param name="UserVerdict">The user's own column: 1 for [Katılmıyorum], null while unsaid.</param>
    public sealed record StoredContactReading(
        long Id,
        long ContactId,
        string Json,
        string? ModelUsed,
        int CallsCovered,
        long? LatestCallId,
        string InputHash,
        int ExcerptCount,
        int RejectedCount,
        int? UserVerdict,
        DateTimeOffset CreatedAt);

    /// <summary>Adds a reading. Never replaces one: the older readings are the measurement.</summary>
    public long SaveContactReading(
        long contactId, string json, string? modelUsed, int callsCovered, long? latestCallId,
        string inputHash, int excerptCount, int rejectedCount)
    {
        using var connection = Open();

        return connection.ExecuteScalar<long>(
            """
            INSERT INTO contact_reading
                (contact_id, json, model_used, calls_covered, latest_call_id,
                 input_hash, excerpt_count, rejected_count, created_at)
            VALUES
                (@contactId, @json, @modelUsed, @callsCovered, @latestCallId,
                 @inputHash, @excerptCount, @rejectedCount, @now)
            RETURNING id;
            """,
            new
            {
                contactId, json, modelUsed, callsCovered, latestCallId,
                inputHash, excerptCount, rejectedCount, now = Iso(DateTimeOffset.UtcNow),
            });
    }

    /// <summary>The newest reading of one person, or null when none was ever produced.</summary>
    public StoredContactReading? LatestContactReading(long contactId)
    {
        using var connection = Open();

        return connection.QueryFirstOrDefault<ContactReadingRow>(
            """
            SELECT * FROM contact_reading
             WHERE contact_id = @contactId
             ORDER BY created_at DESC, id DESC
             LIMIT 1;
            """,
            new { contactId })?.ToModel();
    }

    /// <summary>
    /// USER DATA. Nothing in the analysis writes this column and no re-run clears it — which is
    /// what makes the disable-after-three-disagreements rule an honest measurement rather than
    /// the feature grading itself.
    /// </summary>
    public void SetContactReadingVerdict(long readingId, int? verdict)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE contact_reading SET user_verdict = @verdict WHERE id = @readingId;",
            new { readingId, verdict });
    }

    /// <summary>
    /// The newest reading of each of the last few people, newest first — the acceptance rule's
    /// input. One row per person, because three disagreements about one contact is one opinion.
    /// </summary>
    public IReadOnlyList<int?> RecentContactReadingVerdicts(int limit = 3)
    {
        using var connection = Open();

        return
        [
            .. connection.Query<int?>(
                """
                SELECT r.user_verdict
                  FROM contact_reading r
                 WHERE r.id = (SELECT x.id FROM contact_reading x
                                WHERE x.contact_id = r.contact_id
                                ORDER BY x.created_at DESC, x.id DESC LIMIT 1)
                 ORDER BY r.created_at DESC, r.id DESC
                 LIMIT @limit;
                """,
                new { limit }),
        ];
    }

    // ---- what was asked, and what came back ---------------------------------
    //
    // The two Sor surfaces used to answer a question, pay for the request and drop the answer on
    // the way out of the window. These rows are what makes reopening a panel free.
    //
    // Same contract as the reading and the assessment: signed by a model, dated, a dead end. The
    // one difference is who may remove it — a stored answer is the user's own material and comes
    // off with [Kaldır], where a reading is a note the analysis wrote.

    /// <summary>One stored exchange, exactly as it was answered.</summary>
    /// <param name="CallId">Null when the question was asked of the whole archive.</param>
    /// <param name="Citations">Serialised; read with <see cref="Analysis.StoredExcerpts"/>.</param>
    /// <param name="TranscriptVersionId">
    /// Of the transcript the quotes came from, for a call-scoped row. Null on an archive-wide one,
    /// which draws on many texts at once and therefore has no single one to have gone stale.
    /// </param>
    public sealed record StoredAskExchange(
        long Id,
        long? CallId,
        long? ContactId,
        DateTimeOffset? Since,
        DateTimeOffset? Until,
        string Question,
        string Answer,
        string Citations,
        bool Insufficient,
        string? ModelUsed,
        long? TranscriptVersionId,
        DateTimeOffset AskedAt);

    /// <summary>
    /// Files one answered question. Never replaces one: asking the same thing again on a newer
    /// transcript is a second answer, and which of the two the user believes is their business.
    ///
    /// A call-scoped row is filed under the transcript the call currently shows, by the same
    /// subquery <see cref="SaveReading"/> uses, so a re-transcription can be told from a re-ask.
    /// </summary>
    public long SaveAskExchange(
        long? callId,
        long? contactId,
        string question,
        string answer,
        string citations,
        bool insufficient,
        string? modelUsed,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null)
    {
        using var connection = Open();

        return connection.ExecuteScalar<long>(
            """
            INSERT INTO ask_exchange
                (call_id, contact_id, since_at, until_at, question, answer, citations,
                 insufficient, model_used, transcript_version_id, asked_at)
            VALUES
                (@callId, @contactId, @since, @until, @question, @answer, @citations,
                 @insufficient, @modelUsed,
                 (SELECT transcript_version_id FROM call WHERE id = @callId),
                 @now)
            RETURNING id;
            """,
            new
            {
                callId, contactId, question, answer, citations,
                insufficient = insufficient ? 1 : 0, modelUsed,
                since = since is { } s ? Iso(s) : null,
                until = until is { } u ? Iso(u) : null,
                now = Iso(DateTimeOffset.UtcNow),
            });
    }

    /// <summary>One conversation's questions, oldest first — the order they were asked in.</summary>
    public IReadOnlyList<StoredAskExchange> AskExchangesOf(long callId)
    {
        using var connection = Open();

        return [.. connection.Query<AskExchangeRow>(
            "SELECT * FROM ask_exchange WHERE call_id = @callId ORDER BY asked_at, id;",
            new { callId }).Select(r => r.ToModel())];
    }

    /// <summary>
    /// The questions asked of the archive rather than of one conversation, newest first.
    ///
    /// Deliberately not "every exchange": the shell's panel and a call window's panel answer
    /// different questions, and a call's own exchanges appearing in the archive-wide list would
    /// put an answer about one conversation under a screen that ranges over all of them.
    /// </summary>
    public IReadOnlyList<StoredAskExchange> ArchiveAskExchanges(int limit = 100)
    {
        using var connection = Open();

        return [.. connection.Query<AskExchangeRow>(
            "SELECT * FROM ask_exchange WHERE call_id IS NULL ORDER BY asked_at DESC, id DESC LIMIT @limit;",
            new { limit }).Select(r => r.ToModel())];
    }

    /// <summary>Removes one exchange. The user's own material, so [Kaldır] and not [Reddet].</summary>
    public void DeleteAskExchange(long id)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM ask_exchange WHERE id = @id;", new { id });
    }

    /// <summary>
    /// What one consistency run left behind: the justified warning, and the balancing
    /// observations beside it.
    /// </summary>
    /// <param name="Note">The warning, or null when the evidence earned none.</param>
    /// <param name="Observations">
    /// "These points held up". NULL in the database — null here — means the run's observations
    /// were never kept, which is not the same answer as an empty list: one says nothing was
    /// recorded, the other says the run found nothing in the person's favour.
    /// </param>
    public sealed record StoredConsistency(
        string? Note, IReadOnlyList<string>? Observations, string? ModelUsed, DateTimeOffset CreatedAt);

    /// <summary>
    /// Saves what one consistency run produced besides its findings (one row per call).
    ///
    /// Both halves together, because they are one run: the accusing sentence and the balancing
    /// list were paid for by the same request, and storing only the first is how a reopened
    /// window came to show a person's contradictions with nothing on the other side.
    ///
    /// <paramref name="observations"/> left null writes SQL NULL — "not recorded". Callers that
    /// ran the check pass the list they got, empty included.
    /// </summary>
    public void SaveConsistencyNote(
        long callId, string? note, string? modelUsed, IReadOnlyList<string>? observations = null)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO consistency_note (call_id, note, model_used, created_at, transcript_version_id, observations)
            VALUES (@callId, @note, @modelUsed, @now,
                    (SELECT transcript_version_id FROM call WHERE id = @callId), @observations)
            ON CONFLICT(call_id) DO UPDATE SET
                note = excluded.note, model_used = excluded.model_used, created_at = excluded.created_at,
                transcript_version_id = excluded.transcript_version_id,
                observations = excluded.observations;
            """,
            new
            {
                callId,
                note = note ?? "",
                modelUsed,
                now = Iso(DateTimeOffset.UtcNow),
                observations = observations is null ? null : JsonSerializer.Serialize(observations),
            });
    }

    /// <summary>
    /// The stored warning note and observations, with which model wrote them and when. Null when
    /// the call has never had a run write anything down.
    ///
    /// An empty note comes back as null: the column cannot hold NULL, so "the run earned no
    /// warning" is stored as the empty string, and the screen must not print an empty box.
    /// </summary>
    public StoredConsistency? GetConsistencyNote(long callId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<ConsistencyNoteRow>(
            "SELECT note, model_used, created_at, observations FROM consistency_note WHERE call_id = @callId;",
            new { callId });

        return row is null ? null : row.ToModel();
    }

    public long InsertCommitment(Commitment commitment)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO commitment (call_id, contact_id, by_me, quote, quote_start_ms, obligation,
                                    deadline_raw, deadline_date, amount, currency, is_conditional,
                                    status, fulfilled_by_call_id, dismissed_by_user, created_at)
            VALUES (@CallId, @ContactId, @ByMe, @Quote, @QuoteStartMs, @Obligation,
                    @DeadlineRaw, @DeadlineDate, @Amount, @Currency, @IsConditional,
                    @Status, @FulfilledByCallId, 0, @CreatedAt)
            RETURNING id;
            """,
            new
            {
                commitment.CallId,
                commitment.ContactId,
                ByMe = commitment.ByMe ? 1 : 0,
                commitment.Quote,
                commitment.QuoteStartMs,
                commitment.Obligation,
                commitment.DeadlineRaw,
                DeadlineDate = commitment.DeadlineDate?.ToString("yyyy-MM-dd"),
                Amount = commitment.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                commitment.Currency,
                IsConditional = commitment.IsConditional ? 1 : 0,
                Status = (int)commitment.Status,
                commitment.FulfilledByCallId,
                CreatedAt = Iso(commitment.CreatedAt ?? DateTimeOffset.UtcNow),
            });
    }

    public long InsertClaim(Claim claim)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO claim (call_id, contact_id, by_me, quote, quote_start_ms,
                               entity, attribute, value, numeric_value, unit, low_confidence)
            VALUES (@CallId, @ContactId, @ByMe, @Quote, @QuoteStartMs,
                    @Entity, @Attribute, @Value, @NumericValue, @Unit, @LowConfidence)
            RETURNING id;
            """,
            new
            {
                claim.CallId,
                claim.ContactId,
                ByMe = claim.ByMe ? 1 : 0,
                claim.Quote,
                claim.QuoteStartMs,
                Entity = TurkishText.NormalizeForSearch(claim.Entity),
                Attribute = TurkishText.NormalizeForSearch(claim.Attribute),
                claim.Value,
                NumericValue = claim.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                claim.Unit,
                LowConfidence = claim.LowConfidence ? 1 : 0,
            });
    }

    public long InsertFlag(Flag flag)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO flag (call_id, contact_id, kind, summary, quote, quote_start_ms,
                              counter_quote, counter_call_id, counter_quote_start_ms,
                              low_confidence, is_heuristic, dismissed_by_user,
                              source, confidence, created_at, transcript_version_id)
            VALUES (@CallId, @ContactId, @Kind, @Summary, @Quote, @QuoteStartMs,
                    @CounterQuote, @CounterCallId, @CounterQuoteStartMs,
                    @LowConfidence, @IsHeuristic, 0, @Source, @Confidence, @CreatedAt,
                    -- Which text the quote was located in, taken from the call rather than from
                    -- the caller: the writer already verified the words against this transcript,
                    -- and a finding that cannot say which text it came out of can never be
                    -- labelled when that text is replaced.
                    (SELECT transcript_version_id FROM call WHERE id = @CallId))
            RETURNING id;
            """,
            new
            {
                flag.CallId,
                flag.ContactId,
                Kind = (int)flag.Kind,
                flag.Summary,
                flag.Quote,
                flag.QuoteStartMs,
                flag.CounterQuote,
                flag.CounterCallId,
                flag.CounterQuoteStartMs,
                LowConfidence = flag.LowConfidence ? 1 : 0,
                IsHeuristic = flag.IsHeuristic ? 1 : 0,
                flag.Source,
                flag.Confidence,
                CreatedAt = Iso(flag.CreatedAt == default ? DateTimeOffset.UtcNow : flag.CreatedAt),
            });
    }

    /// <summary>
    /// Undismissed flags for a contact, newest first. Dismissals are permanent: without that,
    /// false positives pile up until the ledger is worthless and the user stops reading it.
    /// </summary>
    public IReadOnlyList<Flag> GetFlags(long contactId, bool includeDismissed = false)
    {
        using var connection = Open();

        var sql = includeDismissed
            ? "SELECT * FROM flag WHERE contact_id = @contactId ORDER BY created_at DESC;"
            : "SELECT * FROM flag WHERE contact_id = @contactId AND dismissed_by_user = 0 ORDER BY created_at DESC;";

        return [.. connection.Query<FlagRow>(sql, new { contactId }).Select(r => r.ToModel())];
    }

    public void DismissFlag(long flagId)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE flag SET dismissed_by_user = 1, decided_at = @now WHERE id = @flagId;",
            new { flagId, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>Several at once — the ledger's select mode.</summary>
    public int DismissFlags(IEnumerable<long> flagIds)
    {
        var ids = flagIds.Distinct().ToList();
        if (ids.Count == 0) return 0;

        using var connection = Open();
        return connection.Execute(
            "UPDATE flag SET dismissed_by_user = 1, decided_at = @now WHERE id IN @ids;",
            new { ids, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>Takes a dismissal back; the row was a tombstone, never gone.</summary>
    public void RestoreFlag(long flagId)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE flag SET dismissed_by_user = 0, decided_at = @now WHERE id = @flagId;",
            new { flagId, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>The findings the user turned down, newest ruling first — the "Reddedilenler" chip.</summary>
    public IReadOnlyList<(Flag Flag, string ContactName)> DismissedFlags(int limit = 500)
    {
        using var connection = Open();

        var rows = connection.Query<FlagRow, string?, (FlagRow, string?)>(
            """
            SELECT f.*, ct.name
            FROM flag f
            LEFT JOIN contact ct ON ct.id = f.contact_id
            WHERE f.dismissed_by_user = 1
            ORDER BY f.decided_at DESC, f.id DESC
            LIMIT @limit;
            """,
            (flag, name) => (flag, name),
            new { limit },
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "Bilinmeyen"))];
    }

    public IReadOnlyList<Commitment> GetOpenCommitments(long contactId)
    {
        using var connection = Open();
        return [.. connection.Query<CommitmentRow>(
            """
            SELECT * FROM commitment
            WHERE contact_id = @contactId AND status = 0 AND dismissed_by_user = 0
            ORDER BY COALESCE(user_deadline_date, deadline_date, '9999-12-31');
            """,
            new { contactId })
            .Select(r => r.ToModel())];
    }

    public IReadOnlyList<Claim> GetClaims(long contactId, string entity, string attribute)
    {
        using var connection = Open();
        return [.. connection.Query<ClaimRow>(
            """
            SELECT * FROM claim
            WHERE contact_id = @contactId AND entity = @entity AND attribute = @attribute
            ORDER BY id;
            """,
            new
            {
                contactId,
                entity = TurkishText.NormalizeForSearch(entity),
                attribute = TurkishText.NormalizeForSearch(attribute),
            })
            .Select(r => r.ToModel())];
    }

    /// <summary>Every claim recorded for a contact, used for cross-call contradiction checks.</summary>
    public IReadOnlyList<Claim> GetAllClaims(long contactId)
    {
        using var connection = Open();
        return [.. connection.Query<ClaimRow>(
            "SELECT * FROM claim WHERE contact_id = @contactId ORDER BY id;", new { contactId })
            .Select(r => r.ToModel())];
    }

    public void SaveSummary(CallSummary summary)
    {
        using var connection = Open();
        connection.Execute(
            """
            INSERT INTO call_summary (call_id, summary, action_items, model_used, created_at, transcript_version_id)
            VALUES (@CallId, @Summary, @ActionItems, @ModelUsed, @CreatedAt,
                    (SELECT transcript_version_id FROM call WHERE id = @CallId))
            ON CONFLICT(call_id) DO UPDATE SET
                summary      = excluded.summary,
                action_items = excluded.action_items,
                model_used   = excluded.model_used,
                created_at   = excluded.created_at,
                transcript_version_id = excluded.transcript_version_id;
            """,
            new
            {
                summary.CallId,
                summary.Summary,
                summary.ActionItems,
                summary.ModelUsed,
                CreatedAt = Iso(summary.CreatedAt == default ? DateTimeOffset.UtcNow : summary.CreatedAt),
            });
    }

    /// <summary>
    /// What the user wrote about a call themselves. Empty when they have written nothing.
    ///
    /// Deliberately separate from everything else the archive holds about a conversation. The
    /// summary, the commitments and the flags were produced by a model and are all replaced when a
    /// call is analysed again; this is the one thing a person wrote, and reprocessing must never
    /// touch it.
    /// </summary>
    public string GetNote(long callId)
    {
        using var connection = Open();

        return connection.QueryFirstOrDefault<string>(
            "SELECT note FROM call_note WHERE call_id = @callId;", new { callId }) ?? "";
    }

    /// <summary>Saves a note, or removes it when it has been emptied.</summary>
    public void SaveNote(long callId, string? note)
    {
        using var connection = Open();

        if (string.IsNullOrWhiteSpace(note))
        {
            // Cleared rather than stored as an empty string, so "has a note" stays a question the
            // database can answer without reading the text.
            connection.Execute("DELETE FROM call_note WHERE call_id = @callId;", new { callId });
            return;
        }

        connection.Execute(
            """
            INSERT INTO call_note (call_id, note, updated_at)
            VALUES (@callId, @note, @now)
            ON CONFLICT(call_id) DO UPDATE SET note = excluded.note, updated_at = excluded.updated_at;
            """,
            new { callId, note = note.Trim(), now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>Which of these calls have a note, for showing a marker without loading the text.</summary>
    public IReadOnlySet<long> CallsWithNotes(IEnumerable<long> callIds)
    {
        var ids = callIds.ToList();
        if (ids.Count == 0) return new HashSet<long>();

        using var connection = Open();

        return connection
            .Query<long>("SELECT call_id FROM call_note WHERE call_id IN @ids;", new { ids })
            .ToHashSet();
    }

    /// <summary>
    /// The notes on many conversations at once, with the text — one query for a whole timeline.
    ///
    /// <see cref="CallsWithNotes"/> answers "is there one" without reading it, which is what a
    /// list row needs; a screen that shows the note itself needs this instead. Without it the
    /// contact window's flow asked <see cref="GetNote"/> for every conversation in turn, each
    /// call its own connection and its own pragmas, for a column that is empty on most rows.
    /// </summary>
    public IReadOnlyDictionary<long, string> NotesOf(IEnumerable<long> callIds)
    {
        var ids = callIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, string>();

        using var connection = Open();

        return connection
            .Query<(long CallId, string Note)>(
                "SELECT call_id, note FROM call_note WHERE call_id IN @ids;", new { ids })
            .ToDictionary(r => r.CallId, r => r.Note);
    }

    public CallSummary? GetSummary(long callId)
    {
        using var connection = Open();
        return connection.QueryFirstOrDefault<SummaryRow>(
            "SELECT * FROM call_summary WHERE call_id = @callId;", new { callId })?.ToModel();
    }

    // ---- contact profile ----------------------------------------------------
    //
    // User-entered facts about a person. The analysis pipeline may never write here: the ledger
    // is the machine's, quotes and all; this is the user's, and needs none.

    public ContactProfile? GetProfile(long contactId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<(long ContactId, string? PhotoFile, string? BirthDate, string UpdatedAt)?>(
            "SELECT contact_id, photo_file, birth_date, updated_at FROM contact_profile WHERE contact_id = @contactId;",
            new { contactId });

        if (row is not { } r) return null;

        return new ContactProfile
        {
            ContactId = r.ContactId,
            PhotoFile = r.PhotoFile,
            BirthDate = r.BirthDate is null ? null : DateOnly.Parse(r.BirthDate),
            UpdatedAt = DateTimeOffset.Parse(r.UpdatedAt),
        };
    }

    public void SetContactPhoto(long contactId, string? photoFile)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO contact_profile (contact_id, photo_file, updated_at)
            VALUES (@contactId, @photoFile, @now)
            ON CONFLICT(contact_id) DO UPDATE SET
                photo_file = excluded.photo_file, updated_at = excluded.updated_at;
            """,
            new { contactId, photoFile, now = Iso(DateTimeOffset.UtcNow) });
    }

    public void SetBirthDate(long contactId, DateOnly? day)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO contact_profile (contact_id, birth_date, updated_at)
            VALUES (@contactId, @day, @now)
            ON CONFLICT(contact_id) DO UPDATE SET
                birth_date = excluded.birth_date, updated_at = excluded.updated_at;
            """,
            new { contactId, day = day?.ToString("yyyy-MM-dd"), now = Iso(DateTimeOffset.UtcNow) });
    }

    public IReadOnlyList<ContactField> GetFields(long contactId)
    {
        using var connection = Open();

        return
        [
            .. connection.Query<(long Id, long ContactId, string Label, string Value, int Position)>(
                """
                SELECT id, contact_id, label, value, position FROM contact_field
                WHERE contact_id = @contactId ORDER BY position, id;
                """,
                new { contactId })
                .Select(r => new ContactField
                {
                    Id = r.Id,
                    ContactId = r.ContactId,
                    Label = r.Label,
                    Value = r.Value,
                    Position = r.Position,
                }),
        ];
    }

    /// <summary>Adds a labelled fact at the end. Blank halves are refused: a fact needs both.</summary>
    public long AddField(long contactId, string label, string value)
    {
        label = label.Trim();
        value = value.Trim();

        if (label.Length == 0 || value.Length == 0)
            throw new ArgumentException("Etiket ve değer boş olamaz.");

        using var connection = Open();

        var next = connection.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(position), -1) + 1 FROM contact_field WHERE contact_id = @contactId;",
            new { contactId });

        return connection.ExecuteScalar<long>(
            """
            INSERT INTO contact_field (contact_id, label, value, position, updated_at)
            VALUES (@contactId, @label, @value, @next, @now)
            RETURNING id;
            """,
            new { contactId, label, value, next, now = Iso(DateTimeOffset.UtcNow) });
    }

    public void UpdateField(long fieldId, string label, string value)
    {
        label = label.Trim();
        value = value.Trim();

        if (label.Length == 0 || value.Length == 0)
            throw new ArgumentException("Etiket ve değer boş olamaz.");

        using var connection = Open();

        connection.Execute(
            "UPDATE contact_field SET label = @label, value = @value, updated_at = @now WHERE id = @fieldId;",
            new { fieldId, label, value, now = Iso(DateTimeOffset.UtcNow) });
    }

    public void RemoveField(long fieldId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM contact_field WHERE id = @fieldId;", new { fieldId });
    }

    /// <summary>Reminder days for many conversations at once — one query, for list rows.</summary>
    public IReadOnlyDictionary<long, DateOnly> RemindersOf(IEnumerable<long> callIds)
    {
        var ids = callIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, DateOnly>();

        using var connection = Open();

        return connection
            .Query<(long CallId, string Day)>(
                "SELECT call_id, remind_on FROM board_card WHERE remind_on IS NOT NULL AND call_id IN @ids;",
                new { ids })
            .ToDictionary(r => r.CallId, r => DateOnly.Parse(r.Day));
    }

    /// <summary>
    /// The newest transcript lines inside a window, no words required.
    ///
    /// This is the ask feature's fallback context: on a single short conversation, "nedir?"
    /// deserves the transcript itself as context, not a refusal because no keyword overlapped.
    /// Newest call first, each call's lines in speaking order.
    /// </summary>
    public IReadOnlyList<SearchHit> RecentSegments(
        long? contactId = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int limit = 40)
    {
        using var connection = Open();

        return [.. connection.Query<SearchHitRow>(
            """
            SELECT s.call_id      AS CallId,
                   s.id           AS SegmentId,
                   c.contact_id   AS ContactId,
                   ct.name        AS ContactName,
                   c.started_at   AS CallStartedAt,
                   s.is_me        AS IsMe,
                   s.start_ms     AS StartMs,
                   s.text         AS Text
            FROM segment s
            JOIN call c          ON c.id = s.call_id
            LEFT JOIN contact ct ON ct.id = c.contact_id
            WHERE (@contactId IS NULL OR c.contact_id = @contactId)
              AND (@since     IS NULL OR c.started_at >= @since)
              AND (@until     IS NULL OR c.started_at <  @until)
            ORDER BY c.started_at DESC, s.start_ms
            LIMIT @limit;
            """,
            new
            {
                contactId,
                since = since?.UtcDateTime.ToString("o"),
                until = until?.UtcDateTime.ToString("o"),
                limit,
            })
            .Select(r => r.ToModel())];
    }

    /// <summary>
    /// Every reminder falling inside a date window, day order — the calendar's month at a time.
    ///
    /// Includes reminders not yet due: the calendar's whole point is seeing what is COMING.
    /// Raw columns, hand-parsed, like every board query — no DateOnly type handler exists, and
    /// materialising dates through Dapper is the exact mistake that once made a dialog throw in
    /// its constructor on any call that had a card.
    /// </summary>
    public IReadOnlyList<(long CallId, string ContactName, string Title, DateOnly Day)> RemindersBetween(
        DateOnly from, DateOnly to)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<(long CallId, string? Name, string? Title, string Day)>(
                    """
                    SELECT b.call_id, ct.name, b.title, b.remind_on
                    FROM board_card b
                    JOIN call c          ON c.id = b.call_id
                    LEFT JOIN contact ct ON ct.id = c.contact_id
                    WHERE b.remind_on IS NOT NULL
                      AND b.remind_on >= @from AND b.remind_on <= @to
                      AND b.lane <> @done
                    ORDER BY b.remind_on, ct.name;
                    """,
                    new
                    {
                        from = from.ToString("yyyy-MM-dd"),
                        to = to.ToString("yyyy-MM-dd"),
                        done = BoardLane.Done,
                    })
                .Select(r => (
                    r.CallId,
                    string.IsNullOrWhiteSpace(r.Name) ? "İsimsiz görüşme" : r.Name,
                    r.Title ?? "",
                    DateOnly.Parse(r.Day))),
        ];
    }

    /// <summary>
    /// The user's OWN promise deadlines inside a date window — the calendar's third marker.
    ///
    /// Only ByMe rows: the other side's deadlines already surface as overdue flags; what the
    /// calendar adds is the promise the USER made and would otherwise forget until it became
    /// an apology. Conditional promises excluded, same reasoning as the overdue check: a date
    /// on "yollarsan gönderirim" is not yet a commitment to a day.
    /// </summary>
    public IReadOnlyList<(long CallId, string ContactName, string Obligation, DateOnly Day)> OwnCommitmentsBetween(
        DateOnly from, DateOnly to)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<(long CallId, string? Name, string Obligation, string Day)>(
                    """
                    SELECT cm.call_id, ct.name, COALESCE(cm.user_obligation, cm.obligation),
                           COALESCE(cm.user_deadline_date, cm.deadline_date)
                    FROM commitment cm
                    LEFT JOIN contact ct ON ct.id = cm.contact_id
                    WHERE cm.by_me = 1
                      AND cm.status = 0
                      AND cm.dismissed_by_user = 0
                      AND cm.is_conditional = 0
                      AND COALESCE(cm.user_deadline_date, cm.deadline_date) IS NOT NULL
                      AND COALESCE(cm.user_deadline_date, cm.deadline_date) >= @from
                      AND COALESCE(cm.user_deadline_date, cm.deadline_date) <= @to
                    ORDER BY COALESCE(cm.user_deadline_date, cm.deadline_date);
                    """,
                    new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") })
                .Select(r => (
                    r.CallId,
                    string.IsNullOrWhiteSpace(r.Name) ? "İsimsiz görüşme" : r.Name,
                    r.Obligation,
                    DateOnly.Parse(r.Day))),
        ];
    }

    /// <summary>
    /// The OTHER side's promise deadlines inside a date window — <see cref="OwnCommitmentsBetween"/>
    /// mirrored to by_me = 0, filters identical. The month view shows both sides because "when is
    /// Uliana's evrak due" is the same glance as "when is mine".
    /// </summary>
    public IReadOnlyList<(long CallId, string ContactName, string Obligation, DateOnly Day)> TheirCommitmentsBetween(
        DateOnly from, DateOnly to)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<(long CallId, string? Name, string Obligation, string Day)>(
                    """
                    SELECT cm.call_id, ct.name, COALESCE(cm.user_obligation, cm.obligation),
                           COALESCE(cm.user_deadline_date, cm.deadline_date)
                    FROM commitment cm
                    LEFT JOIN contact ct ON ct.id = cm.contact_id
                    WHERE cm.by_me = 0
                      AND cm.status = 0
                      AND cm.dismissed_by_user = 0
                      AND cm.is_conditional = 0
                      AND COALESCE(cm.user_deadline_date, cm.deadline_date) IS NOT NULL
                      AND COALESCE(cm.user_deadline_date, cm.deadline_date) >= @from
                      AND COALESCE(cm.user_deadline_date, cm.deadline_date) <= @to
                    ORDER BY COALESCE(cm.user_deadline_date, cm.deadline_date);
                    """,
                    new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") })
                .Select(r => (
                    r.CallId,
                    string.IsNullOrWhiteSpace(r.Name) ? "İsimsiz görüşme" : r.Name,
                    r.Obligation,
                    DateOnly.Parse(r.Day))),
        ];
    }

    /// <summary>
    /// Open action suggestions whose deadline falls inside the window — the calendar's weakest
    /// marker. Machine proposals, not user decisions, so the month view may only display them;
    /// their verbs (done, hide, route) stay on the surfaces that already have them.
    /// </summary>
    public IReadOnlyList<(long CallId, string ContactName, string Action, DateOnly Day)> ActionsDueBetween(
        DateOnly from, DateOnly to)
    {
        using var connection = Open();

        return
        [
            .. connection
                .Query<(long CallId, string? Name, string Action, string Day)>(
                    """
                    SELECT a.call_id, ct.name, a.action, a.deadline_date
                    FROM action_item a
                    LEFT JOIN contact ct ON ct.id = a.contact_id
                    WHERE a.status = 0
                      AND a.deadline_date IS NOT NULL
                      AND a.deadline_date >= @from AND a.deadline_date <= @to
                    ORDER BY a.deadline_date;
                    """,
                    new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") })
                .Select(r => (
                    r.CallId,
                    string.IsNullOrWhiteSpace(r.Name) ? "İsimsiz görüşme" : r.Name,
                    r.Action,
                    DateOnly.Parse(r.Day))),
        ];
    }

    /// <summary>
    /// Birthdays falling within the window, soonest first.
    ///
    /// Every date here was typed by the user on the person's profile — the application infers
    /// nothing. Next-occurrence arithmetic is done here rather than in SQL: month/day wraparound
    /// (a December birthday looked at in January) is exactly the kind of logic SQLite date
    /// functions make easy to get quietly wrong.
    /// </summary>
    public IReadOnlyList<(long ContactId, string Name, DateOnly Day, int DaysAway)> UpcomingBirthdays(
        DateOnly from, int withinDays)
    {
        using var connection = Open();

        var rows = connection.Query<(long Id, string Name, string BirthDate)>(
            """
            SELECT c.id, c.name, p.birth_date FROM contact_profile p
            JOIN contact c ON c.id = p.contact_id
            WHERE p.birth_date IS NOT NULL;
            """);

        var upcoming = new List<(long, string, DateOnly, int)>();

        foreach (var (id, name, birth) in rows)
        {
            var day = DateOnly.Parse(birth);

            var next = new DateOnly(from.Year, day.Month, Math.Min(day.Day, DateTime.DaysInMonth(from.Year, day.Month)));
            if (next < from)
                next = new DateOnly(from.Year + 1, day.Month, Math.Min(day.Day, DateTime.DaysInMonth(from.Year + 1, day.Month)));

            var away = next.DayNumber - from.DayNumber;

            if (away <= withinDays) upcoming.Add((id, name, next, away));
        }

        return [.. upcoming.OrderBy(u => u.Item4)];
    }

    /// <summary>
    /// Segment counts for many conversations in one query — the batch the contact window's own
    /// comment promised while its loop was quietly making one round trip per row.
    /// </summary>
    public IReadOnlyDictionary<long, int> SegmentCounts(IEnumerable<long> callIds)
    {
        var ids = callIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, int>();

        using var connection = Open();

        return connection
            .Query<(long CallId, int Count)>(
                "SELECT call_id, COUNT(*) FROM segment WHERE call_id IN @ids GROUP BY call_id;",
                new { ids })
            .ToDictionary(r => r.CallId, r => r.Count);
    }

    // ---- tags ---------------------------------------------------------------
    //
    // The user's own words for what a conversation was — "tehdit edildik", "önemli", anything.
    // Identity is the Turkish-folded form, so İ/ı casing differences never split one tag in two,
    // while the spelling the user first typed is what every screen shows. User data throughout:
    // reprocessing may never write or delete here.

    /// <summary>Puts a label on a conversation. Re-tagging with a spelling variant is a no-op.</summary>
    public void Tag(long callId, string tag)
    {
        var trimmed = tag.Trim();
        if (trimmed.Length == 0) return;

        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO call_tag (call_id, tag, tag_folded, created_at)
            VALUES (@callId, @trimmed, @folded, @now)
            ON CONFLICT(call_id, tag_folded) DO NOTHING;
            """,
            new
            {
                callId,
                trimmed,
                folded = Text.TurkishText.NormalizeForSearch(trimmed),
                now = Iso(DateTimeOffset.UtcNow),
            });
    }

    public void Untag(long callId, string tag)
    {
        using var connection = Open();

        connection.Execute(
            "DELETE FROM call_tag WHERE call_id = @callId AND tag_folded = @folded;",
            new { callId, folded = Text.TurkishText.NormalizeForSearch(tag.Trim()) });
    }

    public IReadOnlyList<string> TagsOf(long callId)
    {
        using var connection = Open();

        return
        [
            .. connection.Query<string>(
                "SELECT tag FROM call_tag WHERE call_id = @callId ORDER BY created_at, tag;",
                new { callId }),
        ];
    }

    /// <summary>Tags for many conversations at once, for list screens: one query, not one per row.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<string>> TagsOf(IEnumerable<long> callIds)
    {
        var ids = callIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, IReadOnlyList<string>>();

        using var connection = Open();

        return connection
            .Query<(long CallId, string Tag)>(
                "SELECT call_id, tag FROM call_tag WHERE call_id IN @ids ORDER BY created_at, tag;",
                new { ids })
            .GroupBy(r => r.CallId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Tag)]);
    }

    // ---- tag definitions: icon and colour per tag, Outlook-category style ------------------

    /// <summary>Every defined tag look, in the order the user arranged them.</summary>
    public IReadOnlyList<TagDef> TagDefs()
    {
        using var connection = Open();

        // Tuple then map: SQLite hands position back as Int64, which Dapper will not narrow
        // into the record's int parameter on its own.
        return
        [
            .. connection
                .Query<(string Tag, string Icon, string Color, long Position)>(
                    "SELECT tag, icon, color, position FROM tag_def ORDER BY position, tag_folded;")
                .Select(row => new TagDef(row.Tag, row.Icon, row.Color, (int)row.Position)),
        ];
    }

    /// <summary>Creates or updates a tag's look. Identity is the Turkish-folded spelling.</summary>
    public void SaveTagDef(TagDef def)
    {
        var trimmed = def.Tag.Trim();
        if (trimmed.Length == 0) return;

        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO tag_def (tag_folded, tag, icon, color, position)
            VALUES (@folded, @tag, @icon, @color, @position)
            ON CONFLICT(tag_folded) DO UPDATE SET
                tag = excluded.tag, icon = excluded.icon,
                color = excluded.color, position = excluded.position;
            """,
            new
            {
                folded = Text.TurkishText.NormalizeForSearch(trimmed),
                tag = trimmed,
                icon = def.Icon,
                color = def.Color,
                position = def.Position,
            });
    }

    /// <summary>Removes a tag's look. Conversations carrying the tag keep it — plainly dressed.</summary>
    public void DeleteTagDef(string tag)
    {
        using var connection = Open();

        connection.Execute(
            "DELETE FROM tag_def WHERE tag_folded = @folded;",
            new { folded = Text.TurkishText.NormalizeForSearch(tag.Trim()) });
    }

    /// <summary>
    /// The starting vocabulary, written once into an empty table.
    ///
    /// These are ordinary rows, not fixtures: the user renames, recolours and deletes them like
    /// any tag they made themselves. Seeded so the first visit to "Etiketle" offers something to
    /// click instead of an empty box — the same reason Outlook ships with six coloured categories.
    /// </summary>
    public void SeedDefaultTagDefs()
    {
        using var connection = Open();

        var existing = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM tag_def;");
        if (existing > 0) return;

        var position = 0;
        foreach (var (tag, icon, color) in new[]
                 {
                     ("Önemli", "Flag24", "#E81123"),
                     ("İş", "Briefcase24", "#0078D4"),
                     ("Kişisel", "Person24", "#8764B8"),
                     ("Tehdit", "Warning24", "#D13438"),
                     ("Para", "Money24", "#107C10"),
                     ("Takip", "Star24", "#F7630C"),
                 })
        {
            SaveTagDef(new TagDef(tag, icon, color, position++));
        }
    }

    // ---- circles: the user's own groups of people ------------------------------------------

    /// <summary>Every circle the user has defined, in the order they arranged them.</summary>
    public IReadOnlyList<Circle> Circles()
    {
        using var connection = Open();

        // Tuple then map, for the same reason TagDefs does it: SQLite hands position back as
        // Int64 and Dapper will not narrow it into the record's int on its own.
        return
        [
            .. connection
                .Query<(string Circle, string Icon, string Color, long Position)>(
                    "SELECT circle, icon, color, position FROM contact_circle ORDER BY position, circle_folded;")
                .Select(row => new Circle(row.Circle, row.Icon, row.Color, (int)row.Position)),
        ];
    }

    /// <summary>Creates or renames-in-place a circle. Identity is the Turkish-folded spelling.</summary>
    public void SaveCircle(Circle circle)
    {
        var trimmed = circle.Name.Trim();
        if (trimmed.Length == 0) return;

        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO contact_circle (circle_folded, circle, icon, color, position)
            VALUES (@folded, @circle, @icon, @color, @position)
            ON CONFLICT(circle_folded) DO UPDATE SET
                circle = excluded.circle, icon = excluded.icon,
                color = excluded.color, position = excluded.position;
            """,
            new
            {
                folded = Text.TurkishText.NormalizeForSearch(trimmed),
                circle = trimmed,
                icon = circle.Icon,
                color = circle.Color,
                position = circle.Position,
            });
    }

    /// <summary>
    /// Removes a circle's definition. Every assignment stays exactly where it is.
    ///
    /// The same rule as <see cref="DeleteTagDef"/>, and it is not a technicality: the assignments
    /// are the user's own data and deleting a word must never delete what somebody spent an
    /// evening filing. The people fall back into "Çevresiz" while the word is gone, and the
    /// moment it is written again — same spelling, folded — they are all back in it.
    /// </summary>
    public void DeleteCircle(string circle)
    {
        using var connection = Open();

        connection.Execute(
            "DELETE FROM contact_circle WHERE circle_folded = @folded;",
            new { folded = Text.TurkishText.NormalizeForSearch(circle.Trim()) });
    }

    /// <summary>
    /// Renames a circle in place, taking everybody in it along.
    ///
    /// The identity of a circle is its folded spelling, so "Aile" → "Ailem" is a new key: saving
    /// the new definition and deleting the old one would leave every person pointing at a word
    /// that no longer exists, and the user — who typed one letter — would watch a tab that said
    /// 41 become a tab that says 0. So the assignments move with the name, in one transaction.
    ///
    /// Renaming a circle onto a name that already exists merges the two, and that is the honest
    /// outcome: after it, one word means one set of people, and nobody was dropped on the way.
    /// </summary>
    public void RenameCircle(string fromFolded, Circle to)
    {
        var trimmed = to.Name.Trim();
        if (trimmed.Length == 0) return;

        var folded = Text.TurkishText.NormalizeForSearch(trimmed);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            INSERT INTO contact_circle (circle_folded, circle, icon, color, position)
            VALUES (@folded, @circle, @icon, @color, @position)
            ON CONFLICT(circle_folded) DO UPDATE SET
                circle = excluded.circle, icon = excluded.icon,
                color = excluded.color, position = excluded.position;
            """,
            new { folded, circle = trimmed, icon = to.Icon, color = to.Color, position = to.Position },
            transaction);

        if (!string.Equals(folded, fromFolded, StringComparison.Ordinal))
        {
            connection.Execute(
                "UPDATE contact_profile SET circle_folded = @folded WHERE circle_folded = @fromFolded;",
                new { folded, fromFolded }, transaction);

            connection.Execute(
                "DELETE FROM contact_circle WHERE circle_folded = @fromFolded;",
                new { fromFolded }, transaction);
        }

        transaction.Commit();
    }

    /// <summary>
    /// The two starting circles, written once into an empty table.
    ///
    /// Aile and İş, chosen by the user themselves — not three, and not the application's guess.
    /// They are ordinary rows: renameable, deletable, and nothing re-seeds them afterwards, so a
    /// circle somebody deleted stays deleted. An empty strip would have been the honest zero, but
    /// it would also have meant the first visit offers nothing to press — the same reason the tag
    /// wardrobe ships with six words in it.
    /// </summary>
    public void SeedDefaultCircles()
    {
        using var connection = Open();

        var existing = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM contact_circle;");
        if (existing > 0) return;

        var position = 0;
        foreach (var (circle, icon, color) in new[]
                 {
                     ("Aile", "Home24", "#8764B8"),
                     ("İş", "Briefcase24", "#0078D4"),
                 })
        {
            SaveCircle(new Circle(circle, icon, color, position++));
        }
    }

    /// <summary>
    /// Which circle each person is in, by contact id — one query, never one per row.
    ///
    /// Only people whose circle still has a definition are listed. A row pointing at a word the
    /// user has since deleted reads as no circle at all, everywhere, which is the same answer
    /// <see cref="ListCalls"/> and <see cref="CallCountsByCircle"/> give: one rule, so a screen
    /// cannot show a dot for a circle that has no tab.
    /// </summary>
    public IReadOnlyDictionary<long, Circle> CirclesByContact()
    {
        using var connection = Open();

        return connection
            .Query<(long ContactId, string Circle, string Icon, string Color, long Position)>(
                """
                SELECT p.contact_id AS ContactId, d.circle AS Circle, d.icon AS Icon,
                       d.color AS Color, d.position AS Position
                  FROM contact_profile p
                  JOIN contact_circle d ON d.circle_folded = p.circle_folded;
                """)
            .ToDictionary(
                row => row.ContactId,
                row => new Circle(row.Circle, row.Icon, row.Color, (int)row.Position));
    }

    /// <summary>The circle written on a person, defined or not — what the editor shows and writes.</summary>
    public string? CircleOf(long contactId)
    {
        using var connection = Open();

        return connection.QueryFirstOrDefault<string?>(
            "SELECT circle_folded FROM contact_profile WHERE contact_id = @contactId;",
            new { contactId });
    }

    /// <summary>
    /// Puts a person in a circle, or takes them out of every circle when given null.
    ///
    /// Written the moment it is chosen, with no Save button — the same behaviour as
    /// <see cref="SetBirthDate"/> and the note editor, because a screen where some edits are kept
    /// and others need a button is a screen where somebody loses work.
    ///
    /// The pipeline never calls this. A circle is the user's word for a group of people, and
    /// nothing measured, transcribed or inferred may put anybody into one.
    /// </summary>
    public void SetContactCircle(long contactId, string? circle)
    {
        var folded = string.IsNullOrWhiteSpace(circle)
            ? null
            : Text.TurkishText.NormalizeForSearch(circle.Trim());

        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO contact_profile (contact_id, circle_folded, updated_at)
            VALUES (@contactId, @folded, @now)
            ON CONFLICT(contact_id) DO UPDATE SET
                circle_folded = @folded, updated_at = @now;
            """,
            new { contactId, folded, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>
    /// How many conversations each circle holds, across THE WHOLE ARCHIVE.
    ///
    /// Before the filter, always. A tab that counted only what it is showing would say "Aile 12"
    /// on a screen holding twelve rows, which tells the user nothing they could not already see
    /// and hides the number they actually want. The ledger and the promises page have counted
    /// this way from the beginning.
    ///
    /// A call whose person is in a circle whose definition has been deleted counts as uncircled,
    /// and so does a call nobody has named. That is what makes the arithmetic close: every
    /// conversation in the archive is counted in exactly one bucket, so the tabs always add up to
    /// the total and nothing can be reachable from no tab at all.
    /// </summary>
    /// <returns>Folded circle to count, plus the count of everything in no circle.</returns>
    public (IReadOnlyDictionary<string, int> ByCircle, int Uncircled) CallCountsByCircle()
    {
        using var connection = Open();

        var rows = connection.Query<(string? Circle, long Count)>(
            """
            SELECT d.circle_folded AS Circle, COUNT(*) AS Count
              FROM call c
              LEFT JOIN contact_profile p ON p.contact_id = c.contact_id
              LEFT JOIN contact_circle d ON d.circle_folded = p.circle_folded
             GROUP BY d.circle_folded;
            """).ToList();

        return (
            rows.Where(r => r.Circle is not null)
                .ToDictionary(r => r.Circle!, r => (int)r.Count, StringComparer.Ordinal),
            (int)rows.Where(r => r.Circle is null).Sum(r => r.Count));
    }

    /// <summary>
    /// Every conversation carrying a tag, newest first — the tag as a query of its own.
    ///
    /// Shaped as search hits so the search screen can browse a tag with no words typed: the
    /// "text" of each row is the call's one-line summary when one exists, or its size when not.
    /// SegmentId 0 and StartMs 0 mean "the conversation, from the top" to everything downstream.
    /// </summary>
    /// <summary>
    /// Every call in a period, newest first, as search rows — "dünkü görüşmeler" without a word
    /// to search for. The text is the summary when there is one, else the size of the transcript.
    /// </summary>
    public IReadOnlyList<SearchHit> BrowseCalls(
        long? contactId = null, DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 300)
    {
        using var connection = Open();

        return [.. connection.Query<SearchHitRow>(
            """
            SELECT c.id           AS CallId,
                   0              AS SegmentId,
                   c.contact_id   AS ContactId,
                   ct.name        AS ContactName,
                   c.started_at   AS CallStartedAt,
                   0              AS IsMe,
                   0              AS StartMs,
                   COALESCE(
                       (SELECT s.summary FROM call_summary s WHERE s.call_id = c.id),
                       (SELECT COUNT(*) || ' satır konuşma' FROM segment sg WHERE sg.call_id = c.id))
                                  AS Text
            FROM call c
            LEFT JOIN contact ct ON ct.id = c.contact_id
            WHERE (@contactId IS NULL OR c.contact_id = @contactId)
              AND (@since     IS NULL OR c.started_at >= @since)
              AND (@until     IS NULL OR c.started_at <  @until)
            ORDER BY c.started_at DESC
            LIMIT @limit;
            """,
            new
            {
                contactId,
                since = since?.UtcDateTime.ToString("o"),
                until = until?.UtcDateTime.ToString("o"),
                limit,
            })
            .Select(r => r.ToModel())];
    }

    public IReadOnlyList<SearchHit> TaggedCalls(
        string tag, long? contactId = null, DateTimeOffset? since = null, int limit = 200)
    {
        var folded = Text.TurkishText.NormalizeForSearch(tag.Trim());
        if (folded.Length == 0) return [];

        using var connection = Open();

        return [.. connection.Query<SearchHitRow>(
            """
            SELECT c.id           AS CallId,
                   0              AS SegmentId,
                   c.contact_id   AS ContactId,
                   ct.name        AS ContactName,
                   c.started_at   AS CallStartedAt,
                   0              AS IsMe,
                   0              AS StartMs,
                   COALESCE(
                       (SELECT s.summary FROM call_summary s WHERE s.call_id = c.id),
                       (SELECT COUNT(*) || ' satır konuşma' FROM segment sg WHERE sg.call_id = c.id))
                                  AS Text
            FROM call_tag t
            JOIN call c          ON c.id = t.call_id
            LEFT JOIN contact ct ON ct.id = c.contact_id
            WHERE t.tag_folded = @folded
              AND (@contactId IS NULL OR c.contact_id = @contactId)
              AND (@since     IS NULL OR c.started_at >= @since)
            ORDER BY c.started_at DESC
            LIMIT @limit;
            """,
            new { folded, contactId, since = since?.UtcDateTime.ToString("o"), limit })
            .Select(r => r.ToModel())];
    }

    /// <summary>Every tag in use with its count, most used first. Feeds suggestions and filters.</summary>
    public IReadOnlyList<(string Tag, int Count)> AllTags()
    {
        using var connection = Open();

        // The display spelling of a folded group is its earliest: the word the user first chose.
        return
        [
            .. connection.Query<(string, int)>(
                """
                SELECT (SELECT t2.tag FROM call_tag t2
                        WHERE t2.tag_folded = t.tag_folded
                        ORDER BY t2.created_at LIMIT 1),
                       COUNT(*)
                FROM call_tag t
                GROUP BY t.tag_folded
                ORDER BY COUNT(*) DESC, t.tag_folded;
                """),
        ];
    }

    /// <summary>Conversations carrying a tag, optionally one contact's, newest first.</summary>
    public IReadOnlyList<Call> CallsTagged(string tag, long? contactId = null)
    {
        using var connection = Open();

        return
        [
            .. connection.Query<CallRow>(
                """
                SELECT c.* FROM call c
                JOIN call_tag t ON t.call_id = c.id
                WHERE t.tag_folded = @folded
                  AND (@contactId IS NULL OR c.contact_id = @contactId)
                ORDER BY c.started_at DESC;
                """,
                new { folded = Text.TurkishText.NormalizeForSearch(tag.Trim()), contactId })
                .Select(r => r.ToModel()),
        ];
    }

    // ---- silence trimming ---------------------------------------------------

    /// <summary>What a ledger sweep removed.</summary>
    public sealed record LedgerSweep(int Hollow, int Duplicates)
    {
        public int Total => Hollow + Duplicates;
    }

    /// <summary>
    /// Removes ledger entries that say nothing, and collapses the ones that say it twice.
    ///
    /// Both populations exist because of faults that are now fixed at their source, and neither
    /// can be repaired in place — they are rows carrying no information. A commitment with no
    /// obligation text is a promise the archive cannot state: on screen it is a quote under a
    /// person's name with nothing above it, it counts toward "66 açık söz", and it can never be
    /// closed because there is nothing to close. Seventy-nine of eighty in a real archive.
    ///
    /// A ruling the user made is never touched, which is the same rule
    /// <see cref="ClearAnalysis"/> follows: status 0 and not dismissed is the untouched default,
    /// and anything else is somebody's decision about a row they read.
    /// </summary>
    public LedgerSweep SweepLedger()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // The same rule as ClearAnalysis: a row the user ruled on or edited is theirs.
        var hollow = connection.Execute(
            """
            DELETE FROM commitment
             WHERE status = 0 AND dismissed_by_user = 0
               AND edited_at IS NULL AND user_deadline_date IS NULL
               AND (obligation IS NULL OR TRIM(obligation) = '');
            """,
            transaction: transaction);

        // The lowest id in each group survives, so the entry keeps the identity anything else
        // may already point at.
        var duplicates = connection.Execute(
            """
            DELETE FROM commitment
             WHERE status = 0 AND dismissed_by_user = 0
               AND edited_at IS NULL AND user_deadline_date IS NULL
               AND id NOT IN (
                   SELECT MIN(id) FROM commitment
                    GROUP BY call_id, by_me, TRIM(LOWER(obligation)), TRIM(LOWER(quote)));
            """,
            transaction: transaction);

        duplicates += connection.Execute(
            """
            DELETE FROM claim
             WHERE id NOT IN (
                   SELECT MIN(id) FROM claim
                    GROUP BY call_id, TRIM(LOWER(entity)), TRIM(LOWER(attribute)),
                             TRIM(LOWER(value)), TRIM(LOWER(quote)));
            """,
            transaction: transaction);

        transaction.Commit();

        return new LedgerSweep(hollow, duplicates);
    }

    // ---- retention ----------------------------------------------------------

    /// <summary>
    /// Recordings whose audio is old enough to remove, and which nothing says to keep.
    ///
    /// The setting has existed since the beginning and nothing ever acted on it: the screen
    /// offered a number of days, promised that pinned conversations were exempt, and then kept
    /// everything forever. Both halves were untrue, and the second was untrue in a way nobody
    /// could have discovered — nothing in the product pins anything.
    ///
    /// So the exemptions are things that actually exist and that a person actually did: a
    /// conversation on the board, or one they wrote a note about. Both are explicit signals that
    /// this recording matters, which is what "pinned" was reaching for.
    ///
    /// Only the audio goes. The transcript, the ledger and the notes are small and are the part
    /// worth keeping; the recording is what fills a disk.
    /// </summary>
    public IReadOnlyList<Call> AudioToSweep(int olderThanDays)
    {
        if (olderThanDays <= 0) return [];

        var cutoff = Iso(DateTimeOffset.UtcNow.AddDays(-olderThanDays));

        using var connection = Open();

        return
        [
            .. connection.Query<CallRow>(
                """
                SELECT c.* FROM call c
                WHERE c.started_at < @cutoff
                  AND (c.mic_path IS NOT NULL OR c.far_path IS NOT NULL)
                  AND c.is_pinned = 0

                  -- Not while it is queued or being worked on: a re-transcription reads these
                  -- files for minutes, and the sweep used to pull them out from under it.
                  AND c.state NOT IN (1, 2, 4)
                  AND NOT EXISTS (SELECT 1 FROM board_card b WHERE b.call_id = c.id)
                  AND NOT EXISTS (SELECT 1 FROM call_note n WHERE n.call_id = c.id)

                  -- Only once something durable was derived from it.
                  --
                  -- The comment above promises "the transcript, the ledger and the notes are the
                  -- part worth keeping; the recording is what fills a disk" — but the sweep went
                  -- by age alone, so a call that was never transcribed lost the audio too, and
                  -- for that call the audio was the whole record. A failed transcription, a
                  -- machine without Python, a stretch where the model was missing: all of those
                  -- produce recordings with no text, and those are exactly the ones this would
                  -- have deleted while leaving an empty row behind.
                  AND EXISTS (SELECT 1 FROM segment s WHERE s.call_id = c.id)
                ORDER BY c.started_at;
                """,
                new { cutoff })
                .Select(r => r.ToModel()),
        ];
    }

    /// <summary>
    /// Removes one recording's audio, keeping everything derived from it.
    ///
    /// The row survives with its transcript, so the conversation is still searchable, still
    /// quotable and still in the ledger — it simply can no longer be played. The paths are cleared
    /// rather than left pointing at nothing, because a path to a missing file is what makes a
    /// player fail in a way nobody can explain.
    /// </summary>
    /// <returns>How many files were actually removed.</returns>
    public int ForgetAudio(long callId)
    {
        var call = GetCall(callId);
        if (call is null) return 0;

        var removed = 0;
        var micGone = string.IsNullOrWhiteSpace(call.MicPath);
        var farGone = string.IsNullOrWhiteSpace(call.FarPath);

        // Each stream is tracked on its own. A failure on the second file used to return with
        // the first already deleted and its path still on the row — so the row pointed at a
        // file that no longer existed, which is the one state a player cannot explain.
        foreach (var (path, isMic) in new[] { (call.MicPath, true), (call.FarPath, false) })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            try
            {
                if (File.Exists(path)) File.Delete(path);
                removed++;
                if (isMic) micGone = true; else farGone = true;
            }
            catch (Exception)
            {
                // Held open by a player. Left for the next sweep; only what went is cleared.
            }
        }

        // The mixed copy is derived from the two streams and is a playable recording of the
        // whole conversation. Forgetting the audio while leaving it behind forgot nothing.
        // Decoded copies in the cache go with the originals.
        Audio.AudioMaterialiser.Forget(call.MicPath);
        Audio.AudioMaterialiser.Forget(call.FarPath);

        var anchor = call.MicPath ?? call.FarPath;
        if (anchor is not null)
        {
            var mixed = Audio.ConversationMix.PathFor(anchor);

            try { if (File.Exists(mixed)) File.Delete(mixed); }
            catch (Exception) { }

            Audio.ConversationMix.DiscardPartials(mixed);
        }

        using var connection = Open();

        connection.Execute(
            """
            UPDATE call
               SET mic_path = CASE WHEN @micGone THEN NULL ELSE mic_path END,
                   far_path = CASE WHEN @farGone THEN NULL ELSE far_path END
             WHERE id = @callId;
            """,
            new { callId, micGone, farGone });

        return removed;
    }

    // ---- the board ----------------------------------------------------------

    /// <summary>
    /// Puts a conversation on the board, or moves the one already there.
    ///
    /// Keyed on the call rather than given its own identity: a conversation is either on the board
    /// or it is not, and allowing two cards for one call would mean the same thing sitting in two
    /// lanes with no way to say which is true.
    ///
    /// New cards go to the end of their lane. Anywhere else and adding one would silently reorder
    /// work somebody had already arranged.
    /// </summary>
    public void PutOnBoard(long callId, string lane, string? title = null, DateOnly? remindOn = null)
    {
        if (!BoardLane.IsKnown(lane)) lane = BoardLane.ToLookAt;

        using var connection = Open();

        // Table-wide, not per lane. The dashboard panel shows every card as one flat, hand-ordered
        // list, so position is one global sequence — which restricted to any lane is still a valid
        // per-lane order, so nothing that sorts by (lane, position) notices the change.
        var next = connection.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(position), -1) + 1 FROM board_card;");

        connection.Execute(
            """
            INSERT INTO board_card (call_id, lane, position, title, remind_on, created_at)
            VALUES (@callId, @lane, @next, @title, @remindOn, @now)
            ON CONFLICT(call_id) DO UPDATE SET
                lane      = excluded.lane,
                position  = excluded.position,
                title     = COALESCE(excluded.title, board_card.title),

                -- Kept, not overwritten. Moving a card between lanes must not lose its reminder:
                -- the reminder is why the card is on the board at all, and the lane is only where
                -- it currently sits. Clearing one is what RemindOn(id, null) is for.
                remind_on = COALESCE(excluded.remind_on, board_card.remind_on);
            """,
            new
            {
                callId,
                lane,
                next,
                title,
                remindOn = remindOn?.ToString("yyyy-MM-dd"),
                now = Iso(DateTimeOffset.UtcNow),
            });
    }

    /// <summary>
    /// Strikes API keys out of engine references recorded before they were scrubbed at source.
    ///
    /// Runs written by earlier versions hold the worker's echo of "url|key|model" verbatim —
    /// a live credential in a database column that feeds a screen. Run at startup; already-clean
    /// rows match nothing and the pass costs one query.
    /// </summary>
    public void ScrubSecretsFromRuns()
    {
        using var connection = Open();

        var dirty = connection.Query<(long Id, string Engine)>(
            "SELECT id, engine FROM processing_run WHERE engine LIKE '%|%|%';");

        foreach (var run in dirty)
        {
            connection.Execute(
                "UPDATE processing_run SET engine = @engine WHERE id = @id;",
                new { id = run.Id, engine = Asr.SttEndpoint.ScrubRef(run.Engine) });
        }
    }

    /// <summary>
    /// This conversation's card, if it has one — what the reminder dialog prefills from.
    ///
    /// Read as raw columns and parsed by hand, like every board query: no type handler is
    /// registered for DateOnly or DateTimeOffset, so materialising BoardCard directly works
    /// only until the first call that actually HAS a card — at which point it throws, in a
    /// dialog constructor, on the user's machine. That exact sequence shipped once.
    /// </summary>
    public BoardCard? BoardCardOf(long callId)
    {
        using var connection = Open();

        var row = connection
            .Query<(long CallId, string Lane, long Position, string? Title, string? RemindOn, string CreatedAt)>(
                """
                SELECT call_id, lane, position, title, remind_on, created_at
                FROM board_card WHERE call_id = @callId;
                """,
                new { callId })
            .FirstOrDefault();

        return row == default
            ? null
            : new BoardCard
            {
                CallId = row.CallId,
                Lane = row.Lane,
                Position = (int)row.Position,
                Title = row.Title,
                RemindOn = row.RemindOn is null ? null : DateOnly.Parse(row.RemindOn),
                CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
            };
    }

    /// <summary>Takes a conversation off the board. The conversation itself is untouched.</summary>
    public void RemoveFromBoard(long callId)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM board_card WHERE call_id = @callId;", new { callId });
    }

    /// <summary>Sets or clears the day a card should come back.</summary>
    public void RemindOn(long callId, DateOnly? day)
    {
        using var connection = Open();

        connection.Execute(
            "UPDATE board_card SET remind_on = @day WHERE call_id = @callId;",
            new { callId, day = day?.ToString("yyyy-MM-dd") });
    }

    /// <summary>Everything on the board, in lane and position order.</summary>
    /// <summary>
    /// The dashboard panel's projection: every open card, flat, in the order the user made.
    ///
    /// Databases written before position became global can hold ties across lanes; the created_at
    /// and call_id tiebreaks keep those stable until the first reorder rewrites them for good.
    /// </summary>
    public IReadOnlyList<BoardCard> OpenBoardCards()
    {
        using var connection = Open();

        return
        [
            .. connection.Query<(long CallId, string Lane, int Position, string? Title, string? RemindOn, string CreatedAt)>(
                """
                SELECT call_id, lane, position, title, remind_on, created_at FROM board_card
                WHERE lane <> @done
                ORDER BY position, created_at, call_id;
                """,
                new { done = BoardLane.Done })
                .Select(r => new BoardCard
                {
                    CallId = r.CallId,
                    Lane = r.Lane,
                    Position = r.Position,
                    Title = r.Title,
                    RemindOn = r.RemindOn is null ? null : DateOnly.Parse(r.RemindOn),
                    CreatedAt = DateTimeOffset.Parse(r.CreatedAt),
                }),
        ];
    }

    /// <summary>
    /// Rewrites the user's order: each listed card gets its index as its position, atomically.
    /// Cards not listed keep theirs — they only ever compete against each other.
    /// </summary>
    public void ReorderBoard(IReadOnlyList<long> callIdsInOrder)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        for (var i = 0; i < callIdsInOrder.Count; i++)
        {
            connection.Execute(
                "UPDATE board_card SET position = @i WHERE call_id = @callId;",
                new { i, callId = callIdsInOrder[i] }, transaction);
        }

        transaction.Commit();
    }

    public IReadOnlyList<BoardCard> BoardCards()
    {
        using var connection = Open();

        return
        [
            .. connection.Query<(long CallId, string Lane, int Position, string? Title, string? RemindOn, string CreatedAt)>(
                "SELECT call_id, lane, position, title, remind_on, created_at FROM board_card ORDER BY lane, position;")
                .Select(r => new BoardCard
                {
                    CallId = r.CallId,
                    Lane = r.Lane,
                    Position = r.Position,
                    Title = r.Title,
                    RemindOn = r.RemindOn is null ? null : DateOnly.Parse(r.RemindOn),
                    CreatedAt = DateTimeOffset.Parse(r.CreatedAt),
                }),
        ];
    }

    /// <summary>
    /// Cards whose reminder has come due, soonest first.
    ///
    /// Compared by day rather than by instant: a reminder set for Tuesday is due on Tuesday
    /// morning, not at the hour it happened to be created.
    /// </summary>
    public IReadOnlyList<BoardCard> DueCards()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");

        using var connection = Open();

        return
        [
            .. connection.Query<(long CallId, string Lane, string? Title, string RemindOn)>(
                """
                SELECT call_id, lane, title, remind_on
                FROM board_card
                WHERE remind_on IS NOT NULL AND remind_on <= @today AND lane <> @done
                ORDER BY remind_on;
                """,
                new { today, done = BoardLane.Done })
                .Select(r => new BoardCard
                {
                    CallId = r.CallId,
                    Lane = r.Lane,
                    Title = r.Title,
                    RemindOn = DateOnly.Parse(r.RemindOn),
                }),
        ];
    }

    /// <summary>How many cards are in each lane, for the strip on the first screen.</summary>
    public IReadOnlyDictionary<string, int> BoardCounts()
    {
        using var connection = Open();

        return connection
            .Query<(string Lane, int Count)>("SELECT lane, COUNT(*) FROM board_card GROUP BY lane;")
            .ToDictionary(r => r.Lane, r => r.Count);
    }

    // ---- what the work cost -------------------------------------------------

    /// <summary>
    /// Records one completed piece of work.
    ///
    /// Deliberately never throws. This is bookkeeping attached to the side of a pipeline that has
    /// just succeeded at something the user cares about, and letting a statistics insert turn a
    /// finished transcript into a failed call would be the tail wagging the dog.
    /// </summary>
    public void RecordRun(
        long? callId,
        string stage,
        string engine,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        TimeSpan audio,
        int? promptTokens = null,
        int? completionTokens = null,
        bool succeeded = true,
        double? speechCoverage = null)
    {
        try
        {
            using var connection = Open();

            connection.Execute(
                """
                INSERT INTO processing_run
                    (call_id, stage, engine, started_at, elapsed_ms, audio_ms,
                     prompt_tokens, completion_tokens, succeeded, speech_coverage)
                VALUES
                    (@callId, @stage, @engine, @startedAt, @elapsedMs, @audioMs,
                     @promptTokens, @completionTokens, @succeeded, @speechCoverage);
                """,
                new
                {
                    callId,
                    stage,
                    engine = string.IsNullOrWhiteSpace(engine) ? "bilinmiyor" : engine,
                    startedAt = Iso(startedAt),
                    elapsedMs = (long)elapsed.TotalMilliseconds,
                    audioMs = (long)audio.TotalMilliseconds,
                    promptTokens,
                    completionTokens,
                    succeeded = succeeded ? 1 : 0,
                    speechCoverage,
                });
        }
        catch (Exception)
        {
            // See above.
        }
    }

    // LastRuns(stage) — the last run of one stage for EVERY call in one query — was deleted here
    // on 7 September 2026. It was written for a screen that ended up asking a different question:
    // the processing list reads its engine and its figures from the call itself, and the one place
    // that wants a run reads exactly one, through LastRun below. Nothing in src/ or tests/ ever
    // called it.
    //
    // Removed rather than given a consumer, because inventing a caller for a query is how a
    // feature nobody asked for gets shipped. It is small and its shape is in this comment if the
    // per-row need ever turns up.

    /// <summary>
    /// One row per day for one stage, oldest first, with empty days filled in.
    ///
    /// The gaps matter as much as the bars. A chart drawn only from days that have rows compresses
    /// a fortnight of silence into nothing and makes a sporadic week look continuous — which is
    /// the opposite of what somebody is looking at it to find out.
    /// </summary>
    public IReadOnlyList<DailyUsage> DailyUsage(string stage, int days)
    {
        using var connection = Open();

        var since = DateTimeOffset.UtcNow.Date.AddDays(-(days - 1));

        var rows = connection.Query<(string Day, int Runs, long ElapsedMs, long AudioMs, long Tokens)>(
            """
            SELECT substr(started_at, 1, 10)                                AS Day,
                   COUNT(*)                                                 AS Runs,
                   COALESCE(SUM(elapsed_ms), 0)                             AS ElapsedMs,
                   COALESCE(SUM(audio_ms), 0)                               AS AudioMs,
                   COALESCE(SUM(prompt_tokens), 0)
                     + COALESCE(SUM(completion_tokens), 0)                  AS Tokens
            FROM processing_run
            WHERE stage = @stage AND started_at >= @since
            GROUP BY Day;
            """,
            new { stage, since = Iso(since) })
            .ToDictionary(r => r.Day);

        List<DailyUsage> series = [];

        for (var i = 0; i < days; i++)
        {
            var day = since.AddDays(i);
            var key = day.ToString("yyyy-MM-dd");

            rows.TryGetValue(key, out var found);

            series.Add(new DailyUsage
            {
                Day = DateOnly.FromDateTime(day.Date),
                Runs = found.Runs,
                ElapsedMs = found.ElapsedMs,
                AudioMs = found.AudioMs,
                Tokens = found.Tokens,
            });
        }

        return series;
    }

    /// <summary>The most recent run of one stage for one call, or null if it has never had one.</summary>
    public CallRun? LastRun(long callId, string stage)
    {
        using var connection = Open();

        return connection.QueryFirstOrDefault<CallRun>(
            """
            SELECT call_id AS CallId, engine AS Engine, elapsed_ms AS ElapsedMs,
                   audio_ms AS AudioMs, succeeded AS Succeeded,
                   speech_coverage AS SpeechCoverage
            FROM processing_run
            WHERE call_id = @callId AND stage = @stage
            ORDER BY id DESC LIMIT 1;
            """,
            new { callId, stage });
    }

    /// <summary>When one paid stage last SUCCEEDED for a call, and with which model.</summary>
    public sealed record RunStamp(string Engine, DateTimeOffset StartedAt);

    /// <summary>
    /// The trace a run leaves when it produced nothing.
    ///
    /// Findings, suggestions and warning notes are the ordinary evidence that a check ran, and a
    /// check that honestly finds nothing writes none of them. Without this the tab could not
    /// tell "never asked" from "asked, and the answer was no" — so the most expensive button in
    /// the application looked untouched after it had been pressed, and the user pressed it again
    /// to learn the same thing twice.
    ///
    /// Failed runs are excluded. They are recorded because they cost money, but calling a
    /// timed-out request a completed check is the same lie pointing the other way.
    /// </summary>
    public RunStamp? LastSuccessfulRun(long callId, string stage)
    {
        using var connection = Open();

        var row = connection.QueryFirstOrDefault<(string engine, string started_at)>(
            """
            SELECT engine, started_at FROM processing_run
             WHERE call_id = @callId AND stage = @stage AND succeeded = 1
             ORDER BY id DESC LIMIT 1;
            """,
            new { callId, stage });

        return row == default ? null : new RunStamp(row.engine, ParseIso(row.started_at));
    }

    /// <summary>
    /// How much of one call's transcript the model was unsure about.
    ///
    /// Counted rather than sampled, because it is the honest measure of whether the text can be
    /// trusted — and on a recording where one side was quiet or the microphone was wrong it is the
    /// difference between a transcript worth reading and one worth redoing.
    /// </summary>
    public (int Lines, int LowConfidence, int Overlapping) TranscriptQuality(long callId)
    {
        using var connection = Open();

        return connection.QueryFirst<(int, int, int)>(
            """
            SELECT COUNT(*)                                         AS Lines,
                   COALESCE(SUM(low_confidence), 0)                 AS LowConfidence,
                   COALESCE(SUM(overlaps_other_speaker), 0)         AS Overlapping
            FROM segment WHERE call_id = @callId;
            """,
            new { callId });
    }

    /// <summary>Totals for one stage over a window. All zero when nothing has run yet.</summary>
    public UsageTotals Usage(string stage, DateTimeOffset? since = null)
    {
        using var connection = Open();

        return connection.QueryFirstOrDefault<UsageTotals>(
            """
            SELECT
                COUNT(*)                                     AS Runs,
                COALESCE(SUM(succeeded = 0), 0)              AS Failures,
                COALESCE(SUM(elapsed_ms), 0)                 AS ElapsedMs,
                COALESCE(SUM(audio_ms), 0)                   AS AudioMs,
                COALESCE(SUM(prompt_tokens), 0)              AS PromptTokens,
                COALESCE(SUM(completion_tokens), 0)          AS CompletionTokens
            FROM processing_run
            WHERE stage = @stage AND (@since IS NULL OR started_at >= @since);
            """,
            new { stage, since = since is { } s ? Iso(s) : null }) ?? new UsageTotals();
    }

    /// <summary>
    /// Per-engine breakdown for one stage, busiest first.
    ///
    /// The point is comparison. "Transcription is slow" is not actionable; "the local model runs
    /// at 0.4× real time and the hosted one at 12×" is a decision.
    /// </summary>
    public IReadOnlyList<EngineUsage> UsageByEngine(string stage, DateTimeOffset? since = null)
    {
        using var connection = Open();

        return
        [
            .. connection.Query<EngineUsage>(
                """
                SELECT
                    engine                              AS Engine,
                    COUNT(*)                            AS Runs,
                    COALESCE(SUM(succeeded = 0), 0)     AS Failures,
                    COALESCE(SUM(elapsed_ms), 0)        AS ElapsedMs,
                    COALESCE(SUM(audio_ms), 0)          AS AudioMs,
                    COALESCE(SUM(prompt_tokens), 0)     AS PromptTokens,
                    COALESCE(SUM(completion_tokens), 0) AS CompletionTokens
                FROM processing_run
                WHERE stage = @stage AND (@since IS NULL OR started_at >= @since)
                GROUP BY engine
                ORDER BY Runs DESC;
                """,
                new { stage, since = since is { } s ? Iso(s) : null }),
        ];
    }

    // ---- deletion -----------------------------------------------------------

    /// <summary>
    /// Deletes one recording and everything derived from it.
    ///
    /// Separate from deleting a contact because the reasons are different and both are real. A
    /// contact is deleted to remove a person from the archive; a single call is deleted because
    /// that particular one should not have been kept — a misfire, a wrong number, a private
    /// conversation that happened to run through the same application.
    ///
    /// Without this the only way to remove one recording was to remove the whole person, which
    /// is the kind of gap that makes somebody stop trusting a recorder and turn it off.
    ///
    /// The mixed copy goes too. It is derived and rebuildable, which is exactly why forgetting it
    /// would be so bad: a delete that leaves a playable copy of the whole conversation on disk is
    /// not a delete.
    /// </summary>
    public DeletionResult DeleteCall(long callId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var files = connection.Query<string?>(
                """
                SELECT mic_path FROM call WHERE id = @callId AND mic_path IS NOT NULL
                UNION ALL
                SELECT far_path FROM call WHERE id = @callId AND far_path IS NOT NULL;
                """,
                new { callId }, transaction)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        // Segments, commitments, claims, flags and the summary cascade from the call row, and
        // the segment triggers keep the FTS index in step.
        connection.Execute("DELETE FROM call WHERE id = @callId;", new { callId }, transaction);

        transaction.Commit();

        var derived = files
            .Select(Audio.ConversationMix.PathFor)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return RemoveFiles(files.Concat(derived));
    }

    /// <summary>
    /// Erases audio from disk, reporting what could not be removed.
    ///
    /// Failures are returned rather than swallowed. A file held open by a player or sitting on a
    /// drive that went away is still a recording of somebody talking, and the user has to be told
    /// it is still out there rather than shown a success message.
    /// </summary>
    private static DeletionResult RemoveFiles(IEnumerable<string> files)
    {
        var removed = 0;
        var failed = new List<string>();

        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file)) continue;

                File.Delete(file);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(file);
            }
        }

        return new DeletionResult(removed, failed);
    }

    /// <summary>
    /// Contacts whose name contains what has been typed, for the "who was this?" box.
    ///
    /// Folded through the Turkish rules rather than SQL's LIKE, which lowercases with the
    /// Unicode defaults and therefore does not match İ against i or I against ı. Typing "ısıl"
    /// would silently fail to find "Işıl" — no error, just an empty list and a user who
    /// concludes the contact is not there and creates a second one.
    /// </summary>
    public IReadOnlyList<Contact> SearchContacts(string term, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];

        var needle = Text.TurkishText.NormalizeForSearch(term);
        if (needle.Length == 0) return [];

        using var connection = Open();

        return connection.Query<ContactRow>(
                """
                SELECT * FROM contact
                 WHERE instr(name_normalised, @needle) > 0
                 ORDER BY instr(name_normalised, @needle),
                          last_call_at DESC,
                          call_count DESC
                 LIMIT @limit;
                """,
                new { needle, limit })
            .Select(r => r.ToModel())
            .ToList();
    }

    /// <summary>
    /// Removes every trace of a contact and returns the audio files the caller must delete.
    ///
    /// Cascades take care of the rows; audio lives on disk and is returned rather than deleted
    /// here so that file removal stays the caller's explicit, auditable step.
    /// </summary>
    public DeletionResult DeleteContactCompletely(long contactId, string? photosDirectory = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var files = connection.Query<string?>(
                """
                SELECT mic_path FROM call WHERE contact_id = @contactId AND mic_path IS NOT NULL
                UNION ALL
                SELECT far_path FROM call WHERE contact_id = @contactId AND far_path IS NOT NULL;
                """,
                new { contactId }, transaction)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        // "Completely" includes the face. A delete that scrubs every word but leaves the
        // person's photo in the data folder has not kept its name.
        var photo = connection.ExecuteScalar<string?>(
            "SELECT photo_file FROM contact_profile WHERE contact_id = @contactId;",
            new { contactId }, transaction);

        var photoPath = photo is not null && photosDirectory is not null
            ? Path.Combine(photosDirectory, photo)
            : null;

        if (photoPath is not null) files.Add(photoPath);

        // Calls cascade to segments, commitments, claims, flags and summaries, and the segment
        // triggers keep the FTS index in step.
        connection.Execute("DELETE FROM call WHERE contact_id = @contactId;", new { contactId }, transaction);
        connection.Execute("DELETE FROM contact WHERE id = @contactId;", new { contactId }, transaction);

        transaction.Commit();

        // The audio is erased here rather than handed back for somebody else to deal with.
        //
        // This method is called "completely" and the product promises it: a delete that removes
        // the words but leaves the recording of somebody talking on disk is not a delete, it is a
        // worse outcome than never having offered one, because the user now believes it is gone.
        // Returning a list for a caller to maybe act on is exactly how that promise gets broken.
        //
        // The mixed copies are included for the same reason. They are derived and rebuildable,
        // which is precisely why leaving them behind would be so bad: each one is a playable
        // recording of the entire conversation.
        var derived = files
            .Select(Audio.ConversationMix.PathFor)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return RemoveFiles(files.Concat(derived));
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    /// <summary>One promise as the promise screens read it: the row, whose it is, and when it was made.</summary>
    public sealed record PromiseRow(Commitment Commitment, string ContactName, DateTimeOffset CallStartedAt);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    // ---- row types ----------------------------------------------------------
    // Dapper maps snake_case columns onto these, and ToModel() converts to the domain shape.

    private sealed class ContactRow
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public string name_normalised { get; set; } = "";
        public long app { get; set; }
        public string? handle { get; set; }
        public string created_at { get; set; } = "";
        public string? last_call_at { get; set; }
        public long call_count { get; set; }
        public string? notes { get; set; }

        public Contact ToModel() => new()
        {
            Id = id,
            Name = name,
            NameNormalised = name_normalised,
            App = (CallApp)app,
            Handle = handle,
            CreatedAt = ParseIso(created_at),
            LastCallAt = last_call_at is null ? null : ParseIso(last_call_at),
            CallCount = (int)call_count,
            Notes = notes,
        };
    }

    private sealed class CallRow
    {
        public long id { get; set; }
        public long? contact_id { get; set; }
        public long app { get; set; }
        public long direction { get; set; }
        public long kind { get; set; }
        public string started_at { get; set; } = "";
        public string? ended_at { get; set; }
        public long duration_ms { get; set; }
        public string? mic_path { get; set; }
        public string? far_path { get; set; }
        public long state { get; set; }
        public string? failure_reason { get; set; }
        public string? observed_title { get; set; }
        public string? capture_stats { get; set; }
        public long likely_no_headphones { get; set; }
        public long is_pinned { get; set; }
        public string? audio_sha256 { get; set; }
        public string? trimmed_at { get; set; }

        public Call ToModel() => new()
        {
            Id = id,
            ContactId = contact_id,
            App = (CallApp)app,
            Direction = (CallDirection)direction,
            Kind = (CallKind)kind,
            StartedAt = ParseIso(started_at),
            EndedAt = ended_at is null ? null : ParseIso(ended_at),
            Duration = TimeSpan.FromMilliseconds(duration_ms),
            MicPath = mic_path,
            FarPath = far_path,
            State = (ProcessingState)state,
            FailureReason = failure_reason,
            ObservedTitle = observed_title,
            CaptureStats = capture_stats,
            LikelyNoHeadphones = likely_no_headphones != 0,
            IsPinned = is_pinned != 0,
            AudioSha256 = audio_sha256,
            TrimmedAt = trimmed_at is null ? null : DateTimeOffset.Parse(trimmed_at),
        };
    }

    private sealed class SegmentRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public long is_me { get; set; }
        public long start_ms { get; set; }
        public long end_ms { get; set; }
        public string text { get; set; } = "";
        public string text_normalised { get; set; } = "";
        public double? avg_logprob { get; set; }
        public double? no_speech_prob { get; set; }
        public long low_confidence { get; set; }
        public long overlaps_other_speaker { get; set; }
        public long suspected_echo { get; set; }
        public string? words { get; set; }

        public Segment ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            IsMe = is_me != 0,
            StartMs = (int)start_ms,
            EndMs = (int)end_ms,
            Text = text,
            TextNormalised = text_normalised,
            Words = SegmentWords.Read(words),
            AvgLogprob = avg_logprob,
            NoSpeechProb = no_speech_prob,
            LowConfidence = low_confidence != 0,
            OverlapsOtherSpeaker = overlaps_other_speaker != 0,
            SuspectedEcho = suspected_echo != 0,
        };
    }

    private sealed class SearchHitRow
    {
        public long CallId { get; set; }
        public long SegmentId { get; set; }
        public long? ContactId { get; set; }
        public string? ContactName { get; set; }
        public string CallStartedAt { get; set; } = "";
        public long IsMe { get; set; }
        public long StartMs { get; set; }
        public string Text { get; set; } = "";

        public SearchHit ToModel() => new(
            CallId, SegmentId, ContactId, ContactName, ParseIso(CallStartedAt),
            IsMe != 0, (int)StartMs, Text);
    }

    private sealed class CommitmentRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public long? contact_id { get; set; }
        public long by_me { get; set; }
        public string quote { get; set; } = "";
        public long quote_start_ms { get; set; }
        public string obligation { get; set; } = "";
        public string? deadline_raw { get; set; }
        public string? deadline_date { get; set; }
        public string? amount { get; set; }
        public string? currency { get; set; }
        public long is_conditional { get; set; }
        public long status { get; set; }
        public long? fulfilled_by_call_id { get; set; }
        public long dismissed_by_user { get; set; }
        public string? created_at { get; set; }
        public string? fulfilled_at { get; set; }
        public string? decided_at { get; set; }
        public string? user_deadline_date { get; set; }
        public string? user_obligation { get; set; }
        public string? edited_at { get; set; }

        public Commitment ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            ContactId = contact_id,
            ByMe = by_me != 0,
            Quote = quote,
            QuoteStartMs = (int)quote_start_ms,
            Obligation = obligation,
            DeadlineRaw = deadline_raw,
            DeadlineDate = deadline_date is null ? null : DateOnly.Parse(deadline_date),
            Amount = amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            Currency = currency,
            IsConditional = is_conditional != 0,
            Status = (CommitmentStatus)status,
            FulfilledByCallId = fulfilled_by_call_id,
            DismissedByUser = dismissed_by_user != 0,
            CreatedAt = created_at is null ? null : ParseIso(created_at),
            FulfilledAt = fulfilled_at is null ? null : ParseIso(fulfilled_at),
            DecidedAt = decided_at is null ? null : ParseIso(decided_at),
            UserDeadlineDate = user_deadline_date is null ? null : DateOnly.Parse(user_deadline_date),
            UserObligation = user_obligation,
            EditedAt = edited_at is null ? null : ParseIso(edited_at),
        };
    }

    private sealed class ClaimRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public long? contact_id { get; set; }
        public long by_me { get; set; }
        public string quote { get; set; } = "";
        public long quote_start_ms { get; set; }
        public string entity { get; set; } = "";
        public string attribute { get; set; } = "";
        public string value { get; set; } = "";
        public string? numeric_value { get; set; }
        public string? unit { get; set; }
        public long low_confidence { get; set; }

        public Claim ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            ContactId = contact_id,
            ByMe = by_me != 0,
            Quote = quote,
            QuoteStartMs = (int)quote_start_ms,
            Entity = entity,
            Attribute = attribute,
            Value = value,
            NumericValue = numeric_value is null
                ? null
                : decimal.Parse(numeric_value, System.Globalization.CultureInfo.InvariantCulture),
            Unit = unit,
            LowConfidence = low_confidence != 0,
        };
    }

    private sealed class FlagRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public long? contact_id { get; set; }
        public long kind { get; set; }
        public string summary { get; set; } = "";
        public string quote { get; set; } = "";
        public long quote_start_ms { get; set; }
        public string? counter_quote { get; set; }
        public long? counter_call_id { get; set; }
        public long? counter_quote_start_ms { get; set; }
        public long low_confidence { get; set; }
        public long is_heuristic { get; set; }
        public long dismissed_by_user { get; set; }
        public string source { get; set; } = Flag.Sources.Pipeline;
        public string? confidence { get; set; }
        public string created_at { get; set; } = "";
        public string? decided_at { get; set; }
        public long? transcript_version_id { get; set; }

        public Flag ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            ContactId = contact_id,
            Kind = (FlagKind)kind,
            Summary = summary,
            Quote = quote,
            QuoteStartMs = (int)quote_start_ms,
            CounterQuote = counter_quote,
            CounterCallId = counter_call_id,
            CounterQuoteStartMs = counter_quote_start_ms is null ? null : (int)counter_quote_start_ms,
            LowConfidence = low_confidence != 0,
            IsHeuristic = is_heuristic != 0,
            DismissedByUser = dismissed_by_user != 0,
            Source = source,
            Confidence = confidence,
            CreatedAt = ParseIso(created_at),
            DecidedAt = decided_at is null ? null : ParseIso(decided_at),
            TranscriptVersionId = transcript_version_id,
        };
    }

    private sealed class ActionRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public long? contact_id { get; set; }
        public string action { get; set; } = "";
        public string? reason { get; set; }
        public string kind { get; set; } = "diger";
        public string quote { get; set; } = "";
        public long quote_start_ms { get; set; }
        public long quote_is_me { get; set; }
        public string? deadline_raw { get; set; }
        public string? deadline_date { get; set; }
        public long status { get; set; }
        public string? routed_note { get; set; }
        public string? model_used { get; set; }
        public string created_at { get; set; } = "";
        public long? transcript_version_id { get; set; }
        public string? decided_at { get; set; }

        public ActionItem ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            ContactId = contact_id,
            Action = action,
            Reason = reason,
            Kind = kind,
            Quote = quote,
            QuoteStartMs = (int)quote_start_ms,
            QuoteIsMe = quote_is_me != 0,
            DeadlineRaw = deadline_raw,
            DeadlineDate = deadline_date is null ? null : DateOnly.Parse(deadline_date),
            Status = (ActionStatus)status,
            RoutedNote = routed_note,
            ModelUsed = model_used,
            CreatedAt = ParseIso(created_at),
            TranscriptVersionId = transcript_version_id,
            DecidedAt = decided_at is null ? null : ParseIso(decided_at),
        };
    }

    private sealed class SummaryRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public string summary { get; set; } = "";
        public string? action_items { get; set; }
        public string? model_used { get; set; }
        public string created_at { get; set; } = "";
        public long? transcript_version_id { get; set; }

        public CallSummary ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            Summary = summary,
            ActionItems = action_items,
            ModelUsed = model_used,
            CreatedAt = ParseIso(created_at),
            TranscriptVersionId = transcript_version_id,
        };
    }

    private sealed class VerdictRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public string kind { get; set; } = "";
        public long? target_id { get; set; }
        public string quote_folded { get; set; } = "";
        public long start_ms { get; set; }
        public long verdict { get; set; }
        public string decided_at { get; set; } = "";

        public Verdict ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            Kind = kind,
            TargetId = target_id,
            QuoteFolded = quote_folded,
            StartMs = (int)start_ms,
            Value = (VerdictValue)verdict,
            DecidedAt = ParseIso(decided_at),
        };
    }

    /// <summary>One consistency run's stored halves, as the columns hold them.</summary>
    private sealed class ConsistencyNoteRow
    {
        public string note { get; set; } = "";
        public string? model_used { get; set; }
        public string created_at { get; set; } = "";
        public string? observations { get; set; }

        // A JSON column this build cannot read is "not recorded" rather than a crash — the same
        // treatment the habit cache gets, and for the same reason: a stored panel must never be
        // the thing that stops a window opening.
        public StoredConsistency ToModel()
        {
            IReadOnlyList<string>? kept = null;

            if (observations is not null)
            {
                try
                {
                    kept = JsonSerializer.Deserialize<List<string>>(observations);
                }
                catch (JsonException)
                {
                    kept = null;
                }
            }

            return new StoredConsistency(
                string.IsNullOrWhiteSpace(note) ? null : note, kept, model_used, ParseIso(created_at));
        }
    }

    /// <summary>One row per call: the counts and pointers DerivedFreshness judges from.</summary>
    private sealed class FreshnessRow
    {
        public long? current_version { get; set; }
        public long summary_count { get; set; }
        public long? summary_version { get; set; }
        public long reading_count { get; set; }
        public long? reading_version { get; set; }
        public long deception_count { get; set; }
        public long? deception_version { get; set; }
        public long consistency_count { get; set; }
        public long? consistency_version { get; set; }
        public long action_count { get; set; }
        public long action_known { get; set; }
        public long action_stale { get; set; }
        public long flag_count { get; set; }
        public long flag_known { get; set; }
        public long flag_stale { get; set; }
    }

    private sealed class ProsodyRow
    {
        public string json { get; set; } = "";
        public string audio_key { get; set; } = "";
        public string created_at { get; set; } = "";

        public StoredProsody ToModel() => new(json, audio_key, ParseIso(created_at));
    }

    private sealed class AudioEventRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public string channel { get; set; } = "";
        public long start_ms { get; set; }
        public long end_ms { get; set; }
        public string kind { get; set; } = "";

        public AudioEvent ToModel() => new(id, call_id, channel, (int)start_ms, (int)end_ms, kind);
    }

    private sealed class ContactReadingRow
    {
        public long id { get; set; }
        public long contact_id { get; set; }
        public string json { get; set; } = "";
        public string? model_used { get; set; }
        public long calls_covered { get; set; }
        public long? latest_call_id { get; set; }
        public string input_hash { get; set; } = "";
        public long excerpt_count { get; set; }
        public long rejected_count { get; set; }
        public long? user_verdict { get; set; }
        public string created_at { get; set; } = "";

        public StoredContactReading ToModel() => new(
            id, contact_id, json, model_used, (int)calls_covered, latest_call_id, input_hash,
            (int)excerpt_count, (int)rejected_count, (int?)user_verdict, ParseIso(created_at));
    }

    private sealed class HabitRow
    {
        public string json { get; set; } = "";
        public long lexicon_version { get; set; }
        public long? transcript_version_id { get; set; }
        public string created_at { get; set; } = "";

        public StoredHabits ToModel() => new(json, (int)lexicon_version, transcript_version_id, ParseIso(created_at));
    }

    private sealed class AskExchangeRow
    {
        public long id { get; set; }
        public long? call_id { get; set; }
        public long? contact_id { get; set; }
        public string? since_at { get; set; }
        public string? until_at { get; set; }
        public string question { get; set; } = "";
        public string answer { get; set; } = "";
        public string citations { get; set; } = "";
        public long insufficient { get; set; }
        public string? model_used { get; set; }
        public long? transcript_version_id { get; set; }
        public string asked_at { get; set; } = "";

        public StoredAskExchange ToModel() => new(
            id, call_id, contact_id,
            since_at is null ? null : ParseIso(since_at),
            until_at is null ? null : ParseIso(until_at),
            question, answer, citations, insufficient != 0, model_used,
            transcript_version_id, ParseIso(asked_at));
    }

    private sealed class HabitSeriesRaw
    {
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long? contact_id { get; set; }
        public long likely_no_headphones { get; set; }
        public string? engine { get; set; }
        public long lexicon_version { get; set; }
        public string json { get; set; } = "";

        public HabitSeriesRow ToModel() => new(
            call_id, ParseIso(started_at), contact_id, engine, likely_no_headphones != 0, (int)lexicon_version, json);
    }

    private sealed class LexemeRow
    {
        public long id { get; set; }
        public string kind { get; set; } = "";
        public string lexeme_folded { get; set; } = "";
        public string? suffixes { get; set; }
        public string lexeme { get; set; } = "";
        public long position { get; set; }

        public HabitLexeme ToModel() => new()
        {
            Id = id,
            Kind = kind,
            Lexeme = lexeme,
            LexemeFolded = lexeme_folded,
            Suffixes = Endings(suffixes),
            Position = (int)position,
        };

        // A row whose endings do not parse is a bare stem, not a missing stem: the user's word
        // stays in the dictionary whatever happened to the list beside it.
        private static IReadOnlyList<string> Endings(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private sealed class TacticRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public long? contact_id { get; set; }
        public long? transcript_version_id { get; set; }
        public string source { get; set; } = TacticEvidence.Sources.Deception;
        public string tactic { get; set; } = "";
        public long by_me { get; set; }
        public string quote { get; set; } = "";
        public long quote_start_ms { get; set; }
        public long low_confidence { get; set; }
        public string? model_used { get; set; }
        public long dismissed_by_user { get; set; }
        public string created_at { get; set; } = "";

        public TacticEvidence ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            ContactId = contact_id,
            TranscriptVersionId = transcript_version_id,
            Source = source,
            Tactic = tactic,
            ByMe = by_me != 0,
            Quote = quote,
            QuoteStartMs = (int)quote_start_ms,
            LowConfidence = low_confidence != 0,
            ModelUsed = model_used,
            DismissedByUser = dismissed_by_user != 0,
            CreatedAt = ParseIso(created_at),
        };
    }

    private sealed class SpeechActRow
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public long? contact_id { get; set; }
        public long by_me { get; set; }
        public string kind { get; set; } = SpeechAct.Kinds.Question;
        public string? answer_status { get; set; }
        public string quote { get; set; } = "";
        public long quote_start_ms { get; set; }
        public long low_confidence { get; set; }
        public string created_at { get; set; } = "";

        public SpeechAct ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            ContactId = contact_id,
            ByMe = by_me != 0,
            Kind = kind,
            AnswerStatus = answer_status,
            Quote = quote,
            QuoteStartMs = (int)quote_start_ms,
            LowConfidence = low_confidence != 0,
            CreatedAt = ParseIso(created_at),
        };
    }

    /// <summary>One evidence row crossed with one candidate verdict; the folding decides in code.</summary>
    private sealed class PatternRaw
    {
        public long is_flag { get; set; }
        public string kind { get; set; } = "";
        public string source { get; set; } = "";
        public long id { get; set; }
        public long call_id { get; set; }
        public string quote { get; set; } = "";
        public long start_ms { get; set; }
        public long low_confidence { get; set; }
        public long dismissed { get; set; }
        public string started_at { get; set; } = "";
        public string? verdict_quote { get; set; }
        public long? verdict_value { get; set; }
        public string? verdict_at { get; set; }
    }

    private sealed class PatternQuoteRaw
    {
        public long id { get; set; }
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long start_ms { get; set; }
        public long? by_me { get; set; }
        public long low_confidence { get; set; }
        public long dismissed { get; set; }
        public string quote { get; set; } = "";
        public string summary { get; set; } = "";
        public string? decided_at { get; set; }

        public PatternQuote ToModel(string kind, string source) => new(
            id, kind, source, call_id, ParseIso(started_at), (int)start_ms,
            by_me is null ? null : by_me != 0,
            low_confidence != 0, dismissed != 0, quote, summary,
            decided_at is null ? null : ParseIso(decided_at));
    }

    private sealed class FigureRaw
    {
        public long id { get; set; }
        public string entity { get; set; } = "";
        public string attribute { get; set; } = "";
        public string value { get; set; } = "";
        public string? numeric_value { get; set; }
        public string? unit { get; set; }
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long start_ms { get; set; }
        public string quote { get; set; } = "";
        public long low_confidence { get; set; }

        public FigureStop ToStop() => new(
            value,
            numeric_value is null
                ? null
                : decimal.Parse(numeric_value, System.Globalization.CultureInfo.InvariantCulture),
            unit,
            call_id,
            ParseIso(started_at),
            (int)start_ms,
            quote,
            low_confidence != 0);
    }

    private sealed class OwnWordRaw
    {
        public long is_promise { get; set; }
        public string subject { get; set; } = "";
        public string attribute { get; set; } = "";
        public string value { get; set; } = "";
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long start_ms { get; set; }
        public string quote { get; set; } = "";
        public long low_confidence { get; set; }
        public long? status { get; set; }
        public string? deadline { get; set; }

        public OwnWord ToModel() => new(
            is_promise != 0,
            subject,
            attribute,
            value,
            call_id,
            ParseIso(started_at),
            (int)start_ms,
            quote,
            low_confidence != 0,
            status is null ? null : (CommitmentStatus)status,
            deadline is null ? null : DateOnly.Parse(deadline));
    }

    private sealed class SpeechActCountRaw
    {
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long measured { get; set; }
        public long asked { get; set; }
        public long answered { get; set; }
        public long partial { get; set; }
        public long evaded { get; set; }
        public long deflected { get; set; }

        public SpeechActCounts ToModel() => new(
            call_id, ParseIso(started_at), measured != 0,
            (int)asked, (int)answered, (int)partial, (int)evaded, (int)deflected);
    }

    private sealed class ContactSeriesRaw
    {
        public long call_id { get; set; }
        public string started_at { get; set; } = "";
        public long direction { get; set; }
        public string? json { get; set; }

        // A cache row this build can no longer read is "not measured", not a crash: the trend
        // says so with its own denominator rather than pretending the call had no talking in it.
        public ContactCallPoint ToModel() => new(
            call_id,
            ParseIso(started_at),
            (CallDirection)direction,
            json is null ? null : Analysis.HabitSnapshot.FromJson(json)?.Talk);
    }
}
