using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Core.Configuration;

/// <summary>Where transcription runs.</summary>
public enum TranscriptionMode
{
    /// <summary>
    /// Everything stays on this machine. The default, and the premise the product was built on.
    /// </summary>
    LocalOnly,

    /// <summary>
    /// Use the local model when it can actually run, otherwise upload.
    ///
    /// Judged by asking the worker whether CUDA is genuinely reachable, because a machine can
    /// have a card and still be unable to use it. Note what this means in practice: a driver
    /// problem silently turns into call audio being uploaded, so the mode says so plainly and
    /// the application reports which route each call took.
    /// </summary>
    Automatic,

    /// <summary>Always upload, even when the local model would work.</summary>
    CloudOnly,
}

public sealed record AppSettings
{
    // ---- recording ----------------------------------------------------------

    /// <summary>
    /// When first-run setup was finished or deliberately skipped. Null means it has never run.
    ///
    /// Its own field rather than "does a settings file exist", because the wizard did not write
    /// one: the only thing that created settings.json was opening Settings and pressing Save. So
    /// somebody could complete setup, press Finish, close the application, reopen it — and meet
    /// the same wizard again, forever. For an application that starts with Windows, that was the
    /// first thing its owner saw every single day.
    ///
    /// Skipping counts as an answer. Somebody using the cloud route needs none of the local
    /// prerequisites, and asking again every launch is not a wizard, it is nagging.
    /// </summary>
    public DateTimeOffset? SetupCompletedAt { get; init; }

    // ---- updates ------------------------------------------------------------

    /// <summary>
    /// Whether the application looks for new versions.
    ///
    /// On by default, and it only ever <i>looks</i>: the user decided explicitly that nothing may
    /// install without being asked. A check that fails is silent, so turning this off is about not
    /// contacting GitHub at all rather than about avoiding interruptions.
    /// </summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// A version the user said they did not want.
    ///
    /// Stored so "bu sürümü atla" means something beyond the current session, and compared rather
    /// than matched exactly — skipping 1.2.0 must not also skip 1.3.0, or one dismissal silences
    /// updates forever.
    /// </summary>
    public string? SkippedUpdateVersion { get; init; }

    /// <summary>When the last check ran, so the next one is not on every start.</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>
    /// The language the interface is shown in. Turkish by default.
    ///
    /// Turkish is the base rather than a translation of English, which is the reverse of the
    /// usual arrangement and is deliberate: this application's wording — its error messages, its
    /// ledger, and above all its sentences about what is and is not being recorded — was written
    /// in Turkish first, and those were the sentences that took longest to get right.
    ///
    /// Separate from <see cref="Language"/>, which is the language people are expected to be
    /// *speaking* on a call. The two are frequently different and conflating them would mean
    /// switching the interface to English quietly told Whisper to stop expecting Turkish.
    /// </summary>
    public string UiLanguage { get; init; } = "tr";

    public bool RecordWhatsApp { get; init; } = true;
    public bool RecordTelegram { get; init; } = true;

    /// <summary>
    /// Whether Signal Desktop calls are recorded.
    ///
    /// Defaults to on like the other two. A settings file written before Signal was supported has
    /// no value for this, and JSON deserialisation then leaves the field at its default — which is
    /// what is wanted: somebody who asked for their calls to be recorded gets the new application
    /// recorded too, rather than discovering months later that one messenger was quietly skipped.
    /// </summary>
    public bool RecordSignal { get; init; } = true;

    /// <summary>Record every detected call automatically, then ask afterwards whether to keep it.</summary>
    /// <summary>
    /// Whether a detected call is recorded without being asked.
    ///
    /// This existed from the first version and was never read anywhere, so automatic recording
    /// could not in fact be turned off by any means — which is a serious thing to get wrong in an
    /// application that records private conversations. It is now honoured in the orchestrator,
    /// and reachable in one click from the tray, because the moment somebody wants it off is a
    /// moment when a call is about to start.
    ///
    /// Turning it off does not stop the watching: the status card still says a call is happening
    /// and is deliberately not being recorded, which is the reassurance somebody who switched it
    /// off actually wants. Manual recording still works.
    /// </summary>
    public bool RecordAutomatically { get; init; } = true;

