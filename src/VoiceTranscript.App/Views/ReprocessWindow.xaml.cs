using System.Windows;
using System.Windows.Input;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>One way of redoing this recording, as the list offers it.</summary>
/// <param name="Id">Catalogue identifier or model name, or null to follow the setting.</param>
/// <param name="Group">Which heading it sits under: where the audio would go.</param>
public sealed record ReprocessMethod(
    string? Id, string Name, string Detail, string Icon, bool SendsDataOffMachine, string Speed,
    string Group = ReprocessMethod.OnThisMachine, bool SendsAudio = true,
    Core.Llm.LlmProviderKind? RouteKind = null, string? RouteUrl = null)
{
    /// <summary>The row that follows the settings, whatever those currently say.</summary>
    public const string FromSettings = "Ayarlarda seçili";

    public const string OnThisMachine = "Bu makinede";
    public const string InTheCloud = "Buluta gönderilir";

    /// <summary>
    /// WHAT would leave the machine, told truthfully per row. Transcription uploads audio;
    /// analysis sends words. The badge used to say "ses" on analysis models too, which was
    /// simply false — and a privacy label caught lying once is distrusted everywhere.
    /// </summary>
    public string OffMachineLabel => SendsAudio ? "ses makineden çıkar" : "metin makineden çıkar";
}

/// <summary>Which single job this dialog is being opened for. One purpose per dialog.</summary>
public enum ReprocessKind
{
    /// <summary>Audio → text, with a chosen engine.</summary>
    Transcribe,

    /// <summary>Text → ledger, with a chosen model. Never touches the audio.</summary>
    Analyse,
}

/// <summary>What the user asked for, once the dialog closes with a yes.</summary>
/// <param name="AsrModelId">Transcription engine, or null for the configured one.</param>
/// <param name="LlmModel">Analysis model, or null for the configured one.</param>
/// <param name="AnalyseOnly">Keep the existing transcript and rebuild only the ledger.</param>
/// <param name="LlmRouteKind">A provider to use INSTEAD of the configured one, for this run —
/// how a cloud-configured archive analyses one conversation on the local server.</param>
/// <param name="LlmRouteUrl">The overriding provider's endpoint.</param>
public sealed record ReprocessChoice(
    string? AsrModelId, string? LlmModel, bool AnalyseOnly,
    Core.Llm.LlmProviderKind? LlmRouteKind = null, string? LlmRouteUrl = null);

