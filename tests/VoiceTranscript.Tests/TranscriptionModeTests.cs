using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// Uploading call audio is a different proposition from anything else the application does: a
/// recording carries voice identity and background, not just words. These tests pin down that
/// it only ever happens because the user chose it, and that they can tell when it will.
/// </summary>
public sealed class TranscriptionModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-mode-{Guid.NewGuid():N}");

    public TranscriptionModeTests() => Directory.CreateDirectory(Path.Combine(_root, "recordings"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppPaths Paths() => new(_root);

    [Fact]
    public void TheDefaultKeepsEverythingLocal()
    {
        var settings = new AppSettings();

        Assert.Equal(TranscriptionMode.LocalOnly, settings.AsrMode);
        Assert.False(settings.AudioMayLeaveTheMachine);
        Assert.False(settings.ResolveAsrModel(localTranscriptionUsable: true).SendsAudioOffMachine);
    }

    /// <summary>Local-only means local even when the GPU cannot run anything.</summary>
    [Fact]
    public void LocalOnlyNeverUploadsEvenWithoutAUsableGpu()
    {
        var settings = new AppSettings { AsrMode = TranscriptionMode.LocalOnly };

        Assert.False(settings.ResolveAsrModel(localTranscriptionUsable: false).SendsAudioOffMachine);
    }

    [Fact]
    public void AutomaticUsesTheLocalModelWhenItCanActuallyRun()
    {
        var settings = new AppSettings { AsrMode = TranscriptionMode.Automatic, AsrApiKey = "sk-test" };

        var model = settings.ResolveAsrModel(localTranscriptionUsable: true);

        Assert.False(model.SendsAudioOffMachine);
        Assert.Equal(AsrCatalog.DefaultModelId, model.Id);
    }

    [Fact]
    public void AutomaticFallsBackToTheCloudWhenItCannot()
    {
        var settings = new AppSettings { AsrMode = TranscriptionMode.Automatic, AsrApiKey = "sk-test" };

        Assert.True(settings.ResolveAsrModel(localTranscriptionUsable: false).SendsAudioOffMachine);
    }

    [Fact]
    public void CloudOnlyUploadsEvenWhenTheGpuWorks()
    {
        var settings = new AppSettings { AsrMode = TranscriptionMode.CloudOnly, AsrApiKey = "sk-test" };

        Assert.True(settings.ResolveAsrModel(localTranscriptionUsable: true).SendsAudioOffMachine);
    }

    /// <summary>
    /// Automatic can start uploading because a driver broke rather than because anything was
    /// decided, so the setting has to announce that possibility up front.
    /// </summary>
    [Fact]
    public void AnyModeThatCanUploadSaysSo()
    {
        Assert.True(new AppSettings { AsrMode = TranscriptionMode.Automatic }.AudioMayLeaveTheMachine);
        Assert.True(new AppSettings { AsrMode = TranscriptionMode.CloudOnly }.AudioMayLeaveTheMachine);
        Assert.False(new AppSettings { AsrMode = TranscriptionMode.LocalOnly }.AudioMayLeaveTheMachine);
    }

    /// <summary>Without a key the mode cannot work at all, so saving it would be a trap.</summary>
    [Fact]
    public void UploadingWithoutAKeyIsRejected()
    {
        var problems = new AppSettings { AsrMode = TranscriptionMode.CloudOnly }.Validate(Paths());

        Assert.Contains(problems, p => p.Contains("API anahtarı"));
    }

    [Fact]
    public void AProperlyConfiguredCloudModeIsAccepted()
        => Assert.Empty(new AppSettings
        {
            AsrMode = TranscriptionMode.CloudOnly,
            AsrApiKey = "sk-test",
        }.Validate(Paths()));

    [Fact]
    public void TheEndpointDefaultsToTheChosenProvider()
    {
        Assert.Equal("https://api.openai.com/v1", new AppSettings().ResolvedAsrBaseUrl);

        Assert.Equal(
            "https://api.groq.com/openai/v1",
            new AppSettings { CloudAsrModelId = "cloud-groq-turbo" }.ResolvedAsrBaseUrl);

        Assert.Equal(
            "http://localhost:9000/v1",
            new AppSettings { AsrApiBaseUrl = "http://localhost:9000/v1" }.ResolvedAsrBaseUrl);
    }

    /// <summary>A local model in the cloud slot would silently disable the whole mode.</summary>
    [Fact]
    public void ALocalModelCannotBeUsedAsTheCloudFallback()
    {
        var settings = new AppSettings { CloudAsrModelId = AsrCatalog.DefaultModelId };

        Assert.True(settings.CloudAsrModel.SendsAudioOffMachine);
    }

    [Fact]
    public void HostedModelsAreMarkedAndCarryTheirWarning()
    {
        var hosted = AsrCatalog.All.Where(m => m.SendsAudioOffMachine).ToList();

        Assert.NotEmpty(hosted);

        Assert.All(hosted, m =>
        {
            Assert.Equal(0, m.VramGb);
            Assert.Equal(0, m.DownloadGb);
            Assert.NotNull(m.DefaultBaseUrl);
            Assert.False(string.IsNullOrWhiteSpace(m.Warning), $"{m.Id} must say the audio leaves the machine");
            Assert.Contains("makineden çıkar", m.Warning);
        });
    }

    /// <summary>
    /// Groq hosts the same model the application already runs locally, so choosing it buys speed
    /// and costs privacy without buying accuracy. The catalogue should say so rather than let the
    /// user infer that a paid service must be better.
    /// </summary>
    [Fact]
    public void HostingTheSameModelIsNotPresentedAsAnUpgrade()
    {
        var groq = AsrCatalog.Get("cloud-groq-turbo");
        var local = AsrCatalog.Default;

        Assert.Equal(local.Wer!.MediaSpeech, groq.Wer!.MediaSpeech);
        Assert.Contains("doğruluk kazancı yoktur", groq.Warning);
    }

    [Fact]
    public void CloudSettingsSurviveARoundTrip()
    {
        var paths = Paths();
        paths.EnsureCreated();

        new AppSettings
        {
            AsrMode = TranscriptionMode.Automatic,
            CloudAsrModelId = "cloud-groq-turbo",
            AsrApiKey = "sk-secret",
            AsrApiBaseUrl = "https://example.test/v1",
        }.Save(paths.SettingsFile);

        var loaded = AppSettings.Load(paths.SettingsFile);

        Assert.Equal(TranscriptionMode.Automatic, loaded.AsrMode);
        Assert.Equal("cloud-groq-turbo", loaded.CloudAsrModelId);
        Assert.Equal("sk-secret", loaded.AsrApiKey);
        Assert.Equal("https://example.test/v1", loaded.ResolvedAsrBaseUrl);
    }
}
