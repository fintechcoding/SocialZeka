namespace VoiceTranscript.App.Views;

/// <summary>
/// What has been processed, what has not, and what went wrong.
///
/// No code behind it beyond construction: everything the screen does is a command on the view
/// model, and the one thing it cannot do itself — putting work back through the orchestrator — is
/// wired up by the shell, which is the only thing that holds one.
/// </summary>
public partial class ProcessingPage
{
    public ProcessingPage() => InitializeComponent();
}
