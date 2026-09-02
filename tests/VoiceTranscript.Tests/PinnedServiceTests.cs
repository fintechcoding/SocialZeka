using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// Choosing a service in "Yeniden yazıya dök" has to mean that service.
///
/// Two catalogues describe a cloud transcription and only one of them decided anything. The dialog
/// offered AsrCatalog's rows — "OpenAI Whisper API", "Groq" — which say only *that* the audio
/// leaves the machine; where it goes was always the first configured endpoint. Somebody picked
/// OpenAI and watched the toast say it was uploading to our own server. The choice had never been
/// wired to anything; it only became visible once the notice named the real endpoint instead of
/// repeating the model's label.
/// </summary>
public class PinnedServiceTests
{
    private static AppSettings WithServices() => new()
    {
        AsrMode = TranscriptionMode.CloudOnly,
        SttEndpoints =
        [
            new SttEndpoint { Id = "ex5id", Kind = "ex5", ApiKey = "k" },
            new SttEndpoint { Id = "oaiid", Kind = "openai", ApiKey = "k" },
        ],
    };

    [Fact]
    public void AChosenServiceIsFoundByItsOwnIdentity()
    {
        var settings = WithServices();
        var choice = CallOrchestrator.EndpointChoicePrefix + "oaiid";

        var pinned = settings.UsableSttEndpoints.FirstOrDefault(
            e => e.Id == choice[CallOrchestrator.EndpointChoicePrefix.Length..]);

        Assert.NotNull(pinned);
        Assert.Equal("OpenAI", pinned!.ResolvedName);

        // Not the first in the list — which is exactly what used to be used regardless.
        Assert.NotEqual(settings.UsableSttEndpoints[0].Id, pinned.Id);
    }

    /// <summary>
    /// Identity, not position or name. Reordering the cards or renaming one must not silently
    /// send the recording somewhere else.
    /// </summary>
    [Fact]
    public void ReorderingTheCardsDoesNotChangeWhereAChoiceGoes()
    {
        var settings = WithServices();
        var choice = CallOrchestrator.EndpointChoicePrefix + "oaiid";

        var reordered = new AppSettings
        {
            AsrMode = TranscriptionMode.CloudOnly,
            SttEndpoints = [.. settings.SttEndpoints.AsEnumerable().Reverse()],
        };

        var pinned = reordered.UsableSttEndpoints.FirstOrDefault(
            e => e.Id == choice[CallOrchestrator.EndpointChoicePrefix.Length..]);

        Assert.Equal("OpenAI", pinned?.ResolvedName);
    }

    /// <summary>
    /// A card deleted between opening the dialog and the job starting falls back to the ordinary
    /// route rather than to nothing — the recording still gets transcribed.
    /// </summary>
    [Fact]
    public void AServiceRemovedSinceTheDialogWasOpenedResolvesToNothingAndTheRouteGoesOn()
    {
        var settings = WithServices();

        Assert.Null(settings.UsableSttEndpoints.FirstOrDefault(e => e.Id == "silinmis"));
    }

    [Fact]
    public void AnOrdinaryModelChoiceIsNotMistakenForAService()
    {
        Assert.False("cloud-openai-whisper".StartsWith(CallOrchestrator.EndpointChoicePrefix, StringComparison.Ordinal));
        Assert.True((CallOrchestrator.EndpointChoicePrefix + "ex5id")
            .StartsWith(CallOrchestrator.EndpointChoicePrefix, StringComparison.Ordinal));
    }
}
