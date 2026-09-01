namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Reads 16-bit PCM samples out of a WAV file.
///
/// Written out rather than taken from a library because the recorder and the worker both write
/// these files, the format is fixed and small, and the alternative is dragging an audio stack
/// into a project that otherwise has none — which would also put it into the test suite, where
/// this has to run on a machine with no sound hardware at all.
///
/// Chunks are walked rather than assumed to be in a particular order. A LIST chunk between fmt
/// and data is legal and common, and reading past it as though it were audio would shift every
/// sample: the recording would still play, at the wrong speed, with a burst of noise at the
/// front. That is the kind of fault that gets diagnosed as a broken microphone.
/// </summary>
public sealed class PcmReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private long _position;

    private PcmReader(FileStream stream, long dataStart, long dataLength, AudioFormat format)
    {
        _stream = stream;
        _dataStart = dataStart;
        _dataLength = dataLength;

        Format = format;
        _stream.Position = dataStart;
    }

    public AudioFormat Format { get; }

    /// <summary>Whole sample frames in the file. One frame is one sample per channel.</summary>
    public long Frames => Format.BytesPerFrame > 0 ? _dataLength / Format.BytesPerFrame : 0;

    public static PcmReader Open(string path)
    {
        // A compressed recording is decoded into the cache first; the reader never knows.
        var stream = File.OpenRead(AudioMaterialiser.EnsurePcm(path)!);

        try
        {
            var (start, length, format) = ReadHeader(stream);

            if (length <= 0 || format.Channels <= 0)
                throw new InvalidDataException($"Ses verisi yok: {path}");

            return new PcmReader(stream, start, length, format);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Fills the span with samples and returns how many were available.</summary>
    public int Read(Span<short> destination)
    {
        var remaining = _dataLength - _position;
        if (remaining <= 0 || destination.IsEmpty) return 0;

        var wanted = Math.Min(destination.Length * sizeof(short), remaining);

        // Truncated to whole samples: a file cut off mid-sample would otherwise have its last
        // byte read as the low half of a sample and produce a click.
        wanted -= wanted % sizeof(short);
        if (wanted <= 0) return 0;

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
            destination[..(int)(wanted / sizeof(short))]);

        var read = _stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        _position += read;

        return read / sizeof(short);
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Walks the RIFF chunks and returns where the audio starts, how long it is, and its shape.</summary>
    internal static (long DataStart, long DataLength, AudioFormat Format) ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);

        if (header[..4] is not [0x52, 0x49, 0x46, 0x46]) // "RIFF"
            throw new InvalidDataException("RIFF başlığı yok.");

        var format = new AudioFormat(16_000, 1, 16);
        long dataStart = 0;
        long dataLength = 0;

        Span<byte> chunk = stackalloc byte[8];

        while (stream.Position < stream.Length - 8)
        {
            stream.ReadExactly(chunk);

            var id = System.Text.Encoding.ASCII.GetString(chunk[..4]);
            var size = BitConverter.ToUInt32(chunk[4..]);

            if (id == "fmt ")
            {
                var body = new byte[Math.Min(size, 16)];
                stream.ReadExactly(body);

                format = new AudioFormat(
                    SampleRate: BitConverter.ToInt32(body, 4),
                    Channels: BitConverter.ToInt16(body, 2),
                    BitsPerSample: body.Length >= 16 ? BitConverter.ToInt16(body, 14) : 16);

                // Chunks are word-aligned and may carry extension bytes beyond the 16 read here.
                stream.Position += size - body.Length + (size % 2);
            }
            else if (id == "data")
            {
                dataStart = stream.Position;

                // The declared size is not trusted over the file itself. A recording the
                // application was killed during has a header claiming the length it intended to
                // write, and reading to it would run off the end.
                dataLength = Math.Min(size, stream.Length - dataStart);
                break;
            }
            else
            {
                stream.Position += size;

                // The pad byte is required by the specification and omitted by several writers
                // in the wild. Skipping it unconditionally would land one byte into the next
                // chunk id and lose the audio entirely, so it is only skipped when it is really
                // there: a pad byte is always zero, a chunk id never starts with one.
                if (size % 2 != 0 && stream.Position < stream.Length && stream.ReadByte() != 0)
                    stream.Position -= 1;
            }
        }

        return (dataStart, dataLength, format);
    }
}
