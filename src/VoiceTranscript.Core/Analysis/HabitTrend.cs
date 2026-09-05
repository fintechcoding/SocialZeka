using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Analysis;

/// <summary>The six figures the mirror page draws, each with its own denominator.</summary>
public enum HabitMetric
{
    /// <summary>Certain swear words per minute of the user's speech.</summary>
    Profanity,

    /// <summary>Certain fillers per hundred of the user's words.</summary>
    Filler,

    /// <summary>Words per minute, over lines with word timings only. Comparable within one engine.</summary>
    SpeechRate,

    /// <summary>The user's share of the speaking time, 0..1.</summary>
    TalkShare,

    /// <summary>Times the user cut in, per ten minutes of conversation.</summary>
    Interruptions,

    /// <summary>Moments a shaped piece of information was read out. A count, not a rate.</summary>
    Disclosures,
}

/// <summary>One call as the trend sees it: when, with whom, by which engine, and what was counted.</summary>
/// <param name="Engine">The engine of the transcript the report was counted FROM — the badge on the dot — or null when unrecorded.</param>
/// <param name="LikelyNoHeadphones">Drawn hollow: attribution on such a call is not trusted.</param>
public sealed record HabitSample(
    long CallId,
    DateTimeOffset StartedAt,
    long? ContactId,
    string? Engine,
    HabitReport Report,
    TalkStats Talk,
    bool LikelyNoHeadphones = false);

/// <summary>One call's dot on the curve.</summary>
public sealed record TrendPoint(long CallId, DateTimeOffset At, string? Engine, double Value, bool Hollow);

/// <summary>One month's line: the pooled figure and how many calls fed it.</summary>
public sealed record MonthPoint(int Year, int Month, double Value, int Calls);

/// <summary>The engine changed between the call before and the call at <paramref name="At"/>: a dashed vertical line.</summary>
public sealed record EngineBreak(DateTimeOffset At, string? From, string? To);

public sealed record TrendSeries(
    HabitMetric Metric,
    IReadOnlyList<TrendPoint> Calls,
    IReadOnlyList<MonthPoint> Months,
    IReadOnlyList<EngineBreak> Breaks);

/// <summary>
/// Turns stored reports into the points the mirror page draws.
///
/// Every month is POOLED, never averaged: the month's swear words divided by the month's
/// minutes, not the mean of each call's rate. A two-minute call with one hit is thirty per
/// hour; averaged in beside an hour-long call with none, it would drag the month to fifteen and
/// say the user swore for the whole month at a rate they reached for two minutes. Pooling is
/// what "per minute of your own speech" means across more than one call.
///
/// The engine is carried on every dot and a break is marked wherever it changes, because the
/// engines do not count alike — one hears fillers the other normalises away — and a curve that
/// connected across a change would show the switch as a change in the speaker.
///
/// Months are taken from the sample's <see cref="HabitSample.StartedAt"/> as given. The
/// repository hands back UTC; a caller that wants local months converts first.
/// </summary>
public static class HabitTrend
{
    /// <summary>One call's figure for a metric, or null when its denominator is zero (no words, no timings, nobody spoke).</summary>
    public static double? Value(HabitMetric metric, HabitReport report, TalkStats talk) =>
        Fraction(metric, report, talk) is { Denominator: > 0 } f ? f.Numerator / f.Denominator : null;

    /// <summary>
    /// The numerator and denominator behind <see cref="Value"/>, so months can pool them. Null
    /// when the call cannot contribute: a report with no word timings has no speech rate to
    /// give, and a call where nobody spoke has no share.
    /// </summary>
    public static (double Numerator, double Denominator)? Fraction(HabitMetric metric, HabitReport report, TalkStats talk)
    {
        switch (metric)
        {
            case HabitMetric.Profanity:
                return report.MySpokenMs > 0 ? (report.CountOf(HabitKind.Profanity).Certain, report.MyMinutes) : null;

            case HabitMetric.Filler:
                return report.MyWords > 0 ? (report.CountOf(HabitKind.Filler).Certain * 100.0, report.MyWords) : null;

            case HabitMetric.SpeechRate:
                return report.TimedMs > 0 ? (report.TimedWords, report.TimedMs / 60000.0) : null;

            case HabitMetric.TalkShare:
                return talk.TotalMs > 0 ? (talk.MineMs, talk.TotalMs) : null;

            case HabitMetric.Interruptions:
                return talk.TotalMs > 0 ? (talk.MyInterruptions, talk.TotalMs / 600000.0) : null;

            case HabitMetric.Disclosures:
                return (report.Disclosures.Count, 1);

            default:
                return null;
        }
    }

    /// <summary>The series for one metric over a set of calls, in time order.</summary>
    public static TrendSeries Build(HabitMetric metric, IEnumerable<HabitSample> samples)
    {
        var ordered = samples.OrderBy(s => s.StartedAt).ThenBy(s => s.CallId).ToList();

        List<TrendPoint> points = [];
        List<EngineBreak> breaks = [];
        var pooled = new Dictionary<(int Year, int Month), (double N, double D, int Calls)>();

        string? previousEngine = null;
        var first = true;

        foreach (var sample in ordered)
        {
            // A break is a change between consecutive CALLS, whether or not either call has a
            // dot for this metric: the engine changed on that day either way.
            if (!first && !string.Equals(previousEngine, sample.Engine, StringComparison.Ordinal))
                breaks.Add(new EngineBreak(sample.StartedAt, previousEngine, sample.Engine));

            previousEngine = sample.Engine;
            first = false;

            if (Fraction(metric, sample.Report, sample.Talk) is not { Denominator: > 0 } fraction) continue;

            var (n, d) = fraction;
            points.Add(new TrendPoint(sample.CallId, sample.StartedAt, sample.Engine, n / d, sample.LikelyNoHeadphones));

            var key = (sample.StartedAt.Year, sample.StartedAt.Month);
            var soFar = pooled.GetValueOrDefault(key);
            pooled[key] = (soFar.N + n, soFar.D + d, soFar.Calls + 1);
        }

        var months = pooled
            .OrderBy(p => p.Key.Year).ThenBy(p => p.Key.Month)
            .Select(p => new MonthPoint(p.Key.Year, p.Key.Month, p.Value.N / p.Value.D, p.Value.Calls))
            .ToList();

        return new TrendSeries(metric, points, months, breaks);
    }
}
