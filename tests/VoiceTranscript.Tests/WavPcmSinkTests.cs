using System.Buffers.Binary;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

public sealed class WavPcmSinkTests : IDisposable
{
    private static readonly AudioFormat Fmt = AudioFormat.WhisperPcm;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-{Guid.NewGuid():N}.wav");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static byte[] ReadAll(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] Tone(int frames) =>
        [.. Enumerable.Range(0, frames * Fmt.BytesPerFrame).Select(i => (byte)(i % 251 + 1))];

    private static (int channels, int sampleRate, int bits, uint dataBytes) ReadHeader(string path)
    {
        // ReadWrite sharing, because some of these tests read the file while the sink still has
        // it open for writing — which is also what a player or a live waveform would do.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[Math.Min(stream.Length, 64)];
        stream.ReadExactly(bytes);

        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal("data"u8.ToArray(), bytes[36..40]);

        return (
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    [Fact]
    public void WritesAValidHeaderForTheWhisperFormat()
    {
        using (var sink = new WavPcmSink(_path, Fmt))
        {
            sink.Write(Tone(1600));
        }

        var (channels, sampleRate, bits, dataBytes) = ReadHeader(_path);

        Assert.Equal(1, channels);
        Assert.Equal(16_000, sampleRate);
        Assert.Equal(16, bits);
        Assert.Equal((uint)(1600 * 2), dataBytes);
    }

    [Fact]
    public void ReportsDurationFromWhatWasWritten()
    {
        using var sink = new WavPcmSink(_path, Fmt);

        sink.Write(Tone(Fmt.SampleRate));      // one second of audio
        sink.WriteSilence(Fmt.SampleRate / 2); // half a second of silence

        Assert.Equal(TimeSpan.FromSeconds(1.5), sink.Duration);
    }

    [Fact]
    public void SilenceIsWrittenAsActualZeroes()
    {
        using (var sink = new WavPcmSink(_path, Fmt))
        {
            sink.WriteSilence(100);
        }

        var bytes = ReadAll(_path);

        Assert.Equal(44 + 200, bytes.Length);
        Assert.All(bytes[44..], b => Assert.Equal(0, b));
    }

    /// <summary>
    /// A long silence must not be turned into a huge temporary allocation, since a far-end
    /// stream can legitimately be quiet for minutes.
    /// </summary>
    [Fact]
    public void LargeSilenceGapsAreWrittenWithoutABigAllocation()
    {
        using var sink = new WavPcmSink(_path, Fmt);

        sink.WriteSilence(Fmt.SampleRate * 300); // five minutes

        Assert.Equal(TimeSpan.FromMinutes(5), sink.Duration);
    }

    /// <summary>
    /// The recorder can be killed mid-call — a closing lid, a crash, a forced shutdown. A file
    /// checkpointed along the way must still play back everything written up to that point.
    /// </summary>
    [Fact]
    public void CheckpointLeavesAPlayableFileEvenIfWritingNeverFinishes()
    {
        var sink = new WavPcmSink(_path, Fmt);
        try
        {
            sink.Write(Tone(8000));
            sink.Checkpoint();

            var (_, _, _, dataBytes) = ReadHeader(_path);
            Assert.Equal((uint)(8000 * 2), dataBytes);

            // Writing continues from the right place after the header was patched.
            sink.Write(Tone(8000));
        }
        finally
        {
            sink.Dispose();
        }

        var (_, _, _, finalBytes) = ReadHeader(_path);
        Assert.Equal((uint)(16000 * 2), finalBytes);
        Assert.Equal(44 + 16000 * 2, new FileInfo(_path).Length);
    }

    /// <summary>Recovers a recording whose writer died before it could patch the header.</summary>
    [Fact]
    public void TryRepairRebuildsLengthsFromTheFileOnDisk()
    {
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.Write))
        {
            var sink = new WavPcmSink(stream, Fmt);
            sink.Write(Tone(4000));
            // Deliberately not disposed: this is the killed-process case, header still says zero.
            stream.Flush();
        }

        Assert.Equal(0u, ReadHeader(_path).dataBytes);

        Assert.True(WavPcmSink.TryRepair(_path));
        Assert.Equal((uint)(4000 * 2), ReadHeader(_path).dataBytes);
    }

    [Fact]
    public void TryRepairRefusesFilesThatAreNotWav()
    {
        File.WriteAllBytes(_path, new byte[200]);

        Assert.False(WavPcmSink.TryRepair(_path));
    }

    [Fact]
    public void TryRepairRefusesAnEmptyFile()
    {
        File.WriteAllBytes(_path, []);

        Assert.False(WavPcmSink.TryRepair(_path));
    }

    /// <summary>
    /// The end-to-end property: a stream that goes silent still produces a file as long as the
    /// call, so the two recordings line up.
    /// </summary>
    [Fact]
    public void TimelineWriterAndWavSinkTogetherProduceAFullLengthRecording()
    {
        using (var sink = new WavPcmSink(_path, Fmt))
        using (var writer = new TimelineWriter(sink, Fmt))
        {
            var packet = Tone(160); // 10 ms

            // One second of speech, then nine seconds during which the device sends nothing.
            for (var i = 0; i < 100; i++)
                writer.Write(packet, 160, i * 100_000L);

            writer.PadTo(10 * 10_000_000L);

            Assert.Equal(TimeSpan.FromSeconds(10), sink.Duration);
        }

        var (_, _, _, dataBytes) = ReadHeader(_path);
        Assert.Equal((uint)(Fmt.SampleRate * 10 * 2), dataBytes);
    }
}
