using System.Windows;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

/// <summary>
/// What has been processed, what has not, and what went wrong.
///
/// The retry actions open a dialog rather than acting immediately, because a recording is on this
/// screen precisely when its usual route failed — and repeating that route is the one thing
/// already known not to work. The dialog asks which half to redo and with what.
/// </summary>
public partial class ProcessingPage
{
    public ProcessingPage() => InitializeComponent();

    private ProcessingViewModel? ViewModel => DataContext as ProcessingViewModel;

    private void Reprocess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProcessingRow row }) return;

        Ask([row], row.ContactName);
    }

    private void RetranscribeAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;

        Ask(model.ListedTranscriptRows, "");
    }

    /// <summary>
    /// Re-analyses everything the analysis tab lists, straight from the text.
    ///
    /// No dialog: the tab has already answered both of the dialog's questions — which half
    /// (analysis) and from what (the existing transcript). Asking again would be the screen
    /// forgetting what its own tab means.
    /// </summary>
    private void ReanalyseAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;

        model.Requeue(model.ListedAnalysisRows,
            new ReprocessRequest([], null, null, AnalyseOnly: true));
    }

    private void Ask(IReadOnlyList<ProcessingRow> rows, string subject)
    {
        if (ViewModel is not { } model || rows.Count == 0) return;

        var dialog = new ReprocessWindow(App.Repository, App.Settings, subject, rows.Count)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true) return;

        var choice = dialog.Choice;

        model.Requeue(rows, new ReprocessRequest([], choice.AsrModelId, choice.LlmModel, choice.AnalyseOnly));
    }

    /// <summary>
    /// Copies the whole failure, so it can be sent to somebody.
    ///
    /// The row shows a sentence and the expander shows the original; this is what gets it out of
    /// the application. A message you can read but not copy is one you end up retyping from a
    /// screenshot.
    /// </summary>
    private void CopyFailure_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProcessingRow row }) return;
        if (row.RawFailure is not { Length: > 0 } text) return;

        try
        {
            Clipboard.SetText(text);
            if (ViewModel is { } model) model.Notice = "Hata metni panoya kopyalandı.";
        }
        catch (Exception)
        {
            // The clipboard is regularly held by another process. The text is on screen and
            // selectable either way, so this is not worth a message box.
        }
    }

    /// <summary>Ends the running job. The queue behind it is untouched.</summary>
    private void StopCurrent_Click(object sender, RoutedEventArgs e)
        => App.Orchestrator?.StopCurrent();

    private void DismissNotice_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } model) model.Notice = null;
    }
}
