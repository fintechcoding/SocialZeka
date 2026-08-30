using System.Buffers.Binary;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// Putting the two sides of a call back into one recording.
///
/// Worth testing carefully despite being arithmetic, because every failure mode here is silent.
/// A mix that drops the second half still plays; a mix built from streams of different lengths
/// still plays; a mix that reads the RIFF header wrong still plays, at the wrong speed. None of
/// them raise anything. The user finds out by listening to a conversation that is missing the
/// part they wanted.
/// </summary>
public class ConversationMixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-mix-{Guid.NewGuid():N}");

    public ConversationMixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder is swept anyway.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a mono 16-bit WAV of a constant level, optionally with a junk chunk first.</summary>
    private string WriteWav(string name, short level, int frames, bool withListChunk = false)
    {
        var path = Path.Combine(_root, name);
        var data = new byte[frames * 2];

        for (var i = 0; i < frames; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), level);

        // An odd-sized chunk on purpose: the pad byte after it is the detail that decides whether
        // the audio is found at the right offset or one byte into it.
        var junk = withListChunk ? new byte[] { 1, 2, 3 } : [];

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write("RIFF"u8);
        writer.Write(36 + (withListChunk ? 8 + junk.Length + 1 : 0) + data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(16_000 * 2);
        writer.Write((short)2);
        writer.Write((short)16);

        if (withListChunk)
        {
            writer.Write("LIST"u8);
            writer.Write(junk.Length);
            writer.Write(junk);
            writer.Write((byte)0);
        }

        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);

        return path;
    }

    private static short[] SamplesOf(string path)
    {
        using var reader = PcmReader.Open(path);

        var samples = new short[reader.Frames];
        var read = reader.Read(samples);

        return samples[..read];
    }

    [Fact]
    public void BothVoicesEndUpInTheMixedRecording()
    {
        var mic = WriteWav("call-mic.wav", 1000, 400);
        var far = WriteWav("call-far.wav", 300, 400);

        var mixed = ConversationMix.Ensure(mic, far);

        Assert.NotNull(mixed);

        var samples = SamplesOf(mixed!);

        Assert.Equal(400, samples.Length);
        Assert.All(samples, s => Assert.Equal(1300, s));
    }

    [Fact]
    public void TheLongerSideIsKeptWhenOnePersonBarelySpoke()
    {
        // The microphone stops early on a call the user listened through. Truncating to the
        // shorter stream would delete exactly the part worth keeping.
        var mic = WriteWav("short-mic.wav", 500, 100);
        var far = WriteWav("short-far.wav", 500, 900);

        var mixed = ConversationMix.Ensure(mic, far);
        var samples = SamplesOf(mixed!);

        Assert.Equal(900, samples.Length);
        Assert.Equal(1000, samples[0]);

        // Past the end of the microphone only the other party remains — not silence, and not
        // the microphone's last sample held.
        Assert.Equal(500, samples[500]);
    }

    [Fact]
    public void LoudPassagesAreClampedRatherThanWrappedAround()
    {
        // Two loud voices at once exceed what a 16-bit sample can hold. Wrapping turns a peak
        // into a full-scale sample of the opposite sign, which is heard as a violent click.
        var mic = WriteWav("loud-mic.wav", 30000, 50);
        var far = WriteWav("loud-far.wav", 30000, 50);

        var mixed = ConversationMix.Ensure(mic, far);
        var samples = SamplesOf(mixed!);

        Assert.All(samples, s => Assert.Equal(short.MaxValue, s));
    }

    [Fact]
    public void AChunkBeforeTheAudioDoesNotShiftIt()
    {
        // A LIST chunk between fmt and data is legal and common. Reading past it as audio makes
        // the recording play at the wrong speed, which gets diagnosed as a broken microphone.
        var mic = WriteWav("chunked-mic.wav", 100, 200, withListChunk: true);
        var far = WriteWav("chunked-far.wav", 200, 200);

        var mixed = ConversationMix.Ensure(mic, far);
        var samples = SamplesOf(mixed!);

        Assert.Equal(200, samples.Length);
        Assert.All(samples, s => Assert.Equal(300, s));
    }

    [Fact]
    public void OneSideAloneStillProducesSomethingPlayable()
    {
        // A call where the microphone never opened. There is still a conversation to listen to.
        var far = WriteWav("only-far.wav", 700, 120);

        var mixed = ConversationMix.Ensure(null, far);

        Assert.NotNull(mixed);
        Assert.All(SamplesOf(mixed!), s => Assert.Equal(700, s));
    }

    [Fact]
    public void TheMixedCopyIsNotRebuiltWhenItIsAlreadyCurrent()
    {
        // Building it costs a full pass over both recordings. Doing that every time somebody
        // presses play would make an hour-long call take a visible moment to start, every time.
        var mic = WriteWav("cached-mic.wav", 100, 200);
        var far = WriteWav("cached-far.wav", 100, 200);

        var first = ConversationMix.Ensure(mic, far)!;
        var stamp = File.GetLastWriteTimeUtc(first);

        File.SetLastWriteTimeUtc(first, stamp.AddMinutes(5));

        var second = ConversationMix.Ensure(mic, far)!;

        Assert.Equal(first, second);
        Assert.Equal(stamp.AddMinutes(5), File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public void AMixLeftHalfWrittenIsReplacedRatherThanPlayed()
    {
        // What an interrupted build leaves behind. Trusting it means silently playing nothing,
        // for that call, forever.
        var mic = WriteWav("stale-mic.wav", 400, 150);
        var far = WriteWav("stale-far.wav", 400, 150);

        var path = ConversationMix.PathFor(mic);
        File.WriteAllBytes(path, new byte[10]);

        var rebuilt = ConversationMix.Ensure(mic, far);

        Assert.Equal(path, rebuilt);
        Assert.Equal(150, SamplesOf(path).Length);
    }

    [Fact]
    public void TheMixedCopyIsNamedAfterTheCallRatherThanTheMicrophone()
    {
        // "call-mic-butun.wav" would read as a mixdown of the microphone alone.
        var path = ConversationMix.PathFor(Path.Combine(_root, "2026-08-30-0400-mic.wav"));

        Assert.Equal(Path.Combine(_root, "2026-08-30-0400-butun.wav"), path);
    }

    [Fact]
    public void StreamsThatDisagreeAboutSampleRateAreRefused()
    {
        // Mixing them would shift one side against the other, and a mixed file that drifts is
        // worse than none: it sounds authoritative while putting words in the wrong mouth.
        var mic = WriteWav("rate-mic.wav", 100, 100);
        var far = Path.Combine(_root, "rate-far.wav");

        using (var file = File.Create(far))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + 200);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(48_000);
            writer.Write(48_000 * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(200);
            writer.Write(new byte[200]);
        }

        Assert.False(ConversationMix.Build(mic, far, Path.Combine(_root, "refused.wav")));
    }
}
