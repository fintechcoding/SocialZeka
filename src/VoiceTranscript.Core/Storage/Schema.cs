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
    public const int Version = 19;

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
            trimmed_at           TEXT,
            contact_source       TEXT,

            -- Which stored transcript the call's lines currently are.
            --
            -- Without it there is no answer to "where did this text come from", and two visible
            -- faults followed. The quality strip named the engine of the LAST RUN, so a restored
            -- OpenAI transcript was labelled as the local model's work — provenance mixed inside
            -- one sentence, which is worse than either half alone. And restoring had to file a
            -- duplicate copy of the transcript to become "the newest", so pressing "use this one"
            -- four times left four identical rows in the history and pushed real transcriptions
            -- out of it.
            --
            -- Null on calls transcribed before this column existed; the strip falls back to the
            -- run for those.
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL
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
            suspected_echo         INTEGER NOT NULL DEFAULT 0,
            words                  TEXT
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
            dismissed_by_user   INTEGER NOT NULL DEFAULT 0,

            -- When the row was written. NULL on rows from before v15: "bilinmiyor", not a guess.
            created_at          TEXT,

            -- The user's rulings, stamped. fulfilled_at is when they marked it kept; decided_at
            -- the last ruling of any kind (kept, dismissed, reopened, brought back) — so a list
            -- can say "işaretledin: 4 tutuldu" with dates rather than a bare status.
            fulfilled_at        TEXT,
            decided_at          TEXT,

            -- USER COLUMNS. deadline_date and obligation stay what the words said — the machine's
            -- reading of the quote, replaced on every re-run. These are what the user changed
            -- them to (postponed, reworded), and a row that carries either survives every re-run:
            -- a ruling thrown away is work the user has to do again. The quote is never edited.
            user_deadline_date  TEXT,
            user_obligation     TEXT,
            edited_at           TEXT
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

            created_at            TEXT    NOT NULL,

            -- When the user last ruled on it: dismissed, or brought back. NULL: never.
            decided_at            TEXT
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
            created_at TEXT    NOT NULL,

            -- Which stored transcript this was written from. NULL on rows older than v15, which
            -- the screen says as "bilinmiyor" and never as "bayat".
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL
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
            created_at     TEXT    NOT NULL,

            -- Which stored transcript the suggestion was drawn from (NULL before v15), and when
            -- the user last ruled on it (done, hidden, routed, reopened).
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,
            decided_at     TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_action_call ON action_item(call_id);",
        "CREATE INDEX IF NOT EXISTS ix_action_open ON action_item(status, deadline_date);",

        // The model's free-form reading of one conversation, stored as the JSON it produced.
        // Deliberately a dead end in the data model: nothing joins on it, nothing feeds it to
        // other prompts, nothing surfaces it outside the one panel beside the evidence — a
        // subjective reading lives next to the transcript it read, and nowhere else.
        //
        // The one pointer it carries goes the other way: which transcript it read. A call that
        // is transcribed again keeps its reading, and the screen can then say the reading is of
        // an earlier text rather than pass it off as a reading of the one on screen.
        """
        CREATE TABLE IF NOT EXISTS reading_note (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            json       TEXT    NOT NULL,
            model_used TEXT,
            created_at TEXT    NOT NULL,

            -- Which stored transcript this was written from. NULL on rows older than v15, which
            -- the screen says as "bilinmiyor" and never as "bayat".
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL
        );
        """,

        // The opt-in deception/manipulation assessment. Same dead-end rules as the reading:
        // stored as the enforced shape, joined by nothing, fed to no other prompt. It exists
        // because the user explicitly asked to hear the model's opinion — and it is stored as
        // an opinion, never as evidence.
        //
        // The dead end still holds for the two fields that ARE the opinion: the suspicion LEVEL
        // and the ASSESSMENT paragraph never leave this row — not to another table, not to a
        // contact, not into any prompt. What was loosened, deliberately and only for the quote:
        // a tactic line whose words were VERIFIED against the transcript is copied to
        // tactic_evidence, so the same sentence can be counted on the person's card. What is
        // copied there is a machine-verified quote with a label on it, which is the same class
        // of thing the consistency check already writes; the judgement stays here.
        """
        CREATE TABLE IF NOT EXISTS deception_note (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            json       TEXT    NOT NULL,
            model_used TEXT,
            created_at TEXT    NOT NULL,

            -- Which stored transcript this was written from. NULL on rows older than v15, which
            -- the screen says as "bilinmiyor" and never as "bayat".
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL
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

            -- Which stored transcript this was written from. NULL on rows older than v15, which
            -- the screen says as "bilinmiyor" and never as "bayat".
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,
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
        // Its own table rather than a column on `call`: it predates the migration steps (v3 and
        // later), when a column could not be added to a database that already existed, and moving
        // it now would be churn for nothing.
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
        // can enumerate in advance what somebody wants to remember about an acquaintance, and a
        // fixed column is a migration step each time, which a label+value row is not.
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

        // What a person sounds like, so the far end of a call can be recognised without asking.
        //
        // This is the one table holding something derived from a person's body rather than from
        // what they said, and it earns its place by replacing something worse. Who the other party
        // is has until now come from the call window's title, and the archive records what that
        // costs: one generic "Voice call" title spread across eight different contacts, and a
        // migration whose whole job is to mark such titles unreliable after the damage is done.
        //
        // It is not a judgement and it is not a score about anybody — the rule at the top of this
        // file still holds. It is 256 numbers that say whether two recordings are the same voice,
        // and nothing can be recovered from them: the audio is not reconstructible, and a vector
        // from a different model is not even comparable, which is why the model travels with it.
        //
        // In the database rather than beside it, deliberately. BackupService copies the whole
        // SQLite file, so a voiceprint here is included in the encrypted backup automatically,
        // while a file under models/ or cache/ would be in neither. Biometric data belongs on the
        // side of that line that gets encrypted.
        //
        // Off by default and only written when the user turns the feature on; deleted with the
        // contact by the cascade, and all at once from the settings screen.
        """
        CREATE TABLE IF NOT EXISTS contact_voice (
            contact_id     INTEGER PRIMARY KEY REFERENCES contact(id) ON DELETE CASCADE,
            vector         TEXT    NOT NULL,
            model          TEXT    NOT NULL,
            calls_used     INTEGER NOT NULL DEFAULT 0,
            speech_seconds REAL    NOT NULL DEFAULT 0,
            updated_at     TEXT    NOT NULL
        );
        """,

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
            succeeded         INTEGER NOT NULL DEFAULT 1,
            speech_coverage   REAL
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

        // Every transcript this call has ever had, with the engine that produced it.
        //
        // The current transcript lives in "segment" and always has; this is what came before it
        // and what came instead of it. It exists because the question "which engine is better on
        // my calls" was unanswerable: each run overwrote the last, so comparing two of them meant
        // reading a log, re-running one by hand, and hoping the audio had not changed underneath.
        //
        // The lines are kept as JSON rather than as rows in "segment". A version is read whole or
        // not at all — to be compared, or to be put back — and nothing queries across versions;
        // giving "segment" a version column instead would put a filter into every query in the
        // application to serve a screen that asks for one call at a time.
        """
        CREATE TABLE IF NOT EXISTS transcript_version (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id         INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            engine          TEXT    NOT NULL,
            created_at      TEXT    NOT NULL,
            speech_coverage REAL,
            segment_count   INTEGER NOT NULL,
            word_count      INTEGER NOT NULL,
            low_confidence  INTEGER NOT NULL,
            spoken_ms       INTEGER NOT NULL,
            segments        TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_transcript_call ON transcript_version(call_id, created_at DESC);",

        // What the user heard when they listened.
        //
        // USER-ENTERED ONLY: nothing in the pipeline writes here, and ClearAnalysis never
        // touches it. One table for every kind of listening verdict — a flag confirmed or
        // refuted, a counted word that was or was not that word, a level peak that was or was
        // not a change — because every honest figure the coaching screens will show ("14 sayımın
        // 11'i dinlendi, 10'u doğru") is a ratio over this table, and four tables would be four
        // ways to get it wrong.
        //
        // Keyed by the folded quote and the millisecond rather than by a row id alone. target_id
        // is a convenience pointing at flag.id (later tactic_evidence.id) and means nothing after
        // an archive merge, where ids differ; the words and the time survive a re-run and a
        // merge, and are what a recount matches against. No foreign key on target_id, on purpose:
        // it would make a merged archive refuse the user's own verdicts.
        """
        CREATE TABLE IF NOT EXISTS verdict (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id      INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,

            -- 'flag' | 'kufur' | 'dolgu' | 'bilgi' | 'ton' | 'canli' | 'kalip'
            kind         TEXT    NOT NULL,
            target_id    INTEGER,
            quote_folded TEXT    NOT NULL,
            start_ms     INTEGER NOT NULL,

            -- 1 doğru · 0 yanlış duyulmuş · 2 bu o değil · 3 uyarı isterdim · 4 gereksiz
            verdict      INTEGER NOT NULL,
            decided_at   TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_verdict_call ON verdict(call_id, kind);",

        // What the USER did while talking, counted: swear words, fillers, speech rate, talk
        // share, and the moments they gave a piece of information away. One row per call.
        //
        // MACHINE CACHE, like reading_note: rebuilt from the transcript and the lexicon whenever
        // either changes, and the two version columns say which of each it was built from. The
        // JSON holds the report and the talk statistics together so the trend page reads a
        // whole year in one SELECT instead of joining per call.
        //
        // Nothing in here is about the other party — the counters run over the user's own lines
        // only — and nothing in here is a value: a moment where an IBAN was read out is stored
        // as the kind "iban" and the millisecond, never the number. Storing the number would make
        // this table a second place where the archive keeps bank details, in a backup that may
        // not be encrypted, to answer a question that needs only the fact that it happened.
        """
        CREATE TABLE IF NOT EXISTS speech_habit (
            call_id               INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,
            lexicon_version       INTEGER NOT NULL,
            json                  TEXT    NOT NULL,
            created_at            TEXT    NOT NULL
        );
        """,

        // The words the counters look for.
        //
        // USER DATA, on the tag_def pattern: seeded once from the embedded list, then edited by
        // the user — a stem added, a stem removed, a word ruled "bu küfür değil" landing here as
        // an exclusion — and never touched by a recount. Re-seeding on every start would bring
        // back what they deleted, which is the one thing a dictionary the user owns must not do.
        //
        // A row is a stem and the endings it may carry, not a word. Substring matching produced
        // "klasik" and "aman" as hits; whole-token matching missed every inflected form. Turkish
        // is agglutinative, so the rule that works is token boundary + stem + a listed suffix,
        // and the suffix list has to be data because nobody can enumerate it in code.
        """
        CREATE TABLE IF NOT EXISTS habit_lexicon (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,

            -- 'kufur' | 'dolgu' | 'sive' | 'haric'. 'sive' is reserved and unseeded: the dialect
            -- counter is not built until its pre-measurement passes. 'haric' rows remove hits
            -- instead of making them.
            kind           TEXT    NOT NULL,

            -- The stem, folded with TurkishText.NormalizeForSearch — the same folding the
            -- transcript's text_normalised carries, so the two meet in one bucket.
            lexeme_folded  TEXT    NOT NULL,

            -- The endings the stem may carry, as a JSON list of folded strings. NULL or empty
            -- means the bare stem only.
            suffixes       TEXT,
            lexeme         TEXT    NOT NULL,
            position       INTEGER NOT NULL DEFAULT 0,
            UNIQUE(kind, lexeme_folded)
        );
        """,

        // What the user meant to do or not do in a conversation, in their own words, before or
        // after it: "kira rakamını söylemeyeceğim". The Niyet card.
        //
        // USER DATA, like call_note: written only by the user, replaced only by the user, and
        // untouched by every re-run. It exists because intent cannot be measured — "rol yapamama"
        // is on the list of things this product refuses to score — and the honest substitute is
        // to let the user write the intent down and count, afterwards, the moments they marked
        // against it themselves.
        """
        CREATE TABLE IF NOT EXISTS call_intent (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            text       TEXT    NOT NULL,
            updated_at TEXT    NOT NULL
        );
        """,

        // One tactic line whose quote was verified against the transcript, filed under the
        // person it was said to — what the contact card counts under "Kalıplar".
        //
        // MACHINE EVIDENCE, AND ONLY THE QUOTE. The assessment's suspicion LEVEL and its
        // ASSESSMENT PARAGRAPH ARE NEVER COPIED HERE; they stay in deception_note, which is a
        // dead end. And NOTHING IN THIS TABLE IS EVER FED TO A PROMPT — not the contact
        // reading, not the consistency check, not the archive questions. A row is a label, a
        // verbatim sentence and the millisecond it was said, so the user can play it; the
        // machinery that produced the label never gets to read its own output back.
        //
        // The tactic is a whitelist. An unrecognised label is DROPPED rather than filed as
        // "diger" — a bucket named "other" accumulates whatever a model felt like typing, and
        // it would then be counted on somebody's card as a pattern. The same rule the action
        // extraction already applies to its kinds.
        //
        // source says which machinery wrote the row, and it is ownership rather than
        // decoration, exactly as on flag: the ledger rebuild clears the pipeline's rows and
        // leaves the paid assessment's alone. dismissed_by_user rows are tombstones — deleting
        // them would let the next run put the same sentence back.
        """
        CREATE TABLE IF NOT EXISTS tactic_evidence (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id               INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            contact_id            INTEGER REFERENCES contact(id) ON DELETE CASCADE,

            -- Which stored transcript the quote was verified against; NULL when unrecorded.
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,

            -- 'deception' (the opt-in assessment) | 'pipeline' (the extraction's pressure signs).
            source                TEXT    NOT NULL,
            tactic                TEXT    NOT NULL,

            -- Which recorded stream the quote was found in, never the model's opinion of it.
            by_me                 INTEGER NOT NULL DEFAULT 0,
            quote                 TEXT    NOT NULL,
            quote_start_ms        INTEGER NOT NULL DEFAULT 0,
            low_confidence        INTEGER NOT NULL DEFAULT 0,
            model_used            TEXT,
            dismissed_by_user     INTEGER NOT NULL DEFAULT 0,
            created_at            TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_tactic_contact ON tactic_evidence(contact_id, dismissed_by_user);",

        // The questions of a conversation, kept.
        //
        // The extraction has always asked for them and the pipeline has always thrown them
        // away: they lived in a local list long enough to compute one evasion ratio for one
        // call, and then the run ended. So "how often does this person answer you" could only
        // ever be asked about the call on screen, and the contact card's honest denominator —
        // "measured in N of M conversations" — did not exist, because nothing recorded which
        // calls had been looked at at all.
        //
        // MACHINE EVIDENCE, same rules as above: a verified quote, a millisecond, and the
        // stream it was found in. answer_status is one of four words or NULL; it is not a score
        // and no row here is fed to a prompt. Only 'soru' is written today, and the column
        // exists so a second kind is a constant rather than a migration.
        """
        CREATE TABLE IF NOT EXISTS speech_act (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id        INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            contact_id     INTEGER REFERENCES contact(id) ON DELETE CASCADE,
            by_me          INTEGER NOT NULL DEFAULT 0,
            kind           TEXT    NOT NULL,

            -- cevaplandi | kismi | kacamak | savusturuldu, or NULL when the model said nothing
            -- this code recognises. An unknown word is not invented into one of the four.
            answer_status  TEXT,
            quote          TEXT    NOT NULL,
            quote_start_ms INTEGER NOT NULL DEFAULT 0,
            low_confidence INTEGER NOT NULL DEFAULT 0,
            created_at     TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_speech_act_contact ON speech_act(contact_id, kind);",

        // Level and pitch over time, as the worker measured them.
        //
        // Keyed by the AUDIO rather than by the transcript, and that is the whole design. Nothing
        // here comes from the words: transcribing the same recording again with a better engine
        // changes not one of these numbers, and re-running a minute of CPU over the audio to
        // rediscover that would be work for nothing. What does invalidate a row is the recording
        // itself changing — silence trimmed, a file re-encoded — which is what audio_key catches.
        //
        // The measurement is stored; the reading is not. Whether a stretch "stands out" depends on
        // a threshold that is a guess until sixty peaks have been listened to (PLAN-SOSYALZEKA
        // §6.3), and when that number moves nothing should have to touch the audio again.
        //
        // No interpretation reaches this table and none may be derived from it elsewhere: a peak
        // is a place to listen. Voice-stress lie detection performs at chance, and emotion from
        // audio is not validated for Turkish — neither is offered anywhere in this product.
        """
        CREATE TABLE IF NOT EXISTS prosody (
            call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
            audio_key  TEXT    NOT NULL,
            json       TEXT    NOT NULL,
            created_at TEXT    NOT NULL
        );
        """,

        // What the transcription service heard that was not a word: laughter, a cough, a long
        // silence. ElevenLabs labels them when asked; every other engine says nothing, and a call
        // transcribed by one of those simply has no rows here — which the screen says rather than
        // drawing an empty timeline as if nothing had happened.
        //
        // Filed against the transcript that produced them, so a re-transcription replaces them
        // wholesale. ClearAnalysis does not touch them: they came out of the audio with the words,
        // not out of the ledger's reasoning.
        """
        CREATE TABLE IF NOT EXISTS audio_event (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id               INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
            transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,
            channel               TEXT    NOT NULL,
            start_ms              INTEGER NOT NULL,
            end_ms                INTEGER NOT NULL,
            kind                  TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_audio_event_call ON audio_event(call_id, start_ms);",

        // What a model thinks of somebody, when the user asked it to.
        //
        // A DEAD END, and the strictest one in this file. Nothing joins on this table, no screen
        // but the card's own bottom panel reads it, and NO PROMPT EVER RECEIVES A ROW OF IT — not
        // the next contact reading, not the consistency check, not the archive questions. A model
        // that could read its own earlier opinion back would be building a case about a person out
        // of its own prose rather than out of what was said, and every run would make the last one
        // truer. The reading is quote-anchored on the way in and nothing on the way out.
        //
        // NOT contact_profile, which is USER-ENTERED ONLY and stays that way. This is the machine's
        // side of the same person, kept apart so the two can never be mistaken for each other.
        //
        // History, not a row per person. A reading is dated, signed by the model that wrote it, and
        // the previous one stays: the whole measurement of whether this feature is worth having is
        // "does the user disagree with it", and a table that overwrote itself would answer that
        // question with one data point. user_verdict is the USER's column — nothing in the analysis
        // writes it and no re-run clears it.
        //
        // input_hash is what makes "N yeni görüşme var, bu okuma eski" answerable: it is computed
        // from the conversations the packet was built out of, so new calls change it and the panel
        // can say the reading no longer covers the history. rejected_count and excerpt_count travel
        // with the row for the same reason the ledger's denominators do — a reading whose anchors
        // mostly did not resolve is not a better reading, and the signature line says so.
        """
        CREATE TABLE IF NOT EXISTS contact_reading (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id     INTEGER NOT NULL REFERENCES contact(id) ON DELETE CASCADE,
            json           TEXT    NOT NULL,
            model_used     TEXT,
            calls_covered  INTEGER NOT NULL,

            -- The newest conversation the packet drew on; SET NULL so deleting it keeps the row.
            latest_call_id INTEGER REFERENCES call(id) ON DELETE SET NULL,

            input_hash     TEXT    NOT NULL,
            excerpt_count  INTEGER NOT NULL,
            rejected_count INTEGER NOT NULL,

            -- USER: 1 when they pressed [Katılmıyorum]. NULL means they have not said.
            user_verdict   INTEGER,
            created_at     TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS ix_contact_reading ON contact_reading(contact_id, created_at DESC);",
    ];
}
