using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Cuts a line where the other person actually answered, so the conversation reads in the order
/// it happened.
///
/// The chat is a single column ordered by start time, and that is the right shape for it: people
/// read an exchange top to bottom. It breaks in exactly one place. When somebody talks for twelve
/// seconds and the other person answers in the middle, the answer starts later and therefore sorts
/// underneath — below a bubble that visually spans it. On screen the reply appears to come after
/// the whole utterance, and on a conversation with real overlap it happens constantly: one call in
/// this archive spends 130 of its 1129 seconds with both people talking, and 47 of its lines
/// contain one of the other person's turns.
///
/// <b>Not every interruption deserves a cut, and the difference was measured rather than guessed.</b>
/// Of the 56 turns buried inside another line on that call, 18 are back-channel — "Ha,", "Yani."
/// — and 38 are real answers, one of them seven words long. Cutting a sentence in half to make
/// room for "Ha," loses more than it fixes; leaving <i>"Tabii o için de o anlaşılmıyor ama."</i>
/// underneath the sentence it answers is not a display convention, it is the conversation read
/// wrongly. So the threshold below separates the two, and it separates them the way the numbers
/// fall rather than at a round figure chosen for looking reasonable.
///
/// <b>This is a reading, not an edit.</b> It runs when the window builds its bubbles and changes
/// nothing that is stored: quotes keep their timestamps, the ledger keeps its citations, search
/// keeps its index, and the transcript history keeps the lines the engine actually produced. The
/// same cut was tried in the worker first, on the transcript itself, and measured worse — a
/// sentence divided in the database is divided for every reader of it, including the ones with no
/// screen.
/// </summary>
public static class ChatFlow
{
    /// <summary>
    /// Below this, an interruption is back-channel and the line it lands in is left whole.
    ///
    /// Two conditions rather than one because either alone mislabels: "Değil mi? Oh. Onu." is four
    /// short words and a real turn, while a single word held for a second — "Yaa" — is not. A turn
    /// qualifies by saying something (two words) or by taking the floor for long enough (0.8 s).
    /// </summary>
    public const int MinimumWords = 2;

    /// <summary>The other half of the same test. See <see cref="MinimumWords"/>.</summary>
    public const int MinimumMs = 800;

    /// <summary>
    /// The same lines, with any that bury a real answer cut open at the moment it began.
    ///
    /// Order is by start time throughout, so the result drops straight into the existing view. A
    /// line without word timestamps is returned untouched: there is nowhere to cut it that is not
    /// invented, and an invented boundary puts a quote at a moment nobody spoke.
    /// </summary>
    public static IReadOnlyList<Segment> InReadingOrder(IReadOnlyList<Segment> segments)
    {
        if (segments.Count < 2) return segments;

        var ordered = segments.OrderBy(s => s.StartMs).ToList();

        var answers = ordered
            .Where(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= MinimumWords
                        || s.EndMs - s.StartMs >= MinimumMs)
            .ToList();

        var out_ = new List<Segment>(ordered.Count);

        foreach (var line in ordered)
        {
            if (line.Words.Count < 2)
            {
                out_.Add(line);
                continue;
            }

            // Turns of the other person that begin and end inside this line — the ones being
            // buried by it. Their own start is where this line has to give way.
            var buried = answers
                .Where(a => a.IsMe != line.IsMe && a.StartMs > line.StartMs && a.EndMs < line.EndMs)
                .Select(a => a.StartMs)
                .OrderBy(ms => ms)
                .ToList();

            if (buried.Count == 0)
            {
                out_.Add(line);
                continue;
            }

            out_.AddRange(Pieces(line, buried));
        }

        return [.. out_.OrderBy(s => s.StartMs)];
    }

    /// <summary>Splits one line at the first word starting at or after each buried answer.</summary>
    private static IEnumerable<Segment> Pieces(Segment line, List<int> buried)
    {
        var cuts = new List<int>();

        foreach (var at in buried)
        {
            // The first word that had not yet been said when they started answering. Everything
            // before it belongs above the answer; this word and the rest belong below.
            var index = -1;
            for (var i = 0; i < line.Words.Count; i++)
            {
                if (line.Words[i].StartMs < at) continue;
                index = i;
                break;
            }

            // Not found, or it would leave an empty half.
            if (index <= 0 || index >= line.Words.Count) continue;
            if (cuts.Count > 0 && cuts[^1] == index) continue;

            cuts.Add(index);
        }

        if (cuts.Count == 0)
        {
            yield return line;
            yield break;
        }

        var from = 0;

        foreach (var to in cuts.Append(line.Words.Count))
        {
            yield return Piece(line, from, to);
            from = to;
        }
    }

    /// <summary>One run of words, carrying its parent's flags and its own span.</summary>
    private static Segment Piece(Segment line, int from, int to)
    {
        var words = line.Words.Skip(from).Take(to - from).ToList();

        return new Segment
        {
            Id = line.Id,
            CallId = line.CallId,
            IsMe = line.IsMe,
            StartMs = words[0].StartMs,
            EndMs = words[^1].EndMs,
            Text = string.Concat(words.Select(w => w.Text)).Trim(),

            // Confidence was measured over the whole decode, so it carries to both halves. It is a
            // coarse "is this audio trustworthy" gate, not a per-word probability.
            AvgLogprob = line.AvgLogprob,
            NoSpeechProb = line.NoSpeechProb,
            LowConfidence = line.LowConfidence,
            SuspectedEcho = line.SuspectedEcho,

            // A piece that was cut open necessarily overlaps the other speaker: that is why it
            // was cut.
            OverlapsOtherSpeaker = true,

            Words = words,
        };
    }
}
