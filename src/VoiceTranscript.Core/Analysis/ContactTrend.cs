using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Analysis;

/// <summary>The figures the "Gidişat" strip draws for one person. Counts and rates, never words.</summary>
public enum ContactMetric
{
    /// <summary>Conversations in the period. A count.</summary>
    Calls,

    /// <summary>Share of the calls that came in, over the calls whose direction is known.</summary>
    IncomingShare,

    /// <summary>The user's own share of the speaking time, 0..1, averaged over measured calls.</summary>
    TalkShare,

    /// <summary>Times the other party cut in, per ten minutes of conversation.</summary>
    TheirInterruptions,

    /// <summary>Share of the questions the user asked that got no real answer.</summary>
    UnansweredQuestions,

    /// <summary>Promises the other party made in the period. A count.</summary>
    TheirPromises,
}

/// <summary>
/// One month of a person's history.
///
/// Every figure that can be missing carries its own denominator beside it, because the
/// denominators genuinely differ: a call may have talk statistics and no questions, questions
/// and no talk statistics, or neither. A month is not "N calls" for all of them.
/// </summary>
/// <param name="Calls">Conversations that began in this month.</param>
/// <param name="Incoming">Of those, the ones the other party started.</param>
/// <param name="Outgoing">The ones the user started.</param>
/// <param name="DirectionUnknown">
/// Calls whose direction was never recorded. NOT part of the incoming share's denominator —
/// counting them as outgoing would say the user made calls they may not have made.
/// </param>
/// <param name="TalkMeasured">Calls with stored talk statistics. The N of "N/M görüşmede ölçüldü".</param>
/// <param name="MeanTalkShare">The mean of those calls' shares, or null when none were measured.</param>
/// <param name="TheirInterruptionsPer10Min">
/// Pooled, not averaged: the month's interruptions over the month's conversation minutes. A
/// two-minute call with one interruption is five per ten minutes, and averaging that in beside
/// an hour-long call would let the shortest call in a month set its figure.
/// </param>
/// <param name="QuestionsMeasured">Calls with question rows at all — its own N, and usually not TalkMeasured.</param>
/// <param name="QuestionsAsked">Questions the user put to this person in the month.</param>
/// <param name="QuestionsUnanswered">Of those, the ones evaded or deflected.</param>
public sealed record ContactMonth(
    int Year,
    int Month,
    int Calls,
    int Incoming,
    int Outgoing,
    int DirectionUnknown,
    int TalkMeasured,
    double? MeanTalkShare,
    double? TheirInterruptionsPer10Min,
    int QuestionsMeasured,
    int QuestionsAsked,
    int QuestionsUnanswered,
    int TheirPromises)
{
    /// <summary>Calls whose direction is known — the incoming share's denominator.</summary>
    public int DirectionKnown => Incoming + Outgoing;

    /// <summary>0..1, or null when no call in the month recorded which way it went.</summary>
    public double? IncomingShare => DirectionKnown > 0 ? (double)Incoming / DirectionKnown : null;

    /// <summary>0..1, or null when no question was recorded in the month.</summary>
    public double? UnansweredRate =>
        QuestionsAsked > 0 ? (double)QuestionsUnanswered / QuestionsAsked : null;
}

/// <summary>
/// One metric over the last three months against the three before it.
///
/// Two numbers and their denominators, and nothing else. No difference, no direction, no word
/// for whether it got better: which way is better is the user's to decide, and a product that
/// answered it would be scoring a person.
/// </summary>
/// <param name="Recent">The figure over the recent window, or null when nothing measured it.</param>
/// <param name="Previous">The same over the window before it.</param>
/// <param name="RecentMeasured">How many calls fed <paramref name="Recent"/>.</param>
/// <param name="RecentTotal">How many calls the window held, measured or not.</param>
public sealed record ContactChange(
    ContactMetric Metric,
    double? Recent,
    double? Previous,
    int RecentMeasured,
    int RecentTotal,
    int PreviousMeasured,
    int PreviousTotal);

/// <summary>The months and the recent-versus-previous pairs the card draws.</summary>
public sealed record ContactTrendReport(
    IReadOnlyList<ContactMonth> Months,
    IReadOnlyList<ContactChange> Changes);