/// <summary>
/// Asks how a recording should be redone, and with what.
///
/// The retry button used to repeat whatever the settings said — which is the one route already
/// known to have failed, because that is why the recording is here. Two choices turn "try again
/// and hope" into something useful:
///
///   <b>Which half.</b> Transcription costs hours on a machine without a usable GPU; analysis
///   costs a minute. They fail for different reasons and usually only one needs repeating.
///
///   <b>Which engine.</b> The local model when an upload keeps breaking, a hosted one when the
///   processor would take all afternoon.
///
/// Measured speeds are shown beside each engine, taken from what this machine has actually done
/// rather than from a specification. On this archive the local model ran at 0.4x real time and the
/// hosted one at 208x, and no documentation would have said so.
/// </summary>
public partial class ReprocessWindow
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyDictionary<string, double> _measured;

    private readonly ReprocessKind _kind;

    public ReprocessWindow(
        Repository repository, AppSettings settings, string subject, int count,
        ReprocessKind kind = ReprocessKind.Transcribe)
    {
        InitializeComponent();

        _settings = settings;
        _kind = kind;

        Subject.Text = count == 1 ? subject : $"{count} görüşme";

        _measured = repository
            .UsageByEngine(ProcessingStage.Transcribe)
            .Where(e => e.SpeedFactor is not null)
            .ToDictionary(e => e.Engine, e => e.SpeedFactor!.Value, StringComparer.OrdinalIgnoreCase);

        // One purpose per dialog: the title, the reassurance and the list all follow the button
        // that opened it, and nothing asks the user "hangi yarı?" a second time.
        if (kind == ReprocessKind.Analyse)
        {
            Title = "Yeniden çözümle";
            Bar.Title = "Yeniden çözümle";
            StartButton.Content = "Yeniden çözümle";
            Reassurance.Text = "Mevcut metinden çalışır; ses yeniden işlenmez. "
                             + "Kişi, etiket ve notların korunur.";

            ShowAnalysisModels();
            _ = ProbeAnalysisServiceAsync();
            _ = OfferLocalRoutesAsync();

            Hint.Text = "Modeli seç; defter ve özet metinden yeniden üretilir.";
        }
        else
        {
            Title = "Yeniden çevir";
            Bar.Title = "Yeniden çevir";
            StartButton.Content = "Yeniden çevir";

            ShowTranscriptionEngines();

            Hint.Text = _measured.Count == 0
                ? "Hız sütunu, bu makinede ölçüldükçe dolar."
                : "Hız, bu makinede gerçekten ölçülen değerlerdir.";
        }
    }

    /// <summary>What was chosen. Valid once the dialog closes with a result.</summary>
    public ReprocessChoice Choice { get; private set; } = new(null, null, false);

    private void ShowTranscriptionEngines()
    {
        ListHeading.Text = "Hangi motorla yazıya dökülsün";

        List<ReprocessMethod> methods =
        [
            new(null, "Ayarlardaki yol", DescribeAsr(_settings), "Settings24",
                _settings.AsrMode != TranscriptionMode.LocalOnly, "",
                ReprocessMethod.FromSettings),
        ];

        // Everything in the catalogue that could actually start here. The catalogue already knows
        // which need a card and which do not, so nothing impossible is offered.
        //
        // Machine first, then cloud, because the headings follow the order rows appear in — and on
        // a list of ways to handle a recorded phone call, the one that keeps the audio here is the
        // right one to meet first.
        foreach (var model in AsrCatalog.All
                     .Where(m => m.RunsOnCpu || m.VramGb > 0)
                     .OrderBy(m => m.SendsAudioOffMachine))
        {
            methods.Add(new ReprocessMethod(
                model.Id,
                model.DisplayName,
                model.SendsAudioOffMachine
                    ? "Ses bu servise yüklenir."
                    : model.VramGb > 0
                        ? $"Bu makinede · ekran kartı gerekir ({model.VramGb:0.#} GB)"
                        : "Bu makinede · işlemcide çalışır",
                model.SendsAudioOffMachine ? "Cloud24" : "Desktop24",
                model.SendsAudioOffMachine,
                SpeedOf(model),
                model.SendsAudioOffMachine
                    ? ReprocessMethod.InTheCloud
                    : ReprocessMethod.OnThisMachine));
        }

        Bind(methods);
    }

    private void ShowAnalysisModels()
    {
        ListHeading.Text = "Hangi modelle çözümlensin";

        var provider = _settings.Provider;

        var where = provider.SendsDataOffMachine
            ? ReprocessMethod.InTheCloud
            : ReprocessMethod.OnThisMachine;

        List<ReprocessMethod> methods =
        [
            new(null, "Ayarlardaki model",
                $"{provider.DisplayName} · {_settings.ResolvedModelName}",
                "Settings24", provider.SendsDataOffMachine, "", ReprocessMethod.FromSettings,
                SendsAudio: false),
        ];

        // Only the ones this provider is addressed by name, and only for providers that host their
        // own models. A local server serves whichever file was loaded and largely ignores the
        // field, so offering a list there would be offering a choice that does nothing.
        if (_settings.UsesRemoteModelName)
        {
            foreach (var pick in VoiceTranscript.Core.Llm.ModelRecommendations.For(provider.Kind))
            {
                methods.Add(new ReprocessMethod(
                    pick.Id, pick.Id, pick.Reason, "Lightbulb24", provider.SendsDataOffMachine,
                    "", where, SendsAudio: false));
            }
        }

        _analysisMethods = methods;
        Bind(methods);
    }

    private List<ReprocessMethod>? _analysisMethods;

    /// <summary>
    /// Asks the local OpenAI-compatible servers whether anybody is home, and lists the ones
    /// that answer — which is what makes "yerel / bulut" a real choice on this dialog rather
    /// than a filter over a single world. Only servers that actually respond are offered:
    /// a dead route on a list of ways to spend money is a trap, not an option.
    ///
    /// Ollama is deliberately absent — it addresses models by tag, and this dialog cannot know
    /// which tags are pulled; llama-server and LM Studio serve whatever is loaded.
    /// </summary>
    private async Task OfferLocalRoutesAsync()
    {
        if (_analysisMethods is null) return;

        List<ReprocessMethod> found = [];

        foreach (var kind in new[] { Core.Llm.LlmProviderKind.LlamaServer, Core.Llm.LlmProviderKind.LmStudio })
        {
            if (kind == _settings.LlmProvider) continue; // already the settings row

            var candidate = Core.Llm.LlmProviders.Get(kind);

            try
            {
                var client = Core.Llm.LlmClientFactory.Create(
                    App.HttpClient, kind, candidate.DefaultBaseUrl, apiKey: null);

                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (!await client.IsAvailableAsync(deadline.Token)) continue;
            }
            catch (Exception)
            {
                continue; // not running — the ordinary case, not a fault
            }

            found.Add(new ReprocessMethod(
                null, candidate.DisplayName,
                "Bu makinede · sunucuda yüklü olan model çalışır; metin makineden çıkmaz.",
                "Desktop24", SendsDataOffMachine: false, "",
                ReprocessMethod.OnThisMachine, SendsAudio: false,
                RouteKind: kind, RouteUrl: candidate.DefaultBaseUrl));
        }

        if (found.Count == 0 || _analysisMethods is null) return;

        // Machine rows right after the settings row, cloud recommendations after — the same
        // "the route that keeps data here comes first" order the transcription list uses.
        var merged = new List<ReprocessMethod> { _analysisMethods[0] };
        merged.AddRange(found);
        merged.AddRange(_analysisMethods.Skip(1));

        _analysisMethods = merged;
        Dispatcher.Invoke(() => Bind(merged));
    }

    /// <summary>
    /// Asks the configured analysis service whether it answers, before the user commits work to
    /// it — and, where the provider exposes one, reads the remaining balance. The alternative
    /// flow was "seç, bekle, patla": pick a model, watch a queue, read a 401 later.
    /// </summary>
    private async Task ProbeAnalysisServiceAsync()
    {
        var provider = _settings.Provider;

        ServiceLine.Visibility = Visibility.Visible;
        ServiceText.Text = $"{provider.DisplayName} yoklanıyor…";

        try
        {
            var client = Core.Llm.LlmClientFactory.Create(
                App.HttpClient, _settings.LlmProvider, _settings.ResolvedBaseUrl, _settings.LlmApiKey);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reachable = await client.IsAvailableAsync(deadline.Token);

            if (!reachable)
            {
                ServiceIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PlugDisconnected24;
                ServiceText.Text = $"{provider.DisplayName} yanıt vermiyor ({_settings.ResolvedBaseUrl}). "
                                 + "Ayarlar → Çözümleme bölümünden denetle.";
                return;
            }

            ServiceIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PlugConnected24;
            ServiceText.Text = $"{provider.DisplayName} bağlandı.";

            // Balance, only where an endpoint for it exists. OpenRouter publishes one; OpenAI
            // and Anthropic do not, and pretending otherwise would just be a broken number.
            if (_settings.LlmProvider == Core.Llm.LlmProviderKind.OpenRouter)
            {
                var balance = await Core.Llm.LlmBalance.OpenRouterAsync(
                    App.HttpClient, _settings.LlmApiKey, deadline.Token);

                if (balance is not null) ServiceText.Text += $" {balance}";
            }
            else if (provider.SendsDataOffMachine)
            {
                ServiceText.Text += " Bakiye ucu sunmuyor — kalanı sağlayıcının panelinden gör.";
            }
        }
        catch (Exception)
        {
            ServiceIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PlugDisconnected24;
            ServiceText.Text = $"{provider.DisplayName} yoklanamadı.";
        }
    }

    private System.Windows.Data.ListCollectionView? _view;
    private string _scope = "All";

    private void Bind(List<ReprocessMethod> methods)
    {
        // Grouped by where the audio goes. That is the one part of this choice that cannot be
        // taken back — speed and cost are recoverable mistakes, an upload is not — so it is the
        // structure of the list rather than a caption on some rows.
        _view = new System.Windows.Data.ListCollectionView(methods);
        _view.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(ReprocessMethod.Group)));

        Methods.ItemsSource = _view;
        Methods.SelectedIndex = 0;

        // The filter only earns its place when both worlds are actually on the list — with a
        // dozen local engines above the cloud rows, "Bulut" saves a scroll to the bottom.
        var worlds = methods
            .Where(m => m.Group != ReprocessMethod.FromSettings)
            .Select(m => m.Group)
            .Distinct()
            .Count();

        ScopeBar.Visibility = worlds > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Narrows the list to one world. The settings row is never filtered out: the default
    /// route staying visible is what makes "hiçbirini seçme" a real option.
    /// </summary>
    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (_view is null || sender is not FrameworkElement { Tag: string scope }) return;

        _scope = scope;

        foreach (var (button, tag) in new[]
                 {
                     (ScopeAllButton, "All"), (ScopeLocalButton, "Local"), (ScopeCloudButton, "Cloud"),
                 })
        {
            button.Appearance = tag == _scope
                ? Wpf.Ui.Controls.ControlAppearance.Primary
                : Wpf.Ui.Controls.ControlAppearance.Secondary;
        }

        _view.Filter = _scope switch
        {
            "Local" => o => o is ReprocessMethod m
                && m.Group is ReprocessMethod.FromSettings or ReprocessMethod.OnThisMachine,
            "Cloud" => o => o is ReprocessMethod m
                && m.Group is ReprocessMethod.FromSettings or ReprocessMethod.InTheCloud,
            _ => null,
        };

        if (Methods.SelectedItem is null) Methods.SelectedIndex = 0;
    }

    private static string DescribeAsr(AppSettings settings) => settings.AsrMode switch
    {
        TranscriptionMode.LocalOnly => $"Yalnızca bu makinede · {settings.AsrModel.DisplayName}",
        TranscriptionMode.CloudOnly => "Yalnızca buluta gönder",
        _ => $"Önce {settings.AsrModel.DisplayName}, olmazsa buluta",
    };

    /// <summary>
    /// What this engine has actually managed here, rather than what its documentation claims.
    ///
    /// Matched loosely: the recorded name is whatever the worker reported — a model reference, not
    /// a catalogue identifier — so an exact comparison would leave every row blank.
    /// </summary>
    private string SpeedOf(AsrModel model)
    {
        foreach (var (engine, speed) in _measured)
        {
            if (engine.Contains(model.ModelRef, StringComparison.OrdinalIgnoreCase)
                || model.ModelRef.Contains(engine, StringComparison.OrdinalIgnoreCase))
            {
                return $"{speed:0.#}×";
            }
        }

        return "";
    }

    private void Methods_DoubleClick(object sender, MouseButtonEventArgs e) => Start_Click(sender, e);

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Methods.SelectedItem is not ReprocessMethod method) return;

        Choice = _kind == ReprocessKind.Analyse
            ? new ReprocessChoice(null, method.Id, AnalyseOnly: true, method.RouteKind, method.RouteUrl)
            : new ReprocessChoice(method.Id, null, AnalyseOnly: false);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
