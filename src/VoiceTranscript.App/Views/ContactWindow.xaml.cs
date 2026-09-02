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
        Services.EscapeCloses.Attach(this);

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

    /// <summary>
    /// Scrolls to the conversation nearest the picked day.
    ///
    /// A jump, not a filter: filtering to one day usually shows nothing and looks like data loss.
    /// Landing beside the date keeps the neighbours in view, which is how remembering works —
    /// "it was around that week".
    /// </summary>
    private void JumpDate_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } model || JumpDate.SelectedDate is not { } picked) return;
        if (model.Calls.Count == 0) return;

        var target = model.Calls
            .OrderBy(c => Math.Abs((c.Call.StartedAt.LocalDateTime.Date - picked.Date).TotalDays))
            .First();

        CallList.SelectedItem = target;
        CallList.ScrollIntoView(target);
    }

    /// <summary>Brings a photo in. The dialog's filter is honest about what can be decoded.</summary>
    private void PickPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Kişi fotoğrafı seç",
            Filter = "Resimler|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Tüm dosyalar|*.*",
        };

        if (dialog.ShowDialog() == true) model.SetPhoto(dialog.FileName);
    }

    private async void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Core.Domain.ContactField field) return;

        // One click used to be a permanent DELETE. A fact the user typed deserves at least the
        // one-sentence pause every other destructive action in this product gets.
        var confirmed = await Services.Dialogs.ConfirmAsync(
            this, "Bilgiyi sil", $"\"{field.Label}: {field.Value}\" silinsin mi?", okText: "Sil");

        if (confirmed) ViewModel?.RemoveField(field);
    }

    /// <summary>Onto the important pile, from this person's own history.</summary>
    private void RowToBoard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContactCall row) return;

        App.Repository.PutOnBoard(row.Id, Core.Domain.BoardLane.ToLookAt);
    }

    private void RowRetranscribe_Click(object sender, RoutedEventArgs e)
        => RowReprocess(sender, ReprocessKind.Transcribe);

    private void RowReanalyse_Click(object sender, RoutedEventArgs e)
        => RowReprocess(sender, ReprocessKind.Analyse);

    /// <summary>Same dialog the Kişiler page opens, aimed by the verb that was clicked.</summary>
    private void RowReprocess(object sender, ReprocessKind kind)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContactCall row) return;

        if (Services.CallActions.Reprocess(this, row.Call, ViewModel?.Name ?? "Görüşme", kind))
            ViewModel?.Refresh();
    }

    private void RowOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ContactCall row) OpenCall(row.Id);
    }

    private void RowMove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContactCall row) return;

        if (Services.CallActions.Move(this, row.Call, ViewModel?.Name ?? "bilinmeyen kişi")) ViewModel?.Refresh();
    }

    private async void RowShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContactCall row) return;

        await Services.CallActions.ShowInFolderAsync(this, row.Call);
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContactCall row) return;

        if (await Services.CallActions.DeleteAsync(this, row.Call, ViewModel?.Name ?? "Bilinmeyen kişi"))
            ViewModel?.Refresh();
    }

    /// <summary>A reminder in one step: onto the pile if needed, and dated.</summary>
    private void RowRemind_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContactCall row) return;

        RemindWindow.Open(this, App.Repository, row.Id, $"{ViewModel?.Name} · {row.When}");
    }

    /// <summary>A tag pill is a question: "which other conversations did I mark with this?"</summary>
    private void TagPill_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string tag) return;

        MainWindow.SearchTagFromAnywhere(tag);
        e.Handled = true;
    }

    /// <summary>A flow row opens the conversation it happened in.</summary>
    private void FlowRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FlowEvent { CallId: { } callId })
            OpenCall(callId);
    }
}
