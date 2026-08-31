using System.Windows;
using System.Windows.Controls;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Folds one contact into another.
///
/// One person routinely ends up as two rows here, for reasons that are ordinary rather than
/// exotic: a window title that was never a name created a contact, the same name was typed with a
/// different spelling, or the person was reached on two applications — contacts are keyed on
/// (name, app), so those are already two people as far as the archive is concerned.
///
/// Leaving them split is not a cosmetic problem. Everything this product exists to do — notice
/// that a price moved between two calls, that a promise came due, that an account of events
/// changed — is computed per contact, and a divided history makes both halves look complete while
/// the comparison across them silently never happens.
///
/// The direction is fixed by the caller: the contact the user is looking at survives, and the one
/// chosen here is absorbed into it. Stated plainly on screen, because the absorbed contact stops
/// existing and nothing in the application undoes that.
/// </summary>
public partial class MergeContactWindow
{
    private readonly Repository _repository;
    private readonly Contact _target;

    /// <summary>A contact as offered in the list, with enough detail to tell two similar ones apart.</summary>
    private sealed record Candidate(long Id, string Name, string Detail);

    public MergeContactWindow(Repository repository, Contact target)
    {
        InitializeComponent();

        _repository = repository;
        _target = target;

        HeadlineText.Text = $"Seçilen kişi ({target.Name}) kalacak.";
        WarningText.Text =
            $"Seçtiğin kişinin bütün görüşmeleri, sözleri ve defter kayıtları {target.Name} altına "
            + "taşınacak ve o kişi silinecek. Bu işlem geri alınamaz.";

        Show(_repository.ListContacts());

        SearchBox.Focus();
    }

    /// <summary>The contact to absorb, once the window closes with a result.</summary>
    public long? ChosenContactId { get; private set; }

    private void Show(IEnumerable<Contact> contacts)
    {
        // The survivor is not offered: merging a contact into itself is not a thing anybody means.
        ContactList.ItemsSource = contacts
            .Where(c => c.Id != _target.Id)
            .Select(c => new Candidate(
                c.Id,
                c.Name,
                $"{c.CallCount} görüşme · {c.App}"))
            .ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var typed = SearchBox.Text?.Trim() ?? "";

        Show(string.IsNullOrWhiteSpace(typed)
            ? _repository.ListContacts()
            : _repository.SearchContacts(typed, limit: 50));

        MergeButton.IsEnabled = false;
    }

    private void ContactList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => MergeButton.IsEnabled = ContactList.SelectedItem is Candidate;

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (ContactList.SelectedItem is not Candidate candidate) return;

        // Asked once more, by name. The absorbed contact stops existing and nothing here undoes it.
        var answer = MessageBox.Show(
            $"{candidate.Name} → {_target.Name}\n\n"
            + $"{candidate.Name} kişisinin her şeyi {_target.Name} altına taşınacak ve "
            + $"{candidate.Name} silinecek.\n\nDevam edilsin mi?",
            "Kişileri birleştir",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        ChosenContactId = candidate.Id;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
