using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

/// <summary>One dictionary row being edited: the stem as written, and its endings as one line.</summary>
public sealed partial class LexemeRow : ObservableObject
{
    /// <summary>Zero for a row added in this window; the stored id otherwise, which is what a delete needs.</summary>
    public long Id { get; init; }

    [ObservableProperty] private string _lexeme = "";

    /// <summary>
    /// The allowed endings, comma-separated.
    ///
    /// One text box rather than a list editor because that is how a person writing down a set of
    /// endings actually writes them, and because the whole set for one stem is a handful of
    /// syllables. Split on save; the repository folds each one.
    /// </summary>
    [ObservableProperty] private string _suffixes = "";
}

/// <summary>
/// Edits the dictionaries the habit counters read: what counts as swearing, what counts as a
/// filler, and what is ruled out of both.
///
/// The tag manager's shape, for the same reason it has that shape — these are definitions the
/// user owns, seeded once and theirs afterwards. Nothing here touches a conversation: deleting a
/// stem does not erase anything that was said, it changes what the next count looks for.
///
/// Deliberately edited as stems with endings rather than as whole words. Substring matching is
/// the obvious rule and the wrong one — a short stem sits inside perfectly ordinary words — and
/// whole-word matching is wrong the other way in a language that glues. The caption on the window
/// says the rule out loud, because somebody adding a stem needs to know why their word did or did
/// not get counted.
///
/// No word from any of these lists appears in this file, in a log, or in a string. The data lives
/// in the database and in the embedded seed; the code only ever handles it as a value.
/// </summary>
public partial class SozlukWindow
{
    private readonly Repository _repository;

    private readonly ObservableCollection<LexemeRow> _profanity = [];
    private readonly ObservableCollection<LexemeRow> _fillers = [];
    private readonly ObservableCollection<LexemeRow> _exclusions = [];

    /// <summary>Ids present when the window opened. Whatever is missing at save was deleted on purpose.</summary>
    private readonly HashSet<long> _opened;

    public SozlukWindow(Repository repository)
    {
        InitializeComponent();

        _repository = repository;

        foreach (var row in repository.Lexicon())
        {
            var editable = new LexemeRow
            {
                Id = row.Id,
                Lexeme = row.Lexeme,
                Suffixes = string.Join(", ", row.Suffixes),
            };

            Bucket(row.Kind)?.Add(editable);
        }

        _opened = [.. repository.Lexicon().Select(r => r.Id)];

        ProfanityRows.ItemsSource = _profanity;
        FillerRows.ItemsSource = _fillers;
        ExclusionRows.ItemsSource = _exclusions;
    }

    /// <summary>The list a kind is shown in, or null for a kind this window does not edit (şive).</summary>
    private ObservableCollection<LexemeRow>? Bucket(string kind) => kind switch
    {
        HabitKind.Profanity => _profanity,
        HabitKind.Filler => _fillers,
        HabitKind.Exclusion => _exclusions,
        _ => null,
    };

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string kind) return;

        Bucket(kind)?.Add(new LexemeRow());
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LexemeRow row) return;

        _profanity.Remove(row);
        _fillers.Remove(row);
        _exclusions.Remove(row);
    }

    /// <summary>
    /// Writes the lists back: everything still on screen is upserted, everything that was here at
    /// open and is gone now is deleted.
    ///
    /// Deletion is by stored id rather than by word, so renaming a stem in place replaces the row
    /// it came from instead of leaving the old spelling behind counting quietly. Blank rows are
    /// dropped without comment — an empty box is somebody who changed their mind, not an error.
    /// </summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var kept = new HashSet<long>();

        Write(HabitKind.Profanity, _profanity, kept);
        Write(HabitKind.Filler, _fillers, kept);
        Write(HabitKind.Exclusion, _exclusions, kept);

        foreach (var id in _opened)
        {
            if (!kept.Contains(id)) _repository.DeleteLexeme(id);
        }

        DialogResult = true;
        Close();
    }

    private void Write(string kind, IReadOnlyList<LexemeRow> rows, HashSet<long> kept)
    {
        var position = 0;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Lexeme)) continue;

            var suffixes = row.Suffixes
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            // Upsert answers with the row's id whether it inserted or updated, so a stem edited
            // into an existing one merges rather than colliding on the unique key.
            kept.Add(_repository.UpsertLexeme(kind, row.Lexeme, suffixes, position++));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>The one doorway: opened from the Koçluk page's [Düzenle], and modal to it.</summary>
    public static void Open(Window? owner, Repository repository)
    {
        var window = new SozlukWindow(repository) { Owner = owner };

        window.ShowDialog();
    }
}
