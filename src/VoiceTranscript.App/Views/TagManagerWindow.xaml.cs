using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>One colour on offer: a human name and the hex it stands for.</summary>
public sealed record TagColorChoice(string Name, string Hex)
{
    public Brush Brush
    {
        get
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex));
                brush.Freeze();
                return brush;
            }
            catch (FormatException)
            {
                return Brushes.Gray;
            }
        }
    }
}

/// <summary>One definition being edited: name, icon, colour, with a live preview.</summary>
public sealed partial class TagDefRow : ObservableObject
{
    /// <summary>Icons on offer. All verified against this WPF-UI build by the smoke test.</summary>
    public static readonly string[] IconChoices =
    [
        "Flag24", "Briefcase24", "Person24", "Warning24", "Money24", "Star24",
        "Heart24", "Alert24", "Shield24", "Phone24", "Home24", "Gift24",
    ];

    /// <summary>Colours on offer — the Outlook category palette, roughly.</summary>
    public static readonly TagColorChoice[] ColorChoices =
    [
        new("Kırmızı", "#E81123"), new("Turuncu", "#F7630C"), new("Sarı", "#C19C00"),
        new("Yeşil", "#107C10"), new("Camgöbeği", "#038387"), new("Mavi", "#0078D4"),
        new("Mor", "#8764B8"), new("Pembe", "#C239B3"), new("Kahverengi", "#8E562E"),
        new("Gri", "#5D5D5D"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Symbol))]
    private string _icon = "Flag24";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    private TagColorChoice _colorChoice = ColorChoices[0];

    [ObservableProperty] private string _name = "";

    public string[] Icons => IconChoices;
    public TagColorChoice[] Colors => ColorChoices;

    public Wpf.Ui.Controls.SymbolRegular Symbol =>
        Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(Icon, out var symbol)
            ? symbol
            : Wpf.Ui.Controls.SymbolRegular.Tag24;

    public Brush Brush => ColorChoice.Brush;
}

/// <summary>
/// Edits the tag vocabulary: what each tag is called and how it dresses — Outlook's category
/// editor, for this archive. Definitions only; a conversation's taggings are the user's data
/// and are never touched from here.
/// </summary>
public partial class TagManagerWindow
{
    private readonly Repository _repository;
    private readonly List<string> _original;

    private readonly ObservableCollection<TagDefRow> _rows = [];

    public TagManagerWindow(Repository repository)
    {
        InitializeComponent();

        _repository = repository;

        foreach (var def in repository.TagDefs())
        {
            _rows.Add(new TagDefRow
            {
                Name = def.Tag,
                Icon = def.Icon,
                ColorChoice = TagDefRow.ColorChoices
                    .FirstOrDefault(c => string.Equals(c.Hex, def.Color, StringComparison.OrdinalIgnoreCase))
                    ?? new TagColorChoice("Özel", def.Color),
            });
        }

        // What existed when the window opened — the diff against this decides what to delete.
        _original = [.. _rows.Select(r => r.Name)];

        Rows.ItemsSource = _rows;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var row = new TagDefRow
        {
            ColorChoice = TagDefRow.ColorChoices[_rows.Count % TagDefRow.ColorChoices.Length],
        };

        _rows.Add(row);
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TagDefRow row) _rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var kept = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .ToList();

        // Definitions that were here at open and are gone now were deleted on purpose.
        foreach (var name in _original)
        {
            if (!kept.Any(r => string.Equals(
                    Core.Text.TurkishText.NormalizeForSearch(r.Name.Trim()),
                    Core.Text.TurkishText.NormalizeForSearch(name),
                    StringComparison.Ordinal)))
            {
                _repository.DeleteTagDef(name);
            }
        }

        for (var i = 0; i < kept.Count; i++)
        {
            _repository.SaveTagDef(
                new TagDef(kept[i].Name.Trim(), kept[i].Icon, kept[i].ColorChoice.Hex, i));
        }

        // Every pill on every open list reads from the palette; reload it before they repaint.
        Services.TagPalette.Load(_repository);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
