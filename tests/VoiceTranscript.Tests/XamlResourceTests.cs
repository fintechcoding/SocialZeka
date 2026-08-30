using System.IO;
using System.Text.RegularExpressions;

namespace VoiceTranscript.Tests;

/// <summary>
/// Guards the one class of user interface bug the compiler does not catch.
///
/// A <c>{StaticResource Foo}</c> whose key does not exist compiles cleanly and then throws when
/// the window is loaded — so the application starts, the user clicks something, and it dies.
/// This has already happened once here: a theme rewrite dropped several brush keys while five
/// files still referred to them, and the build stayed green.
///
/// The check is textual on purpose. Instantiating the windows would need a WPF application, an
/// STA thread, a database and a Python worker, none of which exist on a build agent; the actual
/// failure is a missing dictionary key, and that is visible in the markup.
/// </summary>
public class XamlResourceTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    private static string[] AppXamlFiles()
    {
        var root = RepoRoot();
        if (root is null) return [];

        var app = Path.Combine(root, "src", "VoiceTranscript.App");
        return Directory.Exists(app) ? Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories) : [];
    }

    [Fact]
    public void EveryStaticResourceKeyIsDefinedSomewhereInTheApplication()
    {
        var files = AppXamlFiles();
        Assert.SkipWhen(files.Length == 0, "Application markup not found next to the test assembly.");

        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<(string Key, string File, int Line)>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Regex.Matches(lines[i], @"x:Key=""([^""]+)"""))
                {
                    defined.Add(m.Groups[1].Value);
                }

                foreach (Match m in Regex.Matches(lines[i], @"StaticResource\s+([A-Za-z0-9_.]+)"))
                {
                    used.Add((m.Groups[1].Value, Path.GetFileName(file), i + 1));
                }
            }
        }

        var missing = used
            .Where(u => !defined.Contains(u.Key))
            .Select(u => $"{u.File}:{u.Line} → {u.Key}")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0,
            "Bu anahtarlar hiçbir sözlükte tanımlı değil, pencere açılırken hata verir:\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>
    /// The system palette changes underneath a running window when the user switches Windows
    /// between light and dark. A StaticResource captures the brush once and then never updates,
    /// so half the window would keep the old theme's colours — dark text on a dark card. Those
    /// keys must always be referenced dynamically.
    /// </summary>
    [Fact]
    public void SystemPaletteBrushesAreNeverReferencedStatically()
    {
        var files = AppXamlFiles();
        Assert.SkipWhen(files.Length == 0, "Application markup not found next to the test assembly.");

        // The Fluent dictionary's own naming: TextFillColorPrimaryBrush, CardBackgroundFillColor…,
        // AccentFillColor…, SystemFillColorCaution…, DividerStrokeColor… and friends.
        var palette = new Regex(
            @"StaticResource\s+((?:Text|Card|Accent|System|Divider|Control|Subtle|Solid|Layer)\w*(?:Fill|Stroke|Background)\w*Brush)");

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in palette.Matches(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} → {m.Groups[1].Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Sistem paleti fırçaları DynamicResource ile kullanılmalı, yoksa tema değişince " +
            "eski renkte kalırlar:\n  " + string.Join("\n  ", offenders));
    }
}
