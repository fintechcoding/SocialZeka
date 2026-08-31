using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// The token-limit field, which OpenAI renamed out from under everyone.
///
/// Their newer models reject "max_tokens" with a 400 that names "max_completion_tokens"; every
/// other OpenAI-compatible server — local runtimes, OpenRouter, Groq — still speaks the original,
/// and some speak only it. A real user hit the 400 on the Sor screen, verbatim: "Unsupported
/// parameter: 'max_tokens' is not supported with this model."
/// </summary>
public class TokenFieldTests
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

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Reply =
        """{"choices":[{"message":{"content":"tamam"},"finish_reason":"stop"}],"usage":{}}""";

    private const string Rejection =
        """
        {"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model.
         Use 'max_completion_tokens' instead.","type":"invalid_request_error",
         "param":"max_tokens","code":"unsupported_parameter"}}
        """;

    private static LlmRequest Request() => new()
    {
        Model = "gpt-test",
        SystemPrompt = "sistem",
        UserPrompt = "soru",
        MaxTokens = 700,
    };

    private static OpenAiCompatibleClient Client(StubHandler handler, string baseUrl) =>
        new(new HttpClient(handler), LlmProviderKind.OpenAi, baseUrl, "sk-test");

    [Fact]
    public async Task OpenAiGetsTheFieldTheirNewModelsDemand()
    {
        var handler = new StubHandler(_ => Json(Reply));

        await Client(handler, "https://api.openai.com/v1").CompleteAsync(Request());

        var payload = JsonNode.Parse(handler.Bodies[0])!;

        Assert.Equal(700, (int)payload["max_completion_tokens"]!);
        Assert.Null(payload["max_tokens"]);
    }

    /// <summary>
    /// Everyone else keeps the original name — a local server that has never heard of the new
    /// field must not be sent it as the opening bid.
    /// </summary>
    [Fact]
    public async Task OtherServersStillGetMaxTokens()
    {
        var handler = new StubHandler(_ => Json(Reply));

        await Client(handler, "http://localhost:11434/v1").CompleteAsync(Request());

        var payload = JsonNode.Parse(handler.Bodies[0])!;

        Assert.Equal(700, (int)payload["max_tokens"]!);
        Assert.Null(payload["max_completion_tokens"]);
    }

    /// <summary>
    /// A proxy fronting OpenAI models under another host answers the real 400; the client
    /// swaps the field and tries once more instead of surfacing the provider's lecture.
    /// </summary>
    [Fact]
    public async Task TheRenameRejectionIsAnsweredWithOneRetry()
    {
        var handler = new StubHandler(
            _ => Json(Rejection, HttpStatusCode.BadRequest),
            _ => Json(Reply));

        var response = await Client(handler, "https://gateway.example.com/v1").CompleteAsync(Request());

        Assert.Equal("tamam", response.Content);
        Assert.Equal(2, handler.Bodies.Count);

        var second = JsonNode.Parse(handler.Bodies[1])!;
        Assert.Equal(700, (int)second["max_completion_tokens"]!);
        Assert.Null(second["max_tokens"]);
    }

    /// <summary>An unrelated 400 is not retried — it would fail identically, a second time.</summary>
    [Fact]
    public async Task AnOrdinaryBadRequestIsNotRetried()
    {
        var handler = new StubHandler(
            _ => Json("""{"error":{"message":"model not found","code":"model_not_found"}}""",
                HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<LlmException>(
            () => Client(handler, "https://api.openai.com/v1").CompleteAsync(Request()));

        Assert.Single(handler.Bodies);
    }
}
