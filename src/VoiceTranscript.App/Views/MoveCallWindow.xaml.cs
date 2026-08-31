using System.Windows;
using System.Windows.Controls;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Puts a recording under a different person.
///
/// This window exists because automatic attribution cannot be made reliable, not because it is
/// currently poor. All WhatsApp and Telegram offer is a window title, and a title is sometimes the
/// person, sometimes whichever conversation happened to be open, and sometimes an unread counter.
/// Calls will therefore land under the wrong contact however careful the guessing becomes, and the
/// honest answer is to make correcting it take two clicks rather than to promise it will not happen.
///
/// Two things about it are deliberate:
///
///   <b>A new contact can be created here.</b> The person a call should be moved to frequently
///   does not exist yet — that is exactly the situation when a wrong name was invented and used
///   instead. Sending the user away to create one first would leave the call filed wrongly in the
///   meantime, which is when these things get forgotten.
///
///   <b>Forgetting the learned title is on by default.</b> The wrong filing is almost never a
///   one-off: it happened because a title was bound to the wrong contact, and until that binding
///   is removed every later call showing the title repeats it. Worse, the contact then looks known,
///   so the labelling question stops being asked and the mistake stops being visible.
/// </summary>
public partial class MoveCallWindow
{
    private readonly Repository _repository;
    private readonly CallApp _app;

    /// <summary>Set while a suggestion is being applied, so the edit does not reopen the list.</summary>
    private bool _choosing;

    public MoveCallWindow(
        Repository repository,
        string currentContactName,
        string? observedTitle,
        CallApp app,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int ledgerEntries)
    {
        InitializeComponent();

        _repository = repository;
        _app = app;

        HeadlineText.Text =
            $"{startedAt.ToLocalTime():d MMMM, HH:mm} · {duration:mm\\:ss} · şu an {currentContactName} altında.";

        // Said plainly, because it is the part people do not expect. A call is not one row: the
        // promises and figures taken out of it are filed against the same person, and they travel
        // with it. Somebody moving a call needs to know their ledger is about to change too.
        LedgerNote.Text = ledgerEntries > 0
            ? $"Bu görüşmeden çıkarılan {ledgerEntries} kayıt (sözler, iddialar, işaretler) da birlikte taşınacak."
            : "Bu görüşmeden çıkarılmış bir defter kaydı yok.";

        ForgetHint.Text = string.IsNullOrWhiteSpace(observedTitle)
            ? "Bu görüşmede kaydedilmiş bir pencere başlığı yok."
            : $"Başlık: “{observedTitle}”";

        ForgetBox.IsEnabled = !string.IsNullOrWhiteSpace(observedTitle);
        if (!ForgetBox.IsEnabled) ForgetBox.IsChecked = false;

        NameBox.Focus();
    }

    /// <summary>The contact chosen, once the window closes with a result.</summary>
    public long? ChosenContactId { get; private set; }

    /// <summary>Whether the learned title pairing should be removed as well.</summary>
    public bool ForgetTitle => ForgetBox.IsChecked == true;

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_choosing) return;

        var typed = NameBox.Text?.Trim() ?? "";

        var offers = _repository.SearchContacts(typed)
            .Where(c => !string.Equals(c.Name, typed, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        MatchList.ItemsSource = offers;
        MatchList.Visibility = offers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchList.SelectedItem is not Contact contact) return;

        _choosing = true;

        try
        {
            NameBox.Text = contact.Name;
            NameBox.CaretIndex = NameBox.Text.Length;

            MatchList.Visibility = Visibility.Collapsed;
            MatchList.SelectedItem = null;

            NameBox.Focus();
        }
        finally
        {
            _choosing = false;
        }
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.Focus();
            return;
        }

        // Creates the contact when it does not exist, matches it when it does. UpsertContact folds
        // Turkish letters and case, so a name typed slightly differently reaches the person who is
        // already there rather than making a second one — which is the mistake this window is
        // usually being opened to repair.
        ChosenContactId = _repository.UpsertContact(name, _app);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
