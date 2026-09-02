using System.Security.Cryptography;
using System.Text;

namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Hands every reader a PCM file, whatever the archive holds.
///
/// Once a recording is compressed, the player, the clip exporter, the mixer and the worker all
/// still want PCM — and rather than teach each of them Opus, the compressed file is decoded once
/// into the cache directory and the readers are pointed there. A PCM path passes straight
/// through. The cache is derived data: it can be deleted at any time and is rebuilt on demand,
/// and it is kept under a size cap so decoded copies of old calls do not quietly bring the
/// disk problem back.
/// </summary>
public static class AudioMaterialiser
{
    private static readonly object Gate = new();

    /// <summary>Set once at startup, to the application's cache directory.</summary>
    public static string? CacheDirectory { get; set; }

    /// <summary>How much decoded audio the cache may hold before the oldest is dropped.</summary>
    public static long CacheCapBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How long a freshly decoded copy is exempt from eviction.</summary>
    public static TimeSpan RecentGrace { get; set; } = TimeSpan.FromHours(3);

    /// <summary>Whether reading this path means decoding first.</summary>
    public static bool IsCompressed(string? path) => OpusArchive.IsCompressed(path);

    /// <summary>
    /// A PCM path for the recording at <paramref name="path"/>: the path itself when it already
    /// is one, otherwise a decoded copy in the cache, decoded now if it is not there yet.
    /// </summary>
    public static string? EnsurePcm(string? path)
    {
        if (path is null || !IsCompressed(path)) return path;

        var cached = CachePathFor(path);

        lock (Gate)
        {
            if (File.Exists(cached) && File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(path))
                return cached;

            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            OpusArchive.Decode(path, cached);
            Trim();
        }

        return cached;
    }

    /// <summary>Drops the decoded copy of a recording that is being forgotten.</summary>
    public static void Forget(string? path)
    {
        if (path is null || !IsCompressed(path)) return;

        try
        {
            var cached = CachePathFor(path);
            if (File.Exists(cached)) File.Delete(cached);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Where the decoded copy lives. Named by a hash of the full path plus the file name, so two
    /// months' call-7 never collide and the name still says which recording it is.
    /// </summary>
    public static string CachePathFor(string path)
    {
        var directory = CacheDirectory ?? Path.Combine(Path.GetTempPath(), "VoiceTranscript");
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12].ToLowerInvariant();

        return Path.Combine(directory, "audio", $"{hash}-{Path.GetFileNameWithoutExtension(path)}.wav");
    }

    /// <summary>Oldest decoded copies go first once the cache is over its cap.</summary>
    private static void Trim()
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePathFor("x.ogg"));
            if (directory is null || !Directory.Exists(directory)) return;

            var files = new DirectoryInfo(directory).GetFiles("*.wav").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);

            // A decoded copy younger than this is assumed to be in use. The worker reads the far
            // stream only when it is done with the mic stream, which on a processor can be an
            // hour later, and it holds no handle in between — so "not open" is not "not needed".
            var keepSince = DateTime.UtcNow - RecentGrace;

            // Workspaces a failed cloud attempt left behind go with their audio once they are old.
            foreach (var stale in new DirectoryInfo(directory).GetDirectories("*.cloudparts"))
            {
                if (stale.LastWriteTimeUtc < DateTime.UtcNow - TimeSpan.FromDays(7))
                    try { stale.Delete(recursive: true); } catch (IOException) { }
            }

            // Half-written files from a compression or a mix that was interrupted.
            //
            // Each is written under a unique name and renamed the moment it is complete, so one
            // that still has the name is one that never finished — an application closed mid-job,
            // or a machine that slept. Nothing reads them and nothing ever will; they were simply
            // never swept, and a real archive had a 1.8 MB one from a fortnight earlier sitting
            // beside the recording it failed to become. A day is far longer than the seconds one
            // of these legitimately exists for.
            foreach (var abandoned in new DirectoryInfo(directory).GetFiles("*.partial"))
            {
                if (abandoned.LastWriteTimeUtc < DateTime.UtcNow - TimeSpan.FromDays(1))
                    try { abandoned.Delete(); } catch (IOException) { }
            }

            foreach (var file in files)
            {
                if (total <= CacheCapBytes) break;
                if (file.LastWriteTimeUtc >= keepSince) continue;

                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch (IOException)
                {
                    // Open in the player. Skipped; the next pass gets it.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
