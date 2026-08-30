namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Cuts the moment a thing was said out of a recording.
///
/// Every item in the ledger already carries a quote and a timestamp so that the user can check it
/// themselves. This is the next step: a small file holding those few seconds, which can be played
/// to the person it is about.
///
/// The reason that is worth building rather than saying "seek to 12:04" is that a claim about a
/// conversation is worth exactly what its evidence is worth. "You said eighteen thousand" is an
/// argument; eight seconds of somebody saying it is not. Nobody is going to install this
/// application to hear the other half of that.
///
/// Cut from the mixed recording rather than one side, so the exchange is audible — a promise with
/// the question that prompted it removed is a different promise. Padding is added at both ends
/// because a transcript segment starts at the first detected syllable, and a clip beginning
/// exactly there sounds truncated and, worse, sounds edited.
/// </summary>
public static class AudioClip
{
    /// <summary>
    /// Seconds kept either side of the quoted stretch.
    ///
    /// Enough to hear the run-up and the reaction, which is what makes a clip credible rather
    /// than merely accurate. Short enough that it stays a clip.
    /// </summary>
    public static readonly TimeSpan DefaultPadding = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Writes the stretch between two moments into its own WAV.
    ///
    /// Returns false rather than throwing when the recording is missing or the range is empty:
    /// this runs from a button beside a ledger entry, and an entry whose audio has been deleted
    /// under it is an ordinary state, not a fault.
    /// </summary>
    public static bool Extract(
        string sourcePath,
        TimeSpan start,
        TimeSpan end,
        string outputPath,
        TimeSpan? padding = null)
    {
        if (!File.Exists(sourcePath)) return false;

        var pad = padding ?? DefaultPadding;

        var from = start - pad;
        var to = end + pad;

        if (from < TimeSpan.Zero) from = TimeSpan.Zero;
        if (to <= from) return false;

        try
        {
            using var reader = PcmReader.Open(sourcePath);

            var format = reader.Format;
            if (format.BitsPerSample != 16 || format.Channels <= 0) return false;

            var firstFrame = (long)(from.TotalSeconds * format.SampleRate);
            var lastFrame = (long)(to.TotalSeconds * format.SampleRate);

            // A quote near the end of a call asks for padding past the end of the file.
            if (firstFrame >= reader.Frames) return false;
            if (lastFrame > reader.Frames) lastFrame = reader.Frames;

            var frames = lastFrame - firstFrame;
            if (frames <= 0) return false;

            var temporary = outputPath + ".partial";
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            using (var output = File.Create(temporary))
            {
                WriteHeader(output, format, frames);

                var buffer = new short[8192];

                // Skipped by reading rather than seeking, because the audio does not begin at a
                // fixed offset — a LIST chunk before it would put every sample in the wrong
                // place, and the clip would be of a different moment entirely.
                var toSkip = firstFrame * format.Channels;
                while (toSkip > 0)
                {
                    var read = reader.Read(buffer.AsSpan(0, (int)Math.Min(toSkip, buffer.Length)));
                    if (read <= 0) return false;

                    toSkip -= read;
                }

                var remaining = frames * format.Channels;

                while (remaining > 0)
                {
                    var read = reader.Read(buffer.AsSpan(0, (int)Math.Min(remaining, buffer.Length)));
                    if (read <= 0) break;

                    output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        buffer.AsSpan(0, read)));

                    remaining -= read;
                }
            }

            File.Move(temporary, outputPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// A file name that says what the clip is, without saying it in the file name.
    ///
    /// The contact's name is deliberately left out. These files are made to be sent to somebody,
    /// and a name in the file name discloses who else is in the archive to whoever receives it.
    /// The date and time are enough to find it again.
    /// </summary>
    public static string SuggestedName(DateTimeOffset callStartedAt, TimeSpan at) =>
        $"kesit-{callStartedAt.ToLocalTime():yyyy-MM-dd-HHmm}-{(int)at.TotalMinutes:00}dk{at.Seconds:00}sn.wav";

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
