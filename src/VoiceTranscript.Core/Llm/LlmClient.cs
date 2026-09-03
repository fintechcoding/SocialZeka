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
    /// The most this request may be given on a second attempt, when the first one spent its
    /// whole budget thinking and answered nothing. See <c>OpenAiCompatibleClient.CompleteAsync</c>.
    /// </summary>
    public int MaxTokensCeiling { get; init; } = 16_384;

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

/// <summary>What a connection test found out, in a form the settings page can word.</summary>
public sealed record LlmProbe(bool Reachable, bool Authorised, int StatusCode, string Message)
{
    public static LlmProbe Ok(int status) => new(true, true, status, "Bağlantı kuruldu.");

    public static LlmProbe FromStatus(int status) => status switch
    {
        401 or 403 => new LlmProbe(true, false, status, $"Anahtar kabul edilmedi ({status}). Anahtarı denetle."),
        404 => new LlmProbe(true, true, status, "Adres yanlış görünüyor (404): sunucu var ama bu yolu tanımıyor."),
        >= 500 => new LlmProbe(true, true, status, $"Sunucu hata verdi ({status}). Biraz sonra yeniden dene."),
        _ => new LlmProbe(true, true, status, $"Beklenmeyen yanıt ({status})."),
    };

    public static LlmProbe Unreachable(string why) => new(false, false, 0, $"Sunucuya ulaşılamadı: {why}");
}

public interface ILlmClient
{
    LlmProviderKind Kind { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether the endpoint is reachable and has the model. Used by the settings page.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The same request, with the answer kept: a refused key, a wrong address and a dead
    /// network used to collapse into one "yanıt vermedi", and the user could not tell which
    /// of the three to fix.
    /// </summary>
    async Task<LlmProbe> ProbeAsync(CancellationToken cancellationToken = default) =>
        await IsAvailableAsync(cancellationToken) ? LlmProbe.Ok(0) : new LlmProbe(false, false, 0, "Sunucu yanıt vermedi.");

    /// <summary>Releases GPU memory if the backend supports it. Best effort.</summary>
    Task UnloadAsync(string model, CancellationToken cancellationToken = default);
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// The provider's own response body, when the failure came with one.
    ///
    /// Separate from <see cref="Exception.Message"/> because the two have different readers. The
    /// message is a sentence for a person deciding whether to wait or to change a setting; this
    /// is what the retry logic reads to tell "this model wants max_completion_tokens" from a real
    /// failure. Putting a human sentence where the machine was looking silently disabled both
    /// corrective retries — every model needing the newer field simply stopped working.
    /// </summary>
    public string? Body { get; init; }
}

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

    /// <summary>How long a single completion may take before it is treated as unreachable.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync(request, cancellationToken);

