using System.Windows.Input;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Services;

/// <summary>
/// One thing the application can do from the keyboard or the palette.
///
/// The page it opens and the key that opens it live here, on the action, and nowhere else: the
/// window builds its key bindings from this list, the palette lists it, and the Ctrl+? sheet is
/// printed from it. Before this the three were three hand-written copies, and the sheet said
/// "Ctrl+1…8" while the markup bound nine keys in a different order.
/// </summary>
public sealed record AppAction(
    string Title,
    string Detail,
    Wpf.Ui.Controls.SymbolRegular Icon,
    Action<ShellViewModel> Run,
    ShellPage? Page = null,
    System.Windows.Input.Key? Key = null,
    ModifierKeys Modifiers = ModifierKeys.Control)
{
    /// <summary>Folded once, matched many times per keystroke.</summary>
    public string Folded { get; } = TurkishText.NormalizeForSearch(Title);

    /// <summary>"Ctrl+5", "F5", "Ctrl+?" — the way the sheet and the palette print the key.</summary>
    public string? ShortcutText => Key is not { } key
        ? null
        : (Modifiers == ModifierKeys.None ? "" : "Ctrl+") + KeyName(key);

    private static string KeyName(System.Windows.Input.Key key) => key switch
    {
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)key - (int)System.Windows.Input.Key.D0).ToString(),
        System.Windows.Input.Key.OemQuestion => "?",
        _ => key.ToString(),
    };
}

public static class ActionRegistry
{
    /// <summary>
    /// The order here is the rail's order: what the calls are, what they left behind, the two ways
    /// to look something up, and the status page in the footer. The digits follow it.
    /// </summary>
    public static IReadOnlyList<AppAction> All { get; } =
    [
        new("Genel bakış", "Sayfaya git", Wpf.Ui.Controls.SymbolRegular.Home24,
            shell => shell.NavigateCommand.Execute("Overview"), ShellPage.Overview, Key.D1),
        new("Görüşmeler", "Sayfaya git — bütün görüşmeler, güne göre, süzgeçli", Wpf.Ui.Controls.SymbolRegular.Call24,
            shell => shell.NavigateCommand.Execute("Calls"), ShellPage.Calls, Key.D2),
        new("Kişiler", "Sayfaya git", Wpf.Ui.Controls.SymbolRegular.People24,
            shell => shell.NavigateCommand.Execute("Contacts"), ShellPage.Contacts, Key.D3),

        new("Defter", "Sayfaya git — değişen rakamlar ve işaretler", Wpf.Ui.Controls.SymbolRegular.Flag24,
            shell => shell.NavigateCommand.Execute("Ledger"), ShellPage.Ledger, Key.D4),
        new("Sözler", "Sayfaya git — kim kime ne söz verdi, vade, tutuldu mu", Wpf.Ui.Controls.SymbolRegular.Handshake24,
            shell => shell.NavigateCommand.Execute("Promises"), ShellPage.Promises, Key.D5),
        new("Takvim", "Sayfaya git — ay görünümü, hatırlatıcılar ve vadeler", Wpf.Ui.Controls.SymbolRegular.CalendarLtr24,
            shell => shell.NavigateCommand.Execute("Calendar"), ShellPage.Calendar, Key.D6),
        new("Yapılacaklar", "Sayfaya git — yazdıkların, öneriler, hatırlatmalar", Wpf.Ui.Controls.SymbolRegular.TaskListLtr24,
            shell => shell.NavigateCommand.Execute("Todo"), ShellPage.Todo, Key.D7),

        new("Aynam", "Sayfaya git — kendi konuşma alışkanlıkların, sayılarla ve anlarla", Wpf.Ui.Controls.SymbolRegular.PersonFeedback24,
            shell => shell.NavigateCommand.Execute("Mirror"), ShellPage.Mirror, Key.D8),

        new("Arama", "Sayfaya git — metinlerde ve etiketlerde ara", Wpf.Ui.Controls.SymbolRegular.Search24,
            shell => shell.NavigateCommand.Execute("Search"), ShellPage.Search, Key.F),
        new("Sor", "Sayfaya git — arşive soru sor", Wpf.Ui.Controls.SymbolRegular.ChatHelp24,
            shell => shell.NavigateCommand.Execute("Ask"), ShellPage.Ask, Key.D9),
        new("Durum", "Sayfaya git — sağlık, kullanım, günlük", Wpf.Ui.Controls.SymbolRegular.Pulse24,
            shell => shell.NavigateCommand.Execute("Health"), ShellPage.Health, Key.D0),

        new("Komut paleti", "Komut ya da kişi ara", Wpf.Ui.Controls.SymbolRegular.Keyboard24,
            shell => shell.OpenPaletteCommand.Execute(null), Key: Key.K),
        new("Kaydı başlat", "Elle kayıt — tespit beklemeden", Wpf.Ui.Controls.SymbolRegular.Record24,
            shell => shell.StartManualRecordingCommand.Execute(null)),
        new("Kaydı durdur", "Elle kaydı bitir", Wpf.Ui.Controls.SymbolRegular.Stop24,
            shell => shell.StopManualRecordingCommand.Execute(null)),
        new("Yenile", "Her sayfayı yeniden yükle", Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24,
            shell => shell.RefreshAll(), Key: Key.F5, Modifiers: ModifierKeys.None),
        new("Klavye kısayolları", "Bu liste", Wpf.Ui.Controls.SymbolRegular.Info24,
            shell => shell.ShowShortcutsCommand.Execute(null), Key: Key.OemQuestion),
    ];

    /// <summary>The action that opens a page, for a rail or a test that wants its key.</summary>
    public static AppAction? ForPage(ShellPage page) => All.FirstOrDefault(a => a.Page == page);

    /// <summary>
    /// Prefix beats substring beats subsequence — all through Turkish folding, so İ/ı never
    /// hides a match. Zero score means no match at all.
    /// </summary>
    public static int Score(string foldedQuery, string foldedTitle)
    {
        if (foldedQuery.Length == 0) return 1;
        if (foldedTitle.StartsWith(foldedQuery, StringComparison.Ordinal)) return 100;
        if (foldedTitle.Contains(foldedQuery, StringComparison.Ordinal)) return 50;

        // Subsequence: every query character appears, in order.
        var at = 0;
        foreach (var c in foldedQuery)
        {
            at = foldedTitle.IndexOf(c, at);
            if (at < 0) return 0;
            at++;
        }

        return 10;
    }
}
