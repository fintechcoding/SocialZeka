namespace VoiceTranscript.App.Services;

/// <summary>
/// How loud a notice is. Carried WITH the message from the code that raised it — severity
/// used to be guessed on the far side by searching the Turkish text for "başarısız", which
/// worked until the first message that phrased its failure differently.
/// </summary>
public enum NoticeSeverity
{
    Info,

    /// <summary>Something completed — the quiet good news.</summary>
    Success,

    /// <summary>Needs a look, nothing is broken.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>One notice, as the bell's history keeps it.</summary>
public sealed record Notice(NoticeSeverity Severity, string Message, DateTimeOffset At)
{
    public string When => At.ToLocalTime().ToString("HH:mm");

    public Wpf.Ui.Controls.SymbolRegular Icon => Severity switch
    {
        NoticeSeverity.Success => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
        NoticeSeverity.Warning => Wpf.Ui.Controls.SymbolRegular.Warning24,
        NoticeSeverity.Error => Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
        _ => Wpf.Ui.Controls.SymbolRegular.Info24,
    };
}
