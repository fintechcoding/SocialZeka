using System.Net;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// The model box has three honest answers — a list, a refused key, or "the service does not
/// say" — and the connection test must never call a model missing on the strength of a list
/// that does not contain transcription models at all.
/// </summary>
public sealed class SttProbeTests
{
    private sealed class Canned(HttpStatusCode status, string body, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static SttProbe Probe(HttpStatusCode status, string body, Action<HttpRequestMessage>? inspect = null) =>
        new(new HttpClient(new Canned(status, body, inspect)));

    private static SttEndpoint Endpoint(string kind, string key = "k") =>
        SttEndpoint.FromProvider(SttProviderCatalog.Find(kind)) with { ApiKey = key };

    private const string ElevenLabsVoices = """{"models":[{"model_id":"eleven_multilingual_v2","name":"Eleven Multilingual v2"}]}""";

    [Fact]
    public async Task ElevenLabsProvesTheKeyAndOffersTheKnownTranscriptionModels()
    {
        HttpRequestMessage? sent = null;
        var probe = Probe(HttpStatusCode.OK, ElevenLabsVoices, r => sent = r);

        var listing = await probe.ListModelsAsync(Endpoint("elevenlabs"));

        Assert.True(listing.KeyAccepted);
        Assert.True(listing.FromCatalogue);
        Assert.Contains("scribe_v2", listing.Models);
        Assert.Equal("k", sent!.Headers.GetValues("xi-api-key").Single());
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task ARefusedKeyIsSaidAsSuchAndTheKnownModelsStayTypeable()
    {
        var listing = await Probe(HttpStatusCode.Unauthorized, """{"detail":"bad key"}""").ListModelsAsync(Endpoint("elevenlabs"));

        Assert.True(listing.KeyRejected);
        Assert.False(listing.KeyAccepted);
        Assert.Contains("scribe_v2", listing.Models);
        Assert.Contains("kabul edilmedi", listing.Message);
    }

    [Fact]
    public async Task DeepgramListsItsTranscriptionModelsUnderStt()
    {
        const string body = """
            {"stt":[{"name":"nova-2","canonical_name":"nova-2-general"},{"name":"nova-3","canonical_name":"nova-3"}],
             "tts":[{"name":"aura-asteria-en"}]}
            """;

        var listing = await Probe(HttpStatusCode.OK, body).ListModelsAsync(Endpoint("deepgram"));

        Assert.False(listing.FromCatalogue);
        Assert.Equal(["nova-2-general", "nova-3"], listing.Models);
    }

    [Fact]
    public async Task AnOpenAiShapedListIsFilteredToTranscriptionFirst()
    {
        const string body = """{"data":[{"id":"gpt-4o"},{"id":"whisper-1"},{"id":"gpt-4o-transcribe"}]}""";

        var listing = await Probe(HttpStatusCode.OK, body).ListModelsAsync(Endpoint("openai"));

        Assert.False(listing.FromCatalogue);
        Assert.Contains("whisper-1", listing.Models);
        Assert.Contains("gpt-4o-transcribe", listing.Models);
    }

    [Fact]
    public async Task AServiceThatPublishesNoListStillLeavesSomethingToPick()
    {
        var listing = await Probe(HttpStatusCode.NotFound, "nope").ListModelsAsync(Endpoint("custom") with { BaseUrl = "https://example.test/v1" });

        Assert.True(listing.FromCatalogue);
        Assert.True(listing.KeyAccepted);
        Assert.NotEmpty(listing.Models);
        Assert.Contains("elle", listing.Message);
    }

    [Fact]
    public async Task WithoutAKeyNothingIsAskedButTheKnownModelsAreShown()
    {
        var asked = false;
        var listing = await Probe(HttpStatusCode.OK, "{}", _ => asked = true).ListModelsAsync(Endpoint("elevenlabs", key: ""));

        Assert.False(asked);
        Assert.True(listing.KeyRejected);
        Assert.Contains("scribe_v2", listing.Models);
    }

    /// <summary>
    /// The connection test used to read ElevenLabs' voice list and announce "scribe_v2 listede
    /// yok" — a green key reported as a missing model. A catalogue answer cannot rule a model out.
    /// </summary>
    [Fact]
    public async Task TheConnectionTestDoesNotCallScribeMissingOnTheStrengthOfTheVoiceList()
    {
        var result = await Probe(HttpStatusCode.OK, ElevenLabsVoices).TestAsync(Endpoint("elevenlabs"));

        Assert.True(result.IsHealthy);
        Assert.True(result.ModelAvailable);
        Assert.DoesNotContain("listede yok", result.Message);
    }

    [Fact]
    public async Task TheConnectionTestStillCatchesAModelTheServiceDoesNotHave()
    {
        const string body = """{"data":[{"id":"whisper-1"}]}""";

        // gpt-4o-transcribe would be resolved to whisper-1 (no word timestamps), so a model the
        // resolver leaves alone is asked for.
        var result = await Probe(HttpStatusCode.OK, body).TestAsync(Endpoint("openai") with { Model = "whisper-large-v3-turbo" });

        Assert.True(result.IsHealthy);
        Assert.False(result.ModelAvailable);
        Assert.Contains("listede yok", result.Message);
    }
}
