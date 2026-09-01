using System.Data;
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
    public void RememberTitle(string title, long contactId, CallApp app)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (pattern.Length == 0) return;

        using var connection = Open();
        connection.Execute(
            """
            INSERT INTO title_binding (title_pattern, contact_id, app, times_used, last_used_at)
            VALUES (@pattern, @contactId, @app, 1, @now)
            ON CONFLICT(title_pattern, app) DO UPDATE SET
                contact_id   = excluded.contact_id,
                times_used   = title_binding.times_used + 1,
                last_used_at = excluded.last_used_at;
            """,
            new { pattern, contactId, app = (int)app, now = Iso(DateTimeOffset.UtcNow) });
    }

    public long? ResolveTitle(string? title, CallApp app)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (pattern.Length == 0) return null;

        using var connection = Open();
        return connection.QueryFirstOrDefault<long?>(
            "SELECT contact_id FROM title_binding WHERE title_pattern = @pattern AND app = @app;",
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
    public long? AssignContact(long callId, long contactId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var previous = connection.QueryFirstOrDefault<long?>(
            "SELECT contact_id FROM call WHERE id = @callId;", new { callId }, transaction);

        connection.Execute(
            "UPDATE call SET contact_id = @contactId WHERE id = @callId;",
            new { contactId, callId }, transaction);

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
    /// Tables whose rows belong to a contact by way of the call they came from.
    ///
    /// Listed once so that adding another derived table is a single edit rather than a bug that
    /// only appears after somebody moves a call.
    /// </summary>
    private static readonly string[] LedgerTables = ["commitment", "claim", "flag"];

    /// <summary>
    /// How many ledger rows a call produced.
    ///
    /// Shown before a move is confirmed. A call is not one row — the promises, figures and flags
    /// taken out of it are filed against the same person and travel with it — and somebody moving
    /// a conversation to a different contact is also moving those, which is not obvious and is
    /// worth saying before rather than after.
    /// </summary>
    public int CountLedgerEntriesForCall(long callId)
    {
        using var connection = Open();

        return LedgerTables.Sum(table => connection.ExecuteScalar<int>(
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

        // Profile facts follow the person. Fields simply move; for the profile row the
        // destination's entries win where both wrote one — a merge must never overwrite what the
        // user typed on the contact they are keeping.
        connection.Execute(
            "UPDATE contact_field SET contact_id = @intoContactId WHERE contact_id = @fromContactId;",
            new { intoContactId, fromContactId }, transaction);

        connection.Execute(
            """
            INSERT INTO contact_profile (contact_id, photo_file, birth_date, updated_at)
            SELECT @intoContactId, photo_file, birth_date, updated_at
            FROM contact_profile WHERE contact_id = @fromContactId
            ON CONFLICT(contact_id) DO UPDATE SET
                photo_file = COALESCE(contact_profile.photo_file, excluded.photo_file),
                birth_date = COALESCE(contact_profile.birth_date, excluded.birth_date);
            """,
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

    public IReadOnlyList<Call> ListCalls(long? contactId = null, int limit = 200)
    {
        using var connection = Open();

        var sql = contactId is null
            ? "SELECT * FROM call ORDER BY started_at DESC LIMIT @limit;"
            : "SELECT * FROM call WHERE contact_id = @contactId ORDER BY started_at DESC LIMIT @limit;";

        return [.. connection.Query<CallRow>(sql, new { contactId, limit }).Select(r => r.ToModel())];
    }

    public IReadOnlyList<Call> CallsAwaitingProcessing()
    {
        using var connection = Open();
        return [.. connection.Query<CallRow>(
            "SELECT * FROM call WHERE state IN (0, 1) ORDER BY started_at ASC;")
            .Select(r => r.ToModel())];
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
                                     overlaps_other_speaker, suspected_echo)
                VALUES (@callId, @isMe, @startMs, @endMs, @text, @normalised,
                        @avgLogprob, @noSpeechProb, @lowConfidence, @overlaps, @echo);
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
              AND cm.deadline_date IS NOT NULL
              AND cm.deadline_date < @today
            ORDER BY cm.deadline_date;
            """,
            (commitment, name) => (commitment, name),
            new { today = today.ToString("yyyy-MM-dd") },
            splitOn: "name");

        return [.. rows.Select(r => (r.Item1.ToModel(), r.Item2 ?? "Bilinmeyen"))];
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
              CASE WHEN cm.deadline_date IS NULL THEN 1 ELSE 0 END,
              cm.deadline_date,
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
            "UPDATE commitment SET dismissed_by_user = 1 WHERE id = @commitmentId;",
            new { commitmentId });
    }

    /// <summary>Marks a promise as kept, so it stops appearing as outstanding.</summary>
    public void FulfilCommitment(long commitmentId, long? byCallId = null)
    {
        using var connection = Open();
        connection.Execute(
            "UPDATE commitment SET status = 1, fulfilled_by_call_id = @byCallId WHERE id = @commitmentId;",
            new { commitmentId, byCallId });
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

        connection.Execute("DELETE FROM commitment WHERE call_id = @callId;", new { callId }, transaction);
        connection.Execute("DELETE FROM claim WHERE call_id = @callId;", new { callId }, transaction);

        // A flag the user has already dismissed stays dismissed. Reprocessing must not bring back
        // a judgement they have explicitly rejected — that is how a ledger stops being read.
        //
        // And only the pipeline's own flags: the consistency check's findings were paid for
        // separately and belong to a different button — rebuilding the ledger must not erase them.
        connection.Execute(
            "DELETE FROM flag WHERE call_id = @callId AND dismissed_by_user = 0 AND source = @source;",
            new { callId, source = Flag.Sources.Pipeline }, transaction);

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

    /// <summary>Flags for one conversation, oldest first — the order the evidence happened in.</summary>
    public IReadOnlyList<Flag> FlagsOf(long callId, bool includeDismissed = false)
    {
        using var connection = Open();

        var sql = includeDismissed
            ? "SELECT * FROM flag WHERE call_id = @callId ORDER BY quote_start_ms, id;"
            : "SELECT * FROM flag WHERE call_id = @callId AND dismissed_by_user = 0 ORDER BY quote_start_ms, id;";

        return [.. connection.Query<FlagRow>(sql, new { callId }).Select(r => r.ToModel())];
    }

    /// <summary>Saves the consistency check's overall note for a conversation (one per call).</summary>
    public void SaveConsistencyNote(long callId, string note, string? modelUsed)
    {
        using var connection = Open();

        connection.Execute(
            """
            INSERT INTO consistency_note (call_id, note, model_used, created_at)
            VALUES (@callId, @note, @modelUsed, @now)
            ON CONFLICT(call_id) DO UPDATE SET
                note = excluded.note, model_used = excluded.model_used, created_at = excluded.created_at;
            """,
            new { callId, note, modelUsed, now = Iso(DateTimeOffset.UtcNow) });
    }

    /// <summary>The stored warning note, with which model wrote it and when. Null when none.</summary>
    public (string Note, string? ModelUsed, DateTimeOffset CreatedAt)? GetConsistencyNote(long callId)
    {
        using var connection = Open();

        var row = connection.QuerySingleOrDefault<(string Note, string? ModelUsed, string CreatedAt)>(
            "SELECT note, model_used, created_at FROM consistency_note WHERE call_id = @callId;",
            new { callId });

        return row == default ? null : (row.Note, row.ModelUsed, ParseIso(row.CreatedAt));
    }

    public long InsertCommitment(Commitment commitment)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO commitment (call_id, contact_id, by_me, quote, quote_start_ms, obligation,
                                    deadline_raw, deadline_date, amount, currency, is_conditional,
                                    status, fulfilled_by_call_id, dismissed_by_user)
            VALUES (@CallId, @ContactId, @ByMe, @Quote, @QuoteStartMs, @Obligation,
                    @DeadlineRaw, @DeadlineDate, @Amount, @Currency, @IsConditional,
                    @Status, @FulfilledByCallId, 0)
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
                              source, confidence, created_at)
            VALUES (@CallId, @ContactId, @Kind, @Summary, @Quote, @QuoteStartMs,
                    @CounterQuote, @CounterCallId, @CounterQuoteStartMs,
                    @LowConfidence, @IsHeuristic, 0, @Source, @Confidence, @CreatedAt)
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
        connection.Execute("UPDATE flag SET dismissed_by_user = 1 WHERE id = @flagId;", new { flagId });
    }

    public IReadOnlyList<Commitment> GetOpenCommitments(long contactId)
    {
        using var connection = Open();
        return [.. connection.Query<CommitmentRow>(
            """
            SELECT * FROM commitment
            WHERE contact_id = @contactId AND status = 0 AND dismissed_by_user = 0
            ORDER BY COALESCE(deadline_date, '9999-12-31');
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
            INSERT INTO call_summary (call_id, summary, action_items, model_used, created_at)
            VALUES (@CallId, @Summary, @ActionItems, @ModelUsed, @CreatedAt)
            ON CONFLICT(call_id) DO UPDATE SET
                summary      = excluded.summary,
                action_items = excluded.action_items,
                model_used   = excluded.model_used,
                created_at   = excluded.created_at;
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

    /// <summary>
    /// Every conversation carrying a tag, newest first — the tag as a query of its own.
    ///
    /// Shaped as search hits so the search screen can browse a tag with no words typed: the
    /// "text" of each row is the call's one-line summary when one exists, or its size when not.
    /// SegmentId 0 and StartMs 0 mean "the conversation, from the top" to everything downstream.
    /// </summary>
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

    /// <summary>
    /// Moves every timestamp this call owns onto the trimmed timeline, atomically.
    ///
    /// One transaction across four tables, because a half-applied shift is worse than none: a
    /// segment on the new clock beside a quote on the old one means clicking a quote plays the
    /// wrong moment — precisely the betrayal this product exists to make impossible. The stamp
    /// is written in the same transaction, so "was this recording shifted" and "were its rows
    /// shifted" can never answer differently.
    /// </summary>
    public void ApplyTrim(
        long callId,
        IReadOnlyList<(long StartMs, long RemovedMs)> cuts,
        TimeSpan newDuration)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // Row by row through the same mapping the audio went through. SQL could express the
        // arithmetic, but not legibly, and a call has hundreds of rows, not millions.
        var segments = connection.Query<(long Id, long StartMs, long EndMs)>(
            "SELECT id, start_ms, end_ms FROM segment WHERE call_id = @callId;",
            new { callId }, transaction);

        foreach (var (id, startMs, endMs) in segments)
        {
            connection.Execute(
                "UPDATE segment SET start_ms = @s, end_ms = @e WHERE id = @id;",
                new
                {
                    id,
                    s = Audio.SilenceTrimmer.MapMs(startMs, cuts),
                    e = Audio.SilenceTrimmer.MapMs(endMs, cuts),
                }, transaction);
        }

        foreach (var table in new[] { "commitment", "claim", "flag" })
        {
            var quotes = connection.Query<(long Id, long QuoteStartMs)>(
                $"SELECT id, quote_start_ms FROM {table} WHERE call_id = @callId;",
                new { callId }, transaction);

            foreach (var (id, quoteStartMs) in quotes)
            {
                connection.Execute(
                    $"UPDATE {table} SET quote_start_ms = @q WHERE id = @id;",
                    new { id, q = Audio.SilenceTrimmer.MapMs(quoteStartMs, cuts) }, transaction);
            }
        }

        var counters = connection.Query<(long Id, long? CounterMs)>(
            "SELECT id, counter_quote_start_ms FROM flag WHERE call_id = @callId AND counter_quote_start_ms IS NOT NULL;",
            new { callId }, transaction);

        foreach (var (id, counterMs) in counters)
        {
            connection.Execute(
                "UPDATE flag SET counter_quote_start_ms = @q WHERE id = @id;",
                new { id, q = Audio.SilenceTrimmer.MapMs(counterMs!.Value, cuts) }, transaction);
        }

        connection.Execute(
            "UPDATE call SET duration_ms = @d, trimmed_at = @now WHERE id = @callId;",
            new { callId, d = (long)newDuration.TotalMilliseconds, now = Iso(DateTimeOffset.UtcNow) },
            transaction);

        transaction.Commit();
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
                  AND NOT EXISTS (SELECT 1 FROM board_card b WHERE b.call_id = c.id)
                  AND NOT EXISTS (SELECT 1 FROM call_note n WHERE n.call_id = c.id)
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

        foreach (var path in new[] { call.MicPath, call.FarPath })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception)
            {
                // A file held open by a player stays. The row is only cleared for files that
                // actually went, so the next sweep tries again rather than losing track of it.
                return removed;
            }
        }

        using var connection = Open();

        connection.Execute(
            "UPDATE call SET mic_path = NULL, far_path = NULL WHERE id = @callId;", new { callId });

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
        bool succeeded = true)
    {
        try
        {
            using var connection = Open();

            connection.Execute(
                """
                INSERT INTO processing_run
                    (call_id, stage, engine, started_at, elapsed_ms, audio_ms,
                     prompt_tokens, completion_tokens, succeeded)
                VALUES
                    (@callId, @stage, @engine, @startedAt, @elapsedMs, @audioMs,
                     @promptTokens, @completionTokens, @succeeded);
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
                });
        }
        catch (Exception)
        {
            // See above.
        }
    }

    /// <summary>
    /// The most recent run of one stage for every call that has had one.
    ///
    /// One query rather than one per row: the processing screen lists up to two thousand calls, and
    /// asking the database once per row is how a screen that opens instantly becomes one that
    /// hangs for a second every time it refreshes.
    ///
    /// Latest is taken by identity rather than by timestamp — the column is an autoincrement, and
    /// two runs of the same call in the same second are otherwise a coin toss.
    /// </summary>
    public IReadOnlyDictionary<long, CallRun> LastRuns(string stage)
    {
        using var connection = Open();

        return connection.Query<CallRun>(
            """
            SELECT r.call_id    AS CallId,
                   r.engine     AS Engine,
                   r.elapsed_ms AS ElapsedMs,
                   r.audio_ms   AS AudioMs,
                   r.succeeded  AS Succeeded
            FROM processing_run r
            JOIN (
                SELECT MAX(id) AS id
                FROM processing_run
                WHERE stage = @stage AND call_id IS NOT NULL
                GROUP BY call_id
            ) latest ON latest.id = r.id;
            """,
            new { stage })
            .ToDictionary(r => r.CallId);
    }

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
                   audio_ms AS AudioMs, succeeded AS Succeeded
            FROM processing_run
            WHERE call_id = @callId AND stage = @stage
            ORDER BY id DESC LIMIT 1;
            """,
            new { callId, stage });
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

        public Segment ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            IsMe = is_me != 0,
            StartMs = (int)start_ms,
            EndMs = (int)end_ms,
            Text = text,
            TextNormalised = text_normalised,
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

        public CallSummary ToModel() => new()
        {
            Id = id,
            CallId = call_id,
            Summary = summary,
            ActionItems = action_items,
            ModelUsed = model_used,
            CreatedAt = ParseIso(created_at),
        };
    }
}
