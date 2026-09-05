using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Analysis;

/// <summary>
/// Who did the talking, as facts with the milliseconds behind them.
///
/// Lifted from <c>CallWindowViewModel.ComputeTalkStats</c> (and its copy in
/// <c>ContactsViewModel</c>), where the rule lived twice and could not be tested or stored. The
/// rule itself is unchanged: speaking time is summed per stream, and an interruption is counted
/// when one side starts while the other is still going. Both are stated as counts rather than
/// as a verdict — "sen %62 konuştun" is a fact somebody can check, whereas "karşı taraf seni
/// sürekli böldü" is a judgement this application has no business making.
///
/// Two additions the view models did not have. Lines the capture layer marked as a suspected
/// echo of the other stream are left out entirely: they are the far end heard through the
/// user's microphone, and counting them puts the other party's seconds on the user's side.
/// Lines that merely overlap the other speaker are kept, exactly as the view models kept them —
/// an overlap is not a defect in the line, it is the interruption being counted.
/// </summary>
/// <param name="MineMs">The user's speaking time.</param>
/// <param name="TheirsMs">The other party's.</param>
/// <param name="MyInterruptions">Times the user started while the other party was still talking.</param>
/// <param name="TheirInterruptions">Times the other party did.</param>
/// <param name="MyQuestions">Lines of the user's that end in a question mark.</param>
/// <param name="TheirQuestions">Lines of the other party's that do.</param>
/// <param name="MyMedianAnswerDelayMs">
/// The median gap between the end of one of their questions and the start of the user's next
/// line, or null when no question of theirs was followed by a line of the user's.
/// </param>
/// <param name="TheirMedianAnswerDelayMs">The same the other way round.</param>
/// <param name="EchoLinesExcluded">How many lines were left out as suspected echo, so the screen can say so.</param>
public sealed record TalkStats(
    int MineMs,
    int TheirsMs,
    int MyInterruptions,
    int TheirInterruptions,
    int MyQuestions,
    int TheirQuestions,
    int? MyMedianAnswerDelayMs,
    int? TheirMedianAnswerDelayMs,
    int EchoLinesExcluded)
{
    public int TotalMs => MineMs + TheirsMs;

    /// <summary>The user's share of the speaking time, 0..1, or null when nobody spoke.</summary>
    public double? MyShare => TotalMs > 0 ? (double)MineMs / TotalMs : null;

    public static readonly TalkStats Empty = new(0, 0, 0, 0, 0, 0, null, null, 0);

    /// <summary>The figures for one conversation's lines. Pure; order of input does not matter.</summary>
    public static TalkStats Compute(IReadOnlyList<Segment> segments)
    {
        if (segments.Count == 0) return Empty;

        var echo = segments.Count(s => s.SuspectedEcho);
        var kept = segments.Where(s => !s.SuspectedEcho).OrderBy(s => s.StartMs).ToList();
        if (kept.Count == 0) return Empty with { EchoLinesExcluded = echo };

        var mine = 0;
        var theirs = 0;

        foreach (var segment in kept)
        {
            var length = Math.Max(0, segment.EndMs - segment.StartMs);
            if (segment.IsMe) mine += length; else theirs += length;
        }

        // The interruption rule, verbatim from the view models: a change of speaker whose line
        // begins before the previous line ended.
        var myCuts = 0;
        var theirCuts = 0;

        for (var i = 1; i < kept.Count; i++)
        {
            var previous = kept[i - 1];
            var current = kept[i];

            if (current.IsMe == previous.IsMe || current.StartMs >= previous.EndMs) continue;

            if (current.IsMe) myCuts++; else theirCuts++;
        }

        var myQuestions = kept.Count(s => s.IsMe && IsQuestion(s.Text));
        var theirQuestions = kept.Count(s => !s.IsMe && IsQuestion(s.Text));

        return new TalkStats(
            mine, theirs,
            myCuts, theirCuts,
            myQuestions, theirQuestions,
            MedianAnswerDelay(kept, answeredByMe: true),
            MedianAnswerDelay(kept, answeredByMe: false),
            echo);
    }

    /// <summary>A line is a question when it ends in one. The transcribers punctuate; nothing subtler is attempted.</summary>
    public static bool IsQuestion(string text) => text.TrimEnd().EndsWith('?');

    /// <summary>
    /// For every question one side asked, how long until the other side's next line began —
    /// clamped at zero, because an answer that starts over the end of the question is an answer
    /// with no delay, not a negative one.
    /// </summary>
    private static int? MedianAnswerDelay(List<Segment> ordered, bool answeredByMe)
    {
        List<int> delays = [];

        for (var i = 0; i < ordered.Count; i++)
        {
            var question = ordered[i];
            if (question.IsMe == answeredByMe || !IsQuestion(question.Text)) continue;

            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (ordered[j].IsMe != answeredByMe) continue;

                delays.Add(Math.Max(0, ordered[j].StartMs - question.EndMs));
                break;
            }
        }

        if (delays.Count == 0) return null;

        delays.Sort();
        var middle = delays.Count / 2;

        return delays.Count % 2 == 1
            ? delays[middle]
            : (delays[middle - 1] + delays[middle]) / 2;
    }
}
