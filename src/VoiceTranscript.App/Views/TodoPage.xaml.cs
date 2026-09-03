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

    private void OnOpenCall(object? sender, TodoEntry entry)
    {
        if (entry.CallId is not { } callId) return;

        // A suggestion opens on the suggestions, not on the transcript. Landing on the
        // conversation and leaving somebody to find the tab is a step this click already knows
        // the answer to.
        CallWindow.Show(
            Window.GetWindow(this), callId,
            tab: entry.Kind == TodoEntryKind.Action ? CallTab.Actions : CallTab.Conversation);
    }
}
