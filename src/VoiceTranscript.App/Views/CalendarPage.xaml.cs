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
        var window = new CallWindow(new CallWindowViewModel(
            App.Repository, () => App.Settings, App.HttpClient, callId))
        {
            Owner = Window.GetWindow(this),
        };

        // Shown rather than shown modally, same as the overview: reading a conversation while
        // looking at the month is the ordinary way to use this.
        window.Show();
    }

    private void OpenContact(long contactId)
        => ContactWindow.Show(
            Window.GetWindow(this),
            new ContactWindowViewModel(App.Repository, contactId));
}
