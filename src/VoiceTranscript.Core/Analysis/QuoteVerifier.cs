using System.Text;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Analysis;

public sealed record VerifiedQuote(string Text, int StartMs, int EndMs, bool IsMe, bool LowConfidence);

/// <summary>
/// Checks that a quote the model produced actually appears in the transcript, and finds where.
///
/// This is the single most important guard in the analysis layer, and it is not optional.
///
/// Everything the product shows is framed as evidence: "this is what they said, at this moment,
/// click to hear it". A model that paraphrases while claiming to quote turns that into
/// fabricated evidence about a real person — someone's friend, their supplier, their family.
/// A system that invents quotes about people is worse than no system at all, so a quote that
/// cannot be located in the source is rejected rather than shown with a caveat.
///
/// The comparison is deliberately forgiving about presentation and strict about content.
/// Whitespace, punctuation and Turkish spelling variants are normalised away, because a model
/// re-spelling "yapacağım" as "yapacagim" is a formatting difference, not a different claim.
/// Different words are a different claim, and those are refused.
/// </summary>
public static class QuoteVerifier
{
    /// <summary>
    /// Locates a quote in the transcript.
    ///
    /// Returns null when the words are not there — which means the model made them up, and the
    /// finding that rests on them must be discarded.
    /// </summary>
    public static VerifiedQuote? Locate(string? quote, IReadOnlyList<Segment> segments)
    {
        if (string.IsNullOrWhiteSpace(quote) || segments.Count == 0) return null;

        var needle = Normalise(quote);
        if (needle.Length == 0) return null;

        // A single short word cannot anchor anything.
        //
        // "Tamam", "olur", "evet" occur several times in most conversations, and a finding built
        // on one would be pinned to whichever minute came first, with a timestamp that looked
        // exact. A wrong timestamp is worse than none here: the whole promise of this ledger is
        // that clicking the time proves the claim.
        //
        // Only single words are refused. Two words are already distinctive enough — "Kapora bir"
        // is a real thing somebody said and there is no reason to throw it away — and the
        // duplicate check below catches the rest by evidence rather than by length.
        if (!needle.Contains(' ') && needle.Length < 15) return null;

        // Common case: the quote sits inside a single segment.
        //
        // Collected rather than returned on sight. A phrase that appears in two places is not
        // evidence for either of them, and picking the earlier one silently invents a moment.
        VerifiedQuote? single = null;

        foreach (var segment in segments)
        {
            if (!Normalise(segment.Text).Contains(needle, StringComparison.Ordinal)) continue;

            // Seen twice: unverifiable, and the caller drops the finding.
            if (single is not null) return null;

            single = new VerifiedQuote(
                segment.Text.Trim(), segment.StartMs, segment.EndMs, segment.IsMe, segment.LowConfidence);
        }

        if (single is not null) return single;

        // A quote may legitimately run across consecutive segments from the same speaker, since
        // segment boundaries come from the transcriber rather than from the sentence.
        return LocateAcrossSegments(needle, segments);
    }

    private static VerifiedQuote? LocateAcrossSegments(string needle, IReadOnlyList<Segment> segments)
    {
        for (var start = 0; start < segments.Count; start++)
        {
            var builder = new StringBuilder();
            var speaker = segments[start].IsMe;
            var lowConfidence = false;

            for (var end = start; end < segments.Count; end++)
            {
                var segment = segments[end];

                // Crossing speakers would stitch together words neither person said as one
                // sentence, which is exactly the kind of invented evidence this class prevents.
                if (segment.IsMe != speaker) break;

                if (builder.Length > 0) builder.Append(' ');
                builder.Append(segment.Text);
                lowConfidence |= segment.LowConfidence;

                if (Normalise(builder.ToString()).Contains(needle, StringComparison.Ordinal))
                {
                    return new VerifiedQuote(
                        builder.ToString().Trim(),
                        segments[start].StartMs,
                        segment.EndMs,
                        speaker,
                        lowConfidence);
                }

                // A real quote spans a couple of segments, not a whole call.
                if (end - start >= 4) break;
            }
        }

        return null;
    }

    /// <summary>
    /// Folds text so that presentation differences do not count as content differences.
    ///
    /// Turkish letters collapse onto their ASCII bases for the same reason the search index does
    /// it: a model that writes "yapacagim" for "yapacağım" has quoted correctly and spelled
    /// loosely, and refusing that would throw away sound findings.
    /// </summary>
    private static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var ch in text)
        {
            var mapped = ch switch
            {
                'İ' or 'I' or 'ı' or 'i' or 'Î' or 'î' => 'i',
                'Ğ' or 'ğ' => 'g',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ö' or 'ö' => 'o',
                'Ü' or 'ü' => 'u',
                'Â' or 'â' => 'a',
                'Û' or 'û' => 'u',
                _ => char.ToLowerInvariant(ch),
            };

            if (char.IsLetterOrDigit(mapped))
            {
                builder.Append(mapped);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                // Punctuation and whitespace both become a single separator, so "on sekiz bin."
                // and "on sekiz bin" compare equal.
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Filters extracted items down to those whose quote could be verified.
    ///
    /// Returns what survived along with what was rejected, so the rejection rate can be shown
    /// rather than hidden: a model rejected on most of its output is a model to stop trusting.
    /// </summary>
    public static (List<T> kept, List<T> rejected) Filter<T>(
        IEnumerable<T> items,
        Func<T, string?> quoteOf,
        IReadOnlyList<Segment> segments,
        Action<T, VerifiedQuote>? onVerified = null)
    {
        List<T> kept = [];
        List<T> rejected = [];

        foreach (var item in items)
        {
            var located = Locate(quoteOf(item), segments);

            if (located is null)
            {
                rejected.Add(item);
                continue;
            }

            onVerified?.Invoke(item, located);
            kept.Add(item);
        }

        return (kept, rejected);
    }
}
