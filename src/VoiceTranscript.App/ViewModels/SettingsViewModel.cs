using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Text;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// Backs the settings window.
///
/// The model pickers show the measured Turkish error rates and the VRAM arithmetic next to each
/// option, rather than a bare list of names. The numbers are the whole point: without them the
/// user is choosing between strings, and the obvious-looking choice is frequently the wrong one
/// — the most popular Turkish Whisper fine-tune makes nearly twice the errors of the plain model
/// it is based on.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly SttProbe _probe;
    private readonly HttpClient _http;

    /// <summary>
    /// The settings this window opened on, kept so saving can amend them rather than replace them.
    ///
    /// <see cref="ToSettings"/> used to build a brand new record from the fields the window shows,
    /// which silently reset every setting it does not show. Three of those were rescued by hand at
    /// the call site and two were not: a transcript retention period was wiped on every save, and
    /// <see cref="AppSettings.DataRoot"/> would be too the moment it started doing anything. A
    /// rescue list is the wrong shape for this — it has to be remembered every time a setting is
    /// added, and forgetting is silent. Amending the original cannot forget.
    /// </summary>
    private readonly AppSettings _original;

    public SettingsViewModel(AppSettings settings, AppPaths paths, HttpClient http)
    {
        _original = settings;
        _paths = paths;
        _http = http;
        _probe = new SttProbe(http);

        // Hosted entries live in their own picker: they need a key rather than a download,
        // and mixing them into the local list would make the choice look like a like-for-like
        // swap when it is a decision about where the audio goes.
        AsrModels = [.. AsrCatalog.All.Where(m => !m.SendsAudioOffMachine)];
        CloudAsrModels = [.. AsrCatalog.All.Where(m => m.SendsAudioOffMachine)];
        LlmModels = [.. LocalLlmCatalog.All];
        Providers = [.. LlmProviders.All];

        _selectedAsrModel = AsrCatalog.TryGet(settings.AsrModelId, out var asr) ? asr : AsrCatalog.Default;
        _selectedLlmModel = LocalLlmCatalog.All.FirstOrDefault(m => m.Id == settings.LlmModelId)
                            ?? LocalLlmCatalog.Default;
        _selectedProvider = LlmProviders.Get(settings.LlmProvider);

        _recordWhatsApp = settings.RecordWhatsApp;
        _recordTelegram = settings.RecordTelegram;
        _recordSignal = settings.RecordSignal;
        _recordAutomatically = settings.RecordAutomatically;
        _uiLanguage = Localisation.Available.FirstOrDefault(l => l.Code == settings.UiLanguage);
        if (_uiLanguage.Code is null) _uiLanguage = Localisation.Available[0];
        _showRecordingBar = settings.ShowRecordingBar;
        _useEchoCancellation = settings.UseEchoCancellation;
        _microphoneDeviceId = settings.MicrophoneDeviceId;
        _outputDeviceId = settings.OutputDeviceId;
        _preferProcessLoopback = settings.PreferProcessLoopback;
        _gpuCooldownSeconds = settings.GpuCooldownSeconds;
        _asrDevice = settings.AsrDevice;
        _asrMode = settings.AsrMode;
        _selectedCloudAsrModel = AsrCatalog.All.FirstOrDefault(m => m.Id == settings.CloudAsrModelId)
                                 ?? AsrCatalog.Get("cloud-openai-whisper");
        _asrApiKey = settings.AsrApiKey ?? "";
        _asrApiBaseUrl = settings.AsrApiBaseUrl ?? "";
        _analyseAutomatically = settings.AnalyseAutomatically;
        _llmRemoteModel = settings.LlmRemoteModel ?? "";
        _llmBaseUrl = settings.LlmBaseUrl ?? "";
        _llmApiKey = settings.LlmApiKey ?? "";
        _exportToObsidian = settings.ExportToObsidian;
        _obsidianVaultPath = settings.ObsidianVaultPath ?? "";
        _exportToNotion = settings.ExportToNotion;
        _notionApiKey = settings.NotionApiKey ?? "";
        _notionDatabaseId = settings.NotionDatabaseId ?? "";
        _audioRetentionDays = settings.AudioRetentionDays;

        foreach (var endpoint in settings.SttEndpoints)
            SttEndpoints.Add(new SttEndpointViewModel(endpoint, _probe));

        // An older settings file has one key rather than a list. Bring it across so nothing is
        // lost and the user sees their existing configuration where they now expect it.
        if (SttEndpoints.Count == 0 && !string.IsNullOrWhiteSpace(settings.AsrApiKey))
        {
            SttEndpoints.Add(new SttEndpointViewModel(
                new SttEndpoint
                {
                    Kind = "openai",
                    BaseUrl = settings.ResolvedAsrBaseUrl,
                    ApiKey = settings.AsrApiKey,
                    Model = _selectedCloudAsrModel.ModelRef,
                },
                _probe));
        }

        RefreshDevices();
        Revalidate();
    }

    /// <summary>
    /// Which model weights are already on this machine, by repository reference.
    ///
    /// The table lists five models with their error rates and download sizes and invites a
    /// choice, and until now it said nothing about which of them were actually present. So the
    /// one fact needed to make the choice — "this one is here, that one is a 3 GB wait" — was
    /// the only fact missing, and picking a row gave no clue whether the next call would
    /// transcribe immediately or stall behind a download.
    ///
    /// Replaced wholesale rather than mutated, so the bindings in the table re-evaluate.
    /// </summary>
    [ObservableProperty] private IReadOnlyCollection<string> _downloadedModelRefs = [];

    public ObservableCollection<AsrModel> AsrModels { get; }
    public ObservableCollection<AsrModel> CloudAsrModels { get; }
    public ObservableCollection<LocalLlmModel> LlmModels { get; }
    public ObservableCollection<LlmProvider> Providers { get; }
    public ObservableCollection<string> Devices { get; } = ["auto", "cuda", "cpu"];

    /// <summary>
    /// The audio endpoints, with "automatic" first.
    ///
    /// Offered because automatic is right most of the time and wrong in exactly the case people
    /// hit: listening on Bluetooth earphones while talking into the laptop microphone. Windows
    /// records those as two unrelated defaults, and capturing the wrong output endpoint produces
    /// an hour of silence from the other person with no error at all — an idle endpoint and a
    /// quiet conversation are the same thing to a loopback client.
    /// </summary>
    public ObservableCollection<AudioDeviceChoice> Microphones { get; } = [];

    public ObservableCollection<AudioDeviceChoice> Outputs { get; } = [];

    [ObservableProperty] private AudioDeviceChoice? _selectedMicrophone;
    [ObservableProperty] private AudioDeviceChoice? _selectedOutput;

    /// <summary>True when a hands-free Bluetooth endpoint is chosen, which is worth warning about.</summary>
    public bool WarnAboutHandsFree =>
        SelectedMicrophone?.IsHandsFree == true || SelectedOutput?.IsHandsFree == true;

    partial void OnSelectedMicrophoneChanged(AudioDeviceChoice? value)
        => OnPropertyChanged(nameof(WarnAboutHandsFree));

    partial void OnSelectedOutputChanged(AudioDeviceChoice? value)
        => OnPropertyChanged(nameof(WarnAboutHandsFree));

    /// <summary>Re-reads the endpoint list. Called when the window opens and on demand.</summary>
    [RelayCommand]
    public void RefreshDevices()
    {
        // Assigned through the generated properties rather than the backing fields, so the
        // selection actually reaches the two combo boxes. Writing the field directly compiles
        // and then silently leaves the UI showing the previous choice.
        SelectedMicrophone = Fill(Microphones, forCapture: true, MicrophoneDeviceId);
        SelectedOutput = Fill(Outputs, forCapture: false, OutputDeviceId);
    }

    private static AudioDeviceChoice Fill(
        ObservableCollection<AudioDeviceChoice> target,
        bool forCapture,
        string? savedId)
    {
        target.Clear();
        target.Add(AudioDeviceChoice.Automatic);

        foreach (var device in Capture.AudioDeviceCatalog.List(forCapture))
            target.Add(new AudioDeviceChoice(device.Id, device.Name, device.Description, device.IsHandsFree));

        // A device that has since been unplugged falls back to automatic rather than vanishing
        // silently, so the setting always shows what will actually be used.
        return target.FirstOrDefault(d => d.Id == savedId) ?? target[0];
    }

    /// <summary>Which endpoint to use, or automatic.</summary>
    public sealed record AudioDeviceChoice(string? Id, string Name, string Description, bool IsHandsFree)
    {
        public static AudioDeviceChoice Automatic { get; } =
            new(null, "Otomatik", "Windows'un aramalar için seçtiği cihaz", false);

        public bool HasDescription => Description.Length > 0;
    }

    /// <summary>
    /// Hosted transcription services, in the order they will be tried.
    ///
    /// A list rather than one entry because one hosted service is a single point of failure on
    /// exactly the evening it matters. The recording only exists once.
    /// </summary>
    public ObservableCollection<SttEndpointViewModel> SttEndpoints { get; } = [];

    public IReadOnlyList<SttProviderInfo> AvailableSttProviders { get; } = SttProviderCatalog.All;
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Starting points for a remote provider. A free-text field, not a closed list.</summary>
    public IReadOnlyList<string> RemoteModelSuggestions => AppSettings.RemoteModelSuggestions;

    /// <summary>True when a call could end up being uploaded under the current selection.</summary>
    public bool UsesCloudAsr => AsrMode != TranscriptionMode.LocalOnly;

    public IReadOnlyList<TranscriptionMode> AsrModes { get; } =
        [TranscriptionMode.LocalOnly, TranscriptionMode.Automatic, TranscriptionMode.CloudOnly];

    /// <summary>True when the chosen provider is addressed by a model identifier it hosts.</summary>
    public bool UsesRemoteModelName =>
        SelectedProvider.Kind is LlmProviderKind.OpenRouter or LlmProviderKind.OpenAiCompatible
                              or LlmProviderKind.Anthropic or LlmProviderKind.OpenAi;

    /// <summary>Whether the provider publishes a catalogue that can be browsed and searched.</summary>
    public bool CanBrowseModels => ModelDirectory.CanFetch(SelectedProvider.Kind);

    [ObservableProperty] private AsrModel _selectedAsrModel;
    [ObservableProperty] private LocalLlmModel _selectedLlmModel;
    [ObservableProperty] private LlmProvider _selectedProvider;
    [ObservableProperty] private string _asrDevice;
    [ObservableProperty] private TranscriptionMode _asrMode;
    [ObservableProperty] private AsrModel _selectedCloudAsrModel;
    [ObservableProperty] private string _asrApiKey;
    [ObservableProperty] private string _asrApiBaseUrl;
    [ObservableProperty] private bool _recordWhatsApp;
    [ObservableProperty] private bool _recordTelegram;
    [ObservableProperty] private bool _recordSignal;
    [ObservableProperty] private bool _useEchoCancellation;

    /// <summary>Saved endpoint identifiers. Null means follow the communications default.</summary>
    private string? _microphoneDeviceId;
    private string? _outputDeviceId;

    public string? MicrophoneDeviceId => SelectedMicrophone?.Id ?? _microphoneDeviceId;
    public string? OutputDeviceId => SelectedOutput?.Id ?? _outputDeviceId;
    [ObservableProperty] private bool _preferProcessLoopback;
    [ObservableProperty] private int _gpuCooldownSeconds;
    [ObservableProperty] private bool _analyseAutomatically;
    [ObservableProperty] private string _llmRemoteModel;
    [ObservableProperty] private string _llmBaseUrl;
    [ObservableProperty] private string _llmApiKey;
    [ObservableProperty] private bool _exportToObsidian;
    [ObservableProperty] private string _obsidianVaultPath;
    [ObservableProperty] private bool _exportToNotion;
    [ObservableProperty] private string _notionApiKey;
    [ObservableProperty] private string _notionDatabaseId;
    [ObservableProperty] private int _audioRetentionDays;

    partial void OnSelectedProviderChanged(LlmProvider value)
    {
        // A real value in the box, not a placeholder. A grey hint reads as an empty required
        // field and sends people hunting for an address the application already knows.
        LlmBaseUrl = value.DefaultBaseUrl;
        LlmStatus = null;
        DiscoveredLlmModels.Clear();

        OnPropertyChanged(nameof(UsesRemoteModelName));
        OnPropertyChanged(nameof(CanBrowseModels));
        Revalidate();
    }
    partial void OnSelectedAsrModelChanged(AsrModel value) => Revalidate();
    partial void OnExportToObsidianChanged(bool value) => Revalidate();
    partial void OnObsidianVaultPathChanged(string value) => Revalidate();
    partial void OnExportToNotionChanged(bool value) => Revalidate();
    /// <summary>
    /// Whether a detected call is recorded without being asked.
    ///
    /// Reachable here as well as from the tray. The tray is for the moment somebody decides the
    /// next call should not be recorded; this is for deciding that recording should not be the
    /// default at all, which is a different decision made at a different time.
    /// </summary>
    [ObservableProperty] private bool _recordAutomatically = true;

    /// <summary>Whether a strip appears at the top of the screen while recording.</summary>
    [ObservableProperty] private bool _showRecordingBar = true;

    /// <summary>Which language the interface is shown in.</summary>
    [ObservableProperty] private (string Code, string Name) _uiLanguage = Localisation.Available[0];

    public IReadOnlyList<(string Code, string Name)> UiLanguages { get; } = Localisation.Available;

    /// <summary>True once a different language has been picked, so the note about restarting shows.</summary>
    public bool LanguageChanged => UiLanguage.Code != Localisation.Language;

    partial void OnUiLanguageChanged((string Code, string Name) value) =>
        OnPropertyChanged(nameof(LanguageChanged));

    partial void OnRecordWhatsAppChanged(bool value) => Revalidate();
    partial void OnRecordTelegramChanged(bool value) => Revalidate();
    partial void OnRecordSignalChanged(bool value) => Revalidate();
    partial void OnLlmApiKeyChanged(string value) => Revalidate();
    partial void OnAsrApiKeyChanged(string value) => Revalidate();

    partial void OnAsrModeChanged(TranscriptionMode value)
    {
        OnPropertyChanged(nameof(UsesCloudAsr));
        Revalidate();
    }
    partial void OnLlmRemoteModelChanged(string value) => Revalidate();
    partial void OnLlmBaseUrlChanged(string value) => Revalidate();

    // ---- hosted services ----------------------------------------------------

    [RelayCommand]
    private void AddEndpoint()
    {
        var endpoint = SttEndpoint.FromProvider(SttProviderCatalog.All[0]);
        SttEndpoints.Add(new SttEndpointViewModel(endpoint, _probe));
        Revalidate();
    }

    [RelayCommand]
    private void RemoveEndpoint(SttEndpointViewModel endpoint)
    {
        SttEndpoints.Remove(endpoint);
        Revalidate();
    }

    /// <summary>Order is the fallback order, so moving an entry up is a meaningful action.</summary>
    [RelayCommand]
    private void MoveEndpointUp(SttEndpointViewModel endpoint)
    {
        var index = SttEndpoints.IndexOf(endpoint);
        if (index > 0) SttEndpoints.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveEndpointDown(SttEndpointViewModel endpoint)
    {
        var index = SttEndpoints.IndexOf(endpoint);
        if (index >= 0 && index < SttEndpoints.Count - 1) SttEndpoints.Move(index, index + 1);
    }

    // ---- analysis provider --------------------------------------------------

    [ObservableProperty] private bool _isTestingLlm;
    [ObservableProperty] private string? _llmStatus;
    [ObservableProperty] private bool _llmStatusIsGood;

    /// <summary>Models the provider says it has. Populated by the test, empty until then.</summary>
    public ObservableCollection<string> DiscoveredLlmModels { get; } = [];

    /// <summary>
    /// Asks the analysis provider whether it is there, and what it can run.
    ///
    /// The discovered model list is the useful half. Typing a model identifier by hand is how a
    /// GGUF file name ends up being sent to a hosted API that has never heard of it, and the
    /// rejection that comes back does not obviously point at the cause.
    /// </summary>
    [RelayCommand]
    private async Task TestLlmAsync()
    {
        if (IsTestingLlm) return;

        IsTestingLlm = true;
        LlmStatus = "Sinaniyor...";
        LlmStatusIsGood = false;

        try
        {
            var endpoint = new SttEndpoint
            {
                Kind = "custom",
                BaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? SelectedProvider.DefaultBaseUrl : LlmBaseUrl,
                // Local servers take no key; a placeholder keeps the probe from refusing to try.
                ApiKey = string.IsNullOrWhiteSpace(LlmApiKey) ? "local" : LlmApiKey,
                Model = string.IsNullOrWhiteSpace(LlmRemoteModel) ? "-" : LlmRemoteModel,
            };

            var result = await _probe.TestAsync(endpoint);

            LlmStatus = result.Message;
            LlmStatusIsGood = result.IsHealthy;

            DiscoveredLlmModels.Clear();
            foreach (var model in result.Models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                DiscoveredLlmModels.Add(model);
        }
        finally
        {
            IsTestingLlm = false;
        }
    }

    /// <summary>
    /// Looks for a model server already running on this machine.
    ///
    /// Ollama, llama-server and LM Studio all listen on well-known ports and all answer the same
    /// model-listing request. Somebody who already has one running should not have to know its
    /// port number to use it.
    /// </summary>
    [RelayCommand]
    private async Task DiscoverLocalServersAsync()
    {
        if (IsTestingLlm) return;

        IsTestingLlm = true;
        LlmStatus = "Yerel sunucular araniyor...";
        LlmStatusIsGood = false;

        try
        {
            (int Port, string Name)[] candidates =
            [
                (11434, "Ollama"),
                (8080, "llama-server"),
                (1234, "LM Studio"),
            ];

            foreach (var (port, name) in candidates)
            {
                var endpoint = new SttEndpoint
                {
                    Kind = "custom",
                    BaseUrl = $"http://127.0.0.1:{port}/v1",
                    ApiKey = "local",
                    Model = "-",
                };

                var result = await _probe.TestAsync(endpoint);
                if (!result.Reachable) continue;

                LlmBaseUrl = endpoint.BaseUrl;
                LlmStatus = result.Models.Count > 0
                    ? $"{name} bulundu ({port}), {result.Models.Count} model kurulu."
                    : $"{name} bulundu ({port}).";
                LlmStatusIsGood = true;

                DiscoveredLlmModels.Clear();
                foreach (var model in result.Models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                    DiscoveredLlmModels.Add(model);

                return;
            }

            LlmStatus = "Bilinen portlarda calisan bir model sunucusu bulunamadi (11434, 8080, 1234).";
        }
        finally
        {
            IsTestingLlm = false;
        }
    }

    // ---- Notion --------------------------------------------------------------

    [ObservableProperty] private bool _isTestingNotion;
    [ObservableProperty] private string? _notionStatus;
    [ObservableProperty] private bool _notionStatusIsGood;

    /// <summary>Exactly what would be sent, so the answer is readable rather than trusted.</summary>
    public IReadOnlyList<string> NotionSends => Core.Export.NotionExporter.WhatIsSent;

    public IReadOnlyList<string> NotionNeverSends => Core.Export.NotionExporter.WhatIsNeverSent;

    [RelayCommand]
    private async Task TestNotionAsync()
    {
        if (IsTestingNotion) return;

        IsTestingNotion = true;
        NotionStatus = "Sinaniyor...";
        NotionStatusIsGood = false;

        try
        {
            var exporter = new Core.Export.NotionExporter(
                repository: null!,
                new Core.Export.NotionOptions
                {
                    ApiKey = NotionApiKey,
                    DatabaseId = NotionDatabaseId,
                },
                _http);

            NotionStatus = await exporter.TestAsync();
            NotionStatusIsGood = true;
        }
        catch (Exception e)
        {
            NotionStatus = e.Message;
        }
        finally
        {
            IsTestingNotion = false;
        }
    }

    /// <summary>
    /// The settings as the window now has them.
    ///
    /// Amends the record the window opened on rather than constructing a fresh one, so a setting
    /// this screen does not display survives being saved. Everything below is what the user can
    /// actually change here; anything absent is deliberately carried through untouched.
    /// </summary>
    public AppSettings ToSettings() => _original with
    {
        RecordWhatsApp = RecordWhatsApp,
        RecordTelegram = RecordTelegram,
        RecordSignal = RecordSignal,
        RecordAutomatically = RecordAutomatically,
        ShowRecordingBar = ShowRecordingBar,
        UiLanguage = UiLanguage.Code,
        UseEchoCancellation = UseEchoCancellation,
        MicrophoneDeviceId = SelectedMicrophone?.Id,
        OutputDeviceId = SelectedOutput?.Id,
        PreferProcessLoopback = PreferProcessLoopback,
        GpuCooldownSeconds = GpuCooldownSeconds,
        AsrModelId = SelectedAsrModel.Id,
        AsrDevice = AsrDevice,
        AsrMode = AsrMode,
        CloudAsrModelId = SelectedCloudAsrModel.Id,
        AsrApiKey = string.IsNullOrWhiteSpace(AsrApiKey) ? null : AsrApiKey,
        AsrApiBaseUrl = string.IsNullOrWhiteSpace(AsrApiBaseUrl) ? null : AsrApiBaseUrl.Trim(),
        AnalyseAutomatically = AnalyseAutomatically,
        LlmProvider = SelectedProvider.Kind,
        LlmModelId = SelectedLlmModel.Id,
        LlmRemoteModel = string.IsNullOrWhiteSpace(LlmRemoteModel) ? null : LlmRemoteModel.Trim(),
        LlmBaseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? null : LlmBaseUrl.Trim(),
        LlmApiKey = string.IsNullOrWhiteSpace(LlmApiKey) ? null : LlmApiKey,
        ExportToObsidian = ExportToObsidian,
        ObsidianVaultPath = string.IsNullOrWhiteSpace(ObsidianVaultPath) ? null : ObsidianVaultPath.Trim(),
        ExportToNotion = ExportToNotion,
        NotionApiKey = string.IsNullOrWhiteSpace(NotionApiKey) ? null : NotionApiKey,
        NotionDatabaseId = string.IsNullOrWhiteSpace(NotionDatabaseId) ? null : NotionDatabaseId.Trim(),
        AudioRetentionDays = AudioRetentionDays,
        SttEndpoints = [.. SttEndpoints.Select(e => e.ToEndpoint())],
    };

    /// <summary>Shows problems as they appear rather than only on save.</summary>
    public void Revalidate()
    {
        Problems.Clear();
        foreach (var problem in ToSettings().Validate(_paths)) Problems.Add(problem);
    }

    public bool IsValid => Problems.Count == 0;
}
