using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceTranscript.Core.Asr;

/// <summary>
/// Asks a configured transcription service whether it actually works.
///
/// This exists because of how the failure otherwise arrives. A wrong key, a base URL with a
/// missing <c>/v1</c>, a model name the provider renamed — none of these are visible until a
/// real conversation has already been recorded and the upload fails, at which point the
/// conversation is still on disk but the user has lost their confidence in the thing. One button
/// that says "this works" before it matters is worth more than any amount of error handling
/// afterwards.
///
/// Everything here is read-only: it lists models and reads balances, and uploads nothing.
/// </summary>
public sealed class SttProbe(HttpClient http)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Checks reachability, the key, and whether the chosen model exists.</summary>
    public async Task<SttTestResult> TestAsync(SttEndpoint endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint.ResolvedBaseUrl))
            return new SttTestResult { Message = "Adres boş." };

        if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
            return new SttTestResult { Message = "API anahtarı girilmemiş." };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var response = await GetAsync(endpoint, "models", timeout.Token);
            stopwatch.Stop();

            var latency = (int)stopwatch.ElapsedMilliseconds;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new SttTestResult
                {
                    Reachable = true,
                    LatencyMs = latency,
                    Message = $"Sunucuya ulaşıldı ama anahtar kabul edilmedi ({(int)response.StatusCode}).",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                // Some providers do not expose a model list at all. That is not a failure of the
                // key — but it is not proof of the key either, and it used to be reported as one.
                // A 404 from a base address missing "/v1" came back as "bağlandı", and the first
                // real upload then failed against an endpoint the test had just blessed.
                //
                // Reachable, yes. Authorised is left unclaimed: nothing here established it.
                return new SttTestResult
                {
                    Reachable = true,
                    Authorised = false,
                    LatencyMs = latency,
                    Message =
                        $"Sunucu ulaşılabilir. Model listesi alınamadı ({(int)response.StatusCode}); " +
                        "bu sağlayıcı liste yayınlamıyor olabilir.",
                };
            }

            var models = TranscriptionFirst(ParseModelList(await response.Content.ReadAsStringAsync(ct)));
            var wanted = endpoint.ResolvedModel;
            var hasModel = models.Count == 0 || models.Contains(wanted, StringComparer.OrdinalIgnoreCase);

            var message = models.Count == 0
                ? $"Bağlantı çalışıyor ({latency} ms). Sağlayıcı model listesi vermiyor."
                : hasModel
                    ? $"Bağlantı çalışıyor ({latency} ms). {wanted} kullanılabilir, {models.Count} model listelendi."
                    : $"Bağlantı çalışıyor ({latency} ms) ama {wanted} listede yok. " +
                      $"Örnekler: {string.Join(", ", models.Take(4))}";

            return new SttTestResult
            {
                Reachable = true,
                Authorised = true,
                ModelAvailable = hasModel,
                LatencyMs = latency,
                Models = models,
                Message = message,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SttTestResult { Message = $"Sunucu {Timeout.TotalSeconds:0} saniyede yanıt vermedi." };
        }
        catch (HttpRequestException e)
        {
            return new SttTestResult { Message = $"Sunucuya ulaşılamadı: {e.Message}" };
        }
    }

    /// <summary>
    /// Puts the models that can actually transcribe at the top of the list.
    ///
    /// The endpoint being asked is <c>/v1/models</c>, which answers with everything the provider
    /// hosts. On OpenAI that is around a hundred entries, almost all of them chat models, and the
    /// four that transcribe are scattered through it alphabetically. Somebody opening the dropdown
    /// to pick a transcription model was being handed a list in which the right answers were
    /// invisible.
    ///
    /// Reordered rather than filtered. Matching on the name is a heuristic, and a heuristic that
    /// hides things is one that will eventually hide the model somebody needs — Deepgram's are
    /// called "nova", and a provider added tomorrow may call its own something else again. Sorting
    /// puts the likely answers first and costs nothing when the guess is wrong.
    /// </summary>
    public static IReadOnlyList<string> TranscriptionFirst(IReadOnlyList<string> models)
    {
        static bool Transcribes(string id) =>
            id.Contains("whisper", StringComparison.OrdinalIgnoreCase)
            || id.Contains("transcribe", StringComparison.OrdinalIgnoreCase);

        return
        [
            .. models
                .OrderByDescending(Transcribes)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Reads how much credit is left, where the provider publishes it.
    ///
    /// Deliberately honest about the gaps. OpenAI and Groq have no balance endpoint, and inventing
    /// a number from usage we happen to have seen would be worse than saying so — the one thing
    /// a credit display must never do is tell somebody they have money left when they do not.
    /// </summary>
    public async Task<SttBalance> BalanceAsync(SttEndpoint endpoint, CancellationToken ct = default)
    {
        var probe = endpoint.Provider.Balance;

        if (probe == BalanceProbe.None)
        {
            return new SttBalance
            {
                Supported = false,
                Message = $"{endpoint.Provider.DisplayName} kalan bakiyeyi API üzerinden bildirmiyor.",
            };
        }

        if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
            return new SttBalance { Supported = true, Message = "Önce API anahtarı gerekiyor." };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            return probe switch
            {
                BalanceProbe.OpenRouterKey => await OpenRouterBalanceAsync(endpoint, timeout.Token),
                BalanceProbe.ElevenLabsSubscription => await ElevenLabsBalanceAsync(endpoint, timeout.Token),
                BalanceProbe.DeepgramBalance => await DeepgramBalanceAsync(endpoint, timeout.Token),
                _ => new SttBalance { Supported = false, Message = "Desteklenmiyor." },
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SttBalance { Supported = true, Message = "Bakiye sorgusu zaman aşımına uğradı." };
        }
        catch (HttpRequestException e)
        {
            return new SttBalance { Supported = true, Message = $"Bakiye alınamadı: {e.Message}" };
        }
        catch (JsonException)
        {
            return new SttBalance { Supported = true, Message = "Bakiye yanıtı anlaşılamadı." };
        }
    }

    // ---- per-provider balance ----------------------------------------------

    private async Task<SttBalance> OpenRouterBalanceAsync(SttEndpoint endpoint, CancellationToken ct)
    {
        using var response = await GetAsync(endpoint, "key", ct);
        if (!response.IsSuccessStatusCode)
            return new SttBalance { Supported = true, Message = $"Bakiye alınamadı ({(int)response.StatusCode})." };

        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["data"];
        if (data is null) return new SttBalance { Supported = true, Message = "Bakiye bilgisi gelmedi." };

        var usage = data["usage"]?.GetValue<double>() ?? 0;
        var limit = data["limit"]?.GetValue<double?>();

        if (limit is null or 0)
        {
            return new SttBalance
            {
                Supported = true,
                Message = $"Sınırsız anahtar. Şimdiye kadarki kullanım: ${usage:0.00}.",
            };
        }

        var remaining = Math.Max(0, limit.Value - usage);

        return new SttBalance
        {
            Supported = true,
            UsedFraction = Math.Clamp(usage / limit.Value, 0, 1),
            Message = $"Kalan ${remaining:0.00} / ${limit.Value:0.00}.",
        };
    }

    private async Task<SttBalance> ElevenLabsBalanceAsync(SttEndpoint endpoint, CancellationToken ct)
    {
        // ElevenLabs authenticates with its own header rather than a bearer token.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.ResolvedBaseUrl}/user/subscription");
        request.Headers.Add("xi-api-key", endpoint.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new SttBalance { Supported = true, Message = $"Kota alınamadı ({(int)response.StatusCode})." };

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));

        var used = json?["character_count"]?.GetValue<double>() ?? 0;
        var limit = json?["character_limit"]?.GetValue<double>() ?? 0;

        if (limit <= 0) return new SttBalance { Supported = true, Message = "Kota bilgisi gelmedi." };

        var remaining = Math.Max(0, limit - used);

        return new SttBalance
        {
            Supported = true,
            UsedFraction = Math.Clamp(used / limit, 0, 1),
            Message = $"Kalan {remaining:N0} / {limit:N0} karakter.",
        };
    }

    private async Task<SttBalance> DeepgramBalanceAsync(SttEndpoint endpoint, CancellationToken ct)
    {
        // Deepgram uses a Token scheme and scopes balances under a project.
        var projects = await DeepgramGetAsync(endpoint, "projects", ct);
        var projectId = projects?["projects"]?.AsArray().FirstOrDefault()?["project_id"]?.GetValue<string>();

        if (projectId is null)
            return new SttBalance { Supported = true, Message = "Proje bulunamadı; anahtar geçersiz olabilir." };

        var balances = await DeepgramGetAsync(endpoint, $"projects/{projectId}/balances", ct);
        var first = balances?["balances"]?.AsArray().FirstOrDefault();

        if (first is null) return new SttBalance { Supported = true, Message = "Bakiye kaydı yok." };

        var amount = first["amount"]?.GetValue<double>() ?? 0;
        var units = first["units"]?.GetValue<string>() ?? "";

        return new SttBalance
        {
            Supported = true,
            Message = units.Contains("usd", StringComparison.OrdinalIgnoreCase)
                ? $"Kalan ${amount:0.00}."
                : $"Kalan {amount:0.##} {units}.",
        };
    }

    private async Task<JsonNode?> DeepgramGetAsync(SttEndpoint endpoint, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.ResolvedBaseUrl}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", endpoint.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    // ---- shared -------------------------------------------------------------

    private async Task<HttpResponseMessage> GetAsync(SttEndpoint endpoint, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.ResolvedBaseUrl}/{path}");
        Authorise(request, endpoint);

        return await http.SendAsync(request, ct);
    }

    /// <summary>
    /// Attaches the key the way this provider expects it.
    ///
    /// The balance probes already knew that ElevenLabs reads <c>xi-api-key</c> and Deepgram a
    /// <c>Token</c> scheme, but the connection test sent <c>Bearer</c> to everyone — so a valid
    /// ElevenLabs or Deepgram key was reported as "anahtar reddedildi" the moment the user
    /// pressed Sına, and the endpoint they had just paid for looked broken.
    /// </summary>
    private static void Authorise(HttpRequestMessage request, SttEndpoint endpoint)
    {
        switch (endpoint.Kind)
        {
            case "elevenlabs":
                request.Headers.Add("xi-api-key", endpoint.ApiKey);
                break;

            case "deepgram":
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", endpoint.ApiKey);
                break;

            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                break;
        }
    }

    /// <summary>
    /// Pulls model identifiers out of a listing.
    ///
    /// Both shapes are in the wild: OpenAI wraps them in <c>data</c>, a few return a bare array.
    /// A provider whose listing we cannot read is treated as publishing nothing, which downgrades
    /// the report rather than failing it.
    /// </summary>
    public static List<string> ParseModelList(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);

            // Order matters: indexing a JsonArray by name throws rather than returning null, so
            // the bare-array shape has to be recognised before "data" is looked for.
            var array = root as JsonArray ?? (root as JsonObject)?["data"] as JsonArray;

            if (array is null) return [];

            return [.. array
                // ElevenLabs keys its models by model_id rather than id.
                .Select(item => item?["id"]?.GetValue<string>() ?? item?["model_id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }
}
