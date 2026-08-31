namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Shortens the dead air in a finished recording, without touching a word.
///
/// A 46-minute call is ~171 MB as two 16 kHz WAVs, and by design more than half of each file is
/// silence: each stream is one side of a conversation, quiet whenever the other person talks.
/// That half cannot be cut — it is what keeps the two files on one clock. What CAN go is the
/// time when nobody is talking: both streams quiet at once, the pauses and the minutes of
/// neither-side-speaking that pad out real phone calls.
///
/// Hence the one rule this class exists to enforce: <b>silence is only silence when it is silent
/// in both files at the same moment, and both files are always cut identically.</b> The two
/// streams share a timeline; trimming them independently would slide one against the other, and
/// every "who said what when" downstream would quietly become fiction.
///
/// The cuts are conservative on purpose. Only runs longer than <see cref="MinimumCutMs"/> are
/// touched, and each keeps <see cref="KeptEdgeMs"/> at both ends, so speech never butts against
/// speech and a held breath before an answer survives. What is removed is dead air, measured,
/// not inferred: an RMS floor per 100 ms window, in both files at once.
/// </summary>
public static class SilenceTrimmer
{
    /// <summary>A span of the ORIGINAL timeline that was removed, in frames.</summary>
    public sealed record Cut(long StartFrame, long Frames)
    {
        public long EndFrame => StartFrame + Frames;
    }

    /// <summary>Window the loudness is measured over. 100 ms: shorter than any spoken word.</summary>
    public const int WindowMs = 100;

    /// <summary>
    /// A silent run must be at least this long before anything is cut. Below it, the pause is
    /// conversation — thinking, breathing, hesitating — and belongs to the recording.
    /// </summary>
    public const int MinimumCutMs = 2_500;

    /// <summary>Kept at each end of every cut, so nothing ever sounds spliced.</summary>
    public const int KeptEdgeMs = 300;

    /// <summary>
    /// RMS below this is silence. ~-44 dBFS: room tone and headset hiss sit under it,
    /// whispered speech does not.
    /// </summary>
    public const double RmsFloor = 200;

    /// <summary>
    /// Where the shared timeline is silent in BOTH files, as removable spans.
    ///
    /// The timeline is as long as the longer file; a shorter file's missing tail counts as
    /// silent, because absence of recording is the one perfect silence there is.
    /// </summary>
    public static IReadOnlyList<Cut> PlanCuts(string micPath, string farPath)
    {
        var mic = WindowLoudness(micPath);
        var far = WindowLoudness(farPath);

        var format = AudioFormat.WhisperPcm;
        var framesPerWindow = format.SampleRate * WindowMs / 1000;

        var windows = Math.Max(mic.Count, far.Count);
        var minimumWindows = MinimumCutMs / WindowMs;
        var edgeWindows = KeptEdgeMs / WindowMs;

        var cuts = new List<Cut>();
        var runStart = -1;

        for (var i = 0; i <= windows; i++)
        {
            var silent = i < windows
                         && (i >= mic.Count || mic[i] < RmsFloor)
                         && (i >= far.Count || far[i] < RmsFloor);

            if (silent)
            {
                if (runStart < 0) runStart = i;
                continue;
            }

            if (runStart >= 0)
            {
                var length = i - runStart;

                if (length >= minimumWindows)
                {
                    var from = (long)(runStart + edgeWindows) * framesPerWindow;
                    var frames = (long)(length - 2 * edgeWindows) * framesPerWindow;

                    cuts.Add(new Cut(from, frames));
                }

                runStart = -1;
            }
        }

        return cuts;
    }

    /// <summary>RMS per window, walked once, streamed — a long call never fits in memory twice.</summary>
    private static List<double> WindowLoudness(string path)
    {
        using var reader = PcmReader.Open(path);

        var framesPerWindow = reader.Format.SampleRate * WindowMs / 1000;
        var buffer = new short[framesPerWindow];
        var loudness = new List<double>();

        while (true)
        {
            var read = ReadFully(reader, buffer);
            if (read == 0) break;

            double sum = 0;
            for (var i = 0; i < read; i++) sum += (double)buffer[i] * buffer[i];

            loudness.Add(Math.Sqrt(sum / read));

            if (read < buffer.Length) break;
        }

        return loudness;
    }

    private static int ReadFully(PcmReader reader, short[] buffer)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = reader.Read(buffer.AsSpan(total));
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    /// <summary>
    /// Writes a copy of one file with the cuts removed, to a new path.
    ///
    /// A new path, never in place: the caller swaps files only after BOTH streams have been
    /// written whole, so a crash mid-trim costs a temp file and nothing else.
    /// </summary>
    public static void Apply(string sourcePath, string targetPath, IReadOnlyList<Cut> cuts)
    {
        using var reader = PcmReader.Open(sourcePath);
        using var sink = new WavPcmSink(targetPath, reader.Format);

        var buffer = new short[reader.Format.SampleRate]; // one second at a time
        long position = 0;
        var next = 0;

        while (true)
        {
            var read = reader.Read(buffer);
            if (read == 0) break;

            var offset = 0;

            while (offset < read)
            {
                // Inside a cut: skip to its end (or the end of what was read).
                if (next < cuts.Count && position >= cuts[next].StartFrame && position < cuts[next].EndFrame)
                {
                    var skip = (int)Math.Min(cuts[next].EndFrame - position, read - offset);
                    position += skip;
                    offset += skip;

                    if (position >= cuts[next].EndFrame) next++;
                    continue;
                }

                // Outside: write up to the next cut's start.
                var until = next < cuts.Count ? cuts[next].StartFrame : long.MaxValue;
                var keep = (int)Math.Min(until - position, read - offset);

                sink.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    buffer.AsSpan(offset, keep)));
                position += keep;
                offset += keep;
            }
        }
    }

    /// <summary>The cuts in milliseconds, for shifting everything the database says about time.</summary>
    public static IReadOnlyList<(long StartMs, long RemovedMs)> ToMilliseconds(IReadOnlyList<Cut> cuts)
    {
        var format = AudioFormat.WhisperPcm;

        return
        [
            .. cuts.Select(c => (
                c.StartFrame * 1000 / format.SampleRate,
                c.Frames * 1000 / format.SampleRate)),
        ];
    }

    /// <summary>
    /// An original-timeline moment's position after the cuts.
    ///
    /// A moment inside a removed span lands at the seam — the instant where the surrounding
    /// audio now meets. Nothing that was speech can be inside a span, so the seam is exact for
    /// everything that matters and off by at most the span for silence nobody can play anyway.
    /// </summary>
    public static long MapMs(long originalMs, IReadOnlyList<(long StartMs, long RemovedMs)> cuts)
    {
        long removedBefore = 0;

        foreach (var (start, removed) in cuts)
        {
            if (originalMs <= start) break;

            removedBefore += Math.Min(removed, originalMs - start);
        }

        return originalMs - removedBefore;
    }
}
