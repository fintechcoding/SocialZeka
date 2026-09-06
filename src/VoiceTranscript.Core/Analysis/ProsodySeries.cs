using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceTranscript.Core.Analysis;

/// <summary>Half a second of one channel, as the worker measured it.</summary>
/// <param name="StartSeconds">From the start of the file.</param>
/// <param name="Dbfs">Mean level of the bin's speech frames; of all its frames when none is speech.</param>
/// <param name="F0Hz">Median pitch of the voiced frames, or null when none was voiced.</param>
/// <param name="Voiced">Share of the bin's frames carrying a pitch, 0..1.</param>
public sealed record ProsodyBin(double StartSeconds, double Dbfs, double? F0Hz, double Voiced);

/// <summary>One channel of one call: the worker's numbers, unchanged.</summary>
public sealed record ProsodyChannel(double FloorDbfs, double SpeechSeconds, IReadOnlyList<ProsodyBin> Bins)
{
    public static readonly ProsodyChannel Empty = new(0, 0, []);
}

/// <summary>Which of the two measurements a point or a peak is about.</summary>
public enum ProsodyMeasure
{
    /// <summary>Loudness, in dBFS. Comparable only inside one channel of one call.</summary>
    Level,

    /// <summary>Pitch, in semitones from this channel's own median.</summary>
    Pitch,
}

/// <summary>One bin, placed against the rest of the same channel.</summary>
/// <param name="LevelZ">How far the level sits from this channel's median, in MAD units. Null when the bin carries no speech.</param>
/// <param name="PitchSemitones">Pitch relative to this channel's median, in semitones. Null when unvoiced.</param>
/// <param name="Excluded">True where the two speakers overlap or the microphone caught the far side: measured, not counted.</param>
public sealed record ProsodyPoint(
    int StartMs,
    int EndMs,
    double Dbfs,
    double? F0Hz,
    double? LevelZ,
    double? PitchSemitones,
    double? PitchZ,
    bool Excluded);

/// <summary>A stretch where one measure stood out from the rest of the same channel.</summary>
public sealed record ProsodyPeak(int StartMs, int EndMs, ProsodyMeasure Measure, double Z, int Bins);

/// <summary>What the whole channel came to: the placed bins, the peaks, and the statistics behind both.</summary>
public sealed record ProsodyReading(
    IReadOnlyList<ProsodyPoint> Points,
    IReadOnlyList<ProsodyPeak> Peaks,
    double? LevelMedianDbfs,
    double? LevelMad,
    double? PitchMedianHz,
    double? PitchMadSemitones,
    int MeasuredBins,
    int ExcludedBins)
{
    public static readonly ProsodyReading Empty = new([], [], null, null, null, null, 0, 0);

    /// <summary>True when there was enough speech for the comparison to mean anything.</summary>
    public bool IsUsable => LevelMad is > 0 && MeasuredBins >= ProsodySeries.MinimumBins;
}

/// <summary>
/// Places one channel's measurements against the rest of that same channel — and against
/// nothing else.
///
/// <b>The rule that shapes this whole file: a number here may only be compared with numbers from
/// the same channel of the same call.</b> Microphone gain depends on the hardware and on where
/// the machine's own mixer left the slider; the far channel's level is whatever WhatsApp's
/// automatic gain decided a moment ago. Two calls' dBFS figures are two different rulers, and
/// subtracting one from the other produces a number that looks like a finding and is an artefact.
/// So everything below is relative: the median of this channel, and the distance from it.
///
/// The distance is measured in MAD units rather than standard deviations. A raised voice is
/// exactly the kind of large, brief excursion that inflates a standard deviation — the outlier
/// drags the very yardstick meant to detect it, and a call with three shouts reports none. The
/// median absolute deviation does not move, which is the whole reason robust statistics exist.
///
/// Pitch is converted to semitones because pitch is heard logarithmically: twenty hertz above a
/// low voice and twenty above a high one are not the same event, and a linear figure would call
/// the second one nothing.
///
/// Nothing here says what a peak MEANS. It is not evidence of anger, stress or lying — the plan
/// forbids that reading and the external research is unambiguous that voice-stress lie detection
/// performs at chance. A peak is a place to listen, and it stays a place to listen until somebody
/// has listened to sixty of them and said whether they heard a change (PLAN-SOSYALZEKA §6.3).
/// </summary>
public static class ProsodySeries
{
    /// <summary>How far from the median counts as standing out. Chosen, not measured — §6.3 sets it.</summary>
    public const double PeakZ = 2.0;

