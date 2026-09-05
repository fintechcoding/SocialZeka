using System.Text.Json;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Storage;

/// <summary>
/// Word timings, in and out of the one column that holds them.
///
/// <b>Written as arrays rather than objects, and the difference is not cosmetic.</b> A long call
/// carries several thousand words; spelled as <c>{"startMs":1180,"endMs":1680,"text":"ne"}</c>
/// that is about forty bytes of punctuation per word before any word is stored, and the archive
/// this belongs to already holds two hundred calls. As <c>[1180,1680,"ne"]</c> the same fact
/// costs what it is worth. Nothing outside this file ever sees the encoding. A fourth element,
/// the engine's confidence, rides along only when the engine gave one — a triple stays a triple,
/// so every line written before confidences were kept reads back exactly as it was.
///
/// Both directions are total: unreadable text reads back as no words, because a line whose
/// timings cannot be parsed is a line to be shown without them, never a line to be lost.
/// </summary>
public static class SegmentWords
{
    /// <summary>The JSON for a line's words, or null when it has none to store.</summary>
    public static string? Write(IReadOnlyList<Domain.SpokenWord>? words)
    {
        if (words is null || words.Count == 0) return null;

        var rows = new object?[words.Count][];

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];

            // Three decimals: a confidence is a rough figure, and fifteen digits of it per word
            // would cost more than the timings it sits beside.
            rows[i] = word.Probability is { } p
                ? [word.StartMs, word.EndMs, word.Text, Math.Round(p, 3)]
                : [word.StartMs, word.EndMs, word.Text];
        }

        return JsonSerializer.Serialize(rows);
    }

    /// <summary>The words in a stored line, or an empty list for anything that has none.</summary>
    public static IReadOnlyList<Domain.SpokenWord> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var rows = JsonSerializer.Deserialize<JsonElement[]>(json);
            if (rows is null) return [];

            var words = new List<Domain.SpokenWord>(rows.Length);

            foreach (var row in rows)
            {
                // A row that is not a triple is skipped rather than failing the line: this
                // column may outlive the shape it was written in, and the text of what somebody
                // said must not depend on the timings beside it parsing.
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) continue;

                // The kinds are checked before the values are asked for, because TryGetInt32
                // answers "no" by throwing when the element is not a number at all — a Try that
                // does not try. Left as it was, one row of the wrong shape took the whole line's
                // text with it, which is the outcome this method exists to prevent.
                if (row[0].ValueKind != JsonValueKind.Number
                    || row[1].ValueKind != JsonValueKind.Number
                    || row[2].ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (row[0].TryGetInt32(out var start)
                    && row[1].TryGetInt32(out var end)
                    && row[2].GetString() is { } text)
                {
                    // The fourth element is optional and, when present, a number; anything else
                    // reads as "no confidence" rather than as a broken word.
                    double? probability =
                        row.GetArrayLength() >= 4 && row[3].ValueKind == JsonValueKind.Number
                            ? row[3].GetDouble()
                            : null;

                    words.Add(new Domain.SpokenWord(start, end, text, probability));
                }
            }

            return words;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
