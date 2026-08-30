using System.Buffers.Binary;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// Cutting the moment a thing was said out of a recording.
///
/// The failure that matters here is not a crash — it is a clip of the wrong moment. That plays
/// perfectly, sounds authoritative, and is evidence about something somebody did not say. So the
/// tests are about *which samples come out*, not about whether a file appears.
/// </summary>
public class AudioClipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-clip-{Guid.NewGuid():N}");

    private const int Rate = 16_000;

    public AudioClipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Writes ten seconds where every sample equals the second it falls in.
    ///
    /// That makes the extracted range self-describing: a sample of 4 can only have come from the
    /// fifth second, so a clip that starts in the wrong place is not merely detectable, it says
    /// exactly how far off it is.
    /// </summary>
    private string WriteRamp(string name, int seconds = 10, bool withListChunk = false)
    {
        var path = Path.Combine(_root, name);
        var frames = seconds * Rate;
        var data = new byte[frames * 2];

        for (var i = 0; i < frames; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(i / Rate));

        var junk = withListChunk ? new byte[] { 7, 7, 7 } : [];

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write("RIFF"u8);
        writer.Write(36 + (withListChunk ? 8 + junk.Length + 1 : 0) + data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
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
    public void TheClipStartsAndEndsWhereItWasAskedTo()
    {
        var source = WriteRamp("call.wav");
        var clip = Path.Combine(_root, "quote.wav");

        var ok = AudioClip.Extract(
            source, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), clip,
            padding: TimeSpan.Zero);

        Assert.True(ok);

        var samples = SamplesOf(clip);

        Assert.Equal(2 * Rate, samples.Length);
        Assert.Equal(4, samples[0]);
        Assert.Equal(5, samples[^1]);
    }

    [Fact]
    public void PaddingIsAddedAtBothEnds()
    {
        // A transcript segment starts at the first detected syllable. A clip beginning exactly
        // there sounds truncated and, worse, sounds edited — which is fatal for something whose
        // entire purpose is to be believed.
        var source = WriteRamp("call.wav");
        var clip = Path.Combine(_root, "padded.wav");

        AudioClip.Extract(
            source, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), clip,
            padding: TimeSpan.FromSeconds(2));

        var samples = SamplesOf(clip);

        Assert.Equal(5 * Rate, samples.Length);
        Assert.Equal(2, samples[0]);
        Assert.Equal(6, samples[^1]);
    }

    [Fact]
    public void PaddingDoesNotRunOffEitherEndOfTheRecording()
    {
        // A quote in the opening seconds asks for padding before zero, and one near the end asks
        // for padding past the file. Both are ordinary.
        var source = WriteRamp("call.wav", seconds: 10);

        var opening = Path.Combine(_root, "opening.wav");
        Assert.True(AudioClip.Extract(source, TimeSpan.Zero, TimeSpan.FromSeconds(1), opening));
        Assert.Equal(0, SamplesOf(opening)[0]);

        var closing = Path.Combine(_root, "closing.wav");
        Assert.True(AudioClip.Extract(
            source, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10), closing));

        Assert.Equal(9, SamplesOf(closing)[^1]);
    }

    [Fact]
    public void AChunkBeforeTheAudioDoesNotShiftTheClip()
    {
        // Seeking to a fixed offset would land in the wrong place and produce a clip of a
        // different moment — which plays perfectly and is evidence about something nobody said.
        var source = WriteRamp("chunked.wav", withListChunk: true);
        var clip = Path.Combine(_root, "chunked-quote.wav");

        AudioClip.Extract(
            source, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), clip,
            padding: TimeSpan.Zero);

        Assert.Equal(3, SamplesOf(clip)[0]);
    }

    [Fact]
    public void AMissingRecordingIsAnOrdinaryNo()
    {
        // The button sits beside a ledger entry whose audio may have been deleted under it.
        Assert.False(AudioClip.Extract(
            Path.Combine(_root, "gone.wav"), TimeSpan.Zero, TimeSpan.FromSeconds(1),
            Path.Combine(_root, "out.wav")));
    }

    [Fact]
    public void AnEmptyOrBackwardsRangeIsRefused()
    {
        var source = WriteRamp("call.wav");

        Assert.False(AudioClip.Extract(
            source, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            Path.Combine(_root, "empty.wav"), padding: TimeSpan.Zero));

        Assert.False(AudioClip.Extract(
            source, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2),
            Path.Combine(_root, "backwards.wav"), padding: TimeSpan.Zero));
    }

    [Fact]
    public void AQuoteBeyondTheEndOfTheRecordingIsRefused()
    {
        // A timestamp past the end means the transcript and the audio disagree. Writing a
        // zero-length clip would present that as a moment of silence somebody chose not to fill.
        var source = WriteRamp("call.wav", seconds: 5);

        Assert.False(AudioClip.Extract(
            source, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31),
            Path.Combine(_root, "beyond.wav")));
    }

    [Fact]
    public void TheFileNameCarriesNoContactName()
    {
        // These files are made to be sent to somebody. A name in the file name discloses who
        // else is in the archive to whoever receives it.
        var when = new DateTimeOffset(2026, 8, 26, 2, 15, 0, TimeSpan.Zero);

        var name = AudioClip.SuggestedName(when, TimeSpan.FromSeconds(754));

        // Matched by shape rather than against a fixed string: the name is in local time, so a
        // literal expectation would pass only in the timezone it was written in.
        Assert.Matches(@"^kesit-\d{4}-\d{2}-\d{2}-\d{4}-12dk34sn\.wav$", name);

        Assert.Equal($"kesit-{when.ToLocalTime():yyyy-MM-dd-HHmm}-12dk34sn.wav", name);
    }
}
