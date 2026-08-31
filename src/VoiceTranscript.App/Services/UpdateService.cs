using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Update;

namespace VoiceTranscript.App.Services;

/// <summary>What a check found.</summary>
/// <param name="Available">A newer release exists and passed every check.</param>
/// <param name="Release">The release, when there is one worth offering.</param>
/// <param name="Message">Why nothing is being offered, when nothing is. Null on success.</param>
public sealed record UpdateCheck(bool Available, Release? Release, string? Message);

/// <summary>
/// Finds out whether a newer version has been published, and installs it when told to.
///
/// The shape of this is set by one decision the user made explicitly: <b>it checks and asks; it
/// never installs on its own.</b> Nothing here runs an installer without somebody having read what
/// changed and pressed a button.
///
/// Three things it will not do, each for a reason that has already cost this project or would:
///
///   <b>It never lets a failed check surface.</b> An update check runs at startup, on a machine
///   that may have no network at all, for an application whose actual job is recording calls. A
///   check that could stop or delay startup would trade the thing that matters for the thing that
///   does not.
///
///   <b>It verifies what it downloaded before running it.</b> Not because a checksum defends
///   against a compromised repository — it does not, and the documentation says so — but because
///   a truncated download that installs anyway is far more likely than an attacker, and afterwards
///   the two are indistinguishable from "the update broke it".
///
///   <b>It quietens the recorder before handing over.</b> The installer waits for the application
///   to exit and never closes it; if the application simply started the installer and carried on,
///   the two would sit waiting for each other. So the order is: stop detecting, finish anything in
///   flight, write the attempt marker, start the installer, then exit.
/// </summary>
public sealed class UpdateService(HttpClient http, AppPaths paths)
{
    /// <summary>Where releases are published.</summary>
    public const string ReleasesApi =
        "https://api.github.com/repos/fintechcoding/VoiceTranscript/releases/latest";

    /// <summary>The releases page, for when somebody would rather do it by hand.</summary>
    public const string ReleasesPage = "https://github.com/fintechcoding/VoiceTranscript/releases";

    /// <summary>
    /// Asks GitHub what the newest release is.
    ///
    /// Every failure becomes a message rather than an exception. This is called on a background
    /// path where nothing is waiting for it, and an update check has no business being the reason
    /// anything else goes wrong.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var running = AppVersion.OfRunningApplication();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);

