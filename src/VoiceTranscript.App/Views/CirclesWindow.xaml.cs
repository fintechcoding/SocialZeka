using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Views;

/// <summary>One circle being edited: name, icon, colour, with a live preview.</summary>
public sealed partial class CircleRow : ObservableObject
{
    /// <summary>Icons on offer. All verified against this WPF-UI build by the smoke test.</summary>
    public static readonly string[] IconChoices =
    [
        "Home24", "Briefcase24", "Person24", "Heart24", "People24", "Star24",
        "Building24", "Phone24", "Gift24", "Shield24",
    ];

    /// <summary>Colours on offer — the same palette the tag wardrobe offers.</summary>
    public static readonly string[] ColorChoices =
    [
        "#8764B8", "#0078D4", "#107C10", "#E81123", "#F7630C",
        "#C19C00", "#038387", "#C239B3", "#8E562E", "#5D5D5D",
    ];

    /// <summary>
    /// The folded spelling this row had when the window opened, or null for a row added since.
    ///
    /// It is what makes a rename a rename. A circle's identity is its folded name, so without
    /// remembering the old one, "Aile" → "Ailem" would write a new circle and delete the old,
    /// and everybody filed under the old word would silently fall out of it.
    /// </summary>
    public string? OriginalFolded { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _icon = "Home24";
    [ObservableProperty] private string _color = ColorChoices[0];

    public string[] Icons => IconChoices;
    public string[] Colors => ColorChoices;
}

/// <summary>One person, with the circle they are in — written the moment it is chosen.</summary>
public sealed partial class CirclePersonRow : ObservableObject
{
    public required long ContactId { get; init; }
    public required string Name { get; init; }

    /// <summary>"25 görüşme" — why this person is near the top of the list.</summary>
    public required string CallLine { get; init; }

    public required IReadOnlyList<CircleChoice> Choices { get; init; }

    /// <summary>
    /// What the database already says about this person.
    ///
    /// The dropdown raises its change event when the row is first drawn as well as when somebody
    /// picks something, and the two are indistinguishable from the handler. Comparing against
    /// this is what keeps opening the window from re-writing every person's card and stamping
    /// them all as edited today.
    /// </summary>
    public string? Written { get; set; }

    [ObservableProperty] private CircleChoice? _choice;
}

/// <summary>
/// The circles: what they are called, and who is in them.
///
/// A window rather than a page on purpose. It is opened, used for a minute and closed — the
/// nineteen clicks that set the whole thing up — and a page in the rail would put a permanent
/// door in the navigation for a job that is done once.
///
/// The two panels are written at different moments and the window says so. Names wait for
/// [Kaydet] because a circle's identity is its spelling and writing every keystroke would create
/// a circle per letter. Filing a person is written immediately, exactly as a birth date is: a
/// screen where some edits are kept and others need a button is a screen where somebody loses
/// work, and of the two halves this is the one that can be made safe.
/// </summary>
public partial class CirclesWindow
{
    private readonly Repository _repository;

    private readonly ObservableCollection<CircleRow> _rows = [];
    private readonly ObservableCollection<CirclePersonRow> _people = [];

    /// <summary>True while the people are being built, so filling a dropdown is not an edit.</summary>
    private bool _loadingPeople;

    /// <summary>The folded names the vocabulary held at open. The diff against it is what deletes.</summary>
    private readonly HashSet<string> _opened = new(StringComparer.Ordinal);

    public CirclesWindow(Repository repository)
    {
        InitializeComponent();

        _repository = repository;

        foreach (var circle in repository.Circles())
        {
            var folded = TurkishText.NormalizeForSearch(circle.Name.Trim());

            _opened.Add(folded);

            _rows.Add(new CircleRow
            {
                OriginalFolded = folded,
                Name = circle.Name,
                Icon = circle.Icon,
                Color = circle.Color,
            });
        }

        Rows.ItemsSource = _rows;
        People.ItemsSource = _people;

        LoadPeople();
    }