    /// <summary>How many consecutive bins a peak must hold. Four halves is two seconds: a raised voice, not a syllable.</summary>
    public const int PeakBins = 4;

    /// <summary>Below this many measured bins the median is not a median. Thirty halves is fifteen seconds of speech.</summary>
    public const int MinimumBins = 30;

    /// <summary>Turns the median absolute deviation into the same units as a standard deviation for a normal sample.</summary>
    private const double MadToSigma = 1.4826;

    /// <summary>
    /// Places one channel's bins.
    /// </summary>
    /// <param name="channel">The worker's measurements for this channel.</param>
    /// <param name="binSeconds">The bin width the worker used, so milliseconds can be recovered.</param>
    /// <param name="excluded">
    /// Regions that must take no part: where both speakers were talking at once, and where the
    /// far side bled into the microphone. A level measured across another voice is that other
    /// voice, and counting it would put somebody else's shouting on this person's curve.
    /// </param>
    public static ProsodyReading Build(
        ProsodyChannel channel,
        double binSeconds,
        IReadOnlyList<(int StartMs, int EndMs)>? excluded = null)
    {
        if (channel.Bins.Count == 0 || binSeconds <= 0) return ProsodyReading.Empty;

        var width = (int)Math.Round(binSeconds * 1000);
        var blocked = excluded ?? [];

        var placed = new List<(ProsodyBin Bin, int StartMs, int EndMs, bool Excluded)>(channel.Bins.Count);

        foreach (var bin in channel.Bins)
        {
            var start = (int)Math.Round(bin.StartSeconds * 1000);
            var end = start + width;
            var overlaps = blocked.Any(region => start < region.EndMs && region.StartMs < end);

            placed.Add((bin, start, end, overlaps));
        }

        // The statistics are taken over the bins that carry speech and are not excluded: a
        // silent half-second is not a quiet moment in a conversation, it is the absence of one,
        // and letting it into the median would put the median in the silence.
        var counted = placed.Where(p => !p.Excluded && p.Bin.Voiced > 0).ToList();

        var levels = counted.Select(p => p.Bin.Dbfs).ToList();
        var pitches = counted.Where(p => p.Bin.F0Hz is > 0).Select(p => p.Bin.F0Hz!.Value).ToList();

        var levelMedian = Median(levels);
        var levelMad = Scale(levels, levelMedian);

        var pitchMedian = Median(pitches);

        // Pitch statistics live in semitones, so the spread means the same thing at every voice.
        var pitchSemitones = pitchMedian is { } reference and > 0
            ? pitches.Select(hz => Semitones(hz, reference)).ToList()
            : [];

        var pitchMad = Scale(pitchSemitones, Median(pitchSemitones));

        var points = new List<ProsodyPoint>(placed.Count);

        foreach (var (bin, start, end, isExcluded) in placed)
        {
            var speech = bin.Voiced > 0 && !isExcluded;

            double? levelZ = speech && levelMedian is { } lm && levelMad is { } lmad and > 0
                ? (bin.Dbfs - lm) / (lmad * MadToSigma)
                : null;

            double? semitones = bin.F0Hz is { } hz and > 0 && pitchMedian is { } pm and > 0
                ? Semitones(hz, pm)
                : null;

            double? pitchZ = speech && semitones is { } st && pitchMad is { } pmad and > 0
                ? st / (pmad * MadToSigma)
                : null;

            points.Add(new ProsodyPoint(start, end, bin.Dbfs, bin.F0Hz, levelZ, semitones, pitchZ, isExcluded));
        }

        var peaks = counted.Count >= MinimumBins
            ? [.. Peaks(points, ProsodyMeasure.Level), .. Peaks(points, ProsodyMeasure.Pitch)]
            : new List<ProsodyPeak>();

        return new ProsodyReading(
            points,
            [.. peaks.OrderBy(p => p.StartMs)],
            levelMedian,
            levelMad,
            pitchMedian,
            pitchMad,
            counted.Count,
            placed.Count(p => p.Excluded));
    }

