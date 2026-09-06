using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceTranscript.Core.Asr;

/// <summary>
/// Wire format between the application and the Python transcription worker.
///
/// The worker runs one job per process: it is started, handed a single JSON request on stdin,
/// writes newline-delimited JSON events to stdout and exits. Process exit is the only mechanism
/// that reliably returns every byte of VRAM to the driver, and it also means a CUDA context is
/// never held across a machine suspend, which is otherwise unrecoverable.
///
/// Parsing lives here, separate from process management, so it can be tested against recorded
/// worker output without launching anything.
/// </summary>
public static class WorkerProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses one stdout line. Returns null for blank lines and for anything that is not valid
    /// JSON — a stray print or a warning from a dependency must not take down a running job.
    /// </summary>
    public static WorkerEvent? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (!document.RootElement.TryGetProperty("type", out var typeElement)) return null;

            return typeElement.GetString() switch
            {
                "hello" => trimmed.Deserialize<WorkerHello>(),
                "progress" => trimmed.Deserialize<WorkerProgress>(),
                "result" => trimmed.Deserialize<WorkerResult>(),
                "error" => trimmed.Deserialize<WorkerFailure>(),
                "downloaded" => trimmed.Deserialize<WorkerDownloaded>(),
                "selftest" => trimmed.Deserialize<WorkerSelfTest>(),
                "voiceprint" => trimmed.Deserialize<WorkerVoiceprint>(),
                "prosody" => trimmed.Deserialize<WorkerProsody>(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Deserialize<T>(this string json) where T : WorkerEvent
        => JsonSerializer.Deserialize<T>(json, Json);

    public static string SerialiseRequest(TranscriptionRequest request)
        => JsonSerializer.Serialize(request, Json);

    /// <summary>Any request the worker understands, in the same snake_case shape.</summary>
    public static string Serialise<T>(T request) => JsonSerializer.Serialize(request, Json);
}

public abstract class WorkerEvent
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("id")] public string? Id { get; init; }
}

/// <summary>Capability report from `vt_worker probe`, used to populate the settings UI.</summary>
public sealed class WorkerHello : WorkerEvent
{
    public string? Python { get; init; }
    public List<EngineAvailability> Engines { get; init; } = [];
    public CudaReport? Cuda { get; init; }

    /// <summary>
    /// Models whose weights are already on disk.
    ///
    /// Shown so the user is not told a model is ready and then left waiting on a multi-gigabyte
    /// download during their first real call.
    /// </summary>
    public List<string> DownloadedModels { get; init; } = [];
}

public sealed class EngineAvailability
{
    public string Name { get; init; } = "";
    public bool Available { get; init; }
    public string? Version { get; init; }
    public string Detail { get; init; } = "";
}

public sealed class CudaReport
{
    public bool Available { get; init; }
    public int DeviceCount { get; init; }
    public string? Ctranslate2Version { get; init; }

    /// <summary>
    /// CUDA runtime DLLs the worker could not load. Since CTranslate2 4.6.3 the only one that
    /// matters is cublas64_12.dll: cuDNN was dropped as a dependency, so any advice to install
    /// it — including the text still in the faster-whisper README — applies to older releases.
    /// </summary>
    public List<string>? MissingDlls { get; init; }

    public string? Hint { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Whether the GPU can really be used, as opposed to merely existing.
    ///
    /// <see cref="Available"/> comes from the device count, which the *driver* answers. It says
    /// 1 on any machine with a working NVIDIA card whether or not cuBLAS — the library the
    /// matrix maths actually runs through — can be loaded. A machine missing cublas64_12.dll
    /// therefore reports CUDA as ready, loads the model onto the card without complaint, and
    /// dies partway through the first encode.
    ///
    /// That failure arrives after the call has ended, when the recording is the only copy of the
    /// conversation left. So the two questions are kept apart.
    /// </summary>
    public bool Usable { get; init; }

    /// <summary>Cards the driver reports, whether or not CUDA is usable. From nvidia-smi.</summary>
    public List<CudaDevice>? Devices { get; init; }

    /// <summary>Which card was chosen, named for display — "RTX 4050 Laptop GPU (6 GB)".</summary>
    public string? SelectedName { get; init; }

    public int? SelectedIndex { get; init; }
}

/// <summary>One graphics card, as the driver names it.</summary>
public sealed class CudaDevice
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public int TotalMemoryMb { get; init; }
}

