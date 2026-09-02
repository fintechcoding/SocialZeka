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

    /// <summary>
    /// A service with its own dialect is routed to the worker engine that speaks it. Both of
    /// these were once sent OpenAI's request and failed on the first upload while the connection
    /// test showed green.
    /// </summary>
    [Theory]
    [InlineData("elevenlabs", "cloud-elevenlabs")]
    [InlineData("deepgram", "cloud-deepgram")]
    [InlineData("ex5", "cloud-ex5")]
    [InlineData("openai", "cloud-openai")]
    [InlineData("groq", "cloud-openai")]
    public void EachProviderIsSentToTheEngineThatSpeaksItsDialect(string kind, string engine)
    {
        var endpoint = Configured(kind);

        Assert.Equal(engine, endpoint.Provider.WorkerEngine);
        Assert.True(endpoint.IsUsable);
    }

    /// <summary>
    /// Every engine named in the catalogue is one the worker will actually build.
    ///
    /// The two sides are separate programs and nothing links them: WorkerEngine is a string here
    /// and a dictionary key in worker/vt_worker/engines/__init__.py, and a mismatch is not a
    /// compile error. It is a recorded conversation that reaches the worker and comes back
    /// "Unknown engine 'cloud-ex5'" — after the call, when the audio is the only copy. This list
    /// is that dictionary, written out so the two drift apart in a test rather than in a job.
    /// </summary>
    [Fact]
    public void EveryCatalogueEntryNamesAnEngineTheWorkerRegisters()
    {
        string[] registered =
        [
            "faster-whisper", "whisper.cpp",
            "cloud-openai", "cloud-elevenlabs", "cloud-deepgram", "cloud-ex5",
        ];

        foreach (var provider in SttProviderCatalog.All)
            Assert.Contains(provider.WorkerEngine, registered);
    }

    /// <summary>
    /// Where an entry sits in the list is behaviour, not formatting.
    ///
    /// Find falls back to the last entry for a kind it does not recognise, and that fallback is
    /// meant to be "Özel adres" — the one entry with no address, key or model of its own, so an
    /// unrecognised kind in an old settings file becomes a card asking to be filled in. Appending
    /// a real provider after it would instead point every stale entry at somebody's server. The
    /// first entry is load-bearing too: the plain "Servis ekle" button seeds a card from it.
    /// </summary>
    [Fact]
    public void TheListEndsWithTheCustomEntryAndBeginsWithOpenAi()
    {
        Assert.Equal("custom", SttProviderCatalog.All[^1].Kind);
        Assert.Equal("openai", SttProviderCatalog.All[0].Kind);
    }

    /// <summary>
    /// Our own server: a Bearer key like everybody else, and no balance to read.
    ///
    /// The base address carries no trailing slash on purpose — ResolvedBaseUrl trims only what
    /// the user typed, so a slash written into the catalogue survives into "…/v1//models".
    /// </summary>
    [Fact]
    public void TheSelfHostedServerIsConfiguredWithoutAnAccountToOpen()
    {
        var provider = SttProviderCatalog.Find("ex5");

        Assert.Equal("https://stt.ex5.ai/v1", provider.BaseUrl);
        Assert.DoesNotContain("//v1", provider.BaseUrl.Replace("https://", ""));
        Assert.EndsWith("/v1", provider.BaseUrl);

        // No self-service signup page: the key is issued out of band, so the card shows no
        // "Anahtar al" link rather than a link that goes nowhere.
        Assert.Null(provider.SignupUrl);

        Assert.True(Configured("ex5").IsUsable);
    }

    [Fact]
    public void ElevenLabsDefaultsToTheCurrentScribeModel()
    {
        Assert.Equal("scribe_v2", Configured("elevenlabs").ResolvedModel);
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

    /// <summary>
    /// The list comes first, and the older single key comes after it — not instead of it.
    ///
    /// This test used to assert the opposite: that configuring anything made the old field stop
    /// counting. That reading cost a real user their OpenAI key. It was in that field, they added
    /// our own server through the new screen, and the key was never looked at again — the reprocess
    /// dialog offered one service on a machine that had two, and nothing said why. It did not fail,
    /// it disappeared, which is the worse of the two.
    ///
    /// Order still belongs to the person who chose it, so the carried-over key goes on the end.
    /// </summary>
    [Fact]
    public void TheOlderSingleKeyIsAddedAfterTheListRatherThanReplacedByIt()
    {
        var settings = new AppSettings
        {
            AsrApiKey = "sk-legacy",
            SttEndpoints = [Configured("groq", "gsk-new") with { Name = "Yeni" }],
        };

        var endpoints = settings.UsableSttEndpoints;

        Assert.Equal(2, endpoints.Count);
        Assert.Equal("Yeni", endpoints[0].ResolvedName);
        Assert.Equal("sk-legacy", endpoints[1].ApiKey);
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
        foreach (var kind in new[] { "openai", "groq", "ex5", "custom" })
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
