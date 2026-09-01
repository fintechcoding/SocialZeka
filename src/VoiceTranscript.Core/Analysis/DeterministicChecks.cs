using System.Globalization;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Analysis;

/// <summary>
/// The cross-checks that produce the ledger, computed in code rather than asked of a model.
///
/// This is where the real value of the product lives, and it is deliberately boring: date
/// arithmetic, decimal comparison, and joins on entity and attribute. A price that changed from
/// 12.000 to 18.000 across two calls is not an inference, it is a subtraction. A deadline that
/// passed is a comparison. Because these are computed, they are exact, they are explainable, and
/// they cannot hallucinate.
///
/// The model's only job upstream was to find and quote. Judgement stays here, in code the user
/// could check by hand if they wanted to.
/// </summary>
public static class DeterministicChecks
{
    /// <summary>
    /// Commitments whose deadline has passed with nothing recorded as fulfilling them.
    ///
    /// Conditional promises are excluded. "Parayı yollarsan cuma günü gönderirim" is not broken
    /// by Friday arriving; treating it as broken would generate exactly the kind of false
    /// accusation that makes a user stop trusting the ledger.
    /// </summary>
    public static IEnumerable<Flag> OverdueCommitments(
        IEnumerable<Commitment> commitments,
        DateOnly today)
    {
        foreach (var commitment in commitments)
        {
            if (commitment.DismissedByUser || commitment.IsConditional) continue;
            if (!commitment.IsOverdue(today)) continue;

            var daysLate = today.DayNumber - commitment.DeadlineDate!.Value.DayNumber;

            // Who owes: "the date passed" is a different sentence when the promise was the
            // user's own — and hiding that turned their forgotten obligations into someone
            // else's fault by omission.
            var who = commitment.ByMe ? "sen" : "karşı taraf";

            yield return new Flag
            {
                CallId = commitment.CallId,
                ContactId = commitment.ContactId,
                Kind = FlagKind.OverdueCommitment,
                Summary = $"Söz verilen tarih {daysLate} gün geçti ({who}): {commitment.Obligation}",
                Quote = commitment.Quote,
                QuoteStartMs = commitment.QuoteStartMs,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>
    /// The same obligation given a later date each time it came up.
    ///
    /// Counted rather than judged: the flag says the date moved three times and totals the slip.
    /// Whether that means someone is stalling is for the user to decide after listening.
    /// </summary>
    public static IEnumerable<Flag> MovedDeadlines(IReadOnlyList<Commitment> history)
    {
        foreach (var group in history
                     .Where(c => c is { DismissedByUser: false, DeadlineDate: not null })
                     .GroupBy(c => Normalise(c.Obligation)))
        {
            var ordered = group.OrderBy(c => c.CallId).ToList();
            if (ordered.Count < 2) continue;

            var moves = 0;
            var totalSlip = 0;

            for (var i = 1; i < ordered.Count; i++)
            {
                var slip = ordered[i].DeadlineDate!.Value.DayNumber - ordered[i - 1].DeadlineDate!.Value.DayNumber;
                if (slip <= 0) continue;

                moves++;
                totalSlip += slip;
            }

            if (moves == 0) continue;

            var latest = ordered[^1];
            var first = ordered[0];

            yield return new Flag
            {
                CallId = latest.CallId,
                ContactId = latest.ContactId,
                Kind = FlagKind.MovedDeadline,
                Summary = $"Teslim tarihi {moves} kez ileri alındı, toplam {totalSlip} gün: {latest.Obligation}",
                Quote = latest.Quote,
                QuoteStartMs = latest.QuoteStartMs,
                CounterQuote = first.Quote,
                CounterCallId = first.CallId,
                CounterQuoteStartMs = first.QuoteStartMs,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>
    /// The same thing quoted at different amounts across calls.
    ///
    /// Low-confidence claims are skipped entirely. A misheard "on sekiz bin" as "on sekiz yüz"
    /// would otherwise become a fabricated price conflict attributed to a real person, which is
    /// precisely the failure mode that would make the whole ledger untrustworthy.
    /// </summary>
    public static IEnumerable<Flag> ChangedAmounts(IReadOnlyList<Claim> claims, decimal minRelativeChange = 0.02m)
    {
        foreach (var group in claims
                     .Where(c => c is { NumericValue: not null, LowConfidence: false, ByMe: false })
                     .GroupBy(c => (Normalise(c.Entity), Normalise(c.Attribute))))
        {
            var ordered = group.OrderBy(c => c.CallId).ThenBy(c => c.QuoteStartMs).ToList();
            if (ordered.Count < 2) continue;

            var first = ordered[0];
            var last = ordered[^1];

            var from = first.NumericValue!.Value;
            var to = last.NumericValue!.Value;
            if (from == to) continue;

            // Ignore rounding noise so that 12.000 versus 12.000,50 is not reported as a change.
            if (from != 0 && Math.Abs((to - from) / from) < minRelativeChange) continue;

            var distinct = ordered.Select(c => c.NumericValue!.Value).Distinct().Count();
            var direction = to > from ? "arttı" : "düştü";
            var percent = from == 0 ? 0 : Math.Round((to - from) / from * 100, 1);

            yield return new Flag
            {
                CallId = last.CallId,
                ContactId = last.ContactId,
                Kind = FlagKind.ChangedAmount,
                Summary =
                    $"{first.Entity} {first.Attribute}: {Format(from)} → {Format(to)} " +
                    $"({direction}, %{Math.Abs(percent)}), {distinct} farklı değer",
                Quote = last.Quote,
                QuoteStartMs = last.QuoteStartMs,
                CounterQuote = first.Quote,
                CounterCallId = first.CallId,
                CounterQuoteStartMs = first.QuoteStartMs,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>
    /// Pairs of statements about the same thing that disagree, offered as candidates only.
    ///
    /// This is the generation step, not the verdict. A pair surfaced here is passed to the model
    /// for a narrow, bounded judgement — contradiction, refinement, different subject, or no
    /// conflict — because "the value changed" and "the person contradicted themselves" are not
    /// the same claim. Prices legitimately change; what matters is whether the earlier statement
    /// was presented as settled.
    /// </summary>
    public static IEnumerable<(Claim earlier, Claim later)> ContradictionCandidates(IReadOnlyList<Claim> claims)
    {
        foreach (var group in claims
                     .Where(c => c is { LowConfidence: false, ByMe: false })
                     .GroupBy(c => (Normalise(c.Entity), Normalise(c.Attribute))))
        {
            var ordered = group.OrderBy(c => c.CallId).ThenBy(c => c.QuoteStartMs).ToList();

            for (var i = 1; i < ordered.Count; i++)
            {
                var earlier = ordered[i - 1];
                var later = ordered[i];

                if (SameValue(earlier, later)) continue;

                // Same call, moments apart is usually someone correcting themselves mid-sentence.
                if (earlier.CallId == later.CallId && Math.Abs(later.QuoteStartMs - earlier.QuoteStartMs) < 30_000)
                    continue;

                yield return (earlier, later);
            }
        }
    }

    private static bool SameValue(Claim a, Claim b)
    {
        if (a.NumericValue is { } x && b.NumericValue is { } y) return x == y;
        return Normalise(a.Value) == Normalise(b.Value);
    }

    /// <summary>
    /// How often direct questions went unanswered, as a countable ratio with the evidence.
    ///
    /// One evaded question is a conversation. A pattern of them is worth seeing, so the flag is
    /// only raised once there are enough questions for the ratio to mean anything.
    /// </summary>
    public static Flag? EvasionRate(
        long callId,
        long? contactId,
        IReadOnlyList<(string quote, int startMs, bool evaded)> directQuestions,
        int minimumQuestions = 3,
        double threshold = 0.5)
    {
        if (directQuestions.Count < minimumQuestions) return null;

        var evaded = directQuestions.Where(q => q.evaded).ToList();
        if (evaded.Count == 0) return null;

        var ratio = (double)evaded.Count / directQuestions.Count;
        if (ratio < threshold) return null;

        var first = evaded[0];

        return new Flag
        {
            CallId = callId,
            ContactId = contactId,
            Kind = FlagKind.EvadedQuestion,
            Summary = $"Doğrudan sorulan {directQuestions.Count} sorudan {evaded.Count} tanesi yanıtsız kaldı",
            Quote = first.quote,
            QuoteStartMs = first.startMs,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string Normalise(string value) => Text.TurkishText.NormalizeForSearch(value);

    private static string Format(decimal value) =>
        value.ToString("#,##0.##", CultureInfo.GetCultureInfo("tr-TR"));
}
