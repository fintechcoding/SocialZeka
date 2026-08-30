using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The interface's words, in both languages.
///
/// Localisation fails quietly in a particular way: a key that resolves to nothing produces a
/// blank label rather than an error, and a screen with three empty captions on it looks like a
/// layout problem rather than a missing string. So the dictionaries are checked against each
/// other and against the markup, which is where the keys are actually used.
/// </summary>
public class LocalisationTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }

    private static Dictionary<string, string> Read(string code)
    {
        var path = Path.Combine(Root, "src", "VoiceTranscript.Core", "Resources", $"strings.{code}.json");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
               ?? throw new InvalidOperationException(path);
    }

    [Fact]
    public void BothLanguagesCoverExactlyTheSameKeys()
    {
        var tr = Read("tr");
        var en = Read("en");

        var missing = tr.Keys.Except(en.Keys).OrderBy(k => k).ToList();
        var extra = en.Keys.Except(tr.Keys).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0, "İngilizcede eksik: " + string.Join(", ", missing.Take(10)));
        Assert.True(extra.Count == 0, "Türkçede olmayan: " + string.Join(", ", extra.Take(10)));
    }

    [Fact]
    public void NoStringIsBlank()
    {
        // A blank value is worse than a missing key: the fallback catches a missing key, and
        // nothing catches an empty string.
        foreach (var code in new[] { "tr", "en" })
        {
            foreach (var (key, value) in Read(code))
                Assert.False(string.IsNullOrWhiteSpace(value), $"{code}: {key}");
        }
    }

    [Fact]
    public void EveryKeyUsedInTheMarkupExists()
    {
        // The markup is the only place these keys are written by hand, so it is the only place
        // one can be mistyped. A mistyped key renders as itself, which is at least visible — but
        // finding it here is better than finding it on screen.
        var tr = Read("tr");
        var used = new HashSet<string>(StringComparer.Ordinal);

        var pattern = new Regex(@"\{loc:T\s+([^\}\s]+)\s*\}", RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(Root, "src", "VoiceTranscript.App"), "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                used.Add(match.Groups[1].Value);
        }

        Assert.NotEmpty(used);

        var unknown = used.Except(tr.Keys).OrderBy(k => k).ToList();
        Assert.True(unknown.Count == 0, "Sözlükte olmayan anahtarlar: " + string.Join(", ", unknown.Take(10)));
    }

    [Fact]
    public void TheDictionariesAreShippedInsideTheAssembly()
    {
        // Loose files beside the executable do not survive an update: the application installs
        // into a versioned directory that is replaced wholesale. If the resource name is wrong
        // every string in the product falls back to its own key, on every screen.
        var assembly = typeof(Localisation).Assembly;
        var names = assembly.GetManifestResourceNames();

        Assert.True(names.Any(n => n.EndsWith("strings.tr.json", StringComparison.Ordinal)),
            "gömülü kaynaklar: " + string.Join(" | ", names));

        Assert.True(names.Any(n => n.EndsWith("strings.en.json", StringComparison.Ordinal)),
            "gömülü kaynaklar: " + string.Join(" | ", names));
    }

    [Fact]
    public void AKnownStringResolvesInBothLanguages()
    {
        Localisation.Use("tr");
        Assert.Equal("Ayarlar", Localisation.T("mainwindow.ayarlar"));

        Localisation.Use("en");
        Assert.Equal("Settings", Localisation.T("mainwindow.ayarlar"));

        Localisation.Use("tr");
    }

    [Fact]
    public void AnUnknownLanguageFallsBackRatherThanThrowing()
    {
        // A settings file carried over from a later version can name a language this build does
        // not have. That must not stop the application opening.
        Localisation.Use("klingon");

        Assert.Equal("tr", Localisation.Language);
        Assert.Equal("Ayarlar", Localisation.T("mainwindow.ayarlar"));
    }

    [Fact]
    public void AMissingKeyShowsItselfRatherThanNothing()
    {
        // Deliberately ugly. A missing string that renders as "overview.nope" is a fault
        // somebody reports; one that renders as an empty label is a fault nobody notices.
        Assert.Equal("overview.nope", Localisation.T("overview.nope"));
    }
}
