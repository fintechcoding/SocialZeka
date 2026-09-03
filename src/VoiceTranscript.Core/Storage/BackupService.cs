using System.IO.Compression;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Export;

namespace VoiceTranscript.Core.Storage;

/// <summary>What a backup or an export ended up containing.</summary>
public sealed record ArchiveResult(string Path, int Files, long Bytes)
{
    public string SizeText
    {
        get
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = Bytes;
            var unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.#} {units[unit]}";
        }
    }
}

/// <summary>
/// Getting the archive out, in one piece and in a form that outlives this application.
///
/// Two different things, and a product that keeps years of somebody's conversations owes them
/// both.
///
/// A **backup** is a copy that can be restored here: the database, the settings and optionally
/// the recordings, in one file. It answers "the laptop died".
///
/// An **export** is the same content as plain markdown, one file per conversation and one page
/// per person. It answers a harder question — what happens to this archive if this application
/// stops being maintained — and the answer has to be "it is a folder of text files you can read
/// in anything", because anything else makes the archive hostage to the software.
/// </summary>
public sealed class BackupService(AppPaths paths, Repository repository)
{
    /// <summary>
    /// Writes a single archive file.
    ///
    /// Audio is optional and off by default, because it dominates the size: an hour of
    /// conversation is about two hundred megabytes of WAV, so a year of ordinary use makes a
    /// backup nobody will ever actually take. Without it the file is a few megabytes and still
    /// carries every word, every ledger entry and every setting.
    /// </summary>
    /// <summary>Whether this file needs a password before it can be restored.</summary>
    public static bool NeedsPassword(string archivePath) =>
        Export.EncryptedArchive.LooksLikeOne(archivePath);

    public async Task<ArchiveResult> BackupAsync(
        string destination,
        bool includeAudio = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        string? password = null)
    {
        progress?.Report("Yedek hazırlanıyor…");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) File.Delete(destination);

        // A password turns the file into something only this application can open, so the archive
        // is built plainly first and sealed at the end. Built beside the destination rather than
        // in the temp folder: a backup with audio is gigabytes, and the disk somebody chose to put
        // it on is the one with room for it.
        var plainPath = password is null ? destination : destination + ".hazirlaniyor";

        var files = 0;

        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(plainPath, ZipArchiveMode.Create);

            // The database first, and with its journal: SQLite in WAL mode keeps recent writes
            // in a side file, so copying only the main file can silently lose the last minutes.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = paths.DatabaseFile + suffix;
                if (!File.Exists(path)) continue;

