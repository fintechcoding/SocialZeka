using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// Starting with Windows.
///
/// Only the decisions are covered here, not the registry write: a test that turned autostart on
/// and off for real would be reaching into the machine running it, and a crash between the two
/// halves would leave a developer's own logon changed.
/// </summary>
public sealed class AutoStartTests
{
    /// <summary>
    /// On by default, and the default is the point. A recorder that has to be remembered and
    /// launched before every conversation records nothing, because the calls worth having a
    /// record of are the ones nobody saw coming.
    /// </summary>
    [Fact]
    public void StartingWithWindowsIsOnUnlessTurnedOff()
    {
        Assert.True(new AppSettings().StartWithWindows);
        Assert.False((new AppSettings { StartWithWindows = false }).StartWithWindows);
    }

    /// <summary>
    /// A settings file written before this setting existed has no value for it, and JSON
    /// deserialisation then leaves the field at its default — which is what is wanted. Somebody
    /// already relying on the installer's startup shortcut keeps starting with Windows rather
    /// than silently stopping.
    /// </summary>
    [Fact]
    public void SettingsWrittenBeforeTheSettingExistedStillStartWithWindows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vt-autostart-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{"RecordWhatsApp":true,"ShowRecordingBar":false}""");

            var loaded = AppSettings.Load(path);

            Assert.True(loaded.StartWithWindows);
            Assert.False(loaded.ShowRecordingBar);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The copy Windows starts opens into the tray. Without the switch the first thing somebody
    /// meets after every boot is a window they did not ask for, which is how a sensible default
    /// becomes the thing they turn off.
    /// </summary>
    [Fact]
    public void TheTraySwitchIsRecognisedWhereverItAppears()
    {
        Assert.True(AutoStart.LaunchedByWindows([AutoStart.TraySwitch]));
        Assert.True(AutoStart.LaunchedByWindows(["--data", "D:\\vt", AutoStart.TraySwitch]));

        // Case-insensitively, because the value in the Run key is written once and then edited by
        // hand for the rest of its life.
        Assert.True(AutoStart.LaunchedByWindows(["--TRAY"]));
    }

    [Fact]
    public void AManualLaunchOpensTheWindow()
    {
        Assert.False(AutoStart.LaunchedByWindows([]));
        Assert.False(AutoStart.LaunchedByWindows(null));
        Assert.False(AutoStart.LaunchedByWindows(["--setup"]));

        // Not a prefix match: "--traybar" would be a different switch, and treating it as this one
        // would hide the window for a reason nobody could find.
        Assert.False(AutoStart.LaunchedByWindows(["--traybar"]));
    }

    /// <summary>Reading the current state must never throw, whatever the registry contains.</summary>
    [Fact]
    public void AskingWhetherItIsEnabledIsAlwaysSafe()
    {
        var problem = Record.Exception(() => AutoStart.IsEnabled());

        Assert.Null(problem);
    }
}