    /// <summary>
    /// Whether a strip appears at the top of the screen while recording.
    ///
    /// On by default and worth defending as a default. The tray icon alone makes a running
    /// recorder indistinguishable from a switched-off one — it is a few pixels of a colour
    /// nobody has memorised, on a bar that is often collapsed. Somebody who cannot tell at a
    /// glance whether their conversations are being recorded will assume the worst, and they
    /// would be right to.
    /// </summary>
    public bool ShowRecordingBar { get; init; } = true;

    /// <summary>
    /// Whether Windows starts this application at logon.
    ///
    /// On by default, because a call recorder that has to be remembered and launched before every
    /// conversation records nothing: the calls worth having a record of are exactly the ones
    /// nobody saw coming.
    ///
    /// The setting is the intent, and the machine is reconciled to it on every start — see
    /// <c>AutoStart</c>. It used to be an installer checkbox alone, which meant it could not be
    /// changed afterwards and a silent update could quietly overturn a deliberate "no".
    /// </summary>
    public bool StartWithWindows { get; init; } = true;

    /// <summary>
    /// Group calls are recorded as audio only.
    ///
    /// Every remote participant arrives mixed into a single stream, so who said what stops being
    /// a fact and becomes a guess. Rather than guess, the audio is kept and the transcript and
    /// analysis are skipped.
    /// </summary>
    public bool TranscribeGroupCalls { get; init; }

    /// <summary>Ask Windows to keep the far end out of the microphone stream.</summary>
    public bool UseEchoCancellation { get; init; } = true;

    /// <summary>
    /// Which microphone to record the user from. Null follows the communications default.
    ///
    /// Stored as the endpoint identifier rather than the name, because names change when a
    /// device is renamed or a driver updates, and a setting that silently stops matching would
    /// send the recorder back to the default without saying so.
    /// </summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary>
    /// Which output endpoint to capture the far end from. Null follows the communications default.
    ///
    /// The setting that matters most, and the least obvious. Listening on Bluetooth earphones
    /// while talking into the laptop microphone is an ordinary thing to do, and Windows records
    /// those as two unrelated defaults. A recorder that assumes one device does both captures an
    /// hour of silence from the other person — with no error, because an idle endpoint and a
    /// quiet conversation are the same thing to a loopback client.
    /// </summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>
    /// Try the per-process capture backend before falling back to the whole output device.
    ///
    /// Off by default. On Windows 11 build 26200 that virtual device reports no usable clock and
    /// has been seen to hand back silence for some VoIP clients, and silence is indistinguishable
    /// from success. It is opt-in, and verified against real audio before being used.
    /// </summary>
    public bool PreferProcessLoopback { get; init; }

    // ---- transcription ------------------------------------------------------

    public string AsrModelId { get; init; } = AsrCatalog.DefaultModelId;
    public string Language { get; init; } = "tr";

    /// <summary>Where transcription runs. See <see cref="TranscriptionMode"/>.</summary>
    public TranscriptionMode AsrMode { get; init; } = TranscriptionMode.LocalOnly;

    /// <summary>Model used when a call falls through to the cloud. Ignored in LocalOnly.</summary>
    public string CloudAsrModelId { get; init; } = "cloud-openai-whisper";

    /// <summary>Overrides the endpoint of the chosen hosted model.</summary>
    public string? AsrApiBaseUrl { get; init; }

    public string? AsrApiKey { get; init; }

    /// <summary>
    /// Hosted transcription services, in the order they should be tried.
    ///
    /// A list rather than one entry because a single hosted service is a single point of
    /// failure on exactly the evening it matters: credit runs out, a rate limit bites, or the
    /// provider has an outage, and a recorder that gives up then has failed at its only job.
    /// With a second endpoint configured the call is transcribed by whichever one answers, and
    /// the user finds out afterwards rather than losing the conversation.
    /// </summary>
    public List<SttEndpoint> SttEndpoints { get; init; } = [];

    /// <summary>
    /// The endpoints worth trying, in order.
    ///
    /// Falls back to the older single-endpoint fields when no list has been configured, so an
    /// existing installation keeps working without the user having to reconfigure anything.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<SttEndpoint> UsableSttEndpoints
    {
        get
        {
            var configured = SttEndpoints.Where(e => e.IsUsable).ToList();
            if (configured.Count > 0) return configured;

            if (string.IsNullOrWhiteSpace(AsrApiKey)) return [];

            var legacy = new SttEndpoint
            {
                Kind = "openai",
                Name = "OpenAI",
                BaseUrl = ResolvedAsrBaseUrl,
                ApiKey = AsrApiKey,
                Model = AsrCatalog.TryGet(CloudAsrModelId, out var cloud) ? cloud.ModelRef : "whisper-1",
            };

            return legacy.IsUsable ? [legacy] : [];
        }
    }

