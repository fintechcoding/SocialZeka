using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Analysis;

public sealed record TranscriptChunk(IReadOnlyList<Segment> Segments, int Index, int Total)
{
    public int StartMs => Segments.Count > 0 ? Segments[0].StartMs : 0;
    public int EndMs => Segments.Count > 0 ? Segments[^1].EndMs : 0;
}

/// <summary>
/// Splits a transcript into pieces small enough to extract from reliably.
///
/// A one-hour Turkish call is roughly 12 to 20 thousand tokens, and a 4B model on a 6 GB card
/// cannot be trusted to recall an exact quote from the far end of that. So the call is processed
/// in pieces and the results are merged.
///
/// Two decisions matter here.
///
/// The split is on speaker turns, never on a token count. Cutting mid-turn separates a promise
/// from the condition attached to it, and "cuma günü yollarım" read without the "parayı
/// yollarsan" before it becomes an unconditional promise the person never made.
///
/// The merge afterwards is a dictionary union in ordinary code, not another model call. Summary
/// map-reduce loses evidence at every hop; extraction map-reduce does not, because the reduce
/// step is deterministic and lossless.
/// </summary>
public static class TranscriptChunker
{
    /// <summary>
    /// Turkish runs roughly two tokens per word with the tokenizer in use, and speaker labels
    /// and timestamps add more. Four characters per token is a deliberately conservative
    /// estimate: overshooting the budget costs recall, undershooting only costs a little speed.
    /// </summary>
    private const int CharactersPerToken = 4;

    public static IReadOnlyList<TranscriptChunk> Split(
        IReadOnlyList<Segment> segments,
        int targetTokens = 2500,
        int overlapTurns = 2)
    {
        if (segments.Count == 0) return [];

        var budget = targetTokens * CharactersPerToken;
        List<List<Segment>> chunks = [];
        List<Segment> current = [];
        var size = 0;

        foreach (var segment in segments)
        {
            var cost = segment.Text.Length + 24; // label and timestamp overhead

            if (current.Count > 0 && size + cost > budget)
            {
                chunks.Add(current);

                // Carry the last turns forward so a promise split across the boundary is still
                // seen whole by the next chunk, and pronouns still have something to refer to.
                current = [.. current.TakeLast(Math.Min(overlapTurns, current.Count))];
                size = current.Sum(s => s.Text.Length + 24);
            }

            current.Add(segment);
            size += cost;
        }

        if (current.Count > 0) chunks.Add(current);

        return [.. chunks.Select((c, i) => new TranscriptChunk(c, i, chunks.Count))];
    }

    /// <summary>
    /// A short running summary carried into each chunk so references resolve.
    ///
    /// Deliberately thin, and deliberately not where anything is stored. "O para" and "geçen
    /// sefer dediğin gibi" need context to make sense, but facts live in the extracted JSON,
    /// never in a summary — a summary decays with every hop, and a price mentioned eight calls
    /// ago survives in a table while it would not survive a chain of paraphrases.
    /// </summary>
    public static string BuildRollingContext(IReadOnlyList<Segment> priorSegments, int maxCharacters = 1200)
    {
        if (priorSegments.Count == 0) return "";

        var lines = priorSegments
            .TakeLast(12)
            .Select(s => $"{(s.IsMe ? "BEN" : "KARSI")}: {s.Text.Trim()}")
            .ToList();

        var text = string.Join("\n", lines);
        return text.Length <= maxCharacters ? text : text[^maxCharacters..];
    }
}
