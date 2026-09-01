using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// A crash mid-recording leaves a WAV whose header says it is empty while the samples sit
/// behind it. The header is the only thing that is wrong, and it is what every reader trusts.
/// </summary>
public sealed class WavRepairTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-{Guid.NewGuid():N}.wav");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Writes a header claiming no data, then the samples a crash would have left behind.</summary>
    private void WriteCrashed(int seconds)
    {
        var format = AudioFormat.WhisperPcm;

        using (var stream = File.Create(_path))
        {
            // Not disposing the sink is the point: Dispose is what patches the length in, and a
            // process that died never reached it. Flushing through a non-seekable view keeps the
            // zero-length header the sink wrote first.
            var sink = new WavPcmSink(stream, format);
            sink.Write(new byte[format.BytesPerSecond * seconds]);
            stream.Flush();
        }

        // The sink above was never disposed, so the header still reads zero. Make that explicit
        // rather than rely on it: overwrite the data-length field with what the crash left.
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 40;
            stream.Write(new byte[4]);
        }
    }

    [Fact]
    public void ARecordingWithAStaleHeaderGetsItsLengthBack()
    {
        WriteCrashed(seconds: 3);

        var length = WavRepair.Finalise(_path);

        Assert.NotNull(length);
        Assert.Equal(3, length.Value.TotalSeconds, precision: 2);

        using var reader = PcmReader.Open(_path);
        Assert.Equal(AudioFormat.WhisperPcm.SampleRate * 3, reader.Frames);
    }

    [Fact]
    public void AFinishedRecordingIsLeftAlone()
    {
        var format = AudioFormat.WhisperPcm;
        using (var sink = new WavPcmSink(_path, format))
            sink.Write(new byte[format.BytesPerSecond * 2]);

        var before = File.ReadAllBytes(_path);
        var length = WavRepair.Finalise(_path);

        Assert.NotNull(length);
        Assert.Equal(2, length.Value.TotalSeconds, precision: 2);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void AFileWithNoSamplesIsNotARecording()
    {
        using (var sink = new WavPcmSink(_path, AudioFormat.WhisperPcm)) { }

        Assert.Null(WavRepair.Finalise(_path));
    }

    [Fact]
    public void SomethingThatIsNotAWavIsRefused()
    {
        File.WriteAllBytes(_path, new byte[500]);

        Assert.Null(WavRepair.Finalise(_path));
    }
}
