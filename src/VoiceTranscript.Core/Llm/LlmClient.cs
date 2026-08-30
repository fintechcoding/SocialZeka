using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VoiceTranscript.Core.Llm;

public sealed record LlmRequest
{
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }

    /// <summary>
    /// JSON Schema the reply must conform to.
    ///
    /// This is enforced by constrained decoding rather than by asking politely: invalid tokens
    /// are masked at every step, so the output is structurally valid whatever the model's
    /// Turkish is like. Asking for "JSON only" in the prompt is not equivalent and fails often
    /// enough to matter over hundreds of calls.
    /// </summary>
    public JsonNode? JsonSchema { get; init; }

    /// <summary>Low for extraction. Creativity here means invented evidence.</summary>
    public double Temperature { get; init; } = 0.2;

    public int MaxTokens { get; init; } = 2048;

    /// <summary>
    /// Ask the server to unload the model when the request finishes.
    ///
    /// Whisper and the analysis model cannot both be resident in 6 GB, so whichever finishes
    /// first has to let go. Only honoured by backends that support it.
    /// </summary>
    public bool UnloadAfterwards { get; init; }
}

public sealed record LlmResponse(string Content, string? FinishReason, int? PromptTokens, int? CompletionTokens)
{
    /// <summary>
    /// True when generation stopped naturally.
    ///
    /// Worth checking on every reply: a schema guarantees the shape of what was produced, not
    /// that it finished. A response cut off at the token limit is structurally valid so far and
    /// still unparseable, and the failure looks like a model bug rather than a budget one.
    /// </summary>
    public bool CompletedNormally => FinishReason is null or "stop" or "eos";
}

public interface ILlmClient
{
    LlmProviderKind Kind { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether the endpoint is reachable and has the model. Used by the settings page.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases GPU memory if the backend supports it. Best effort.</summary>
    Task UnloadAsync(string model, CancellationToken cancellationToken = default);
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Talks to anything exposing the OpenAI chat-completions API.
///
/// That covers every backend on offer — llama-server, Ollama, LM Studio and OpenRouter — so one
/// implementation serves all of them and the user can switch without anything downstream
/// noticing. Ollama gets a small amount of special handling for model lifetime, because it is
/// the only local backend that can be told to release the GPU on demand.
/// </summary>
public sealed class OpenAiCompatibleClient(
    HttpClient http,
    LlmProviderKind kind,
    string baseUrl,
    string? apiKey = null) : ILlmClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Without this every Turkish letter is written as an escape sequence: still valid JSON,
        // but six bytes instead of two. A transcript is almost entirely Turkish, so the default
        // roughly doubles the size of every request sent to a metered API.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LlmProviderKind Kind => kind;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync(request, cancellationToken);
        }
        catch (LlmException e) when (request.JsonSchema is not null && LooksLikeUnsupportedSchema(e.Message))
        {
            // Not every hosted model accepts a JSON schema, and OpenRouter routes to many of
            // them. Refusing to work at all would be the wrong response: the pipeline already
            // rejects unparseable output, and every quote is verified against the transcript
            // afterwards, so falling back to an instruction degrades gracefully rather than
            // failing shut.
            var withoutSchema = request with
            {
                JsonSchema = null,
                SystemPrompt = request.SystemPrompt +
                    "\n\nYanıtını YALNIZCA geçerli JSON olarak ver. Açıklama, başlık veya kod bloğu ekleme.",
            };

            var response = await SendAsync(withoutSchema, cancellationToken);
            return response with { Content = StripCodeFence(response.Content) };
        }
    }

    /// <summary>
    /// Whether a failure is the provider rejecting structured output rather than something real.
    ///
    /// Matched on the message because the providers disagree on status codes for this, and a
    /// wrong guess either way is cheap: retrying a genuinely broken request just fails twice.
    /// </summary>
    private static bool LooksLikeUnsupportedSchema(string message)
    {
        foreach (var marker in new[] { "response_format", "json_schema", "structured output", "schema" })
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>Removes a markdown code fence, which models add despite being told not to.</summary>
    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);

        return (closing < 0 ? body : body[..closing]).Trim();
    }

    private async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
        };

        if (request.JsonSchema is not null)
        {
            payload["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "extraction",
                    ["strict"] = true,
                    ["schema"] = request.JsonSchema.DeepClone(),
                },
            };
        }

        if (kind == LlmProviderKind.Ollama && request.UnloadAfterwards)
        {
            // Ollama-specific: unload as soon as this reply is done, so Whisper can have the GPU.
            payload["keep_alive"] = 0;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Combine(baseUrl, "chat/completions"))
        {
            Content = JsonContent.Create(payload, options: Json),
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new LlmException($"{kind} adresine ulaşılamadı ({baseUrl}): {e.Message}", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new LlmException($"{kind} {(int)response.StatusCode} döndürdü: {Truncate(body)}");

            return Parse(body);
        }
    }

    private static LlmResponse Parse(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException e)
        {
            throw new LlmException($"Yanıt JSON olarak ayrıştırılamadı: {Truncate(body)}", e);
        }

        var choice = root?["choices"]?.AsArray().FirstOrDefault()
            ?? throw new LlmException($"Yanıtta 'choices' yok: {Truncate(body)}");

        var content = choice["message"]?["content"]?.GetValue<string>() ?? "";
        var finish = choice["finish_reason"]?.GetValue<string>();
        var usage = root?["usage"];

        return new LlmResponse(
            content,
            finish,
            usage?["prompt_tokens"]?.GetValue<int>(),
            usage?["completion_tokens"]?.GetValue<int>());
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(Combine(baseUrl, "models"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Frees GPU memory. Only Ollama exposes a way to do this on demand.
    ///
    /// For llama-server the equivalent is stopping the process, which the application does
    /// itself; for cloud backends there is nothing to free.
    /// </summary>
    public async Task UnloadAsync(string model, CancellationToken cancellationToken = default)
    {
        if (kind != LlmProviderKind.Ollama) return;

        try
        {
            // The native endpoint, not the OpenAI-compatible one: an empty prompt with
            // keep_alive 0 is Ollama's documented way to evict a model.
            var root = baseUrl.Replace("/v1", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');

            using var content = JsonContent.Create(
                new JsonObject { ["model"] = model, ["keep_alive"] = 0 }, options: Json);

            using var response = await http.PostAsync($"{root}/api/generate", content, cancellationToken);
            _ = response;
        }
        catch (Exception)
        {
            // Best effort. If it fails the model is evicted by Ollama's own idle timer instead.
        }
    }

    private static string Combine(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}/{path}";

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";
}