            return NeedsMoreRoom(request, response)
                ? await AskAgainWithRoom(request, response, cancellationToken)
                : response;
        }
        catch (LlmException e) when (request.JsonSchema is not null && LooksLikeUnsupportedSchema(e.Message))
        {
            CoreLog.Write("llm",
                $"{kind}/{request.Model}: response_format kabul edilmedi, şemasız talimatla yeniden deneniyor");

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
    /// Whether the model ran out of budget before it said anything at all.
    ///
    /// A reasoning model spends completion tokens on its own thinking before a single visible
    /// character is emitted, and that thinking comes out of the SAME allowance as the answer. On
    /// a real call the ledger step asked for 2048 and the reply came back finished for "length"
    /// with 1787 prompt tokens, 2048 completion tokens and zero characters: the model had thought
    /// for the whole budget and never started writing.
    ///
    /// Nothing about that looks like a failure from the outside. The request succeeded, the
    /// section was skipped as "unfinished", and the conversation was recorded as analysed with
    /// no commitments and no claims in it — which reads exactly like a conversation in which
    /// nothing was promised.
    ///
    /// A cut-off answer is a different thing and is left alone: it holds real content, the
    /// caller can see it was truncated, and asking again would pay for the same tokens twice.
    /// </summary>
    private static bool NeedsMoreRoom(LlmRequest request, LlmResponse response) =>
        response.FinishReason == "length"
        && string.IsNullOrWhiteSpace(response.Content)
        && request.MaxTokens < request.MaxTokensCeiling;

    /// <summary>
    /// The same request with room to think in, asked once.
    ///
    /// Four times the budget rather than a little more: the failure says the model needs more
    /// than it was given and says nothing about how much, and a retry that lands one token short
    /// costs the whole call again. Capped, so a model that would never answer cannot be paid for
    /// indefinitely.
    /// </summary>
    private async Task<LlmResponse> AskAgainWithRoom(
        LlmRequest request, LlmResponse first, CancellationToken cancellationToken)
    {
        var room = Math.Min(request.MaxTokens * 4, request.MaxTokensCeiling);

        CoreLog.Write("llm",
            $"{kind}/{request.Model}: {request.MaxTokens} jetonun tamamı düşünmeye gitti, "
            + $"yanıt boş — {room} ile yeniden deneniyor");

        var second = await SendAsync(request with { MaxTokens = room }, cancellationToken);

        // The first answer was empty, so there is nothing to lose by preferring the second even
        // when it is truncated too — and something to gain, because a truncated answer with
        // content in it is one the caller can at least read.
        return string.IsNullOrWhiteSpace(second.Content) ? first : second;
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

    /// <summary>
    /// Removes a reasoning preamble, which thinking models put in front of their answer.
    ///
    /// Qwen3-family models (and several others served through llama-server, Ollama and
    /// OpenRouter) open with a <c>&lt;think&gt;…&lt;/think&gt;</c> block when their chat template
    /// leaves thinking on. When structured output is grammar-enforced the block never appears;
    /// on the instruction-only fallback it does, and everything before the closing tag is the
    /// model talking to itself — JSON.Parse meets an essay. An unclosed block means the whole
    /// reply was thinking and there is no answer in it at all.
    /// </summary>
    private static string StripReasoning(string content)
    {
        var trimmed = content.TrimStart();

        if (!trimmed.StartsWith("<think>", StringComparison.OrdinalIgnoreCase)) return content;

        var close = trimmed.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);

        return close < 0 ? "" : trimmed[(close + "</think>".Length)..].TrimStart();
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

    /// <summary>
    /// The name of the token-limit field this endpoint expects.
    ///
    /// OpenAI renamed it: their newer models reject "max_tokens" outright with a 400 telling the
    /// caller to send "max_completion_tokens" — a real user hit exactly that on the Sor screen.
    /// Every other OpenAI-compatible server (local runtimes, OpenRouter, Groq) still speaks the
    /// original name, and some know nothing else. So the host picks the opening bid, and the one
    /// 400 that names the other field swaps and retries once.
    /// </summary>
    private string PreferredTokenField()
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
               && uri.Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase)
            ? "max_completion_tokens"
            : "max_tokens";
    }

    private static bool WantsOtherTokenField(string body, string otherField) =>
        body.Contains("unsupported_parameter", StringComparison.OrdinalIgnoreCase)
        && body.Contains(otherField, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the provider just refused our temperature outright.
    ///
    /// The same vintage of OpenAI models that renamed the token field also stopped accepting any
    /// temperature but their default — a real archive hit it verbatim: "'temperature' does not
    /// support 0.2 with this model. Only the default (1) value is supported." The right answer is
    /// to stop sending the field, not to send 1: the point of a low temperature was determinism,
    /// and a model that refuses the field has made that decision for us.
    /// </summary>
    private static bool RefusesTemperature(string body) =>
        body.Contains("temperature", StringComparison.OrdinalIgnoreCase)
        && (body.Contains("unsupported_value", StringComparison.OrdinalIgnoreCase)
            || body.Contains("does not support", StringComparison.OrdinalIgnoreCase));

    private async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var tokenField = PreferredTokenField();
        var sendTemperature = true;

        // At most one corrective retry per rejected parameter: each 400 names exactly one
        // fault, and each correction can only be applied once. Anything else is a real error.
        for (var corrections = 0; ; corrections++)
        {
            try
            {
                return await SendOnceAsync(request, tokenField, sendTemperature, cancellationToken);
            }
            catch (LlmException e) when (corrections < 2)
            {
                var other = tokenField == "max_tokens" ? "max_completion_tokens" : "max_tokens";

                var body = e.Body ?? e.Message;

                if (WantsOtherTokenField(body, other))
                {
                    CoreLog.Write("llm",
                        $"{kind}/{request.Model}: {tokenField} reddedildi, {other} ile yeniden deneniyor");
                    tokenField = other;
                }
                else if (sendTemperature && RefusesTemperature(body))
                {
                    CoreLog.Write("llm",
                        $"{kind}/{request.Model}: temperature desteklenmiyor, alan atlanarak yeniden deneniyor");
                    sendTemperature = false;
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private async Task<LlmResponse> SendOnceAsync(
        LlmRequest request, string tokenField, bool sendTemperature, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            [tokenField] = request.MaxTokens,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
        };

        if (sendTemperature) payload["temperature"] = request.Temperature;

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

        // One request gets its own deadline, shorter than the shared client's.
        //
        // That client is configured with a ten-minute timeout because uploading an hour of audio
        // for transcription legitimately takes that long. A chat completion does not, and letting
        // it inherit ten minutes is what turned an endpoint that accepts connections but never
        // answers — a crashed local server, a proxy swallowing the request — into a stall that
        // held the processing slot while every new recording queued up behind it.
        //
        // Five minutes rather than something tight: a local model generating on the processor is
        // genuinely slow, and cutting off a real answer would be a worse failure than waiting.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, deadline.Token);
        }
        catch (HttpRequestException e)
        {
            throw new LlmException($"{kind} adresine ulaşılamadı ({baseUrl}): {e.Message}", e);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Ours, not the caller's: the endpoint took too long. Reported as a reachability
            // failure rather than a cancellation, because that is what it is — and because a
            // cancellation would be mistaken for a shutdown and put the call back in the queue to
            // be retried against the same dead endpoint on every start.
            throw new LlmException(
                $"{kind} {RequestTimeout.TotalMinutes:0} dakika içinde yanıt vermedi ({baseUrl}). "
                + "Servis çalışmıyor ya da erişilemiyor olabilir.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // The status turned into something the reader can act on.
            //
            // What stood here was the number and the first 400 characters of whatever JSON the
            // provider chose to send — accurate, and useless to the person reading it, who has to
            // decide one thing: wait, or fix something. "model overloaded" and "insufficient
            // quota" are the same shape on the wire and opposite answers in practice.
            //
            // The raw body still goes to the log, where a number is the right thing to have.
            if (!response.IsSuccessStatusCode)
            {
                CoreLog.Write("llm",
                    $"{kind}/{request.Model}: HTTP {(int)response.StatusCode} — {Truncate(body)}");

                throw new LlmException(LlmFailureText.Describe(kind, (int)response.StatusCode, body))
                {
                    Body = body,
                };
            }

            var parsed = Parse(body);

            // The traffic, described without being quoted: enough to see what was sent and what
            // shape came back, none of what anybody said. When a model misbehaves on a machine
            // far from any debugger, this line is the difference between diagnosis and guessing.
            CoreLog.Write("llm",
                $"{kind}/{request.Model}: istek {tokenField}={request.MaxTokens}"
                + $" · temperature={(sendTemperature ? request.Temperature.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) : "yok")}"
                + $" · şema={(request.JsonSchema is not null ? "var" : "yok")}"
                + $" → yanıt {parsed.Content.Length} karakter"
                + $" · bitiş={parsed.FinishReason ?? "?"}"
                + $" · jeton={parsed.PromptTokens?.ToString() ?? "?"}+{parsed.CompletionTokens?.ToString() ?? "?"}");

            return parsed;
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

        // Reasoning models think before they answer, and not every server keeps the thinking
        // out of the content field. Stripped here, centrally, so no caller ever parses an essay.
        var content = StripReasoning(choice["message"]?["content"]?.GetValue<string>() ?? "");
        var finish = choice["finish_reason"]?.GetValue<string>();
        var usage = root?["usage"];

        return new LlmResponse(
            content,
            finish,
            usage?["prompt_tokens"]?.GetValue<int>(),
            usage?["completion_tokens"]?.GetValue<int>());
    }

    /// <summary>
    /// Whether the endpoint answers.
    ///
    /// The key goes with the request. Without it this asked a hosted provider an unauthenticated
    /// question, got the 401 it deserved, and reported the service as unreachable — so a correct
    /// OpenAI or OpenRouter key looked like a broken one on both the settings screen and the
    /// status page. Local servers ignore the header, so sending it always is simpler than deciding
    /// when to.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(cancellationToken);
        return probe.Reachable && probe.Authorised && probe.StatusCode is >= 200 and < 300;
    }

    public async Task<LlmProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Combine(baseUrl, "models"));

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await http.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode
                ? LlmProbe.Ok((int)response.StatusCode)
                : LlmProbe.FromStatus((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LlmProbe.Unreachable("zaman aşımı");
        }
        catch (Exception e)
        {
            return LlmProbe.Unreachable(e.Message);
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
