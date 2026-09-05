namespace VoiceTranscript.Core.Analysis;

/// <summary>
/// Where each dot, tick and break sits when a series is drawn into a box.
///
/// Pure, and separate from the page that draws it, on the TimelineLayout model: "does a month
/// with no calls still get its tick", "do two engines get two lines", "is the top of the axis a
/// round number" are questions worth answering without a window open. The page turns these
/// numbers into a Polyline and some Ellipses and nothing else.
///
/// Time is linear across the width, from the first day of the earliest month to the last day of
/// the latest, so month ticks fall where the months are and a quiet month stays visibly empty
/// instead of being squeezed out. Values run from zero at the bottom to a rounded ceiling at the
/// top: an axis whose top reads 0,73 is an axis nobody can read a value off.
/// </summary>
public static class HabitTrendLayout
{
    /// <summary>One dot: which point of the series, and where. Hollow when the call's attribution is not trusted.</summary>
    public readonly record struct Dot(int Index, double X, double Y, bool Hollow);

    /// <summary>A month tick on the x axis. The label is the page's: months are words, and words are localised there.</summary>
    public readonly record struct MonthTick(double X, int Year, int Month);

    /// <summary>A value tick on the y axis.</summary>
    public readonly record struct ValueTick(double Y, double Value);

    /// <param name="Dots">One per point of the series, in the series' order.</param>
    /// <param name="Runs">
    /// Indices into <paramref name="Dots"/>, grouped into the stretches the curve connects: consecutive
    /// dots of the same engine. A run of one is a dot with no line through it.
    /// </param>
    /// <param name="BreakXs">Where the dashed engine-change lines go.</param>
    /// <param name="Ceiling">The value at the top of the box.</param>
    public sealed record Layout(
        IReadOnlyList<Dot> Dots,
        IReadOnlyList<IReadOnlyList<int>> Runs,
        IReadOnlyList<double> BreakXs,
        IReadOnlyList<MonthTick> MonthTicks,
        IReadOnlyList<ValueTick> ValueTicks,
        double Ceiling);

    public static readonly Layout Empty = new([], [], [], [], [], 1);

    /// <summary>Places a series into a box of the given size. Empty series, empty layout — never a division by zero.</summary>
    public static Layout Place(TrendSeries series, double width, double height)
    {
        if (series.Calls.Count == 0 || width <= 0 || height <= 0) return Empty;

        var earliest = series.Calls.Min(p => p.At);
        var latest = series.Calls.Max(p => p.At);

        // Whole months at both ends, so the first and last ticks are real month boundaries and
        // a single call in a month still sits inside that month's span rather than on its edge.
        var from = new DateTimeOffset(earliest.Year, earliest.Month, 1, 0, 0, 0, earliest.Offset);
        var to = new DateTimeOffset(latest.Year, latest.Month, 1, 0, 0, 0, latest.Offset).AddMonths(1);

        var span = (to - from).TotalSeconds;
        var ceiling = Ceiling(series.Calls.Max(p => p.Value));

        var dots = new List<Dot>(series.Calls.Count);

        for (var i = 0; i < series.Calls.Count; i++)
        {
            var point = series.Calls[i];
            var x = (point.At - from).TotalSeconds / span * width;
            var y = height - point.Value / ceiling * height;
            dots.Add(new Dot(i, x, y, point.Hollow));
        }

        // The curve connects dots of one engine and stops at a change: a line across a change
        // would draw the switch as a change in the speaker.
        List<IReadOnlyList<int>> runs = [];
        List<int> run = [];

        for (var i = 0; i < series.Calls.Count; i++)
        {
            if (run.Count > 0 && !string.Equals(series.Calls[run[^1]].Engine, series.Calls[i].Engine, StringComparison.Ordinal))
            {
                runs.Add(run);
                run = [];
            }

            run.Add(i);
        }

        if (run.Count > 0) runs.Add(run);

        var breakXs = series.Breaks
            .Where(b => b.At >= from && b.At <= to)
            .Select(b => (b.At - from).TotalSeconds / span * width)
            .ToList();

        List<MonthTick> monthTicks = [];
        for (var month = from; month < to; month = month.AddMonths(1))
            monthTicks.Add(new MonthTick((month - from).TotalSeconds / span * width, month.Year, month.Month));

        List<ValueTick> valueTicks =
        [
            new(height, 0),
            new(height / 2, ceiling / 2),
            new(0, ceiling),
        ];

        return new Layout(dots, runs, breakXs, monthTicks, valueTicks, ceiling);
    }

    /// <summary>
    /// The smallest of 1, 2, 5 times a power of ten that is at least the value — the top of an
    /// axis a person can read. Zero and below give 1, so an all-zero month still has an axis.
    /// </summary>
    public static double Ceiling(double max)
    {
        if (max <= 0 || double.IsNaN(max) || double.IsInfinity(max)) return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));

        foreach (var step in new[] { 1, 2, 5, 10 })
        {
            var candidate = step * magnitude;
            if (candidate >= max) return candidate;
        }

        return 10 * magnitude;
    }
}
