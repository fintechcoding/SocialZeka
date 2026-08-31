using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// Entering a cloud transcription key where the interface asks for it.
///
/// The settings screen used to have one key field and now has a list of services. The validation
/// was never moved: it still tested the old single field, which nothing fills in any more. So
/// somebody who typed their key into the service — the only place the interface offers — was told
/// "API anahtarı girilmemiş" while looking straight at the key they had just entered, and could
/// not save.
///
/// Two separate faults produced that, and both are covered here: the check read the wrong place,
/// and nothing re-ran the check when a service was edited.
/// </summary>
public sealed class CloudKeyValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-cloudkey-{Guid.NewGuid():N}");

    public CloudKeyValidationTests() => Directory.CreateDirectory(Path.Combine(_root, "recordings"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppPaths Paths() => new(_root);

    private static SttEndpoint OpenAi(string? key = "sk-test") => new()
    {
        Kind = "openai",
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = key ?? "",
        Model = "whisper-1",
        Enabled = true,
    };

    private static bool ComplainsAboutTheKey(IReadOnlyList<string> problems) =>
        problems.Any(p => p.Contains("Buluta gönderme açık", StringComparison.Ordinal));

    /// <summary>The exact bug the user hit: key entered in the service, refused anyway.</summary>
    [Fact]
    public void AKeyEnteredInTheServiceCountsAsAKey()
    {
        var settings = new AppSettings
        {
            AsrMode = TranscriptionMode.Automatic,
            SttEndpoints = [OpenAi()],

            // Empty, because this is the field the screen stopped offering.
            AsrApiKey = null,
        };

        Assert.False(ComplainsAboutTheKey(settings.Validate(Paths())));
    }

    /// <summary>With nothing configured at all, the complaint is right and must stay.</summary>
    [Fact]
    public void SendingToTheCloudWithNoServiceIsStillRefused()
    {
        var settings = new AppSettings { AsrMode = TranscriptionMode.Automatic };

        Assert.True(ComplainsAboutTheKey(settings.Validate(Paths())));
    }

    /// <summary>A service with no key is not a service. Half-filled must not pass.</summary>
    [Fact]
    public void AServiceMissingItsKeyDoesNotCount()
    {
        var settings = new AppSettings
        {
            AsrMode = TranscriptionMode.Automatic,
            SttEndpoints = [OpenAi(key: null)],
        };

        Assert.True(ComplainsAboutTheKey(settings.Validate(Paths())));
    }

    /// <summary>
    /// A service switched off does not keep the configuration valid. Otherwise turning off the
    /// only service you have would leave the screen claiming everything is fine while nothing
    /// could run.
    /// </summary>
    [Fact]
    public void ADisabledServiceDoesNotCount()
    {
        var settings = new AppSettings
        {
            AsrMode = TranscriptionMode.Automatic,
            SttEndpoints = [OpenAi() with { Enabled = false }],
        };

        Assert.True(ComplainsAboutTheKey(settings.Validate(Paths())));
    }

    /// <summary>Settings written before the list existed keep working from the old single field.</summary>
    [Fact]
    public void TheOldSingleKeyFieldStillSatisfiesTheCheck()
    {
        var settings = new AppSettings
        {
            AsrMode = TranscriptionMode.Automatic,
            AsrApiKey = "sk-eski",
        };

        Assert.False(ComplainsAboutTheKey(settings.Validate(Paths())));
    }

    /// <summary>Running locally needs no key at all.</summary>
    [Fact]
    public void LocalOnlyNeedsNothing()
    {
        var settings = new AppSettings { AsrMode = TranscriptionMode.LocalOnly };

        Assert.False(ComplainsAboutTheKey(settings.Validate(Paths())));
    }

    // ---- the second fault: the warning did not refresh -----------------------

    /// <summary>
    /// Typing the key has to clear the warning while you are looking at it.
    ///
    /// Each service is its own view model, so its notifications reached nobody. The message stayed
    /// on screen until an unrelated field was touched — which reads as "the application does not
    /// believe me", and is how somebody concludes the feature is broken and gives up.
    /// </summary>
    [Fact]
    public void TypingAKeyIntoAServiceClearsTheWarningImmediately()
    {
        using var http = new HttpClient();

        var model = new SettingsViewModel(
            new AppSettings
            {
                AsrMode = TranscriptionMode.Automatic,
                SttEndpoints = [OpenAi(key: null)],
            },
            Paths(),
            http);

        Assert.True(ComplainsAboutTheKey(model.Problems));

        model.SttEndpoints.Single().ApiKey = "sk-test";

        Assert.False(ComplainsAboutTheKey(model.Problems));
    }

    /// <summary>And switching a service off has to bring the warning back.</summary>
    [Fact]
    public void SwitchingTheOnlyServiceOffBringsTheWarningBack()
    {
        using var http = new HttpClient();

        var model = new SettingsViewModel(
            new AppSettings
            {
                AsrMode = TranscriptionMode.Automatic,
                SttEndpoints = [OpenAi()],
            },
            Paths(),
            http);

        Assert.False(ComplainsAboutTheKey(model.Problems));

        model.SttEndpoints.Single().Enabled = false;

        Assert.True(ComplainsAboutTheKey(model.Problems));
    }

    /// <summary>Removing the last service does too — the collection itself is watched.</summary>
    [Fact]
    public void RemovingTheLastServiceBringsTheWarningBack()
    {
        using var http = new HttpClient();

        var model = new SettingsViewModel(
            new AppSettings
            {
                AsrMode = TranscriptionMode.Automatic,
                SttEndpoints = [OpenAi()],
            },
            Paths(),
            http);

        Assert.False(ComplainsAboutTheKey(model.Problems));

        model.SttEndpoints.Clear();

        Assert.True(ComplainsAboutTheKey(model.Problems));
    }
}
