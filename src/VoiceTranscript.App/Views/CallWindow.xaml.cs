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

    /// <summary>Plays from the line the menu was opened on.</summary>
    private void PlayFromHere_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn }) return;

        ViewModel?.PlayTurnCommand.Execute(turn);
    }

    /// <summary>Copies one line's words.</summary>
    private void CopyTurn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn }) return;

        try
        {
            Clipboard.SetText($"[{turn.Time}] {turn.Speaker}: {turn.Text}");
        }
        catch (Exception)
        {
            // The clipboard is held by another process often enough that failing loudly here
            // would be the more surprising behaviour.
        }
    }

    /// <summary>
    /// Cuts a line and the replies after it out as an audio file.
    ///
    /// A save dialog rather than a folder set once in settings: these files exist to be sent to
    /// somebody, so the user is choosing where to put something they are about to share, and being
    /// asked is what makes that a decision rather than a side effect.
    /// </summary>
    private void ExportExchange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn, Tag: string tag }) return;
        if (ViewModel is not { } model) return;

        var following = int.TryParse(tag, out var parsed) ? parsed : 0;
        var (from, to) = model.ExchangeRange(turn, following);

        var exporter = new Services.ClipExporter(App.Repository);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Ses kesiti nereye kaydedilsin?",
            FileName = exporter.NameFor(model.CallId, from),
            Filter = "Ses dosyası (*.wav)|*.wav",
            DefaultExt = ".wav",
        };

        if (dialog.ShowDialog() != true) return;

        var result = exporter.ExportExchange(
            model.CallId, from, to, dialog.FileName, model.ContactName);

        MessageBox.Show(result.Message, "Ses kesiti", MessageBoxButton.OK,
            result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Services.AppLog.Write(
            "kesit", result.Ok ? "görüşmeden kesit yazıldı" : $"kesit alınamadı: {result.Message}");
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
