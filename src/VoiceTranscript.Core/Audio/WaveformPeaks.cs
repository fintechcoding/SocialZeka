namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Loudness over time, reduced to a few hundred numbers.
///
/// Drawn as two mirrored bands — the user above the line, the other party below — which is the
/// clearest statement this product can make about what it is. Nothing else in a call recorder
/// can show that shape honestly: a single mixed stream has one waveform and no way to say whose
/// it is at any moment. Here it is not a rendering trick, it is two files.
///
/// It is also the fastest way to read a conversation. Long even bands mean somebody talked at
/// length; a dense picket fence means the two of you traded short sentences; a stretch where
/// only the lower band moves is somebody being talked at.
/// </summary>
public static class WaveformPeaks
{
    /// <summary>
    /// Reads a WAV into <paramref name="buckets"/> peak values between 0 and 1.
    ///
    /// Peak rather than average: an average flattens speech into a low even smear, because most
    /// of any spoken syllable is quiet. The peak is what the eye reads as "somebody was talking
    /// here", and that is the only question this drawing has to answer.
    /// </summary>
    public static float[] Read(string path, int buckets = 600)
    {
        if (buckets <= 0) return [];
        if (!File.Exists(path)) return new float[buckets];

        try
        {
            using var stream = File.OpenRead(path);
            var (dataStart, dataLength, format) = PcmReader.ReadHeader(stream);

            if (dataLength <= 0 || format.BitsPerSample != 16 || format.Channels <= 0)
                return new float[buckets];

            var totalFrames = dataLength / format.BytesPerFrame;
            if (totalFrames <= 0) return new float[buckets];

            var peaks = new float[buckets];
            var framesPerBucket = Math.Max(1, totalFrames / buckets);

            stream.Position = dataStart;

            // Read in blocks rather than sample by sample: an hour of 16 kHz mono is 115 MB of
            // reads, and this runs while somebody is waiting to look at a transcript.
            var block = new byte[framesPerBucket * format.BytesPerFrame];

            for (var bucket = 0; bucket < buckets; bucket++)
            {
                var read = stream.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
                if (read <= 0) break;

                peaks[bucket] = PeakOf(block.AsSpan(0, read - read % 2), format.Channels);
            }

            return peaks;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            // A recording still being written, or one on a disconnected drive. An empty waveform
            // is a fine answer; refusing to open the transcript is not.
            return new float[buckets];
        }
    }

    private static float PeakOf(ReadOnlySpan<byte> pcm, int channels)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        if (samples.Length == 0) return 0;

        var peak = 0;

        // Only the first channel: this application records mono, and on a stereo file the two
        // channels of a voice recording are near identical anyway.
        for (var i = 0; i < samples.Length; i += channels)
        {
            var sample = samples[i];
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }

        return peak / (float)short.MaxValue;
    }

}
