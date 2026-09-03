using System.Windows;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Sets a reminder on one conversation: a reason, and a day.
///
/// Modelled on how Outlook treats a follow-up rather than on how the code stores it. The quick
/// menus this replaces ("Yarın / 3 gün / 1 hafta / 1 ay") could only produce a bare date, so
/// every reminder surfaced on the board as a day with no explanation — a puzzle addressed to
/// your future self. Here the reason is typed first, lands as the card's title on the first
/// screen, and the presets survive as one-click ways to fill the date field.
///
/// Opening it on a call that already has a reminder shows what is set and offers to remove it,
/// because the second visit to a reminder is usually to change or cancel it.
/// </summary>
public partial class RemindWindow
{
    private readonly Repository _repository;
    private readonly long _callId;

    /// <param name="reason">
    /// A pre-drafted reason, when something concrete brought the user here — a consistency
    /// finding's "ask this again". Wins over the stored card title on screen: it is the
    /// user's fresh intent, and they can still edit or erase it before saving.
    /// </param>
    public RemindWindow(Repository repository, long callId, string subject, string? reason = null)
    {
        InitializeComponent();

        _repository = repository;
        _callId = callId;

        Subject.Text = subject;

        // The second visit: show what stands, and offer the way out.
        var existing = repository.BoardCardOf(callId);
        if (existing is not null)
        {
            Reason.Text = existing.Title ?? "";

            if (existing.RemindOn is { } day)
            {
                Day.SelectedDate = day.ToDateTime(TimeOnly.MinValue);
                Clear.Visibility = Visibility.Visible;
            }
        }

        if (!string.IsNullOrWhiteSpace(reason)) Reason.Text = reason;

        // The call's own note, editable where the reminder is set: leaving a reminder and
        // writing down why are one act, and the note travels with the conversation (Notlar
        // sekmesi), not with the card.
        _loadedNote = repository.GetNote(callId);
        CallNote.Text = _loadedNote;

        UpdateVerdict();
    }

    private readonly string _loadedNote = "";

    /// <summary>One click fills the date; the sentence below says what that now means.</summary>
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !int.TryParse(tag, out var days)) return;

        Day.SelectedDate = DateTime.Today.AddDays(days);
    }

    private void Day_Changed(object sender, RoutedEventArgs e) => UpdateVerdict();

    private void UpdateVerdict()
    {
        if (Day.SelectedDate is { } picked)
        {
            Save.IsEnabled = picked.Date >= DateTime.Today;
            Verdict.Text = picked.Date < DateTime.Today
                ? "Geçmiş bir gün seçili — hatırlatma ancak bugünden ileriye kurulabilir."
                : $"{picked:d MMMM yyyy dddd} günü ana ekranın Bugün bölümünde belirir.";
        }
        else
        {
            Save.IsEnabled = false;
            Verdict.Text = "Bir gün seç ya da yukarıdaki hazır seçeneklerden birine tıkla.";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Day.SelectedDate is not { } picked) return;

        var reason = string.IsNullOrWhiteSpace(Reason.Text) ? null : Reason.Text.Trim();

        // PutOnBoard keeps the existing title when null arrives, so an emptied box never
        // erases a label the user gave the card some other way.
        _repository.PutOnBoard(_callId, Core.Domain.BoardLane.ToLookAt, title: reason);
        _repository.RemindOn(_callId, DateOnly.FromDateTime(picked.Date));

        // Written only when the user actually touched it — the Notlar tab may hold a longer
        // note this dialog must not clobber by mere passage.
        if (CallNote.Text != _loadedNote) _repository.SaveNote(_callId, CallNote.Text);

        DialogResult = true;
        Close();
    }

    /// <summary>Removes the reminder. The card itself stays where the user put it.</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _repository.RemindOn(_callId, null);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// The one doorway every "Hatırlat" click goes through, wherever it started.
    ///
    /// Returns whether a reminder was actually set. The callers act on that: one of them marked
    /// the suggestion as dealt with and dropped it off the list the moment this returned, so
    /// pressing Escape here made the suggestion disappear without anything being scheduled — and
    /// nothing shows it again.
    /// </summary>
    public static bool Open(
        Window? owner, Repository repository, long callId, string subject, string? reason = null)
    {
        var dialog = new RemindWindow(repository, callId, subject, reason) { Owner = owner };

        return dialog.ShowDialog() == true;
    }
}
