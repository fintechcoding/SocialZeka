using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VoiceTranscript.App;

/// <summary>
/// Turns a name into up to two initials.
///
/// Turkish casing matters here: <c>i</c> upper-cases to <c>İ</c> and <c>ı</c> to <c>I</c>.
/// Invariant upper-casing would put a visibly wrong letter on somebody's avatar — "İpek" would
/// come out as "IPEK" — and an avatar is the one place a name is reduced to a single glyph.
/// </summary>
public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string;
        if (string.IsNullOrWhiteSpace(name)) return "?";

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var initials = words.Length switch
        {
            0 => "?",
            1 => words[0][..1],
            _ => $"{words[0][..1]}{words[^1][..1]}",
        };

        return Core.Text.TurkishText.ToUpperTr(initials);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Gives a name a stable colour.
///
/// Derived from the name rather than stored, so the same person is the same colour in every
/// session and on every screen without a migration or a settings entry. The palette is fixed and
/// picked for legibility against white text at small sizes; generating a hue from a hash
/// produces muddy yellows and greens that fail exactly that test.
/// </summary>
public sealed class NameToBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x0F, 0x6C, 0xBD), // blue
        Color.FromRgb(0x77, 0x30, 0x8D), // purple
        Color.FromRgb(0x0E, 0x70, 0x61), // teal
        Color.FromRgb(0xB1, 0x46, 0x0F), // orange
        Color.FromRgb(0x8A, 0x2C, 0x4E), // plum
        Color.FromRgb(0x2A, 0x5B, 0x2E), // green
        Color.FromRgb(0x1F, 0x49, 0x8B), // indigo
        Color.FromRgb(0x9A, 0x3B, 0x1B), // rust
    ];

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.Ordinal);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? "";

        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;

            // A plain character sum rather than a hash: this has to stay identical across runs,
            // and string.GetHashCode is randomised per process by design.
            var sum = 0;
            foreach (var c in name) sum = (sum + c) % 4096;

            var brush = new SolidColorBrush(Palette[sum % Palette.Length]);
            brush.Freeze();

            Cache[name] = brush;
            return brush;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a duration the way a call log does: 4:12, or 1:04:12 once it passes an hour.</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var span = value switch
        {
            TimeSpan t => t,
            int ms => TimeSpan.FromMilliseconds(ms),
            long ms => TimeSpan.FromMilliseconds(ms),
            double seconds => TimeSpan.FromSeconds(seconds),
            _ => TimeSpan.Zero,
        };

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Says when something happened in the words a person would use.
///
/// "Dün 14:30" is read at a glance; "29.08.2026 14:30" has to be worked out against today's
/// date. Anything older than a week gets the date, because by then the exact day is the useful
/// part rather than the distance from now.
/// </summary>
public sealed class RelativeDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset when) return "";

        var local = when.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var day = local.Date;

        if (day == today) return $"Bugün {local:HH:mm}";
        if (day == today.AddDays(-1)) return $"Dün {local:HH:mm}";

        var days = (today - day).TotalDays;
        if (days < 7) return $"{local:dddd} {local:HH:mm}";

        return local.Year == today.Year ? $"{local:d MMMM HH:mm}" : $"{local:d MMMM yyyy}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Turns a 0-1 level into the width of a meter fill.
///
/// The parameter is the track width. A decibel curve is used rather than the raw amplitude:
/// ordinary speech sits far down the linear scale, so a linear meter looks almost flat while
/// somebody is talking — which is precisely the case it exists to make visible.
/// </summary>
public sealed class LevelToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value switch
        {
            double d => d,
            float f => f,
            _ => 0.0,
        };

        var track = parameter is string s
                    && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var w)
            ? w
            : 120.0;

        if (level <= 0) return 0.0;

        var db = 20 * Math.Log10(Math.Clamp(level, 1e-4, 1.0));
        return track * Math.Clamp((db + 60) / 60.0, 0, 1);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Human-readable byte sizes, so a disk figure is readable without arithmetic.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => (double)l,
            int i => i,
            double d => d,
            _ => 0.0,
        };

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.#} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows an element only when a number is greater than zero. For badges and counts.</summary>
public sealed class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int i when i > 0 => Visibility.Visible,
            long l when l > 0 => Visibility.Visible,
            double d when d > 0 => Visibility.Visible,
            _ => Visibility.Collapsed,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Splits a 0-1 ratio across two columns.
