namespace VoiceTranscript.Core;

/// <summary>
/// The channel through which Core describes what it is doing, to whatever log the host keeps.
///
/// Core cannot reference the application's log directly, and a failure like "the model answered
/// with something that was not an object" happens entirely inside Core — on the user's machine,
/// where no debugger will ever be attached. Without this bridge such failures surface as a bare
/// exception name and the diagnosis becomes guesswork.
///
/// The same privacy contract as the log itself applies to every caller: states, shapes, counts,
/// durations and parameter names only. Never transcript text, contact names or keys — the log
/// file is written to be sent to a stranger.
/// </summary>
public static class CoreLog
{
    /// <summary>Where the lines go. Null until the host wires it; writes before that are dropped.</summary>
    public static Action<string, string>? Sink { get; set; }

    public static void Write(string area, string message)
    {
        try
        {
            Sink?.Invoke(area, message);
        }
        catch (Exception)
        {
            // Logging must never take the operation down with it.
        }
    }
}
