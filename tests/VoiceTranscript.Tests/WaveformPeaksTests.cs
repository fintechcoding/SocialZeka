using System.Buffers.Binary;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// Reading a recording into a shape somebody can look at.
///
/// The drawing has one job: say where in the call each person was talking. If it is wrong the
/// user clicks the wrong moment, hears the wrong thing, and stops trusting every quote in the
/// application — so the arithmetic is worth pinning down.
/// </summary>
public class WaveformPeaksTests
{
    private const int Rate = 16_000;

    /// <summary>Writes a 16-bit mono WAV from (seconds, amplitude) blocks.</summary>
    private static string WriteWav(string path, params (double Seconds, short Amplitude)[] blocks)
    {
        var samples = new List<short>();

        foreach (var (seconds, amplitude) in blocks)
        {
            var count = (int)(seconds * Rate);
            for (var i = 0; i < count; i++)
                samples.Add(i % 2 == 0 ? amplitude : (short)-amplitude);
        }

        var data = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);      // PCM
        writer.Write((short)1);      // mono
        writer.Write(Rate);
        writer.Write(Rate * 2);      // byte rate
        writer.Write((short)2);      // block align
        writer.Write((short)16);     // bits
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);

        return path;
    }

    private static string Temp(string name) =>
        Path.Combine(Path.GetTempPath(), $"vt-wave-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void SilenceReadsAsZeroAndSoundReadsAsLoud()
    {
        var path = Temp("halves.wav");

        try
        {
            // Ten seconds of silence, then ten seconds of near full scale.
            WriteWav(path, (10, 0), (10, 30000));

            var peaks = WaveformPeaks.Read(path, buckets: 100);

            Assert.Equal(100, peaks.Length);
            Assert.All(peaks.Take(45), p => Assert.True(p < 0.01f, $"beklenmedik ses: {p}"));
            Assert.All(peaks.Skip(55), p => Assert.True(p > 0.8f, $"beklenmedik sessizlik: {p}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePeakIsKeptRatherThanAveragedAway()
    {
        // Most of a spoken syllable is quiet, so an average flattens speech into a low even
        // smear and the drawing stops answering the only question it exists for.
        var path = Temp("burst.wav");

        try
        {
            // One second: a brief loud burst inside a lot of silence.
            WriteWav(path, (0.45, 0), (0.10, 32000), (0.45, 0));

            var peaks = WaveformPeaks.Read(path, buckets: 4);

            Assert.True(peaks.Max() > 0.9f, $"tepe kayboldu: {peaks.Max()}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AlwaysReturnsExactlyTheRequestedNumberOfBuckets()
    {
        // The drawing binds to a fixed-width strip, so a short answer would silently misalign
        // the playhead against the audio.
        var path = Temp("short.wav");

        try
        {
            WriteWav(path, (0.2, 12000));

            Assert.Equal(600, WaveformPeaks.Read(path).Length);
            Assert.Equal(37, WaveformPeaks.Read(path, buckets: 37).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileGivesAFlatLineRatherThanThrowing()
    {
        // A recording on a disconnected drive must not stop the transcript from opening.
        var peaks = WaveformPeaks.Read(Temp("nowhere.wav"), buckets: 50);

        Assert.Equal(50, peaks.Length);
        Assert.All(peaks, p => Assert.Equal(0, p));
    }

    [Fact]
    public void AFileThatIsNotAWavGivesAFlatLineRatherThanThrowing()
    {
        var path = Temp("garbage.wav");

        try
        {
            File.WriteAllText(path, "bu bir wav değil");

            var peaks = WaveformPeaks.Read(path, buckets: 20);

            Assert.Equal(20, peaks.Length);
            Assert.All(peaks, p => Assert.Equal(0, p));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AChunkBetweenTheFormatAndTheDataDoesNotShiftEverything()
    {
        // A LIST chunk between fmt and data is legal and common. Assuming a fixed 44-byte header
        // would read metadata as audio and put every peak in the wrong place — which is the kind
        // of bug that makes the playhead land seconds away from the quote.
        var path = Temp("withlist.wav");

        try
        {
            var samples = new byte[Rate * 2 * 2]; // two seconds
            for (var i = 0; i < samples.Length; i += 2)
                BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i), (short)(i % 4 == 0 ? 30000 : -30000));

            var note = "VoiceTranscript"u8.ToArray();

            using (var file = File.Create(path))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write("RIFF"u8);
                writer.Write(36 + note.Length + 1 + 8 + samples.Length);
                writer.Write("WAVE"u8);
                writer.Write("fmt "u8);
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(Rate);
                writer.Write(Rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write("LIST"u8);
                writer.Write(note.Length);
                writer.Write(note);
                writer.Write((byte)0); // RIFF pads an odd-sized chunk to a word boundary
                writer.Write("data"u8);
                writer.Write(samples.Length);
                writer.Write(samples);
            }

            var peaks = WaveformPeaks.Read(path, buckets: 20);

            Assert.All(peaks, p => Assert.True(p > 0.8f, $"veri yanlış yerden okundu: {p}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ZeroBucketsIsAnsweredWithNothingRatherThanADivideByZero()
    {
        Assert.Empty(WaveformPeaks.Read(Temp("any.wav"), buckets: 0));
    }
}
