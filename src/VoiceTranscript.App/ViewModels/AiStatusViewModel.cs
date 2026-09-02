using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>Whether a service answered when it was asked.</summary>
public enum ServiceReach
{
    /// <summary>Not asked yet, or the answer is stale.</summary>
    Unknown,

    /// <summary>No service chosen at all — nothing to reach.</summary>
    NotChosen,

    /// <summary>Answered. Work sent here will be attempted.</summary>
    Reachable,

    /// <summary>Did not answer. Work will fall through to the next one, or be skipped.</summary>
    Unreachable,

    /// <summary>Runs on this machine, so there is nothing to reach.</summary>
    Local,

    /// <summary>Configured but missing something it needs — usually a key.</summary>
    Incomplete,
}

/// <summary>
/// One day in the usage chart.
/// </summary>
/// <param name="Share">Height as a fraction of the busiest day in view, 0–1.</param>
public sealed record DayBar(string Label, double Minutes, double Share, int Runs)
{
    /// <summary>
    /// Pixel height, with a visible floor for days that had work.
    ///
    /// A day with two minutes of audio beside a day with four hours would otherwise round to
    /// nothing and read as a day off, which is a different fact.
    /// </summary>
    public double Height => Runs == 0 ? 0 : Math.Max(3, Share * 90);

    public string Tooltip => Runs == 0
        ? $"{Label}: kayıt işlenmedi"
        : $"{Label}: {Runs} görüşme · {Minutes:0} dk ses";
}