/// <summary>Result of fetching model weights.</summary>
public sealed class WorkerDownloaded : WorkerEvent
{
    public string Repository { get; init; } = "";
    public string Path { get; init; } = "";
    public double SizeMb { get; init; }
}

/// <summary>
/// Result of loading a model and running it once.
///
/// Establishes that the weights are intact, the device is reachable and the chain executes.
/// It says nothing about Turkish accuracy — that can only be measured against real calls the
/// user has corrected by hand, and the note field says so in as many words.
/// </summary>
public sealed class WorkerSelfTest : WorkerEvent
{
    public string Engine { get; init; } = "";
    public string ModelRef { get; init; } = "";
    public string Repository { get; init; } = "";
    /// <summary>The device the model was really built on — cuda or cpu, never "auto".</summary>
    public string Device { get; init; } = "";

    /// <summary>
    /// The precision in use, which is itself evidence.
    ///
    /// int8_float16 is a GPU compute type; CTranslate2 refuses it on a processor. Seeing it here
    /// settles the question a flat graph in Task Manager cannot: Windows does not show CUDA work
    /// in the default GPU panels at all.
    /// </summary>
    public string ComputeType { get; init; } = "";

    /// <summary>What was asked for, when that differs from what happened.</summary>
    public string RequestedDevice { get; init; } = "";

    public double LoadSeconds { get; init; }
    public double TranscribeSeconds { get; init; }

    /// <summary>How many seconds of audio are processed per second of wall clock.</summary>
    public double SpeedFactor { get; init; }

    /// <summary>
    /// Text the model produced from a clip containing no speech.
    ///
    /// Not a failure of the installation, but worth surfacing: it is exactly why the recorder
    /// runs with voice-activity filtering and hallucination suppression turned on.
    /// </summary>
    public List<string> HallucinatedOnSilence { get; init; } = [];

    public string Note { get; init; } = "";

    /// <summary>Where it ran, in words rather than a device string.</summary>
    public string Where => Device switch
    {
        "cuda" => $"Ekran kartında çalıştı ({ComputeType})",
        "cpu" when RequestedDevice == "cuda" => "İşlemcide çalıştı — ekran kartı kullanılamadı",
        "cpu" => "İşlemcide çalıştı",
        _ => "Çalışıyor",
    };

    /// <summary>Plain-language verdict for the settings window.</summary>
    public string Summary =>
        $"{Where}. Yükleme {LoadSeconds:0.0} sn, gerçek zamanın {SpeedFactor:0.0} katı hızda işliyor" +
        (HallucinatedOnSilence.Count > 0
            ? $". Sessizlikte metin uydurdu ({HallucinatedOnSilence.Count} parça) — kayıtta bu filtreleniyor."
            : ".");
}

/// <summary>
/// One voice, as the recogniser hears it.
///
/// <see cref="Vector"/> is null when the recording held too little speech to answer with. That is
/// an ordinary outcome rather than a failure — one side of a call is silent while the other person
/// talks — and it is reported as a result so that nothing writes a failure into the log for it.
/// Measured over this application's archive, below thirty seconds of speech the error rate is
/// thirteen times what it is above, which is why the worker refuses rather than guessing.
/// </summary>
public sealed class WorkerVoiceprint : WorkerEvent
{
    public float[]? Vector { get; init; }
    public double SpeechSeconds { get; init; }
    public int Windows { get; init; }

    /// <summary>
    /// Which model produced it. Stored beside every voiceprint because vectors from two models
    /// are not comparable, and comparing them does not fail — it quietly returns a number near
    /// zero for two recordings of the same person.
    /// </summary>
    public string Model { get; init; } = "";

    /// <summary>Why there is no vector, when there is none.</summary>
    public string? Reason { get; init; }

    public bool Usable => Vector is { Length: > 0 };
}

/// <summary>
/// What `vt_worker prosody` measured: level and pitch over time, per channel.
///
/// The two channels arrive separately and stay separate. They are different signals with
/// different gains — one is a microphone, the other is whatever the far end's application sent —
/// and putting them on one scale would invent a comparison neither supports.
/// </summary>
public sealed class WorkerProsody : WorkerEvent
{
    /// <summary>The bin width the worker used, in seconds. Milliseconds are recovered from it.</summary>
    public double BinSeconds { get; init; }

