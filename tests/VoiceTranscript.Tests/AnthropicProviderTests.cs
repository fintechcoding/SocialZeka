using System.Net;
using System.Text;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// Anthropic is not the OpenAI API at a different hostname, and every one of these covers a way
/// that difference silently produces a rejection whose message explains none of it.
/// </summary>
public sealed class AnthropicProviderTests
{
    /// <summary>
    /// Tolerates a request with no body, unlike the one in RemoteProviderTests.
    ///
    /// The model catalogue is fetched with GET, where Content is null — reading it unconditionally
    /// throws inside the handler and surfaces as an unrelated failure.
    /// </summary>
    private sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _index;

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return responses[Math.Min(_index++, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string TextReply =
        """
        {"content":[{"type":"text","text":"merhaba"}],"stop_reason":"end_turn",
         "usage":{"input_tokens":11,"output_tokens":4}}
        """;

    private const string ToolReply =
        """
        {"content":[{"type":"tool_use","name":"extraction","input":{"taahhutler":[]}}],
         "stop_reason":"tool_use","usage":{"input_tokens":20,"output_tokens":9}}
        """;

    private static LlmRequest Request(bool withSchema = true) => new()
    {
        Model = "claude-haiku-4-5",
        SystemPrompt = "sistem",
        UserPrompt = "kullanıcı",
        JsonSchema = withSchema ? System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object"}""") : null,
    };

    private static AnthropicClient Client(HttpClient http) =>
        new(http, "https://api.anthropic.com/v1", "sk-ant-test");

    // ---- the protocol differences -------------------------------------------

    /// <summary>
    /// A bearer token is not how Anthropic authenticates, and sending one produces a 401 that
    /// reads as a bad key rather than as the wrong header.
    /// </summary>
    [Fact]
    public async Task TheKeyGoesInTheApiKeyHeaderAndTheVersionIsPinned()
    {
        var handler = new StubHandler(_ => Json(TextReply));
        using var http = new HttpClient(handler);

        await Client(http).CompleteAsync(Request(withSchema: false), TestContext.Current.CancellationToken);

        var sent = handler.Requests[0];

        Assert.Equal("sk-ant-test", sent.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicClient.ApiVersion, sent.Headers.GetValues("anthropic-version").Single());
        Assert.Null(sent.Headers.Authorization);
        Assert.EndsWith("/v1/messages", sent.RequestUri!.AbsoluteUri);
    }

    /// <summary>
    /// The system prompt is a top-level field, not a message with role "system". Sent the OpenAI
    /// way it is rejected as an invalid role, and the extraction instructions never arrive.
    /// </summary>
    [Fact]
    public async Task TheSystemPromptIsATopLevelFieldRatherThanAMessage()
    {
        var handler = new StubHandler(_ => Json(TextReply));
        using var http = new HttpClient(handler);

        await Client(http).CompleteAsync(Request(withSchema: false), TestContext.Current.CancellationToken);

        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.Bodies[0])!;

        Assert.Equal("sistem", body["system"]!.GetValue<string>());
        Assert.Single(body["messages"]!.AsArray());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    /// <summary>
    /// Structured output goes through a forced tool. Left as an option the model sometimes replies
    /// in prose about what it would have extracted, which parses as nothing at all.
    /// </summary>
    [Fact]
    public async Task ASchemaBecomesAForcedToolAndItsInputIsTheAnswer()
    {
        var handler = new StubHandler(_ => Json(ToolReply));
        using var http = new HttpClient(handler);

        var response = await Client(http).CompleteAsync(Request(), TestContext.Current.CancellationToken);

        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.Bodies[0])!;

        Assert.Equal("extraction", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("tool", body["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("extraction", body["tool_choice"]!["name"]!.GetValue<string>());

        // The tool's input is the object, and it must arrive as the content rather than as an
        // empty string taken from a text block that is not there.
        Assert.Contains("taahhutler", response.Content);
        Assert.DoesNotContain("response_format", handler.Bodies[0]);
    }

    /// <summary>
    /// stop_reason has different spellings here. Unmapped, "end_turn" reads as an abnormal stop
    /// and every successful extraction would be discarded by the caller.
    /// </summary>
    [Theory]
    [InlineData(TextReply)]
    [InlineData(ToolReply)]
    public async Task ANormalStopIsRecognisedAsOne(string reply)
    {
        var handler = new StubHandler(_ => Json(reply));
        using var http = new HttpClient(handler);

        var response = await Client(http).CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(response.CompletedNormally);
    }

    [Fact]
    public async Task RunningOutOfTokensIsNotReportedAsANormalStop()
    {
        var handler = new StubHandler(_ => Json(
            """{"content":[{"type":"text","text":"yarim"}],"stop_reason":"max_tokens"}"""));

        using var http = new HttpClient(handler);

        var response = await Client(http).CompleteAsync(
            Request(withSchema: false), TestContext.Current.CancellationToken);

        Assert.False(response.CompletedNormally);
    }

    [Fact]
    public async Task TokenCountsAreReported()
    {
        var handler = new StubHandler(_ => Json(TextReply));
        using var http = new HttpClient(handler);

        var response = await Client(http).CompleteAsync(
            Request(withSchema: false), TestContext.Current.CancellationToken);

        Assert.Equal(11, response.PromptTokens);
        Assert.Equal(4, response.CompletionTokens);
    }

    /// <summary>The useful part of a failure is nested; a raw dump buries it in envelope.</summary>
    [Fact]
    public async Task TheProvidersOwnErrorMessageIsShown()
    {
        var handler = new StubHandler(_ => Json(
            """{"type":"error","error":{"type":"invalid_request_error","message":"credit balance is too low"}}""",
            HttpStatusCode.BadRequest));

        using var http = new HttpClient(handler);

        var problem = await Assert.ThrowsAsync<LlmException>(() =>
            Client(http).CompleteAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains("credit balance is too low", problem.Message);
    }

    // ---- wiring --------------------------------------------------------------

    /// <summary>
    /// The whole point of the factory. Before it existed each call site constructed the
    /// chat-completions client directly, so choosing Anthropic would have posted an OpenAI-shaped
    /// body to /v1/chat/completions on a host that has no such route.
    /// </summary>
    [Fact]
    public void TheFactoryPicksTheProtocolTheProviderActuallySpeaks()
    {
        using var http = new HttpClient();

        Assert.IsType<AnthropicClient>(
            LlmClientFactory.Create(http, LlmProviderKind.Anthropic, "https://api.anthropic.com/v1", "k"));

        foreach (var kind in new[]
        {
            LlmProviderKind.OpenAi, LlmProviderKind.OpenRouter, LlmProviderKind.LlamaServer,
            LlmProviderKind.Ollama, LlmProviderKind.LmStudio, LlmProviderKind.OpenAiCompatible,
        })
        {
            Assert.IsType<OpenAiCompatibleClient>(
                LlmClientFactory.Create(http, kind, "https://example/v1", "k"));
        }
    }

    /// <summary>
    /// Both new providers host their own models, so they are addressed by identifier. Left out of
    /// this test the settings screen would offer a local GGUF file name to a cloud API.
    /// </summary>
    [Theory]
    [InlineData(LlmProviderKind.Anthropic)]
    [InlineData(LlmProviderKind.OpenAi)]
    public void TheNewCloudProvidersAreAddressedByModelIdentifier(LlmProviderKind kind)
    {
        var settings = new AppSettings { LlmProvider = kind, LlmRemoteModel = "bir-model" };

        Assert.True(settings.UsesRemoteModelName);
        Assert.Equal("bir-model", settings.ResolvedModelName);
        Assert.DoesNotContain(".gguf", settings.ResolvedModelName);
    }

    /// <summary>
    /// The same model is spelled differently depending on how it is reached, and offering the
    /// OpenRouter spelling to somebody using Anthropic directly produces a rejection naming
    /// neither the model nor the format.
    /// </summary>
    [Fact]
    public void ModelSuggestionsAreSpelledForTheProviderTheyAreOffs()
    {
        Assert.All(
            AppSettings.SuggestionsFor(LlmProviderKind.Anthropic),
            id => Assert.DoesNotContain("/", id));

        Assert.All(
            AppSettings.SuggestionsFor(LlmProviderKind.OpenRouter),
            id => Assert.Contains("/", id));
    }

    [Fact]
    public void AKeylessCloudProviderIsNotConsideredReachable()
    {
        Assert.False(new AppSettings { LlmProvider = LlmProviderKind.Anthropic }.LlmReachableInPrinciple);

        Assert.True(new AppSettings
        {
            LlmProvider = LlmProviderKind.Anthropic,
            LlmApiKey = "sk-ant-x",
        }.LlmReachableInPrinciple);
    }

    // ---- the model catalogue -------------------------------------------------

    [Fact]
    public async Task TheOpenRouterCatalogueIsReadWithItsPricesAndContextLengths()
    {
        var handler = new StubHandler(_ => Json(
            """
            {"data":[
              {"id":"anthropic/claude-haiku-4.5","name":"Claude Haiku 4.5",
               "context_length":200000,"pricing":{"prompt":"0.000001","completion":"0.000005"}},
              {"id":"meta/free-thing","name":"Free Thing",
               "context_length":8192,"pricing":{"prompt":"0","completion":"0"}}
            ]}
            """));

        using var http = new HttpClient(handler);

        var models = await ModelDirectory.FetchAsync(
            http, LlmProviderKind.OpenRouter, "https://openrouter.ai/api/v1", null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, models.Count);
        Assert.Equal("anthropic/claude-haiku-4.5", models[0].Id);
        Assert.Contains("200k bağlam", models[0].Detail);

        // Per million, because per token is six leading zeros and nobody compares in those.
        Assert.Contains("$1", models[0].Detail);
        Assert.Contains("ücretsiz", models[1].Detail);
    }

    /// <summary>Anthropic names the field differently, and an unread name leaves the list blank.</summary>
    [Fact]
    public async Task TheAnthropicCatalogueUsesDisplayNameAndIsAskedWithTheVersionHeader()
    {
        var handler = new StubHandler(_ => Json(
            """{"data":[{"id":"claude-haiku-4-5","display_name":"Claude Haiku 4.5"}]}"""));

        using var http = new HttpClient(handler);

        var models = await ModelDirectory.FetchAsync(
            http, LlmProviderKind.Anthropic, "https://api.anthropic.com/v1", "sk-ant-test",
            TestContext.Current.CancellationToken);

        Assert.Equal("Claude Haiku 4.5", models.Single().Name);
        Assert.Equal("sk-ant-test", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicClient.ApiVersion, handler.Requests[0].Headers.GetValues("anthropic-version").Single());
    }

    /// <summary>
    /// A refused key and an empty catalogue are different problems with different fixes, and
    /// reporting the first as the second sends somebody looking for a model that is right there.
    /// </summary>
    [Fact]
    public async Task ARefusedKeyIsReportedAsARefusedKey()
    {
        var handler = new StubHandler(_ => Json("""{"error":"nope"}""", HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);

        var problem = await Assert.ThrowsAsync<LlmException>(() => ModelDirectory.FetchAsync(
            http, LlmProviderKind.OpenAi, "https://api.openai.com/v1", "bad",
            TestContext.Current.CancellationToken));

        Assert.Contains("anahtar", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Searching has to work on the parts people remember, not only the identifier.</summary>
    [Fact]
    public void AModelIsSearchableByNameAndByPriceNotOnlyByIdentifier()
    {
        var model = new RemoteModel("anthropic/claude-haiku-4.5", "Claude Haiku 4.5", "200k bağlam · ücretsiz");

        Assert.Contains("haiku", model.Haystack, StringComparison.Ordinal);
        Assert.Contains("ücretsiz", model.Haystack, StringComparison.Ordinal);
        Assert.Contains("200k", model.Haystack, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyProvidersThatPublishACatalogueOfferBrowsing()
    {
        Assert.True(ModelDirectory.CanFetch(LlmProviderKind.OpenRouter));
        Assert.True(ModelDirectory.CanFetch(LlmProviderKind.Anthropic));
        Assert.True(ModelDirectory.CanFetch(LlmProviderKind.OpenAi));

        Assert.False(ModelDirectory.CanFetch(LlmProviderKind.LlamaServer));
        Assert.False(ModelDirectory.CanFetch(LlmProviderKind.Ollama));
    }
}
