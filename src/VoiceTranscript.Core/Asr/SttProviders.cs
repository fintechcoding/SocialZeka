using System.Text.Json.Serialization;

namespace VoiceTranscript.Core.Asr;

/// <summary>How a provider reports what is left to spend, if it reports anything at all.</summary>
public enum BalanceProbe
{
    /// <summary>The provider has no endpoint for this. Said plainly rather than guessed at.</summary>
    None,

    /// <summary>OpenRouter-style: GET {base}/key returns limit, usage and remaining.</summary>
    OpenRouterKey,

    /// <summary>ElevenLabs: GET /v1/user/subscription returns characters used against a limit.</summary>
    ElevenLabsSubscription,

    /// <summary>Deepgram: list projects, then read the balance of the first one.</summary>
    DeepgramBalance,
}

/// <summary>
/// A hosted transcription service the application knows how to talk to.
///
/// The catalogue exists so that adding a provider is picking a name from a list rather than
/// looking up a base URL and a model identifier in somebody's documentation. Everything here is
/// a default that the user can override; nothing is hard-wired.
/// </summary>
public sealed record SttProviderInfo
{
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string BaseUrl { get; init; }
    public required string DefaultModel { get; init; }

    /// <summary>Models known to work, offered before the live list arrives.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>One sentence on what this service is and what it costs.</summary>
    public required string Summary { get; init; }

    public BalanceProbe Balance { get; init; } = BalanceProbe.None;

    /// <summary>
    /// Whether the service speaks the OpenAI audio-transcriptions shape.
    ///
    /// Everything the worker can currently upload to does. A provider with its own request
    /// format is listed here only once an engine exists for it, because offering a choice that
    /// cannot work is worse than not listing it.
    /// </summary>
    public bool OpenAiCompatible { get; init; } = true;

    /// <summary>Where to get a key. Shown as a link, because that is the actual next step.</summary>
    public string? SignupUrl { get; init; }
}

public static class SttProviderCatalog
{
    /// <summary>
    /// The providers offered by name.
    ///
    /// All of these copy the OpenAI request shape, which is what makes one uploader reach all of
    /// them. That is also why the list can grow without touching the worker: a service that
    /// implements <c>POST /audio/transcriptions</c> works today under "Özel adres".
    /// </summary>
    public static IReadOnlyList<SttProviderInfo> All { get; } =
    [
        new()
        {
            Kind = "openai",
            DisplayName = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            DefaultModel = "whisper-1",
            // whisper-1 only. The gpt-4o transcribe models used to be listed here and produced a
            // verified 400 on a real archive: they refuse verbose_json, which is the only format
            // that carries word timestamps — and a transcript without timestamps cannot back a
            // single quote in the ledger. The catalogue already dropped them; offering them here
            // was the settings screen contradicting the product.
            Models = ["whisper-1"],
            Summary =
                "Whisper large-v3 ile aynı aile. Dakika başına ücretli. Kalan bakiye için API " +
                "uç noktası sunmuyor, bakiyeyi kendi panelinden görürsün.",
            Balance = BalanceProbe.None,
            SignupUrl = "https://platform.openai.com/api-keys",
        },
        new()
        {
            Kind = "groq",
            DisplayName = "Groq",
            BaseUrl = "https://api.groq.com/openai/v1",
            DefaultModel = "whisper-large-v3-turbo",
            Models = ["whisper-large-v3-turbo", "whisper-large-v3"],
            Summary =
                "Aynı Whisper ağırlıkları, belirgin şekilde hızlı ve ucuz. Kalan kota için uç " +
                "nokta yok; sınırlar istek başlıklarında bildiriliyor.",
            Balance = BalanceProbe.None,
            SignupUrl = "https://console.groq.com/keys",
        },

        // Both verified against live documentation (September 2026): OpenAI request shape,
        // verbose_json accepted, segment/word timestamps returned — the whole contract.
        new()
        {
            Kind = "together",
            DisplayName = "Together AI",
            BaseUrl = "https://api.together.xyz/v1",
            DefaultModel = "openai/whisper-large-v3",
            Models = ["openai/whisper-large-v3"],
            Summary =
                "Whisper large-v3, saati ~$0.09. Ayırt edici özelliği limitleri: 4 saate kadar " +
                "kayıt, 80 MB yükleme — uzun görüşmeler için biçilmiş. Bakiye ucu yok.",
            Balance = BalanceProbe.None,
            SignupUrl = "https://api.together.ai/settings/api-keys",
        },
        new()
        {
            Kind = "openrouter",
            DisplayName = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            DefaultModel = "openai/whisper-1",
            Models = ["openai/whisper-1"],
            Summary =
                "Tek anahtarla birden çok sağlayıcıya kapı; verbose_json'u destekleyen modellere " +
                "aynen geçirir. Bakiye ucu sunan tek uyumlu sağlayıcı — kalan kredi burada görünür.",
            Balance = BalanceProbe.OpenRouterKey,
            SignupUrl = "https://openrouter.ai/keys",
        },
        new()
        {
            Kind = "elevenlabs",
            DisplayName = "ElevenLabs Scribe",
            BaseUrl = "https://api.elevenlabs.io/v1",
            DefaultModel = "scribe_v1",
            Models = ["scribe_v1"],
            Summary =
                "Türkçede iddialı bir model. Karakter kotası üzerinden çalışır ve kalan kotayı " +
                "API'den bildirir, böylece bitmeden görebilirsin.",
            Balance = BalanceProbe.ElevenLabsSubscription,
            SignupUrl = "https://elevenlabs.io/app/settings/api-keys",
        },
        new()
        {
            Kind = "deepgram",
            DisplayName = "Deepgram",
            BaseUrl = "https://api.deepgram.com/v1",
            DefaultModel = "nova-2",
            Models = ["nova-2", "nova-3"],
            Summary =
                "Kalan bakiyeyi para birimiyle bildiren tek sağlayıcı. İstek biçimi OpenAI'den " +
                "farklı olduğu için yükleme bu sürümde desteklenmiyor; yalnızca bakiye izlenir.",
            Balance = BalanceProbe.DeepgramBalance,
            OpenAiCompatible = false,
            SignupUrl = "https://console.deepgram.com/",
        },
        new()
        {
            Kind = "openrouter",
            DisplayName = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            DefaultModel = "openai/whisper-1",
            Summary =
                "Tek anahtarla birçok sağlayıcıya erişim. Kalan krediyi API'den bildirir. " +
                "Ses desteği sunucudan sunucuya değiştiği için modeli sınayarak doğrula.",
            Balance = BalanceProbe.OpenRouterKey,
            SignupUrl = "https://openrouter.ai/keys",
        },
        new()
        {
            Kind = "custom",
            DisplayName = "Özel adres",
            BaseUrl = "",
            DefaultModel = "whisper-1",
            Summary =
                "OpenAI'nin /audio/transcriptions biçimini uygulayan herhangi bir sunucu: " +
                "kendi makinende çalıştırdığın bir whisper sunucusu da olabilir.",
            Balance = BalanceProbe.None,
        },
    ];

