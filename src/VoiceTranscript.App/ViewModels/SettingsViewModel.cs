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
        _assignContactFromTitle = settings.AssignContactFromTitle;
        _transcribeGroupCalls = settings.TranscribeGroupCalls;
        _speechVocabulary = settings.SpeechVocabulary;
        _mixedLanguage = settings.MixedLanguage;
        _spokenLanguage = SpokenLanguages.FirstOrDefault(l => l.Code == settings.Language)
                          ?? SpokenLanguages[0];
        _uiLanguage = UiLanguages.FirstOrDefault(l => l.Code == settings.UiLanguage) ?? UiLanguages[0];
        _showRecordingBar = settings.ShowRecordingBar;
        _identifySpeakers = settings.IdentifySpeakers;
        _logDetail = settings.LogDetail;
        _startWithWindows = settings.StartWithWindows;
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
        _consistencyAutomatically = settings.ConsistencyAutomatically;
        _consistencyModel = settings.ConsistencyModel ?? "";
        _consistencyUsesLedgerContext = settings.ConsistencyUsesLedgerContext;
        _consistencyOtherPartyOnly = settings.ConsistencyOtherPartyOnly;
        _extractActions = settings.ExtractActions;
        _themeChoice = settings.ThemeChoice ?? "system";
        _deceptionEnabled = settings.DeceptionEnabled;
        _commentaryEnabled = settings.CommentaryEnabled;
        _llmRemoteModel = settings.LlmRemoteModel ?? "";
        _llmBaseUrl = settings.LlmBaseUrl ?? "";
        _llmApiKey = settings.LlmApiKey ?? "";
        _exportToObsidian = settings.ExportToObsidian;
        _obsidianVaultPath = settings.ObsidianVaultPath ?? "";
        _exportToNotion = settings.ExportToNotion;
        _notionApiKey = settings.NotionApiKey ?? "";
        _notionDatabaseId = settings.NotionDatabaseId ?? "";
        _audioRetentionDays = settings.AudioRetentionDays;
        _trimSilence = settings.TrimSilenceAfterProcessing;
        _compressAudio = settings.CompressAudioAfterProcessing;

        foreach (var endpoint in settings.SttEndpoints)
            SttEndpoints.Add(new SttEndpointViewModel(endpoint, _probe));


        // Every service the application knows how to talk to, whether or not it has been set up.
        //
        // The list used to hold only what somebody had added by hand through "Servis ekle", which
        // made the reprocess dialog show one row on a machine with one key — and there is no way,
        // from that screen, to learn that Groq or OpenAI were options at all. Somebody with an
        // OpenAI key would have to guess that the service existed, go to Settings, find the menu,
        // pick it, and only then be offered it.
        //
        // So the rest arrive as empty cards. An empty key is not usable (see SttEndpoint.IsUsable),
        // so nothing about the routing changes and no service is contacted; the card is a labelled
        // box waiting for a key, and pasting one is the entire setup. They are appended, so the
        // order somebody chose for their own services — which is the order they are tried in —
        // is untouched.
        //
        // "Özel adres" is deliberately not seeded. It has no address of its own, so an empty one
        // is a card that cannot say what it is for; that entry stays on the "Servis ekle" menu
        // where it is a deliberate choice.
        foreach (var provider in SttProviderCatalog.All)
        {
            if (provider.Kind == "custom") continue;
            if (SttEndpoints.Any(e => e.Kind == provider.Kind)) continue;

            SttEndpoints.Add(new SttEndpointViewModel(SttEndpoint.FromProvider(provider), _probe));
        }

        // The older single-key field, moved onto the card it belongs to.
        //
        // This used to run only when the list was completely empty, which meant that adding any
        // service through the new screen stranded it: the key was still in the settings file, still
        // valid, and never shown or used again. Now the card exists either way, so the key simply
        // goes where somebody would look for it.
        //
        // Only onto an empty card. A key typed into the screen is the more recent decision and must
        // not be overwritten by one carried over from an older file.
        if (!string.IsNullOrWhiteSpace(settings.AsrApiKey))
        {
            var openAiDefault = SttProviderCatalog.Find("openai").BaseUrl;

            var home = SttEndpoints.FirstOrDefault(
                e => string.IsNullOrWhiteSpace(e.ApiKey)
                     && e.Kind == (settings.ResolvedAsrBaseUrl == openAiDefault ? "openai" : "custom"));

            // No "custom" card is seeded, so an older key pointing somewhere else gets one made
            // for it rather than being dropped on the floor.
            if (home is null && settings.ResolvedAsrBaseUrl != openAiDefault)
            {
                home = new SttEndpointViewModel(
                    SttEndpoint.FromProvider(SttProviderCatalog.Find("custom")) with
                    {
                        BaseUrl = settings.ResolvedAsrBaseUrl,
                        Model = _selectedCloudAsrModel.ModelRef,
                    },
                    _probe);

                SttEndpoints.Add(home);
            }

            if (home is not null)
            {
                home.ApiKey = settings.AsrApiKey;

                // And the old fields are emptied, because carrying them costs more than it saves.
                //
                // They have no control anywhere, but they were written back on every save — and
                // UsableSttEndpoints appends a hidden endpoint for whatever they hold, deduping
                // only on exact key equality. So changing the OpenAI card's key left the previous
                // one live as an invisible fallback that nothing on screen could show or remove.
                // Once the key is on a card, the card is where it lives.
                _asrApiKey = "";
                _asrApiBaseUrl = "";
            }
        }

        // Editing a service has to re-run the checks, and nothing was listening.
        //
        // Each service is its own view model, so typing a key into one raised PropertyChanged on
        // that object and reached nobody. The warning above the buttons went on saying "API
        // anahtarı girilmemiş" while the user looked straight at the key they had just typed —
        // and it stayed until some unrelated field happened to be touched.
        WatchEndpoints();

        RefreshDevices();
        Revalidate();
    }

    /// <summary>
    /// Keeps the validation warnings honest while services are being edited.
    ///
    /// Both halves matter: adding or removing a service changes the answer, and so does editing
    /// one that is already there. Subscriptions are dropped when an item leaves, because a service
    /// the user deleted must not keep voting on whether the configuration is valid.
    /// </summary>
    private void WatchEndpoints()
    {
        foreach (var endpoint in SttEndpoints) endpoint.PropertyChanged += OnEndpointChanged;

        SttEndpoints.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<SttEndpointViewModel>() ?? [])
                added.PropertyChanged += OnEndpointChanged;

            foreach (var removed in e.OldItems?.OfType<SttEndpointViewModel>() ?? [])
                removed.PropertyChanged -= OnEndpointChanged;

            Revalidate();
        };
    }

    private void OnEndpointChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only the fields that decide whether a service can be used. Status, balance and the busy
        // flag change while a connection is being tested, and revalidating on those would rerun
        // the checks several times per keystroke for no gain.
        if (e.PropertyName is nameof(SttEndpointViewModel.ApiKey)
                           or nameof(SttEndpointViewModel.BaseUrl)
                           or nameof(SttEndpointViewModel.Model)
                           or nameof(SttEndpointViewModel.Kind)
                           or nameof(SttEndpointViewModel.Enabled))
        {
            Revalidate();
        }
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

    /// <summary>
    /// Where the chosen model will run, named: the card, or the processor.
    ///
    /// "CUDA hazır (1 cihaz)" was true and useless — it named neither the card nor whether the
    /// library it needs could be loaded, which is the difference between a green light and a
    /// transcription that dies after the call has ended. Filled by the window after it probes
    /// the worker; null until then.
    /// </summary>
    [ObservableProperty] private string? _deviceSummary;

    /// <summary>Whether the chosen model's weights are already on this machine.</summary>
    public bool SelectedModelDownloaded => DownloadedModelRefs.Contains(SelectedAsrModel.ModelRef);

    /// <summary>One line saying whether choosing this model means waiting for a download.</summary>
    public string SelectedModelReadiness => DownloadedModelRefs.Count == 0
        ? Localisation.T("settingswindow.denetleniyor")
        : SelectedModelDownloaded
            ? Localisation.T("settingswindow.dosyalari-hazir-ilk-gorusme-beklemez")
            : string.Format(Localisation.T("settingswindow.henuz-indirilmedi-gb-ilk-gorusmede-inecek"), SelectedAsrModel.DownloadGb);

    partial void OnDownloadedModelRefsChanged(IReadOnlyCollection<string> value) => AnnounceSelection();

    private void AnnounceSelection()
    {
        OnPropertyChanged(nameof(SelectedModelDownloaded));
        OnPropertyChanged(nameof(SelectedModelReadiness));
    }

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

    /// <summary>
    /// True when the local engine can still be reached under the current mode.
    ///
    /// The local half of the transcription page — the model table, the download and self-test
    /// buttons, the compute device, the GPU cool-down — stayed fully editable in CloudOnly, where
    /// none of it does anything. Downloading two gigabytes of weights that will never be loaded is
    /// not a preference somebody expressed, it is a screen that failed to say the choice above had
    /// already answered the question.
    /// </summary>
    public bool UsesLocalAsr => AsrMode != TranscriptionMode.CloudOnly;

    /// <summary>
    /// Whether "yerel sunucu bul" makes sense for the chosen provider.
    ///
    /// It writes http://127.0.0.1:11434/v1 into the address box unconditionally, so with Anthropic
    /// or OpenAI selected it overwrote a fixed, correct address with one that cannot work.
    /// </summary>
    public bool CanDiscoverLocalServers =>
        SelectedProvider.Kind is LlmProviderKind.Ollama or LlmProviderKind.OpenAiCompatible;

    /// <summary>
    /// The model identifiers this provider is likely to accept, for the box that had no examples.
    ///
    /// AppSettings has carried these all along and nothing ever showed them: the identifier is
    /// provider-specific, spelled differently by each, and the only place the user ever saw one
    /// was in a validation message after getting it wrong.
    /// </summary>
    public IReadOnlyList<string> RemoteModelExamples =>
        AppSettings.SuggestionsFor(SelectedProvider.Kind);

    /// <summary>Those examples on one line, for the caption under the box.</summary>
    public string ModelExamplesLine => string.Join(", ", RemoteModelExamples.Take(3));

    public bool HasModelExamples => RemoteModelExamples.Count > 0;

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

    // ---- consistency check: its own switch, its own model slot ----
    [ObservableProperty] private bool _consistencyAutomatically;
    [ObservableProperty] private string _consistencyModel = "";
    [ObservableProperty] private bool _consistencyUsesLedgerContext = true;
    [ObservableProperty] private bool _consistencyOtherPartyOnly;
    [ObservableProperty] private bool _extractActions = true;
    [ObservableProperty] private string _themeChoice = "system";
    [ObservableProperty] private bool _deceptionEnabled;
    [ObservableProperty] private bool _commentaryEnabled = true;
    [ObservableProperty] private string _llmRemoteModel;
    [ObservableProperty] private string _llmBaseUrl;
    [ObservableProperty] private string _llmApiKey;
    [ObservableProperty] private bool _exportToObsidian;
    [ObservableProperty] private string _obsidianVaultPath;
    [ObservableProperty] private bool _exportToNotion;
    [ObservableProperty] private string _notionApiKey;
    [ObservableProperty] private string _notionDatabaseId;
    [ObservableProperty] private int _audioRetentionDays;

    /// <summary>Shrink the nobody-talking stretches once a recording is processed.</summary>
    [ObservableProperty] private bool _trimSilence;
    [ObservableProperty] private bool _compressAudio = true;

    /// <summary>Nothing chosen for analysis: the page shows what to do instead of an empty address.</summary>
    public bool NoLlmChosen => SelectedProvider.Kind == LlmProviderKind.None;

    partial void OnSelectedProviderChanged(LlmProvider value)
    {
        OnPropertyChanged(nameof(NoLlmChosen));

        // A real value in the box, not a placeholder. A grey hint reads as an empty required
        // field and sends people hunting for an address the application already knows.
        LlmBaseUrl = value.DefaultBaseUrl;
        LlmStatus = null;
        DiscoveredLlmModels.Clear();

        OnPropertyChanged(nameof(UsesRemoteModelName));
        OnPropertyChanged(nameof(CanBrowseModels));
        OnPropertyChanged(nameof(CanDiscoverLocalServers));
        OnPropertyChanged(nameof(CanDiscoverNow));
        OnPropertyChanged(nameof(RemoteModelExamples));
        OnPropertyChanged(nameof(ModelExamplesLine));
        OnPropertyChanged(nameof(HasModelExamples));
        Revalidate();
    }
    partial void OnSelectedAsrModelChanged(AsrModel value)
    {
        AnnounceSelection();
        Revalidate();
    }
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
    [ObservableProperty] private bool _assignContactFromTitle;

    /// <summary>
    /// Transcribe group calls too, accepting that everyone at the far end is one mixed stream.
    ///
    /// Read by the orchestrator since it was written, and bound to nothing — so it could only ever
    /// hold its default unless somebody edited settings.json by hand.
    /// </summary>
    [ObservableProperty] private bool _transcribeGroupCalls;
    [ObservableProperty] private string _speechVocabulary = "";
    [ObservableProperty] private bool _mixedLanguage;

    /// <summary>The language the recogniser is told to expect. Withheld entirely when MixedLanguage is on.</summary>
    [ObservableProperty] private LanguageChoice _spokenLanguage = new("tr", "Türkçe");

    /// <summary>Whether a strip appears at the top of the screen while recording.</summary>
    [ObservableProperty] private bool _showRecordingBar = true;

    /// <summary>Recognise the far end by voice. Off until chosen — it stores biometric data.</summary>
    [ObservableProperty] private bool _identifySpeakers;

    /// <summary>Owned by the health page; carried here so saving other settings keeps it.</summary>
    /// <summary>How much the log records. Three positions, because two were not enough.</summary>
    [ObservableProperty] private LogDetail _logDetail = LogDetail.Verbose;

    public IReadOnlyList<LogDetail> LogDetails { get; } =
        [LogDetail.Normal, LogDetail.Verbose, LogDetail.Debug];

    /// <summary>Whether Windows starts this application at logon. Reconciled by AutoStart on save.</summary>
    [ObservableProperty] private bool _startWithWindows = true;

    /// <summary>
    /// A language the ComboBox can actually display.
    ///
    /// A record, not a tuple, and that is the whole point: WPF's DisplayMemberPath resolves
    /// PROPERTIES by reflection, and a ValueTuple's names exist only at compile time — bound
    /// as tuples, the dropdown rendered every language as a blank row and the selection as an
    /// empty box. Same trap as Dapper and DateOnly: tuples don't carry their names to runtime.
    /// </summary>
    public sealed record LanguageChoice(string Code, string Name);

    /// <summary>Which language the interface is shown in.</summary>
    [ObservableProperty] private LanguageChoice _uiLanguage =
        new(Localisation.Available[0].Code, Localisation.Available[0].Name);

    public IReadOnlyList<LanguageChoice> UiLanguages { get; } =
        [.. Localisation.Available.Select(l => new LanguageChoice(l.Code, l.Name))];

    /// <summary>
    /// What is spoken on calls — a different question from what the screen is written in, and one
    /// the screen had been claiming to answer without offering anywhere to answer it.
    /// </summary>
    public IReadOnlyList<LanguageChoice> SpokenLanguages { get; } =
        [.. AppSettings.SpokenLanguages.Select(l => new LanguageChoice(l.Code, l.Name))];

    /// <summary>True once a different language has been picked, so the note about restarting shows.</summary>
    public bool LanguageChanged => UiLanguage.Code != Localisation.Language;

    partial void OnUiLanguageChanged(LanguageChoice value) =>
        OnPropertyChanged(nameof(LanguageChanged));

    partial void OnRecordWhatsAppChanged(bool value) => Revalidate();
    partial void OnRecordTelegramChanged(bool value) => Revalidate();
    partial void OnRecordSignalChanged(bool value) => Revalidate();
    partial void OnLlmApiKeyChanged(string value) => Revalidate();

    // Notion's two fields were the last pair that did not re-check themselves. Validate() looks at
    // both, so switching the export on raised "anahtar ve veritabanı kimliği gerekli" — and then
    // typing them changed nothing on screen until some unrelated field moved or Kaydet was
    // pressed. The same failure the STT cards had, in the one place it had not been fixed.
    partial void OnNotionApiKeyChanged(string value) => Revalidate();

    partial void OnNotionDatabaseIdChanged(string value) => Revalidate();
    partial void OnAsrApiKeyChanged(string value) => Revalidate();

    partial void OnAsrModeChanged(TranscriptionMode value)
    {
        OnPropertyChanged(nameof(UsesCloudAsr));
        OnPropertyChanged(nameof(UsesLocalAsr));
        Revalidate();
    }
    partial void OnLlmRemoteModelChanged(string value) => Revalidate();
    partial void OnLlmBaseUrlChanged(string value) => Revalidate();

    // ---- hosted services ----------------------------------------------------

    [RelayCommand]
    private void AddEndpoint() => AddEndpointFor(SttProviderCatalog.All[0]);

    /// <summary>
    /// Adds a card for the chosen service and, in the local-only mode, switches to automatic —
    /// a service added and never used is the state the old hidden block produced.
    /// </summary>
    [RelayCommand]
    private void AddEndpointFor(SttProviderInfo provider)
    {
        var endpoint = SttEndpoint.FromProvider(provider);
        SttEndpoints.Add(new SttEndpointViewModel(endpoint, _probe));

        if (AsrMode == TranscriptionMode.LocalOnly) AsrMode = TranscriptionMode.Automatic;

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

    /// <summary>
    /// Whether looking for a local server is worth offering right now.
    ///
    /// Two conditions, combined here because a control takes one IsEnabled: not already busy, and
    /// a provider that could plausibly be running on this machine. The second half is the fix —
    /// the button wrote 127.0.0.1 into the address box whatever was selected, so pressing it with
    /// Anthropic chosen replaced a fixed, correct address with one that cannot work.
    /// </summary>
    public bool CanDiscoverNow => CanDiscoverLocalServers && !IsTestingLlm;

    partial void OnIsTestingLlmChanged(bool value) => OnPropertyChanged(nameof(CanDiscoverNow));
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
    private (LlmProviderKind Kind, string BaseUrl, string? Key)? _llmModelsFetchedFor;

    /// <summary>
    /// Fills the analysis model box from the provider when it is opened, once per key — the
    /// transcription box already did; this one waited for "Bağlantıyı sına".
    /// </summary>
    [RelayCommand]
    private async Task RefreshLlmModelsAsync()
    {
        if (IsTestingLlm || !ModelDirectory.CanFetch(SelectedProvider.Kind)) return;

        var baseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl) ? SelectedProvider.DefaultBaseUrl : LlmBaseUrl.Trim();
        var key = string.IsNullOrWhiteSpace(LlmApiKey) ? null : LlmApiKey;
        var fetchKey = (SelectedProvider.Kind, baseUrl, key);

        if (_llmModelsFetchedFor == fetchKey || string.IsNullOrWhiteSpace(baseUrl)) return;
        if (SelectedProvider.RequiresApiKey && key is null) return;

        try
        {
            var models = await ModelDirectory.FetchAsync(_http, SelectedProvider.Kind, baseUrl, key);

            var current = LlmRemoteModel;
            DiscoveredLlmModels.Clear();
            foreach (var model in models.Select(m => m.Id).OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                DiscoveredLlmModels.Add(model);
            LlmRemoteModel = current;

            _llmModelsFetchedFor = fetchKey;
            LlmStatus = $"{DiscoveredLlmModels.Count} model listelendi.";
            LlmStatusIsGood = true;
        }
        catch (LlmException e)
        {
            LlmStatus = $"Model listesi alınamadı: {e.Message}";
            LlmStatusIsGood = false;
        }
    }

    [RelayCommand]
    private async Task TestLlmAsync()
    {
        if (IsTestingLlm) return;

        IsTestingLlm = true;
        LlmStatus = "Sınanıyor…";
        LlmStatusIsGood = false;

        var baseUrl = string.IsNullOrWhiteSpace(LlmBaseUrl)
            ? SelectedProvider.DefaultBaseUrl
            : LlmBaseUrl.Trim();

        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                LlmStatus = "Önce sağlayıcının adresini gir.";
                return;
            }

            var key = string.IsNullOrWhiteSpace(LlmApiKey) ? null : LlmApiKey;

            // Through the provider's own client, not the transcription probe.
            //
            // The probe speaks one dialect: a bearer token and nothing else. Anthropic wants
            // x-api-key and a version header, so every request it made was rejected with a 400 —
            // and the probe counts anything that is not a 401 or 403 as authorised. The result was
            // the worst kind of broken: a green tick over a key that could not work, shown by the
            // one control whose entire job is to catch that before a conversation is wasted on it.
            if (SelectedProvider.RequiresApiKey && key is null)
            {
                LlmStatus = "Önce API anahtarını gir.";
                return;
            }

            var client = LlmClientFactory.Create(_http, SelectedProvider.Kind, baseUrl, key);

            // The answer, not a boolean: a refused key, a wrong address and a dead network need
            // three different fixes and used to read as one sentence.
            var probe = await client.ProbeAsync();

            DiscoveredLlmModels.Clear();

            if (!probe.Reachable || !probe.Authorised || probe.StatusCode is < 200 or >= 300)
            {
                LlmStatus = $"{SelectedProvider.DisplayName}: {probe.Message}";
                return;
            }

            // Reachable is the answer. The catalogue is a bonus, and a provider that does not
            // publish one must not turn a working connection into a failure.
            if (!ModelDirectory.CanFetch(SelectedProvider.Kind))
            {
                LlmStatus = "Bağlantı kuruldu.";
                LlmStatusIsGood = true;
                return;
            }

            try
            {
                var models = await ModelDirectory.FetchAsync(_http, SelectedProvider.Kind, baseUrl, key);

                foreach (var model in models.Select(m => m.Id).OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                    DiscoveredLlmModels.Add(model);

                LlmStatus = $"Bağlantı kuruldu. {DiscoveredLlmModels.Count} model bulundu.";
                LlmStatusIsGood = true;
            }
            catch (LlmException e)
            {
                LlmStatus = $"Bağlantı kuruldu, model listesi alınamadı: {e.Message}";
                LlmStatusIsGood = true;
            }
        }
        catch (Exception e)
        {
            LlmStatus = $"Sınanamadı: {e.Message}";
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
        AssignContactFromTitle = AssignContactFromTitle,
        TranscribeGroupCalls = TranscribeGroupCalls,
        SpeechVocabulary = SpeechVocabulary.Trim(),
        MixedLanguage = MixedLanguage,
        Language = SpokenLanguage.Code,
        ShowRecordingBar = ShowRecordingBar,
        IdentifySpeakers = IdentifySpeakers,
        LogDetail = LogDetail,
        StartWithWindows = StartWithWindows,
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
        ConsistencyAutomatically = ConsistencyAutomatically,
        ConsistencyModel = ConsistencyModel.Trim(),
        ConsistencyUsesLedgerContext = ConsistencyUsesLedgerContext,
        ConsistencyOtherPartyOnly = ConsistencyOtherPartyOnly,
        ExtractActions = ExtractActions,
        ThemeChoice = ThemeChoice,
        DeceptionEnabled = DeceptionEnabled,
        CommentaryEnabled = CommentaryEnabled,
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
        TrimSilenceAfterProcessing = TrimSilence,
        CompressAudioAfterProcessing = CompressAudio,
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
