using System.Net.Http;
using System.Text.Json.Nodes;

namespace VoiceTranscript.Core.Llm;

/// <summary>
/// Remaining credit, for the providers that publish it.
///
/// Only OpenRouter does, among the analysis providers this application speaks to (verified
/// against live docs, September 2026): GET /api/v1/credits returns lifetime purchases and
/// lifetime usage, and the difference is what is left. OpenAI and Anthropic offer no such
/// endpoint — for them the honest sentence is "panelden bak", and this class stays silent
/// rather than inventing a number.
/// </summary>
public static class LlmBalance
{
    /// <summary>"Kalan: $12.34" — or null when it cannot be known, which is not an error.</summary>
    public static async Task<string?> OpenRouterAsync(
        HttpClient http, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://openrouter.ai/api/v1/credits");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var data = body?["data"];

            var total = data?["total_credits"]?.GetValue<double>();
            var used = data?["total_usage"]?.GetValue<double>();

            if (total is null || used is null) return null;

            return $"Kalan: ${total - used:0.00}.";
        }
        catch (Exception)
        {
            // A balance that cannot be read is a line that does not appear. Failing the dialog
            // over a nicety would invert the feature's worth.
            return null;
        }
    }
}
