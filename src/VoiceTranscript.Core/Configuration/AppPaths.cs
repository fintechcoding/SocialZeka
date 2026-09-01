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
        Photos = Path.Combine(Root, "photos");
        Cache = Path.Combine(Root, "cache");
        DatabaseFile = Path.Combine(Root, "voicetranscript.db");
        SettingsFile = Path.Combine(Root, "settings.json");
    }

    public string Root { get; }
    public string Recordings { get; }
    public string Models { get; }
    public string Logs { get; }

    /// <summary>Contact photos, copied in and shrunk — never references to files elsewhere.</summary>
    public string Photos { get; }

    /// <summary>Derived files that can be rebuilt from the archive at any time — decoded audio, mostly.</summary>
    public string Cache { get; }
    public string DatabaseFile { get; }
    public string SettingsFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Recordings);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Photos);
        Directory.CreateDirectory(Cache);
    }

    /// <summary>Directory for one call's audio, grouped by month so folders stay browsable.</summary>
    public string RecordingDirectoryFor(DateTimeOffset startedAt) =>
        Path.Combine(Recordings, startedAt.ToLocalTime().ToString("yyyy-MM"));

    /// <summary>The command-line switch that redirects the data directory.</summary>
    public const string DataSwitch = "--data";

    /// <summary>
    /// Works out which data directory to use: the command line wins, then the stored setting,
    /// then the default beside the user's other local application data.
    ///
    /// <b>Why a command-line switch exists at all.</b> This application is developed on one
    /// machine and used on another, and the faults that matter — capture, CUDA, real calls, and
    /// reading a contact's name off a real call window — only happen on the machine somebody is
    /// actually having conversations on. Which means development builds have to be run there,
    /// beside an archive of real recordings. An experimental build writing into that archive is
    /// how a month of conversations gets lost to a half-finished migration, and no amount of care
    /// while editing prevents it. A switch does: <c>--data C:\vt-dev</c> and the two never touch.
    ///
    /// <b>Why the setting alone could not do it.</b> <see cref="AppSettings.DataRoot"/> is stored
    /// in settings.json, which lives inside the data directory — so reading the setting requires
    /// already knowing where the directory is. The circle is broken by reading settings from the
    /// default location first and honouring <c>DataRoot</c> from there, which works for somebody
    /// relocating their archive but is useless for the development case, because a development
    /// build must not be forced to modify the real installation's settings file to stay away
    /// from it.
    ///
    /// Both are supported and the switch takes precedence, so a development run is a matter of
    /// one argument and leaves nothing behind.
    /// </summary>
    /// <param name="commandLine">Arguments as given, without the executable name.</param>
    /// <param name="settingRoot">The stored <c>DataRoot</c>, if any.</param>
    /// <param name="defaultRoot">Where the data lives when nothing overrides it.</param>
    public static string ResolveRoot(
        IEnumerable<string>? commandLine, string? settingRoot, string defaultRoot)
    {
        if (DataDirectoryFrom(commandLine) is { } chosen) return Path.GetFullPath(chosen);

        return string.IsNullOrWhiteSpace(settingRoot)
            ? defaultRoot
            : Path.GetFullPath(settingRoot);
    }

    /// <summary>
    /// Reads <c>--data &lt;path&gt;</c> or <c>--data=&lt;path&gt;</c>, whichever form was used.
    ///
    /// A switch with no value after it is ignored rather than treated as an empty path. Falling
    /// back silently to the real archive is the one outcome this whole mechanism exists to
    /// prevent, so the caller is expected to notice and refuse to start — see the startup code.
    /// </summary>
    public static string? DataDirectoryFrom(IEnumerable<string>? commandLine)
    {
        if (commandLine is null) return null;

        var arguments = commandLine.ToList();

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (argument.StartsWith(DataSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                var inline = argument[(DataSwitch.Length + 1)..].Trim('"');
                return string.IsNullOrWhiteSpace(inline) ? null : inline;
            }

            if (!argument.Equals(DataSwitch, StringComparison.OrdinalIgnoreCase)) continue;

            if (i + 1 >= arguments.Count) return null;

            var next = arguments[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(next) ? null : next;
        }

        return null;
    }

    /// <summary>
    /// Whether the command line asked for a data directory, regardless of whether it was usable.
    ///
    /// Kept separate from <see cref="ResolveRoot"/> so that "asked for one and it was malformed"
    /// can be told apart from "did not ask". The first has to stop startup; treating it as the
    /// second would run a development build against the real recordings, which is precisely the
    /// accident being guarded against.
    /// </summary>
    public static bool AsksForDataDirectory(IEnumerable<string>? commandLine) =>
        commandLine is not null
        && commandLine.Any(a => a.Equals(DataSwitch, StringComparison.OrdinalIgnoreCase)
                                || a.StartsWith(DataSwitch + "=", StringComparison.OrdinalIgnoreCase));

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