/// <summary>One service in a chain, with its place in the order.</summary>
/// <param name="Order">1-based. The order they will actually be tried in.</param>
/// <param name="IsActive">The one that will be used, all being well — the first usable.</param>
public sealed partial class AiServiceRow(
    int order, string name, string detail, bool isActive, bool sendsDataOffMachine,
    bool transcription = true) : ObservableObject
{
    public int Order { get; } = order;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool IsActive { get; } = isActive;

    /// <summary>Whether using this one means the conversation leaves the machine.</summary>
    public bool SendsDataOffMachine { get; } = sendsDataOffMachine;

    /// <summary>Which half of the pipeline this row belongs to — it decides where a click lands.</summary>
    public bool IsTranscription { get; } = transcription;

    /// <summary>
    /// WHAT leaves the machine, said truthfully per row. Transcription uploads the audio;
    /// analysis sends the words. The badge used to say "ses" on both, which for the analysis
    /// row was simply false — and a privacy label that is wrong once is distrusted everywhere.
    /// </summary>
    public string OffMachineLabel => IsTranscription ? "ses makineden çıkar" : "metin makineden çıkar";

    [ObservableProperty] private ServiceReach _reach = ServiceReach.Unknown;

    public string ReachText => Reach switch
    {
        ServiceReach.Reachable => "bağlandı",
        ServiceReach.Unreachable => "yanıt vermiyor",
        ServiceReach.Local => "bu makinede",
        ServiceReach.Incomplete => "anahtar eksik",
        ServiceReach.NotChosen => "seçilmedi",
        _ => "denenmedi",
    };

    public string ReachBrushKey => Reach switch
    {
        ServiceReach.Reachable or ServiceReach.Local => "SystemFillColorSuccessBrush",
        ServiceReach.Unreachable => "SystemFillColorCriticalBrush",
        ServiceReach.Incomplete => "SystemFillColorCautionBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    partial void OnReachChanged(ServiceReach value)
    {
        OnPropertyChanged(nameof(ReachText));
        OnPropertyChanged(nameof(ReachBrushKey));
    }
}

/// <summary>
/// Which services will actually do the work, in the order they will be tried.
///
/// The application already made these decisions and kept them to itself. Transcription can run
/// locally or fall through an ordered list of hosted endpoints; analysis talks to whichever model
/// is configured, which may or may not be running. All of that was settled at the moment a
/// recording finished, invisibly, and the only way to find out what had been chosen was to read
/// the log afterwards — or to notice that a summary never appeared.
///
/// Two things make this worth a screen rather than a line in settings:
///
///   <b>The order is real and it matters.</b> Hosted transcription endpoints are tried in
///   sequence, so the second one is what runs when the first is out of credit. Somebody who has
///   configured three of them has expressed a preference, and preferences that cannot be seen
///   cannot be checked.
///
///   <b>"Configured" and "working" are different questions.</b> A local model server is
///   configured by default and is usually not running. Settings can only answer the first
///   question; this asks the second, out loud, on demand.
/// </summary>
public sealed partial class AiStatusViewModel(
    Func<AppSettings> settings,
    HttpClient http,
    Repository repository,
    Func<bool?>? localTranscriptionUsable = null) : ObservableObject
{
    /// <summary>Transcription, in the order it will be attempted.</summary>
    public ObservableCollection<AiServiceRow> Transcription { get; } = [];

    /// <summary>Analysis. One entry today; a list because that is the shape it will grow into.</summary>
    public ObservableCollection<AiServiceRow> Analysis { get; } = [];

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private DateTimeOffset? _checkedAt;

    /// <summary>One line for the places that have room for one line.</summary>
    public string Summary
    {
        get
        {
            var stt = Transcription.FirstOrDefault(r => r.IsActive)?.Name ?? "yok";
            var llm = Analysis.FirstOrDefault(r => r.IsActive)?.Name ?? "yok";

            return $"Yazıya dökme: {stt} · Çözümleme: {llm}";
        }
    }

    /// <summary>True when analysis is switched off, so its absence is a choice rather than a fault.</summary>
    public bool AnalysisDisabled => !settings().AnalyseAutomatically;

    /// <summary>
    /// Raised when the user wants to change one of these services.
    ///
    /// The screen answers "what is running and is it working", and the answer is regularly "it is
    /// not". Making them find Settings themselves at that moment is asking somebody who has just
    /// been told something is wrong to go and look for the door.
    ///
    /// An event rather than opening the window here: the shell already owns that flow — building
    /// the settings model, saving the result, reconciling autostart — and a second copy of it
    /// would be a second place for the two to drift apart.
    /// </summary>
    public event EventHandler<string?>? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, "Analysis");

    /// <summary>A service row was clicked: open settings at the section that configures it.</summary>
    public void OpenSectionFor(bool transcription)
        => SettingsRequested?.Invoke(this, transcription ? "Transcription" : "Analysis");

    [RelayCommand]
    public void Refresh()
    {
        var current = settings();

        Transcription.Clear();
        Analysis.Clear();

        BuildTranscription(current);
        BuildAnalysis(current);

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AnalysisDisabled));

        LoadUsage();
    }

    private void BuildTranscription(AppSettings current)
    {
        var order = 1;

        // The local engine comes first whenever it is allowed to, because that is the order the
        // orchestrator resolves in: local unless the mode forbids it or the hardware cannot.
        if (current.AsrMode != TranscriptionMode.CloudOnly)
        {
            // The route the orchestrator actually takes, not an assumption that it is local.
            //
            // This screen exists to say what leaves the machine. Hard-coding "local works" made
            // it list the local engine as active on a machine where every call was being
            // uploaded — the one screen whose entire job is to prevent that surprise. Unknown
            // (before the first probe) is shown as local, which is what the orchestrator does
            // too, so the two never disagree.
            var model = current.ResolveAsrModel(
                localTranscriptionUsable: localTranscriptionUsable?.Invoke() ?? true);

            Transcription.Add(new AiServiceRow(
                order++,
                model.DisplayName,
                $"bu makinede · {current.AsrDevice}",
                isActive: true,
                sendsDataOffMachine: false)
            {
                Reach = ServiceReach.Local,
            });
        }

        // Then the hosted endpoints, in the order they are actually tried.
        foreach (var endpoint in current.UsableSttEndpoints)
        {
            Transcription.Add(new AiServiceRow(
                order,
                endpoint.ResolvedName,
                endpoint.BaseUrl,
                isActive: order == 1,
                sendsDataOffMachine: true));

            order++;
        }

        if (Transcription.Count == 0)
        {
            Transcription.Add(new AiServiceRow(
                1, "Yapılandırılmamış", "Ayarlardan bir yol seç", isActive: false, sendsDataOffMachine: false)
            {
                Reach = ServiceReach.Incomplete,
            });
        }
    }

    private void BuildAnalysis(AppSettings current)
    {
        var provider = current.Provider;

        var reach = current.LlmProvider == Core.Llm.LlmProviderKind.None
            ? ServiceReach.NotChosen
            : !current.LlmReachableInPrinciple
                ? ServiceReach.Incomplete
                : ServiceReach.Unknown;

        Analysis.Add(new AiServiceRow(
            1,
            provider.DisplayName,
            $"{current.ResolvedModelName} · {current.ResolvedBaseUrl}",
            isActive: true,
            provider.SendsDataOffMachine,
            transcription: false)
        {
            Reach = reach,
        });
    }

    /// <summary>
    /// Asks each service whether it is there.
    ///
    /// On demand rather than continuously. These are network calls against services the user is
    /// paying for or running themselves, and polling them in the background to keep a dot green
    /// would be spending somebody's money to decorate a screen.
    /// </summary>
    [RelayCommand]
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsChecking) return;

        IsChecking = true;

        try
        {
            var current = settings();

            foreach (var row in Analysis.Concat(Transcription))
            {
                if (row.Reach is ServiceReach.Local or ServiceReach.Incomplete) continue;

                row.Reach = ServiceReach.Unknown;
            }

            // Analysis first: it is the one that is usually configured and not running, so it is
            // the answer somebody opening this screen is most likely looking for.
            if (current.LlmReachableInPrinciple)
            {
                var client = LlmClientFactory.Create(
                    http, current.LlmProvider, current.ResolvedBaseUrl, current.LlmApiKey);

                Analysis[0].Reach = await ReachAsync(
                    token => client.IsAvailableAsync(token), cancellationToken);
            }

            var probe = new SttProbe(http);

            foreach (var row in Transcription.Where(r => r.SendsDataOffMachine))
            {
                var endpoint = current.UsableSttEndpoints
                    .FirstOrDefault(e => e.ResolvedName == row.Name);

                if (endpoint is null) continue;

                // Reachable and authorised, not merely answering: an endpoint that responds
                // with 401 will fail every real request, and calling that "bağlandı" would
                // send somebody looking for the fault everywhere except at their key.
                row.Reach = await ReachAsync(
                    async token =>
                    {
                        var result = await probe.TestAsync(endpoint, token);
                        return result.Reachable && result.Authorised;
                    },
                    cancellationToken);
            }

            CheckedAt = DateTimeOffset.Now;
        }
        finally
        {
            IsChecking = false;
        }
    }

    // ---- what it has cost ---------------------------------------------------

    /// <summary>
    /// Whether the figures cover the last thirty days or the whole archive.
    ///
    /// Two windows rather than one because they answer different questions. "How fast is this
    /// machine" is a question about now — it changes when a driver breaks or a GPU is added, and a
    /// lifetime average buries that under months of old runs. "What has this cost me" is a
    /// question about the total.
    /// </summary>
    [ObservableProperty] private bool _recentOnly = true;

    /// <summary>
    /// How many days the chart covers. Zero means the whole archive, which has no chart.
    ///
    /// Three lengths rather than a date picker: the questions people actually ask here are "what
    /// has this week been like", "is it keeping up this month", and "what has all of it cost".
    /// A calendar would answer a question nobody asked and cost a dialog.
    /// </summary>
    [ObservableProperty] private int _windowDays = 30;

    /// <summary>One bar per day, empty days included so gaps stay visible.</summary>
    public ObservableCollection<DayBar> Days { get; } = [];

    public bool HasChart => Days.Any(d => d.Minutes > 0);

    [RelayCommand]
    private void ShowWeek() => SetWindow(7);

    [RelayCommand]
    private void ShowMonth() => SetWindow(30);

    [RelayCommand]
    private void ShowEverything() => SetWindow(0);

    private void SetWindow(int days)
    {
        WindowDays = days;
        RecentOnly = days > 0;

        LoadUsage();

        OnPropertyChanged(nameof(IsWeek));
        OnPropertyChanged(nameof(IsMonth));
        OnPropertyChanged(nameof(IsEverything));
    }

    public bool IsWeek => WindowDays == 7;
    public bool IsMonth => WindowDays == 30;
    public bool IsEverything => WindowDays == 0;

    [ObservableProperty] private UsageTotals _transcribeUsage = new();
    [ObservableProperty] private UsageTotals _analyseUsage = new();
    [ObservableProperty] private UsageTotals _askUsage = new();

    /// <summary>Per-engine transcription figures, so a local model and a hosted one can be compared.</summary>
    public ObservableCollection<EngineUsage> Engines { get; } = [];

    /// <summary>Per-model analysis spend, tokens included — the itemised half of the bill.</summary>
    public ObservableCollection<EngineUsage> AnalysisEngines { get; } = [];

    public string WindowName => WindowDays switch
    {
        7 => "Son 7 gün",
        30 => "Son 30 gün",
        _ => "Tüm zaman",
    };

    /// <summary>
    /// Builds the daily bars, scaled to the busiest day in view.
    ///
    /// Scaled to the window rather than to a fixed maximum: the point of the chart is the shape of
    /// one person's own weeks, and a scale chosen for somebody else's archive would flatten theirs
    /// into a line.
    /// </summary>
    private void LoadChart()
    {
        Days.Clear();

        if (WindowDays <= 0)
        {
            OnPropertyChanged(nameof(HasChart));
            return;
        }

        var series = repository.DailyUsage(ProcessingStage.Transcribe, WindowDays);
        var busiest = series.Max(d => d.AudioMs);

        foreach (var day in series)
        {
            Days.Add(new DayBar(
                day.Label,
                day.Audio.TotalMinutes,
                busiest > 0 ? (double)day.AudioMs / busiest : 0,
                day.Runs));
        }

        OnPropertyChanged(nameof(HasChart));
    }

    public bool HasUsage => TranscribeUsage.Runs > 0 || AnalyseUsage.Runs > 0 || AskUsage.Runs > 0;

    public string TranscribeLine => TranscribeUsage.Runs == 0
        ? "Bu aralıkta hiçbir görüşme yazıya dökülmedi."
        : $"{TranscribeUsage.Runs} görüşme · {Span(TranscribeUsage.Audio)} ses · "
          + $"{Span(TranscribeUsage.Elapsed)} işlem süresi";

    /// <summary>
    /// The speed line, and the reason this screen exists.
    ///
    /// Below one means an hour of conversation costs more than an hour to transcribe, so the
    /// backlog grows for as long as calls keep being made. That is a thing somebody needs told
    /// plainly rather than left to infer from a progress bar that never empties.
    /// </summary>
    public string SpeedLine => TranscribeUsage.SpeedFactor switch
    {
        null => "",
        < 1 => $"Gerçek zamanın {TranscribeUsage.SpeedFactor:0.0} katı — ses, işlenmesinden hızlı "
             + "birikiyor. GPU yoksa beklenen davranış budur.",
        { } speed => $"Gerçek zamanın {speed:0.0} katı hızda işleniyor.",
    };

    public bool SpeedIsPoor => TranscribeUsage.SpeedFactor is < 1;

    public string AnalyseLine => AnalyseUsage.Runs == 0
        ? "Bu aralıkta hiçbir görüşme çözümlenmedi."
        : $"{AnalyseUsage.Runs} görüşme · {Span(AnalyseUsage.Elapsed)} · "
          + (AnalyseUsage.TotalTokens > 0
              ? $"{Tokens(AnalyseUsage.PromptTokens)} giriş + {Tokens(AnalyseUsage.CompletionTokens)} çıkış jeton"
              : "jeton bildirilmedi");

    /// <summary>
    /// Questions asked of the archive, counted apart from analysis.
    ///
    /// The same paid endpoint, spent for a different reason: analysis runs once per call whether
    /// anyone is watching, and this runs because somebody asked. Shown separately so a bill that
    /// looks too large can be traced to whichever of the two caused it.
    /// </summary>
    public string AskLine => AskUsage.Runs == 0
        ? ""
        : $"Sorular: {AskUsage.Runs} soru · "
          + (AskUsage.TotalTokens > 0
              ? $"{Tokens(AskUsage.PromptTokens)} giriş + {Tokens(AskUsage.CompletionTokens)} çıkış jeton"
              : "jeton bildirilmedi");

    public string FailureLine
    {
        get
        {
            var failures = TranscribeUsage.Failures + AnalyseUsage.Failures + AskUsage.Failures;

            return failures == 0 ? "" : $"{failures} deneme başarısız oldu.";
        }
    }

    [RelayCommand]
    private void SwitchWindow() => RecentOnly = !RecentOnly;

    partial void OnRecentOnlyChanged(bool value) => LoadUsage();

    private void LoadUsage()
    {
        var since = WindowDays > 0 ? DateTimeOffset.UtcNow.AddDays(-WindowDays) : (DateTimeOffset?)null;

        LoadChart();

        TranscribeUsage = repository.Usage(ProcessingStage.Transcribe, since);
        AnalyseUsage = repository.Usage(ProcessingStage.Analyse, since);
        AskUsage = repository.Usage(ProcessingStage.Ask, since);

        Engines.Clear();
        foreach (var engine in repository.UsageByEngine(ProcessingStage.Transcribe, since))
        {
            // The row stores what the worker reported ("cloud-openai", "large-v3"); the screen
            // owes the user the human name — plumbing ids leaking into the wallpaper was met
            // and named by the user.
            Engines.Add(engine with { Engine = AsrCatalog.DisplayFor(engine.Engine) });
        }

        // The paid side, model by model. Transcription is billed by the minute and analysis by
        // the token, and a bill that looks wrong can only be traced when both are itemised.
        AnalysisEngines.Clear();
        foreach (var engine in repository.UsageByEngine(ProcessingStage.Analyse, since))
            AnalysisEngines.Add(engine);

        foreach (var name in new[]
        {
            nameof(WindowName), nameof(HasUsage), nameof(HasChart),
            nameof(TranscribeLine), nameof(SpeedLine), nameof(SpeedIsPoor),
            nameof(AnalyseLine), nameof(AskLine), nameof(FailureLine),
        })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Durations in the units people say them in — never "0.03 saat".</summary>
    private static string Span(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} sa {span.Minutes} dk"
        : span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes} dk"
            : $"{(int)span.TotalSeconds} sn";

    /// <summary>Token counts, abbreviated. A raw 1_284_339 is not a number anybody reads.</summary>
    private static string Tokens(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.0}M",
        >= 1_000 => $"{count / 1_000.0:0.0}B",
        _ => count.ToString(),
    };

    /// <summary>
    /// One bounded attempt. Unreachable is an answer, not an error.
    ///
    /// The shared client is set to ten minutes because it also uploads hours of audio; a
    /// reachability check inheriting that would leave this screen spinning for the rest of the day
    /// against a service that simply is not there.
    /// </summary>
    private static async Task<ServiceReach> ReachAsync(
        Func<CancellationToken, Task<bool>> ask, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            // The deadline is handed to the probe. It used to be created, armed and then never
            // passed anywhere, so "Bağlantıyı sına" against a dead address waited out the shared
            // client's ten-minute timeout with the button greyed for the duration.
            return await ask(deadline.Token) ? ServiceReach.Reachable : ServiceReach.Unreachable;
        }
        catch (Exception)
        {
            return ServiceReach.Unreachable;
        }
    }
}
