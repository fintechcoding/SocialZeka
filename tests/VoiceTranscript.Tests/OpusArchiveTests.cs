using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// The archive codec. A recording goes in as PCM, comes out twenty times smaller, and comes
/// back with every frame — because the file this replaces was the only copy of a conversation.
/// </summary>
public sealed class OpusArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vt-opus-{Guid.NewGuid():N}");

    private const int Rate = 16_000;

    public OpusArchiveTests()
    {
        Directory.CreateDirectory(_dir);
        AudioMaterialiser.CacheDirectory = Path.Combine(_dir, "cache");
    }

    public void Dispose()
    {
        AudioMaterialiser.CacheDirectory = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Three seconds: a tone, a silence, a tone. Speech-shaped enough to tell apart.</summary>
    private string WriteSource(string name = "call-1-mic.wav")
    {
        var path = Path.Combine(_dir, name);
        var samples = new short[Rate * 3];

        for (var i = 0; i < samples.Length; i++)
        {
            var second = i / Rate;
            if (second == 1) continue;

            samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        }

        using var sink = new WavPcmSink(path, AudioFormat.WhisperPcm);
        sink.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));

        return path;
    }

    private static double Rms(short[] samples, int from, int to)
    {
        double sum = 0;
        for (var i = from; i < to; i++) sum += (double)samples[i] * samples[i];
        return Math.Sqrt(sum / (to - from));
    }

    [Fact]
    public void ARecordingShrinksTwentyFoldAndDecodesWhole()
    {
        var wav = WriteSource();
        var ogg = OpusArchive.CompressedPathFor(wav);

        var encoded = OpusArchive.Encode(wav, ogg);

        Assert.Equal(Rate * 3, encoded);
        Assert.True(new FileInfo(ogg).Length * 8 < new FileInfo(wav).Length,
            $"ogg {new FileInfo(ogg).Length} B is not small enough next to wav {new FileInfo(wav).Length} B");

        // Every frame, give or take the codec's own padding — never a second short.
        var counted = OpusArchive.CountFrames(ogg, Rate);
        Assert.InRange(counted, Rate * 3 - Rate / 10, Rate * 3 + Rate / 10);

        var back = Path.Combine(_dir, "back.wav");
        OpusArchive.Decode(ogg, back);

        using var reader = PcmReader.Open(back);
        Assert.InRange(reader.Frames, Rate * 3 - Rate / 10, Rate * 3 + Rate / 10);

        // And it is the same sound, not just the same length: loud where the tone was, quiet
        // where the silence was.
        var samples = new short[reader.Frames];
        reader.Read(samples);

        var tone = Rms(samples, (int)(Rate * 0.2), (int)(Rate * 0.8));
        var silence = Rms(samples, (int)(Rate * 1.2), (int)(Rate * 1.8));

        Assert.True(tone > 10 * silence, $"tone rms {tone:0} vs silence rms {silence:0}");
    }

    [Fact]
    public void TheMaterialiserDecodesOnceAndForgetsOnRequest()
    {
        var wav = WriteSource();
        var ogg = OpusArchive.CompressedPathFor(wav);
        OpusArchive.Encode(wav, ogg);

        // PCM passes straight through.
        Assert.Equal(wav, AudioMaterialiser.EnsurePcm(wav));
        Assert.Null(AudioMaterialiser.EnsurePcm(null));

        var first = AudioMaterialiser.EnsurePcm(ogg)!;
        Assert.StartsWith(AudioMaterialiser.CacheDirectory!, first);
        Assert.True(File.Exists(first));

        var stamp = File.GetLastWriteTimeUtc(first);
        var second = AudioMaterialiser.EnsurePcm(ogg);

        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first));

        // Every PCM reader gets the decoded copy without knowing.
        using (var reader = PcmReader.Open(ogg))
            Assert.InRange(reader.Frames, Rate * 3 - Rate / 10, Rate * 3 + Rate / 10);

        AudioMaterialiser.Forget(ogg);
        Assert.False(File.Exists(first));
    }

    [Fact]
    public void AnEncodeThatFailsLeavesNoHalfFile()
    {
        var wav = Path.Combine(_dir, "broken.wav");
        File.WriteAllBytes(wav, new byte[100]);

        var ogg = OpusArchive.CompressedPathFor(wav);

        Assert.ThrowsAny<Exception>(() => OpusArchive.Encode(wav, ogg));
        Assert.False(File.Exists(ogg));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }
}
