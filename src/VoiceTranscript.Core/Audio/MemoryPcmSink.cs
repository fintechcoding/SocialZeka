namespace VoiceTranscript.Core.Audio;

/// <summary>
/// In-memory PCM destination. Used by the tests and by the development-machine capture path,
/// which has no audio hardware at all.
/// </summary>
public sealed class MemoryPcmSink : IPcmSink
{
    private readonly MemoryStream _buffer = new();
    private readonly int _bytesPerFrame;

    public MemoryPcmSink(AudioFormat format) => _bytesPerFrame = format.BytesPerFrame;

    public long BytesWritten => _buffer.Length;
    public long FramesWritten => _buffer.Length / _bytesPerFrame;

    public void Write(ReadOnlySpan<byte> data) => _buffer.Write(data);

    public void WriteSilence(long frameCount)
    {
        var remaining = frameCount * _bytesPerFrame;

        // Chunked so that a multi-minute fill does not allocate a multi-megabyte scratch array.
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();

        while (remaining > 0)
        {
            var take = (int)Math.Min(zeros.Length, remaining);
            _buffer.Write(zeros[..take]);
            remaining -= take;
        }
    }

    public byte[] ToArray() => _buffer.ToArray();

    public void Dispose() => _buffer.Dispose();
}