    /// <summary>Keyed "mic" and "far"; a channel that was not recorded arrives as null.</summary>
    public Dictionary<string, WorkerProsodyChannel?> Channels { get; init; } = [];

    public double ElapsedS { get; init; }
}

/// <summary>One channel's measurements. Bins are [start s, dBFS, pitch Hz or null, voiced 0..1].</summary>
public sealed class WorkerProsodyChannel
{
    public double FloorDbfs { get; init; }
    public double SpeechSeconds { get; init; }
    public double?[][] Bins { get; init; } = [];
}

/// <summary>The job handed to `vt_worker prosody`: one call, up to two files.</summary>
public sealed class ProsodyRequest
{
    public required string Id { get; init; }

    /// <summary>The user's own channel, or null when it was not recorded.</summary>
    public string? MicPath { get; init; }

    /// <summary>The far end, or null.</summary>
    public string? FarPath { get; init; }
}

/// <summary>The job handed to `vt_worker speaker`: one recording, one voice.</summary>
public sealed class SpeakerRequest
{
    public required string Id { get; init; }

    /// <summary>The far-end WAV — 16 kHz mono 16-bit, which is what the recorder writes.</summary>
    public required string WavPath { get; init; }

    /// <summary>Where the ONNX weights are cached. Same directory as the Whisper models.</summary>
    public string? CacheDir { get; init; }
}

public sealed class WorkerProgress : WorkerEvent
{
    /// <summary>loading, mic, far or merge.</summary>
    public string Stage { get; init; } = "";

    public double Percent { get; init; }

    /// <summary>
    /// What the engine is doing, in its own words — "3/5 yükleniyor · 12.4 MB · Opus · dil tr",
    /// "sunucuda sırada · 4 dk", "2/5 geldi · dil tr · 18 satır · 214 kelime". Null when nothing
    /// new has been said since the last event.
    ///
    /// The cloud engines had been composing these all along and the worker discarded them, so a
    /// percentage was the only thing that ever reached a log. Four days of "why is the cloud
    /// worse" were spent guessing at what a request contained while the request was describing
    /// itself into a variable named with a leading underscore.
    /// </summary>
    public string? Note { get; init; }
}

public sealed class WorkerFailure : WorkerEvent
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>True when the failure is about the CUDA runtime rather than the audio or model.</summary>
    public bool IsCudaProblem => Code is "cuda_runtime";

    /// <summary>True when retrying without a GPU is worth offering.</summary>
    public bool CanRetryOnCpu => Code is "cuda_runtime" or "model_load_failed";
}

public sealed class WorkerResult : WorkerEvent
{
    public List<TranscriptSegment> Segments { get; init; } = [];
    public double Duration { get; init; }
    public TranscriptStats? Stats { get; init; }
    public string? Engine { get; init; }
    public string? ModelRef { get; init; }
    public string? Language { get; init; }
    public double ElapsedS { get; init; }

    /// <summary>
    /// Per stream ("mic", "far"), the share of the audible speech that came back with words on
    /// it. Absent for an engine or a recording where the question does not apply.
    ///
    /// The measurement that would have caught this in a day rather than in four. A hosted service
    /// was returning words for 108 of 157 seconds of speech while the local engine returned 150,
    /// and the transcript alone could not say so — the missing 49 seconds were at the same level
    /// as the rest, so what came back read as a conversation with pauses in it.
    /// </summary>
    public Dictionary<string, double>? SpeechCoverage { get; init; }

    /// <summary>The worst of the streams, which is the one worth reporting. Null if unmeasured.</summary>
    public double? WorstSpeechCoverage =>
        SpeechCoverage is { Count: > 0 } c ? c.Values.Min() : null;

    /// <summary>
    /// What the service heard that was not a word: laughter, a cough, a long silence.
    ///
    /// Only ElevenLabs labels these, and only when asked. Every other engine sends an empty list,
    /// which is the honest answer — a call transcribed by one of those has no events rather than
    /// no laughter, and the screen says which.
    ///
    /// Deliberately beside the words rather than inside them: an event in the transcript would be
    /// a sentence nobody said, and it would then be quoted as one.
    /// </summary>
    public List<TranscriptAudioEvent> AudioEvents { get; init; } = [];
}

