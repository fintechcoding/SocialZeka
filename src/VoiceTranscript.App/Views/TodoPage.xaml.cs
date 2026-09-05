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
            if (e.OldValue is TodoViewModel previous)
            {
                previous.OpenCallRequested -= OnOpenCall;
                previous.PropertyChanged -= OnModelPropertyChanged;
            }

            if (e.NewValue is TodoViewModel next)
            {
                next.OpenCallRequested += OnOpenCall;
                next.PropertyChanged += OnModelPropertyChanged;
            }
        };
    }

    /// <summary>
    /// Whether the finished section is open is remembered, as the timeline view is: it is a way
    /// of reading the list, not a decision to repeat on every visit. Written to the settings the
    /// application saves when it closes.
    /// </summary>
    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TodoViewModel.ShowDone)) return;
        if (sender is not TodoViewModel model) return;
        if (App.Settings.TodoShowDone == model.ShowDone) return;

        App.Settings = App.Settings with { TodoShowDone = model.ShowDone };
    }

    /// <summary>
    /// Clicking the line opens the conversation it came from.
    ///
    /// The buttons inside the row swallow their own clicks, so ticking, refusing and deleting
    /// still do their own thing; the space between them is the way in.
    /// </summary>
    private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoEntry entry }) return;
        if (DataContext is not TodoViewModel model) return;

        model.OpenCommand.Execute(entry);
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
