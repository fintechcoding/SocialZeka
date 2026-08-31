using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

/// <summary>
/// One person, on their own.
///
/// One window per contact, tracked here. A second double-click on the same person brings the one
/// already open to the front rather than stacking another copy — the whole structural reason for
/// this being a window is being able to have two DIFFERENT people side by side, and two of the
/// same person defeats that while looking like a bug.
/// </summary>
public partial class ContactWindow
{
    private static readonly Dictionary<long, ContactWindow> Open = [];

    /// <summary>
    /// Public so the screen can be constructed without being shown.
    ///
    /// <see cref="Show"/> is the way to open one — it is what keeps a person to a single window.
    /// This exists because the smoke test really builds every screen, and a window that can only
    /// be created by a method that also displays it cannot be tested that way.
    /// </summary>
    public ContactWindow(ContactWindowViewModel model)
    {
        InitializeComponent();

        DataContext = model;

        // Cascaded rather than centred on the owner. CenterOwner stacks the second person exactly
        // on top of the first, which hides the one capability this window has over the page.
        var offset = 32 * Open.Count;
        Left = (SystemParameters.WorkArea.Width - Width) / 2 + offset;
        Top = Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2 + offset);

        Closed += (_, _) => Open.Remove(model.ContactId);
    }

    /// <summary>Opens this person, or raises the window already showing them.</summary>
    public static void Show(Window? owner, ContactWindowViewModel model)
    {
        if (Open.TryGetValue(model.ContactId, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new ContactWindow(model) { Owner = owner };
        Open[model.ContactId] = window;

        window.Show();
    }

    private ContactWindowViewModel? ViewModel => DataContext as ContactWindowViewModel;

    /// <summary>Opens one of this person's conversations to read.</summary>
    private void CallRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox { SelectedItem: ContactCall row }) return;

        OpenCall(row.Id);
    }

    /// <summary>
    /// Opens the conversation a search hit came from, at the moment it was said.
    ///
    /// Playing from the line is the point of the search: a result you cannot hear is a claim about
    /// a conversation, and this product does not ask to be believed.
    /// </summary>
    private void Hit_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox { SelectedItem: ContactHit hit }) return;

        OpenCall(hit.CallId, hit.StartMs, hit.IsMe);
    }

    private void OpenCall(long callId, int? startMs = null, bool isMe = false)
    {
        var model = new CallWindowViewModel(
            App.Repository, () => App.Settings, App.HttpClient, callId);

        var window = new CallWindow(model) { Owner = this };
        window.Show();

        if (startMs is { } at) model.Playback.PlayFrom(at, isMe);
    }

    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ViewModel is not { } model) return;

        model.SearchCommand.Execute(null);
        e.Handled = true;
    }
}
