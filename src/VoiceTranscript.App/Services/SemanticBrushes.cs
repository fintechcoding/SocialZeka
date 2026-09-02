using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace VoiceTranscript.App.Services;

/// <summary>
/// The three colours that carry meaning — me, the other party, good — in a light and a dark pair.
///
/// They are fixed rather than taken from the accent colour, and deliberately so: "me" must be
/// the same blue on every machine or a screenshot of a transcript stops being readable. But
/// fixed does not mean one: the grey chosen for the other party sits at 3:1 on a dark card and
/// the blue and green go muddy. Each has a dark twin, and the pair is swapped whenever the
/// application's theme changes. Consumers use DynamicResource, so the swap reaches them.
/// </summary>
public static class SemanticBrushes
{
    private static readonly (string Key, Color Light, Color Dark)[] Pairs =
    [
        ("MeBrush", Color.FromRgb(0x0F, 0x6C, 0xBD), Color.FromRgb(0x60, 0xA5, 0xFA)),
        ("ThemBrush", Color.FromRgb(0x7A, 0x75, 0x74), Color.FromRgb(0xB8, 0xB3, 0xB0)),
        ("GoodBrush", Color.FromRgb(0x0F, 0x7B, 0x0F), Color.FromRgb(0x6C, 0xCB, 0x5F)),
    ];

    public static void Apply()
    {
        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        foreach (var (key, light, darkColour) in Pairs)
        {
            var brush = new SolidColorBrush(dark ? darkColour : light);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    /// <summary>Follows the theme from now on. Called once at startup.</summary>
    public static void Follow()
    {
        ApplicationThemeManager.Changed += (_, _) => Apply();
        Apply();
    }
}
