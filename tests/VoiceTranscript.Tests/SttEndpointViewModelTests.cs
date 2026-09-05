using System.Net;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Asr;

namespace VoiceTranscript.Tests;

/// <summary>
/// The model box on a service card: what it offers after a test, and in what order.
///
/// The probe narrows the provider's list to the models that transcribe and orders them; the card
/// used to sort the result alphabetically again, which put gpt-3.5-turbo back above whisper-1 and
/// undid the one thing the probe had done for the user. And the rest of the list is a click away,
/// with its count, rather than gone.
/// </summary>
public sealed class SttEndpointViewModelTests
{
    private sealed class Canned(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private static SttEndpointViewModel Card(string body) =>
        new(SttEndpoint.FromProvider(SttProviderCatalog.Find("openai")) with { ApiKey = "k" },
            new SttProbe(new HttpClient(new Canned(body))));

    private const string OpenAiList = """{"data":[{"id":"gpt-4o"},{"id":"whisper-1"},{"id":"babbage-002"},{"id":"gpt-4o-transcribe"}]}""";

    [Fact]
    public async Task TheTestFillsTheBoxInTheProbesOrderAndKeepsTheTypedModel()
    {
        var card = Card(OpenAiList);
        card.Model = "whisper-1";

        await card.TestCommand.ExecuteAsync(null);

        Assert.Equal(["gpt-4o-transcribe", "whisper-1"], card.Models);
        Assert.Equal("whisper-1", card.Model);
        Assert.Equal(2, card.HiddenModelCount);
        Assert.True(card.HasHiddenModels);
    }

    [Fact]
    public async Task ShowingAllPutsTheWholeListInTheBoxAndBack()
    {
        var card = Card(OpenAiList);
        await card.TestCommand.ExecuteAsync(null);

        card.ToggleShowAllModelsCommand.Execute(null);
        Assert.Equal(4, card.Models.Count);
        Assert.Contains("babbage-002", card.Models);

        card.ToggleShowAllModelsCommand.Execute(null);
        Assert.Equal(["gpt-4o-transcribe", "whisper-1"], card.Models);
    }

    [Fact]
    public void ACardBuiltFromTheCatalogueHidesNothing()
    {
        var card = Card(OpenAiList);

        Assert.False(card.HasHiddenModels);
        Assert.Equal(0, card.HiddenModelCount);
    }
}
