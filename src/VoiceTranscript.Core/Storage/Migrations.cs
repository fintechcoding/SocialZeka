namespace VoiceTranscript.Core.Storage;

/// <summary>
/// Ordered changes for databases created before the current schema.
///
/// The model is baseline-plus-delta. <see cref="Schema.Statements"/> is the baseline: it always
/// describes the CURRENT shape and creates it whole on a fresh database. The steps here replay
/// history for databases that already exist — each one carries the version it upgrades TO, and
/// <see cref="Database.Migrate"/> applies exactly those above the stored version, in order,
/// each in its own transaction, after snapshotting the file.
///
/// Until this existed the project had a rule instead of a mechanism: "new columns silently do
/// nothing, so new data means new tables". The rule kept things safe and produced eight tables;
/// it could not last — the silence-trim map, encryption metadata, anything that belongs ON an
/// existing row, all need ALTER TABLE. This is what makes that possible without abandoning the
/// property that made the rule good: an old database is never quietly half-upgraded.
///
/// Rules for writing a step:
///   - The step's SQL must bring a version-N database to version N+1 — nothing more.
///   - The SAME change must also appear in the baseline, so fresh databases are born current.
///   - Never edit or remove a shipped step: databases in the field have already recorded its
///     version number, and history that changes under them is corruption with extra steps.
/// </summary>
public static class Migrations
{
    /// <param name="Version">The version a database is AT once this step has run.</param>
    /// <param name="Description">One line for the log, in Turkish: the user may see it.</param>
    public sealed record Step(int Version, string Description, string[] Sql);

