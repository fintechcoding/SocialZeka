using System.IO.Compression;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Getting the archive out, and putting it back.
///
/// A product that keeps years of somebody's conversations owes them a way to take those
/// conversations elsewhere. These tests pin the two properties that make that promise real: the
/// backup actually contains the words, and a restore cannot destroy the archive it was meant to
/// protect.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-backup-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;
    private readonly BackupService _backup;

    public BackupServiceTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);
        _backup = new BackupService(_paths, _repository);

        SampleData.Load(_repository, _paths);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The database file can still be held briefly.
        }

        GC.SuppressFinalize(this);
    }

    private string Destination(string name) => Path.Combine(_root, "out", name);

    [Fact]
    public async Task ABackupContainsTheDatabaseAndTheSettings()
    {
        File.WriteAllText(_paths.SettingsFile, "{}");

        var result = await _backup.BackupAsync(Destination("yedek.zip"));

        Assert.True(File.Exists(result.Path));
        Assert.True(result.Bytes > 0);

        using var archive = ZipFile.OpenRead(result.Path);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains(names, n => n.StartsWith("data/", StringComparison.Ordinal) && n.EndsWith(".db"));
        Assert.Contains("data/settings.json", names);
    }

    [Fact]
    public async Task AudioIsLeftOutUnlessItIsAskedFor()
    {
        // An hour of conversation is about two hundred megabytes of WAV. A backup that includes
        // it by default is a backup nobody ever actually takes.
        var without = await _backup.BackupAsync(Destination("yalin.zip"));
        var with = await _backup.BackupAsync(Destination("tam.zip"), includeAudio: true);

        using (var archive = ZipFile.OpenRead(without.Path))
        {
            Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith("recordings/", StringComparison.Ordinal));
        }

        using (var archive = ZipFile.OpenRead(with.Path))
        {
            Assert.Contains(archive.Entries, e => e.FullName.StartsWith("recordings/", StringComparison.Ordinal));
        }

        Assert.True(with.Files > without.Files);
    }

    [Fact]
    public async Task ABackupCanBeTakenWhileTheDatabaseIsOpen()
    {
        // The recorder holds these files while a call is in progress, which is exactly when
        // somebody might press backup. Opening them without sharing would throw.
        _ = _repository.ListContacts();

        var result = await _backup.BackupAsync(Destination("acikken.zip"));

        Assert.True(result.Files >= 1);
    }

    [Fact]
    public async Task ExportingWritesReadableMarkdownForEveryConversation()
    {
        // The point is that these files stay readable with no software at all. Anything else
        // makes the archive hostage to this application still existing.
        var folder = Destination("disari");
        var result = await _backup.ExportEverythingAsync(folder);

        Assert.Equal(3, result.Files);

        var written = Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(written);

        var all = string.Join("\n", written.Select(File.ReadAllText));

        Assert.Contains("On iki bin", all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SampleData.ContactName, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARestoreIsStagedRatherThanAppliedWhileTheDatabaseIsOpen()
    {
        // This application lives in the tray for weeks, so the files are held by the very
        // process doing the restoring. Overwriting them in place fails halfway through, which is
        // the worst possible outcome for something somebody reached for after losing data.
        var backup = await _backup.BackupAsync(Destination("once.zip"));

        var staged = await _backup.StageRestoreAsync(backup.Path);

        Assert.True(staged > 0);
        Assert.True(BackupService.HasPendingRestore(_paths));

        // Nothing has changed yet: the archive is still exactly as it was.
        Assert.Single(_repository.ListContacts());
    }

    [Fact]
    public async Task ApplyingAStagedRestorePutsTheOldDataAsideRatherThanDeletingIt()
    {
        var backup = await _backup.BackupAsync(Destination("once.zip"));
        await _backup.StageRestoreAsync(backup.Path);

        // Applying a restore is documented as something that happens before anything opens the
        // database. A live process has no pooled connections at that point; this test does,
        // because its fixture opened one. Releasing them is how the test honours the contract
        // rather than asking the production code to work around it.
        // Scoped to this test’s own database. ClearAllPools would dispose pooled handles
        // belonging to every other test class running in parallel, which is a real and
        // measured source of ObjectDisposedException in unrelated tests.
        new Database(_paths.DatabaseFile).ClearPool();

        var aside = BackupService.ApplyPendingRestore(_paths);

        Assert.NotNull(aside);
        Assert.True(Directory.Exists(aside), "eski veriler kenara alınmadı");
        Assert.NotEmpty(Directory.EnumerateFiles(aside!));
        Assert.True(File.Exists(_paths.DatabaseFile), "veritabanı geri gelmedi");
        Assert.False(BackupService.HasPendingRestore(_paths));
    }

    /// <summary>
    /// A restore puts the conversations back and leaves THIS machine's settings alone.
    ///
    /// Goes red the day a restore starts replacing settings.json again. It used to, and the damage
    /// is specific: the other computer's microphone and speaker identity, its API keys and its
    /// data root land here, so a restore taken to recover an archive silently repoints the
    /// recorder at a device that does not exist on this machine. <c>MergeArchive</c>'s own
    /// documentation forbids exactly this, and the restore was doing it anyway.
    ///
    /// The incoming file is not deleted either — it goes into the aside folder under a name that
    /// says what it is, because a restore must destroy nothing.
    /// </summary>
    [Fact]
    public async Task ARestoreLeavesThisMachinesSettingsAlone()
    {
        File.WriteAllText(_paths.SettingsFile, """{"AsrModelId":"oteki-makinenin-modeli"}""");

        var backup = await _backup.BackupAsync(Destination("oteki.zip"));

        // What this machine has now, written after the backup was taken: what a restore must not
        // touch, and what the old behaviour replaced.
        File.WriteAllText(_paths.SettingsFile, """{"AsrModelId":"bu-makinenin-modeli"}""");

        await _backup.StageRestoreAsync(backup.Path);
        new Database(_paths.DatabaseFile).ClearPool();

        var aside = BackupService.ApplyPendingRestore(_paths);

        Assert.NotNull(aside);
        Assert.Contains("bu-makinenin-modeli", File.ReadAllText(_paths.SettingsFile));

        // And the other machine's settings are set down beside the previous data rather than
        // thrown away, so a value can still be copied out of them by hand.
        var carried = Path.Combine(aside!, "yedekten-gelen-settings.json");
        Assert.True(File.Exists(carried), "yedekten gelen ayar dosyası kenara alınmadı");
        Assert.Contains("oteki-makinenin-modeli", File.ReadAllText(carried));
    }

    /// <summary>
    /// The other half of the same rule: a restore onto an installation with no settings at all
    /// puts the backup's settings in place.
    ///
    /// Writing into an empty place is a move, not a merge — there is nothing here to displace, so
    /// nothing is being overwritten. Goes red when the fix above is taken too far and a restore
    /// after a dead laptop comes back with no API keys and no audio devices, which would trade one
    /// silent loss for another.
    /// </summary>
    [Fact]
    public async Task ARestoreOntoAnInstallationWithNoSettingsBringsThemBack()
    {
        File.WriteAllText(_paths.SettingsFile, """{"AsrModelId":"kaybolan-makine"}""");

        var backup = await _backup.BackupAsync(Destination("olen-dizustu.zip"));

        await _backup.StageRestoreAsync(backup.Path);
        new Database(_paths.DatabaseFile).ClearPool();

        File.Delete(_paths.SettingsFile);

        BackupService.ApplyPendingRestore(_paths);

        Assert.True(File.Exists(_paths.SettingsFile), "boş yere yazmak taşımadır — ayarlar gelmedi");
        Assert.Contains("kaybolan-makine", File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void ApplyingWithNothingStagedDoesNothingAtAll()
    {
        Assert.Null(BackupService.ApplyPendingRestore(_paths));
        Assert.True(File.Exists(_paths.DatabaseFile));
    }

    [Fact]
    public async Task StagingFromAMissingFileSaysSoRatherThanEmptyingTheArchive()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _backup.StageRestoreAsync(Destination("olmayan.zip")));

        Assert.Single(_repository.ListContacts());
        Assert.False(BackupService.HasPendingRestore(_paths));
    }

    [Fact]
    public async Task StagingSomethingThatIsNotABackupIsRefused()
    {
        // An archive is untrusted input even when the user picked the file, and a restore that
        // half-applies a stranger is worse than one that refuses.
        var stranger = Destination("yabanci.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(stranger)!);

        using (var archive = ZipFile.Open(stranger, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("okuma.txt").Open();
            entry.Write("merhaba"u8);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _backup.StageRestoreAsync(stranger));

        Assert.False(BackupService.HasPendingRestore(_paths));
        Assert.Single(_repository.ListContacts());
    }

    // ---- password ----------------------------------------------------------

    /// <summary>
    /// The round trip that matters: written with a password, restored with it, nothing lost.
    ///
    /// A backup holds every word of every conversation and often the audio too, and it is going to
    /// sit on a disk or in a cloud folder somewhere. Encryption that produced a file the
    /// application could not read back would be worse than none.
    /// </summary>
    [Fact]
    public async Task ABackupWrittenWithAPasswordIsRestoredWithIt()
    {
        var backup = await _backup.BackupAsync(Destination("kilitli.zip"), password: "parolam");

        Assert.True(Core.Storage.BackupService.NeedsPassword(backup.Path));

        var staged = await _backup.StageRestoreAsync(backup.Path, password: "parolam");

        Assert.True(staged > 0);
    }

    /// <summary>
    /// The wrong password restores nothing. It has to fail before anything is staged: a restore
    /// that half-applies is the worst outcome for an operation somebody reached for because they
    /// had already lost something.
    /// </summary>
    [Fact]
    public async Task TheWrongPasswordRestoresNothing()
    {
        var backup = await _backup.BackupAsync(Destination("kilitli.zip"), password: "dogru");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backup.StageRestoreAsync(backup.Path, password: "yanlis"));

        Assert.Contains("Parola", failed.Message);
    }

    [Fact]
    public async Task AndSoDoesNoPasswordAtAll()
    {
        var backup = await _backup.BackupAsync(Destination("kilitli.zip"), password: "parolam");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backup.StageRestoreAsync(backup.Path));
    }

    /// <summary>
    /// A backup reported as written can be opened with the password just used.
    ///
    /// This is the one guarantee a locked backup has to make, and it is the one nobody can check
    /// later: the readable copy is deleted the moment the encrypted one exists, and whether that
    /// file opens is discovered on the day somebody needs it. So it is checked here, by opening
    /// it — every frame of an AES-GCM archive carries its own tag, so reading it through proves
    /// the key and the bytes together.
    /// </summary>
    [Fact]
    public async Task AReportedBackupOpensWithThePasswordItWasGiven()
    {
        var backup = await _backup.BackupAsync(Destination("kilitli.zip"), password: "dogru parola");

        Assert.True(Core.Storage.BackupService.NeedsPassword(backup.Path));

        using var written = File.OpenRead(backup.Path);

        Assert.Equal(
            Core.Export.ArchiveFault.None,
            Core.Export.EncryptedArchive.TryRead(written, Stream.Null, "dogru parola"));
    }

    /// <summary>
    /// An empty password means no encryption, and the file must say so about itself.
    ///
    /// The two are asked for in the same box and the difference decides whether the restore ever
    /// prompts. A file encrypted under an empty password would be one the window can never open:
    /// it would ask for a password and treat the empty answer as "cancelled", forever.
    /// </summary>
    [Fact]
    public async Task AnEmptyPasswordLeavesTheFileReadable()
    {
        var backup = await _backup.BackupAsync(Destination("bos.zip"), password: null);

        Assert.False(Core.Storage.BackupService.NeedsPassword(backup.Path));

        // And it restores without one being asked for.
        Assert.True(await _backup.StageRestoreAsync(backup.Path) > 0);
    }

    /// <summary>
    /// The readable copy must not outlive the encrypted one. Somebody who asked for a password
    /// expects the plain version gone, not sitting beside it with a different extension.
    /// </summary>
    [Fact]
    public async Task NoReadableCopyIsLeftBeside()
    {
        var backup = await _backup.BackupAsync(Destination("kilitli.zip"), password: "parolam");

        var beside = Directory.GetFiles(Path.GetDirectoryName(backup.Path)!);

        Assert.Equal(backup.Path, Assert.Single(beside));
    }

    /// <summary>An ordinary backup stays ordinary: one click, no password, opens in any tool.</summary>
    [Fact]
    public async Task WithoutAPasswordNothingChanges()
    {
        var backup = await _backup.BackupAsync(Destination("acik.zip"));

        Assert.False(Core.Storage.BackupService.NeedsPassword(backup.Path));

        Assert.True(await _backup.StageRestoreAsync(backup.Path) > 0);
    }
}
