using System.Text;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// How long a recording really is, asked of the file rather than of the database.
///
/// The database was wrong about it and nothing noticed, because nothing ever compared the two: a
/// step that rewrote both audio streams failed before it reached the row, and the recording
/// became 28 seconds shorter than the number beside it. The player scales its timeline to that
/// number, so every position in the conversation drifted by up to a seventh of its length.
///
/// This is what makes the disagreement visible. It has to answer without decoding — it is asked
/// about every recording in an archive at once — and it has to answer "I do not know" rather
/// than a wrong number, because a wrong number here silently rewrites a correct row.
/// </summary>
public class AudioLengthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-length-{Guid.NewGuid():N}");

    public AudioLengthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Nothing here holds a handle, but the directory can still be busy on Windows.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A 16 kHz mono 16-bit WAV, which is what this application records.</summary>
    private string Wav(string name, int samples, int? declaredDataSize = null)
    {
        var path = Path.Combine(_root, name);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        const int rate = 16_000;
        const short channels = 1;
        const short bits = 16;
        var byteRate = rate * channels * bits / 8;
        var data = samples * channels * bits / 8;

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + data);
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(byteRate);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);

        writer.Write("data"u8.ToArray());
        writer.Write(declaredDataSize ?? data);
        writer.Write(new byte[data]);

        return path;
    }

    /// <summary>An Ogg page header carrying a granule position, which is all this reads.</summary>
    private string Ogg(string name, long granule, int trailing = 0)
    {
        var path = Path.Combine(_root, name);

        var page = new byte[27 + trailing];
        "OggS"u8.CopyTo(page);
        BitConverter.GetBytes(granule).CopyTo(page, 6);

        File.WriteAllBytes(path, page);
        return path;
    }

    [Fact]
    public void AWavIsAsLongAsItsDataChunkSays()
    {
        var length = AudioLength.Of(Wav("bir.wav", samples: 16_000 * 5));

        Assert.NotNull(length);
        Assert.Equal(5, length.Value.TotalSeconds, 3);
    }

    /// <summary>
    /// A recording cut short by a crash keeps a header claiming the length it meant to reach.
    /// The bytes that are actually there are the truth.
    /// </summary>
    [Fact]
    public void AWavTruncatedAfterItsHeaderIsMeasuredByWhatIsThere()
    {
        var length = AudioLength.Of(Wav("kesik.wav", samples: 16_000, declaredDataSize: 16_000 * 2 * 60));

        Assert.NotNull(length);
        Assert.Equal(1, length.Value.TotalSeconds, 3);
    }

    /// <summary>Opus counts its granule at 48 kHz whatever the audio was encoded from.</summary>
    [Fact]
    public void AnOggIsAsLongAsItsLastGranulePosition()
    {
        var length = AudioLength.Of(Ogg("bir.ogg", granule: 48_000 * 200));

        Assert.NotNull(length);
        Assert.Equal(200, length.Value.TotalSeconds, 3);
    }

    /// <summary>The LAST page, not the first: everything before it is a partial count.</summary>
    [Fact]
    public void TheLastPageWins()
    {
        var path = Path.Combine(_root, "cok-sayfa.ogg");

        var first = new byte[27];
        "OggS"u8.CopyTo(first);
        BitConverter.GetBytes(48_000L * 10).CopyTo(first, 6);

        var last = new byte[27];
        "OggS"u8.CopyTo(last);
        BitConverter.GetBytes(48_000L * 175).CopyTo(last, 6);

        File.WriteAllBytes(path, [.. first, .. new byte[500], .. last]);

        Assert.Equal(175, AudioLength.Of(path)!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void AFileThatIsNotThereHasNoLength()
    {
        Assert.Null(AudioLength.Of(Path.Combine(_root, "yok.ogg")));
        Assert.Null(AudioLength.Of(null));
        Assert.Null(AudioLength.Of(""));
    }

    /// <summary>
    /// Silence rather than a guess. This answer overwrites a stored duration, so "I do not
    /// recognise this" has to be distinguishable from "it is zero seconds long".
    /// </summary>
    [Fact]
    public void AFormatItDoesNotReadHasNoLength()
    {
        var path = Path.Combine(_root, "baska.m4a");
        File.WriteAllBytes(path, new byte[4096]);

        Assert.Null(AudioLength.Of(path));
    }

    [Fact]
    public void AnOggWithNothingInItHasNoLength()
    {
        Assert.Null(AudioLength.Of(Ogg("bos.ogg", granule: 0)));
    }
}
