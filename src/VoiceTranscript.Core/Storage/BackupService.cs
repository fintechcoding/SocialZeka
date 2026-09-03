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

/// <summary>What an import added to the archive that was already here.</summary>
public sealed record ImportResult(int Contacts, int Calls, int Segments, int Recordings, int AlreadyHere);

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

                // Opened again with the same password before the readable copy is let go.
                //
                // Everything downstream of this point is irreversible and silent: the plain zip
                // is deleted, and whether the encrypted one can be opened is not discovered until
                // somebody needs it — which, for a backup, is the worst day to find out. The
                // archive is AES-GCM with a tag per frame, so reading it through and discarding
                // the output verifies every byte and the key together. One extra pass over the
                // file is nothing against the alternative.
                //
                // Written to Stream.Null rather than to disk: nothing here wants the plaintext,
                // only the answer to whether it comes back.
                using (var written = File.OpenRead(destination))
                {
                    var fault = Export.EncryptedArchive.TryRead(written, Stream.Null, password);

                    if (fault != Export.ArchiveFault.None)
                    {
                        // The readable copy stays, and the unusable encrypted one goes. A backup
                        // that exists and cannot be opened is worse than one that is not secret.
                        TryDelete(destination);

                        throw new InvalidOperationException(
                            "Yedek şifrelendi ama aynı parolayla geri açılamadı, o yüzden "
                            + "yazılmadı: " + Export.EncryptedArchive.Explain(fault)
                            + " Şifrelenmemiş kopya duruyor: " + plainPath);
                    }
                }

                // The unencrypted copy must not outlive the encrypted one, or the password was
                // decoration: somebody who asked for this expects the readable version gone.
                File.Delete(plainPath);
            }, ct);
        }

        var size = new FileInfo(destination).Length;

        // Written down, because nothing else records that this happened.
        //
        // A backup and a restore are the two operations that move somebody's whole archive, and
        // neither left a trace — so when a restore behaved unexpectedly there was nothing to look
        // at afterwards, only a memory of which buttons were pressed. Names and content stay out;
        // what is kept is what was done, to how much, and whether it was locked.
        CoreLog.Write("veri",
            $"yedek yazıldı: {files} dosya · {size / 1_048_576.0:0.0} MB · "
            + $"ses {(includeAudio ? "dahil" : "haric")} · "
            + $"{(password is null ? "sifrelenmedi" : "sifrelendi ve geri acilarak dogrulandi")}");

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

        // Said before anything is opened, so a restore that behaved unexpectedly can be read
        // back afterwards rather than reconstructed from memory. Whether the FILE is encrypted
        // and whether a password was SUPPLIED are recorded separately: the interesting case is
        // the two disagreeing.
        CoreLog.Write("veri",
            $"geri yukleme baslatildi: {Path.GetFileName(archivePath)} · "
            + $"{new FileInfo(archivePath).Length / 1_048_576.0:0.0} MB · "
            + $"dosya {(NeedsPassword(archivePath) ? "sifreli" : "sifresiz")} · "
            + $"parola {(string.IsNullOrEmpty(password) ? "verilmedi" : "verildi")}");

        var opened = await OpenForReadingAsync(archivePath, password, StagingFolder + ".acilan", progress, ct);

        var staging = Path.Combine(paths.Root, StagingFolder);
        var extracted = await UnpackAsync(opened, archivePath, staging, progress, ct);

        progress?.Report($"{extracted} dosya hazırlandı. Uygulama yeniden başlatıldığında yerine konacak.");
        return extracted;
    }

    /// <summary>
    /// Empties <paramref name="staging"/> and unpacks the archive into it.
    ///
    /// Shared by the restore and the import because "what a backup file is" should have one
    /// answer: the two known prefixes, nothing that climbs out of the folder, and the decrypted
    /// copy removed however this ends — an archive is untrusted input even when the user chose
    /// the file themselves.
    /// </summary>
    /// <param name="opened">The readable zip, which is the decrypted copy when there was one.</param>
    /// <param name="original">The file the user chose, so the decrypted copy can be told apart.</param>
    private static async Task<int> UnpackAsync(
        string opened,
        string original,
        string staging,
        IProgress<string>? progress,
        CancellationToken ct)
    {
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
            // A decrypted copy of somebody's archive left on disk beside the encrypted one would
            // make the password pointless.
            if (!ReferenceEquals(opened, original) && opened != original) TryDelete(opened);
        }

        if (extracted == 0)
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidOperationException("Bu dosya bir VoiceTranscript yedeği değil.");
        }

        return extracted;
    }

    /// <summary>Name of the folder an import is unpacked into.</summary>
    private const string ImportFolder = "ice-aktarma";

    /// <summary>
    /// Adds another archive to this one, now, without a restart.
    ///
    /// The restore below answers "the laptop died" and answers it by replacing everything, which
    /// is why it has to wait for the next start: the database is open and held by the process
    /// doing the restoring. That cost is worth paying to put a lost archive back. It is the wrong
    /// price for what people actually do most of the time — carry conversations from one machine
    /// to another, or open last month's backup beside three newer weeks — because there the
    /// replacement is the damage: one of the two halves is deliberately thrown away, and the
    /// restart is a minute spent watching it happen.
    ///
    /// So this one merges into the live database through the ordinary connection. Nothing is
    /// moved aside, nothing has to be restarted, and a call that is already here is left exactly
    /// as it is. The rules are in <see cref="Repository.MergeArchive"/>; what happens here is the
    /// file half — unpacking, bringing the archive's schema up to date, and putting its
    /// recordings where this installation keeps recordings.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        string archivePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        string? password = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Yedek dosyası bulunamadı.", archivePath);

        CoreLog.Write("veri",
            $"ice aktarma baslatildi: {Path.GetFileName(archivePath)} · "
            + $"{new FileInfo(archivePath).Length / 1_048_576.0:0.0} MB · "
            + $"dosya {(NeedsPassword(archivePath) ? "sifreli" : "sifresiz")} · "
            + $"parola {(string.IsNullOrEmpty(password) ? "verilmedi" : "verildi")}");

        var opened = await OpenForReadingAsync(archivePath, password, ImportFolder + ".acilan", progress, ct);
        var staging = Path.Combine(paths.Root, ImportFolder);

        await UnpackAsync(opened, archivePath, staging, progress, ct);

        try
        {
            var incoming = Path.Combine(staging, "data", "voicetranscript.db");

            if (!File.Exists(incoming))
                throw new InvalidOperationException("Bu yedekte veritabanı yok, içe aktarılamaz.");

            progress?.Report("Yedek okunuyor…");

            // An archive written by an older build is missing tables and columns this one relies
            // on. Migrating the COPY rather than this installation's database means the merge
            // reads a shape it understands, and a backup from a newer build is left alone: its
            // stored version is already above every step, so nothing runs.
            var archive = new Database(incoming);
            archive.Migrate();
            archive.ClearPool();

            progress?.Report("Görüşmeler birleştiriliyor…");

            var merged = await Task.Run(() => repository.MergeArchive(incoming), ct);

            progress?.Report("Ses kayıtları yerine konuyor…");

            var recordings = await Task.Run(() => AdoptRecordings(merged.NewCalls, staging), ct);

            CoreLog.Write("veri", $"ice aktarma bitti: {merged.Calls} gorusme, {recordings} ses dosyasi");

            return new ImportResult(
                merged.Contacts, merged.Calls, merged.Segments, recordings, merged.AlreadyHere);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // Left for the next import to clear. Not worth failing an import that worked.
            }
        }
    }

    /// <summary>
    /// Moves the imported calls' audio into this installation's recordings folder.
    ///
    /// Renamed to the identifier the call was given HERE, because the archive's call-5 and this
    /// machine's call-5 are two different conversations and one would overwrite the other. A call
    /// whose audio is not in the archive — which is every call in an ordinary backup, since audio
    /// is off by default — is left with no audio rather than with a path to a file on somebody
    /// else's disk.
    /// </summary>
    private int AdoptRecordings(IReadOnlyList<ImportedCall> calls, string staging)
    {
        var source = Path.Combine(staging, "recordings");
        var adopted = 0;

        foreach (var call in calls)
        {
            var mic = Adopt(call.MicPath, "mic");
            var far = Adopt(call.FarPath, "far");

            // Written even when both are null, and that is the important half. The row arrived
            // carrying the other machine's path, and leaving it there would be a lie the whole
            // interface believes: a retry button that cannot work, a "show the file" that opens
            // nothing — and, once the startup repair re-roots stale paths onto this machine, a
            // conversation that could be pointed at a DIFFERENT call's audio, because the archive
            // numbers its recordings from one just as this installation does.
            repository.SetAudioPaths(call.Id, mic, far);

            if (mic is not null || far is not null) adopted++;

            continue;

            string? Adopt(string? stored, string which)
            {
                if (!Directory.Exists(source)) return null;

                // The same tail the archive recorded, looked for under what was just unpacked.
                if (Repository.RebaseRecordingPath(stored, source) is not { } found) return null;

                var directory = paths.RecordingDirectoryFor(call.StartedAt);
                Directory.CreateDirectory(directory);

                var target = Path.Combine(
                    directory, $"call-{call.Id}-{which}{Path.GetExtension(found)}");

                File.Move(found, target, overwrite: true);
                return target;
            }
        }

        return adopted;
    }

    /// <summary>
    /// A readable zip for this archive: the file itself, or a decrypted copy of it.
    ///
    /// The copy is written beside the data rather than in the temp folder for the same reason the
    /// backup was: with audio these are gigabytes, and the disk somebody chose is the one with
    /// room. The caller removes it; <see cref="UnpackAsync"/> does that however it ends.
    /// </summary>
    private async Task<string> OpenForReadingAsync(
        string archivePath,
        string? password,
        string copyName,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!NeedsPassword(archivePath)) return archivePath;

        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Bu yedek parolalı. Parola olmadan açılamaz.");

        progress?.Report("Yedek çözülüyor…");

        var opened = Path.Combine(paths.Root, copyName);

        var fault = await Task.Run(() =>
        {
            using var input = File.OpenRead(archivePath);
            using var output = File.Create(opened);

            return Export.EncryptedArchive.TryRead(input, output, password);
        }, ct);

        if (fault != Export.ArchiveFault.None)
        {
            TryDelete(opened);
            CoreLog.Write("veri", $"yedek acilamadi: {fault}");

            throw new InvalidOperationException(Export.EncryptedArchive.Explain(fault));
        }

        CoreLog.Write("veri", "parola dogru, yedek acildi");
        return opened;
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
