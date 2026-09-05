using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Stored reports turned into the points the mirror page draws.
///
/// Two properties carry the page. A month is POOLED — the month's hits over the month's minutes
/// — because averaging each call's rate lets a two-minute call outweigh an hour; and the engine
/// travels with every dot and marks a break where it changes, because the engines do not count
/// alike and a curve across a change would show the switch as a change in the speaker.
/// </summary>
public sealed class HabitTrendTests
{
    private static readonly DateTimeOffset June = DateTimeOffset.Parse("2026-06-10T10:00:00+03:00");

    private static HabitReport Report(int profanity = 0, int filler = 0, int spokenMs = 60_000, int words = 100, int timedWords = 0, int timedMs = 0, int disclosures = 0) => new()
    {
        Counts =
        [
            new HabitCount(HabitKind.Profanity, profanity, 0, 0),
            new HabitCount(HabitKind.Filler, filler, 0, 0),
            new HabitCount(HabitKind.Dialect, 0, 0, 0),
        ],
        MySpokenMs = spokenMs,
        MyWords = words,
        TimedWords = timedWords,
        TimedMs = timedMs,
        Disclosures = [.. Enumerable.Range(0, disclosures).Select(i => new DisclosureMoment(DisclosureKind.Amount, i * 1000, i * 1000 + 500))],
    };

    private static TalkStats Talk(int mine = 60_000, int theirs = 60_000, int myCuts = 0) =>
        new(mine, theirs, myCuts, 0, 0, 0, null, null, 0);

    private static HabitSample Sample(long id, DateTimeOffset at, string? engine, HabitReport report, TalkStats? talk = null, bool hollow = false) =>
        new(id, at, null, engine, report, talk ?? Talk(), hollow);

    /// <summary>Goes red when a month averages the calls' rates instead of pooling their counts and minutes.</summary>
    [Fact]
    public void AMonthIsPooledNotAveraged()
    {
        var series = HabitTrend.Build(HabitMetric.Profanity,
        [
            Sample(1, June, "large-v3", Report(profanity: 1, spokenMs: 120_000)),      // 0.5 / min
            Sample(2, June.AddDays(1), "large-v3", Report(profanity: 0, spokenMs: 58 * 60_000)),
        ]);

        var month = Assert.Single(series.Months);
        Assert.Equal((2026, 6, 2), (month.Year, month.Month, month.Calls));
        Assert.Equal(1.0 / 60, month.Value, 9);

        Assert.Equal([0.5, 0.0], series.Calls.Select(p => p.Value));
    }

    /// <summary>A call with nothing in the denominator has no dot, and does not pull the month towards zero either.</summary>
    [Fact]
    public void ACallThatCannotContributeHasNoDot()
    {
        var series = HabitTrend.Build(HabitMetric.SpeechRate,
        [
            Sample(1, June, "large-v3", Report(timedWords: 0, timedMs: 0)),
            Sample(2, June.AddDays(1), "large-v3", Report(timedWords: 300, timedMs: 120_000)),
        ]);

        var dot = Assert.Single(series.Calls);
        Assert.Equal(2, dot.CallId);
        Assert.Equal(150.0, dot.Value);
        Assert.Equal(1, Assert.Single(series.Months).Calls);

        Assert.Empty(HabitTrend.Build(HabitMetric.Profanity, [Sample(1, June, null, Report(spokenMs: 0))]).Calls);
        Assert.Empty(HabitTrend.Build(HabitMetric.TalkShare, [Sample(1, June, null, Report(), Talk(0, 0))]).Calls);
    }

    /// <summary>Goes red when the switch from one engine to another is not marked, or is marked on the wrong call.</summary>
    [Fact]
    public void AnEngineChangeIsMarkedBetweenTheCalls()
    {
        var series = HabitTrend.Build(HabitMetric.Filler,
        [
            Sample(1, June, "large-v3", Report(filler: 1)),
            Sample(2, June.AddDays(5), "large-v3", Report(filler: 2)),
            Sample(3, June.AddDays(9), "nova-3", Report(filler: 3)),
            Sample(4, June.AddDays(12), "nova-3", Report(filler: 4)),
        ]);

        var change = Assert.Single(series.Breaks);
        Assert.Equal(June.AddDays(9), change.At);
        Assert.Equal(("large-v3", "nova-3"), (change.From, change.To));
        Assert.Equal(["large-v3", "large-v3", "nova-3", "nova-3"], series.Calls.Select(p => p.Engine));
    }

    /// <summary>Each metric over its own denominator: per minute, per hundred words, per ten minutes, a share, a count.</summary>
    [Fact]
    public void EachMetricHasItsOwnDenominator()
    {
        var report = Report(profanity: 3, filler: 5, spokenMs: 90_000, words: 250, timedWords: 250, timedMs: 90_000, disclosures: 2);
        var talk = Talk(mine: 90_000, theirs: 210_000, myCuts: 6);   // 5 minutes of conversation

        Assert.Equal(2.0, HabitTrend.Value(HabitMetric.Profanity, report, talk));
        Assert.Equal(2.0, HabitTrend.Value(HabitMetric.Filler, report, talk));
        Assert.Equal(250 / 1.5, HabitTrend.Value(HabitMetric.SpeechRate, report, talk));
        Assert.Equal(0.3, HabitTrend.Value(HabitMetric.TalkShare, report, talk));
        Assert.Equal(12.0, HabitTrend.Value(HabitMetric.Interruptions, report, talk));
        Assert.Equal(2.0, HabitTrend.Value(HabitMetric.Disclosures, report, talk));
    }

    [Fact]
    public void CallsAreOrderedByTimeWhateverOrderTheyArrive()
    {
        var series = HabitTrend.Build(HabitMetric.Disclosures,
        [
            Sample(3, June.AddMonths(2), "x", Report(disclosures: 3)),
            Sample(1, June, "x", Report(disclosures: 1)),
            Sample(2, June.AddMonths(1), "x", Report(disclosures: 2)),
        ]);

        Assert.Equal([1L, 2L, 3L], series.Calls.Select(p => p.CallId));
        Assert.Equal([6, 7, 8], series.Months.Select(m => m.Month));
        Assert.Equal([1.0, 2.0, 3.0], series.Months.Select(m => m.Value));
    }

    /// <summary>A call recorded without headphones cannot be trusted for who said what, and its dot says so.</summary>
    [Fact]
    public void AHollowDotFollowsTheHeadphoneWarning()
    {
        var series = HabitTrend.Build(HabitMetric.TalkShare,
        [
            Sample(1, June, "x", Report(), hollow: true),
            Sample(2, June.AddDays(1), "x", Report()),
        ]);

        Assert.Equal([true, false], series.Calls.Select(p => p.Hollow));
    }
}
