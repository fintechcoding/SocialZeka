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
/// resolution takes the day the words were spoken as a required argument and never reads the
/// clock. There is deliberately no "today" default: it existed once, both production callers
/// leaned on it, and every re-analysis of an old call moved "cuma" into the current week and
/// produced an overdue promise nobody had made.
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
    /// Resolves a spoken date relative to the day it was said. Returns null when unsure.
    /// </summary>
    /// <param name="spokenOn">
    /// The local date of the call the phrase was heard in — the caller's
    /// <c>DateOnly.FromDateTime(call.StartedAt.LocalDateTime)</c>. Never the current date.
    /// </param>
    public static DateOnly? TryResolve(string? phrase, DateOnly spokenOn)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;
        if (IsNonCommittal(phrase)) return null;

        var text = TurkishText.NormalizeForSearch(phrase);

        if (text.Contains("bugun", StringComparison.Ordinal)) return spokenOn;
        if (text.Contains("yarin", StringComparison.Ordinal)) return spokenOn.AddDays(1);
        if (text.Contains("obur gun", StringComparison.Ordinal)) return spokenOn.AddDays(2);

        // "3 gün sonra", "iki hafta içinde": a count of days or weeks from the call.
        if (TryCounted(text, spokenOn, out var counted)) return counted;

        // An explicit date, with or without the year.
        if (TryExplicit(text, spokenOn, out var explicitDate)) return explicitDate;

        if (TryWeekday(text, spokenOn, out var weekday)) return weekday;

        // "Haftaya" on its own is a week, not a day. Only resolve it when paired with a weekday,
        // which the branch above already handles.
        if (text.Contains("ay sonu", StringComparison.Ordinal))
            return new DateOnly(spokenOn.Year, spokenOn.Month, DateTime.DaysInMonth(spokenOn.Year, spokenOn.Month));

        return null;
    }

    private static readonly Dictionary<string, int> SmallNumbers = new()
    {
        ["bir"] = 1, ["iki"] = 2, ["uc"] = 3, ["dort"] = 4, ["bes"] = 5,
        ["alti"] = 6, ["yedi"] = 7, ["sekiz"] = 8, ["dokuz"] = 9, ["on"] = 10,
    };

    /// <summary>
    /// "3 gün sonra", "iki hafta içinde" — a count of days or weeks from the day of the call.
    ///
    /// Only a single plain count qualifies. "Bir iki gün" is a range and "birkaç gün" is a
    /// shrug; pinning either to a date would invent a deadline. A weekday alongside the count
    /// ("iki hafta sonra cuma") is left to the weekday rule.
    /// </summary>
    private static bool TryCounted(string text, DateOnly spokenOn, out DateOnly result)
    {
        result = default;

        if (Weekdays.Keys.Any(w => text.Contains(w, StringComparison.Ordinal))) return false;

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', ',', ';', '!', '?'))
            .ToArray();

        for (var i = 0; i < tokens.Length - 2; i++)
        {
            if (!TryCount(tokens[i], out var count)) continue;
            if (tokens[i + 1] is not ("gun" or "hafta")) continue;
            if (tokens[i + 2] is not ("sonra" or "icinde")) continue;

            // "Bir iki gün sonra": a range, not a date.
            if (i > 0 && TryCount(tokens[i - 1], out _)) return false;

            result = spokenOn.AddDays(tokens[i + 1] == "hafta" ? count * 7 : count);
            return true;
        }

        return false;

        static bool TryCount(string token, out int count)
        {
            if (SmallNumbers.TryGetValue(token, out count)) return true;

            return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out count)
                && count is >= 1 and <= 99;
        }
    }

    private static bool TryWeekday(string text, DateOnly spokenOn, out DateOnly result)
    {
        result = default;

        // The longest name that occurs, not the first in the table: "cumartesi" contains "cuma"
        // and "pazartesi" contains "pazar", and a substring match in table order turned Saturday
        // into Friday — a wrong date, which is worse than none.
        var match = Weekdays
            .Where(w => text.Contains(w.Key, StringComparison.Ordinal))
            .OrderByDescending(w => w.Key.Length)
            .FirstOrDefault();
        if (match.Key is null) return false;

        var daysAhead = ((int)match.Value - (int)spokenOn.DayOfWeek + 7) % 7;

        // "Cuma günü" said on a Friday means next Friday, not today.
        if (daysAhead == 0) daysAhead = 7;

        // "Haftaya cuma" is the Friday of the following week.
        if (text.Contains("haftaya", StringComparison.Ordinal) ||
            text.Contains("gelecek hafta", StringComparison.Ordinal))
        {
            daysAhead += 7;
        }

        result = spokenOn.AddDays(daysAhead);
        return true;
    }

    private static bool TryExplicit(string text, DateOnly spokenOn, out DateOnly result)
    {
        result = default;

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out var day)) continue;
            if (day is < 1 or > 31) continue;
            if (!Months.TryGetValue(tokens[i + 1], out var month)) continue;

            var year = spokenOn.Year;
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
                if (candidate < spokenOn) year++;
            }

            if (day > DateTime.DaysInMonth(year, month)) return false;

            result = new DateOnly(year, month, day);
            return true;
        }

        return false;
    }
}
