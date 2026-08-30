using System.Globalization;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Analysis;

/// <summary>
/// Turns a spoken Turkish date into a real one, where that can be done without guessing.
///
/// Resolving "cuma günü" to a date is what makes an overdue promise computable rather than a
/// judgement call. But a wrong date is worse than none: it produces a flag accusing a real
/// person of missing a deadline they never gave. So anything ambiguous returns null and the
/// phrase is kept verbatim instead, shown to the user as they heard it.
///
/// "Yarın" from a call three weeks ago means the day after that call, not tomorrow, which is why
/// every method takes the date of the call rather than reading the clock.
/// </summary>
public static class TurkishDates
{
    private static readonly Dictionary<string, DayOfWeek> Weekdays = new()
    {
        ["pazartesi"] = DayOfWeek.Monday,
        ["sali"] = DayOfWeek.Tuesday,
        ["carsamba"] = DayOfWeek.Wednesday,
        ["persembe"] = DayOfWeek.Thursday,
        ["cuma"] = DayOfWeek.Friday,
        ["cumartesi"] = DayOfWeek.Saturday,
        ["pazar"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, int> Months = new()
    {
        ["ocak"] = 1, ["subat"] = 2, ["mart"] = 3, ["nisan"] = 4,
        ["mayis"] = 5, ["haziran"] = 6, ["temmuz"] = 7, ["agustos"] = 8,
        ["eylul"] = 9, ["ekim"] = 10, ["kasim"] = 11, ["aralik"] = 12,
    };

    /// <summary>
    /// Phrases that sound like a commitment and are not one.
    ///
    /// This matters more than the parsing. In Turkish, "bakarız" and "inşallah" are usually a
    /// polite refusal, and "bir ara" means no particular time at all. Recording those as
    /// promises produces a ledger full of broken commitments nobody ever made — which would
    /// quietly turn the product into a machine for manufacturing grievances.
    /// </summary>
    private static readonly string[] NonCommittal =
    [
        "bakariz", "insallah", "bir ara", "duruma gore", "belki", "muhtemelen",
        "en kisa zamanda", "yakinda", "gorusuruz", "denerim", "calisirim",
    ];

    /// <summary>True when the phrase is a polite deferral rather than a date.</summary>
    public static bool IsNonCommittal(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return true;

        var normalised = TurkishText.NormalizeForSearch(phrase);
        return NonCommittal.Any(p => normalised.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves a spoken date relative to when it was said. Returns null when unsure.
    /// </summary>
    public static DateOnly? TryResolve(string? phrase, DateOnly? spokenOn = null)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;
        if (IsNonCommittal(phrase)) return null;

        var today = spokenOn ?? DateOnly.FromDateTime(DateTime.Now);
        var text = TurkishText.NormalizeForSearch(phrase);

        if (text.Contains("bugun", StringComparison.Ordinal)) return today;
        if (text.Contains("yarin", StringComparison.Ordinal)) return today.AddDays(1);
        if (text.Contains("obur gun", StringComparison.Ordinal)) return today.AddDays(2);

        // An explicit date, with or without the year.
        if (TryExplicit(text, today, out var explicitDate)) return explicitDate;

        if (TryWeekday(text, today, out var weekday)) return weekday;

        // "Haftaya" on its own is a week, not a day. Only resolve it when paired with a weekday,
        // which the branch above already handles.
        if (text.Contains("ay sonu", StringComparison.Ordinal))
            return new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

        return null;
    }

    private static bool TryWeekday(string text, DateOnly today, out DateOnly result)
    {
        result = default;

        var match = Weekdays.FirstOrDefault(w => text.Contains(w.Key, StringComparison.Ordinal));
        if (match.Key is null) return false;

        var daysAhead = ((int)match.Value - (int)today.DayOfWeek + 7) % 7;

        // "Cuma günü" said on a Friday means next Friday, not today.
        if (daysAhead == 0) daysAhead = 7;

        // "Haftaya cuma" is the Friday of the following week.
        if (text.Contains("haftaya", StringComparison.Ordinal) ||
            text.Contains("gelecek hafta", StringComparison.Ordinal))
        {
            daysAhead += 7;
        }

        result = today.AddDays(daysAhead);
        return true;
    }

    private static bool TryExplicit(string text, DateOnly today, out DateOnly result)
    {
        result = default;

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out var day)) continue;
            if (day is < 1 or > 31) continue;
            if (!Months.TryGetValue(tokens[i + 1], out var month)) continue;

            var year = today.Year;
            if (i + 2 < tokens.Length &&
                int.TryParse(tokens[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var spoken) &&
                spoken is >= 2000 and <= 2100)
            {
                year = spoken;
            }
            else
            {
                // No year given. A month already past means they meant next year.
                var candidate = new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
                if (candidate < today) year++;
            }

            if (day > DateTime.DaysInMonth(year, month)) return false;

            result = new DateOnly(year, month, day);
            return true;
        }

        return false;
    }
}