    /// <summary>
    /// The people, most talked-to first.
    ///
    /// That order is the whole design of this panel: the person who fills the recent list is at
    /// the top, so the first two clicks answer two thirds of the question. One query for the
    /// contacts and one for the assignments — never one per row.
    /// </summary>
    private void LoadPeople()
    {
        // Being in no circle is offered as a choice rather than as a blank: it is a place a
        // person can be put back into, and the first screen has a tab for it.
        var choices = new List<CircleChoice>
        {
            new(CircleTabKind.Uncircled, Localisation.T("circleswindow.cevresiz"), null, ""),
        };

        foreach (var circle in _repository.Circles())
        {
            choices.Add(new CircleChoice(
                CircleTabKind.Circle,
                circle.Name,
                TurkishText.NormalizeForSearch(circle.Name.Trim()),
                circle.Color));
        }

        var assigned = _repository.CirclesByContact();

        _loadingPeople = true;

        _people.Clear();

        foreach (var contact in _repository.ListContacts().OrderByDescending(c => c.CallCount).ThenBy(c => c.Name))
        {
            var circle = assigned.GetValueOrDefault(contact.Id);

            var folded = circle is null
                ? null
                : TurkishText.NormalizeForSearch(circle.Name.Trim());

            _people.Add(new CirclePersonRow
            {
                ContactId = contact.Id,
                Name = contact.Name,
                CallLine = string.Format(Localisation.T("circleswindow.n-gorusme"), contact.CallCount),
                Choices = choices,
                Written = folded,
                Choice = choices.FirstOrDefault(c => string.Equals(c.Folded, folded, StringComparison.Ordinal))
                         ?? choices[0],
            });
        }

        _loadingPeople = false;

        UpdateCoverage();
    }

    /// <summary>"8 / 9 kişi bir çevrede" — the coverage measure, said where it can be moved.</summary>
    private void UpdateCoverage()
    {
        CoverageLine.Text = string.Format(
            Localisation.T("circleswindow.n-m-kisi-bir-cevrede"),
            _people.Count(p => p.Choice?.Folded is not null),
            _people.Count);
    }

    /// <summary>Filing a person. Written at once, with no button to press and none to forget.</summary>
    private void Assign_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPeople) return;
        if (sender is not FrameworkElement { DataContext: CirclePersonRow row }) return;

        // Nothing to write when the dropdown is merely showing what is already stored — which is
        // what it does the first time each row is drawn.
        if (string.Equals(row.Choice?.Folded, row.Written, StringComparison.Ordinal)) return;

        row.Written = row.Choice?.Folded;

        _repository.SetContactCircle(row.ContactId, row.Written);

        UpdateCoverage();
    }

    private void AddCircle_Click(object sender, RoutedEventArgs e) =>
        _rows.Add(new CircleRow
        {
            Color = CircleRow.ColorChoices[_rows.Count % CircleRow.ColorChoices.Length],
        });

    /// <summary>
    /// Takes a circle out of the vocabulary. Nobody is taken out of anything.
    ///
    /// The assignments stay in the database: the people fall back into "Çevresiz" while the word
    /// is gone, and writing the same word again brings every one of them back. Deleting what
    /// somebody spent an evening filing, because they deleted a label, is not a thing this window
    /// is allowed to do.
    /// </summary>
    private void RemoveCircle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CircleRow row) _rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var kept = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList();

        var survivors = kept
            .Where(r => r.OriginalFolded is not null)
            .Select(r => r.OriginalFolded!)
            .ToHashSet(StringComparer.Ordinal);

        // A definition that was here when the window opened and is not here now was deleted on
        // purpose. Only the word goes: the assignments stay in the database.
        foreach (var gone in _opened.Where(f => !survivors.Contains(f)))
            _repository.DeleteCircle(gone);

        for (var i = 0; i < kept.Count; i++)
        {
            var row = kept[i];
            var circle = new Circle(row.Name.Trim(), row.Icon, row.Color, i);

            // A rename carries the people with it; a row added just now is simply written.
            if (row.OriginalFolded is { } original)
                _repository.RenameCircle(original, circle);
            else
                _repository.SaveCircle(circle);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
