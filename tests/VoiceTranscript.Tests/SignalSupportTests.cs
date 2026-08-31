using VoiceTranscript.Capture;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Signal Desktop as a third recorded application.
///
/// Adding a messenger touches four separate places — the stored enum, the process names, the
/// setting that decides whether to record it, and the list of window titles that are the
/// application talking about itself rather than a person. Miss any one and the failure is silent:
/// calls attributed to nobody, a switch that does nothing, or a contact called "Signal".
/// </summary>
public sealed class SignalSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-signal-{Guid.NewGuid():N}");

    public SignalSupportTests() => Directory.CreateDirectory(Path.Combine(_root, "recordings"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The enum value is written into every row. Renumbering it would silently re-attribute every
    /// call already recorded, so it is pinned here rather than left to a future tidy-up.
    /// </summary>
    [Fact]
    public void SignalKeepsItsStoredNumber()
    {
        Assert.Equal(3, (int)CallApp.Signal);

        // And the ones it was appended after.
        Assert.Equal(1, (int)CallApp.WhatsApp);
        Assert.Equal(2, (int)CallApp.Telegram);
    }

    /// <summary>
    /// Signal's own window titles are not people. Without this a call lands under a contact named
    /// "Signal", and every later call joins them — one row accumulating unrelated conversations
    /// with different people, which is the worst possible attribution failure because it looks
    /// like it worked.
    /// </summary>
    [Theory]
    [InlineData("Signal")]
    [InlineData("Signal Desktop")]
    [InlineData("Signal Beta")]
    public void SignalsOwnWindowTitlesAreNeverTakenForAContact(string title)
    {
        Assert.True(CallWindows.IsShellTitle(CallApp.Signal, title));
    }

    [Fact]
    public void ARealNameInASignalWindowIsStillAName()
    {
        Assert.False(CallWindows.IsShellTitle(CallApp.Signal, "Serdal"));
        Assert.False(CallWindows.IsShellTitle(CallApp.Signal, "Uliana"));
    }

    /// <summary>
    /// Recorded by default, like the other two — and the reason is the same one that governs the
    /// whole product: somebody who asked for their calls to be recorded should not discover months
    /// later that one messenger was quietly skipped.
    /// </summary>
    [Fact]
    public void SignalIsRecordedUnlessTurnedOff()
    {
        Assert.True(new AppSettings().RecordSignal);
    }

    /// <summary>
    /// A settings file written before Signal existed has no value for it, and deserialisation
    /// leaves the field at its default. That is what is wanted: the switch appears already on
    /// rather than off, so an upgrade adds a messenger instead of quietly disabling one.
    /// </summary>
    [Fact]
    public void SettingsWrittenBeforeSignalExistedStillRecordIt()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"RecordWhatsApp":true,"RecordTelegram":false}""");

        var loaded = AppSettings.Load(path);

        Assert.True(loaded.RecordSignal);
        Assert.False(loaded.RecordTelegram);
    }

    /// <summary>
    /// Turning all three off records nothing at all, and the settings screen has to say so rather
    /// than accepting a configuration that silently does no work.
    /// </summary>
    [Fact]
    public void TurningEveryMessengerOffIsRefused()
    {
        var settings = new AppSettings
        {
            RecordWhatsApp = false,
            RecordTelegram = false,
            RecordSignal = false,
        };

        Assert.Contains(
            settings.Validate(new AppPaths(_root)),
            problem => problem.Contains("En az bir uygulama", StringComparison.Ordinal));
    }

    /// <summary>Switching Signal off alone is a legitimate configuration.</summary>
    [Fact]
    public void RecordingOnlyTheOtherTwoIsAllowed()
    {
        var settings = new AppSettings { RecordSignal = false };

        Assert.DoesNotContain(
            settings.Validate(new AppPaths(_root)),
            problem => problem.Contains("En az bir uygulama", StringComparison.Ordinal));
    }
}
