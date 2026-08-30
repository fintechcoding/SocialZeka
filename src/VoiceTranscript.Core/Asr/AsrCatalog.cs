namespace VoiceTranscript.Core.Asr;

/// <summary>Transcription backend. Each one is implemented in the Python worker.</summary>
public enum AsrEngineKind
{
    /// <summary>faster-whisper on CTranslate2. Fastest CUDA path, and the default.</summary>
    FasterWhisper,

    /// <summary>whisper.cpp. Slower on CUDA, but runs acceptably on CPU and on non-NVIDIA GPUs.</summary>
    WhisperCpp,

    /// <summary>Vosk. Tiny and CPU-only; useful as a last resort, not for quality.</summary>
    Vosk,

    /// <summary>
    /// A hosted API speaking the OpenAI audio-transcriptions shape.
    ///
    /// The audio itself is uploaded, which is a different proposition from anything else here:
    /// a recording carries voice identity and background, not just words. Offered because a
    /// machine without a usable GPU has no other route to good Turkish, but never a default.
    /// </summary>
    CloudOpenAi,
}

public enum ComputePrecision
{
    Float16,
    Int8Float16,
    Int8,
}

/// <summary>
/// Measured Turkish word error rates, lower is better.
///
/// Source: the ysdede Turkish ASR leaderboard (huggingface.co/spaces/ysdede/turkish_asr_leaderboard),
/// float16, WhisperX backend, NVIDIA L4. These are the only public numbers that actually measure
/// Turkish — the Open ASR Leaderboard multilingual track excludes it entirely, so any
/// "multilingual average" quoted elsewhere is not a Turkish proxy.
///
/// Null means the leaderboard has no entry for that pairing, not that the model failed.
/// </summary>
public sealed record TurkishWer(
    double? MediaSpeech,
    double? TurkishVoiceDataset,
    double? CommonVoice17,
    double? MedicalAudio = null)
{
    /// <summary>Mean of whatever datasets have a figure. Used only for ordering the picker.</summary>
    public double? Average
    {
        get
        {
            double[] values = [.. new[] { MediaSpeech, TurkishVoiceDataset, CommonVoice17, MedicalAudio }
                .Where(v => v.HasValue)
                .Select(v => v!.Value)];
            return values.Length == 0 ? null : values.Average();
        }
    }
}

/// <summary>One selectable transcription model, as presented in the settings UI.</summary>
public sealed record AsrModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AsrEngineKind Engine { get; init; }

    /// <summary>
    /// What the engine is asked to load. For faster-whisper this is either a short alias that
    /// resolves to an official CTranslate2 conversion, or a full Hugging Face repository id.
    /// </summary>
    public required string ModelRef { get; init; }

    public TurkishWer? Wer { get; init; }

    /// <summary>Approximate download size in gigabytes.</summary>
    public double DownloadGb { get; init; }

    /// <summary>Approximate VRAM at the given precision, in gigabytes. Estimate, not a measurement.</summary>
    public double VramGb { get; init; }

    public ComputePrecision Precision { get; init; } = ComputePrecision.Int8Float16;

    /// <summary>Rough speed multiplier versus real time on a mid-range laptop GPU.</summary>
    public string SpeedHint { get; init; } = "";

    /// <summary>Shown under the model name in the picker. Plain language, one line.</summary>
    public required string Summary { get; init; }

    /// <summary>Set when the model is known to be a poor choice, with the reason.</summary>
    public string? Warning { get; init; }

    public bool RunsOnCpu { get; init; }

    /// <summary>
    /// True when the exact repository id still has to be confirmed before a download is attempted.
    /// The UI must not offer a one-click download for these.
    /// </summary>
    public bool RepositoryUnconfirmed { get; init; }

    public bool IsRecommended { get; init; }

    /// <summary>True when using this model uploads the recording off the machine.</summary>
    public bool SendsAudioOffMachine => Engine == AsrEngineKind.CloudOpenAi;

    /// <summary>Endpoint used when this is a hosted model.</summary>
    public string? DefaultBaseUrl { get; init; }
}

