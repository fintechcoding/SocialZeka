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
    ];
}
