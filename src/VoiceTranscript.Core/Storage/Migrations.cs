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
    ];
}