/// <summary>
/// The curated model list. Deliberately short: every entry earns its place, and each one carries
/// the numbers that justify choosing it, so the choice is informed rather than a guess.
/// </summary>
public static class AsrCatalog
{
    public const string DefaultModelId = "faster-whisper-large-v3-turbo";
    public const string DevelopmentModelId = "faster-whisper-small-cpu";

    public static IReadOnlyList<AsrModel> All { get; } =
    [
        new()
        {
            Id = DefaultModelId,
            DisplayName = "Whisper large-v3-turbo",
            Engine = AsrEngineKind.FasterWhisper,
            ModelRef = "large-v3-turbo",
            Wer = new TurkishWer(12.17, 11.13, null, 6.18),
            DownloadGb = 1.6,
            VramGb = 1.8,
            SpeedHint = "40x gerçek zaman",
            IsRecommended = true,
            Summary = "Varsayılan. Türkçede large-v3 ile arasında 0.24 WER fark var ama 3 kat hızlı "
                    + "ve yarı yarıya az VRAM kullanıyor.",
        },
        new()
        {
            Id = "faster-whisper-large-v3",
            DisplayName = "Whisper large-v3",
            Engine = AsrEngineKind.FasterWhisper,
            ModelRef = "large-v3",
            Wer = new TurkishWer(11.93, 10.67, 9.53, 6.03),
            DownloadGb = 3.1,
            VramGb = 3.1,
            SpeedHint = "10x gerçek zaman",
            Summary = "En doğru genel model. Turbo'dan çok az daha iyi, belirgin şekilde yavaş. "
                    + "Zor kayıtlarda tekrar işlemek için.",
        },
        new()
        {
            Id = "faster-whisper-medium",
            DisplayName = "Whisper medium",
            Engine = AsrEngineKind.FasterWhisper,
            ModelRef = "medium",
            Wer = new TurkishWer(16.65, 14.91, 15.22),
            DownloadGb = 0.8,
            VramGb = 0.9,
            SpeedHint = "17x gerçek zaman",
            Warning = "Türkçede turbo'ya göre yaklaşık %37 daha fazla hata yapıyor, üstelik turbo "
                    + "daha az VRAM kullanıyor. VRAM kazanmak için buraya düşmenin anlamı yok.",
            Summary = "Küçük ve hızlı, ama doğruluk kaybı büyük.",
        },
        new()
        {
            Id = DevelopmentModelId,
            DisplayName = "Whisper small (CPU)",
            Engine = AsrEngineKind.FasterWhisper,
            ModelRef = "small",
            Wer = new TurkishWer(null, null, 24.22),
            DownloadGb = 0.25,
            VramGb = 0,
            Precision = ComputePrecision.Int8,
            RunsOnCpu = true,
            SpeedHint = "CPU'da 2-4x gerçek zaman",
            Warning = "Doğruluğu gerçek kullanım için yetersiz.",
            Summary = "Ekran kartı olmayan makinede boru hattını test etmek için. Üretimde kullanma.",
        },
        new()
        {
            Id = "faster-whisper-itu-mainframe-tr",
            DisplayName = "ITU Mainframe (Türkçe)",
            Engine = AsrEngineKind.FasterWhisper,
            ModelRef = "RsGoksel/ITU_Mainframe",
            Wer = new TurkishWer(9.02, 11.92, 8.73, 6.05),
            DownloadGb = 1.6,
            VramGb = 1.8,
            SpeedHint = "40x gerçek zaman",
            RepositoryUnconfirmed = true,
            Summary = "İki veri setinde turbo'yu geçen tek Türkçe uyarlaması (9.02 ve 8.73 WER). "
                    + "Buna karşılık başka setlerde geride kalıyor, yani kesin bir üstünlük değil.",
            Warning = "Depo adresi henüz doğrulanmadı ve CTranslate2 dönüşümü gerekebilir. "
                    + "Kendi kayıtlarınla turbo ile karşılaştırmadan varsayılan yapma.",
        },
        new()
        {
            Id = "faster-whisper-selimc-turbo-tr",
            DisplayName = "selimc turbo-turkish",
            Engine = AsrEngineKind.FasterWhisper,
            ModelRef = "selimc/whisper-large-v3-turbo-turkish",
            Wer = new TurkishWer(20.71, 19.10, null, 8.85),
            DownloadGb = 1.6,
            VramGb = 1.8,
            SpeedHint = "43x gerçek zaman",
            Warning = "Arama sonuçlarında en çok çıkan Türkçe model, ama ölçümlerde vanilla "
                    + "turbo'nun neredeyse iki katı hata yapıyor (20.71 vs 12.17). Common Voice'a "
                    + "aşırı uydurulmuş. Sadece karşılaştırma amaçlı listede.",
            Summary = "Popüler ama ölçülen performansı zayıf.",
        },
        new()
        {
            Id = "cloud-openai-whisper",
            DisplayName = "OpenAI Whisper API",
            Engine = AsrEngineKind.CloudOpenAi,
            ModelRef = "whisper-1",
            DefaultBaseUrl = "https://api.openai.com/v1",
            Wer = new TurkishWer(11.93, 10.67, 9.53, 6.03),
            DownloadGb = 0,
            VramGb = 0,
            RunsOnCpu = true,
            SpeedHint = "yükleme hızına bağlı",
            Summary = "Ses OpenAI'ye yüklenir, metin geri gelir. Ekran kartı gerekmez, model "
                    + "indirilmez. Çalıştırdığı model large-v3 ile aynı ailedendir.",
            Warning = "Görüşme SESİ makineden çıkar — sadece yazısı değil. Bu, yerel çalıştırmaya "
                    + "göre farklı bir karardır. Ayrıca 25 MB dosya sınırı vardır; uzun aramalar "
                    + "sıkıştırılıp gerekirse parçalara bölünerek gönderilir.",
        },
        new()
        {
            Id = "cloud-groq-turbo",
            DisplayName = "Groq (whisper large-v3-turbo)",
            Engine = AsrEngineKind.CloudOpenAi,
            ModelRef = "whisper-large-v3-turbo",
            DefaultBaseUrl = "https://api.groq.com/openai/v1",
            Wer = new TurkishWer(12.17, 11.13, null, 6.18),
            DownloadGb = 0,
            VramGb = 0,
            RunsOnCpu = true,
            SpeedHint = "çok hızlı",
            Summary = "Aynı OpenAI arayüzü, farklı sağlayıcı. Yerelde çalıştırdığımız modelin "
                    + "aynısını barındırır, yani doğruluk beklentisi de aynıdır — kazancı hızdır.",
            Warning = "Görüşme sesi makineden çıkar. Doğruluk yerel turbo ile aynı olduğu için, "
                    + "ekran kartın çalışıyorsa bunu kullanmanın bir doğruluk kazancı yoktur.",
        },
        new()
        {
            Id = "whispercpp-large-v3-turbo",
            DisplayName = "Whisper large-v3-turbo (whisper.cpp)",
            Engine = AsrEngineKind.WhisperCpp,
            ModelRef = "ggml-large-v3-turbo-q5_0.bin",
            Wer = new TurkishWer(12.17, 11.13, null, 6.18),
            DownloadGb = 0.6,
            VramGb = 1.5,
            RunsOnCpu = true,
            SpeedHint = "CUDA'da 15x, CPU'da 1-2x",
            Summary = "Aynı model, farklı motor. CTranslate2 kurulumu bozulursa veya NVIDIA "
                    + "olmayan bir makinede çalıştırmak gerekirse yedek yol.",
        },
    ];

    public static AsrModel Default => Get(DefaultModelId);

    public static AsrModel Get(string id) =>
        All.FirstOrDefault(m => m.Id == id)
        ?? throw new KeyNotFoundException($"Unknown ASR model id: {id}");

    public static bool TryGet(string id, out AsrModel model)
    {
        var found = All.FirstOrDefault(m => m.Id == id);
        model = found!;
        return found is not null;
    }

    /// <summary>Models that can produce a usable transcript without a CUDA device.</summary>
    public static IEnumerable<AsrModel> CpuCapable => All.Where(m => m.RunsOnCpu);

    /// <summary>Models whose weights plus runtime overhead plausibly fit the given VRAM budget.</summary>
    public static IEnumerable<AsrModel> FittingIn(double vramGb) =>
        All.Where(m => m.RunsOnCpu || m.VramGb <= vramGb);
}
