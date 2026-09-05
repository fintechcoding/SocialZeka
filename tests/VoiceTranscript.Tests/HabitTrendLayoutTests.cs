using VoiceTranscript.Core.Analysis;

namespace VoiceTranscript.Tests;

/// <summary>
/// The curve drawn into a box, without a window.
///
/// What must hold: a dot sits where its time and value put it; the top of the axis is a number a
/// person can read; a month with no calls keeps its tick rather than being squeezed out; and the
/// line stops where the engine changes.
/// </summary>
public sealed class HabitTrendLayoutTests
{
    private static readonly DateTimeOffset Jan1 = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

    private static TrendPoint At(DateTimeOffset at, double value, string engine = "x", long id = 0, bool hollow = false) =>
        new(id, at, engine, value, hollow);

    private static TrendSeries Series(params TrendPoint[] points) =>
        new(HabitMetric.Profanity, points, [], []);

    [Fact]
    public void AnEmptySeriesGivesAnEmptyLayoutNotADivisionByZero()
    {
        var layout = HabitTrendLayout.Place(Series(), 300, 100);

        Assert.Empty(layout.Dots);
        Assert.Empty(layout.MonthTicks);
        Assert.Equal(1, layout.Ceiling);
    }

    /// <summary>Time runs linearly across the width from the first month's start to the last month's end; values run from the bottom to the ceiling.</summary>
    [Fact]
    public void DotsSitWhereTimeAndValuePutThem()
    {
        // January and February: the span is Jan 1 to Mar 1, fifty-nine days.
        var layout = HabitTrendLayout.Place(
            Series(At(Jan1, 0), At(Jan1.AddDays(31), 1)), width: 590, height: 100);

        Assert.Equal(1, layout.Ceiling);

        Assert.Equal((0.0, 100.0), (layout.Dots[0].X, layout.Dots[0].Y));
        Assert.Equal(310.0, layout.Dots[1].X, 6);
        Assert.Equal(0.0, layout.Dots[1].Y, 6);
    }

    /// <summary>An axis whose top reads 0,73 is an axis nobody can read a value off.</summary>
    [Theory]
    [InlineData(0.73, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.3, 2)]
    [InlineData(3.2, 5)]
    [InlineData(7, 10)]
    [InlineData(42, 50)]
    [InlineData(0.042, 0.05)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void TheAxisTopIsARoundNumberAtOrAboveTheMaximum(double max, double expected)
    {
        Assert.Equal(expected, HabitTrendLayout.Ceiling(max), 9);
    }

    /// <summary>The value ticks are the bottom, the middle and the ceiling — and the dots are placed against that same ceiling.</summary>
    [Fact]
    public void TheValueTicksFrameTheCeiling()
    {
        var layout = HabitTrendLayout.Place(Series(At(Jan1, 0.73)), 100, 200);

        Assert.Equal([0.0, 0.5, 1.0], layout.ValueTicks.Select(t => t.Value));
        Assert.Equal([200.0, 100.0, 0.0], layout.ValueTicks.Select(t => t.Y));
        Assert.Equal(200 - 0.73 * 200, layout.Dots[0].Y, 6);
    }

    /// <summary>Goes red when a quiet month vanishes from the axis: an empty month is a fact about the period, not a gap to close.</summary>
    [Fact]
    public void AMonthWithNoCallsStillGetsItsTick()
    {
        var layout = HabitTrendLayout.Place(
            Series(At(Jan1.AddDays(10), 1), At(Jan1.AddMonths(2).AddDays(3), 1)), 300, 100);

        Assert.Equal([1, 2, 3], layout.MonthTicks.Select(t => t.Month));
        Assert.Equal(0.0, layout.MonthTicks[0].X);
        Assert.True(layout.MonthTicks[1].X > 0 && layout.MonthTicks[1].X < layout.MonthTicks[2].X);
        Assert.True(layout.MonthTicks[2].X < 300);
    }

    /// <summary>The curve connects dots of one engine and stops where it changes; the break is drawn where the series put it.</summary>
    [Fact]
    public void TheCurveBreaksWhereTheEngineChanges()
    {
        var change = Jan1.AddDays(20);

        var series = new TrendSeries(
            HabitMetric.Filler,
            [
                At(Jan1, 1, "large-v3"),
                At(Jan1.AddDays(10), 2, "large-v3"),
                At(change, 3, "nova-3"),
                At(Jan1.AddDays(25), 4, "nova-3"),
            ],
            [],
            [new EngineBreak(change, "large-v3", "nova-3")]);

        var layout = HabitTrendLayout.Place(series, 310, 100);

        Assert.Equal([[0, 1], [2, 3]], layout.Runs.Select(r => r.ToArray()));
        Assert.Equal(200.0, Assert.Single(layout.BreakXs), 6);
    }

    /// <summary>A single dot lands inside its month under a rounded ceiling, with a full month of axis around it.</summary>
    [Fact]
    public void ASingleDotStillHasAnAxis()
    {
        var layout = HabitTrendLayout.Place(Series(At(Jan1.AddDays(15), 0.4, hollow: true)), 310, 100);

        var dot = Assert.Single(layout.Dots);
        Assert.Equal(150.0, dot.X, 6);
        Assert.Equal(20.0, dot.Y, 6);
        Assert.True(dot.Hollow);
        Assert.Equal(0.5, layout.Ceiling, 9);
        Assert.Single(layout.MonthTicks);
        Assert.Equal([[0]], layout.Runs.Select(r => r.ToArray()));
    }
}
