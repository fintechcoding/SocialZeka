using System.Net.Http;
using System.Reflection;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// Choosing where the data directory is.
///
/// This exists because development moved onto the machine the application is actually used on.
/// That is the only place a real call, a real capture device or a real call window exists, so it
/// is the only place most faults can be reproduced — and it is also the machine holding an archive
/// of real conversations. A development build writing into that archive is how a month of
/// recordings gets lost to a half-finished experiment, and being careful is not a mechanism.
/// </summary>
public class DataDirectoryTests
{
    private static readonly string Default = new AppPaths().Root;

    [Fact]
    public void NothingGivenMeansTheUsualPlace()
    {
        Assert.Equal(Default, AppPaths.ResolveRoot([], null, Default));
        Assert.Equal(Default, AppPaths.ResolveRoot(null, null, Default));
    }

    [Theory]
    [InlineData("--data")]
    [InlineData("--DATA")]
    public void TheSwitchRedirectsTheDataDirectory(string flag)
    {
        var resolved = AppPaths.ResolveRoot([flag, @"C:\vt-dev"], null, Default);

        Assert.Equal(Path.GetFullPath(@"C:\vt-dev"), resolved);
        Assert.NotEqual(Default, resolved);
    }

    [Fact]
    public void TheSwitchAlsoAcceptsAnEqualsSign()
    {
        Assert.Equal(
            Path.GetFullPath(@"C:\vt-dev"),
            AppPaths.ResolveRoot([@"--data=C:\vt-dev"], null, Default));
    }

    /// <summary>Quoted paths survive the shell, and a quoted path with a space is normal on Windows.</summary>
    [Fact]
    public void QuotesAreStripped()
    {
        Assert.Equal(
            Path.GetFullPath(@"C:\vt dev"),
            AppPaths.ResolveRoot(["--data", @"""C:\vt dev"""], null, Default));
    }

    /// <summary>The switch is not necessarily the first argument; --setup is already in use.</summary>
    [Fact]
    public void TheSwitchIsFoundAmongOtherArguments()
    {
        Assert.Equal(
            Path.GetFullPath(@"C:\vt-dev"),
            AppPaths.ResolveRoot(["--setup", "--data", @"C:\vt-dev"], null, Default));
    }

    [Fact]
    public void TheStoredSettingIsUsedWhenTheCommandLineSaysNothing()
    {
        Assert.Equal(
            Path.GetFullPath(@"D:\arsiv"),
            AppPaths.ResolveRoot([], @"D:\arsiv", Default));
    }

    /// <summary>
    /// The switch beats the setting, and that ordering is the entire point.
    ///
    /// A development build has to be able to stay away from the real archive without editing the
    /// real installation's settings file — touching it to avoid touching it would be absurd.
    /// </summary>
    [Fact]
    public void TheCommandLineBeatsTheStoredSetting()
    {
        Assert.Equal(
            Path.GetFullPath(@"C:\vt-dev"),
            AppPaths.ResolveRoot(["--data", @"C:\vt-dev"], @"D:\arsiv", Default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyStoredSettingIsNoSetting(string stored)
    {
        Assert.Equal(Default, AppPaths.ResolveRoot([], stored, Default));
    }

    /// <summary>
    /// A switch with nothing usable after it must be detectable as a mistake.
    ///
    /// Falling back to the default here would run a development build against the real recordings
    /// — the one outcome the switch exists to prevent — so startup refuses instead. That is only
    /// possible if "asked and got it wrong" can be told apart from "did not ask".
    /// </summary>
    [Fact]
    public void AMalformedSwitchIsRecognisedRatherThanIgnored()
    {
        string[][] malformed =
        [
            ["--data"],             // nothing after it at all
            ["--data", "   "],      // whitespace where a path should be
            ["--data="],            // the inline form with an empty value
            ["--setup", "--data"],  // swallowed by the end of the command line
        ];

        foreach (var arguments in malformed)
        {
            Assert.Null(AppPaths.DataDirectoryFrom(arguments));
            Assert.Equal(Default, AppPaths.ResolveRoot(arguments, null, Default));

            // The pair is the point: no usable path, but the user plainly asked for one. Startup
            // uses exactly this to refuse rather than fall back onto the real recordings.
            Assert.True(AppPaths.AsksForDataDirectory(arguments));
        }
    }

    [Fact]
    public void NotAskingIsNotAMistake()
    {
        Assert.False(AppPaths.AsksForDataDirectory([]));
        Assert.False(AppPaths.AsksForDataDirectory(["--setup"]));
        Assert.False(AppPaths.AsksForDataDirectory(null));
    }
}

/// <summary>
/// Saving settings must not quietly discard the ones the window does not show.
///
/// <c>ToSettings</c> used to build a brand new record from the fields on screen, so every setting
/// absent from that screen reset to its default. Three were carried across by hand at the call
/// site and two were not: a transcript retention period was wiped on every save, and the data
/// directory would have been the moment it started doing anything — which would have made a
/// relocated archive invisible the first time somebody opened settings and pressed save.
///
/// The fix was to amend the record the window opened on rather than construct a fresh one, so
/// this is not a test of five particular fields. It is a test of the shape: any setting this
/// screen does not edit has to come back untouched, including ones added later by somebody who
/// never reads this file.
/// </summary>
public class SettingsSurviveSavingTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), $"vt-settings-{Guid.NewGuid():N}");

