using System.Windows;
using System.Windows.Input;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>One way of redoing this recording, as the list offers it.</summary>
/// <param name="Id">Catalogue identifier or model name, or null to follow the setting.</param>
public sealed record ReprocessMethod(
    string? Id, string Name, string Detail, string Icon, bool SendsDataOffMachine, string Speed);

/// <summary>What the user asked for, once the dialog closes with a yes.</summary>
/// <param name="AsrModelId">Transcription engine, or null for the configured one.</param>
/// <param name="LlmModel">Analysis model, or null for the configured one.</param>
/// <param name="AnalyseOnly">Keep the existing transcript and rebuild only the ledger.</param>
public sealed record ReprocessChoice(string? AsrModelId, string? LlmModel, bool AnalyseOnly);

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

    public ReprocessWindow(Repository repository, AppSettings settings, string subject, int count)
    {
        InitializeComponent();

        _settings = settings;

        Subject.Text = count == 1 ? subject : $"{count} görüşme";

        _measured = repository
            .UsageByEngine(ProcessingStage.Transcribe)
            .Where(e => e.SpeedFactor is not null)
            .ToDictionary(e => e.Engine, e => e.SpeedFactor!.Value, StringComparer.OrdinalIgnoreCase);

        ShowTranscriptionEngines();

        Hint.Text = _measured.Count == 0
            ? "Hız sütunu, bu makinede ölçüldükçe dolar."
            : "Hız, bu makinede gerçekten ölçülen değerlerdir.";
    }

    /// <summary>What was chosen. Valid once the dialog closes with a result.</summary>
    public ReprocessChoice Choice { get; private set; } = new(null, null, false);

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // Fires while the window is still being built, before the list exists.
        if (Methods is null) return;

        if (ModeAnalyse.IsChecked == true) ShowAnalysisModels();
        else ShowTranscriptionEngines();
    }

    private void ShowTranscriptionEngines()
    {
        ListHeading.Text = "Hangi motorla yazıya dökülsün";

        List<ReprocessMethod> methods =
        [
            new(null, "Ayarlardaki yol", DescribeAsr(_settings), "Settings24",
                _settings.AsrMode != TranscriptionMode.LocalOnly, ""),
        ];

        // Everything in the catalogue that could actually start here. The catalogue already knows
        // which need a card and which do not, so nothing impossible is offered.
        foreach (var model in AsrCatalog.All.Where(m => m.RunsOnCpu || m.VramGb > 0))
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
                SpeedOf(model)));
        }

        Bind(methods);
    }

    private void ShowAnalysisModels()
    {
        ListHeading.Text = "Hangi modelle çözümlensin";

        var provider = _settings.Provider;

        List<ReprocessMethod> methods =
        [
            new(null, "Ayarlardaki model",
                $"{provider.DisplayName} · {_settings.ResolvedModelName}",
                "Settings24", provider.SendsDataOffMachine, ""),
        ];

        // Only the ones this provider is addressed by name, and only for providers that host their
        // own models. A local server serves whichever file was loaded and largely ignores the
        // field, so offering a list there would be offering a choice that does nothing.
        if (_settings.UsesRemoteModelName)
        {
            foreach (var pick in VoiceTranscript.Core.Llm.ModelRecommendations.For(provider.Kind))
            {
                methods.Add(new ReprocessMethod(
                    pick.Id, pick.Id, pick.Reason, "Lightbulb24", provider.SendsDataOffMachine, ""));
            }
        }

        Bind(methods);
    }

    private void Bind(List<ReprocessMethod> methods)
    {
        Methods.ItemsSource = methods;
        Methods.SelectedIndex = 0;
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

        Choice = ModeAnalyse.IsChecked == true
            ? new ReprocessChoice(null, method.Id, AnalyseOnly: true)
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
