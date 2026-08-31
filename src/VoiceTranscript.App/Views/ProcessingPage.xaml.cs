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

    private void ReprocessAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;

        Ask(model.AllRows, "");
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

    private void DismissNotice_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } model) model.Notice = null;
    }
}
