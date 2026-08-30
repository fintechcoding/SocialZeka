namespace VoiceTranscript.Core.Audio;

/// <summary>Uncompressed PCM format description. Whisper wants 16 kHz mono 16-bit.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public static readonly AudioFormat WhisperPcm = new(16_000, 1, 16);

    public int BytesPerFrame => Channels * (BitsPerSample / 8);
    public int BytesPerSecond => SampleRate * BytesPerFrame;

    public long FramesToTicks(long frames) => frames * TimeSpan.TicksPerSecond / SampleRate;
    public long TicksToFrames(long ticks) => ticks * SampleRate / TimeSpan.TicksPerSecond;

    public TimeSpan FramesToDuration(long frames) => TimeSpan.FromTicks(FramesToTicks(frames));
}
