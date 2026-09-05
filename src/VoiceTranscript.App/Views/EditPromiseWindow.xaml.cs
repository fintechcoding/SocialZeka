using System.Windows;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Edits one promise the way the user is allowed to: their own wording, their own date.
///
/// The transcript's words are never touched — they are what the user verifies by listening —
/// and neither is the date the words resolved to, because the consistency check reads it to see
/// whether the OTHER person moved a deadline. Both of the user's columns are written through
/// <see cref="LedgerActions.Edit"/>, so the caller gets one undo for one Kaydet and every screen
/// showing the row learns of the change.
///
/// Saving what is already there writes nothing: a wording equal to the machine's, or a date equal
/// to the spoken one, is not a correction, and a row is not stamped "edited" for a look.
/// </summary>
public partial class EditPromiseWindow
{
    private readonly Repository _repository;
    private readonly Commitment _commitment;
    private readonly bool _loaded;

    /// <summary>What Kaydet or "Düzeltmeyi kaldır" did, with the way back. Null when nothing was written.</summary>
    public PendingUndo? Result { get; private set; }

    public EditPromiseWindow(Repository repository, Commitment commitment)
    {
        InitializeComponent();

        _repository = repository;
        _commitment = commitment;

        Quote.Text = commitment.Quote.Trim();
        MachineReading.Text = string.Format(Localisation.T("editpromisewindow.makinenin-okudugu-n"), commitment.Obligation);

        SpokenDate.Text = commitment.DeadlineDate is { } spoken
            ? string.Format(Localisation.T("editpromisewindow.soylenen-tarih-n"), $"{spoken:d MMMM yyyy}")
            : !string.IsNullOrWhiteSpace(commitment.DeadlineRaw)
                ? string.Format(Localisation.T("editpromisewindow.soylenen-tarih-n"), commitment.DeadlineRaw)
                : Localisation.T("editpromisewindow.soylenen-bir-tarih-yok");

        // The second visit: what stands.
        Wording.Text = commitment.UserObligation ?? "";
        Day.SelectedDate = commitment.UserDeadlineDate?.ToDateTime(TimeOnly.MinValue);
        Clear.Visibility = commitment.IsEdited ? Visibility.Visible : Visibility.Collapsed;

        _loaded = true;
        UpdateVerdict();
    }

    // ---- what would be written --------------------------------------------------------------

    /// <summary>The user's wording, or null when the box is blank or merely repeats the machine's.</summary>
    private string? ProposedWording
    {
        get
        {
            var text = Wording.Text.Trim();
            return text.Length == 0 || text == _commitment.Obligation.Trim() ? null : text;
        }
    }

    /// <summary>The user's date, or null when none is picked or it is the spoken one.</summary>
    private DateOnly? ProposedDeadline
    {
        get
        {
            if (Day.SelectedDate is not { } picked) return null;

            var day = DateOnly.FromDateTime(picked.Date);
            return day == _commitment.DeadlineDate ? null : day;
        }
    }

    private string? CurrentWording =>
        string.IsNullOrWhiteSpace(_commitment.UserObligation) ? null : _commitment.UserObligation.Trim();

    private bool HasChanges =>
        ProposedWording != CurrentWording || ProposedDeadline != _commitment.UserDeadlineDate;

    private void Wording_Changed(object sender, RoutedEventArgs e) => UpdateVerdict();

    private void Day_Changed(object sender, RoutedEventArgs e) => UpdateVerdict();

    private void ClearDay_Click(object sender, RoutedEventArgs e) => Day.SelectedDate = null;

    private void UpdateVerdict()
    {
        if (!_loaded) return;

        ClearDay.Visibility = Day.SelectedDate is null ? Visibility.Collapsed : Visibility.Visible;

        var changed = HasChanges;
        Save.IsEnabled = changed;
        Verdict.Text = changed
            ? Localisation.T("editpromisewindow.kaydedince-defterde-senin-ifaden-gorunur")
            : Localisation.T("editpromisewindow.degisiklik-yok");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!HasChanges) return;

        Result = LedgerActions.Edit(_repository, _commitment, ProposedWording, ProposedDeadline);

        DialogResult = true;
        Close();
    }

    /// <summary>Both of the user's columns go; the spoken words and date were never anywhere else.</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Result = LedgerActions.Edit(_repository, _commitment, null, null);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// The one doorway every ✎ and "Ertele" goes through. Returns what was written, with its
    /// undo, or null when the dialog was cancelled — so a caller never offers "Geri al" for
    /// nothing.
    /// </summary>
    public static PendingUndo? Open(Window? owner, Repository repository, Commitment commitment)
    {
        var dialog = new EditPromiseWindow(repository, commitment) { Owner = owner };

        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
