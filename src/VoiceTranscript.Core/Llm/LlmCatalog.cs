namespace VoiceTranscript.Core.Llm;

public enum LlmProviderKind
{
    /// <summary>llama.cpp llama-server, started and supervised by this application. The default.</summary>
    LlamaServer,

    /// <summary>A separately installed Ollama service. Native API adds keep_alive lifetime control.</summary>
    Ollama,

    /// <summary>LM Studio local server. Convenient while experimenting with prompts.</summary>
    LmStudio,

    /// <summary>OpenRouter. Cloud: transcript-derived text leaves the machine.</summary>
    OpenRouter,

    /// <summary>Anthropic directly. Speaks /v1/messages, not chat-completions — see AnthropicClient.</summary>
    Anthropic,

    /// <summary>OpenAI directly. Chat-completions, so the ordinary client serves it.</summary>
    OpenAi,

    /// <summary>Any other endpoint speaking the OpenAI chat-completions API.</summary>
    OpenAiCompatible,

    /// <summary>
    /// No service chosen. The default, so a fresh install shows "pick one" instead of a local
    /// server the application claimed to start and never did. Appended last: the setting is
    /// stored by name, but nothing should have to depend on the order.
    /// </summary>
    None,
}

/// <summary>A configured place to send analysis requests.</summary>
public sealed record LlmProvider
{
    public required LlmProviderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string DefaultBaseUrl { get; init; }
    public required string Summary { get; init; }

    /// <summary>
    /// True when using this provider sends conversation-derived text off the machine.
    /// The UI must show this plainly next to the choice, not bury it in a help page.
    /// </summary>
    public bool SendsDataOffMachine { get; init; }

    public bool RequiresApiKey { get; init; }

    /// <summary>
    /// True when the provider can be told to release GPU memory after a request. This matters
    /// because Whisper and the analysis model cannot both be resident in 6 GB.
    /// </summary>
    public bool SupportsExplicitUnload { get; init; }

    /// <summary>True when this application starts and stops the process itself.</summary>
    public bool IsSupervisedByApp { get; init; }

    /// <summary>Where to get a key. Shown as a link beside the key field, because that is the next step.</summary>
    public string? SignupUrl { get; init; }
}