            // GitHub refuses requests without one, and naming the application makes this
            // identifiable in their logs as something other than an anonymous scraper.
            request.Headers.UserAgent.ParseAdd($"VoiceTranscript/{running}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            // Its own deadline. The shared client is set to ten minutes for uploading audio, and
            // an update check inheriting that would leave a task hanging for the rest of the day
            // against an endpoint that is simply unreachable.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await http.SendAsync(request, deadline.Token);

            if (!response.IsSuccessStatusCode)
                return new UpdateCheck(false, null, $"Güncelleme sunucusu {(int)response.StatusCode} döndürdü.");

            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            var (release, rejection) = ReleaseAssets.Read(body);

            if (release is null)
                return new UpdateCheck(false, null, ReleaseAssets.Explain(rejection));

            if (release.Version <= running)
                return new UpdateCheck(false, null, $"En güncel sürümü kullanıyorsun ({running}).");

            return new UpdateCheck(true, release, null);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return new UpdateCheck(false, null, "Güncelleme denetlenemedi: internete ulaşılamadı.");
        }
        catch (Exception e)
        {
            AppLog.Error("güncelleme", e, "denetim başarısız");
            return new UpdateCheck(false, null, "Güncelleme denetlenemedi.");
        }
    }

    /// <summary>Where a downloaded installer is kept until it has been run.</summary>
    private string DownloadDirectory => Path.Combine(paths.Root, "updates");

    /// <summary>
    /// Downloads the installer and checks it against the published checksum.
    /// </summary>
    /// <returns>The path to the verified installer, or null with a reason.</returns>
    public async Task<(string? Path, string? Failure)> DownloadAsync(
        Release release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DownloadDirectory);

        var target = Path.Combine(DownloadDirectory, release.InstallerName);
        var partial = target + ".partial";

        try
        {
            var expected = await FetchChecksumAsync(release, cancellationToken);

            if (expected is null)
                return (null, $"Yayındaki {ReleaseAssets.ChecksumFileName} okunamadı; kurulum doğrulanamaz.");

            // Written to a .partial and renamed only once it is complete and verified.
            //
            // An interrupted download that left a short file under the real name would be found by
            // the next attempt, look finished, fail its checksum, and have to be explained. Worse,
            // a resumed session could run it.
            await DownloadToAsync(release.InstallerUrl, partial, release.SizeBytes, progress, cancellationToken);

            var actual = await Sha256Async(partial, cancellationToken);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                Delete(partial);

                return (null,
                    "İndirilen dosya doğrulanamadı. İndirme yarıda kalmış olabilir; tekrar denenebilir.");
            }

            Delete(target);
            File.Move(partial, target);

            AppLog.Write("güncelleme", $"{release.Version} indirildi ve doğrulandı");

            return (target, null);
        }
        catch (OperationCanceledException)
        {
            Delete(partial);
            return (null, "İndirme iptal edildi.");
        }
        catch (Exception e)
        {
            Delete(partial);
            AppLog.Error("güncelleme", e, "indirme başarısız");

            return (null, $"İndirilemedi: {e.Message}");
        }
    }

    private async Task<string?> FetchChecksumAsync(Release release, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.ChecksumUrl);
        request.Headers.UserAgent.ParseAdd("VoiceTranscript");

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var listing = await response.Content.ReadAsStringAsync(cancellationToken);

        return ReleaseAssets.ChecksumFor(listing, release.InstallerName);
    }

    private async Task DownloadToAsync(
        string url, string path, long expectedBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("VoiceTranscript");

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedBytes;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            written += read;
            if (total > 0) progress?.Report(Math.Min(1.0, written / (double)total));
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken);

        return Convert.ToHexStringLower(hash);
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind rather than made into a failure of its own.
        }
    }

    /// <summary>
    /// Records that an update was started, so a silent failure can be noticed afterwards.
    ///
    /// The application is dead while the installer runs, so there is no other way to tell an
    /// installer that did nothing from one that worked. On the next start, a marker whose version
    /// does not match the version now running means the update did not take — and the user is told
    /// rather than being left believing they are current.
    /// </summary>
    public void RecordAttempt(AppVersion from, AppVersion to)
    {
        try
        {
            Directory.CreateDirectory(DownloadDirectory);

            File.WriteAllText(
                Path.Combine(DownloadDirectory, "attempt.txt"),
                $"{DateTimeOffset.Now:O}\t{from}\t{to}");
        }
        catch (IOException e)
        {
            AppLog.Error("güncelleme", e, "deneme işareti yazılamadı");
        }
    }

    /// <summary>
    /// What became of the last attempt, once. Null when there was none or it succeeded.
    /// </summary>
    public string? TakeFailedAttempt()
    {
        var marker = Path.Combine(DownloadDirectory, "attempt.txt");

        try
        {
            if (!File.Exists(marker)) return null;

            var parts = File.ReadAllText(marker).Split('\t');
            Delete(marker);

            if (parts.Length < 3) return null;

            var intended = AppVersion.Parse(parts[2]);
            var running = AppVersion.OfRunningApplication();

            if (running >= intended) return null;

            return $"{intended} sürümüne güncelleme tamamlanmadı; hâlâ {running} kullanılıyor.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes installers left from earlier updates.
    ///
    /// Each is around seventy megabytes and nothing else ever deletes them, so on a machine that
    /// has updated a few times they quietly add up inside the data directory — the same directory
    /// the user is told holds their recordings.
    /// </summary>
    public void CleanUp()
    {
        try
        {
            if (!Directory.Exists(DownloadDirectory)) return;

            foreach (var file in Directory.EnumerateFiles(DownloadDirectory, "*.exe")) Delete(file);
            foreach (var file in Directory.EnumerateFiles(DownloadDirectory, "*.partial")) Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Housekeeping. Never worth reporting.
        }
    }
}
