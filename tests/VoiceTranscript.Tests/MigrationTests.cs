using Microsoft.Data.Sqlite;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The schema migration machinery — the thing the project ran without for as long as every
/// change was a new table, and cannot run without any longer.
///
/// The properties under test are the ones that make upgrades boring: a fresh database is born
/// current and never replays history; an old one walks the steps in order and records where it
/// got to; the file is snapshotted before its shape changes; and a step that throws leaves both
/// the version and the data exactly as they were.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-mig-{Guid.NewGuid():N}");
    private readonly string _path;

    public MigrationTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "calls.db");
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Migrations.Step ColumnStep(int version) => new(
        version,
        "test: call tablosuna deneme sütunu",
        [$"ALTER TABLE call ADD COLUMN test_v{version} TEXT;"]);

    private int Stored()
    {
        using var connection = new Database(_path).Open();
        return Database.StoredVersion(connection);
    }

    private bool ColumnExists(string column)
    {
        using var connection = new Database(_path).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('call') WHERE name = $n;";
        command.Parameters.AddWithValue("$n", column);
        return (long)command.ExecuteScalar()! > 0;
    }

    [Fact]
    public void AFreshDatabaseIsBornAtTheLatestVersionWithoutReplayingHistory()
    {
        var database = new Database(_path);

        // A pending step exists, but a fresh database must not run it: the baseline already
        // describes the final shape, and altering a newborn is how double columns happen.
        database.Migrate([ColumnStep(Schema.Version + 1)]);

        Assert.Equal(Schema.Version + 1, Stored());
        Assert.False(ColumnExists($"test_v{Schema.Version + 1}"));

        // And no backup was taken — nothing was at risk.
        Assert.Empty(Directory.GetFiles(_root, "*.premigration-*"));
    }

    [Fact]
    public void AnExistingDatabaseWalksThePendingStepsInOrder()
    {
        new Database(_path).Migrate(); // born at Schema.Version, no steps

        var database = new Database(_path);
        database.Migrate([ColumnStep(Schema.Version + 1), ColumnStep(Schema.Version + 2)]);

        Assert.True(ColumnExists($"test_v{Schema.Version + 1}"));
        Assert.True(ColumnExists($"test_v{Schema.Version + 2}"));
        Assert.Equal(Schema.Version + 2, Stored());
    }

    [Fact]
    public void MigratingTwiceAppliesEachStepOnce()
    {
        new Database(_path).Migrate();

        var step = ColumnStep(Schema.Version + 1);

        new Database(_path).Migrate([step]);
        new Database(_path).Migrate([step]); // ALTER would throw "duplicate column" if replayed

        Assert.Equal(Schema.Version + 1, Stored());
    }

    /// <summary>
    /// The snapshot is the difference between "the migration failed" and "the archive is gone".
    /// </summary>
    [Fact]
    public void TheFileIsSnapshottedBeforeItsShapeChanges()
    {
        new Database(_path).Migrate();

        new Database(_path).Migrate([ColumnStep(Schema.Version + 1)]);

        var backup = $"{_path}.premigration-v{Schema.Version}";
        Assert.True(File.Exists(backup));

        // The snapshot is a real database at the OLD shape, not a copy of a half-written file.
        using var connection = new SqliteConnection($"Data Source={backup}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('call') WHERE name LIKE 'test_%';";
        Assert.Equal(0L, command.ExecuteScalar());
        SqliteConnection.ClearPool(connection);
    }

    [Fact]
    public void AFailingStepLeavesVersionAndDataUntouched()
    {
        new Database(_path).Migrate();

        var broken = new Migrations.Step(
            Schema.Version + 1, "test: bozuk adım", ["ALTER TABLE yok_boyle_tablo ADD COLUMN x TEXT;"]);

        Assert.ThrowsAny<SqliteException>(() => new Database(_path).Migrate([broken]));

        Assert.Equal(Schema.Version, Stored());
    }

    /// <summary>
    /// A step after a failing step must not run: versions are a ladder, not a checklist.
    /// </summary>
    [Fact]
    public void StepsAfterAFailureDoNotRun()
    {
        new Database(_path).Migrate();

        var broken = new Migrations.Step(
            Schema.Version + 1, "test: bozuk", ["SELECT * FROM yok;"]);

        Assert.ThrowsAny<SqliteException>(
            () => new Database(_path).Migrate([broken, ColumnStep(Schema.Version + 2)]));

        Assert.False(ColumnExists($"test_v{Schema.Version + 2}"));
        Assert.Equal(Schema.Version, Stored());
    }

    /// <summary>
    /// Shipped steps must be strictly ascending, above the original baseline, and end exactly at
    /// the current Schema.Version — a step without its matching baseline change (or the other way
    /// round) means fresh and upgraded databases have different shapes.
    /// </summary>
    [Fact]
    public void TheShippedStepListIsWellFormed()
    {
        var versions = Migrations.Steps.Select(s => s.Version).ToList();

        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Distinct(), versions);
        Assert.All(versions, v => Assert.InRange(v, 3, Schema.Version));

        if (versions.Count > 0) Assert.Equal(Schema.Version, versions[^1]);
    }

    /// <summary>
    /// The property the whole model rests on: a database upgraded step by step and a database
    /// born fresh must end with the same columns.
    /// </summary>
    [Fact]
    public void AnUpgradedDatabaseMatchesAFreshOne()
    {
        // Born at v2: baseline as it was before the first shipped step.
        using (var connection = new Database(_path).Open())
        {
            // A minimal v2-era call table — the real one minus every column the steps add.
            using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE call (id INTEGER PRIMARY KEY AUTOINCREMENT, contact_id INTEGER,
                    app INTEGER NOT NULL DEFAULT 0, direction INTEGER NOT NULL DEFAULT 0,
                    kind INTEGER NOT NULL DEFAULT 0, started_at TEXT NOT NULL, ended_at TEXT,
                    duration_ms INTEGER NOT NULL DEFAULT 0, mic_path TEXT, far_path TEXT,
                    state INTEGER NOT NULL DEFAULT 0, failure_reason TEXT, observed_title TEXT,
                    capture_stats TEXT, likely_no_headphones INTEGER NOT NULL DEFAULT 0,
                    is_pinned INTEGER NOT NULL DEFAULT 0, audio_sha256 TEXT);
                CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO setting VALUES ('schema_version', '2');

                -- Two tables in their pre-v15 shape. The baseline creates every table it does not
                -- find in its CURRENT shape, and the idempotent ALTER then skips a column that is
                -- already there — so without these, no v15 ALTER would ever execute in this test
                -- and a typo in one of its thirteen lines would stay green here and fail on the
                -- first real v14 database. With them, the ALTERs run, REFERENCES clause and all.
                CREATE TABLE commitment (id INTEGER PRIMARY KEY AUTOINCREMENT,
                    call_id INTEGER NOT NULL REFERENCES call(id) ON DELETE CASCADE,
                    contact_id INTEGER, by_me INTEGER NOT NULL DEFAULT 0, quote TEXT NOT NULL,
                    quote_start_ms INTEGER NOT NULL DEFAULT 0, obligation TEXT NOT NULL,
                    deadline_raw TEXT, deadline_date TEXT, amount TEXT, currency TEXT,
                    is_conditional INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 0,
                    fulfilled_by_call_id INTEGER REFERENCES call(id) ON DELETE SET NULL,
                    dismissed_by_user INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE reading_note (call_id INTEGER PRIMARY KEY REFERENCES call(id) ON DELETE CASCADE,
                    json TEXT NOT NULL, model_used TEXT, created_at TEXT NOT NULL);
                """;
            create.ExecuteNonQuery();
        }

        new Database(_path).Migrate(); // walks the real shipped steps

        Assert.True(ColumnExists("trimmed_at"));
        Assert.Equal(Schema.Version, Stored());

        // v4: the tag wardrobe arrived by step for old databases, by baseline for fresh ones.
        using (var connection = new Database(_path).Open())
        {
            using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM tag_def;";
            Assert.Equal(0L, probe.ExecuteScalar());
        }

        // v5: flags gained an owner; every row written before the column existed came from
        // the pipeline, and the default must say so.
        Assert.True(ColumnExistsIn("flag", "source"));
        Assert.True(ColumnExistsIn("flag", "confidence"));

        using (var connection = new Database(_path).Open())
        {
            // The column's declared default is the honesty guarantee: every flag row written
            // before v5 reads back as the pipeline's.
            using var dflt = connection.CreateCommand();
            dflt.CommandText = "SELECT dflt_value FROM pragma_table_info('flag') WHERE name = 'source';";
            Assert.Equal("'pipeline'", dflt.ExecuteScalar());

            using var note = connection.CreateCommand();
            note.CommandText = "SELECT COUNT(*) FROM consistency_note;";
            Assert.Equal(0L, note.ExecuteScalar());
        }

        // v6: the action list and the model's reading arrived — by step here, by baseline for
        // fresh files. Empty, because the machine has written nothing yet.
        using (var connection = new Database(_path).Open())
        {
            using var actions = connection.CreateCommand();
            actions.CommandText = "SELECT COUNT(*) FROM action_item;";
            Assert.Equal(0L, actions.ExecuteScalar());

            using var reading = connection.CreateCommand();
            reading.CommandText = "SELECT COUNT(*) FROM reading_note;";
            Assert.Equal(0L, reading.ExecuteScalar());
        }

        // v7: the opt-in deception assessment's store.
        using (var connection = new Database(_path).Open())
        {
            using var deception = connection.CreateCommand();
            deception.CommandText = "SELECT COUNT(*) FROM deception_note;";
            Assert.Equal(0L, deception.ExecuteScalar());
        }

        // v11: the voiceprints and the record of how a call came to be filed.
        Assert.True(ColumnExistsIn("call", "contact_source"));

        using (var connection = new Database(_path).Open())
        {
            using var voices = connection.CreateCommand();
            voices.CommandText = "SELECT COUNT(*) FROM contact_voice;";
            Assert.Equal(0L, voices.ExecuteScalar());
        }

        // v12: where each word was said. Nullable on purpose — every line transcribed before
        // this has none, and that must read as "no word detail" rather than as an empty line.
        Assert.True(ColumnExistsIn("segment", "words"));

        using (var connection = new Database(_path).Open())
        {
            using var nullable = connection.CreateCommand();
            nullable.CommandText =
                "SELECT \"notnull\" FROM pragma_table_info('segment') WHERE name = 'words';";
            Assert.Equal(0L, nullable.ExecuteScalar());
        }

        // v13: every transcript a call has had, so two engines can be compared on the same
        // conversation rather than on a memory of one.
        using (var connection = new Database(_path).Open())
        {
            using var versions = connection.CreateCommand();
            versions.CommandText = "SELECT COUNT(*) FROM transcript_version;";
            Assert.Equal(0L, versions.ExecuteScalar());
        }

        Assert.True(ColumnExistsIn("transcript_version", "engine"));
        Assert.True(ColumnExistsIn("transcript_version", "speech_coverage"));

        // v8, v9, v10: never asserted before. Backfilled so the list is complete.
        Assert.True(ColumnExistsIn("title_binding", "unreliable"));
        Assert.True(ColumnExistsIn("processing_run", "speech_coverage"));

        using (var connection = new Database(_path).Open())
        {
            using var todos = connection.CreateCommand();
            todos.CommandText = "SELECT COUNT(*) FROM todo;";
            Assert.Equal(0L, todos.ExecuteScalar());
        }

        // v14: the pointer from a call to the transcript it shows.
        Assert.True(ColumnExistsIn("call", "transcript_version_id"));

        // v15: the derived notes know their transcript; the promise carries its stamps and the
        // user's own columns; the flag and the suggestion carry their ruling stamp; and the
        // verdict table exists. The commitment and reading_note ALTERs really ran here (the
        // tables were seeded in their old shape), so a REFERENCES clause that SQLite refused
        // would have surfaced as an exception above, not as a missing column.
        foreach (var table in new[] { "reading_note", "deception_note", "consistency_note", "action_item", "call_summary" })
            Assert.True(ColumnExistsIn(table, "transcript_version_id"), table);

        foreach (var column in new[] { "created_at", "fulfilled_at", "decided_at", "user_deadline_date", "user_obligation", "edited_at" })
            Assert.True(ColumnExistsIn("commitment", column), column);

        Assert.True(ColumnExistsIn("flag", "decided_at"));
        Assert.True(ColumnExistsIn("action_item", "decided_at"));

        using (var connection = new Database(_path).Open())
        {
            // Nullable on purpose: every row from before v15 has no transcript pointer, and that
            // must read as "bilinmiyor", never as a constraint failure on the next write.
            using var nullable = connection.CreateCommand();
            nullable.CommandText =
                "SELECT \"notnull\" FROM pragma_table_info('reading_note') WHERE name = 'transcript_version_id';";
            Assert.Equal(0L, nullable.ExecuteScalar());

            using var verdicts = connection.CreateCommand();
            verdicts.CommandText = "SELECT COUNT(*) FROM verdict;";
            Assert.Equal(0L, verdicts.ExecuteScalar());

            using var index = connection.CreateCommand();
            index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_verdict_call';";
            Assert.Equal(1L, index.ExecuteScalar());
        }

        // v16: the habit cache, the dictionary and the intent card — by step here, by baseline
        // for fresh files. All three empty: the dictionary's seed is the application's to write
        // on first use, not the migration's, so a database upgraded on a machine that never
        // opens Aynam carries no rows it did not ask for.
        foreach (var table in new[] { "speech_habit", "habit_lexicon", "call_intent" })
        {
            using var connection = new Database(_path).Open();
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.Equal(0L, count.ExecuteScalar());
        }

        Assert.True(ColumnExistsIn("speech_habit", "transcript_version_id"));
        Assert.True(ColumnExistsIn("speech_habit", "lexicon_version"));
        Assert.True(ColumnExistsIn("habit_lexicon", "suffixes"));
        Assert.True(ColumnExistsIn("habit_lexicon", "lexeme_folded"));
        Assert.True(ColumnExistsIn("call_intent", "text"));

        using (var connection = new Database(_path).Open())
        {
            // The dictionary's identity is the kind and the folded stem. The same folded stem
            // twice under one kind is the bug the constraint exists to refuse — the merge's
            // INSERT OR IGNORE and the upsert both rest on it.
            using var twice = connection.CreateCommand();
            twice.CommandText =
                """
                INSERT INTO habit_lexicon (kind, lexeme_folded, lexeme) VALUES ('dolgu', 'yani', 'yani');
                INSERT INTO habit_lexicon (kind, lexeme_folded, lexeme) VALUES ('dolgu', 'yani', 'Yani');
                """;
            Assert.ThrowsAny<SqliteException>(() => twice.ExecuteNonQuery());

            using var clean = connection.CreateCommand();
            clean.CommandText = "DELETE FROM habit_lexicon;";
            clean.ExecuteNonQuery();
        }

        // v17: the contact card's evidence — the verified tactic quotes and the questions.
        // Both empty, both indexed the way the card reads them, and both cascading with the call
        // and the contact they belong to: a person deleted must not leave sentences behind.
        foreach (var table in new[] { "tactic_evidence", "speech_act" })
        {
            using var connection = new Database(_path).Open();
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.Equal(0L, count.ExecuteScalar());
        }

        foreach (var column in new[]
                 {
                     "call_id", "contact_id", "transcript_version_id", "source", "tactic",
                     "by_me", "quote", "quote_start_ms", "low_confidence", "model_used",
                     "dismissed_by_user", "created_at",
                 })
        {
            Assert.True(ColumnExistsIn("tactic_evidence", column), column);
        }

        foreach (var column in new[]
                 {
                     "call_id", "contact_id", "by_me", "kind", "answer_status",
                     "quote", "quote_start_ms", "low_confidence", "created_at",
                 })
        {
            Assert.True(ColumnExistsIn("speech_act", column), column);
        }

        using (var connection = new Database(_path).Open())
        {
            // The two indexes the card's reads depend on. A missing one is not a wrong answer,
            // it is a contact page that gets slower every year without anybody noticing why.
            using var indexes = connection.CreateCommand();
            indexes.CommandText =
                """
                SELECT COUNT(*) FROM sqlite_master
                 WHERE type = 'index' AND name IN ('ix_tactic_contact', 'ix_speech_act_contact');
                """;
            Assert.Equal(2L, indexes.ExecuteScalar());

            // answer_status is nullable on purpose: a word the extraction invented is stored as
            // "not recorded", never rounded to the nearest of the four.
            using var nullable = connection.CreateCommand();
            nullable.CommandText =
                "SELECT \"notnull\" FROM pragma_table_info('speech_act') WHERE name = 'answer_status';";
            Assert.Equal(0L, nullable.ExecuteScalar());
        }
    }

    /// <summary>
    /// v18: how it was said, and what was not a word.
    ///
    /// Two tables that come out of the audio rather than the words. Goes red when either is
    /// missing from an upgraded database, when the prosody row stops being keyed by the call, or
    /// when an audio event loses its pointer to the transcript that reported it.
    /// </summary>
    [Fact]
    public void TheEighteenthStepAddsTheAudioMeasurements()
    {
        new Database(_path).Migrate();

        Assert.True(ColumnExistsIn("prosody", "audio_key"));
        Assert.True(ColumnExistsIn("prosody", "json"));
        Assert.True(ColumnExistsIn("audio_event", "transcript_version_id"));
        Assert.True(ColumnExistsIn("audio_event", "channel"));

        using var connection = new Database(_path).Open();

        using var events = connection.CreateCommand();
        events.CommandText = "SELECT COUNT(*) FROM audio_event;";
        Assert.Equal(0L, events.ExecuteScalar());

        using var index = connection.CreateCommand();
        index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_audio_event_call';";
        Assert.Equal(1L, index.ExecuteScalar());
    }

    /// <summary>
    /// v19: what a model makes of a person, kept as history and as a dead end.
    ///
    /// Goes red when the table is missing from an upgraded database, when it stops being able to
    /// hold more than one reading per contact (the whole acceptance measurement depends on the
    /// older ones surviving), when the user's verdict column stops being nullable — "has not
    /// said" is not "agreed" — or when the index the panel's read depends on disappears.
    /// </summary>
    [Fact]
    public void TheNineteenthStepAddsTheContactReading()
    {
        new Database(_path).Migrate();

        foreach (var column in new[]
                 {
                     "contact_id", "json", "model_used", "calls_covered", "latest_call_id",
                     "input_hash", "excerpt_count", "rejected_count", "user_verdict", "created_at",
                 })
        {
            Assert.True(ColumnExistsIn("contact_reading", column), column);
        }

        using var connection = new Database(_path).Open();

        using var empty = connection.CreateCommand();
        empty.CommandText = "SELECT COUNT(*) FROM contact_reading;";
        Assert.Equal(0L, empty.ExecuteScalar());

        using var index = connection.CreateCommand();
        index.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_contact_reading';";
        Assert.Equal(1L, index.ExecuteScalar());

        // History, not one row per person: contact_id is not the primary key.
        using var key = connection.CreateCommand();
        key.CommandText = "SELECT \"pk\" FROM pragma_table_info('contact_reading') WHERE name = 'contact_id';";
        Assert.Equal(0L, key.ExecuteScalar());

        // "Nobody has said" must stay tellable from "agreed".
        using var nullable = connection.CreateCommand();
        nullable.CommandText =
            "SELECT \"notnull\" FROM pragma_table_info('contact_reading') WHERE name = 'user_verdict';";
        Assert.Equal(0L, nullable.ExecuteScalar());
    }

    /// <summary>
    /// v20: the questions people ask and the answers they paid for.
    ///
    /// Goes red when the table is missing from an upgraded database, when a column the panel reads
    /// disappears, or — the one that would quietly break the shell's Sor page — when call_id stops
    /// being nullable. A question asked of the whole archive belongs to no conversation, and a NOT
    /// NULL column there means it cannot be stored at all: the exact defect this table exists to
    /// fix, reintroduced by a schema edit.
    ///
    /// Also red if the citations column becomes optional. An answer restored without the quotes it
    /// cited is a claim with nothing behind it, which is the one thing this product does not show.
    /// </summary>
    [Fact]
    public void TheTwentiethStepAddsTheStoredQuestions()
    {
        new Database(_path).Migrate();

        foreach (var column in new[]
                 {
                     "call_id", "contact_id", "since_at", "until_at", "question", "answer",
                     "citations", "insufficient", "model_used", "transcript_version_id", "asked_at",
                 })
        {
            Assert.True(ColumnExistsIn("ask_exchange", column), column);
        }

        using var connection = new Database(_path).Open();

        using var empty = connection.CreateCommand();
        empty.CommandText = "SELECT COUNT(*) FROM ask_exchange;";
        Assert.Equal(0L, empty.ExecuteScalar());

        // A question asked of the archive has no call, and must still have somewhere to live.
        using var nullableCall = connection.CreateCommand();
        nullableCall.CommandText =
            "SELECT \"notnull\" FROM pragma_table_info('ask_exchange') WHERE name = 'call_id';";
        Assert.Equal(0L, nullableCall.ExecuteScalar());

        // The evidence is not optional.
        using var quotes = connection.CreateCommand();
        quotes.CommandText =
            "SELECT \"notnull\" FROM pragma_table_info('ask_exchange') WHERE name = 'citations';";
        Assert.Equal(1L, quotes.ExecuteScalar());

        // History, not one row per call: several questions about one conversation are several rows.
        using var key = connection.CreateCommand();
        key.CommandText = "SELECT \"pk\" FROM pragma_table_info('ask_exchange') WHERE name = 'call_id';";
        Assert.Equal(0L, key.ExecuteScalar());

        foreach (var index in new[] { "ix_ask_call", "ix_ask_asked" })
        {
            using var probe = connection.CreateCommand();
            probe.CommandText =
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{index}';";
            Assert.Equal(1L, probe.ExecuteScalar());
        }
    }

    /// <summary>
    /// The general form of the test above: every table, every column, compared between a
    /// database that walked the steps and one born fresh. The spot checks catch the column
    /// somebody thought to assert; this catches the one they forgot — a column in the step but
    /// not the baseline, or the other way round, which gives fresh and upgraded installations
    /// different shapes and a bug that only the user with the older file ever sees.
    ///
    /// Column ORDER is allowed to differ (ALTER appends; the baseline places), so the sets are
    /// compared by name.
    /// </summary>
    [Fact]
    public void AnUpgradedDatabaseHasEveryColumnAFreshOneHas()
    {
        using (var connection = new Database(_path).Open())
        {
            using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE call (id INTEGER PRIMARY KEY AUTOINCREMENT, contact_id INTEGER,
                    app INTEGER NOT NULL DEFAULT 0, direction INTEGER NOT NULL DEFAULT 0,
                    kind INTEGER NOT NULL DEFAULT 0, started_at TEXT NOT NULL, ended_at TEXT,
                    duration_ms INTEGER NOT NULL DEFAULT 0, mic_path TEXT, far_path TEXT,
                    state INTEGER NOT NULL DEFAULT 0, failure_reason TEXT, observed_title TEXT,
                    capture_stats TEXT, likely_no_headphones INTEGER NOT NULL DEFAULT 0,
                    is_pinned INTEGER NOT NULL DEFAULT 0, audio_sha256 TEXT);
                CREATE TABLE setting (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO setting VALUES ('schema_version', '2');
                """;
            create.ExecuteNonQuery();
        }

        new Database(_path).Migrate();

        var freshPath = Path.Combine(_root, "fresh.db");
        var fresh = new Database(freshPath);
        fresh.Migrate();

        try
        {
            var upgraded = Shape(_path);
            var born = Shape(freshPath);

            var missing = born.Keys.Except(upgraded.Keys).OrderBy(k => k).ToList();
            var extra = upgraded.Keys.Except(born.Keys).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0, "Yükseltilen veritabanında olmayan sütunlar: " + string.Join(", ", missing));
            Assert.True(extra.Count == 0, "Taze veritabanında olmayan sütunlar: " + string.Join(", ", extra));

            foreach (var (key, shape) in born)
                Assert.True(shape == upgraded[key], $"{key}: taze {shape}, yükseltilen {upgraded[key]}");
        }
        finally
        {
            fresh.ClearPool();
        }
    }

    /// <summary>Every user table's (table.column) → "type notnull default", for the comparison above.</summary>
    private static Dictionary<string, string> Shape(string path)
    {
        var shape = new Dictionary<string, string>(StringComparer.Ordinal);

        using var connection = new Database(path).Open();

        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        var names = new List<string>();
        using (var reader = tables.ExecuteReader())
        {
            while (reader.Read()) names.Add(reader.GetString(0));
        }

        foreach (var table in names)
        {
            using var columns = connection.CreateCommand();
            columns.CommandText = $"SELECT name, type, \"notnull\", IFNULL(dflt_value, '') FROM pragma_table_info('{table}');";

            using var reader = columns.ExecuteReader();
            while (reader.Read())
                shape[$"{table}.{reader.GetString(0)}"] = $"{reader.GetString(1)} {reader.GetInt64(2)} {reader.GetString(3)}";
        }

        return shape;
    }

    private bool ColumnExistsIn(string table, string column)
    {
        using var connection = new Database(_path).Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        return (long)command.ExecuteScalar()! > 0;
    }
}
