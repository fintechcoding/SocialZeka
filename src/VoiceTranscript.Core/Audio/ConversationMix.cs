namespace VoiceTranscript.Core.Audio;

/// <summary>
/// The two sides of a call put back together as one recording.
///
/// Recording the microphone and the speaker separately is what lets this application say who
/// said something rather than guess, and nothing here changes that: the two streams remain the
/// originals and remain the evidence. But nobody listens to half a conversation. Playing one
/// side alone is a person talking into a void with the replies cut out, which is unusable for
/// the ordinary thing somebody wants — to hear how it went.
///
/// So the whole conversation is a third file, built from the other two on demand and cached
/// beside them. Built rather than recorded, deliberately:
///
///   - it costs nothing during the call, when the machine is busy and being noticed;
///   - every recording ever made gets one, including the ones already on disk;
///   - it can be deleted freely, because it is derived and can always be made again.
///
/// The alignment it depends on is the same alignment the speaker attribution depends on. Both
/// streams are written against one wall clock with gaps filled from QPC stamps, so sample N of
/// one is the same instant as sample N of the other. If that were not true the transcript would
/// already be wrong and a mixed file would be the least of it.
/// </summary>
public static class ConversationMix
{
    /// <summary>Where the mixed copy of a recording belongs: beside it, ending in -butun.</summary>
    public static string PathFor(string micPath)
    {
        var directory = Path.GetDirectoryName(micPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(micPath);

        // The microphone file is named "<call>-mic.wav", so the suffix is replaced rather than
        // appended — "<call>-mic-butun.wav" would read as a mixdown of the microphone alone.
        if (name.EndsWith("-mic", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return Path.Combine(directory, name + "-butun.wav");
    }

    /// <summary>
    /// Writes both sides into one file, and returns its path.
    ///
    /// Returns the existing file untouched when it is already there and newer than its sources,
    /// so playing the same conversation twice does not rebuild it. A stale one — sources rewritten
    /// after a re-record — is replaced rather than trusted.
    /// </summary>
    public static string? Ensure(string? micPath, string? farPath)
    {
        var anchor = micPath ?? farPath;
        if (anchor is null) return null;

        var output = PathFor(anchor);

        if (File.Exists(output) && !IsStale(output, micPath, farPath)) return output;

        return Build(micPath, farPath, output) ? output : null;
    }

    private static bool IsStale(string output, string? micPath, string? farPath)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(output);

            foreach (var source in new[] { micPath, farPath })
            {
                if (source is null || !File.Exists(source)) continue;
                if (File.GetLastWriteTimeUtc(source) > written) return true;
            }

            // A zero-length file is what a mix interrupted halfway leaves behind. Treating it as
            // valid would mean silently playing nothing, forever, with no way to recover.
            return new FileInfo(output).Length <= 44;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Sums the two recordings into a new WAV.
    ///
    /// Summed at full level and clamped rather than halved. Halving is the textbook way to make
    /// clipping impossible, and it is wrong here: for most of a call only one person is speaking,
    /// so halving turns the entire conversation down by 6 dB to protect against the seconds where
    /// both talk at once. Voice recordings from a VoIP stack arrive well short of full scale, and
    /// two of them overlapping rarely reach it — and when they do, a moment of clipping on a
    /// convenience copy is a far smaller price than a whole recording nobody can hear.
    ///
    /// Streams of different lengths are padded with silence rather than truncated. The shorter
    /// file is usually the microphone on a call the user barely spoke in, and cutting the other
    /// side off at that point would delete the part worth keeping.
    /// </summary>
    public static bool Build(string? micPath, string? farPath, string outputPath)
    {
        try
        {
            using var mic = OpenPcm(micPath);
            using var far = OpenPcm(farPath);

            if (mic is null && far is null) return false;

            var format = mic?.Format ?? far!.Format;

            // Mixing samples from streams that disagree about rate or channel count would shift
            // one side against the other, and a mixed file that drifts is worse than none: it
            // looks authoritative while putting words in the wrong mouths.
            if (far is not null && mic is not null && !SameShape(mic.Format, far.Format)) return false;
            if (format.BitsPerSample != 16) return false;

            // Named per build, not per output. Two builds of the same conversation can overlap —
            // playback asks for the mix while an export of the same call is producing it — and
            // with one shared ".partial" name the second File.Create truncated the file the first
            // was still writing, and whichever finished last renamed a torn file into place.
            var temporary = $"{outputPath}.{Guid.NewGuid():N}.partial";
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            var frames = Math.Max(mic?.Frames ?? 0, far?.Frames ?? 0);
            if (frames <= 0) return false;

            using (var output = File.Create(temporary))
            {
                WriteHeader(output, format, frames);

                var a = new short[8192];
                var b = new short[8192];
                var mixed = new short[8192];

                var remaining = frames * format.Channels;

                while (remaining > 0)
                {
                    var take = (int)Math.Min(remaining, a.Length);

                    var readA = mic?.Read(a.AsSpan(0, take)) ?? 0;
                    var readB = far?.Read(b.AsSpan(0, take)) ?? 0;

                    a.AsSpan(readA, take - readA).Clear();
                    b.AsSpan(readB, take - readB).Clear();

                    for (var i = 0; i < take; i++)
                        mixed[i] = (short)Math.Clamp(a[i] + b[i], short.MinValue, short.MaxValue);

                    output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        mixed.AsSpan(0, take)));

                    remaining -= take;
                }
            }

            // Renamed into place only once it is complete, so an interrupted mix never leaves a
            // half-written file that looks finished.
            File.Move(temporary, outputPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DiscardPartials(outputPath);
            return false;
        }
    }

    /// <summary>
    /// Removes every unfinished build of this mix, whatever run left it behind.
    ///
    /// Also what forgetting a recording calls, so the temporaries never outlive the audio they
    /// were derived from.
    /// </summary>
    public static void DiscardPartials(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        foreach (var partial in Directory.EnumerateFiles(directory, Path.GetFileName(outputPath) + ".*partial"))
        {
            try { File.Delete(partial); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool SameShape(AudioFormat a, AudioFormat b) =>
        a.SampleRate == b.SampleRate && a.Channels == b.Channels && a.BitsPerSample == b.BitsPerSample;

    private static PcmReader? OpenPcm(string? path)
    {
        if (path is null || !File.Exists(path)) return null;

        try
        {
            return PcmReader.Open(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static void WriteHeader(Stream stream, AudioFormat format, long frames)
    {
        var dataBytes = (int)(frames * format.BytesPerFrame);
        var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(format.SampleRate * format.BytesPerFrame);
        writer.Write((short)format.BytesPerFrame);
        writer.Write((short)format.BitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Flush();
    }
}