    /// <summary>auto, cuda or cpu.</summary>
    public string AsrDevice { get; init; } = "auto";

    /// <summary>
    /// Wait after a call ends before starting GPU work.
    ///
    /// Not about correctness. The laptop GPU shares a power budget with the video encoder the
    /// call itself is using, so transcribing too eagerly makes the machine throttle and the user
    /// notice the recorder. Waiting costs nothing, since nobody needs the transcript instantly.
    /// </summary>
    public int GpuCooldownSeconds { get; init; } = 60;

    // ---- analysis -----------------------------------------------------------

    public bool AnalyseAutomatically { get; init; } = true;
    public LlmProviderKind LlmProvider { get; init; } = LlmProviderKind.LlamaServer;

    /// <summary>Which entry of the local model catalogue to use. Ignored by remote providers.</summary>
    public string LlmModelId { get; init; } = LocalLlmCatalog.DefaultModelId;

    /// <summary>
    /// Model identifier for a provider that hosts its own models, such as
    /// "qwen/qwen3-235b-a22b-instruct" on OpenRouter.
    ///
    /// Separate from <see cref="LlmModelId"/> on purpose: the local catalogue holds GGUF file
    /// names, which mean nothing to a remote API. Sending one would simply be rejected, and the
    /// error would not obviously point at the cause.
    /// </summary>
    public string? LlmRemoteModel { get; init; }

    public string? LlmBaseUrl { get; init; }

    /// <summary>Only used by providers that require one. Never written for local backends.</summary>
    public string? LlmApiKey { get; init; }

    // ---- export -------------------------------------------------------------

    public bool ExportToObsidian { get; init; }
    public string? ObsidianVaultPath { get; init; }

    /// <summary>
    /// Off by default, and stays off unless deliberately enabled.
    ///
    /// Notion is a cloud service: turning this on sends conversation summaries to somebody
    /// else's servers, which contradicts the reason this application exists. When it is on, only
    /// the summary is sent — never the transcript, never the audio.
    /// </summary>
    public bool ExportToNotion { get; init; }

    public string? NotionApiKey { get; init; }
    public string? NotionDatabaseId { get; init; }

    // ---- retention ----------------------------------------------------------

    /// <summary>
    /// Days after which a recording's audio is deleted. Zero keeps it forever.
    ///
    /// Only the audio goes — the transcript, the ledger entry and any notes stay, because those
    /// are the small part and the part worth keeping. A conversation on the board or one the user
    /// wrote a note about is never swept: both are somebody explicitly saying this one matters.
    /// </summary>
    public int AudioRetentionDays { get; init; }

    // ---- storage ------------------------------------------------------------

    /// <summary>Overrides the data directory. Rejected if it resolves inside a cloud-sync folder.</summary>
    public string? DataRoot { get; init; }

    [JsonIgnore]
    public AsrModel AsrModel =>
        AsrCatalog.TryGet(AsrModelId, out var model) ? model : AsrCatalog.Default;

    [JsonIgnore]
    public AsrModel CloudAsrModel =>
        AsrCatalog.TryGet(CloudAsrModelId, out var model) && model.SendsAudioOffMachine
            ? model
            : AsrCatalog.Get("cloud-openai-whisper");

    /// <summary>
    /// Picks the model for one call.
    ///
    /// <paramref name="localTranscriptionUsable"/> is answered by asking the worker whether CUDA
    /// is actually reachable, not by reading a specification sheet: a card can be present and
    /// still unusable because a runtime DLL is missing, and that is exactly the case where
    /// falling back is worth doing.
    /// </summary>
    public AsrModel ResolveAsrModel(bool localTranscriptionUsable) => AsrMode switch
    {
        TranscriptionMode.CloudOnly => CloudAsrModel,
        TranscriptionMode.Automatic => localTranscriptionUsable ? AsrModel : CloudAsrModel,
        _ => AsrModel,
    };

    /// <summary>True when a call could end up being uploaded under the current settings.</summary>
    [JsonIgnore]
    public bool AudioMayLeaveTheMachine => AsrMode != TranscriptionMode.LocalOnly;