/// <summary>
/// Turns one person's calls, questions and promises into the months and the two-window
/// comparison the contact card shows.
///
/// Pure: everything it needs is passed in, including the day to count back from, so the same
/// archive produces the same picture whenever it is opened and a test does not have to wait for
/// a month to turn over.
///
/// Three rules run through all of it. A call that was never measured is NOT a zero — it is
/// absent from that figure and present in its denominator, which is why every rate here comes
/// with the count that produced it. Unknown call direction stays out of the direction figures
/// entirely rather than being guessed to one side. And nothing here produces a word: the output
/// is counts, shares and rates, and the screen puts "4/ay → 2/ay" next to them without saying
/// which of the two is the better month.
///
/// There is deliberately no "findings per month" series. It would measure which checks had been
/// run on which calls — the ledger grew new checks over the years — and read as a change in the
/// person.
/// </summary>
public static class ContactTrend
{
    /// <summary>How many months each of the two compared windows holds.</summary>
    public const int WindowMonths = 3;

    /// <summary>
    /// The whole report for one contact.
    /// </summary>
    /// <param name="calls">Every call with this person, from <see cref="Repository.ContactSeries"/>.</param>
    /// <param name="questions">The per-call question counts, from <see cref="Repository.SpeechActs"/>.</param>
    /// <param name="promises">This person's promise rows, from <see cref="Repository.PromiseLedger"/>.</param>
    /// <param name="asOf">The day the windows are counted back from. Passed, never read from the clock.</param>
    public static ContactTrendReport Build(
        IReadOnlyList<Repository.ContactCallPoint> calls,
        Repository.SpeechActSummary questions,
        IReadOnlyList<Repository.PromiseRow> promises,
        DateTimeOffset asOf)
    {
        var byCall = questions.Calls.ToDictionary(c => c.CallId);

        // Promises are counted in the month of the conversation they were made in, not the month
        // they came due: the series is about what happened between these two people, and a date
        // somebody named is not an event.
        var promisesByMonth = promises
            .Where(p => !p.Commitment.ByMe && !p.Commitment.DismissedByUser)
            .GroupBy(p => (p.CallStartedAt.Year, p.CallStartedAt.Month))
            .ToDictionary(g => g.Key, g => g.Count());

        List<ContactMonth> months = [];

        foreach (var group in calls
                     .GroupBy(c => (c.StartedAt.Year, c.StartedAt.Month))
                     .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month))
        {
            var window = group.ToList();

            months.Add(new ContactMonth(
                group.Key.Year,
                group.Key.Month,
                window.Count,
                window.Count(c => c.Direction == CallDirection.Incoming),
                window.Count(c => c.Direction == CallDirection.Outgoing),
                window.Count(c => c.Direction == CallDirection.Unknown),
                TalkMeasured(window),
                MeanTalkShare(window),
                InterruptionsPer10Min(window),
                QuestionsMeasured(window, byCall),
                Asked(window, byCall),
                Unanswered(window, byCall),
                promisesByMonth.GetValueOrDefault(group.Key)));
        }

        var recentFrom = FirstOfMonth(asOf).AddMonths(-(WindowMonths - 1));
        var previousFrom = recentFrom.AddMonths(-WindowMonths);

        var recent = calls.Where(c => c.StartedAt >= recentFrom).ToList();
        var previous = calls.Where(c => c.StartedAt >= previousFrom && c.StartedAt < recentFrom).ToList();

        List<ContactChange> changes =
        [
            Change(ContactMetric.Calls, recent, previous, byCall, promises, recentFrom, previousFrom),
            Change(ContactMetric.IncomingShare, recent, previous, byCall, promises, recentFrom, previousFrom),
            Change(ContactMetric.TalkShare, recent, previous, byCall, promises, recentFrom, previousFrom),
            Change(ContactMetric.TheirInterruptions, recent, previous, byCall, promises, recentFrom, previousFrom),
            Change(ContactMetric.UnansweredQuestions, recent, previous, byCall, promises, recentFrom, previousFrom),
            Change(ContactMetric.TheirPromises, recent, previous, byCall, promises, recentFrom, previousFrom),
        ];

