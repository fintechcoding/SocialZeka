using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Which services will do the work, in the order they will be tried.
///
/// Almost everything is a command on the view model, including the connection test, which is
/// deliberately a button rather than a timer. The one piece of code here turns a click on a
/// service row into the settings section that configures it — "yanıt vermiyor" and the place to
/// fix it must be one click apart, or the status screen is a complaint with no door.
/// </summary>
public partial class AiStatusPage
{
    public AiStatusPage() => InitializeComponent();

    private void ServiceRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AiServiceRow row) return;

        (DataContext as AiStatusViewModel)?.OpenSectionFor(row.IsTranscription);
    }
}
