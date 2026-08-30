namespace VoiceTranscript.Core.Configuration;

/// <summary>
/// Where everything lives on disk.
///
/// Data is deliberately kept outside the application directory. The installer replaces its own
/// folder on every update, so a database or a few gigabytes of model weights stored there would
/// be re-downloaded each time at best, and deleted at worst.
///
/// The recordings directory is also checked against cloud-sync folders. Putting call audio
/// inside OneDrive would quietly upload every conversation to a third party, which is the exact
/// opposite of what this application is for, and it would happen without a single visible error.
/// </summary>
public sealed class AppPaths
{
    public const string ApplicationName = "VoiceTranscript";

    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            $"{ApplicationName}.Data");

        Recordings = Path.Combine(Root, "recordings");
        Models = Path.Combine(Root, "models");
        Logs = Path.Combine(Root, "logs");
        DatabaseFile = Path.Combine(Root, "voicetranscript.db");
        SettingsFile = Path.Combine(Root, "settings.json");
    }

    public string Root { get; }
    public string Recordings { get; }
    public string Models { get; }
    public string Logs { get; }
    public string DatabaseFile { get; }
    public string SettingsFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Recordings);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Logs);
    }

    /// <summary>Directory for one call's audio, grouped by month so folders stay browsable.</summary>
    public string RecordingDirectoryFor(DateTimeOffset startedAt) =>
        Path.Combine(Recordings, startedAt.ToLocalTime().ToString("yyyy-MM"));

    /// <summary>
    /// Names of cloud-sync roots detected under a path.
    ///
    /// Empty means the location is local. Anything else means recordings placed there would be
    /// uploaded, so the application refuses rather than warning: a warning gets dismissed once
    /// and then every future call is copied to somebody else's server.
    /// </summary>
    public static IReadOnlyList<string> DetectCloudSync(string path)
    {
        var full = Path.GetFullPath(path);
        List<string> found = [];

        foreach (var (variable, label) in new[]
                 {
                     ("OneDrive", "OneDrive"),
                     ("OneDriveConsumer", "OneDrive"),
                     ("OneDriveCommercial", "OneDrive for Business"),
                 })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root) && IsUnder(full, root) && !found.Contains(label))
                found.Add(label);
        }

        // Clients that do not export an environment variable are matched by their conventional
        // folder name instead.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (folder, label) in new[]
                 {
                     ("Dropbox", "Dropbox"),
                     ("Google Drive", "Google Drive"),
                     ("My Drive", "Google Drive"),
                     ("iCloudDrive", "iCloud Drive"),
                     ("Yandex.Disk", "Yandex.Disk"),
                 })
        {
            var candidate = Path.Combine(userProfile, folder);
            if (Directory.Exists(candidate) && IsUnder(full, candidate) && !found.Contains(label))
                found.Add(label);
        }

        return found;
    }

    private static bool IsUnder(string path, string root)
    {
        var normalisedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalisedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        return normalisedPath.Equals(normalisedRoot, StringComparison.OrdinalIgnoreCase)
               || normalisedPath.StartsWith(normalisedRoot + Path.DirectorySeparatorChar,
                                            StringComparison.OrdinalIgnoreCase);
    }
}
