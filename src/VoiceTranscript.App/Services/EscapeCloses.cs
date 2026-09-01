using System.Windows;
using System.Windows.Input;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Esc closes the window — the Windows reflex, honoured everywhere.
///
/// Dialogs with a cancel button get this for free from IsCancel; the browsing windows (a
/// conversation, a person, the settings) had nothing, and Esc doing nothing in one window of
/// an application where it works in every other reads as a bug, which is how it was reported.
///
/// Listens on bubbling KeyDown, not the preview: a control that already answers Esc — an open
/// dropdown, a calendar flyout — handles the key first, so Esc still closes the innermost
/// thing that is open, and only then the window. MainWindow never attaches this: Esc must not
/// take the whole application down.
/// </summary>
public static class EscapeCloses
{
    public static void Attach(Window window) =>
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;

            e.Handled = true;
            window.Close();
        };
}
