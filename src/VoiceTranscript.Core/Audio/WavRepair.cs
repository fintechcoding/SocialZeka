using System.Buffers.Binary;

namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Finishes the header of a recording the process did not live to finish.
///
/// WavPcmSink writes its header with a zero-length data chunk and patches the real length in
/// when it is closed. A crash or a power cut in the middle of a call skips the patch, so the
/// file on disk holds every sample that was captured and a header that says it holds none.
/// Every reader then agrees the recording is empty, and the only copy of that conversation is
/// treated as if it had never happened.
///
/// The true length is not lost: it is the file's length minus the header. This puts it back.
/// </summary>
public static class WavRepair
{
    private const int HeaderBytes = 44;

    /// <summary>
    /// Repairs the header when it is stale and reports how long the audio is either way.
    /// Null when the file is not a PCM WAV this code knows how to read, or holds no samples.
    /// </summary>
    public static TimeSpan? Finalise(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length <= HeaderBytes) return null;

            Span<byte> header = stackalloc byte[HeaderBytes];
            stream.ReadExactly(header);

            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) return null;
            if (!header[12..16].SequenceEqual("fmt "u8) || !header[36..40].SequenceEqual("data"u8)) return null;

            var bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
            if (bytesPerSecond == 0) return null;

            var actualData = stream.Length - HeaderBytes;
            var declaredData = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);

            // A header that already agrees with the file is left alone: a finished recording is
            // never rewritten, however many times this runs over it.
            if (declaredData != actualData && actualData <= uint.MaxValue)
            {
                Span<byte> patch = stackalloc byte[4];

                stream.Position = 4;
                BinaryPrimitives.WriteUInt32LittleEndian(patch, (uint)(HeaderBytes - 8 + actualData));
                stream.Write(patch);

                stream.Position = 40;
                BinaryPrimitives.WriteUInt32LittleEndian(patch, (uint)actualData);
                stream.Write(patch);

                stream.Flush();
            }

            return TimeSpan.FromSeconds((double)actualData / bytesPerSecond);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
