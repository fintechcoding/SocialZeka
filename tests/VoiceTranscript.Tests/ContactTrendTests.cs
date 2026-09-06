using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// "Gidişat": one person's months, and the last three against the three before.
///
/// Everything here is arithmetic over data somebody else fetched, and the tests are about the
/// three ways that arithmetic can lie. A call nobody measured must not be averaged in as a zero.
/// A call whose direction was never recorded must not be counted as outgoing. And the output
/// must stay numbers — the moment a "worse" appears in here, the product is scoring a person.
/// </summary>
public sealed class ContactTrendTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static Repository.ContactCallPoint CallOn(
        long id, int year, int month, int day,
        CallDirection direction = CallDirection.Unknown,
        TalkStats? talk = null) =>
        new(id, new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero), direction, talk);

    /// <summary>Mine, theirs, and how often they cut in — the fields the trend actually reads.</summary>
    private static TalkStats Talk(int mineMs, int theirsMs, int theirCuts) =>
        new(mineMs, theirsMs, 0, theirCuts, 0, 0, null, null, 0);

    private static Repository.SpeechActCounts Questions(
        long call, int year, int month, int day, bool measured,
        int asked = 0, int answered = 0, int partial = 0, int evaded = 0, int deflected = 0) =>
        new(call, new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero),
            measured, asked, answered, partial, evaded, deflected);

    private static Repository.PromiseRow Promise(long call, bool byMe, DateTimeOffset at) =>
        new(new Commitment
        {
            CallId = call,
            ByMe = byMe,
            Quote = "bir söz",
            Obligation = "bir şey",
        }, "Gürhan", at);

    /// <summary>
    /// Each month gets its own row, with the counts that month actually held.
    ///
    /// Red means the months have merged, are out of order, or a month with no calls has been
    /// invented — which would put a zero on the curve where there is simply nothing.
    /// </summary>
    [Fact]
    public void MonthsAreCountedSeparatelyAndInOrder()
    {
        var report = ContactTrend.Build(
            [
                CallOn(1, 2026, 7, 3),
                CallOn(2, 2026, 7, 20),
                CallOn(3, 2026, 9, 1),
            ],
            new Repository.SpeechActSummary([]),
            [],
            AsOf);

        Assert.Equal(2, report.Months.Count);
        Assert.Equal((2026, 7, 2), (report.Months[0].Year, report.Months[0].Month, report.Months[0].Calls));
        Assert.Equal((2026, 9, 1), (report.Months[1].Year, report.Months[1].Month, report.Months[1].Calls));
    }

    /// <summary>
    /// A call whose direction was never recorded stays out of the incoming share entirely.
    ///
    /// Red means the card is about to say "you called them seven times" on the strength of rows
    /// that record nothing at all about who called.
    /// </summary>
    [Fact]
    public void UnknownDirectionIsNotInTheDenominator()
    {
        var report = ContactTrend.Build(
            [
                CallOn(1, 2026, 9, 1, CallDirection.Incoming),
                CallOn(2, 2026, 9, 2, CallDirection.Incoming),
                CallOn(3, 2026, 9, 3, CallDirection.Outgoing),
                CallOn(4, 2026, 9, 4),
                CallOn(5, 2026, 9, 5),
            ],
            new Repository.SpeechActSummary([]),
            [],
            AsOf);

        var month = Assert.Single(report.Months);

        Assert.Equal(5, month.Calls);
        Assert.Equal(2, month.Incoming);
        Assert.Equal(1, month.Outgoing);
        Assert.Equal(2, month.DirectionUnknown);
        Assert.Equal(3, month.DirectionKnown);
        Assert.Equal(2.0 / 3.0, month.IncomingShare!.Value, 6);

        var change = report.Changes.Single(c => c.Metric == ContactMetric.IncomingShare);
        Assert.Equal(2.0 / 3.0, change.Recent!.Value, 6);
        Assert.Equal(3, change.RecentMeasured);
        Assert.Equal(5, change.RecentTotal);
    }

    /// <summary>
    /// Talk share is the mean over the calls that HAVE talk statistics, and says how many those
    /// were.
    ///
    /// Red means a call analysed before Aynam existed is being averaged in as a share of zero,
    /// which would show a user who talks half the time as one who barely speaks.
    /// </summary>
    [Fact]
    public void UnmeasuredCallsAreNotAveragedInAsZero()
    {
        var report = ContactTrend.Build(
            [
                CallOn(1, 2026, 9, 1, talk: Talk(mineMs: 60_000, theirsMs: 40_000, theirCuts: 0)),
                CallOn(2, 2026, 9, 2, talk: Talk(mineMs: 40_000, theirsMs: 60_000, theirCuts: 0)),
                CallOn(3, 2026, 9, 3),
                CallOn(4, 2026, 9, 4),
            ],
            new Repository.SpeechActSummary([]),
            [],
            AsOf);

        var month = Assert.Single(report.Months);

        Assert.Equal(4, month.Calls);
        Assert.Equal(2, month.TalkMeasured);
        Assert.Equal(0.5, month.MeanTalkShare!.Value, 6);

        var change = report.Changes.Single(c => c.Metric == ContactMetric.TalkShare);
        Assert.Equal(0.5, change.Recent!.Value, 6);
        Assert.Equal(2, change.RecentMeasured);
        Assert.Equal(4, change.RecentTotal);
    }

    /// <summary>
    /// Interruptions are pooled over the measured minutes, not averaged per call.
    ///
    /// Red means the shortest call in a month sets the month's figure: one interruption in two
    /// minutes is five per ten minutes, and averaging that beside a quiet hour would report a
    /// rate nobody experienced.
    /// </summary>
    [Fact]
    public void InterruptionsArePooledOverTheMeasuredMinutes()
    {
        var report = ContactTrend.Build(
            [
                // Two minutes of conversation with one interruption.
                CallOn(1, 2026, 9, 1, talk: Talk(mineMs: 60_000, theirsMs: 60_000, theirCuts: 1)),

                // Eighteen minutes with three.
                CallOn(2, 2026, 9, 2, talk: Talk(mineMs: 600_000, theirsMs: 480_000, theirCuts: 3)),
            ],
            new Repository.SpeechActSummary([]),
            [],
            AsOf);

        var month = Assert.Single(report.Months);

        // Four interruptions over twenty minutes: two per ten.
        Assert.Equal(2.0, month.TheirInterruptionsPer10Min!.Value, 6);
    }

    /// <summary>
    /// The unanswered-question rate carries its own N of M, which is not the talk statistics' N.
    ///
    /// Red means a rate is being computed over whichever calls happen to have question rows while
    /// the card claims it speaks for the whole history — the exact reason speech_act records
    /// every call rather than only the ones with questions in them.
    /// </summary>
    [Fact]
    public void TheQuestionRateHasItsOwnDenominator()
    {
        var calls = new[]
        {
            CallOn(1, 2026, 9, 1, talk: Talk(60_000, 60_000, 0)),
            CallOn(2, 2026, 9, 2, talk: Talk(60_000, 60_000, 0)),
            CallOn(3, 2026, 9, 3, talk: Talk(60_000, 60_000, 0)),
        };

        var questions = new Repository.SpeechActSummary(
        [
            Questions(1, 2026, 9, 1, measured: true, asked: 4, answered: 2, evaded: 1, deflected: 1),
            Questions(2, 2026, 9, 2, measured: false),
            Questions(3, 2026, 9, 3, measured: false),
        ]);

        var report = ContactTrend.Build(calls, questions, [], AsOf);

        var month = Assert.Single(report.Months);
        Assert.Equal(1, month.QuestionsMeasured);
        Assert.Equal(4, month.QuestionsAsked);
        Assert.Equal(2, month.QuestionsUnanswered);
        Assert.Equal(0.5, month.UnansweredRate!.Value, 6);

        // Three calls have talk statistics; one has questions. The two denominators differ, and
        // the report says so rather than reusing one for both.
        Assert.Equal(3, month.TalkMeasured);

        var change = report.Changes.Single(c => c.Metric == ContactMetric.UnansweredQuestions);
        Assert.Equal(1, change.RecentMeasured);
        Assert.Equal(3, change.RecentTotal);
    }

    /// <summary>
    /// The two windows are three whole months each, counted back from the day passed in.
    ///
    /// Red means the comparison has started reading the clock, or has slid by a day — either of
    /// which makes the same archive show a different history depending on when it was opened.
    /// </summary>
    [Fact]
    public void RecentIsTheLastThreeMonthsAndPreviousIsTheThreeBefore()
    {
        var report = ContactTrend.Build(
            [
                // Recent window: July, August, September 2026.
                CallOn(1, 2026, 7, 1),
                CallOn(2, 2026, 8, 1),
                CallOn(3, 2026, 9, 1),
                CallOn(4, 2026, 9, 4),

                // Previous window: April, May, June.
                CallOn(5, 2026, 4, 30),
                CallOn(6, 2026, 6, 15),

                // Older than both, and counted in neither.
                CallOn(7, 2026, 3, 31),
            ],
            new Repository.SpeechActSummary([]),
            [],
            AsOf);

        var change = report.Changes.Single(c => c.Metric == ContactMetric.Calls);

        Assert.Equal(4.0, change.Recent);
        Assert.Equal(2.0, change.Previous);
        Assert.Equal(4, change.RecentTotal);
        Assert.Equal(2, change.PreviousTotal);
    }

    /// <summary>
    /// Promises are counted in the month they were made in, on the other party's side only.
    ///
    /// Red means the user's own promises are being counted against the person opposite, or a
    /// promise the user threw out is back.
    /// </summary>
    [Fact]
    public void OnlyTheOtherPartysUndismissedPromisesAreCounted()
    {
        var september = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
        var may = new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero);

        var report = ContactTrend.Build(
            [CallOn(1, 2026, 9, 2), CallOn(2, 2026, 5, 2)],
            new Repository.SpeechActSummary([]),
            [
                Promise(1, byMe: false, september),
                Promise(1, byMe: false, september),
                Promise(1, byMe: true, september),
                Promise(2, byMe: false, may),
            ],
            AsOf);

        Assert.Equal(2, report.Months.Single(m => m.Month == 9).TheirPromises);
        Assert.Equal(1, report.Months.Single(m => m.Month == 5).TheirPromises);

        var change = report.Changes.Single(c => c.Metric == ContactMetric.TheirPromises);
        Assert.Equal(2.0, change.Recent);
        Assert.Equal(1.0, change.Previous);
    }

    /// <summary>
    /// A window with nothing measured in it produces null, not zero.
    ///
    /// Red means "not measured" has become "measured, and the answer was none" — which is the
    /// difference between an honest blank on the card and a claim about a person.
    /// </summary>
    [Fact]
    public void AWindowWithNothingMeasuredIsNullRatherThanZero()
    {
        var report = ContactTrend.Build(
            [CallOn(1, 2026, 9, 1), CallOn(2, 2026, 9, 2)],
            new Repository.SpeechActSummary([Questions(1, 2026, 9, 1, measured: false)]),
            [],
            AsOf);

        var month = Assert.Single(report.Months);
        Assert.Null(month.MeanTalkShare);
        Assert.Null(month.TheirInterruptionsPer10Min);
        Assert.Null(month.UnansweredRate);
        Assert.Null(month.IncomingShare);

        foreach (var metric in new[]
                 {
                     ContactMetric.TalkShare,
                     ContactMetric.TheirInterruptions,
                     ContactMetric.UnansweredQuestions,
                     ContactMetric.IncomingShare,
                 })
        {
            var change = report.Changes.Single(c => c.Metric == metric);
            Assert.Null(change.Recent);
            Assert.Null(change.Previous);
        }
    }

    /// <summary>
    /// Every metric gets a pair, and no metric is missing from the report.
    ///
    /// Red means a figure was added to the enum and forgotten in the builder, which shows on the
    /// card as a row that is silently always blank.
    /// </summary>
    [Fact]
    public void EveryMetricGetsExactlyOnePair()
    {
        var report = ContactTrend.Build([CallOn(1, 2026, 9, 1)], new Repository.SpeechActSummary([]), [], AsOf);

        foreach (var metric in Enum.GetValues<ContactMetric>())
            Assert.Single(report.Changes, c => c.Metric == metric);
    }

    /// <summary>An archive with no calls for this person is an empty report, not a crash.</summary>
    [Fact]
    public void NoCallsIsAnEmptyReport()
    {
        var report = ContactTrend.Build([], new Repository.SpeechActSummary([]), [], AsOf);

        Assert.Empty(report.Months);
        Assert.All(report.Changes, c => Assert.Equal(0, c.RecentTotal));
    }
}
