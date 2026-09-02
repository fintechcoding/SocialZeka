using System.Windows;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class TodoPage
{
    public TodoPage()
    {
        InitializeComponent();

        // The page owns the window, as the calendar does: a view model does not open windows.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TodoViewModel previous) previous.OpenCallRequested -= OnOpenCall;
            if (e.NewValue is TodoViewModel next) next.OpenCallRequested += OnOpenCall;
        };
    }

    private void OnOpenCall(object? sender, long callId)
    {
        CallWindow.Show(Window.GetWindow(this), callId);
    }
}
