using System.Net;
using System.Text;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// Using a hosted provider instead of a local server has to work without a local server being
/// installed at all. These cover the parts that would otherwise fail quietly.
/// </summary>
public sealed class RemoteProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-rem-{Guid.NewGuid():N}");

    public RemoteProviderTests() => Directory.CreateDirectory(Path.Combine(_root, "recordings"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppPaths Paths() => new(_root);

    private static AppSettings OpenRouter(string? model = "qwen/qwen3-235b-a22b-instruct") => new()
    {
        LlmProvider = LlmProviderKind.OpenRouter,
        LlmApiKey = "sk-or-test",
        LlmRemoteModel = model,
    };

    /// <summary>
    /// The bug this covers: the local catalogue holds GGUF file names, and sending
    /// "Qwen3.5-4B-Q6_K.gguf" to a hosted API is rejected for reasons that never mention the
    /// model, so the analysis silently never runs.
    /// </summary>
    [Fact]
    public void ARemoteProviderIsGivenItsOwnModelIdentifier()
    {
        var settings = OpenRouter();

        Assert.Equal("qwen/qwen3-235b-a22b-instruct", settings.ResolvedModelName);
        Assert.DoesNotContain(".gguf", settings.ResolvedModelName);
    }

    [Fact]
    public void ALocalProviderStillGetsTheCatalogueFileName()
    {
        var settings = new AppSettings { LlmProvider = LlmProviderKind.LlamaServer };

        Assert.EndsWith(".gguf", settings.ResolvedModelName);
    }

    [Fact]
    public void OllamaIsAddressedByTag()
    {
        var settings = new AppSettings { LlmProvider = LlmProviderKind.Ollama, LlmModelId = "qwen3.5-4b-q6k" };

        Assert.Equal("qwen3.5-4b-q6k", settings.ResolvedModelName);
    }

    [Fact]
    public void OnlyHostedProvidersNeedAModelIdentifier()
    {
        Assert.True(OpenRouter().UsesRemoteModelName);
        Assert.True(new AppSettings { LlmProvider = LlmProviderKind.OpenAiCompatible }.UsesRemoteModelName);

        Assert.False(new AppSettings { LlmProvider = LlmProviderKind.LlamaServer }.UsesRemoteModelName);
        Assert.False(new AppSettings { LlmProvider = LlmProviderKind.Ollama }.UsesRemoteModelName);
        Assert.False(new AppSettings { LlmProvider = LlmProviderKind.LmStudio }.UsesRemoteModelName);
    }

    /// <summary>Saving a hosted provider with no model would produce requests that always fail.</summary>
    [Fact]
    public void AHostedProviderWithNoModelIsRejected()
    {
        var problems = OpenRouter(model: null).Validate(Paths());

        Assert.Contains(problems, p => p.Contains("model adı"));
    }

    [Fact]
    public void AProperlyConfiguredHostedProviderIsAccepted()
        => Assert.Empty(OpenRouter().Validate(Paths()));

    [Fact]
    public void AGenericEndpointNeedsItsAddress()
    {
        var settings = new AppSettings
        {
            LlmProvider = LlmProviderKind.OpenAiCompatible,
            LlmApiKey = "key",
            LlmRemoteModel = "some-model",
        };

        Assert.Contains(settings.Validate(Paths()), p => p.Contains("adres"));
    }

    [Fact]
    public void UsingAHostedProviderIsReportedAsLeavingTheMachine()
        => Assert.True(OpenRouter().AnythingLeavesTheMachine);

    [Fact]
    public void TheSuggestedModelsAreProviderIdentifiersNotFileNames()
    {
        Assert.NotEmpty(AppSettings.RemoteModelSuggestions);

        Assert.All(AppSettings.RemoteModelSuggestions, m =>
        {
            Assert.Contains('/', m);
            Assert.DoesNotContain(".gguf", m);
        });
    }

    [Fact]
    public void TheRemoteModelIsPersisted()
    {
        var paths = Paths();
        paths.EnsureCreated();

        OpenRouter().Save(paths.SettingsFile);

        Assert.Equal("qwen/qwen3-235b-a22b-instruct", AppSettings.Load(paths.SettingsFile).LlmRemoteModel);
    }

    // ---- transport ----------------------------------------------------------

    private sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _index;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            var responder = responses[Math.Min(_index++, responses.Length - 1)];
            return responder(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string GoodReply =
        """{"choices":[{"message":{"content":"{\"taahhutler\":[]}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

    private static LlmRequest Request(bool withSchema = true) => new()
    {
        Model = "qwen/qwen3-235b-a22b-instruct",
        SystemPrompt = "sistem",
        UserPrompt = "kullanıcı",
        JsonSchema = withSchema ? System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object"}""") : null,
    };

    [Fact]
    public async Task TheApiKeyIsSentAsABearerToken()
    {
        string? auth = null;
        var handler = new StubHandler(r =>
        {
            auth = r.Headers.Authorization?.ToString();
            return Json(GoodReply);
        });

        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenRouter, "https://openrouter.ai/api/v1", "sk-or-test");

        await client.CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer sk-or-test", auth);
    }

    /// <summary>
    /// OpenRouter routes to many models and not all accept a JSON schema. Failing shut would
    /// mean analysis silently never runs; the pipeline rejects unparseable output anyway, and
    /// every quote is verified against the transcript afterwards.
    /// </summary>
    [Fact]
    public async Task AModelThatRejectsSchemasFallsBackToAnInstruction()
    {
        var handler = new StubHandler(
            _ => Json("""{"error":{"message":"response_format is not supported by this model"}}""", HttpStatusCode.BadRequest),
            _ => Json(GoodReply));

        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenRouter, "https://openrouter.ai/api/v1", "k");

        var response = await client.CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("response_format", handler.Bodies[0]);
        Assert.DoesNotContain("response_format", handler.Bodies[1]);
        Assert.Contains("YALNIZCA geçerli JSON", handler.Bodies[1]);
        Assert.Contains("taahhutler", response.Content);
    }

    /// <summary>A genuine failure must not be retried forever or dressed up as success.</summary>
    [Fact]
    public async Task AnUnrelatedFailureIsReportedRatherThanRetried()
    {
        var handler = new StubHandler(_ => Json("""{"error":{"message":"insufficient credits"}}""", HttpStatusCode.PaymentRequired));

        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenRouter, "https://openrouter.ai/api/v1", "k");

        var error = await Assert.ThrowsAsync<LlmException>(
            () => client.CompleteAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Single(handler.Bodies);
        Assert.Contains("insufficient credits", error.Message);
    }

    /// <summary>Models wrap JSON in a markdown fence despite being told not to.</summary>
    [Fact]
    public async Task ACodeFenceAroundTheFallbackReplyIsStripped()
    {
        var fenced =
            """{"choices":[{"message":{"content":"```json\n{\"taahhutler\":[]}\n```"},"finish_reason":"stop"}]}""";

        var handler = new StubHandler(
            _ => Json("""{"error":{"message":"json_schema unsupported"}}""", HttpStatusCode.BadRequest),
            _ => Json(fenced));

        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenRouter, "https://openrouter.ai/api/v1", "k");

        var response = await client.CompleteAsync(Request(), TestContext.Current.CancellationToken);

        Assert.StartsWith("{", response.Content);
        Assert.DoesNotContain("```", response.Content);
    }

    /// <summary>There is no GPU to release when the model runs on somebody else's hardware.</summary>
    [Fact]
    public async Task UnloadingIsANoOpForAHostedProvider()
    {
        var handler = new StubHandler(_ => Json(GoodReply));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenRouter, "https://openrouter.ai/api/v1", "k");

        await client.UnloadAsync("any", TestContext.Current.CancellationToken);

        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task OllamaIsToldToReleaseTheModelWhenAsked()
    {
        var handler = new StubHandler(_ => Json(GoodReply));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleClient(http, LlmProviderKind.Ollama, "http://127.0.0.1:11434/v1");

        await client.CompleteAsync(Request(withSchema: false) with { UnloadAfterwards = true },
            TestContext.Current.CancellationToken);

        Assert.Contains("keep_alive", handler.Bodies[0]);
    }
}
