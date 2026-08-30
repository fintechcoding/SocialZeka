namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Mirrors the WASAPI AUDCLNT_BUFFERFLAGS_* bits we actually care about.
/// </summary>
[Flags]
public enum CaptureFlags
{
    None = 0,

    /// <summary>
    /// AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY — the device dropped data before this packet.
    /// The timeline must be re-anchored rather than silence-filled.
    /// </summary>
    DataDiscontinuity = 1,

    /// <summary>
    /// AUDCLNT_BUFFERFLAGS_SILENT — buffer contents must be treated as silence regardless of
    /// what the pointer actually holds.
    /// </summary>
    Silent = 2,

    /// <summary>
    /// AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR — the QPC stamp on this packet is not trustworthy,
    /// so it must not be used to position the packet.
    /// </summary>
    TimestampError = 4,
}