public static class LlmProviders
{
    public static IReadOnlyList<LlmProvider> All { get; } =
    [
        new()
        {
            Kind = LlmProviderKind.None,
            DisplayName = "Seçilmedi",
            DefaultBaseUrl = "",
            Summary = "Özet ve defter için bir servis seç: bu makinede llama-server, Ollama ya da LM Studio; "
                    + "bulutta OpenRouter, OpenAI ya da Anthropic. Seçilmeden görüşmeler yalnızca yazıya dökülür.",
        },
        new()
        {
            Kind = LlmProviderKind.LlamaServer,
            DisplayName = "llama-server (yerel)",
            DefaultBaseUrl = "http://127.0.0.1:8080/v1",
            IsSupervisedByApp = true,
            SupportsExplicitUnload = true,
            // Said plainly: nothing in the application launches llama-server. The earlier text
            // promised "uygulama kendi başlatıp kapatır", and a user who believed it waited for a
            // server that never came.
            Summary = "Ayrıca kurulup elle başlatılması gerekir (127.0.0.1:8080); uygulama başlatmaz. "
                    + "Model dosyasını sen seçersin. Hiçbir veri dışarı çıkmaz.",
        },
        new()
        {
            Kind = LlmProviderKind.Ollama,
            DisplayName = "Ollama (yerel)",
            DefaultBaseUrl = "http://127.0.0.1:11434",
            SupportsExplicitUnload = true,
            Summary = "Ayrıca kurulması gerekir. Model yönetimi daha kolay, hız yaklaşık %10 daha "
                    + "düşük. Hiçbir veri dışarı çıkmaz.",
        },
        new()
        {
            Kind = LlmProviderKind.LmStudio,
            DisplayName = "LM Studio (yerel)",
            DefaultBaseUrl = "http://127.0.0.1:1234/v1",
            Summary = "Prompt denemeleri için pratik. Arayüzlü bir uygulamaya bağımlı olduğu için "
                    + "sürekli kullanım yerine geliştirme aşamasında önerilir.",
        },
        new()
        {
            Kind = LlmProviderKind.OpenRouter,
            DisplayName = "OpenRouter (bulut)",
            SignupUrl = "https://openrouter.ai/keys",
            DefaultBaseUrl = "https://openrouter.ai/api/v1",
            SendsDataOffMachine = true,
            RequiresApiKey = true,
            Summary = "Çok daha güçlü modellere erişim verir. Karşılığında görüşme metinleri "
                    + "OpenRouter üzerinden seçtiğin modelin sağlayıcısına gider.",
        },
        new()
        {
            Kind = LlmProviderKind.Anthropic,
            DisplayName = "Anthropic / Claude (bulut)",
            SignupUrl = "https://console.anthropic.com/settings/keys",
            DefaultBaseUrl = "https://api.anthropic.com/v1",
            SendsDataOffMachine = true,
            RequiresApiKey = true,
            Summary = "Kendi Anthropic anahtarınla doğrudan Claude. Türkçe'de ve şemaya uyan "
                    + "çıktıda güçlü. Görüşme metinleri Anthropic'e gider.",
        },
        new()
        {
            Kind = LlmProviderKind.OpenAi,
            DisplayName = "OpenAI / GPT (bulut)",
            SignupUrl = "https://platform.openai.com/api-keys",
            DefaultBaseUrl = "https://api.openai.com/v1",
            SendsDataOffMachine = true,
            RequiresApiKey = true,
            Summary = "Kendi OpenAI anahtarınla doğrudan GPT modelleri. Görüşme metinleri "
                    + "OpenAI'a gider.",
        },
        new()
        {
            Kind = LlmProviderKind.OpenAiCompatible,
            DisplayName = "Diğer (OpenAI uyumlu)",
            DefaultBaseUrl = "",
            RequiresApiKey = true,
            Summary = "Kendi sunucun veya başka bir sağlayıcı. Adresi ve anahtarı sen girersin.",
        },
    ];

    public static LlmProvider Get(LlmProviderKind kind) => All.First(p => p.Kind == kind);
}

/// <summary>A local GGUF model offered in the picker, with the VRAM arithmetic already done.</summary>
public sealed record LocalLlmModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Hugging Face repository holding the GGUF files.</summary>
    public required string Repository { get; init; }

    /// <summary>The specific quantised file to fetch.</summary>
    public required string FileName { get; init; }

    public required string Quantisation { get; init; }
    public double WeightsGb { get; init; }

    /// <summary>KV cache size at <see cref="ContextTokens"/> with q8_0 cache quantisation.</summary>
    public double KvCacheGb { get; init; }

    public int ContextTokens { get; init; }
    public double TotalGb => Math.Round(WeightsGb + KvCacheGb, 2);

    public required string Summary { get; init; }
    public string? Warning { get; init; }
    public bool IsRecommended { get; init; }

    /// <summary>Estimated generation speed on a 6 GB laptop GPU. Extrapolated, not measured.</summary>
    public string SpeedHint { get; init; } = "";
}

public static class LocalLlmCatalog
{
    /// <summary>
    /// Usable VRAM on the target 6 GB card once Windows compositing and the CUDA context are
    /// accounted for. Anything above this needs a clean desktop and some luck.
    /// </summary>
    public const double UsableVramGb = 4.65;

    public const string DefaultModelId = "qwen3.5-4b-q6k";

