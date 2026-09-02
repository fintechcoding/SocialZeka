using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class CallsPage
{
    public CallsPage() => InitializeComponent();

    private CallsViewModel? ViewModel => DataContext as CallsViewModel;

    private static RecentCall? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as RecentCall;

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { } row) CallWindow.Show(Window.GetWindow(this), row.Call.Id);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) CallWindow.Show(Window.GetWindow(this), row.Call.Id);
    }

    private void Label_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        var dialog = new LabelCallWindow(
            App.Repository, row.Call.Id, row.Call.Duration, row.Call.ObservedTitle, row.Call.App,
            audioSummary: "", hasSilentStream: false)
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();
        RefreshEverywhere();
    }

    private void ToBoard_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        App.Repository.PutOnBoard(row.Call.Id, Core.Domain.BoardLane.ToLookAt);
        RefreshEverywhere();
    }

    private void Remind_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        RemindWindow.Open(Window.GetWindow(this), App.Repository, row.Call.Id, $"{row.ContactName} · {row.When}");
        RefreshEverywhere();
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Services.CallActions.Move(Window.GetWindow(this), row.Call, row.ContactName);
    }

    private void Retranscribe_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Services.CallActions.Reprocess(Window.GetWindow(this), row.Call, row.ContactName, ReprocessKind.Transcribe);
    }

    private void Reanalyse_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Services.CallActions.Reprocess(Window.GetWindow(this), row.Call, row.ContactName, ReprocessKind.Analyse);
    }

    private async void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) await Services.CallActions.ShowInFolderAsync(Window.GetWindow(this), row.Call);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) await Services.CallActions.DeleteAsync(Window.GetWindow(this), row.Call, row.ContactName);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearFilters();

    /// <summary>Board and reminder changes do not go through CallActions; the shell re-reads on request.</summary>
    private void RefreshEverywhere()
    {
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell) shell.RefreshAll();
        else ViewModel?.Refresh();
    }
}
