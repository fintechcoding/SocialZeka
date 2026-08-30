using System.Buffers.Binary;

namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Writes a RIFF/WAVE file incrementally.
///
/// Hand-rolled rather than taken from an audio library for one reason: the sizes in a WAV header
/// are only known once writing has finished, and a recorder must survive being killed
/// mid-call — a laptop lid closing, a crash, a forced shutdown. This writes a provisional header
/// up front and patches the real lengths in on close, and it also exposes
/// <see cref="TryRepair"/> so a file whose header was never patched can still be recovered from
/// its actual size. Losing an hour of conversation to an un-patched four-byte field would be an
/// absurd way to fail.
/// </summary>
public sealed class WavPcmSink : IPcmSink
{
    private const int HeaderBytes = 44;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly AudioFormat _format;
    private long _dataBytes;
    private bool _disposed;

    public WavPcmSink(string path, AudioFormat format)
        : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024), format, ownsStream: true)
    {
        Path = path;
    }

    public WavPcmSink(Stream stream, AudioFormat format, bool ownsStream = false)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _format = format;

        WriteHeader(dataBytes: 0);
    }

    public string? Path { get; }

    public long DataBytes => _dataBytes;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)_dataBytes / _format.BytesPerSecond);

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stream.Write(data);
        _dataBytes += data.Length;
    }

    public void WriteSilence(long frameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frameCount <= 0) return;

        var remaining = frameCount * _format.BytesPerFrame;

        // Chunked: a multi-minute gap must not allocate a multi-megabyte scratch buffer.
        Span<byte> zeros = stackalloc byte[8192];
        zeros.Clear();

        while (remaining > 0)
        {
            var take = (int)Math.Min(zeros.Length, remaining);
            _stream.Write(zeros[..take]);
            _dataBytes += take;
            remaining -= take;
        }
    }

    /// <summary>
    /// Flushes to disk without closing.
    ///
    /// Called periodically during a long call so that a crash costs seconds rather than the
    /// whole recording. The header is patched each time too, so even a hard kill leaves a file
    /// that plays.
    /// </summary>
    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_stream.CanSeek)
        {
            _stream.Flush();
            return;
        }

        var position = _stream.Position;
        WriteHeader(_dataBytes);
        _stream.Position = position;
        _stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_stream.CanSeek) WriteHeader(_dataBytes);
            _stream.Flush();
        }
        finally
        {
            if (_ownsStream) _stream.Dispose();
        }
    }

    private void WriteHeader(long dataBytes)
    {
        if (_stream.CanSeek) _stream.Position = 0;

        Span<byte> header = stackalloc byte[HeaderBytes];
        var blockAlign = (short)_format.BytesPerFrame;

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(HeaderBytes - 8 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);

        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);      // PCM chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);       // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)_format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)_format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)_format.BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)_format.BitsPerSample);

        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);

        _stream.Write(header);
    }

    /// <summary>
    /// Rewrites the length fields of a WAV file from its actual size on disk.
    ///
    /// Recovers a recording whose writer was killed before it could finish — the audio is all
    /// there, only the header disagrees. Returns false if the file is too small or is not a WAV.
    /// </summary>
    public static bool TryRepair(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= HeaderBytes) return false;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) != 4 || !magic.SequenceEqual("RIFF"u8)) return false;

        var dataBytes = (uint)(info.Length - HeaderBytes);

        Span<byte> value = stackalloc byte[4];

        stream.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)(info.Length - 8));
        stream.Write(value);

        stream.Position = 40;
        BinaryPrimitives.WriteUInt32LittleEndian(value, dataBytes);
        stream.Write(value);

        return true;
    }
}