    public static SttProviderInfo Find(string kind) =>
        All.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase))
        ?? All[^1];
}

/// <summary>
/// One configured transcription service.
///
/// Several can be configured at once and ordered. The point is not novelty: a hosted service
/// runs out of credit, rate-limits, or has an outage on the evening a conversation happens, and
/// a recorder that silently fails on that evening has failed at the only job it has. With a
/// second endpoint configured, the call is transcribed by whichever one answers.
/// </summary>
public sealed record SttEndpoint
{
    /// <summary>Stable identity, so reordering and renaming do not lose the key.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Kind { get; init; } = "openai";

    /// <summary>What the user called it. Defaults to the provider name.</summary>
    public string Name { get; init; } = "";

    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>Disabled endpoints stay configured but are skipped. Useful while a key is renewed.</summary>
    public bool Enabled { get; init; } = true;

    [JsonIgnore]
    public SttProviderInfo Provider => SttProviderCatalog.Find(Kind);

    [JsonIgnore]
    public string ResolvedName => string.IsNullOrWhiteSpace(Name) ? Provider.DisplayName : Name;

    [JsonIgnore]
    public string ResolvedBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl) ? Provider.BaseUrl : BaseUrl.TrimEnd('/');

    /// <summary>
    /// Models that speak the endpoint but cannot return word timestamps.
    ///
    /// They were once offered by the settings screen, so real settings files carry them — and a
    /// saved choice keeps failing long after the list is fixed. Verified against the live API on
    /// 2026-08-31: both reject verbose_json ("Use 'json' or 'text' instead"), and without
    /// verbose_json there are no word times, and without word times no quote in the ledger can be
    /// played. Coerced here so every existing installation heals itself on the next run.
    /// </summary>
    private static readonly string[] NoWordTimestamps = ["gpt-4o-transcribe", "gpt-4o-mini-transcribe"];

    [JsonIgnore]
    public string ResolvedModel =>
        string.IsNullOrWhiteSpace(Model) ? Provider.DefaultModel
        : NoWordTimestamps.Contains(Model.Trim(), StringComparer.OrdinalIgnoreCase) ? "whisper-1"
        : Model;

    /// <summary>Whether this entry has everything it needs to be tried.</summary>
    [JsonIgnore]
    public bool IsUsable =>
        Enabled
        && Provider.OpenAiCompatible
        && !string.IsNullOrWhiteSpace(ResolvedBaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ResolvedModel);

    /// <summary>The single string the Python worker expects: base URL, key and model.</summary>
    public string ToModelRef() => $"{ResolvedBaseUrl}|{ApiKey}|{ResolvedModel}";

    public static SttEndpoint FromProvider(SttProviderInfo provider) => new()
    {
        Kind = provider.Kind,
        Name = provider.DisplayName,
        BaseUrl = provider.BaseUrl,
        Model = provider.DefaultModel,
    };
}

/// <summary>What a connection test found.</summary>
public sealed record SttTestResult
{
    public bool Reachable { get; init; }
    public bool Authorised { get; init; }
    public bool ModelAvailable { get; init; }
    public int LatencyMs { get; init; }

    /// <summary>Models the service says it has. Empty when it does not publish a list.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    public required string Message { get; init; }

    public bool IsHealthy => Reachable && Authorised;
}

/// <summary>What is left to spend, when the provider will say.</summary>
public sealed record SttBalance
{
    public bool Supported { get; init; }
    public required string Message { get; init; }

    /// <summary>0-1 where a limit is known, so a bar can be drawn. Null when it is not.</summary>
    public double? UsedFraction { get; init; }

    /// <summary>True once little enough is left that it is worth saying so.</summary>
    public bool IsLow => UsedFraction is >= 0.9;
}
