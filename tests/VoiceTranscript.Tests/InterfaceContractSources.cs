using System.Reflection;
using System.Text.RegularExpressions;

namespace VoiceTranscript.Tests;

/// <summary>
/// Where the interface contract's rules get their text, and how they read it.
///
/// PLAN-IKINCI-TUR §4 is twelve rules over the same two piles: the markup under
/// <c>src/VoiceTranscript.App</c> and the view models behind it. Ten of the twelve are text
/// scans, and a scan that each rule writes for itself is a scan each rule gets subtly wrong —
/// one forgets the windows, one forgets that an attribute can wrap onto the next line, one
/// looks at a file the build no longer compiles. So the reading happens once, here, and every
/// rule asks the same questions of the same pile.
///
/// <b>Attributes are read per element, not per line.</b> This markup wraps: a chip's Command is
/// on one line and its CommandParameter on the next, and a line-at-a-time scan sees two
/// unrelated fragments. <see cref="Elements"/> hands back whole start tags, so a rule can ask
/// "does this one element carry both of these" and get a true answer.
/// </summary>
internal static class InterfaceContractSources
{
    /// <summary>The repository, found from wherever the test assembly was copied to.</summary>
    internal static string Root { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }

    private static string AppDirectory => Path.Combine(Root, "src", "VoiceTranscript.App");

    /// <summary>Every piece of markup in the application, windows and pages alike.</summary>
    internal static IReadOnlyList<string> Markup() =>
        Directory.Exists(AppDirectory)
            ? [.. Directory.GetFiles(AppDirectory, "*.xaml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// The screens the rail can navigate to — the pages the page skeleton is about.
    ///
    /// <c>Views/*Page.xaml</c> is nearly that list but not quite: AiStatusPage and
    /// ProcessingPage are named like pages and are really tabs inside HealthPage. They must not
    /// carry a page title of their own — a second 28-pixel heading inside somebody else's page
    /// is the incoherence the rule exists to prevent, not an instance of it. So a page that
    /// another page instantiates is a section of that page and is dropped here, which needs no
    /// hand-kept list: it is visible in the markup as &lt;views:AiStatusPage&gt;.
    /// </summary>
    internal static IReadOnlyList<string> Pages()
    {
        var views = Path.Combine(AppDirectory, "Views");
        if (!Directory.Exists(views)) return [];

        var files = Directory.GetFiles(views, "*Page.xaml", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var hosted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);

            foreach (var other in files)
            {
                var name = Path.GetFileNameWithoutExtension(other);

                if (!string.Equals(name, Path.GetFileNameWithoutExtension(file), StringComparison.Ordinal)
                    && text.Contains("<views:" + name, StringComparison.Ordinal))
                {
                    hosted.Add(other);
                }
            }
        }

        return [.. files.Where(f => !hosted.Contains(f))];
    }

    /// <summary>One start tag, with the file and line it was written on.</summary>
    /// <param name="File">Absolute path.</param>
    /// <param name="Line">One-based, for an error message somebody can click.</param>
    /// <param name="Name">The element's name, namespace prefix included.</param>
    /// <param name="Text">The whole start tag, attributes and all.</param>
    internal readonly record struct Element(string File, int Line, string Name, string Text)
    {
        /// <summary>The value of one attribute, or null when the element does not carry it.</summary>
        internal string? Attribute(string name)
        {
            var match = Regex.Match(Text, @"(?<![\w.])" + Regex.Escape(name) + @"\s*=\s*""([^""]*)""");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>Where to point somebody: <c>LedgerPage.xaml:83</c>.</summary>
        internal string Where => $"{Path.GetFileName(File)}:{Line}";
    }

    /// <summary>
    /// Every start tag in one file, comments removed.
    ///
    /// Comments are stripped first because this repository writes long ones and they contain
    /// example markup: a rule counting "&lt;StackPanel Style=EmptyState&gt;" would otherwise
    /// find one in a paragraph explaining why there should be one.
    /// </summary>
    internal static IEnumerable<Element> Elements(string file)
    {
        var text = File.ReadAllText(file);
        var stripped = Regex.Replace(text, "<!--.*?-->", m => Blank(m.Value), RegexOptions.Singleline);

        foreach (Match match in Regex.Matches(stripped, @"<([A-Za-z_][\w.:]*)((?:[^<>""]|""[^""]*"")*?)/?>", RegexOptions.Singleline))
        {
            var line = stripped.Take(match.Index).Count(c => c == '\n') + 1;
            yield return new Element(file, line, match.Groups[1].Value, match.Value);
        }
    }

    /// <summary>Keeps the line numbering while taking a comment's content out of the way.</summary>
    private static string Blank(string comment) =>
        new([.. comment.Select(c => c == '\n' || c == '\r' ? c : ' ')]);

    /// <summary>
    /// The view model a piece of markup is written against, from its own design-time
    /// declaration — <c>d:DataContext="{d:DesignInstance vm:LedgerViewModel}"</c>.
    ///
    /// Resolving a binding path by property name alone is not good enough: <c>Filter</c> is a
    /// <c>LedgerFilter</c> on one page and a <c>PromiseFilter</c> on another, and a rule that
    /// picked either would be right half the time. Every view here declares its own, so the
    /// answer is in the file being read.
    /// </summary>
    internal static Type? DataContextOf(string file)
    {
        var match = Regex.Match(File.ReadAllText(file), @"d:DataContext\s*=\s*""\{d:DesignInstance\s+vm:(\w+)\}""");
        if (!match.Success) return null;

        return typeof(VoiceTranscript.App.ViewModels.LedgerViewModel).Assembly
            .GetType("VoiceTranscript.App.ViewModels." + match.Groups[1].Value);
    }

    /// <summary>The property a binding path names, or null when the view model has no such member.</summary>
    internal static PropertyInfo? Property(Type? owner, string path) =>
        owner?.GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
}
