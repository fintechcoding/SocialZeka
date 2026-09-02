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
    public const int Version = 9;

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

            -- Set once this pattern has been seen belonging to two different people.
            --
            -- That is proof the title does not identify anybody — it is the chat that happened to
            -- be open, or a WebView2 page title, or the application's own furniture. Before this
            -- existed, the first person labelled against such a title captured every later call:
            -- WhatsApp put every conversation under whoever was named first, silently, with no
            -- prompt, because ResolveTitle found a match and filed the call without asking.
            unreliable    INTEGER NOT NULL DEFAULT 0,

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
            audio_sha256         TEXT,
            trimmed_at           TEXT
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

            -- Who wrote the row: 'pipeline' or 'consistency'. Ownership — each writer clears
            -- only its own rows, so neither re-run erases the other's findings.
            source                TEXT    NOT NULL DEFAULT 'pipeline',

            -- The model's stated confidence for consistency findings (dusuk/orta/yuksek).
            confidence            TEXT,

            created_at            TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_flag_contact ON flag(contact_id, dismissed_by_user);",
        "CREATE INDEX IF NOT EXISTS ix_flag_call ON flag(call_id);",

        // The consistency check's overall warning note, one per conversation. Separate from
        // flags because it is a synthesis over them, not one more piece of evidence — and it
        // is rewritten wholesale on every re-run while dismissed flags must survive.
        """
        CREATE TABLE IF NOT EXISTS consistency_note (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            note       TEXT    NOT NULL,
            model_used TEXT,
            created_at TEXT    NOT NULL
        );
        """,

        // The model's proposed next moves for the USER, one row per suggestion. Machine-owned:
        // suggestions never write into user spaces — routing to a reminder or the board happens
        // only through the user's click, and a hidden suggestion stays hidden across re-runs.
        // Every row is anchored to a verbatim, verified quote; unanchored suggestions never
        // reach this table.
        """
        CREATE TABLE IF NOT EXISTS action_item (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id        INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            contact_id     INTEGER REFERENCES contact(id) ON DELETE CASCADE,
            action         TEXT    NOT NULL,
            reason         TEXT,
            kind           TEXT    NOT NULL DEFAULT 'diger',
            quote          TEXT    NOT NULL,
            quote_start_ms INTEGER NOT NULL DEFAULT 0,
            quote_is_me    INTEGER NOT NULL DEFAULT 0,
            deadline_raw   TEXT,
            deadline_date  TEXT,

            -- 0 open, 1 done, 2 hidden, 3 routed (to a reminder or the board).
            status         INTEGER NOT NULL DEFAULT 0,
            routed_note    TEXT,
            model_used     TEXT,
            created_at     TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_action_call ON action_item(call_id);",
        "CREATE INDEX IF NOT EXISTS ix_action_open ON action_item(status, deadline_date);",

        // The model's free-form reading of one conversation, stored as the JSON it produced.
        // Deliberately a dead end in the data model: nothing joins on it, nothing feeds it to
        // other prompts, nothing surfaces it outside the one panel beside the evidence — a
        // subjective reading lives next to the transcript it read, and nowhere else.
        """
        CREATE TABLE IF NOT EXISTS reading_note (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            json       TEXT    NOT NULL,
            model_used TEXT,
            created_at TEXT    NOT NULL
        );
        """,

        // The opt-in deception/manipulation assessment. Same dead-end rules as the reading:
        // stored as the enforced shape, joined by nothing, fed to no other prompt. It exists
        // because the user explicitly asked to hear the model's opinion — and it is stored as
        // an opinion, never as evidence.
        """
        CREATE TABLE IF NOT EXISTS deception_note (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            json       TEXT    NOT NULL,
            model_used TEXT,
            created_at TEXT    NOT NULL
        );
        """,

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

        // Labels the user put on conversations: "tehdit edildik", "önemli", whatever they need.
        //
        // Free vocabulary on purpose. The board's lanes are fixed because they are the product's
        // own structure; a tag is the user's word for what a conversation was, and nobody can
        // enumerate those in advance. Folded copy for identity, so "Önemli" and "ONEMLI" are one
        // tag while the user's own spelling is what the screen shows.
        //
        // User data, like call_note: reprocessing may never touch this table.
        """
        CREATE TABLE IF NOT EXISTS call_tag (
            call_id    INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            tag        TEXT    NOT NULL,
            tag_folded TEXT    NOT NULL,
            created_at TEXT    NOT NULL,
            PRIMARY KEY (call_id, tag_folded)
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_call_tag ON call_tag(tag_folded);",

        // How a tag LOOKS: its icon and colour, the way Outlook gives a category both. Separate
        // from call_tag on purpose — a tag exists the moment somebody types it on a call, with or
        // without a row here, and deleting a definition must never delete the taggings. Rows here
        // are also what the "default tags" form edits: the vocabulary offered before any call has
        // been tagged at all.
        //
        // User data, like call_tag: reprocessing may never touch this table.
        """
        CREATE TABLE IF NOT EXISTS tag_def (
            tag_folded TEXT    PRIMARY KEY,
            tag        TEXT    NOT NULL,
            icon       TEXT    NOT NULL,
            color      TEXT    NOT NULL,
            position   INTEGER NOT NULL DEFAULT 0
        );
        """,

        // What the user knows about a person: photo, birth date, and free-form labelled facts.
        //
        // USER-ENTERED ONLY. Nothing in the analysis pipeline may write these tables. The ledger
        // holds what the machine heard, each claim with its quote; this holds what the user typed,
        // which needs no quote because they are its source. The two must never mix — a birthday
        // "extracted" from a call would be the application asserting things about people, which is
        // exactly what this product refuses to do.
        //
        // Photo and birth date are fixed columns because the application computes with them
        // (file lifecycle, upcoming-day arithmetic). Everything else is label+value rows: nobody
        // can enumerate in advance what somebody wants to remember about an acquaintance, and
        // with no ALTER TABLE machinery every future fixed column would cost another table anyway.
        """
        CREATE TABLE IF NOT EXISTS contact_profile (
            contact_id INTEGER PRIMARY KEY REFERENCES contact(id) ON DELETE CASCADE,
            photo_file TEXT,
            birth_date TEXT,
            updated_at TEXT NOT NULL
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS contact_field (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id INTEGER NOT NULL REFERENCES contact(id) ON DELETE CASCADE,
            label      TEXT    NOT NULL,
            value      TEXT    NOT NULL,
            position   INTEGER NOT NULL DEFAULT 0,
            updated_at TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_field_contact ON contact_field(contact_id, position);",

        // What each piece of work cost.
        //
        // The question this answers is one the application could not answer at all: how long
        // transcription actually takes on this machine, and how much is being spent on analysis.
        // Both matter here more than they would elsewhere. Without a usable GPU transcription runs
        // several times slower than real time — a forty-seven minute call once took three and a
        // half hours — and a machine that is working looks exactly like one that has hung. And a
        // hosted model is somebody's money, spent silently, with the bill arriving monthly.
        //
        // No conversation content, no contact, no title: a row is a stage, an engine, a duration
        // and a token count. Cascaded on delete anyway, so "her şey silinecek" stays literally
        // true — the totals shrink, which is the honest outcome.
        """
        CREATE TABLE IF NOT EXISTS processing_run (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id           INTEGER REFERENCES call(id) ON DELETE CASCADE,
            stage             TEXT    NOT NULL,
            engine            TEXT    NOT NULL,
            started_at        TEXT    NOT NULL,
            elapsed_ms        INTEGER NOT NULL DEFAULT 0,
            audio_ms          INTEGER NOT NULL DEFAULT 0,
            prompt_tokens     INTEGER,
            completion_tokens INTEGER,
            succeeded         INTEGER NOT NULL DEFAULT 1
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_run_started ON processing_run(started_at DESC);",
        "CREATE INDEX IF NOT EXISTS ix_run_stage ON processing_run(stage, started_at DESC);",

        // Conversations the user has put aside to come back to.
        //
        // A card is always a conversation, never a free-standing note. The rule the whole product
        // rests on is that every claim carries a verbatim quote and a timestamp you can play — a
        // bare "call Ahmet" card has neither, and the moment the board accepts one this stops
        // being an archive of evidence and becomes a to-do list that happens to sit beside one.
        //
        // The lane is a plain string rather than a foreign key to a table of user-named columns.
        // Fixed lanes are what let the first screen say "Bende: 3 · Onlarda: 1" at all, and let
        // every empty column carry a sentence explaining what belongs in it. A board that opens
        // with no columns and a "create a column" button is a second emptiness on top of the one
        // it was meant to fix.
        //
        // ON DELETE CASCADE: a card whose conversation was deleted is a card about nothing.
        """
        CREATE TABLE IF NOT EXISTS board_card (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            lane       TEXT    NOT NULL,
            position   INTEGER NOT NULL DEFAULT 0,
            title      TEXT,
            remind_on  TEXT,
            created_at TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_board_lane ON board_card(lane, position);",
        "CREATE INDEX IF NOT EXISTS ix_board_remind ON board_card(remind_on);",

        // What the user wrote down to do. ON DELETE SET NULL on both pointers: a note outlives
        // the conversation or the person it was about — it is the user's, not theirs.
        """
        CREATE TABLE IF NOT EXISTS todo (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            text       TEXT    NOT NULL,
            due_date   TEXT,
            done_at    TEXT,
            contact_id INTEGER REFERENCES contact(id) ON DELETE SET NULL,
            call_id    INTEGER REFERENCES call(id) ON DELETE SET NULL,
            created_at TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_todo_open ON todo(done_at, due_date);",
    ];
}
