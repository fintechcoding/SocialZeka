using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;

namespace VoiceTranscript.App.Views;

/// <summary>
/// One conversation, opened on its own.
///
/// The code here is only what a view model cannot do: turning a click on a bubble into a command
/// call, because the bubbles live inside an ItemsControl whose items are not the window's data
/// context and binding a command through two relative-source hops for a mouse event is harder to
/// read than four lines.
/// </summary>
public partial class CallWindow
{
    public CallWindow(CallWindowViewModel model)
    {
        InitializeComponent();

        DataContext = model;

        // The player holds a file handle and a wave device. Left alive, a window somebody opened
        // and closed keeps the recording locked, and the next thing that tries to delete or
        // re-process it fails for a reason nobody could guess from the message.
        Closed += (_, _) => model.Dispose();
    }

    private CallWindowViewModel? ViewModel => DataContext as CallWindowViewModel;

    /// <summary>Clicking a line plays from it.</summary>
    private void Turn_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn }) return;

        ViewModel?.PlayTurnCommand.Execute(turn);
    }

    /// <summary>Clicking a quote plays the moment it came from.</summary>
    private void Citation_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Excerpt excerpt }) return;

        ViewModel?.PlayExcerptCommand.Execute(excerpt);
    }

    /// <summary>Enter asks, because a single-line question box that needs the mouse is not used.</summary>
    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ViewModel is not { } model) return;

        if (model.AskCommand.CanExecute(null)) model.AskCommand.Execute(null);

        e.Handled = true;
    }
}
