namespace VoiceTranscript.Core.Storage;

/// <summary>
/// The SQLite schema, applied by <see cref="Database"/> on open.
///
/// Two decisions are worth reading before changing anything here.
///
/// TURKISH SEARCH. FTS5 indexes a separate normalised column rather than the visible text. Its
/// unicode61 tokenizer applies standard Unicode case folding, which is wrong for Turkish: a
/// search for "ışık" does not match "IŞIK". It fails silently — zero rows come back and it looks
/// like the data was never stored. Both the indexed column and the query string are folded by
/// TurkishText.NormalizeForSearch, so every spelling of a word lands in the same bucket.
///
/// EVIDENCE, NOT VERDICTS. Commitments, claims and flags all carry a verbatim quote and the
/// millisecond it was spoken. That is what lets the user click a line and hear it. Nothing in
/// this schema stores a trust score or a judgement about a person, and nothing should.
/// </summary>
public static class Schema
{
    public const int Version = 2;

    public static readonly string[] Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS contact (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            name             TEXT    NOT NULL,
            name_normalised  TEXT    NOT NULL,
            app              INTEGER NOT NULL DEFAULT 0,
            handle           TEXT,
            created_at       TEXT    NOT NULL,
            last_call_at     TEXT,
            call_count       INTEGER NOT NULL DEFAULT 0,
            notes            TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_contact_normalised ON contact(name_normalised);",

        """
        CREATE TABLE IF NOT EXISTS title_binding (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            title_pattern TEXT    NOT NULL,
            contact_id    INTEGER NOT NULL REFERENCES contact(id) ON DELETE CASCADE,
            app           INTEGER NOT NULL DEFAULT 0,
            times_used    INTEGER NOT NULL DEFAULT 0,
            last_used_at  TEXT    NOT NULL,
            UNIQUE(title_pattern, app)
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS call (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id           INTEGER REFERENCES contact(id) ON DELETE SET NULL,
            app                  INTEGER NOT NULL DEFAULT 0,
            direction            INTEGER NOT NULL DEFAULT 0,
            kind                 INTEGER NOT NULL DEFAULT 0,
            started_at           TEXT    NOT NULL,
            ended_at             TEXT,
            duration_ms          INTEGER NOT NULL DEFAULT 0,
            mic_path             TEXT,
            far_path             TEXT,
            state                INTEGER NOT NULL DEFAULT 0,
            failure_reason       TEXT,
            observed_title       TEXT,
            capture_stats        TEXT,
            likely_no_headphones INTEGER NOT NULL DEFAULT 0,
            is_pinned            INTEGER NOT NULL DEFAULT 0,
            audio_sha256         TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_call_contact ON call(contact_id, started_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_call_state ON call(state);",
        "CREATE INDEX IF NOT EXISTS ix_call_started ON call(started_at DESC);",

        """
        CREATE TABLE IF NOT EXISTS segment (
            id                     INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id                INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            is_me                  INTEGER NOT NULL,
            start_ms               INTEGER NOT NULL,
            end_ms                 INTEGER NOT NULL,
            text                   TEXT    NOT NULL,
            text_normalised        TEXT    NOT NULL,
            avg_logprob            REAL,
            no_speech_prob         REAL,
            low_confidence         INTEGER NOT NULL DEFAULT 0,
            overlaps_other_speaker INTEGER NOT NULL DEFAULT 0,
            suspected_echo         INTEGER NOT NULL DEFAULT 0
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_segment_call ON segment(call_id, start_ms);",

        // The index holds only the folded text. The visible text is fetched from `segment` by
        // rowid, so the two never drift apart.
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS segment_fts USING fts5(
            text_normalised,
            content='segment',
            content_rowid='id',
            tokenize='unicode61 remove_diacritics 2'
        );
        """,
        """
        CREATE TRIGGER IF NOT EXISTS segment_fts_ai AFTER INSERT ON segment BEGIN
            INSERT INTO segment_fts(rowid, text_normalised) VALUES (new.id, new.text_normalised);
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS segment_fts_ad AFTER DELETE ON segment BEGIN
            INSERT INTO segment_fts(segment_fts, rowid, text_normalised)
            VALUES ('delete', old.id, old.text_normalised);
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS segment_fts_au AFTER UPDATE ON segment BEGIN
            INSERT INTO segment_fts(segment_fts, rowid, text_normalised)
            VALUES ('delete', old.id, old.text_normalised);
            INSERT INTO segment_fts(rowid, text_normalised) VALUES (new.id, new.text_normalised);
        END;
        """,

        // ---- written messages ------------------------------------------------------------
        //
        // Imported from a platform's own export rather than read out of its local storage. That
        // is a product decision as much as a technical one: the encrypted store of a messaging
        // app is not ours to open, breaks on every update, and is indistinguishable from what
        // spyware does. Telegram publishes a full export of the user's own data, so that is the
        // door used.
        //
        // Messages sit alongside calls rather than inside them. A conversation with somebody
        // happens across both, and the whole point of bringing text in is that a price said on
        // the phone can be compared with a price written two days later — which is a comparison
        // neither source can make alone.
        """
        CREATE TABLE IF NOT EXISTS message (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id      INTEGER NOT NULL REFERENCES contact(id) ON DELETE CASCADE,

            -- Which application it came from, so the interface can say so and an import can be
            -- replaced without touching the other platform's messages.
            source          INTEGER NOT NULL,

            -- The identifier the platform gave it. Together with the source this is what makes
            -- re-importing the same export idempotent instead of doubling every conversation.
            external_id     TEXT    NOT NULL,

            sent_at         TEXT    NOT NULL,
            is_me           INTEGER NOT NULL,
            text            TEXT    NOT NULL,
            text_normalised TEXT    NOT NULL,

            -- Present when the message answered another one, so a reply can be read with the
            -- thing it replied to rather than on its own.
            reply_to        TEXT,

            -- Telegram reports edits. A figure that was written and then changed is exactly the
            -- kind of thing this application exists to notice.
            edited_at       TEXT,

            -- A photo or file with no caption carries no words but still says somebody wrote at
            -- that moment, which matters when reading a conversation back.
            has_attachment  INTEGER NOT NULL DEFAULT 0,

            imported_at     TEXT    NOT NULL,

            UNIQUE(source, external_id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_message_contact ON message(contact_id, sent_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_message_sent ON message(sent_at DESC);",

        // The same arrangement the transcript uses: the index holds only the folded text and the
        // visible text is fetched by rowid, so a Turkish search finds a written "IŞIK" exactly as
        // it finds a spoken one.
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS message_fts USING fts5(
            text_normalised,
            content='message',
            content_rowid='id',
            tokenize='unicode61 remove_diacritics 2'
        );
        """,
        """
        CREATE TRIGGER IF NOT EXISTS message_fts_ai AFTER INSERT ON message BEGIN
            INSERT INTO message_fts(rowid, text_normalised) VALUES (new.id, new.text_normalised);
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS message_fts_ad AFTER DELETE ON message BEGIN
            INSERT INTO message_fts(message_fts, rowid, text_normalised)
            VALUES ('delete', old.id, old.text_normalised);
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS message_fts_au AFTER UPDATE ON message BEGIN
            INSERT INTO message_fts(message_fts, rowid, text_normalised)
            VALUES ('delete', old.id, old.text_normalised);
            INSERT INTO message_fts(rowid, text_normalised) VALUES (new.id, new.text_normalised);
        END;
        """,

        """
        CREATE TABLE IF NOT EXISTS commitment (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id             INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            contact_id          INTEGER REFERENCES contact(id) ON DELETE CASCADE,
            by_me               INTEGER NOT NULL DEFAULT 0,
            quote               TEXT    NOT NULL,
            quote_start_ms      INTEGER NOT NULL DEFAULT 0,
            obligation          TEXT    NOT NULL,
            deadline_raw        TEXT,
            deadline_date       TEXT,
            amount              TEXT,
            currency            TEXT,
            is_conditional      INTEGER NOT NULL DEFAULT 0,
            status              INTEGER NOT NULL DEFAULT 0,
            fulfilled_by_call_id INTEGER REFERENCES call(id) ON DELETE SET NULL,
            dismissed_by_user   INTEGER NOT NULL DEFAULT 0
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_commitment_contact ON commitment(contact_id, status);",

        """
        CREATE TABLE IF NOT EXISTS claim (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id        INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            contact_id     INTEGER REFERENCES contact(id) ON DELETE CASCADE,
            by_me          INTEGER NOT NULL DEFAULT 0,
            quote          TEXT    NOT NULL,
            quote_start_ms INTEGER NOT NULL DEFAULT 0,
            entity         TEXT    NOT NULL,
            attribute      TEXT    NOT NULL,
            value          TEXT    NOT NULL,
            numeric_value  TEXT,
            unit           TEXT,
            low_confidence INTEGER NOT NULL DEFAULT 0
        );
        """,
        // The join that finds contradictions and changed amounts without any model involvement.
        "CREATE INDEX IF NOT EXISTS ix_claim_lookup ON claim(contact_id, entity, attribute);",

        """
        CREATE TABLE IF NOT EXISTS flag (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id               INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            contact_id            INTEGER REFERENCES contact(id) ON DELETE CASCADE,
            kind                  INTEGER NOT NULL,
            summary               TEXT    NOT NULL,
            quote                 TEXT    NOT NULL,
            quote_start_ms        INTEGER NOT NULL DEFAULT 0,
            counter_quote         TEXT,
            counter_call_id       INTEGER REFERENCES call(id) ON DELETE SET NULL,
            counter_quote_start_ms INTEGER,
            low_confidence        INTEGER NOT NULL DEFAULT 0,
            is_heuristic          INTEGER NOT NULL DEFAULT 0,
            dismissed_by_user     INTEGER NOT NULL DEFAULT 0,
            created_at            TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_flag_contact ON flag(contact_id, dismissed_by_user);",
        "CREATE INDEX IF NOT EXISTS ix_flag_call ON flag(call_id);",

        """
        CREATE TABLE IF NOT EXISTS call_summary (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id    INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            summary    TEXT    NOT NULL,
            action_items TEXT,
            model_used TEXT,
            created_at TEXT    NOT NULL,
            UNIQUE(call_id)
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS setting (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,

        // What the user wrote about a conversation themselves.
        //
        // Its own table rather than a column on `call`, and that is forced rather than preferred:
        // Migrate() runs CREATE TABLE IF NOT EXISTS over this list and there is no ALTER TABLE
        // machinery, so adding a column would silently do nothing on every database that already
        // exists — which is all of them.
        //
        // Kept apart from the summary on purpose. Everything else the archive holds about a call
        // was produced by a machine and is replaced whenever the call is analysed again; this is
        // the one thing a person wrote, and it must survive every reprocess untouched.
        """
        CREATE TABLE IF NOT EXISTS call_note (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            note       TEXT    NOT NULL,
            updated_at TEXT    NOT NULL
        );
        """,
    ];
}
