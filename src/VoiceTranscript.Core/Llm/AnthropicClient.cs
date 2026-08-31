using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VoiceTranscript.Core.Llm;

/// <summary>
/// Talks to Anthropic's own API.
///
/// A separate implementation rather than another endpoint behind the OpenAI-compatible client,
/// because the two are not the same protocol wearing different hostnames. Anthropic posts to
/// <c>/v1/messages</c>, authenticates with <c>x-api-key</c> instead of a bearer token, requires a
/// version header, carries the system prompt as a top-level field rather than as a message, and
/// returns an array of content blocks rather than a choices list. Pointing the existing client at
/// it produces a 400 whose message is about none of that.
///
/// Reaching Claude through OpenRouter was already possible and remains a reasonable choice. This
/// exists because somebody who has an Anthropic key should be able to use it: routing through a
/// third party to spend it means a second account, a second balance, and conversation text passing
/// through one more company on its way.
///
/// Structured output goes through a tool rather than a prompt instruction. Anthropic has no
/// <c>response_format</c>, and asking politely for JSON is not equivalent — it fails often enough
/// to matter over hundreds of calls. A single tool with the schema as its input, plus a forced
/// tool choice, is the supported way to get an object back with its shape guaranteed.
/// </summary>
public sealed class AnthropicClient(
    HttpClient http,
    string baseUrl,
    string? apiKey = null) : ILlmClient
{
    /// <summary>
    /// The API version this client is written against.
    ///
    /// Anthropic dates its versions and holds them stable, so pinning is the supported way to use
    /// the API rather than a shortcut: an unpinned client silently changes behaviour underneath a
    /// running installation, which for an application that analyses recordings would show up as
    /// extraction quality drifting for no reason anyone could trace.
    /// </summary>
    public const string ApiVersion = "2023-06-01";

    /// <summary>Name of the tool used to carry structured output. Never shown to the user.</summary>
    private const string ExtractionTool = "extraction";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Turkish is most of what this sends. Without relaxed escaping every accented letter
        // becomes a six-byte escape sequence, which roughly doubles the size of every request
        // against a metered API.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LlmProviderKind Kind => LlmProviderKind.Anthropic;

    public async Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["system"] = request.SystemPrompt,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
        };

        if (request.JsonSchema is not null)
        {
            payload["tools"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = ExtractionTool,
                    ["description"] = "Konuşmadan çıkarılan bilgileri verilen şemaya göre döndürür.",
                    ["input_schema"] = request.JsonSchema.DeepClone(),
                });

            // Forced, not offered. Left to its own judgement the model sometimes answers in prose
            // about what it would have extracted, which parses as nothing at all.
            payload["tool_choice"] = new JsonObject
            {
                ["type"] = "tool",
                ["name"] = ExtractionTool,
            };
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Combine(baseUrl, "messages"))
        {
            Content = JsonContent.Create(payload, options: Json),
        };

        if (!string.IsNullOrWhiteSpace(apiKey)) message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", ApiVersion);

        // Its own deadline, shorter than the shared client's ten minutes — that allowance exists
        // for uploading an hour of audio, and letting a chat completion inherit it is what turns
        // an endpoint that accepts connections but never answers into a stall holding the
        // processing slot while recordings queue behind it.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OpenAiCompatibleClient.RequestTimeout);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, deadline.Token);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(
                $"Anthropic {OpenAiCompatibleClient.RequestTimeout.TotalMinutes:0} dakika içinde yanıt vermedi.");
        }
        catch (HttpRequestException e)
        {
            throw new LlmException($"Anthropic'e ulaşılamadı: {e.Message}", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            if (!response.IsSuccessStatusCode)
                throw new LlmException($"Anthropic {(int)response.StatusCode} döndürdü: {Describe(body)}");

            return Parse(body);
        }
    }

    /// <summary>
    /// Pulls the reply out of the content blocks.
    ///
    /// A reply is a list rather than a string, and which block matters depends on how the request
    /// was made: a schema request comes back as a tool call whose input is the object, and a plain
    /// one as text. Taking the first block regardless would return an empty string for every
    /// extraction, which downstream reads as a model that found nothing.
    /// </summary>
    private static LlmResponse Parse(string body)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException e)
        {
            throw new LlmException("Anthropic okunamayan bir yanıt döndürdü.", e);
        }

        var blocks = root?["content"]?.AsArray();
        string? content = null;

        if (blocks is not null)
        {
            foreach (var block in blocks)
            {
                var type = block?["type"]?.GetValue<string>();

                if (type == "tool_use" && block?["input"] is { } input)
                {
                    content = input.ToJsonString(Json);
                    break;
                }

                if (type == "text") content ??= block?["text"]?.GetValue<string>();
            }
        }

        if (content is null)
            throw new LlmException("Anthropic boş bir yanıt döndürdü.");

        var usage = root?["usage"];

        return new LlmResponse(
            content,
            MapStopReason(root?["stop_reason"]?.GetValue<string>()),
            usage?["input_tokens"]?.GetValue<int>(),
            usage?["output_tokens"]?.GetValue<int>());
    }

    /// <summary>
    /// Translates Anthropic's stop reasons into the ones <see cref="LlmResponse"/> understands.
    ///
    /// Worth doing rather than passing through: <c>CompletedNormally</c> decides whether a reply
    /// is trusted, and an unmapped "end_turn" reads as an abnormal stop — so every successful
    /// extraction would be thrown away.
    /// </summary>
    private static string? MapStopReason(string? reason) => reason switch
    {
        "end_turn" or "tool_use" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        _ => reason,
    };

    /// <summary>Whether the key works and the service answers. Used by the settings page.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, Combine(baseUrl, "models"));

            if (!string.IsNullOrWhiteSpace(apiKey)) message.Headers.Add("x-api-key", apiKey);
            message.Headers.Add("anthropic-version", ApiVersion);

            using var response = await http.SendAsync(message, cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Nothing to unload: the model runs on somebody else's hardware.</summary>
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Turns an error body into something worth showing.
    ///
    /// The interesting part is nested under <c>error.message</c>, and the rest is envelope. A raw
    /// dump would put a wall of JSON in a message box where "credit balance is too low" was the
    /// whole content.
    /// </summary>
    private static string Describe(string body)
    {
        try
        {
            var message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (JsonException)
        {
            // Not JSON. Fall through to the truncated body, which is better than nothing.
        }

        return body.Length > 300 ? body[..300] + "…" : body;
    }

    private static string Combine(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