/// <summary>One non-word sound the service reported, on the channel it heard it on.</summary>
public sealed class TranscriptAudioEvent
{
    /// <summary>"mic" or "far".</summary>
    public string Channel { get; init; } = "";

    public int StartMs { get; init; }
    public int EndMs { get; init; }

    /// <summary>One lower-case ASCII token, as the worker normalised it: "laughter", "door_slam".</summary>
    public string Kind { get; init; } = "";
}

public sealed class TranscriptSegment
{
    /// <summary>"me" or "them". A fact from which stream the audio came, not a model prediction.</summary>
    public string Speaker { get; init; } = "";

    public double Start { get; init; }
    public double End { get; init; }
    public string Text { get; init; } = "";
    public double? AvgLogprob { get; init; }
    public double? NoSpeechProb { get; init; }

    /// <summary>
    /// When true, numbers and dates from this segment must be excluded from automatic
    /// contradiction detection. A misheard amount otherwise becomes a fabricated price conflict
    /// attributed to a real person.
    /// </summary>
    public bool LowConfidence { get; init; }

    public bool OverlapsOtherSpeaker { get; init; }
    public bool SuspectedEcho { get; init; }
    public List<TranscriptWord> Words { get; init; } = [];

    public TimeSpan StartTime => TimeSpan.FromSeconds(Start);
    public TimeSpan EndTime => TimeSpan.FromSeconds(End);
    public bool IsMe => Speaker == "me";
}

public sealed class TranscriptWord
{
    public double Start { get; init; }
    public double End { get; init; }
    public string Text { get; init; } = "";

    [JsonPropertyName("p")] public double? Probability { get; init; }
}

public sealed class TranscriptStats
{
    public int MicSegments { get; init; }
    public int FarSegments { get; init; }
    public int OverlapSegments { get; init; }
    public int SuspectedEchoSegments { get; init; }
    public int LowConfidenceSegments { get; init; }

    /// <summary>
    /// The far end bled into the microphone, which means the user was on loudspeaker. Windows
    /// does not echo-cancel a second independent capture client, so both streams end up holding
    /// the same voice and attribution degrades. Worth telling the user about once.
    /// </summary>
    public bool LikelyNoHeadphones { get; init; }
}

/// <summary>The job handed to the worker on stdin.</summary>
public sealed class TranscriptionRequest
{
    public required string Id { get; init; }
    public string Engine { get; init; } = "faster-whisper";
    public required string ModelRef { get; init; }

    /// <summary>auto, cuda or cpu.</summary>
    public string Device { get; init; } = "auto";

    /// <summary>auto, float16, int8_float16 or int8.</summary>
    public string ComputeType { get; init; } = "auto";

    public string Language { get; init; } = "tr";

    /// <summary>The user's own voice. Null for a recording that captured only the far end.</summary>
    public string? MicPath { get; init; }

    /// <summary>The other party. Null for a recording that captured only the microphone.</summary>
    public string? FarPath { get; init; }

    public int BeamSize { get; init; } = 5;
    public bool WordTimestamps { get; init; } = true;
    public bool VadFilter { get; init; } = true;

    /// <summary>
    /// Names and terms the recogniser should expect — "Sumsub, KYC, Uliana" — biased at every
    /// decoding window. This is what stops a product name coming out as a Turkish word that
    /// sounds like it.
    /// </summary>
    public string? Hotwords { get; init; }

    // InitialPrompt was here, and the comment above it was right about the risk without being
    // right about the size of it. A prompt is not a stronger kind of hotword: it is text the
    // decoder is told it has already written, so it continues the *style* of it. A list of
    // capitalised terms separated by commas is a style, and the model went on producing the list
    // instead of the conversation — on the hosted service and, less visibly, on the local engine
    // too. Measured, removed, and not to be reintroduced without measuring again.

    /// <summary>
    /// Detect the language per window rather than once per file, for calls that switch between
    /// Turkish and English mid-sentence. Slower, and only supported by the large models.
    /// </summary>
    public bool Multilingual { get; init; }

    /// <summary>
    /// Pause length above which one segment is cut into two. Whisper places boundaries from its
    /// own decoding rather than the timeline, so without this a quote can be stamped seconds
    /// away from where it was spoken.
    /// </summary>
    public double ResegmentMaxGap { get; init; } = 1.5;

    /// <summary>Where weights are cached. Kept outside the application directory.</summary>
    public string? CacheDir { get; init; }
}
