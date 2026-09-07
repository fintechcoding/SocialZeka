using System.Reflection;
using System.Text.RegularExpressions;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.Tests;

/// <summary>
/// The four rules of the interface contract that live in the markup — PLAN-IKINCI-TUR §4, K5–K8.
///
/// The user asked for "UI'de bütünlük": one product rather than eleven screens that each solved
/// the same problem their own way. A rule agreed in conversation lasts until the next page is
/// written; a rule with a red test lasts. So each of these four is a rule somebody can break,
/// and a test that goes red the moment they do.
///
/// Three of the four are source scans and one adds reflection, and that is on purpose: the
/// failures they catch are failures of shape, not of behaviour. A hand-copied undo strip runs
/// perfectly — it is simply the sixth copy of something that should exist once, and the sixth
/// copy is the one that will be forgotten when the wording changes. Nothing here needs a
/// database, a Python worker or an STA thread; <see cref="WindowSmokeTests"/> is what proves
/// the markup these rules police still parses.
/// </summary>
public class InterfaceContractScreenTests
{
    // ---- K5: one undo -----------------------------------------------------------------------

    /// <summary>
    /// Goes red when a screen grows its own undo instead of using the shared one.
    ///
    /// Six pages made rulings the user could take back, and each carried its own hand-copied
    /// strip: the same Border, the same three columns, the same ✕ — with a different label key
    /// each time, and two of the six offering "Geri al" whether or not there was anything to
    /// take back. Three of the view models behind them kept their own <c>Notice</c> field and
    /// their own pair of commands rather than the shared <see cref="UndoSlot"/>, and the to-do
    /// page had invented a fourth set of command names for the same two verbs.
    ///
    /// Four things are checked, and each is a way the copy came back:
    /// the strip is the shared control everywhere; every slot a view model holds is actually on
    /// screen; no view model reinvents the slot; and the control binds can-undo, so no strip can
    /// ever again offer a button that does nothing.
    /// </summary>
    [Fact]
    public void EveryTakeBackIsTheOneSharedUndoBar()
    {
        var bar = Path.Combine(InterfaceContractSources.Root,
            "src", "VoiceTranscript.App", "Controls", "UndoBar.xaml");

        Assert.True(File.Exists(bar), "Paylaşılan geri alma şeridi yok: Controls/UndoBar.xaml");

        var failures = new List<string>();

        // 1. Nobody hand-builds a strip. Binding an undo command outside the shared control is
        //    the copy being made again, whatever the copy is called.
        var undoCommand = new Regex(@"\{Binding\s+([\w.]*(?:Undo|UndoDismiss)Command)\b");

        foreach (var file in InterfaceContractSources.Markup())
        {
            if (string.Equals(file, bar, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var element in InterfaceContractSources.Elements(file))
            {
                var match = undoCommand.Match(element.Text);

                if (match.Success)
                    failures.Add($"{element.Where}: elde kopya geri alma şeridi ({match.Groups[1].Value}) — <controls:UndoBar Slot=\"…\" /> kullan.");
            }
        }

        // 2. Every slot a view model carries is shown, by the shared control, in that view
        //    model's own view. A slot nothing displays is a ruling that cannot be taken back.
        var views = InterfaceContractSources.Markup()
            .Select(f => (File: f, Context: InterfaceContractSources.DataContextOf(f)))
            .Where(v => v.Context is not null)
            .ToList();

        foreach (var type in ViewModelTypes())
        {
            foreach (var slot in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.PropertyType == typeof(UndoSlot)))
            {
                var shown = views
                    .Where(v => v.Context == type)
                    .Any(v => Regex.IsMatch(File.ReadAllText(v.File),
                        @"<controls:UndoBar[^>]*Slot\s*=\s*""\{Binding\s+" + Regex.Escape(slot.Name) + @"\s*\}"""));

                if (!shown)
                    failures.Add($"{type.Name}.{slot.Name}: taşınan UndoSlot hiçbir görünümde UndoBar ile gösterilmiyor.");
            }
        }

        // 3. Nobody keeps their own. The slot is the only thing in the application that answers
        //    "can this be taken back".
        foreach (var type in ViewModelTypes().Where(t => t != typeof(UndoSlot)))
        {
            foreach (var name in new[] { "CanUndo", "UndoCommand", "ClearNoticeCommand", "UndoDismissCommand" })
            {
                if (type.GetMember(name, BindingFlags.Public | BindingFlags.Instance).Length > 0)
                    failures.Add($"{type.Name}.{name}: kendi geri almasını taşıyor — UndoSlot kullanmalı.");
            }
        }

        // 4. The one strip asks whether there is anything to take back. Two of the six copies
        //    never did, and their "Geri al" was a button that sometimes did nothing.
        Assert.Contains("CanUndo", File.ReadAllText(bar), StringComparison.Ordinal);

        Assert.True(failures.Count == 0,
            "Tek geri alma (K5) bozuldu:\n  " + string.Join("\n  ", failures));
    }

    // ---- K6: the page skeleton --------------------------------------------------------------

