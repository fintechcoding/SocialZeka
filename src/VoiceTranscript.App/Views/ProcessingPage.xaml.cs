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

    // ---- the row menu: the same verbs as every other call row, from CallActions ----------------

    private static ProcessingRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ProcessingRow;

    private void RowOpen_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        CallWindow.Show(Window.GetWindow(this), row.Call.Id);
    }

    private void RowMove_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Services.CallActions.Move(Window.GetWindow(this), row.Call, row.ContactName);
    }

    private void RowRetranscribe_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Services.CallActions.Reprocess(Window.GetWindow(this), row.Call, row.ContactName, ReprocessKind.Transcribe);
    }

    private void RowReanalyse_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Services.CallActions.Reprocess(Window.GetWindow(this), row.Call, row.ContactName, ReprocessKind.Analyse);
    }

    private async void RowShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) await Services.CallActions.ShowInFolderAsync(Window.GetWindow(this), row.Call);
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) await Services.CallActions.DeleteAsync(Window.GetWindow(this), row.Call, row.ContactName);
    }

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

    /// <summary>
    /// Stops the running job and empties the queue behind it.
    ///
    /// Said out loud afterwards, with the count: a button that silently removes thirty-nine
    /// recordings from a list is indistinguishable from one that deleted them, and the whole
    /// reassurance here is that nothing was lost.
    /// </summary>
    private void StopEverything_Click(object sender, RoutedEventArgs e)
    {
        var dropped = App.Orchestrator?.StopEverything() ?? 0;

        if (ViewModel is { } model)
        {
            model.Notice = dropped == 0
                ? "İşlem durduruldu. Kayıt duruyor, yeniden işlenebilir."
                : $"İşlem durduruldu, sıradaki {dropped} kayıt da beklemeye alındı. Hiçbiri silinmedi.";
        }
    }

    /// <summary>Re-analyses one row straight from its text — the analysis tab's whole point.</summary>
    private void ReanalyseRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProcessingRow row }) return;

        ViewModel?.Requeue([row], new ReprocessRequest([], null, null, AnalyseOnly: true));
    }

    /// <summary>The button inside a guidance note: straight to the section the note names.</summary>
    private void RowOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        // Guidance on these rows is about services — transcription or analysis. Analysis is the
        // overwhelmingly common case ("çalışan bir yapay zekâ servisi yok"), and landing one
        // section over is still a hundred times better than landing at the front door.
        var section = (sender as FrameworkElement)?.DataContext is ProcessingRow { HasTranscript: true }
            ? "Analysis"
            : "Transcription";

        (Window.GetWindow(this) as MainWindow)?.OpenSettings(section);
    }

    // ---- the counters as doors ----------------------------------------------

    private void Go(int tab, Action<ProcessingViewModel> filter)
    {
        if (ViewModel is not { } model) return;

        Tabs.SelectedIndex = tab;
        filter(model);
    }

    private void CounterWaiting_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Go(0, m => m.TranscriptFilter = TranscriptFilter.Unfinished);

    private void CounterTranscriptFailed_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Go(0, m => m.TranscriptFilter = TranscriptFilter.Unfinished);

    private void CounterUnanalysed_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Go(1, m => m.AnalyseFilter = AnalyseFilter.Unanalysed);

    private void CounterReady_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Go(1, m => m.AnalyseFilter = AnalyseFilter.Done);

    private void DismissNotice_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } model) model.Notice = null;
    }
}