    /// <summary>Endpoint for the hosted model, falling back to its catalogue default.</summary>
    [JsonIgnore]
    public string ResolvedAsrBaseUrl =>
        string.IsNullOrWhiteSpace(AsrApiBaseUrl)
            ? CloudAsrModel.DefaultBaseUrl ?? "https://api.openai.com/v1"
            : AsrApiBaseUrl.Trim();

    [JsonIgnore]
    public LlmProvider Provider => LlmProviders.Get(LlmProvider);

    /// <summary>Endpoint to use, falling back to the provider's default.</summary>
    [JsonIgnore]
    public string ResolvedBaseUrl =>
        string.IsNullOrWhiteSpace(LlmBaseUrl) ? Provider.DefaultBaseUrl : LlmBaseUrl;

    /// <summary>
    /// Whether there is anything configured for analysis to talk to.
    ///
    /// Answered from settings alone, without touching the network, because it is asked on the path
    /// that decides whether to attempt analysis at all — and the whole point is to avoid a request
    /// that will hang. A hosted provider with no key cannot answer, and no address means there is
    /// nothing to ask.
    ///
    /// <b>This is deliberately "in principle".</b> A local server that is configured but not
    /// running still passes here, because the only way to know is to ask it, and asking is exactly
    /// what must not block. That case is bounded by a short timeout on the request instead: it
    /// fails in seconds and is reported as a failure, rather than sitting for ten minutes per
    /// transcript chunk while the recording queue backs up behind it.
    /// </summary>
    [JsonIgnore]
    public bool LlmReachableInPrinciple
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ResolvedBaseUrl)) return false;

            // A hosted service without a key is a request that will be refused, every time.
            return !Provider.SendsDataOffMachine || !string.IsNullOrWhiteSpace(LlmApiKey);
        }
    }

    /// <summary>
    /// True when the provider hosts the models itself and is addressed by a model identifier
    /// rather than by a file this machine holds.
    /// </summary>
    [JsonIgnore]
    public bool UsesRemoteModelName =>
        LlmProvider is LlmProviderKind.OpenRouter or LlmProviderKind.OpenAiCompatible
                    or LlmProviderKind.Anthropic or LlmProviderKind.OpenAi;

    /// <summary>
    /// The exact string sent as the model in a chat request.
    ///
    /// llama-server and LM Studio serve whichever file was loaded and largely ignore this;
    /// Ollama addresses models by tag; a remote provider needs its own identifier. Getting this
    /// wrong is a request the server rejects for reasons that do not mention the model at all.
    /// </summary>
    [JsonIgnore]
    public string ResolvedModelName
    {
        get
        {
            if (UsesRemoteModelName)
                return string.IsNullOrWhiteSpace(LlmRemoteModel) ? "" : LlmRemoteModel.Trim();

            if (LlmProvider == LlmProviderKind.Ollama) return LlmModelId;

            return LocalLlmCatalog.All.FirstOrDefault(m => m.Id == LlmModelId)?.FileName ?? LlmModelId;
        }
    }

    /// <summary>
    /// Problems that should be shown before the settings are accepted.
    ///
    /// Each one names the section it belongs to. The settings window shows one category page at a
    /// time and this list at the bottom of all of them, so a problem was regularly read while its
    /// cause was on a page the reader was not looking at — somebody standing on "Çözümleme" was
    /// told "Buluta gönderme açık ama API anahtarı girilmemiş", which is about transcription, and
    /// then hunted for a key that was already filled in on the page in front of them.
    /// </summary>
    public IReadOnlyList<string> Validate(AppPaths paths)
    {
        List<string> problems = [];

        const string Recording = "Kayıt";
        const string Transcription = "Yazıya dökme";
        const string Analysis = "Çözümleme";
        const string Export = "Dışa aktarma";

        void Problem(string section, string message) => problems.Add($"{section} — {message}");

        if (!RecordWhatsApp && !RecordTelegram && !RecordSignal)
            Problem(Recording, "En az bir uygulama seçilmeli, yoksa hiçbir görüşme kaydedilmez.");

        var cloud = AppPaths.DetectCloudSync(paths.Recordings);
        if (cloud.Count > 0)
        {
            Problem(Recording,
                $"Kayıt klasörü {string.Join(" ve ", cloud)} içinde. Görüşme kayıtları buluta " +
                "yüklenir. Başka bir konum seçin.");
        }

        if (ExportToObsidian && string.IsNullOrWhiteSpace(ObsidianVaultPath))
            Problem(Export, "Obsidian dışa aktarımı açık ama vault klasörü seçilmemiş.");

        if (ExportToObsidian && !string.IsNullOrWhiteSpace(ObsidianVaultPath) && !Directory.Exists(ObsidianVaultPath))
            Problem(Export, "Seçilen Obsidian vault klasörü bulunamadı.");

        if (Provider.RequiresApiKey && string.IsNullOrWhiteSpace(LlmApiKey))
            Problem(Analysis, $"{Provider.DisplayName} bir API anahtarı gerektiriyor.");

        if (UsesRemoteModelName && string.IsNullOrWhiteSpace(LlmRemoteModel))
        {
            Problem(Analysis,
                $"{Provider.DisplayName} için model adı yazılmalı, örneğin " +
                $"\"{SuggestionsFor(LlmProvider).First()}\".");
        }

        if (LlmProvider == LlmProviderKind.OpenAiCompatible && string.IsNullOrWhiteSpace(LlmBaseUrl))
            Problem(Analysis, "Bu sağlayıcı için adres yazılmalı.");

        if (ExportToNotion && (string.IsNullOrWhiteSpace(NotionApiKey) || string.IsNullOrWhiteSpace(NotionDatabaseId)))
            Problem(Export, "Notion dışa aktarımı için anahtar ve veritabanı kimliği gerekli.");

        if (!AsrCatalog.TryGet(AsrModelId, out _))
            Problem(Transcription, $"Bilinmeyen yazıya dökme modeli: {AsrModelId}");

        // Asked of the same property that decides what actually runs.
        //
        // This used to test AsrApiKey alone — the single-key field the settings screen replaced
        // with a list of services. So somebody who entered their key where the interface asks for
        // it, in the service, was refused with "API anahtarı girilmemiş" while looking straight at
        // the key they had just typed. The runtime was already using the list; only the check was
        // still reading the field nothing fills in any more.
        //
        // UsableSttEndpoints falls back to the legacy field when the list is empty, so old
        // settings files keep working and there is exactly one answer to "is a cloud service
        // configured".
        if (AsrMode != TranscriptionMode.LocalOnly && UsableSttEndpoints.Count == 0)
        {
            Problem(Transcription,
                "Buluta gönderme açık ama kullanılabilir bir servis yok. Servisin açık olduğundan, "
                + "adresinin, API anahtarının ve model adının dolu olduğundan emin ol.");
        }

        return problems;
    }

    /// <summary>
    /// A few OpenRouter identifiers that suit this workload, offered as a starting point.
    ///
    /// The task is extraction with a fixed schema, not conversation, so a large model buys
    /// little here beyond cost. What matters is Turkish competence and reliable structured
    /// output. These are suggestions in a free-text field, never a closed list: the catalogue
    /// changes constantly and a hard-coded list would rot.
    /// </summary>
    [JsonIgnore]
    public static IReadOnlyList<string> RemoteModelSuggestions { get; } =
    [
        "qwen/qwen3-235b-a22b-instruct",
        "google/gemini-2.5-flash",
        "anthropic/claude-haiku-4.5",
        "openai/gpt-5-mini",
        "deepseek/deepseek-chat",
    ];

    /// <summary>
    /// Starting points for one provider, since the identifier spelling is provider-specific.
    ///
    /// The same model is "anthropic/claude-haiku-4.5" through OpenRouter and "claude-haiku-4-5"
    /// against Anthropic directly. Offering the OpenRouter spelling to somebody who picked
    /// Anthropic produces a rejection that names neither the model nor the format, so the two
    /// lists are kept apart.
    ///
    /// Still only suggestions. The real list is fetched from the provider — see ModelDirectory —
    /// because any list compiled here starts rotting the day it is written.
    /// </summary>
    public static IReadOnlyList<string> SuggestionsFor(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.Anthropic =>
            ["claude-haiku-4-5", "claude-sonnet-4-5", "claude-opus-4-1"],

        LlmProviderKind.OpenAi =>
            ["gpt-4.1-mini", "gpt-4o-mini", "gpt-4.1"],

        _ => RemoteModelSuggestions,
    };

    /// <summary>Whether conversation-derived text would leave the machine as configured.</summary>
    [JsonIgnore]
    public bool AnythingLeavesTheMachine => Provider.SendsDataOffMachine || ExportToNotion;

    // ---- persistence --------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // A corrupt settings file must not stop the application from starting. Defaults are
            // safe, and the user can fix the file or re-enter their preferences.
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written via a temporary file so an interrupted save cannot leave settings truncated.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));

        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }
}