    /// <summary>
    /// Goes red when a page stops looking like the others: no title, no subtitle, or its own
    /// idea of how far the content sits from the window's edge.
    ///
    /// The inset is the point of the third check. <c>PadPage</c> exists because seven different
    /// margins meant the left edge and the title moved a few pixels on every navigation, which
    /// reads as the window frame jumping. A page that hand-rolls a margin instead, or that
    /// spends the page inset on a card in its middle, brings that back one page at a time.
    /// </summary>
    [Fact]
    public void EveryPageCarriesTheSameSkeleton()
    {
        var pages = InterfaceContractSources.Pages();
        Assert.NotEmpty(pages);

        var failures = new List<string>();

        foreach (var page in pages)
        {
            var name = Path.GetFileName(page);
            var elements = InterfaceContractSources.Elements(page).ToList();

            var titles = elements.Count(e => e.Attribute("Style") == "{StaticResource PageTitle}");
            var subtitles = elements.Count(e => e.Attribute("Style") == "{StaticResource PageSubtitle}");

            if (titles != 1) failures.Add($"{name}: {titles} sayfa başlığı (PageTitle), tam olarak 1 olmalı.");
            if (subtitles != 1) failures.Add($"{name}: {subtitles} alt başlık (PageSubtitle), tam olarak 1 olmalı.");

            var padded = elements
                .Where(e => e.Attribute("Margin") == "{StaticResource PadPage}"
                            || e.Attribute("Padding") == "{StaticResource PadPage}")
                .ToList();

            if (padded.Count != 1)
            {
                failures.Add($"{name}: PadPage {padded.Count} yerde, tam olarak 1 olmalı (kökte).");
                continue;
            }

            var root = RootElement(page);

            if (root is null)
                failures.Add($"{name}: kök öğe bulunamadı.");
            else if (root.Value.Line != padded[0].Line)
                failures.Add($"{name}: sayfa boşluğu kökte değil — kök {root.Value.Name} {root.Value.Line}. satırda, PadPage {padded[0].Line}. satırda.");
        }

        Assert.True(failures.Count == 0,
            "Sayfa iskeleti (K6) bozuldu:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// The page's own outermost element: what comes after the UserControl's own property
    /// elements (<c>&lt;UserControl.Resources&gt;</c> and friends), which are declarations
    /// rather than content.
    /// </summary>
    private static InterfaceContractSources.Element? RootElement(string page)
    {
        var elements = InterfaceContractSources.Elements(page).ToList();

        return elements.FirstOrDefault(e =>
            !e.Name.StartsWith("UserControl", StringComparison.Ordinal)
            && !e.Name.Contains('.', StringComparison.Ordinal)
            && elements.IndexOf(e) > 0
            && !InsideResources(elements, e));
    }

    private static bool InsideResources(List<InterfaceContractSources.Element> elements, InterfaceContractSources.Element element)
    {
        var text = File.ReadAllText(elements[0].File);
        var close = text.IndexOf("</UserControl.Resources>", StringComparison.Ordinal);
        if (close < 0) return false;

        var line = text.Take(close).Count(c => c == '\n') + 1;
        return element.Line <= line;
    }

    // ---- K7: the empty state ----------------------------------------------------------------

    /// <summary>
    /// Goes red when a page answers "there is nothing here" with a grey line instead of the
    /// shared empty state.
    ///
    /// An empty screen is the first thing a new user sees, and four pages met them with one
    /// small grey sentence — "Şu an için bir şey yok" — which says nothing about what would
    /// appear there or how to make it appear. The shared control has a title and a body for
    /// exactly that reason.
    ///
    /// A page is asked the question only when its own markup admits the list can be empty: it
    /// shows something on <c>IsEmpty</c>, on an inverted <c>Has…</c> whose collection the page
    /// actually lists, or on a <c>Count</c> that converts to invisible. That is why the Sor page
    /// is not in the list and does not need a hand-kept exception: it hides its history heading
    /// when there is no history and claims nothing about emptiness.
    /// </summary>
    [Fact]
    public void EveryEmptyListSaysWhatWouldBeThere()
    {
        var pages = InterfaceContractSources.Pages();
        Assert.NotEmpty(pages);

        var failures = new List<string>();

        foreach (var page in pages)
        {
            var text = File.ReadAllText(page);

            foreach (var element in InterfaceContractSources.Elements(page))
            {
                if (!IsEmptinessSignal(element, text)) continue;

                if (element.Attribute("Style") != "{StaticResource EmptyState}")
                    failures.Add($"{element.Where}: liste boşken gösterilen {element.Name}, paylaşılan EmptyState değil.");
            }
        }

        Assert.True(failures.Count == 0,
            "Boş durum (K7) bozuldu:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>Whether this element is what the page shows when one of its lists is empty.</summary>
    private static bool IsEmptinessSignal(InterfaceContractSources.Element element, string file)
    {
        var visibility = element.Attribute("Visibility");
        if (visibility is null) return false;

        // "…Count → invisible": the collection is named, so nothing else has to be inferred.
        if (Regex.IsMatch(visibility, @"CountToVisibility\}, ?ConverterParameter=invert")) return true;

        var binding = Regex.Match(visibility, @"\{Binding\s+([\w.]+)\s*,");
        if (!binding.Success) return false;

        var path = binding.Groups[1].Value;

        if (visibility.Contains("{StaticResource BoolToVisibility}", StringComparison.Ordinal))
            return path.Contains("IsEmpty", StringComparison.Ordinal);

        if (!visibility.Contains("{StaticResource InverseBoolToVisibility}", StringComparison.Ordinal)) return false;

        // "Has<X>" is only about emptiness when <X> is something the page lists. HasPhoto and
        // HasSelection are not empty states; HasEntries and HasMoments are.
        if (!path.StartsWith("Has", StringComparison.Ordinal)) return false;

        return Regex.IsMatch(file, @"ItemsSource\s*=\s*""\{Binding\s+" + Regex.Escape(path[3..]) + @"[,\}]");
    }

    // ---- K8: the filter's identity ----------------------------------------------------------

    /// <summary>
    /// Goes red when a filter chip carries a word instead of a value, or when it decides for
    /// itself which chip looks selected.
    ///
    /// The criterion is not "does this contain a Turkish letter" — PLAN-IKINCI-TUR §4.1 threw
    /// that one out, because most of the real violations were pure ASCII ("Hepsi", "Kural",
    /// "Bu ay") and the rule would have stayed green on its own examples. The criterion is
    /// whether the value is a value at all: the property behind the chip must be an enum (or a
    /// number), and the parameter must name one of its members. A string property holding
    /// "Değerlendirme" is a filter whose identity is a sentence in one language, and a sentence
    /// is exactly the thing that changes when the interface changes language.
    ///
    /// The second half matters as much: a chip whose <c>IsChecked="True"</c> is written in the
    /// markup is showing a selection the view model does not know it has. It looks right until
    /// anything but a click changes the filter, and then it lies.
    /// </summary>
    [Fact]
    public void EveryFilterChipCarriesAValueAndReadsItsSelectionFromTheViewModel()
    {
        var failures = new List<string>();

        foreach (var file in InterfaceContractSources.Markup())
        {
            var owner = InterfaceContractSources.DataContextOf(file);

            foreach (var element in InterfaceContractSources.Elements(file))
            {
                var command = Regex.Match(element.Text, @"Command\s*=\s*""\{Binding\s+(Set\w+Command)\b");
                var selected = Regex.Match(element.Text,
                    @"(?:Appearance|IsChecked)\s*=\s*""\{Binding\s+(?:Path\s*=\s*)?([\w.]+).*?(?:FilterAppearance|IsValue|IsPage)\}, ?ConverterParameter=([^,\}""]+)",
                    RegexOptions.Singleline);

                if (!command.Success && !selected.Success) continue;

                var parameter = element.Attribute("CommandParameter");

                // A row's own command carries the row, not a filter value. Those are not chips.
                if (parameter is not null && parameter.StartsWith("{Binding", StringComparison.Ordinal)) continue;

                if (!selected.Success)
                {
                    failures.Add($"{element.Where}: süzgeç çipi seçili hâlini görünüm modelinden okumuyor (IsChecked/Appearance bağlı değil).");
                    continue;
                }

                var path = selected.Groups[1].Value;
                var value = selected.Groups[2].Value.Trim();

                if (parameter is not null && !string.Equals(parameter, value, StringComparison.Ordinal))
                    failures.Add($"{element.Where}: komut '{parameter}' diyor, çip '{value}' diye bakıyor.");

                var property = InterfaceContractSources.Property(owner, path);

                if (property is null)
                {
                    failures.Add($"{element.Where}: '{path}' {owner?.Name ?? "bilinmeyen görünüm modeli"} üzerinde yok.");
                    continue;
                }

                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                if (type.IsEnum)
                {
                    // By name, not by Enum.TryParse: that also accepts "7" and "Open, Kept",
                    // neither of which is a chip naming one value.
                    if (!Enum.GetNames(type).Contains(value, StringComparer.Ordinal))
                        failures.Add($"{element.Where}: '{value}' {type.Name} üyesi değil.");
                }
                else if (type == typeof(int) || type == typeof(long) || type == typeof(double))
                {
                    if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _))
                        failures.Add($"{element.Where}: '{value}' sayı değil, ama {path} sayı taşıyor.");
                }
                else
                {
                    failures.Add($"{element.Where}: süzgeç seçimi {type.Name} olarak taşınıyor — '{value}' bir ekran metni, dilden bağımsız bir enum olmalı.");
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Süzgecin kimliği (K8) bozuldu:\n  " + string.Join("\n  ", failures));
    }

    private static IEnumerable<Type> ViewModelTypes() =>
        typeof(LedgerViewModel).Assembly.GetTypes()
            .Where(t => t.IsClass && t.Namespace == "VoiceTranscript.App.ViewModels");
}
