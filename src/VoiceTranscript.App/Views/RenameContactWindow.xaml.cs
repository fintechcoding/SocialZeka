using System.Windows;
using System.Windows.Input;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Asks for a corrected name for a contact.
///
/// A separate operation rather than "delete and add again", because the archive keys contacts on
/// the name: retyping creates a second person and leaves one history split between two rows, both
/// of which look complete. That is the exact failure the contact-repair work exists to undo, so
/// the fix for a misspelling must not cause it.
/// </summary>
public partial class RenameContactWindow
{
    public RenameContactWindow(string currentName)
    {
        InitializeComponent();

        NameBox.Text = currentName;

        // Selected rather than merely focused, so typing replaces the name. Somebody correcting
        // "Serdaal" usually retypes it; somebody adding a surname clicks to the end. The first is
        // the common case and this makes it one keystroke.
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The name typed, once the window closes with a result.</summary>
    public string NewName { get; private set; } = "";

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        Save_Click(sender, e);
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.Focus();
            return;
        }

        NewName = name;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
