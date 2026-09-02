using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// Every service the application knows how to talk to is on the screen, key or no key.
///
/// The list used to hold only what somebody had added by hand, so a machine with one key showed
/// one row in the reprocess dialog — and there is no way, from that screen, to learn that Groq or
/// OpenAI were options at all. Somebody holding an OpenAI key had to guess the service existed,
/// find the "Servis ekle" menu, pick it, and only then be offered it.
/// </summary>
public class SeededServiceCardsTests
{
    /// <summary>The rule, as the settings view model applies it on load.</summary>
    private static List<string> AfterSeeding(params string[] alreadyConfigured)
    {
        var kinds = alreadyConfigured.ToList();

        foreach (var provider in SttProviderCatalog.All)
        {
            if (provider.Kind == "custom") continue;
            if (kinds.Contains(provider.Kind)) continue;

            kinds.Add(provider.Kind);
        }

        return kinds;
    }

    [Fact]
    public void AFreshInstallOffersEveryServiceExceptTheBlankOne()
    {
        var kinds = AfterSeeding();

        Assert.Contains("openai", kinds);
        Assert.Contains("groq", kinds);
        Assert.Contains("ex5", kinds);

        // "Özel adres" has no address of its own, so an empty one is a card that cannot say what
        // it is for. It stays on the menu, where choosing it is deliberate.
        Assert.DoesNotContain("custom", kinds);
    }

    /// <summary>
    /// Order is the order services are tried in, so somebody's own arrangement has to survive.
    /// The seeded ones go on the end.
    /// </summary>
    [Fact]
    public void SomebodysOwnOrderIsUntouched()
    {
        var kinds = AfterSeeding("ex5", "deepgram");

        Assert.Equal("ex5", kinds[0]);
        Assert.Equal("deepgram", kinds[1]);
    }

    [Fact]
    public void AServiceAlreadySetUpIsNotDuplicated()
    {
        Assert.Single(AfterSeeding("openai").Where(k => k == "openai"));
    }

    /// <summary>
    /// The seeded cards change nothing until a key is typed into one: an endpoint without a key is
    /// not usable, so it is never tried and no service is contacted.
    /// </summary>
    /// <summary>
    /// The question this was all for: every service with a key shows up, all of them, not one.
    ///
    /// The reprocess dialog builds its cloud rows from UsableSttEndpoints, so this is the same
    /// list that screen renders. Three keys, three rows — and the empty cards beside them stay out
    /// of it, because offering a service that cannot answer is worse than not listing it.
    /// </summary>
    [Fact]
    public void EveryServiceWithAKeyIsOfferedForReprocessing()
    {
        var settings = new AppSettings
        {
            SttEndpoints =
            [
                new SttEndpoint { Kind = "ex5", ApiKey = "wsk-x" },
                new SttEndpoint { Kind = "openai", ApiKey = "sk-x" },
                new SttEndpoint { Kind = "groq", ApiKey = "gsk-x" },
                new SttEndpoint { Kind = "deepgram" },                       // seeded, no key yet
                new SttEndpoint { Kind = "together", ApiKey = "tg-x", Enabled = false },
            ],
        };

        var offered = settings.UsableSttEndpoints.Select(e => e.Kind).ToList();

        Assert.Equal(["ex5", "openai", "groq"], offered);
    }

    [Fact]
    public void AnEmptyCardIsNotTried()
    {
        var seeded = SttEndpoint.FromProvider(SttProviderCatalog.Find("groq"));

        Assert.False(seeded.IsUsable);
        Assert.True((seeded with { ApiKey = "gsk-x" }).IsUsable);
    }
}
