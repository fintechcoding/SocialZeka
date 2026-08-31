namespace VoiceTranscript.App.Views;

/// <summary>
/// Which services will do the work, in the order they will be tried.
///
/// No code behind it: everything is a command on the view model, including the connection test,
/// which is deliberately a button rather than a timer.
/// </summary>
public partial class AiStatusPage
{
    public AiStatusPage() => InitializeComponent();
}
