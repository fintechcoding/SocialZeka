using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VoiceTranscript.App;

/// <summary>Hides an element when its bound value is null or blank.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>True when there is something to show, used to open the notice bar.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && !(value is string s && string.IsNullOrWhiteSpace(s));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows an element only when the bound value is false.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Shows a page only when it is the selected one.
///
/// The pages are all present in the tree and toggled by visibility rather than swapped through a
/// Frame. That keeps scroll position and selection intact when the user moves between them and
/// back, which matters here: hunting through an archive means going back and forth constantly.
/// </summary>
public sealed class PageVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is null) return Visibility.Collapsed;

        var current = values[0]!.ToString();
        var mine = values[1]?.ToString();

        return string.Equals(current, mine, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows a section only when its collection has something in it.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Visible when the count is greater than zero, or the other way round with "invert".
    ///
    /// The inverse case is what every empty state needs, and without it each one would need a
    /// separate boolean on the view model that says nothing the count does not already say.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var any = value is int count && count > 0;
        if (parameter as string == "invert") any = !any;

        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Names the transcription modes in Turkish, with what each one actually does.</summary>
public sealed class AsrModeNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            VoiceTranscript.Core.Configuration.TranscriptionMode.LocalOnly =>
                "Yalnızca bu makinede",
            VoiceTranscript.Core.Configuration.TranscriptionMode.Automatic =>
                "Otomatik — yerel çalışmazsa buluta gönder",
            VoiceTranscript.Core.Configuration.TranscriptionMode.CloudOnly =>
                "Her zaman buluta gönder",
            _ => value?.ToString() ?? "",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