///
/// Used by the talk-time bar. The parameter selects which side: "me" or "them". This is the one
/// statistic in the application that no competing tool can produce honestly, because it needs
/// the two speakers recorded separately rather than a model guessing at who is talking.
/// </summary>
public sealed class RatioToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is double d ? Math.Clamp(d, 0, 1) : 0.5;
        var wantsMine = parameter as string == "me";

        return new GridLength(wantsMine ? ratio : 1 - ratio, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Titles the empty search panel according to whether a search has happened.
///
/// The two states are completely different messages — "here is how search works" before, and
/// "that phrase is not in any conversation" after — and showing the wrong one makes the feature
/// look broken on first use.
/// </summary>
public sealed class SearchEmptyTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Sonuç yok" : "Ne aramıştın?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Negates a boolean. For enabling a button while a task is not running.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>
/// Titles the empty ledger.
///
/// "Nothing has gone wrong" and "there is nothing here yet" are opposite messages. Showing the
/// wrong one either worries somebody whose archive is fine, or reassures somebody whose recorder
/// has never actually run.
/// </summary>
public sealed class LedgerEmptyTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Bu süzgeçte bir şey yok" : "Defter temiz";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LedgerEmptyBodyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? "Başka bir süzgeç dene ya da kişi filtresini temizle."
            : "Tutulmamış söz, değişen rakam ya da cevapsız kalmış soru bulunmadı. "
              + "Görüşmeler çözümlendikçe burası kendiliğinden dolar.";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Names whichever stream is being listened to.</summary>
/// <summary>
/// Whether a model in the table is already downloaded.
///
/// A multi-binding because the answer needs both the row (which model) and the view model (what
/// is on disk), and a row in a DataGrid has no path to the second.
/// </summary>
public sealed class ModelPresenceConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string modelRef) return "";

        // Before the first probe there is no answer yet, and guessing "not downloaded" would
        // put a wrong label on every row for the second the window takes to ask.
        if (values[1] is not IReadOnlyCollection<string> present) return "";
        if (present.Count == 0) return "";

        return present.Contains(modelRef) ? "İndirildi" : "İnmedi";
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ListeningToConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Sen" : "Karşı taraf";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Names a search period in Turkish.</summary>
public sealed class SearchPeriodNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ViewModels.SearchPeriod period ? ViewModels.SearchPeriodExtensions.Label(period) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Resolves a brush by resource key.
///
/// Lets a view model say "this is a warning" without holding a Brush, which would tie it to a
/// theme and break when the user switches Windows between light and dark mid-session.
/// </summary>
public sealed class BrushFromKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string ?? "TextFillColorSecondaryBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Headline for the status page, from the number of things currently wrong.</summary>
public sealed class HealthHeadlineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            0 => "Her şey çalışıyor",
            1 => "Bir şey dikkat istiyor",
            int n => $"{n} şey dikkat istiyor",
            _ => "Denetlenmedi",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A stored photo path as a drawable image, or nothing.
///
/// Loaded with OnLoad and a decode width, so the file on disk is never held open — a photo the
/// image system kept locked could not be replaced or deleted, and both are ordinary actions.
/// A file that fails to decode (corrupt, half-synced, codec missing) converts to null, and the
/// initials avatar behind it shows instead: a broken picture must never break the window.
/// </summary>
public sealed class PhotoConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 112; // 2× the largest avatar, for high-DPI screens.
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visible when two values are equal — the way a list row learns it is the one being worked on.
///
/// The alternative was pushing an IsActive flag into every row object and rebuilding the list on
/// each progress tick, which flickers and loses the selection several times a second.
/// </summary>
public sealed class EqualToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values is [{ } a, { } b, ..] && Equals(a, b)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Primary when the bound filter equals the parameter — how a row of filter buttons shows which
/// one is on. Seven identical grey buttons answered "hangisi seçili?" with silence.
/// </summary>
public sealed class FilterToAppearanceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal)
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
