using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// Turning a provider's catalogue into a choice.
///
/// OpenAI answers /v1/models with 126 entries on an ordinary account, verified against a real key.
/// Most are not choices: dated snapshots of models already in the list, text-to-speech voices,
/// embedding models, coding variants. Handing that over whole is not offering a choice, it is
/// offering a haystack — and picking wrong means a provider that rejects every request for reasons
/// that never mention the model.
/// </summary>
public sealed class ModelRecommendationTests
{
    private static RemoteModel M(string id) => new(id, id, null);

    /// <summary>
    /// A dated snapshot is dropped only when the plain name is also there. Pinning a date is a
    /// legitimate thing to want, and the undated entry means the same model.
    /// </summary>
    [Fact]
    public void DatedDuplicatesGoOnlyWhenTheUndatedNameIsAlsoOffered()
    {
        var models = new[]
        {
            M("gpt-5.5"), M("gpt-5.5-2026-04-23"),
            M("gpt-4.1-mini"), M("gpt-4.1-mini-2025-04-14"),

            // No undated sibling, so this one has to survive: dropping it would remove the only
            // way to reach that model.
            M("o1-pro-2025-03-19"),
        };

        var kept = ModelRecommendations.Winnow(models, forTranscription: false)
            .Select(m => m.Id).ToList();

        Assert.Contains("gpt-5.5", kept);
        Assert.DoesNotContain("gpt-5.5-2026-04-23", kept);
        Assert.DoesNotContain("gpt-4.1-mini-2025-04-14", kept);
        Assert.Contains("o1-pro-2025-03-19", kept);
    }

    /// <summary>A speech synthesiser cannot analyse a transcript.</summary>
    [Fact]
    public void ThingsThatCannotDoTheJobAreNotOfferedForIt()
    {
        var models = new[]
        {
            M("gpt-5.5"), M("gpt-4o-mini-tts"), M("text-embedding-3-large"),
            M("omni-moderation-latest"), M("dall-e-3"), M("gpt-5.2-codex"),
            M("whisper-1"), M("gpt-4o-transcribe"),
        };

        var forAnalysis = ModelRecommendations.Winnow(models, forTranscription: false)
            .Select(m => m.Id).ToList();

        Assert.Contains("gpt-5.5", forAnalysis);
        Assert.DoesNotContain("gpt-4o-mini-tts", forAnalysis);
        Assert.DoesNotContain("text-embedding-3-large", forAnalysis);
        Assert.DoesNotContain("dall-e-3", forAnalysis);
        Assert.DoesNotContain("whisper-1", forAnalysis);

        // And the reverse: choosing a transcription model must keep the transcription models.
        var forTranscription = ModelRecommendations.Winnow(models, forTranscription: true)
            .Select(m => m.Id).ToList();

        Assert.Contains("whisper-1", forTranscription);
        Assert.Contains("gpt-4o-transcribe", forTranscription);
        Assert.DoesNotContain("gpt-4o-mini-tts", forTranscription);
    }

    /// <summary>
    /// An empty picker is a worse failure than a cluttered one. A provider whose names happen to
    /// trip every rule must still be usable.
    /// </summary>
    [Fact]
    public void FilteringNeverEmptiesTheList()
    {
        var models = new[] { M("my-tts-thing"), M("another-embedding") };

        Assert.Equal(2, ModelRecommendations.Winnow(models, forTranscription: false).Count);
    }

    /// <summary>
    /// Recommendations are the intersection with what the provider actually returned, so a name
    /// that has been retired disappears instead of being offered and then rejected.
    /// </summary>
    [Fact]
    public void OnlyRecommendationsTheProviderActuallyHasAreShown()
    {
        var offered = ModelRecommendations.For(LlmProviderKind.OpenAi).First().Id;

        var (recommended, others) = ModelRecommendations.Split(
            [M(offered), M("baska-bir-model")], LlmProviderKind.OpenAi);

        Assert.Single(recommended);
        Assert.Equal(offered, recommended[0].Id);
        Assert.True(recommended[0].IsRecommended);

        // The reason replaces the provider's own description, which is usually the id repeated.
        Assert.False(string.IsNullOrWhiteSpace(recommended[0].Detail));

        Assert.Single(others);
        Assert.Equal("baska-bir-model", others[0].Id);
        Assert.False(others[0].IsRecommended);
    }

    [Fact]
    public void AProviderWithNoRecommendationsKeepsItsWholeList()
    {
        var (recommended, others) = ModelRecommendations.Split(
            [M("qwen3.5-4b")], LlmProviderKind.LlamaServer);

        Assert.Empty(recommended);
        Assert.Single(others);
    }

    /// <summary>
    /// The recommendation lists must be spelled for the provider they are offered to. The same
    /// model is "anthropic/claude-haiku-4.5" through OpenRouter and "claude-haiku-4-5" against
    /// Anthropic directly, and offering the wrong spelling produces a rejection naming neither.
    /// </summary>
    [Fact]
    public void RecommendationsAreSpelledForTheirProvider()
    {
        Assert.All(ModelRecommendations.For(LlmProviderKind.Anthropic),
            r => Assert.DoesNotContain("/", r.Id));

        Assert.All(ModelRecommendations.For(LlmProviderKind.OpenAi),
            r => Assert.DoesNotContain("/", r.Id));

        Assert.All(ModelRecommendations.For(LlmProviderKind.OpenRouter),
            r => Assert.Contains("/", r.Id));
    }

    /// <summary>Every recommendation says why, because a list of names is not advice.</summary>
    [Fact]
    public void EveryRecommendationCarriesAReason()
    {
        foreach (var kind in new[]
        {
            LlmProviderKind.OpenAi, LlmProviderKind.Anthropic, LlmProviderKind.OpenRouter,
        })
        {
            Assert.All(ModelRecommendations.For(kind), r =>
            {
                Assert.False(string.IsNullOrWhiteSpace(r.Reason));
                Assert.EndsWith(".", r.Reason);
            });
        }
    }
}
