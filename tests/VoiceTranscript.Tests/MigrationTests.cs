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

    /// <summary>Shipped steps must be strictly ascending and above the baseline.</summary>
    [Fact]
    public void TheShippedStepListIsWellFormed()
    {
        var versions = Migrations.Steps.Select(s => s.Version).ToList();

        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Distinct(), versions);
        Assert.All(versions, v => Assert.True(v > Schema.Version || Migrations.Steps.Count == 0));
    }
}
