namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Finding the recordings whose archived audio is too poor to transcribe again.
///
/// The archive was written at 24 kbps for a while, chosen when it was only ever going to be
/// listened to. It is not only listened to: "Yeniden yazıya dök" decodes it back to PCM and
/// transcribes that, and 24 kbps is measurably on the wrong side of a cliff — the same recording
/// gave 1624 words at 21.5 kbps and 330 at 18.2, and Opus undershoots its target on speech with
/// pauses in it. Those files cannot be improved. What the encoder threw away is gone, and a second
/// attempt reads exactly what the first one did.
///
/// Measured rather than dated. A cut-off by timestamp would be a guess about which build wrote
/// which file, would be wrong for anybody who updated late, and could not be checked; the size of
/// the file against the length of the call is the thing itself. On a real archive the old files
/// measured 19–24 kbps and the new ones sit near 55, so nothing lands near the line.
/// </summary>
public static class DegradedAudio
{
    /// <summary>
    /// Below this, a second transcription is not worth attempting.
    ///
    /// Halfway between what the archive used to be written at and what it is written at now, so
    /// neither the undershoot of the old setting nor the variation of the new one reaches it.
    /// </summary>
    public const int QualityFloorBps = 40_000;

    /// <summary>Bits per second an archived file actually holds, or zero when it cannot be told.</summary>
    public static double BitrateOf(long bytes, TimeSpan duration)
    {
        if (bytes <= 0 || duration <= TimeSpan.Zero) return 0;

        return bytes * 8.0 / duration.TotalSeconds;
    }

    /// <summary>
    /// Whether this file is too compressed to transcribe again.
    ///
    /// A file whose length is unknown is not degraded. The answer here removes recordings
    /// permanently, so anything it cannot measure is left alone — the cost of keeping a bad
    /// recording is disk, and the cost of removing a good one is a conversation.
    /// </summary>
    public static bool IsDegraded(long bytes, TimeSpan duration)
    {
        var bitrate = BitrateOf(bytes, duration);

        return bitrate > 0 && bitrate < QualityFloorBps;
    }
}
