using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// Several transcription services, tried in order.
///
/// The reason this is a list rather than one entry is worth restating: a hosted service is a
/// single point of failure on precisely the evening it matters, and the recording only exists
/// once. Everything here is about not losing that recording.
/// </summary>
public class SttProviderTests
{
    private static SttEndpoint Configured(string kind = "openai", string key = "sk-test") =>
        SttEndpoint.FromProvider(SttProviderCatalog.Find(kind)) with { ApiKey = key };

    [Fact]
    public void EveryCatalogueEntryHasWhatAnEndpointNeeds()
    {
        foreach (var provider in SttProviderCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName), provider.Kind);
            Assert.False(string.IsNullOrWhiteSpace(provider.DefaultModel), provider.Kind);
            Assert.False(string.IsNullOrWhiteSpace(provider.Summary), provider.Kind);

            // "Özel adres" is the one entry with no address of its own; that is its whole point.
            if (provider.Kind != "custom")
                Assert.False(string.IsNullOrWhiteSpace(provider.BaseUrl), provider.Kind);
        }
    }

    [Fact]
    public void AnUnknownProviderFallsBackToTheCustomEntryRatherThanThrowing()
    {
        // Settings files outlive releases. A provider removed from the catalogue must not stop
        // the application from starting.
        var provider = SttProviderCatalog.Find("a-provider-that-no-longer-exists");

        Assert.Equal("custom", provider.Kind);
    }

    [Fact]
    public void AnEndpointFillsItsGapsFromTheProvider()
    {
        var endpoint = new SttEndpoint { Kind = "groq", ApiKey = "gsk-x" };

        Assert.Equal("Groq", endpoint.ResolvedName);
        Assert.Equal("https://api.groq.com/openai/v1", endpoint.ResolvedBaseUrl);
        Assert.Equal("whisper-large-v3-turbo", endpoint.ResolvedModel);
        Assert.True(endpoint.IsUsable);
    }

    [Fact]
    public void AnEndpointWithoutAKeyIsNotTried()
    {
        Assert.False((Configured() with { ApiKey = "" }).IsUsable);
    }

    [Fact]
    public void ADisabledEndpointStaysConfiguredButIsNotTried()
    {
        // Kept rather than deleted so a key can be rotated without retyping the whole entry.
        var endpoint = Configured() with { Enabled = false };

        Assert.False(endpoint.IsUsable);
        Assert.Equal("OpenAI", endpoint.ResolvedName);
    }

    [Fact]
    public void AProviderWeCannotUploadToIsNeverSelectedForUpload()
    {
        // Deepgram is listed so its balance can be watched, but its request shape is not the one
        // the worker speaks. Offering it as a destination would fail on a real call.
        var deepgram = Configured("deepgram");

        Assert.False(deepgram.Provider.OpenAiCompatible);
        Assert.False(deepgram.IsUsable);
    }

    [Fact]
    public void TheWorkerReferenceCarriesAddressKeyAndModel()
    {
        var reference = Configured("groq", "gsk-abc").ToModelRef();

        Assert.Equal("https://api.groq.com/openai/v1|gsk-abc|whisper-large-v3-turbo", reference);
    }

    [Fact]
    public void ATrailingSlashOnTheAddressDoesNotProduceADoubleSlash()
    {
        var endpoint = Configured() with { BaseUrl = "https://example.test/v1/" };

        Assert.Equal("https://example.test/v1", endpoint.ResolvedBaseUrl);
    }

    // ---- settings integration ----------------------------------------------

    [Fact]
    public void ConfiguredEndpointsAreOfferedInOrder()
    {
        var settings = new AppSettings
        {
            SttEndpoints =
            [
                Configured("openai", "sk-1") with { Name = "Birinci" },
                Configured("groq", "gsk-2") with { Name = "İkinci" },
            ],
        };

        Assert.Equal(["Birinci", "İkinci"], settings.UsableSttEndpoints.Select(e => e.ResolvedName));
    }

    [Fact]
    public void UnusableEndpointsAreSkippedWithoutBreakingTheOrder()
    {
        var settings = new AppSettings
        {
            SttEndpoints =
            [
                Configured("openai", "") with { Name = "Anahtarsız" },
                Configured("groq", "gsk-2") with { Name = "Çalışan" },
            ],
        };

        Assert.Equal(["Çalışan"], settings.UsableSttEndpoints.Select(e => e.ResolvedName));
    }

    [Fact]
    public void AnOlderSettingsFileWithOneKeyStillWorks()
    {
        // The single-endpoint fields predate the list. An existing installation must keep
        // transcribing after an update without the user reconfiguring anything.
        var settings = new AppSettings
        {
            AsrApiKey = "sk-legacy",
            AsrApiBaseUrl = "https://api.openai.com/v1",
            CloudAsrModelId = "cloud-openai-whisper",
        };

        var endpoints = settings.UsableSttEndpoints;

        Assert.Single(endpoints);
        Assert.Equal("sk-legacy", endpoints[0].ApiKey);
        Assert.Contains("openai.com", endpoints[0].ResolvedBaseUrl);
    }

    [Fact]
    public void TheListWinsOverTheOlderSingleKeyOnceItIsConfigured()
    {
        var settings = new AppSettings
        {
            AsrApiKey = "sk-legacy",
            SttEndpoints = [Configured("groq", "gsk-new") with { Name = "Yeni" }],
        };

        Assert.Single(settings.UsableSttEndpoints);
        Assert.Equal("Yeni", settings.UsableSttEndpoints[0].ResolvedName);
    }

    [Fact]
    public void NoConfigurationAtAllYieldsNoEndpointsRatherThanABrokenOne()
    {
        Assert.Empty(new AppSettings().UsableSttEndpoints);
    }

    // ---- model listing ------------------------------------------------------

    [Theory]
    [InlineData("""{"data":[{"id":"whisper-1"},{"id":"gpt-4o-transcribe"}]}""")]
    [InlineData("""[{"id":"whisper-1"},{"id":"gpt-4o-transcribe"}]""")]
    public void BothModelListingShapesAreUnderstood(string body)
    {
        // OpenAI wraps the list in "data"; a few compatible servers return a bare array.
        var models = SttProbe.ParseModelList(body);

        Assert.Equal(["whisper-1", "gpt-4o-transcribe"], models);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"error":"nope"}""")]
    [InlineData("""{"data":"unexpected"}""")]
    public void AnUnreadableListingIsTreatedAsNoListingRatherThanAFailure(string body)
    {
        // A provider that does not publish models is common. Reporting that as a broken key
        // would send the user hunting for a problem that is not there.
        Assert.Empty(SttProbe.ParseModelList(body));
    }

    // ---- balance ------------------------------------------------------------

    [Fact]
    public void ProvidersWithoutABalanceEndpointSaySoRatherThanShowingAFigure()
    {
        // The one thing a credit display must never do is tell somebody they have money left
        // when nobody asked the provider.
        foreach (var kind in new[] { "openai", "groq", "custom" })
            Assert.Equal(BalanceProbe.None, SttProviderCatalog.Find(kind).Balance);
    }

    [Fact]
    public void ProvidersThatPublishABalanceAreWiredToReadIt()
    {
        Assert.Equal(BalanceProbe.OpenRouterKey, SttProviderCatalog.Find("openrouter").Balance);
        Assert.Equal(BalanceProbe.ElevenLabsSubscription, SttProviderCatalog.Find("elevenlabs").Balance);
        Assert.Equal(BalanceProbe.DeepgramBalance, SttProviderCatalog.Find("deepgram").Balance);
    }

    [Theory]
    [InlineData(0.95, true)]
    [InlineData(0.90, true)]
    [InlineData(0.89, false)]
    [InlineData(null, false)]
    public void ABalanceIsCalledLowOnlyWhenItReallyIs(double? usedFraction, bool expected)
    {
        var balance = new SttBalance { Supported = true, Message = "", UsedFraction = usedFraction };

        Assert.Equal(expected, balance.IsLow);
    }

    [Fact]
    public void AHealthyResultRequiresBothReachabilityAndAKeyThatWorks()
    {
        Assert.False(new SttTestResult { Message = "", Reachable = true }.IsHealthy);
        Assert.False(new SttTestResult { Message = "", Authorised = true }.IsHealthy);
        Assert.True(new SttTestResult { Message = "", Reachable = true, Authorised = true }.IsHealthy);
    }
}

/// <summary>
/// Saved model choices that can no longer work are healed, not honoured.
///
/// The settings screen once offered OpenAI's gpt-4o transcribe models, so real settings files
/// carry them — and they reject verbose_json, the one format that carries word timestamps. A
/// verified failure from a real archive: 400, "Use 'json' or 'text' instead". Honouring the
/// saved choice means failing on every call forever; healing it means the next run just works.
/// </summary>
public class SavedModelHealingTests
{
    [Fact]
    public void AKnownTimestamplessModelIsCoercedToWhisper()
    {
        var endpoint = new VoiceTranscript.Core.Asr.SttEndpoint
        {
            Kind = "openai",
            ApiKey = "sk-x",
            Model = "gpt-4o-mini-transcribe",
        };

        Assert.Equal("whisper-1", endpoint.ResolvedModel);
    }

    [Fact]
    public void AnOrdinaryModelChoiceIsHonoured()
    {
        var endpoint = new VoiceTranscript.Core.Asr.SttEndpoint
        {
            Kind = "groq",
            ApiKey = "gsk-x",
            Model = "whisper-large-v3",
        };

        Assert.Equal("whisper-large-v3", endpoint.ResolvedModel);
    }

    [Fact]
    public void TheOpenAiProviderNoLongerOffersTimestamplessModels()
    {
        var provider = VoiceTranscript.Core.Asr.SttProviderCatalog.Find("openai");

        Assert.DoesNotContain("gpt-4o-transcribe", provider.Models);
        Assert.DoesNotContain("gpt-4o-mini-transcribe", provider.Models);
    }
}
