using System.Net;
using System.Text;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// The "Bağlantıyı sına" button, which had become worse than useless for Anthropic.
///
/// Its entire job is to catch a key that cannot work before a real conversation is spent finding
/// out. Instead it went through the transcription probe, which speaks one dialect — a bearer token
/// and nothing else. Anthropic rejects that with a 400, and the probe counts anything that is not a
/// 401 or 403 as authorised. So a wrong key, a right key and no key at all produced the same green
/// tick.
///
/// These pin the two facts that make the difference: the request has to be shaped for the provider
/// it is aimed at, and a refusal has to be reported as one.
/// </summary>
public sealed class ProviderTestButtonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-probe-{Guid.NewGuid():N}");

    public ProviderTestButtonTests() => Directory.CreateDirectory(Path.Combine(_root, "recordings"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private SettingsViewModel Model(HttpClient http, LlmProviderKind kind, string? apiKey)
    {
        var vm = new SettingsViewModel(new AppSettings(), new AppPaths(_root), http)
        {
            SelectedProvider = LlmProviders.Get(kind),
        };

        vm.LlmApiKey = apiKey ?? "";
        return vm;
    }

    /// <summary>
    /// The bug, stated as a test: an Anthropic key that the service refuses must not come back
    /// green. Before the fix every non-401 answer was treated as proof the key worked.
    /// </summary>
    [Fact]
    public async Task ARefusedAnthropicKeyIsNotReportedAsWorking()
    {
        var handler = new StubHandler(_ => Json(
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""",
            HttpStatusCode.Unauthorized));

        using var http = new HttpClient(handler);
        var vm = Model(http, LlmProviderKind.Anthropic, "yanlis-anahtar");

        await vm.TestLlmCommand.ExecuteAsync(null);

        Assert.False(vm.LlmStatusIsGood);
        Assert.NotNull(vm.LlmStatus);
    }

    /// <summary>
    /// And the reason it was refused: the request has to carry x-api-key and the version header,
    /// not a bearer token. A probe that sends the wrong shape is testing its own dialect, not the
    /// user's credentials.
    /// </summary>
    [Fact]
    public async Task TestingAnthropicSendsItsOwnHeadersRatherThanABearerToken()
    {
        var handler = new StubHandler(_ => Json("""{"data":[{"id":"claude-haiku-4-5"}]}"""));

        using var http = new HttpClient(handler);
        var vm = Model(http, LlmProviderKind.Anthropic, "sk-ant-test");

        await vm.TestLlmCommand.ExecuteAsync(null);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r =>
        {
            Assert.Null(r.Headers.Authorization);
            Assert.Equal("sk-ant-test", r.Headers.GetValues("x-api-key").Single());
            Assert.Equal(AnthropicClient.ApiVersion, r.Headers.GetValues("anthropic-version").Single());
        });
    }

    /// <summary>A working provider still reports success, and fills the model list.</summary>
    [Fact]
    public async Task AWorkingProviderIsReportedAsWorkingAndItsModelsAreOffered()
    {
        var handler = new StubHandler(_ => Json(
            """{"data":[{"id":"claude-haiku-4-5","display_name":"Claude Haiku 4.5"}]}"""));

        using var http = new HttpClient(handler);
        var vm = Model(http, LlmProviderKind.Anthropic, "sk-ant-test");

        await vm.TestLlmCommand.ExecuteAsync(null);

        Assert.True(vm.LlmStatusIsGood);
        Assert.Contains("claude-haiku-4-5", vm.DiscoveredLlmModels);
    }

    /// <summary>
    /// OpenAI and OpenRouter do take a bearer token. The fix must not break the providers that
    /// were working.
    /// </summary>
    [Theory]
    [InlineData(LlmProviderKind.OpenAi)]
    [InlineData(LlmProviderKind.OpenRouter)]
    public async Task BearerProvidersStillAuthenticateTheWayTheyExpect(LlmProviderKind kind)
    {
        var handler = new StubHandler(_ => Json("""{"data":[{"id":"bir-model"}]}"""));

        using var http = new HttpClient(handler);
        var vm = Model(http, kind, "sk-test");

        await vm.TestLlmCommand.ExecuteAsync(null);

        Assert.True(vm.LlmStatusIsGood);
        Assert.All(handler.Requests, r =>
            Assert.Equal("Bearer sk-test", r.Headers.Authorization?.ToString()));
    }

    /// <summary>
    /// An unreachable endpoint is a failure, not a pass. This is the shape of the original bug in
    /// its most general form: "the server said something" is not "the server said yes".
    /// </summary>
    [Fact]
    public async Task AnEndpointThatRefusesEverythingIsReportedAsAFailure()
    {
        var handler = new StubHandler(_ => Json("""{"error":"nope"}""", HttpStatusCode.BadRequest));

        using var http = new HttpClient(handler);
        var vm = Model(http, LlmProviderKind.Anthropic, "sk-ant-test");

        await vm.TestLlmCommand.ExecuteAsync(null);

        Assert.False(vm.LlmStatusIsGood);
    }

    /// <summary>
    /// Only the providers that publish a catalogue offer the browse button. The predicate exists
    /// and, until this test, was bound to nothing — so "shown only when relevant" was true by
    /// coincidence rather than by construction.
    /// </summary>
    [Fact]
    public void BrowsingIsOfferedExactlyForTheProvidersThatCanBeBrowsed()
    {
        using var http = new HttpClient();

        foreach (var provider in LlmProviders.All)
        {
            var vm = new SettingsViewModel(new AppSettings(), new AppPaths(_root), http)
            {
                SelectedProvider = provider,
            };

            Assert.Equal(ModelDirectory.CanFetch(provider.Kind), vm.CanBrowseModels);
        }
    }
}
