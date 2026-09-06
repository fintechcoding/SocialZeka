using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class CalendarPage
{
    public CalendarPage() => InitializeComponent();

    private CalendarViewModel? ViewModel => DataContext as CalendarViewModel;

    private void Day_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CalendarCell cell)
            ViewModel?.SelectDay(cell);
    }

    /// <summary>
    /// An agenda row opens what it hangs on: the conversation when there is one, the person
    /// when there is not (a birthday has a profile but no call).
    /// </summary>
    private void AgendaRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarEntry entry) return;

        if (entry.CallId is { } callId) OpenCall(callId);
        else if (entry.ContactId is { } contactId) OpenContact(contactId);
    }

    private void OpenCall(long callId)
    {
        CallWindow.Show(Window.GetWindow(this), callId);
    }

    private void OpenContact(long contactId)
        => ContactWindow.Show(
            Window.GetWindow(this),
            new ContactWindowViewModel(App.Repository, contactId, App.Paths.Photos, App.ModelAccess));
}
