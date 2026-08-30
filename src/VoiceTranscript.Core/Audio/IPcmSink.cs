namespace VoiceTranscript.Core.Audio;

/// <summary>Destination for the aligned PCM byte stream produced by <see cref="TimelineWriter"/>.</summary>
public interface IPcmSink : IDisposable
{
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Append <paramref name="frameCount"/> frames of digital silence.</summary>
    void WriteSilence(long frameCount);
}