    /// <summary>Shipped steps, ascending. A step's version equals the Schema.Version it produces.</summary>
    public static readonly IReadOnlyList<Step> Steps =
    [
        // v3 — silence trimming records when it ran, so a recording is never trimmed twice and
        // the screen can say why a file is smaller than its duration suggests.
        new(3, "Görüşme tablosuna sessizlik kırpma damgası",
            ["ALTER TABLE call ADD COLUMN trimmed_at TEXT;"]),

        // v4 — tags gain a face: icon and colour per tag, Outlook-category style, plus the
        // form that edits the default vocabulary. Definitions only; call_tag rows are untouched.
        new(4, "Etiket görünümleri tablosu (ikon ve renk)",
            [
                """
                CREATE TABLE IF NOT EXISTS tag_def (
                    tag_folded TEXT    PRIMARY KEY,
                    tag        TEXT    NOT NULL,
                    icon       TEXT    NOT NULL,
                    color      TEXT    NOT NULL,
                    position   INTEGER NOT NULL DEFAULT 0
                );
                """,
            ]),

        // v5 — the consistency check arrives. Flags gain an owner column so the ledger rebuild
        // and the consistency re-run can each clear only their own rows, and a confidence column
        // so the model's stated certainty is data rather than prose smuggled into the summary.
        // Every flag written before this version came from the pipeline, hence the default.
        new(5, "İşaretlere kaynak ve güven sütunları; tutarlılık notu tablosu",
            [
                "ALTER TABLE flag ADD COLUMN source TEXT NOT NULL DEFAULT 'pipeline';",
                "ALTER TABLE flag ADD COLUMN confidence TEXT;",
                """
                CREATE TABLE IF NOT EXISTS consistency_note (
                    call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
                    note       TEXT    NOT NULL,
                    model_used TEXT,
                    created_at TEXT    NOT NULL
                );
                """,
            ]),

        // v6 — the action layer and the reading panel. Suggestions for the user's next moves
        // (quote-anchored, user-routed) and the model's stored free reading of a call.
        new(6, "Aksiyon önerileri ve model okuması tabloları",
            [
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
                    status         INTEGER NOT NULL DEFAULT 0,
                    routed_note    TEXT,
                    model_used     TEXT,
                    created_at     TEXT    NOT NULL
                );
                """,
                "CREATE INDEX IF NOT EXISTS ix_action_call ON action_item(call_id);",
                "CREATE INDEX IF NOT EXISTS ix_action_open ON action_item(status, deadline_date);",
                """
                CREATE TABLE IF NOT EXISTS reading_note (
                    call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
                    json       TEXT    NOT NULL,
                    model_used TEXT,
                    created_at TEXT    NOT NULL
                );
                """,
            ]),

        // v7 — the opt-in deception/manipulation assessment. The user asked for the model's
        // explicit opinion as a switchable feature; the table mirrors reading_note because the
        // rules are the same: enforced shape in, dead end after.
        new(7, "Yalan/manipülasyon değerlendirmesi tablosu",
            [
                """
                CREATE TABLE IF NOT EXISTS deception_note (
                    call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
                    json       TEXT    NOT NULL,
                    model_used TEXT,
                    created_at TEXT    NOT NULL
                );
                """,
            ]),

        // v8 — a learned title can be known to be worthless, and the ones already poisoned are
        // cleaned out.
        //
        // The fault this repairs, in the words of the person who hit it: "her konuşmayı
        // WhatsApp'da Uliana zannediyor". A window title was bound to a contact the first time a
        // call was labelled, and every later call carrying the same title was filed under that
        // person without a prompt. On WhatsApp the observed title is not the caller at all — it
        // is whatever chat was open, or a WebView2 page title — so one labelling swallowed every
        // conversation that followed.
        //
        // The repair is evidence-based rather than a guess: a pattern is marked unreliable when
        // the calls carrying it already belong to more than one contact. That is proof from the
        // user's own archive that the title does not identify anybody, and it leaves alone the
        // Telegram bindings, which are genuinely per-person and are the reason this feature
        // exists.
        new(8, "Kişiyi tanımlamayan pencere başlıklarını işaretle ve temizle",
            [
                "ALTER TABLE title_binding ADD COLUMN unreliable INTEGER NOT NULL DEFAULT 0;",

                """
                UPDATE title_binding
                   SET unreliable = 1
                 WHERE title_pattern IN (
                       SELECT observed_title
                         FROM call
                        WHERE observed_title IS NOT NULL
                          AND contact_id IS NOT NULL
                        GROUP BY observed_title
                       HAVING COUNT(DISTINCT contact_id) > 1);
                """,
            ]),

        // The to-do page: the user's own list, beside the suggestions and reminders the
        // application already keeps.
        new(9, "Yapılacaklar tablosu",
            [
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
            ]),

        // How much of the speech in a recording came back with words on it.
        //
        // The failure this exists to make visible had been running for days without anybody being
        // able to name it. A transcript that invents is obvious the moment you read it; one that
        // goes quiet is not, because a conversation with pauses in it looks exactly the same. On
        // one measured stretch the service returned words for 108 of 157 seconds of speech where
        // the local engine returned 150 — and the 49 seconds it dropped ran at the same level as
        // the rest, so nothing in the text said they were missing.
        //
        // Kept per run rather than per call: it is a property of the engine and the flags that
        // produced this text, and comparing two runs of the same recording is exactly the question
        // somebody asks when a transcript looks thin.
        new(10, "Konuşmanın ne kadarının yazıya döküldüğü",
            ["ALTER TABLE processing_run ADD COLUMN speech_coverage REAL;"]),

        // Recognising the other party by voice, and recording how a call came to be filed.
        //
        // The two belong in one step because the second only exists for the first. An assignment
        // made from a voiceprint is a machine decision about who somebody is, and this application
        // has already paid for one of those going unrecorded: a window title bound to a contact
        // filed every later call under that person, and nothing said which rows had been decided
        // that way, so the repair had to be inferred from the damage. contact_source makes the
        // same class of mistake visible and reversible in one query.
        new(11, "Sesten kişi tanıma: ses izleri ve atamanın kaynağı",
            [
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

                "ALTER TABLE call ADD COLUMN contact_source TEXT;",

                // Everything already filed was filed by a person, one call at a time, through the
                // labelling window. Saying so is what makes "show me what the voice decided" a
                // question with an answer from the first day rather than the second.
                "UPDATE call SET contact_source = 'user' WHERE contact_id IS NOT NULL;",
            ]),

        // Where each word was said, so a transcript can be read while it plays.
        //
        // The engines have always returned these and the worker has always passed them across;
        // storage threw them away, and everything downstream then had only the line to work with.
        // A line is enough to order a conversation and not enough to follow one: the reader
        // listening to a nine-second turn has no way to see which part of it is sounding now.
        //
        // Kept as JSON on the segment rather than as rows of their own. A word belongs to exactly
        // one line, is never queried on its own, and is always wanted with the line — a table
        // would add four thousand rows per call to answer a question nobody asks. The column is
        // the same shape the archive already uses for capture_stats and the reading notes.
        //
        // Null on every line transcribed before this, and the reader treats null as "no
        // word-level detail" rather than as an empty transcript. Re-transcribing fills it in.
        new(12, "Kelime zaman damgaları",
            ["ALTER TABLE segment ADD COLUMN words TEXT;"]),

        // v13 — every transcript a call has had, with the engine that produced it, so two
        // engines can be compared on the same conversation instead of on a memory of one.
        new(13, "Yazıya dökme geçmişi",
            [
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
            ]),

        // v14 — the call remembers which stored transcript its lines came from.
        //
        // "Newest wins" was doing this job implicitly and doing it badly in two directions. The
        // quality strip asked the LAST RUN which engine produced the text, so restoring an older
        // transcript left it naming a different engine than the one on screen — provenance mixed
        // inside a single sentence, in a product whose whole argument is that a quote can be
        // traced. And restoring had to write a duplicate copy to become the newest, so pressing
        // "use this one" four times left four identical rows and evicted real transcriptions
        // from a history capped at ten.
        //
        // Null for calls transcribed before this, which is honest: nothing recorded it then.
        new(14, "Görüşme, hangi dökümü gösterdiğini hatırlasın",
            ["ALTER TABLE call ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;"]),

        // v15 — three things the ledger could not say.
        //
        // Which transcript a derived note was written from: transcribing a call again replaced
        // its lines and left the reading, the assessment, the summary and the suggestions
        // standing on text that no longer existed, quoting words the screen no longer showed.
        // When the user ruled on a promise or a flag, and what they changed it to: "tutuldu" was
        // a status with no date, a postponed deadline had nowhere to go but over what was said,
        // and a re-run erased both. And what the user heard when they listened: every precision
        // figure the coaching screens will show is a ratio over the verdict table.
        //
        // All nullable, so nothing needs a default and old rows honestly read as "bilinmiyor".
        new(15, "Türev notlar hangi dökümden; söz kararları damgalı; kulak teyidi",
            [
                "ALTER TABLE reading_note ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;",
                "ALTER TABLE deception_note ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;",
                "ALTER TABLE consistency_note ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;",
                "ALTER TABLE action_item ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;",
                "ALTER TABLE call_summary ADD COLUMN transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL;",
                "ALTER TABLE commitment ADD COLUMN created_at TEXT;",
                "ALTER TABLE commitment ADD COLUMN fulfilled_at TEXT;",
                "ALTER TABLE commitment ADD COLUMN decided_at TEXT;",
                "ALTER TABLE commitment ADD COLUMN user_deadline_date TEXT;",
                "ALTER TABLE commitment ADD COLUMN user_obligation TEXT;",
                "ALTER TABLE commitment ADD COLUMN edited_at TEXT;",
                "ALTER TABLE flag ADD COLUMN decided_at TEXT;",
                "ALTER TABLE action_item ADD COLUMN decided_at TEXT;",
                """
                CREATE TABLE IF NOT EXISTS verdict (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    call_id      INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
                    kind         TEXT    NOT NULL,
                    target_id    INTEGER,
                    quote_folded TEXT    NOT NULL,
                    start_ms     INTEGER NOT NULL,
                    verdict      INTEGER NOT NULL,
                    decided_at   TEXT    NOT NULL
                );
                """,
                "CREATE INDEX IF NOT EXISTS ix_verdict_call ON verdict(call_id, kind);",
            ]),

        // v16 — Aynam: what the user did while talking, counted.
        //
        // Three tables and two owners. speech_habit is the machine's: one row per call, rebuilt
        // when the transcript or the lexicon changes, carrying which of each it was built from.
        // habit_lexicon and call_intent are the user's — the dictionary the counters read
        // (seeded once, then theirs) and the intent they wrote down for a conversation — and no
        // re-run may touch either. No column on an existing table changes shape.
        new(16, "Aynam: konuşma alışkanlıkları önbelleği, sözlük ve niyet kartı",
            [
                """
                CREATE TABLE IF NOT EXISTS speech_habit (
                    call_id               INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
                    transcript_version_id INTEGER REFERENCES transcript_version(id) ON DELETE SET NULL,
                    lexicon_version       INTEGER NOT NULL,
                    json                  TEXT    NOT NULL,
                    created_at            TEXT    NOT NULL
                );
                """,
                """
                CREATE TABLE IF NOT EXISTS habit_lexicon (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind           TEXT    NOT NULL,
                    lexeme_folded  TEXT    NOT NULL,
                    suffixes       TEXT,
                    lexeme         TEXT    NOT NULL,
                    position       INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(kind, lexeme_folded)
                );
                """,
                """
                CREATE TABLE IF NOT EXISTS call_intent (
                    call_id    INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
                    text       TEXT    NOT NULL,
                    updated_at TEXT    NOT NULL
                );
                """,
            ]),
    ];
}