                Add(archive, path, $"data/{Path.GetFileName(path)}");
                files++;
            }

            if (File.Exists(paths.SettingsFile))
            {
                Add(archive, paths.SettingsFile, "data/settings.json");
                files++;
            }

            if (!includeAudio) return;

            progress?.Report("Ses kayıtları ekleniyor…");

            foreach (var file in Directory.EnumerateFiles(paths.Recordings, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(paths.Recordings, file).Replace('\\', '/');
                Add(archive, file, $"recordings/{relative}");
                files++;

                if (files % 25 == 0) progress?.Report($"{files} dosya eklendi…");
            }
        }, ct);

        if (password is not null)
        {
            progress?.Report("Yedek şifreleniyor…");

            await Task.Run(() =>
            {
                using (var plain = File.OpenRead(plainPath))
                using (var sealedFile = File.Create(destination))
                {
                    Export.EncryptedArchive.Write(sealedFile, plain, password);
                }

                // The unencrypted copy must not outlive the encrypted one, or the password was
                // decoration: somebody who asked for this expects the readable version gone.
                File.Delete(plainPath);
            }, ct);
        }

        var size = new FileInfo(destination).Length;

        progress?.Report(password is null
            ? $"Yedek hazır: {files} dosya."
            : $"Yedek hazır ve şifrelendi: {files} dosya.");

        return new ArchiveResult(destination, files, size);
    }

    /// <summary>
    /// Writes every conversation as markdown.
    ///
    /// The same exporter the Obsidian integration uses, pointed at a folder of the user's
    /// choosing. Deliberately not a bespoke format: the point is that these files stay readable
    /// with no software at all, which is the only honest answer to "what if you stop working on
    /// this".
    /// </summary>
    public async Task<ArchiveResult> ExportEverythingAsync(
        string folder,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);

        var exporter = new ObsidianExporter(repository, new ObsidianOptions
        {
            VaultPath = folder,
            Folder = "Görüşmeler",
        });

        var written = 0;

        await Task.Run(() =>
        {
            var calls = repository.ListCalls(limit: 100_000);

            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();

                // An unattributed call has no contact page to belong to, and exporting it under
                // a placeholder name would put a stranger in somebody's archive.
                if (call.ContactId is null) continue;

                try
                {
                    exporter.ExportCall(call.Id);
                    written++;
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    // One unwritable note must not stop the export of the other four hundred.
                }

                if (written % 10 == 0) progress?.Report($"{written} görüşme yazıldı…");
            }
        }, ct);

        var bytes = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

        progress?.Report($"Dışa aktarıldı: {written} görüşme.");

        return new ArchiveResult(folder, written, bytes);
    }

    /// <summary>Name of the folder a staged restore waits in.</summary>
    private const string StagingFolder = "geri-yukleme";

    /// <summary>
    /// Prepares a restore, to be applied on the next start.
    ///
    /// Staged rather than applied immediately, because the database is open: this application
    /// runs in the tray for weeks and the files are held by the process doing the restoring.
    /// Trying to overwrite them in place fails halfway through, which is the worst possible
    /// outcome for an operation somebody reached for because they had already lost something.
    ///
    /// So the archive is unpacked beside the live data and a marker is left. The next start puts
    /// it into place before anything opens the database, and moves the current data aside rather
    /// than deleting it — a restore from the wrong file must not destroy the archive it was
    /// meant to protect.
    /// </summary>
    public async Task<int> StageRestoreAsync(
        string archivePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        string? password = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Yedek dosyası bulunamadı.", archivePath);

        // An encrypted backup is opened into a plain copy first, and that copy is removed however
        // this ends. It is written beside the staging area rather than in the temp folder for the
        // same reason the backup was: with audio it is gigabytes.
        var opened = archivePath;

        if (NeedsPassword(archivePath))
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Bu yedek parolalı. Parola olmadan açılamaz.");

            progress?.Report("Yedek çözülüyor…");

            opened = Path.Combine(paths.Root, "geri-yukleme.acilan");

            var fault = await Task.Run(() =>
            {
                using var input = File.OpenRead(archivePath);
                using var output = File.Create(opened);

                return Export.EncryptedArchive.TryRead(input, output, password);
            }, ct);

            if (fault != Export.ArchiveFault.None)
            {
                TryDelete(opened);
                throw new InvalidOperationException(Export.EncryptedArchive.Explain(fault));
            }
        }

        var staging = Path.Combine(paths.Root, StagingFolder);

        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        progress?.Report("Yedek açılıyor…");

        var extracted = 0;

        try
        {
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(opened);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Length == 0) continue;

                // Only the two known prefixes, and never a path that climbs out of the folder.
                // An archive is untrusted input even when the user chose the file.
                var name = entry.FullName.Replace('\\', '/');

                if (!name.StartsWith("data/", StringComparison.Ordinal)
                    && !name.StartsWith("recordings/", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                extracted++;
            }
        }, ct);
        }
        finally
        {
            // However this ended. A decrypted copy of somebody's archive left on disk beside the
            // encrypted one would make the password pointless.
            if (!ReferenceEquals(opened, archivePath) && opened != archivePath) TryDelete(opened);
        }

        if (extracted == 0)
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidOperationException("Bu dosya bir VoiceTranscript yedeği değil.");
        }

        progress?.Report($"{extracted} dosya hazırlandı. Uygulama yeniden başlatıldığında yerine konacak.");
        return extracted;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind rather than throwing over a temporary file; the sweep will get it.
        }
    }

    /// <summary>Whether a restore is waiting to be applied.</summary>
    public static bool HasPendingRestore(AppPaths paths) =>
        Directory.Exists(Path.Combine(paths.Root, StagingFolder));

    /// <summary>
    /// Applies a staged restore. Must run before anything opens the database.
    ///
    /// Returns where the previous data was moved to, so it can be named to the user. Nothing is
    /// deleted: if the restore turns out to be the wrong one, what they had is still on disk.
    /// </summary>
    public static string? ApplyPendingRestore(AppPaths paths)
    {
        var staging = Path.Combine(paths.Root, StagingFolder);
        if (!Directory.Exists(staging)) return null;

        // Deliberately does NOT clear the SQLite connection pool.
        //
        // That call is process-wide: it disposes pooled connections belonging to every database
        // in the process, including ones another component is about to use. It was added here to
        // make a test pass and it made a different test fail intermittently, with a
        // ObjectDisposedException surfacing inside an unrelated query.
        //
        // It is also unnecessary. This method runs at startup before anything opens the database,
        // which is the contract stated above — and the caller is responsible for honouring it.

        var aside = Path.Combine(paths.Root, $"onceki-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(aside);

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = paths.DatabaseFile + suffix;
            if (File.Exists(path)) File.Move(path, Path.Combine(aside, Path.GetFileName(path)), overwrite: true);
        }

        if (File.Exists(paths.SettingsFile))
            File.Move(paths.SettingsFile, Path.Combine(aside, "settings.json"), overwrite: true);

        var data = Path.Combine(staging, "data");

        if (Directory.Exists(data))
        {
            foreach (var file in Directory.EnumerateFiles(data))
            {
                var name = Path.GetFileName(file);

                var target = name == "settings.json"
                    ? paths.SettingsFile
                    : Path.Combine(Path.GetDirectoryName(paths.DatabaseFile)!, name);

                File.Move(file, target, overwrite: true);
            }
        }

        var recordings = Path.Combine(staging, "recordings");

        if (Directory.Exists(recordings))
        {
            foreach (var file in Directory.EnumerateFiles(recordings, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(recordings, file);
                var target = Path.Combine(paths.Recordings, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
            }
        }

        Directory.Delete(staging, recursive: true);
        return aside;
    }

    private static void Add(ZipArchive archive, string path, string entryName)
    {
        // Read with sharing: the database is open and the recorder may be writing.
        using var source = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        using var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        source.CopyTo(entry);
    }
}
