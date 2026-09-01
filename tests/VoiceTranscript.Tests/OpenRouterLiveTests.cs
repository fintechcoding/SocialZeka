using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// The client against real hosted models, through OpenRouter, with a real (tiny) transcript.
///
/// Skipped unless VT_OPENROUTER_KEY is set: these tests spend real money and need a network.
/// They exist because provider quirks are discovered in production otherwise — a real archive
/// hit the max_tokens rename, the temperature refusal, and the verbose_json rejection one
/// evening at a time. This is the same discovery, purchased for a fraction of a cent, before
/// the user pays for it with an evening.
///
/// The assertion is deliberately the product's own bar and nothing stricter: whatever the model
/// family, the CLIENT must come back with parseable JSON that carries the commitment — via the
/// schema when the provider enforces it, via the instruction fallback (plus think-block and
/// fence stripping) when it does not.
/// </summary>
public class OpenRouterLiveTests
{
    private static string? Key => Environment.GetEnvironmentVariable("VT_OPENROUTER_KEY");

    private const string BaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>A conversation with one unmistakable commitment in it.</summary>
    private const string Transcript =
        """
        [00:04] Karşı taraf: Abi para bende hazır, merak etme.
        [00:09] Ben: Ne zaman gönderiyorsun?
        [00:12] Karşı taraf: Yarın öğlene kadar beş bin lirayı hesabına göndereceğim, söz.
        [00:18] Ben: Tamam, bekliyorum o zaman.
        """;

    private static readonly JsonNode Schema = JsonNode.Parse(
        """
        {
          "type": "object",
          "properties": {
            "taahhutler": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "kim": { "type": "string" },
                  "ne": { "type": "string" },
                  "alinti": { "type": "string" }
                },
                "required": ["kim", "ne", "alinti"],
                "additionalProperties": false
              }
            }
          },
          "required": ["taahhutler"],
          "additionalProperties": false
        }
        """)!;

    private static LlmRequest Request() => new()
    {
        Model = "",
        SystemPrompt =
            "Bir telefon görüşmesi dökümünden verilen sözleri (taahhütleri) çıkarıyorsun. "
            + "Her taahhüt için kim verdi, ne sözü verdi ve dayandığı alıntıyı döndür. "
            + "Yanıt yalnızca istenen JSON.",
        UserPrompt = Transcript,
        JsonSchema = Schema,
        MaxTokens = 2048, // düşünen modeller düşünmeye de yer ister
    };

    /// <summary>
    /// One family each: Anthropic, Qwen, DeepSeek, Google. Preference-ordered candidates,
    /// because hosted catalogues rename models under everyone; the first the catalogue actually
    /// lists is the one exercised.
    /// </summary>
    public static TheoryData<string, string[]> Families => new()
    {
        { "claude", ["anthropic/claude-haiku-4.5", "anthropic/claude-3.5-haiku", "anthropic/claude-sonnet-4.5"] },
        { "qwen", ["qwen/qwen3.5-27b", "qwen/qwen3-32b", "qwen/qwen-2.5-72b-instruct", "qwen/qwen3-30b-a3b"] },
        { "deepseek", ["deepseek/deepseek-chat-v3.1", "deepseek/deepseek-chat"] },
        { "gemini", ["google/gemini-2.5-flash", "google/gemini-2.0-flash-001"] },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public async Task AFamilyReturnsParseableCommitments(string family, string[] candidates)
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Key), "VT_OPENROUTER_KEY tanımlı değil.");

        using var http = new HttpClient();

        var model = await PickAsync(http, candidates);
        Assert.SkipWhen(model is null, $"{family}: adaylardan hiçbiri katalogda yok.");

        var client = new OpenAiCompatibleClient(http, LlmProviderKind.OpenRouter, BaseUrl, Key);

        var response = await client.CompleteAsync(Request() with { Model = model! });

        // The product's whole bar: the content parses, and the commitment is in it.
        JsonNode parsed;
        try
        {
            parsed = JsonNode.Parse(response.Content)!;
        }
        catch (JsonException e)
        {
            Assert.Fail(
                $"{family} ({model}): içerik JSON olarak ayrıştırılamadı: {e.Message}\n"
                + $"İçerik başı: {response.Content[..Math.Min(300, response.Content.Length)]}");
            return;
        }

        var commitments = parsed["taahhutler"]?.AsArray();

        Assert.True(commitments is { Count: > 0 },
            $"{family} ({model}): taahhüt listesi boş geldi. İçerik: {response.Content[..Math.Min(300, response.Content.Length)]}");

        var text = commitments!.ToJsonString();

        Assert.True(
            text.Contains("bin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("5000", StringComparison.Ordinal)
            || text.Contains("5.000", StringComparison.Ordinal),
            $"{family} ({model}): beş bin liralık söz yakalanamadı: {text}");
    }

    /// <summary>The first candidate the live catalogue actually lists.</summary>
    private static async Task<string?> PickAsync(HttpClient http, string[] candidates)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var ids = JsonNode.Parse(body)?["data"]?.AsArray()
            .Select(m => m?["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        return candidates.FirstOrDefault(c => ids.Contains(c));
    }
}