    /// <summary>
    /// Runs of consecutive bins above the threshold.
    ///
    /// Rises only. What the user asked to be able to see is whether they raise their voice, and a
    /// quiet stretch is not the same kind of event — it is usually the other person talking, which
    /// the share bar already says better.
    /// </summary>
    private static List<ProsodyPeak> Peaks(IReadOnlyList<ProsodyPoint> points, ProsodyMeasure measure)
    {
        var found = new List<ProsodyPeak>();

        var run = 0;
        var sum = 0.0;
        var startMs = 0;

        for (var i = 0; i <= points.Count; i++)
        {
            var z = i < points.Count
                ? measure == ProsodyMeasure.Level ? points[i].LevelZ : points[i].PitchZ
                : null;

            if (z is { } value && value > PeakZ)
            {
                if (run == 0) startMs = points[i].StartMs;
                run++;
                sum += value;
                continue;
            }

            if (run >= PeakBins)
                found.Add(new ProsodyPeak(startMs, points[i - 1].EndMs, measure, sum / run, run));

            run = 0;
            sum = 0;
        }

        return found;
    }

    /// <summary>Distance in semitones — pitch is heard logarithmically, so it is measured that way.</summary>
    public static double Semitones(double hz, double reference) => 12 * Math.Log2(hz / reference);

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// The spread of a channel, robustly.
    ///
    /// The median absolute deviation, which a handful of shouts cannot inflate — and that is the
    /// whole point: a standard deviation is dragged upward by the very excursions it is meant to
    /// find, so a call with three raised voices in it reports none.
    ///
    /// With one catch it took a failing test to notice. The MAD is zero whenever more than half
    /// the bins share a value, which is not exotic: fifteen loud bins among forty-five identical
    /// quiet ones is exactly the case this measurement exists for, and it would have divided by
    /// nothing and reported no peaks at all — silence about the loudest call in the archive.
    /// So when the MAD is zero and there is nevertheless spread, the mean absolute deviation
    /// stands in: still taken from the median, still finite, and only ever reached in the case
    /// where the robust figure has collapsed. Genuinely flat means null, which is honest — a
    /// channel with no variation has no "unusual".
    /// </summary>
    private static double? Scale(IReadOnlyList<double> values, double? median)
    {
        if (median is not { } m || values.Count == 0) return null;

        var deviations = values.Select(v => Math.Abs(v - m)).ToList();

        if (Median(deviations) is { } mad and > 0) return mad;

        var mean = deviations.Average();

        return mean > 0 ? mean : null;
    }

    /// <summary>
    /// What audio a stored measurement was made from.
    ///
    /// Prosody is a property of the recording, not of the transcript: transcribing again with a
    /// different engine changes none of it, and re-running the measurement would be wasted work.
    /// What DOES invalidate it is the audio changing underneath — silence trimmed, a file
    /// re-encoded — and both of those change a file's length. Names and lengths, then; a hash of
    /// two hours of audio to answer a question two integers already answer is not worth its
    /// minute.
    /// </summary>
    public static string AudioKey(string? micPath, string? farPath) =>
        string.Join('|', new[] { micPath, farPath }.Select(Describe));

    private static string Describe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "-";

        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Name}:{file.Length}" : $"{Path.GetFileName(path)}:yok";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A key that cannot be read is a key that never matches, which re-measures. That is
            // the safe direction: the alternative is showing a curve of the wrong recording.
            return $"{Path.GetFileName(path)}:?";
        }
    }
}

/// <summary>
/// Both channels of one call as they are stored — the worker's numbers, not the reading.
///
/// The measurement is kept and the interpretation is recomputed, because the interpretation is
/// the part that will change: the peak threshold is a guess until sixty peaks have been listened
/// to, and when it moves, nothing should have to touch the audio again.
/// </summary>
public sealed record ProsodySnapshot(double BinSeconds, ProsodyChannel? Mic, ProsodyChannel? Far)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Null rather than an exception: a row this build cannot read is a missing measurement, not a crash.</summary>
    public static ProsodySnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<ProsodySnapshot>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
