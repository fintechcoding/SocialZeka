using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// A reasoning model that spends its whole allowance thinking and answers nothing.
///
/// The thinking comes out of the same budget as the answer, so a request that would have been
/// generous for an ordinary model can be exhausted before a single visible character is written.
/// On a real call the ledger step asked for 2048 and got back finish_reason "length", 2048
/// completion tokens and an empty string.
///
/// Nothing about it looked like a failure. The request succeeded, the section was skipped as
/// unfinished, and the conversation was filed as analysed with no summary, no commitments and no
/// claims — which on screen is indistinguishable from a conversation in which nothing was
/// promised. The user's question was the right one: how did the suggestions come out when the
/// summary did not? Because they are a separate call with a separate budget, and only one of the
/// two ran out.
/// </summary>
public sealed class ReasoningBudgetTests
{
    private sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return responses[Math.Min(_index++, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>All budget, no answer — the shape the archive actually received.</summary>
    private const string SpentItAllThinking =
        """
        {"choices":[{"message":{"content":""},"finish_reason":"length"}],
         "usage":{"prompt_tokens":1787,"completion_tokens":2048}}
        """;

    private const string Answered =
        """
        {"choices":[{"message":{"content":"{\"taahhutler\":[]}"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1787,"completion_tokens":260}}
        """;

    /// <summary>A real answer that happens to be cut off. Different thing, left alone.</summary>
    private const string CutOff =
        """
        {"choices":[{"message":{"content":"{\"taahhutler\":[{\"yukumluluk\":\"yarın"},"finish_reason":"length"}],
         "usage":{"prompt_tokens":1787,"completion_tokens":2048}}
        """;

    private static LlmRequest Request(int maxTokens = 2048, int ceiling = 16_384) => new()
    {
        Model = "gpt-5.6-sol",
        SystemPrompt = "sistem",
        UserPrompt = "kullanıcı",
        MaxTokens = maxTokens,
        MaxTokensCeiling = ceiling,
    };

    private static int BudgetIn(string body) =>
        (JsonNode.Parse(body)!["max_tokens"] ?? JsonNode.Parse(body)!["max_completion_tokens"])!.GetValue<int>();

    [Fact]
    public async Task AnEmptyAnswerAtTheLimitIsAskedAgainWithRoomToThink()
    {
        var handler = new StubHandler(_ => Json(SpentItAllThinking), _ => Json(Answered));
        using var http = new HttpClient(handler);

        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenAi, "https://example.invalid/v1", "k");
        var response = await client.CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("{\"taahhutler\":[]}", response.Content);
        Assert.Equal(2, handler.Bodies.Count);

        Assert.Equal(2048, BudgetIn(handler.Bodies[0]));
        Assert.Equal(8192, BudgetIn(handler.Bodies[1]));
    }

    /// <summary>
    /// A truncated answer holds real content and the caller can see it was cut. Asking again
    /// would pay for the same tokens twice and is not this method's business.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatWasMerelyCutOffIsNotRepeated()
    {
        var handler = new StubHandler(_ => Json(CutOff));
        using var http = new HttpClient(handler);

        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenAi, "https://example.invalid/v1", "k");
        var response = await client.CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Single(handler.Bodies);
        Assert.Contains("yarın", response.Content);
    }

    /// <summary>Once, not until the money runs out.</summary>
    [Fact]
    public async Task ItIsAskedAgainOnlyOnce()
    {
        var handler = new StubHandler(_ => Json(SpentItAllThinking));
        using var http = new HttpClient(handler);

        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenAi, "https://example.invalid/v1", "k");
        var response = await client.CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("", response.Content);
        Assert.Equal("length", response.FinishReason);
    }

    /// <summary>The ceiling holds: a request already at it is not retried at all.</summary>
    [Fact]
    public async Task ARequestAlreadyAtTheCeilingIsNotRepeated()
    {
        var handler = new StubHandler(_ => Json(SpentItAllThinking));
        using var http = new HttpClient(handler);

        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenAi, "https://example.invalid/v1", "k");
        await client.CompleteAsync(Request(maxTokens: 4096, ceiling: 4096), TestContext.Current.CancellationToken);

        Assert.Single(handler.Bodies);
    }

    /// <summary>And the raise never goes past the ceiling.</summary>
    [Fact]
    public async Task TheSecondAskIsCappedAtTheCeiling()
    {
        var handler = new StubHandler(_ => Json(SpentItAllThinking), _ => Json(Answered));
        using var http = new HttpClient(handler);

        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenAi, "https://example.invalid/v1", "k");
        await client.CompleteAsync(Request(maxTokens: 2048, ceiling: 3000), TestContext.Current.CancellationToken);

        Assert.Equal(3000, BudgetIn(handler.Bodies[1]));
    }
}
