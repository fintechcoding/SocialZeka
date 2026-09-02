using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// The key from before there was a list still counts.
///
/// A single API key used to live in one field. When services became a list the old field was kept
/// as a fallback — used only "if nothing else is configured" — and that condition quietly cost
/// somebody their OpenAI key: it was in that field, and the moment they added a second service
/// through the new screen the list stopped being empty and the key was never looked at again.
///
/// It did not fail. It disappeared. The reprocess dialog offered one service on a machine that had
/// two, and nothing anywhere said why.
/// </summary>
public class StrandedLegacyKeyTests
{
    private static AppSettings With(string? legacy, params SttEndpoint[] cards) => new()
    {
        AsrApiKey = legacy,
        SttEndpoints = [.. cards],
    };

    [Fact]
    public void TheOldKeyIsStillOfferedAfterANewServiceIsAdded()
    {
        var settings = With("sk-eski", new SttEndpoint { Kind = "ex5", ApiKey = "wsk-x" });

        var offered = settings.UsableSttEndpoints.Select(e => e.ResolvedName).ToList();

        Assert.Equal(["ex5 Whisper (kendi sunucumuz)", "OpenAI"], offered);
    }

    /// <summary>
    /// Appended, not prepended. The order services are listed in is the order they are tried in,
    /// and that order is somebody's own decision — a key carried over from an older file must not
    /// quietly take the front of the queue.
    /// </summary>
    [Fact]
    public void SomebodysOwnFirstChoiceStaysFirst()
    {
        var settings = With("sk-eski", new SttEndpoint { Kind = "ex5", ApiKey = "wsk-x" });

        Assert.Equal("ex5", settings.UsableSttEndpoints[0].Kind);
    }

    /// <summary>
    /// Once the key has been moved onto a card, the old field must not add it a second time —
    /// the same service twice under two names, tried twice, failing twice.
    /// </summary>
    [Fact]
    public void AKeyAlreadyOnACardIsNotOfferedTwice()
    {
        var settings = With("sk-ayni", new SttEndpoint { Kind = "openai", ApiKey = "sk-ayni" });

        Assert.Single(settings.UsableSttEndpoints);
    }

    [Fact]
    public void WithNoOldKeyNothingIsAdded()
    {
        var settings = With(null, new SttEndpoint { Kind = "ex5", ApiKey = "wsk-x" });

        Assert.Single(settings.UsableSttEndpoints);
    }

    /// <summary>The case the fallback was written for still works: nothing else configured.</summary>
    [Fact]
    public void OnAnUntouchedInstallationTheOldKeyIsTheWholeList()
    {
        var settings = With("sk-eski");

        Assert.Equal("OpenAI", Assert.Single(settings.UsableSttEndpoints).ResolvedName);
    }

    [Fact]
    public void ADisabledCardDoesNotSuppressTheOldKey()
    {
        var settings = With("sk-eski", new SttEndpoint { Kind = "ex5", ApiKey = "wsk-x", Enabled = false });

        Assert.Equal("OpenAI", Assert.Single(settings.UsableSttEndpoints).ResolvedName);
    }
}
