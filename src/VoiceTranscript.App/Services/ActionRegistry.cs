using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Services;

/// <summary>One thing the palette can do.</summary>
public sealed record AppAction(
    string Title, string Detail, Wpf.Ui.Controls.SymbolRegular Icon, Action<ShellViewModel> Run)
{
    /// <summary>Folded once, matched many times per keystroke.</summary>
    public string Folded { get; } = TurkishText.NormalizeForSearch(Title);
}

/// <summary>
/// Everything the command palette offers, in one place.
///
/// A registry rather than scattered handlers so the palette and the keyboard layer expose the
/// SAME actions — built once, reachable twice. Matching folds through the Turkish rules the
/// rest of the product uses: "isaret" must find "İşaretler", or the palette teaches people it
/// cannot be trusted with their own language.
/// </summary>
public static class ActionRegistry
{
    public static IReadOnlyList<AppAction> All { get; } =
    [
        new("Genel bakış", "Sayfaya git", Wpf.Ui.Controls.SymbolRegular.Home24,
            shell => shell.NavigateCommand.Execute("Overview")),
        new("Defter", "Sayfaya git — sözler, değişen rakamlar, işaretler", Wpf.Ui.Controls.SymbolRegular.Flag24,
            shell => shell.NavigateCommand.Execute("Ledger")),
        new("Takvim", "Sayfaya git — ay görünümü, hatırlatıcılar ve vadeler", Wpf.Ui.Controls.SymbolRegular.CalendarLtr24,
            shell => shell.NavigateCommand.Execute("Calendar")),
        new("Kişiler", "Sayfaya git", Wpf.Ui.Controls.SymbolRegular.People24,
            shell => shell.NavigateCommand.Execute("Contacts")),
        new("Arama", "Sayfaya git — metinlerde ve etiketlerde ara", Wpf.Ui.Controls.SymbolRegular.Search24,
            shell => shell.NavigateCommand.Execute("Search")),
        new("Sor", "Sayfaya git — arşive soru sor", Wpf.Ui.Controls.SymbolRegular.ChatHelp24,
            shell => shell.NavigateCommand.Execute("Ask")),
        new("Durum", "Sayfaya git — sağlık, kullanım, günlük", Wpf.Ui.Controls.SymbolRegular.Pulse24,
            shell => shell.NavigateCommand.Execute("Health")),

        new("Kaydı başlat", "Elle kayıt — tespit beklemeden", Wpf.Ui.Controls.SymbolRegular.Record24,
            shell => shell.StartManualRecordingCommand.Execute(null)),
        new("Kaydı durdur", "Elle kaydı bitir", Wpf.Ui.Controls.SymbolRegular.Stop24,
            shell => shell.StopManualRecordingCommand.Execute(null)),
        new("Yenile", "Her sayfayı yeniden yükle", Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24,
            shell => shell.RefreshAll()),
    ];

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
