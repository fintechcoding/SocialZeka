using System.Text;

namespace VoiceTranscript.Core.Audio;

/// <summary>
/// How long the audio in a file really is, read from the file itself.
///
/// The point of asking is that the database can be wrong about it, and was. A recording's
/// duration is written when the call ends and every later step that rewrites the audio is
/// supposed to correct it — so any step that rewrites the file and then fails before it gets to
/// the row leaves a number that no longer describes anything on disk. Nothing noticed, because
/// nothing ever compared the two: the player scaled its timeline to the stored figure while the
/// waveform came from a file 28 seconds shorter, and every position in the conversation drifted
/// by up to a seventh of its length.
///
/// Deliberately without decoding. A WAV says its length in its header and an Ogg says it in the
/// granule position of its last page, so the answer costs one seek to the end of the file rather
/// than a pass through several minutes of Opus — which matters, because this is asked about every
/// recording in an archive.
/// </summary>
public static class AudioLength
{
    /// <summary>The audio's real duration, or null when the file cannot answer.</summary>
    public static TimeSpan? Of(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return FromWav(path);

            if (path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
            {
                return FromOgg(path);
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            // A length nobody can read is a missing number, never a reason to fail whatever
            // asked for it.
            return null;
        }
    }

    /// <summary>The data chunk over the byte rate, which is what a WAV header is for.</summary>
    private static TimeSpan? FromWav(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (Tag(reader) != "RIFF") return null;
        reader.ReadInt32();
        if (Tag(reader) != "WAVE") return null;

        var byteRate = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            var id = Tag(reader);
            var size = reader.ReadInt32();

            if (size < 0) return null;

            if (id == "fmt ")
            {
                var chunk = reader.ReadBytes(size);
                if (chunk.Length < 16) return null;

                byteRate = BitConverter.ToInt32(chunk, 8);

                // Odd-sized chunks are padded to an even boundary.
                if (size % 2 == 1 && stream.Position < stream.Length) stream.Position++;
            }
            else if (id == "data")
            {
                if (byteRate <= 0) return null;

                // A recording cut short by a crash carries a header claiming the length it
                // intended to reach, so the bytes actually present win.
                var length = Math.Min((long)size, stream.Length - stream.Position);

                return TimeSpan.FromSeconds((double)length / byteRate);
            }
            else
            {
                var skip = (long)size + (size % 2);
                if (stream.Position + skip > stream.Length) return null;

                stream.Position += skip;
            }
        }

        return null;
    }

    /// <summary>
    /// The granule position of the last page, which for Opus counts samples at 48 kHz whatever
    /// the audio was encoded from.
    /// </summary>
    private static TimeSpan? FromOgg(string path)
    {
        const int OggSampleRate = 48_000;
        const int TailBytes = 64 * 1024;

        using var stream = File.OpenRead(path);

        var take = (int)Math.Min(TailBytes, stream.Length);
        if (take < 27) return null;

        stream.Seek(-take, SeekOrigin.End);

        var buffer = new byte[take];
        stream.ReadExactly(buffer);

        for (var i = buffer.Length - 27; i >= 0; i--)
        {
            if (buffer[i] != (byte)'O' || buffer[i + 1] != (byte)'g'
                || buffer[i + 2] != (byte)'g' || buffer[i + 3] != (byte)'S')
            {
                continue;
            }

            var granule = BitConverter.ToInt64(buffer, i + 6);

            // -1 is the page saying "no packet ends here", which the last page never is; a
            // negative anything else is a file this is not going to reason about.
            if (granule <= 0) return null;

            return TimeSpan.FromSeconds((double)granule / OggSampleRate);
        }

        return null;
    }

    private static string Tag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
