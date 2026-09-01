using Microsoft.Data.Sqlite;

namespace VoiceTranscript.Core.Storage;

/// <summary>
/// Opens and configures the SQLite database.
///
/// Several pragmas here are per-connection rather than persistent, which is a common source of
/// bugs: foreign keys in particular default to OFF on every new connection, so a cascade that
/// works in one code path silently does nothing in another. Everything goes through
/// <see cref="Open"/> so that cannot happen.
/// </summary>
public sealed class Database(string path)
{
    public string Path { get; } = path;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // The recorder writes while the UI reads, so pooling plus WAL is what keeps
        // "database is locked" from appearing at the end of every call.
        Pooling = true,
    }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);

        connection.Open();

        using var pragmas = connection.CreateCommand();
        pragmas.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            PRAGMA secure_delete = ON;
            """;
        pragmas.ExecuteNonQuery();

        return connection;
    }

    /// <summary>
    /// Releases the pooled connections belonging to <em>this</em> database, and nothing else.
    ///
    /// Needed because a pooled connection keeps the file open on Windows, so a caller that wants
    /// to move or delete the database has to let go of it first.
    ///
    /// The obvious way to do that is SqliteConnection.ClearAllPools, and it is a trap. That
    /// method is keyed by nothing: it disposes every pooled handle in the process, including one
    /// another thread is in the middle of renting for a completely unrelated database. The victim
    /// then throws ObjectDisposedException from the first statement it issues, which is the
    /// PRAGMA batch below — a failure that names a different innocent caller every time and
    /// disappears on a re-run.
    ///
    /// Measured, not assumed: six threads each hammering their own database file survived
    /// 867,646 open cycles with no clears and over a million with per-database clears, and failed
    /// within seconds once a second thread began calling ClearAllPools.
    /// </summary>
    public void ClearPool() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));

    /// <summary>
    /// Brings the database to the current schema, whatever it is today. Safe on every start.
    ///
    /// Baseline plus delta. A fresh database is created whole from <see cref="Schema.Statements"/>
    /// and stamped current. An existing one first gets the baseline replayed (idempotent — it only
    /// ever CREATEs IF NOT EXISTS, which is how new tables have always arrived), then any
    /// <see cref="Migrations.Steps"/> above its stored version, in order, each in its own
    /// transaction — and before the first of those runs, the whole file is snapshotted beside
    /// itself. An upgrade that goes wrong must leave a database to go back to; "the migration
    /// failed" and "the archive is gone" are different sentences.
    /// </summary>
    /// <param name="steps">Overridable for tests; production always means <see cref="Migrations.Steps"/>.</param>
    public void Migrate(IReadOnlyList<Migrations.Step>? steps = null)
    {
        steps ??= Migrations.Steps;

        using var connection = Open();

        var stored = StoredVersion(connection);
        var fresh = stored == 0 && !TableExists(connection, "call");

        // A fresh database skips the steps entirely: the baseline below creates it already in
        // its final shape, and replaying history onto it would alter columns it was born with.
        List<Migrations.Step> pending = fresh
            ? []
            : [.. steps.Where(s => s.Version > stored).OrderBy(s => s.Version)];

        // The snapshot, before anything changes shape. VACUUM INTO writes a consistent copy
        // without closing the pooled connections that keep the live file open on Windows.
        if (pending.Count > 0)
        {
            var backup = $"{Path}.premigration-v{stored}";

            try
            {
                if (File.Exists(backup)) File.Delete(backup);

                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $target;";
                vacuum.Parameters.AddWithValue("$target", backup);
                vacuum.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                // No snapshot, no migration. Proceeding without one turns any step bug into
                // data loss; refusing turns it into an error message, and only one of those
                // can be apologised for.
                throw new InvalidOperationException(
                    $"Veritabanı yedeği alınamadı, şema güncellemesi yapılmadı: {e.Message}", e);
            }
        }

        ApplyBaseline(connection);

        foreach (var step in pending)
        {
            using var transaction = connection.BeginTransaction();

            foreach (var sql in step.Sql)
            {
                // ADD COLUMN steps are made idempotent here, because SQLite offers no IF NOT
                // EXISTS for columns and the baseline runs first: a table that did not exist at
                // the stored version is created by the baseline in its CURRENT shape, and the
                // step that adds its column then arrives late to a done job. Skipping is the
                // step succeeding, not being ignored — the column is there either way.
                if (AddColumnTarget(sql) is ({ } table, { } column)
                    && ColumnExists(connection, table, column))
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            WriteVersion(connection, transaction, step.Version);
            transaction.Commit();
        }

        // Fresh databases are born at the latest version; existing ones have now walked to it.
        var final = Math.Max(Schema.Version, Latest(steps));

        if (StoredVersion(connection) < final)
        {
            using var transaction = connection.BeginTransaction();
            WriteVersion(connection, transaction, final);
            transaction.Commit();
        }
    }

    private static int Latest(IReadOnlyList<Migrations.Step> steps) =>
        steps.Count == 0 ? 0 : steps.Max(s => s.Version);

    private void ApplyBaseline(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        foreach (var statement in Schema.Statements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The version this database last recorded, or zero for none.</summary>
    public static int StoredVersion(SqliteConnection connection)
    {
        if (!TableExists(connection, "setting")) return 0;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM setting WHERE key = 'schema_version';";

        return int.TryParse(command.ExecuteScalar() as string, out var version) ? version : 0;
    }

    /// <summary>The (table, column) of an ALTER TABLE … ADD COLUMN statement, or null.</summary>
    private static (string Table, string Column)? AddColumnTarget(string sql)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            sql.Trim(),
            @"^ALTER\s+TABLE\s+(\w+)\s+ADD\s+COLUMN\s+(\w+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);

        return command.ExecuteScalar() is not null;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);

        return command.ExecuteScalar() is not null;
    }

    private static void WriteVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO setting(key, value) VALUES('schema_version', $v) " +
                              "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$v", version.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Verifies FTS5 is actually compiled into the SQLite build in use.
    ///
    /// Worth an explicit check: only some SQLitePCLRaw bundles include FTS5, and when it is
    /// missing the failure is a confusing "no such module" at schema creation rather than
    /// anything that points at the real cause.
    /// </summary>
    public static bool Fts5Available()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE fts_probe USING fts5(x);";

        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