    public static IReadOnlyList<LocalLlmModel> All { get; } =
    [
        new()
        {
            Id = DefaultModelId,
            DisplayName = "Qwen3.5 4B",
            Repository = "unsloth/Qwen3.5-4B-GGUF",
            FileName = "Qwen3.5-4B-Q6_K.gguf",
            Quantisation = "Q6_K",
            WeightsGb = 3.53,
            KvCacheGb = 0.57,
            ContextTokens = 32_768,
            IsRecommended = true,
            SpeedHint = "~40-46 token/sn",
            Summary = "Varsayılan. 32 bin token bağlamla tamamen ekran kartına sığıyor ve Türkçede "
                    + "kelime başına en az token harcayan tokenizer'a sahip.",
        },
        new()
        {
            Id = "qwen3.5-4b-iq4xs",
            DisplayName = "Qwen3.5 4B (düşük VRAM)",
            Repository = "unsloth/Qwen3.5-4B-GGUF",
            FileName = "Qwen3.5-4B-IQ4_XS.gguf",
            Quantisation = "IQ4_XS",
            WeightsGb = 2.48,
            KvCacheGb = 0.29,
            ContextTokens = 16_384,
            SpeedHint = "~55 token/sn",
            Summary = "Ekran kartı başka işle meşgulken veya 4 GB altı boş alan kaldığında.",
        },
        new()
        {
            Id = "qwen3.5-9b-q3km",
            DisplayName = "Qwen3.5 9B",
            Repository = "unsloth/Qwen3.5-9B-GGUF",
            FileName = "Qwen3.5-9B-Q3_K_M.gguf",
            Quantisation = "Q3_K_M",
            WeightsGb = 4.67,
            KvCacheGb = 0.29,
            ContextTokens = 16_384,
            SpeedHint = "~30 token/sn",
            Warning = "6 GB'a ancak masaüstü tertemizken sığar. Ayrıca Q3_K sıkıştırması ölçülebilir "
                    + "hasar veriyor: 4B modeli Q6_K'da çalıştırmak bundan daha iyi sonuç verir.",
            Summary = "Daha büyük model, ama bu kartta daha ağır sıkıştırma gerektiriyor.",
        },
        new()
        {
            Id = "gemma-4-12b-q4km",
            DisplayName = "Gemma 4 12B (kısmi CPU)",
            Repository = "unsloth/gemma-4-12B-it-GGUF",
            FileName = "gemma-4-12B-it-Q4_K_M.gguf",
            Quantisation = "Q4_K_M",
            WeightsGb = 7.12,
            KvCacheGb = 0.79,
            ContextTokens = 16_384,
            SpeedHint = "~6-12 token/sn",
            Warning = "Ekran kartına sığmaz, katmanların bir kısmı işlemcide çalışır. Etkileşimli "
                    + "kullanım için çok yavaş.",
            Summary = "Kalite tavanı. Gece boyunca çalışan toplu analiz için mantıklı, anlık kullanım için değil.",
        },
        new()
        {
            Id = "trendyol-llm-8b-t1",
            DisplayName = "Trendyol LLM 8B T1 (Türkçe)",
            Repository = "Trendyol/Trendyol-LLM-8B-T1",
            FileName = "",
            Quantisation = "Q4_K_M",
            WeightsGb = 4.9,
            KvCacheGb = 0.6,
            ContextTokens = 16_384,
            SpeedHint = "~30 token/sn",
            Warning = "GGUF dönüşümü hazır gelmiyor olabilir. Ayrıca yayımlanmış bir Türkçe "
                    + "karşılaştırma skoru bulunamadı — kendi kayıtlarınla denemeden varsayılan yapma.",
            Summary = "Qwen3-8B tabanlı Türkçe uyarlaması, Apache-2.0. Varsayılan model Türkçede "
                    + "yetersiz kalırsa ilk denenecek alternatif.",
        },
    ];

    public static LocalLlmModel Default => Get(DefaultModelId);

    public static LocalLlmModel Get(string id) =>
        All.FirstOrDefault(m => m.Id == id)
        ?? throw new KeyNotFoundException($"Unknown LLM model id: {id}");

    /// <summary>Models that fit entirely in the given VRAM budget.</summary>
    public static IEnumerable<LocalLlmModel> FittingIn(double vramGb = UsableVramGb) =>
        All.Where(m => m.TotalGb <= vramGb);
}
