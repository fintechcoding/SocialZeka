using System.Windows;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Views;

/// <summary>
/// "Bu görüşmede söylemek istemediğim şey" — one note, written for one conversation.
///
/// The reminder dialog's shape: a subject line saying which conversation this is, a box, and
/// three exits. It differs from that one in what it deliberately does not do. There is no date,
/// because the note is about the next ten minutes; there is no scoring afterwards, because
/// whether somebody kept to an intention cannot be read off a transcript — a plan not to raise a
/// figure and a conversation where the figure never came up look identical in text.
///
/// The row is the user's, in a table no analysis writes to and no re-analysis clears. Saving a
/// blank box removes it: an empty card is no card.
/// </summary>
public partial class NiyetWindow
{
    private readonly Repository _repository;
    private readonly long _callId;

    public NiyetWindow(Repository repository, long callId, string subject)
    {
        InitializeComponent();

        _repository = repository;
        _callId = callId;

        Subject.Text = subject;

        // The second visit: show what stands, and offer the way out. Opening the card mid-call to
        // change or drop the note is at least as likely as opening it to write one.
        if (repository.GetCallIntent(callId) is { } existing)
        {
            Intent.Text = existing.Text;
            Clear.Visibility = Visibility.Visible;

            Stamp.Text = string.Format(
                Localisation.T("niyetwindow.son-yazilan-n"), existing.UpdatedAt.ToLocalTime());
            Stamp.Visibility = Visibility.Visible;
        }

        // The caret where the typing goes, without stealing focus from anything before Loaded —
        // this window can be opened from the recording strip, which must never take the keyboard.
        Loaded += (_, _) =>
        {
            Intent.Focus();
            Intent.CaretIndex = Intent.Text.Length;
        };
    }

    /// <summary>Saves, or removes when the box was emptied — SaveCallIntent treats blank as a delete.</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _repository.SaveCallIntent(_callId, Intent.Text);

        DialogResult = true;
        Close();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _repository.DeleteCallIntent(_callId);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// The one doorway. Returns whether the note was written or removed, so a caller that shows a
    /// mark beside the call knows to repaint — and knows not to when the user pressed Escape.
    /// </summary>
    public static bool Open(Window? owner, Repository repository, long callId, string subject)
    {
        var window = new NiyetWindow(repository, callId, subject) { Owner = owner };

        return window.ShowDialog() == true;
    }
}
