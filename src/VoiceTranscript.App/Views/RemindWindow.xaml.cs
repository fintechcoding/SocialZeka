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

    public RemindWindow(Repository repository, long callId, string subject)
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

        UpdateVerdict();
    }

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

    /// <summary>The one doorway every "Hatırlat" click goes through, wherever it started.</summary>
    public static void Open(Window? owner, Repository repository, long callId, string subject)
    {
        var dialog = new RemindWindow(repository, callId, subject) { Owner = owner };
        dialog.ShowDialog();
    }
}
