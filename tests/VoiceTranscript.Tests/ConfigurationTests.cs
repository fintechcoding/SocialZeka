using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-paths-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CreatesEverySubdirectoryItWillWriteTo()
    {
        var paths = new AppPaths(_root);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.Recordings));
        Assert.True(Directory.Exists(paths.Models));
        Assert.True(Directory.Exists(paths.Logs));
    }

    /// <summary>
    /// Data lives outside the application folder because the installer replaces that folder on
    /// every update — gigabytes of weights would be re-downloaded, and the database lost.
    /// </summary>
    [Fact]
    public void DefaultsToALocationTheInstallerDoesNotReplace()
    {
        var paths = new AppPaths();

        Assert.Contains("VoiceTranscript.Data", paths.Root);
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            paths.Root);
    }

    [Fact]
    public void RecordingsAreGroupedByMonth()
    {
        var paths = new AppPaths(_root);
        var directory = paths.RecordingDirectoryFor(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.EndsWith(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM"),
            directory);
    }

    /// <summary>
    /// Recordings inside a sync folder would be uploaded silently. Detecting that is worth more
    /// than any other privacy feature in the application, because it is the one failure mode
    /// that produces no visible symptom.
    /// </summary>
    [Fact]
    public void DetectsAPathInsideOneDrive()
    {
        var oneDrive = Path.Combine(_root, "OneDrive");
        Directory.CreateDirectory(oneDrive);

        var previous = Environment.GetEnvironmentVariable("OneDrive");
        Environment.SetEnvironmentVariable("OneDrive", oneDrive);

        try
        {
            var detected = AppPaths.DetectCloudSync(Path.Combine(oneDrive, "VoiceTranscript", "recordings"));
            Assert.Contains("OneDrive", detected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDrive", previous);
        }
    }

    [Fact]
    public void ALocalPathIsNotFlagged()
    {
        Directory.CreateDirectory(_root);
        Assert.Empty(AppPaths.DetectCloudSync(_root));
    }

    /// <summary>A folder merely named similarly must not trip the check.</summary>
    [Fact]
    public void SiblingFoldersWithASharedPrefixAreNotConfused()
    {
        var oneDrive = Path.Combine(_root, "OneDrive");
        var lookalike = Path.Combine(_root, "OneDriveBackupNotes");
        Directory.CreateDirectory(oneDrive);
        Directory.CreateDirectory(lookalike);

        var previous = Environment.GetEnvironmentVariable("OneDrive");
        Environment.SetEnvironmentVariable("OneDrive", oneDrive);

        try
        {
            Assert.Empty(AppPaths.DetectCloudSync(lookalike));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDrive", previous);
        }
    }
}

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-set-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppPaths Paths()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        return paths;
    }

    /// <summary>
    /// The defaults are the product's position: everything local, nothing shared, group calls
    /// archived as audio rather than guessed at.
    /// </summary>
    [Fact]
    public void DefaultsKeepEverythingOnTheMachine()
    {
        var settings = new AppSettings();

        Assert.Equal(LlmProviderKind.LlamaServer, settings.LlmProvider);
        Assert.False(settings.AnythingLeavesTheMachine);
        Assert.False(settings.ExportToNotion);
        Assert.False(settings.TranscribeGroupCalls);
        Assert.Equal("tr", settings.Language);
    }

    [Fact]
    public void EnablingACloudProviderIsReportedAsSuch()
    {
        var settings = new AppSettings { LlmProvider = LlmProviderKind.OpenRouter };
        Assert.True(settings.AnythingLeavesTheMachine);

        Assert.True(new AppSettings { ExportToNotion = true }.AnythingLeavesTheMachine);
    }

    [Fact]
    public void RoundTripsThroughDisk()
    {
        var paths = Paths();

        var original = new AppSettings
        {
            AsrModelId = "faster-whisper-large-v3",
            LlmProvider = LlmProviderKind.Ollama,
            ExportToObsidian = true,
            ObsidianVaultPath = _root,
            AudioRetentionDays = 30,
        };

        original.Save(paths.SettingsFile);
        var loaded = AppSettings.Load(paths.SettingsFile);

        Assert.Equal("faster-whisper-large-v3", loaded.AsrModelId);
        Assert.Equal(LlmProviderKind.Ollama, loaded.LlmProvider);
        Assert.True(loaded.ExportToObsidian);
        Assert.Equal(30, loaded.AudioRetentionDays);
    }

    /// <summary>A corrupt settings file must not stop the application from starting.</summary>
    [Fact]
    public void ACorruptFileFallsBackToDefaults()
    {
        var paths = Paths();
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var loaded = AppSettings.Load(paths.SettingsFile);

        Assert.Equal(LlmProviderKind.LlamaServer, loaded.LlmProvider);
    }

    [Fact]
    public void AMissingFileYieldsDefaults()
        => Assert.NotNull(AppSettings.Load(Path.Combine(_root, "nope.json")));

    [Fact]
    public void ValidDefaultsProduceNoComplaints()
        => Assert.Empty(new AppSettings().Validate(Paths()));

    [Fact]
    public void RecordingNothingIsRejected()
    {
        var problems = new AppSettings { RecordWhatsApp = false, RecordTelegram = false }.Validate(Paths());

        Assert.Contains(problems, p => p.Contains("En az bir uygulama"));
    }

    [Fact]
    public void ObsidianExportWithoutAVaultIsRejected()
    {
        var problems = new AppSettings { ExportToObsidian = true }.Validate(Paths());

        Assert.Contains(problems, p => p.Contains("vault"));
    }

    [Fact]
    public void ACloudProviderWithoutAKeyIsRejected()
    {
        var problems = new AppSettings { LlmProvider = LlmProviderKind.OpenRouter }.Validate(Paths());

        Assert.Contains(problems, p => p.Contains("API anahtarı"));
    }

    [Fact]
    public void AnUnknownModelIdIsRejected()
    {
        var problems = new AppSettings { AsrModelId = "bilinmeyen-model" }.Validate(Paths());

        Assert.Contains(problems, p => p.Contains("Bilinmeyen"));
    }

    [Fact]
    public void AnUnknownModelIdStillResolvesToSomethingUsable()
        => Assert.Equal(
            Core.Asr.AsrCatalog.DefaultModelId,
            new AppSettings { AsrModelId = "yok-boyle-bir-sey" }.AsrModel.Id);

    [Fact]
    public void TheEndpointFallsBackToTheProviderDefault()
    {
        Assert.Equal(
            LlmProviders.Get(LlmProviderKind.Ollama).DefaultBaseUrl,
            new AppSettings { LlmProvider = LlmProviderKind.Ollama }.ResolvedBaseUrl);

        Assert.Equal(
            "http://127.0.0.1:9999/v1",
            new AppSettings { LlmBaseUrl = "http://127.0.0.1:9999/v1" }.ResolvedBaseUrl);
    }

    /// <summary>
    /// The GPU shares a power budget with the video encoder the call is using, so starting
    /// transcription immediately makes the machine throttle and the user notice the recorder.
    /// </summary>
    [Fact]
    public void ThereIsACooldownBeforeGpuWorkStarts()
        => Assert.True(new AppSettings().GpuCooldownSeconds >= 30);
}
