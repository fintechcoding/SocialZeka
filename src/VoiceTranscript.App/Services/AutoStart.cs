using System.IO;
using Microsoft.Win32;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Starting with Windows, owned by the application rather than by the installer.
///
/// It was an installer checkbox and nothing else, which fails in three ways. Somebody who
/// unchecked it had no way to change their mind afterwards. Somebody who checked it had no way to
/// stop it short of editing a startup folder. And a silent update reruns the installer with its
/// default task selection, so a deliberate "no" could be quietly overturned by an upgrade the user
/// approved for entirely unrelated reasons.
///
/// Now the setting is the intent and this reconciles the machine to it on every start. That also
/// repairs a state nobody chose — a startup entry left behind by an old install, or one removed by
/// a cleanup tool.
///
/// The registry Run key rather than a shortcut in the startup folder. A shortcut has to be created
/// through COM, cannot be inspected without parsing it, and is the thing every "speed up your PC"
/// utility deletes first. A registry value is one call to read and one to write, and its absence
/// is unambiguous.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name. Also what the user sees in Task Manager's startup list.</summary>
    private const string ValueName = Core.Configuration.AppPaths.ApplicationName;

    /// <summary>
    /// Passed to the copy Windows starts, so it opens into the tray rather than onto the screen.
    ///
    /// Without it the first thing somebody meets after every boot is a window they did not ask
    /// for, from an application whose entire point is to sit quietly until a call happens. That is
    /// how a useful default becomes the thing people turn off.
    /// </summary>
    public const string TraySwitch = "--tray";

    /// <summary>Whether the copy now running was started by Windows rather than by a person.</summary>
    public static bool LaunchedByWindows(IEnumerable<string>? commandLine) =>
        commandLine?.Any(a => string.Equals(a, TraySwitch, StringComparison.OrdinalIgnoreCase)) ?? false;

    /// <summary>Whether the entry is currently present.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);

            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes the machine match the setting. Called at startup and whenever settings are saved.
    /// </summary>
    /// <returns>What the state is afterwards, which is not always what was asked for.</returns>
    public static bool Apply(bool wanted)
    {
        // The legacy shortcut goes either way. Left in place beside a registry entry it would
        // start a second copy — harmless because of the single-instance guard, but it would also
        // mean turning the setting off did not actually stop it starting.
        RemoveLegacyShortcut();

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return IsEnabled();

            if (!wanted)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return false;
            }

            if (ExecutablePath() is not { } exe) return false;

            key.SetValue(ValueName, $"\"{exe}\" {TraySwitch}");
            return true;
        }
        catch (Exception e)
        {
            // Reported rather than thrown: an unwritable Run key — a locked-down machine, group
            // policy — must not stop the application from starting.
            AppLog.Error("otomatik başlatma", e, "Kayıt defteri girdisi yazılamadı");
            return IsEnabled();
        }
    }

    /// <summary>
    /// Where this application actually lives.
    ///
    /// From the running process rather than from the assembly, because a single-file publish
    /// reports the extracted temporary copy for the second and the real executable for the first.
    /// Writing the temporary path into a startup entry produces one that works until the next
    /// reboot clears the folder, and then silently does not.
    /// </summary>
    private static string? ExecutablePath()
    {
        var path = Environment.ProcessPath;

        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
    }

    /// <summary>Deletes the startup-folder shortcut older installers created. Best effort.</summary>
    private static void RemoveLegacyShortcut()
    {
        try
        {
            var shortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "VoiceTranscript.lnk");

            if (File.Exists(shortcut)) File.Delete(shortcut);
        }
        catch (Exception)
        {
            // A shortcut that cannot be removed is not worth a message. The registry entry is
            // the one that decides behaviour from here on.
        }
    }
}