    /// <summary>
    /// Everything the settings window is entitled to change.
    ///
    /// Anything not on this list must round-trip unchanged. A new setting is therefore protected
    /// by default; one that genuinely belongs on this screen fails the test until it is added
    /// here deliberately, which is the moment to ask whether it round-trips correctly.
    /// </summary>
    private static readonly HashSet<string> EditedByTheWindow =
    [
        nameof(AppSettings.RecordWhatsApp), nameof(AppSettings.RecordTelegram),
        nameof(AppSettings.RecordAutomatically), nameof(AppSettings.ShowRecordingBar),
        nameof(AppSettings.UiLanguage), nameof(AppSettings.UseEchoCancellation),
        nameof(AppSettings.MicrophoneDeviceId), nameof(AppSettings.OutputDeviceId),
        nameof(AppSettings.PreferProcessLoopback), nameof(AppSettings.GpuCooldownSeconds),
        nameof(AppSettings.AsrModelId), nameof(AppSettings.AsrDevice), nameof(AppSettings.AsrMode),
        nameof(AppSettings.CloudAsrModelId), nameof(AppSettings.AsrApiKey),
        nameof(AppSettings.AsrApiBaseUrl), nameof(AppSettings.AnalyseAutomatically),
        nameof(AppSettings.LlmProvider), nameof(AppSettings.LlmModelId),
        nameof(AppSettings.LlmRemoteModel), nameof(AppSettings.LlmBaseUrl),
        nameof(AppSettings.LlmApiKey), nameof(AppSettings.ExportToObsidian),
        nameof(AppSettings.ObsidianVaultPath), nameof(AppSettings.ExportToNotion),
        nameof(AppSettings.NotionApiKey), nameof(AppSettings.NotionDatabaseId),
        nameof(AppSettings.AudioRetentionDays), nameof(AppSettings.SttEndpoints),
        nameof(AppSettings.RecordSignal), nameof(AppSettings.StartWithWindows),
        nameof(AppSettings.IdentifySpeakers), nameof(AppSettings.PreferredName),
        nameof(AppSettings.Language), nameof(AppSettings.TranscribeGroupCalls),
        nameof(AppSettings.LogDetail),
        nameof(AppSettings.HabitCountingEnabled), nameof(AppSettings.IntentCardEnabled),
        nameof(AppSettings.LiveTalkMeterEnabled), nameof(AppSettings.ProsodyMeasurementEnabled),

        // The contact card's opinion panel. The second of the pair is not a switch anybody
        // touches: the panel writes it when it turns itself off after measuring badly, and the
        // window carries it back out unchanged. It is on this list because it passes through
        // ToSettings deliberately rather than by accident.
        nameof(AppSettings.ContactReadingEnabled), nameof(AppSettings.ContactReadingMeasuredNegative),
    ];

    private static AppSettings RoundTrip(AppSettings original)
    {
        using var http = new HttpClient();
        return new SettingsViewModel(original, new AppPaths(Root), http).ToSettings();
    }

    /// <summary>The two that were actually being lost, named so a regression says which.</summary>
    [Fact]
    public void TheDataDirectoryAndRetentionPeriodAreNotWipedBySaving()
    {
        var saved = RoundTrip(new AppSettings
        {
            DataRoot = @"D:\arsiv",
            AudioRetentionDays = 180,
        });

        Assert.Equal(@"D:\arsiv", saved.DataRoot);
        Assert.Equal(180, saved.AudioRetentionDays);
    }

    /// <summary>
    /// Resetting the first-run stamp would reopen the setup wizard every time settings were saved.
    /// This was already handled by hand at the call site; it is pinned here so the hand-written
    /// rescue can be deleted safely.
    /// </summary>
    [Fact]
    public void TheFirstRunStampAndTranscriptionLanguageSurvive()
    {
        var stamped = DateTimeOffset.UtcNow.AddDays(-3);

        var saved = RoundTrip(new AppSettings
        {
            SetupCompletedAt = stamped,
            Language = "en",
            TranscribeGroupCalls = true,
        });

        Assert.Equal(stamped, saved.SetupCompletedAt);
        Assert.Equal("en", saved.Language);
        Assert.True(saved.TranscribeGroupCalls);
    }

    /// <summary>
    /// The guard that makes the two tests above unnecessary to write again.
    ///
    /// Walks every property on <see cref="AppSettings"/> and insists that the ones this screen
    /// does not edit come back exactly as they went in. Reverting to constructing a fresh record
    /// fails this immediately, and so does adding a setting to the screen without saying so.
    /// </summary>
    [Fact]
    public void EverySettingTheWindowDoesNotEditComesBackUnchanged()
    {
        var original = new AppSettings
        {
            DataRoot = @"D:\arsiv",
            SetupCompletedAt = DateTimeOffset.UtcNow.AddDays(-3),
            Language = "en",
            TranscribeGroupCalls = true,
        };

        var saved = RoundTrip(original);

        var lost = new List<string>();

        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Computed properties have no stored value to lose.
            if (property.SetMethod is null) continue;
            if (EditedByTheWindow.Contains(property.Name)) continue;

            var before = property.GetValue(original);
            var after = property.GetValue(saved);

            if (!Equals(before, after)) lost.Add($"{property.Name}: {before ?? "null"} → {after ?? "null"}");
        }

        Assert.True(lost.Count == 0,
            "Ayarlar ekranı bu alanları göstermiyor ama kaydedince değiştirdi:"
            + Environment.NewLine + string.Join(Environment.NewLine, lost));
    }
}
