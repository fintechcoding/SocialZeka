using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class OverviewPage
{
    public OverviewPage() => InitializeComponent();

    /// <summary>
    /// Opens the conversation a row on the first screen refers to.
    ///
    /// The rows already drew themselves as pressable and did nothing when pressed. That is worse
    /// than a plain list: it teaches somebody that the overview is a display rather than a way in,
    /// and once learned they stop trying. This makes the shortest question — "what happened
    /// today" — one click from its answer.
    /// </summary>
    /// <summary>Goes to the board.</summary>
    private void OpenBoard_Click(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell)
            shell.Page = ShellPage.Board;
    }

    /// <summary>Opens the conversation a due reminder points at.</summary>
    private void DueCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DueCard due }) return;

        Open(due.CallId);
    }

    private void RecentCall_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentCall row }) return;

        Open(row.Call.Id);
    }

    private void Open(long callId)
    {
        var window = new CallWindow(new CallWindowViewModel(
            App.Repository, () => App.Settings, App.HttpClient, callId))
        {
            Owner = Window.GetWindow(this),
        };

        // Shown rather than shown modally, for the same reason the contact page opens it that
        // way: reading a conversation while looking something else up is the ordinary way to use
        // this, and a modal window forbids it.
        window.Show();
    }
}
