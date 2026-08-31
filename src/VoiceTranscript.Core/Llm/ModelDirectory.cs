using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceTranscript.Core.Llm;

/// <summary>One model a hosted provider offers.</summary>
/// <param name="Id">The exact string to send as the model. The only part that must be right.</param>
/// <param name="Name">Human-readable name, falling back to the identifier.</param>
/// <param name="Detail">Context length and price where the provider publishes them.</param>
public sealed record RemoteModel(string Id, string Name, string? Detail)
{
    /// <summary>Everything worth matching a search against, folded once at construction.</summary>
    public string Haystack { get; } =
        $"{Id} {Name} {Detail}".ToLower(CultureInfo.InvariantCulture);
}

/// <summary>
/// Asks a hosted provider which models it has.
///
/// Before this, choosing a remote model meant typing its identifier into a free-text box from
/// memory, with five suggestions underneath that were written once and started rotting the same
/// day. Getting it wrong is not a friendly failure either: a provider handed an unknown model
/// answers with a 404 or a 400 whose message frequently does not mention the model at all, so the
/// symptom is "analysis stopped working" and the cause is a typo.
///
/// OpenRouter alone publishes several hundred models across dozens of vendors. A list that long
/// is only usable with a search box, which is why <see cref="RemoteModel.Haystack"/> exists — the
/// filtering happens over identifier, name and description together, because people look for
/// "haiku", "ucuz", "gemini" and "128k" and only one of those is in the identifier.
/// </summary>
public static class ModelDirectory
{
    /// <summary>Whether this provider can be asked for a list at all.</summary>
    public static bool CanFetch(LlmProviderKind kind) =>
        kind is LlmProviderKind.OpenRouter
             or LlmProviderKind.Anthropic
             or LlmProviderKind.OpenAi
             or LlmProviderKind.OpenAiCompatible;

    /// <summary>
    /// Fetches the catalogue, newest and cheapest first is not attempted — the provider's own
    /// order is kept, and sorting is left to the search box.
    /// </summary>
    /// <exception cref="LlmException">The service refused, or answered with something unreadable.</exception>
    public static async Task<IReadOnlyList<RemoteModel>> FetchAsync(
        HttpClient http,
        LlmProviderKind kind,
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new LlmException("Bu sağlayıcı için önce adres girilmeli.");

        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");

        if (kind == LlmProviderKind.Anthropic)
        {
            if (!string.IsNullOrWhiteSpace(apiKey)) message.Headers.Add("x-api-key", apiKey);
            message.Headers.Add("anthropic-version", AnthropicClient.ApiVersion);
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // OpenRouter publishes its catalogue without a key, but sending one is harmless and
            // means the same code path serves every provider.
            message.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        // Short on purpose. This runs while somebody is looking at a dialog waiting for a list,
        // and a minute of nothing is indistinguishable from a hang.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, deadline.Token);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException("Model listesi zaman aşımına uğradı.");
        }
        catch (HttpRequestException e)
        {
            throw new LlmException($"Sağlayıcıya ulaşılamadı: {e.Message}", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(deadline.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                        or System.Net.HttpStatusCode.Forbidden
                        ? "Anahtar kabul edilmedi. API anahtarını kontrol et."
                        : $"Sağlayıcı {(int)response.StatusCode} döndürdü.");
            }

            return Parse(body, kind);
        }
    }

    /// <summary>
    /// Reads whichever shape the provider returned.
    ///
    /// All three wrap the list in <c>data</c>, and then disagree about everything inside it:
    /// OpenRouter carries a name, a context length and per-token pricing; Anthropic carries a
    /// display name; OpenAI carries an identifier and little else. Anything missing is simply
    /// omitted rather than filled with a placeholder, because an invented context length is worse
    /// than a blank one.
    /// </summary>
    private static IReadOnlyList<RemoteModel> Parse(string body, LlmProviderKind kind)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException e)
        {
            throw new LlmException("Model listesi okunamadı.", e);
        }

        if (root?["data"]?.AsArray() is not { } items)
            throw new LlmException("Sağlayıcı beklenen biçimde bir liste döndürmedi.");

        List<RemoteModel> models = [];

        foreach (var item in items)
        {
            var id = item?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var name = item?["display_name"]?.GetValue<string>()
                       ?? item?["name"]?.GetValue<string>()
                       ?? id;

            models.Add(new RemoteModel(id, name, DescribeModel(item, kind)));
        }

        if (models.Count == 0)
            throw new LlmException("Sağlayıcı boş bir liste döndürdü.");

        return models;
    }

    /// <summary>Context length and price, when the provider says.</summary>
    private static string? DescribeModel(JsonNode? item, LlmProviderKind kind)
    {
        List<string> parts = [];

        if (item?["context_length"]?.GetValue<long>() is { } context and > 0)
            parts.Add($"{context / 1000}k bağlam");

        // OpenRouter quotes prices in dollars per token, which at six leading zeros is unreadable.
        // Per million is the unit people actually compare in.
        if (item?["pricing"]?["prompt"]?.GetValue<string>() is { } prompt
            && double.TryParse(prompt, NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken))
        {
            parts.Add(perToken <= 0
                ? "ücretsiz"
                : $"${perToken * 1_000_000:0.##}/M giriş");
        }

        if (kind == LlmProviderKind.OpenAi
            && item?["owned_by"]?.GetValue<string>() is { Length: > 0 } owner)
        {
            parts.Add(owner);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
