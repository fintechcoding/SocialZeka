namespace VoiceTranscript.Core.Audio;

/// <summary>Which side of the conversation a stream carries.</summary>
public enum StreamRole
{
    /// <summary>The microphone. This is the user.</summary>
    Microphone,

    /// <summary>What the application sends to the speakers. This is the other party.</summary>
    Loopback,
}

/// <summary>One packet as handed over by the audio stack.</summary>
public readonly ref struct CapturedPacket(
    ReadOnlySpan<byte> data,
    int frameCount,
    long qpcPosition,
    CaptureFlags flags)
{
    public ReadOnlySpan<byte> Data { get; } = data;
    public int FrameCount { get; } = frameCount;

    /// <summary>Timestamp in 100 ns units. What keeps the two streams on one timeline.</summary>
    public long QpcPosition { get; } = qpcPosition;

    public CaptureFlags Flags { get; } = flags;
}

public delegate void PacketHandler(StreamRole role, CapturedPacket packet);

/// <summary>
/// A source of the two audio streams a call consists of.
///
/// Three implementations exist and they are ranked, because the best one is not available
/// everywhere:
///
///   Device loopback — records whatever the default output endpoint is playing. Needs no driver
///   and no special permission, and its packets carry a real device clock. This is the default.
///   Its only cost is that it also captures anything else playing, which recording exclusively
///   while a call is up makes largely theoretical.
///
///   Process loopback — records one process tree in isolation. Cleaner in principle, but on
///   Windows 11 build 26200 this virtual device returns E_NOTIMPL for format negotiation,
///   reports a device position that is always zero, and hands out QPC values that are a packet
///   counter rather than a clock. It is offered, tested at startup, and used only if it works.
///
///   File — replays prepared WAV files. The development machine has no audio hardware at all,
///   so this is what makes the whole pipeline testable there.
///
/// Implementations must report packets for both roles with timestamps from the same clock.
/// Alignment itself is not their job; <see cref="TimelineWriter"/> does that.
/// </summary>
public interface IAudioCaptureBackend : IDisposable
{
    string Name { get; }

    /// <summary>Format the packets are delivered in.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Whether this backend isolates the target application rather than recording the whole
    /// output device. Shown in the UI so the user knows what ended up in the recording.
    /// </summary>
    bool IsProcessIsolated { get; }

    event PacketHandler? PacketReady;

    /// <summary>Raised when the audio device changes underneath us and capture had to restart.</summary>
    event EventHandler<string>? Interrupted;

    /// <summary>
    /// Begins capturing.
    ///
    /// Asynchronous because it genuinely is: WASAPI activates a process-loopback client through
    /// a completion callback, and asking the stream to follow the default device also defers
    /// activation. NAudio refuses a synchronous build in both cases rather than blocking, and
    /// blocking on it here would only move the deadlock somewhere harder to see.
    /// </summary>
    Task StartAsync(int? targetProcessId = null, CancellationToken cancellationToken = default);

    void Stop();
}