        return new ContactTrendReport(months, changes);
    }

    /// <summary>Midnight on the first of the month <paramref name="at"/> falls in, at its own offset.</summary>
    private static DateTimeOffset FirstOfMonth(DateTimeOffset at) =>
        new(at.Year, at.Month, 1, 0, 0, 0, at.Offset);

    private static ContactChange Change(
        ContactMetric metric,
        IReadOnlyList<Repository.ContactCallPoint> recent,
        IReadOnlyList<Repository.ContactCallPoint> previous,
        IReadOnlyDictionary<long, Repository.SpeechActCounts> byCall,
        IReadOnlyList<Repository.PromiseRow> promises,
        DateTimeOffset recentFrom,
        DateTimeOffset previousFrom)
    {
        var (recentValue, recentMeasured) = Window(metric, recent, byCall, promises, recentFrom, null);
        var (previousValue, previousMeasured) =
            Window(metric, previous, byCall, promises, previousFrom, recentFrom);

        return new ContactChange(
            metric, recentValue, previousValue,
            recentMeasured, recent.Count, previousMeasured, previous.Count);
    }

    /// <summary>One metric over one window: the figure, and how many calls actually fed it.</summary>
    private static (double? Value, int Measured) Window(
        ContactMetric metric,
        IReadOnlyList<Repository.ContactCallPoint> calls,
        IReadOnlyDictionary<long, Repository.SpeechActCounts> byCall,
        IReadOnlyList<Repository.PromiseRow> promises,
        DateTimeOffset from,
        DateTimeOffset? until)
    {
        switch (metric)
        {
            case ContactMetric.Calls:
                return (calls.Count, calls.Count);

            case ContactMetric.IncomingShare:
            {
                var known = calls.Count(c => c.Direction != CallDirection.Unknown);
                var incoming = calls.Count(c => c.Direction == CallDirection.Incoming);
                return (known > 0 ? (double)incoming / known : null, known);
            }

            case ContactMetric.TalkShare:
                return (MeanTalkShare(calls), TalkMeasured(calls));

            case ContactMetric.TheirInterruptions:
                return (InterruptionsPer10Min(calls), TalkMeasured(calls));

            case ContactMetric.UnansweredQuestions:
            {
                var asked = Asked(calls, byCall);
                var unanswered = Unanswered(calls, byCall);
                return (asked > 0 ? (double)unanswered / asked : null, QuestionsMeasured(calls, byCall));
            }

            case ContactMetric.TheirPromises:
            {
                var made = promises.Count(p =>
                    !p.Commitment.ByMe
                    && !p.Commitment.DismissedByUser
                    && p.CallStartedAt >= from
                    && (until is not { } end || p.CallStartedAt < end));

                return (made, calls.Count);
            }

            default:
                return (null, 0);
        }
    }

    private static int TalkMeasured(IEnumerable<Repository.ContactCallPoint> calls) =>
        calls.Count(c => c.Talk is not null);

    /// <summary>
    /// The mean of the measured calls' talk shares — one call, one vote.
    ///
    /// A call where nobody spoke has no share at all and is left out of both the mean and its
    /// count; it is a recording, not a conversation with a balance.
    /// </summary>
    private static double? MeanTalkShare(IEnumerable<Repository.ContactCallPoint> calls)
    {
        var shares = calls
            .Select(c => c.Talk?.MyShare)
            .OfType<double>()
            .ToList();

        return shares.Count == 0 ? null : shares.Average();
    }

    /// <summary>Their interruptions over the measured conversation time, pooled, per ten minutes.</summary>
    private static double? InterruptionsPer10Min(IEnumerable<Repository.ContactCallPoint> calls)
    {
        var measured = calls.Select(c => c.Talk).OfType<TalkStats>().ToList();

        var totalMs = measured.Sum(t => (long)t.TotalMs);
        if (totalMs == 0) return null;

        return measured.Sum(t => t.TheirInterruptions) / (totalMs / 600_000.0);
    }

    private static int QuestionsMeasured(
        IEnumerable<Repository.ContactCallPoint> calls,
        IReadOnlyDictionary<long, Repository.SpeechActCounts> byCall) =>
        calls.Count(c => byCall.TryGetValue(c.CallId, out var counts) && counts.Measured);

    private static int Asked(
        IEnumerable<Repository.ContactCallPoint> calls,
        IReadOnlyDictionary<long, Repository.SpeechActCounts> byCall) =>
        calls.Sum(c => byCall.TryGetValue(c.CallId, out var counts) ? counts.Asked : 0);

    private static int Unanswered(
        IEnumerable<Repository.ContactCallPoint> calls,
        IReadOnlyDictionary<long, Repository.SpeechActCounts> byCall) =>
        calls.Sum(c => byCall.TryGetValue(c.CallId, out var counts) ? counts.Unanswered : 0);
}
