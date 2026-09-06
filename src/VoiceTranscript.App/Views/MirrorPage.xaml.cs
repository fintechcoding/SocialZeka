using System.Windows;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class MirrorPage
{
    public MirrorPage()
    {
        InitializeComponent();

        // The page owns the dialogs, as the promises page does: a view model does not open windows.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MirrorViewModel previous) previous.ExplainRequested -= OnExplain;
            if (e.NewValue is MirrorViewModel next) next.ExplainRequested += OnExplain;
        };
    }

    /// <summary>
    /// "neden ▸": why a measure somebody might look for is not on this page.
    ///
    /// In a dialog rather than as a permanent paragraph because the reasons are long and are read
    /// once. They are the product's reasoning — the transcribers normalise dialect away, intent
    /// cannot be measured, emotion from speech is unvalidated in Turkish — and each is stated
    /// where the absence is noticed.
    /// </summary>
    private async void OnExplain(object? sender, (string Title, string Body) explanation) =>
        await Services.Dialogs.InfoAsync(Window.GetWindow(this), explanation.Title, explanation.Body);

    /// <summary>
    /// A dot on the curve is one conversation; clicking it opens that conversation.
    ///
    /// In the code-behind rather than as a MouseBinding on the ellipse: an InputBinding is a
    /// Freezable outside the visual tree, and a command bound through one resolves against
    /// nothing in some hosts — a dead click that no test would catch, because the markup parses.
    /// </summary>
    private void Dot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: MirrorDot dot }) return;
        if (DataContext is MirrorViewModel model) model.OpenDotCommand.Execute(dot);
    }
}
