using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Who did the talking, lifted out of two view models into one tested function.
///
/// The rule is the view models' rule and these tests say so with a copy of it: the figures
/// must be what the call window and the contact card have always shown, or the strip changes
/// under the user for no reason they were told. The two things the view models never did —
/// leaving out suspected echo, counting questions and the delay before an answer — are pinned
/// separately.
/// </summary>
public sealed class TalkStatsTests
{
    private static Segment Line(bool me, int start, int end, string text = "…", bool echo = false, bool overlaps = false) => new()
    {
        CallId = 1, IsMe = me, StartMs = start, EndMs = end, Text = text, SuspectedEcho = echo, OverlapsOtherSpeaker = overlaps,
    };

    [Fact]
    public void SpeakingTimeAndShareFollowTheStreams()
    {
        var stats = TalkStats.Compute([Line(true, 0, 10_000), Line(false, 10_000, 30_000)]);

        Assert.Equal(10_000, stats.MineMs);
        Assert.Equal(20_000, stats.TheirsMs);
        Assert.Equal(30_000, stats.TotalMs);
        Assert.Equal(1.0 / 3, stats.MyShare!.Value, 9);
    }

    /// <summary>An interruption is a change of speaker whose line begins before the previous line ended — and only that.</summary>
    [Fact]
    public void AnInterruptionIsAChangeOfSpeakerBeforeThePreviousLineEnded()
    {
        var stats = TalkStats.Compute(
        [
            Line(false, 0, 5000),
            Line(true, 4000, 8000),      // I cut in
            Line(false, 7000, 9000),     // they cut in
            Line(true, 9000, 10_000),    // after they finished: not a cut
            Line(true, 9500, 11_000),    // over myself: not a cut
        ]);

        Assert.Equal(1, stats.MyInterruptions);
        Assert.Equal(1, stats.TheirInterruptions);
    }

    /// <summary>
    /// Echo is the far end heard through the microphone. Goes red when it adds to the user's
    /// seconds or, worse, makes the user interrupt themselves with the other party's words.
    /// </summary>
    [Fact]
    public void ASuspectedEchoLineIsLeftOutEntirely()
    {
        var stats = TalkStats.Compute(
        [
            Line(false, 0, 5000),
            Line(true, 1000, 4000, echo: true),
            Line(true, 5000, 6000),
        ]);

        Assert.Equal(1000, stats.MineMs);
        Assert.Equal(0, stats.MyInterruptions);
        Assert.Equal(1, stats.EchoLinesExcluded);
    }

    /// <summary>An overlapping line is not a defective line, it is the interruption being counted; the view models kept it and so does this.</summary>
    [Fact]
    public void AnOverlappingLineIsKeptAsTheViewModelsKeptIt()
    {
        var stats = TalkStats.Compute([Line(false, 0, 5000), Line(true, 4000, 8000, overlaps: true)]);

        Assert.Equal(4000, stats.MineMs);
        Assert.Equal(1, stats.MyInterruptions);
    }

    [Fact]
    public void QuestionsAreLinesEndingInAQuestionMark()
    {
        var stats = TalkStats.Compute(
        [
            Line(true, 0, 1000, "Ne zaman?"),
            Line(false, 1000, 2000, "Cuma."),
            Line(false, 2000, 3000, "Sen ne diyorsun? "),
            Line(true, 3000, 4000, "Bilmem ki"),
            Line(false, 4000, 5000, "Olur mu?"),
        ]);

        Assert.Equal(1, stats.MyQuestions);
        Assert.Equal(2, stats.TheirQuestions);
    }

    /// <summary>
    /// The delay is the gap from the end of one side's question to the start of the other side's
    /// next line, the median of them, and never negative — an answer that starts over the end of
    /// the question is an answer with no delay.
    /// </summary>
    [Fact]
    public void TheAnswerDelayIsTheMedianGapToTheOtherSidesNextLine()
    {
        var stats = TalkStats.Compute(
        [
            Line(false, 0, 5000, "Ne zaman?"),
            Line(true, 7000, 8000, "Cuma"),               // 2000 after
            Line(false, 10_000, 12_000, "Kaça?"),
            Line(false, 12_000, 13_000, "Yani ne kadar?"), // they kept talking: still my next line
            Line(true, 13_500, 14_000, "On"),             // 1500 after the first, 500 after the second
            Line(false, 20_000, 22_000, "Emin misin?"),
            Line(true, 21_000, 23_000, "Evet"),           // over the end: 0
            Line(true, 30_000, 31_000, "Sen?"),
            Line(false, 31_000, 32_000, "Ben de"),         // theirs: 0
        ]);

        // Mine: 2000, 1500, 500, 0 → median (500 + 1500) / 2.
        Assert.Equal(1000, stats.MyMedianAnswerDelayMs);
        Assert.Equal(0, stats.TheirMedianAnswerDelayMs);
    }

    [Fact]
    public void NoQuestionsMeansNoDelay()
    {
        var stats = TalkStats.Compute([Line(true, 0, 1000, "Tamam"), Line(false, 1000, 2000, "Olur")]);

        Assert.Null(stats.MyMedianAnswerDelayMs);
        Assert.Null(stats.TheirMedianAnswerDelayMs);
    }

    [Fact]
    public void EmptyInputGivesTheEmptyFigures()
    {
        var stats = TalkStats.Compute([]);

        Assert.Equal(TalkStats.Empty, stats);
        Assert.Null(stats.MyShare);
        Assert.Equal(0, stats.TotalMs);
    }

    /// <summary>
    /// The view models' rule, copied here as it stood in CallWindowViewModel.ComputeTalkStats,
    /// run against the same lines. Goes red when the lifted function drifts from what the
    /// screens have shown all along.
    /// </summary>
    [Fact]
    public void TheFiguresMatchTheViewModelsRule()
    {
        var random = new Random(42);
        List<Segment> lines = [];
        var clock = 0;

        for (var i = 0; i < 60; i++)
        {
            var start = clock + random.Next(-1500, 3000);
            var end = start + random.Next(200, 6000);
            lines.Add(Line(random.Next(2) == 0, Math.Max(0, start), end));
            clock = end;
        }

        var expected = ViewModelRule(lines);
        var stats = TalkStats.Compute(lines);

        Assert.Equal(expected.mine, stats.MineMs);
        Assert.Equal(expected.theirs, stats.TheirsMs);
        Assert.Equal(expected.myCuts, stats.MyInterruptions);
        Assert.Equal(expected.theirCuts, stats.TheirInterruptions);
    }

    /// <summary>Verbatim from CallWindowViewModel.ComputeTalkStats, with the TimeSpans as milliseconds.</summary>
    private static (int mine, int theirs, int myCuts, int theirCuts) ViewModelRule(IReadOnlyList<Segment> segments)
    {
        var mine = 0;
        var theirs = 0;

        foreach (var segment in segments)
        {
            var length = Math.Max(0, segment.EndMs - segment.StartMs);
            if (segment.IsMe) mine += length; else theirs += length;
        }

        var ordered = segments.OrderBy(s => s.StartMs).ToList();
        var myCuts = 0;
        var theirCuts = 0;

        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];

            if (current.IsMe == previous.IsMe || current.StartMs >= previous.EndMs) continue;

            if (current.IsMe) myCuts++; else theirCuts++;
        }

        return (mine, theirs, myCuts, theirCuts);
    }
}
