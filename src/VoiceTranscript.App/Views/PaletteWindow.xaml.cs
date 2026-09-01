using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Views;

/// <summary>
/// The Ctrl+K palette. Everything it offers comes from <see cref="ActionRegistry"/> plus a
/// live contact search, so the keyboard layer and the palette can never drift apart — they
/// read the same list.
///
/// Opens over whichever window summoned it, top third; goes away on Esc, on losing focus,
/// and after every executed action. Arrow keys move the selection while typing continues in
/// the box — the hands never leave the keyboard, which is the entire point.
/// </summary>
public partial class PaletteWindow
{
    private readonly ShellViewModel _shell;

    private PaletteWindow(Window owner, ShellViewModel shell)
    {
        InitializeComponent();
        Services.EscapeCloses.Attach(this);

        _shell = shell;
        Owner = owner;

        Left = owner.Left + (owner.ActualWidth - Width) / 2;
        Top = owner.Top + owner.ActualHeight / 5;

        Deactivated += (_, _) => Close();
        Loaded += (_, _) => { QueryBox.Focus(); Refill(""); };
    }

    /// <summary>The one way in — from any window that has the shell.</summary>
    public static void Open(Window owner, ShellViewModel shell)
        => new PaletteWindow(owner, shell).Show();

    private sealed record Row(
        string Title, string Detail, Wpf.Ui.Controls.SymbolRegular Icon, Action Run);

    private void Refill(string query)
    {
        var folded = TurkishText.NormalizeForSearch(query.Trim());

        List<(int Score, Row Row)> rows = [];

        foreach (var action in ActionRegistry.All)
        {
            var score = ActionRegistry.Score(folded, action.Folded);
            if (score > 0)
                rows.Add((score, new Row(action.Title, action.Detail, action.Icon,
                    () => action.Run(_shell))));
        }

        // People join the list as soon as there is something to match on — opening a contact
        // is the palette's most common errand.
        if (folded.Length >= 2)
        {
            foreach (var contact in App.Repository.SearchContacts(query.Trim()))
            {
                var id = contact.Id;
                rows.Add((90, new Row(contact.Name, "Kişiyi aç",
                    Wpf.Ui.Controls.SymbolRegular.Person24,
                    () => _shell.OpenContact(id))));
            }
        }

        Results.ItemsSource = rows
            .OrderByDescending(r => r.Score)
            .Select(r => r.Row)
            .Take(12)
            .ToList();

        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }

    private void Query_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Refill(QueryBox.Text);

    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up:
                Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;

            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void Results_Click(object sender, MouseButtonEventArgs e) => RunSelected();

    private void RunSelected()
    {
        if (Results.SelectedItem is not Row row) return;

        Close();
        row.Run();
    }
}
