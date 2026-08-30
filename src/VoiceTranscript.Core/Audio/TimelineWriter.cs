namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Places captured PCM packets on a wall-clock timeline instead of concatenating them.
///
/// This exists because of a documented WASAPI behaviour that silently destroys dual-stream
/// recordings: a loopback capture client receives NO packets at all while nothing is being
/// rendered. Naively appending packets therefore produces a file whose duration equals the
/// amount of audible time, not elapsed time. Over a one-hour call where the far end speaks
/// roughly half the time, the loopback file ends up about 30 minutes shorter than the
/// microphone file, and every speaker attribution after the first silence is wrong.
///
/// The fix is to treat the QPC stamp on each packet as authoritative: compute where the packet
/// belongs on the timeline, and fill any hole with digital silence so that
/// frame index == elapsed time, independently for each stream.
/// </summary>
public sealed class TimelineWriter : IDisposable
{
    /// <summary>WASAPI reports QPC positions in 100-nanosecond units.</summary>
    private const long QpcUnitsPerSecond = 10_000_000;

    private readonly IPcmSink _sink;
    private readonly AudioFormat _format;
    private readonly long _maxFillFrames;
    private readonly long _driftToleranceFrames;

    private long? _anchorQpc;
    private long _framesWritten;
    private int _peakAmplitude;
    private bool _disposed;

    public TimelineWriter(
        IPcmSink sink,
        AudioFormat format,
        TimeSpan? maxSilenceFill = null,
        TimeSpan? driftTolerance = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _format = format;

        // A hole larger than this is not credible as real silence — it means the clock lied or
        // the device was reset. Re-anchor instead of writing hours of zeros.
        _maxFillFrames = format.TicksToFrames((maxSilenceFill ?? TimeSpan.FromMinutes(5)).Ticks);

        // Sub-packet jitter is normal and must not trigger a fill or trim on every packet.
        _driftToleranceFrames = Math.Max(
            1,
            format.TicksToFrames((driftTolerance ?? TimeSpan.FromMilliseconds(10)).Ticks));
    }

    /// <summary>Frames committed to the sink so far, including inserted silence.</summary>
    public long FramesWritten => _framesWritten;

    public TimeSpan Duration => _format.FramesToDuration(_framesWritten);

    public TimelineStats Stats { get; } = new();

    /// <summary>Positions one captured packet on the timeline.</summary>
    /// <param name="data">Raw PCM. Ignored when <see cref="CaptureFlags.Silent"/> is set.</param>
    /// <param name="frameCount">Number of frames the packet represents.</param>
    /// <param name="qpcPosition">Packet timestamp in 100 ns units, as handed over by WASAPI.</param>
    /// <param name="flags">Buffer flags reported alongside the packet.</param>
    public void Write(
        ReadOnlySpan<byte> data,
        int frameCount,
        long qpcPosition,
        CaptureFlags flags = CaptureFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frameCount <= 0) return;

        var timestampUsable = (flags & CaptureFlags.TimestampError) == 0;

        if ((flags & CaptureFlags.DataDiscontinuity) != 0)
        {
            // The device admits it lost data. The gap is real but its size is unknown, so trust
            // the new stamp and re-anchor rather than guessing how much silence to insert.
            Stats.Discontinuities++;
            if (timestampUsable) ReAnchor(qpcPosition);
        }
        else if (timestampUsable)
        {
            if (_anchorQpc is null)
                _anchorQpc = qpcPosition;
            else
                AlignTo(qpcPosition);
        }
        else
        {
            Stats.TimestampErrors++;
        }

        if ((flags & CaptureFlags.Silent) != 0)
        {
            // Microsoft contract: when this flag is set the buffer contents are undefined and
            // must be treated as silence, whatever the pointer happens to hold.
            _sink.WriteSilence(frameCount);
            Stats.SilentPackets++;
        }
        else
        {
            var expected = frameCount * _format.BytesPerFrame;
            if (data.Length < expected)
            {
                throw new ArgumentException(
                    $"Packet claims {frameCount} frames ({expected} bytes) but only {data.Length} bytes were supplied.",
                    nameof(data));
            }

            _sink.Write(data[..expected]);
            TrackPeak(data[..expected]);
        }

        _framesWritten += frameCount;
        Stats.PacketsWritten++;
    }

    /// <summary>
    /// Remembers the loudest sample this stream has carried.
    ///
    /// This answers the one question the packet counters cannot: whether anything was actually
    /// heard. A capture pointed at the wrong endpoint, a muted microphone and a per-process
    /// loopback that returns zero-filled buffers all deliver a full complement of packets on
    /// time, so every other statistic looks perfect while the file contains an hour of nothing.
    /// Finding that out after the conversation is too late, so it is measured while recording
    /// and reported when the call ends.
    /// </summary>
    private void TrackPeak(ReadOnlySpan<byte> pcm)
    {
        if (_format.BitsPerSample != 16) return;

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        var peak = _peakAmplitude;

        foreach (var sample in samples)
        {
            // short.MinValue has no positive counterpart; clamping it keeps the value in range.
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }

        _peakAmplitude = peak;
        Stats.PeakAmplitude = peak;
    }

    /// <summary>True once a packet has established where this stream starts on the timeline.</summary>
    public bool IsAnchored => _anchorQpc is not null;

    /// <summary>
    /// Anchors a stream that has not received any packet yet.
    ///
    /// Needed for the case where one side never makes a sound for the whole call — the far end
    /// listening in silence, or a capture that produced nothing. Without an anchor the stream
    /// cannot be padded, so it would be written as a zero-length file while the other side is an
    /// hour long, and every timestamp comparison between them would be meaningless. Anchoring it
    /// to the moment the other stream started instead yields a correct file of silence.
    /// </summary>
    public void AnchorAt(long qpcPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _anchorQpc ??= qpcPosition;
    }

    /// <summary>
    /// Pads the stream out to <paramref name="qpcPosition"/>, so two independently captured
    /// streams anchored at the same moment also end at the same length.
    /// </summary>
    public void PadTo(long qpcPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_anchorQpc is null) return;
        AlignTo(qpcPosition);
    }

    private void AlignTo(long qpcPosition)
    {
        var target = FrameIndexFor(qpcPosition);
        var delta = target - _framesWritten;

        if (delta > _driftToleranceFrames)
        {
            if (delta > _maxFillFrames)
            {
                // Implausible hole: a suspended machine, a device reset, or a bogus clock.
                // Filling it would produce an enormous file and still not align the streams.
                Stats.OversizedGaps++;
                ReAnchor(qpcPosition);
                return;
            }

            _sink.WriteSilence(delta);
            _framesWritten += delta;
            Stats.SilenceFramesInserted += delta;
            Stats.GapsFilled++;
        }
        else if (delta < -_driftToleranceFrames)
        {
            // The packet claims to start before where we already are. Audio cannot be un-written,
            // so the overlap is accepted and counted; a high count here means the clock source is
            // unreliable and the capture backend should be reconsidered.
            Stats.OverlapFrames += -delta;
            Stats.Overlaps++;
        }
    }

    private void ReAnchor(long qpcPosition)
    {
        // Redefine the origin so that "now" maps to the frames already committed.
        _anchorQpc = qpcPosition - (_framesWritten * QpcUnitsPerSecond / _format.SampleRate);
        Stats.ReAnchors++;
    }

    private long FrameIndexFor(long qpcPosition)
        => (qpcPosition - _anchorQpc!.Value) * _format.SampleRate / QpcUnitsPerSecond;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sink.Dispose();
    }
}

/// <summary>
/// Diagnostics for one captured stream. Surfaced after a call: a recording with a high overlap
/// or re-anchor count must not be trusted for speaker attribution.
/// </summary>
public sealed class TimelineStats
{
    public long PacketsWritten { get; set; }
    public long SilentPackets { get; set; }
    public long GapsFilled { get; set; }
    public long SilenceFramesInserted { get; set; }
    public long Overlaps { get; set; }
    public long OverlapFrames { get; set; }
    public long Discontinuities { get; set; }
    public long ReAnchors { get; set; }
    public long OversizedGaps { get; set; }
    public long TimestampErrors { get; set; }

    /// <summary>Loudest sample seen, 0-32767. Zero means the stream carried pure digital silence.</summary>
    public int PeakAmplitude { get; set; }

    /// <summary>
    /// Whether this stream carried something worth transcribing.
    ///
    /// The threshold is roughly -40 dBFS. Below it there is nothing a listener would call sound:
    /// a live but silent microphone still shows a noise floor of a few hundred units, whereas a
    /// broken capture path shows single digits or an exact zero.
    /// </summary>
    public bool CarriedAudio => PeakAmplitude >= 327;

    /// <summary>True when nothing happened that would misalign the two streams.</summary>
    public bool IsClean =>
        Overlaps == 0 && Discontinuities == 0 && OversizedGaps == 0 && TimestampErrors == 0;

    public override string ToString() =>
        $"packets={PacketsWritten} silent={SilentPackets} gaps={GapsFilled} " +
        $"filled={SilenceFramesInserted}f overlaps={Overlaps}({OverlapFrames}f) " +
        $"disc={Discontinuities} reanchor={ReAnchors} oversized={OversizedGaps} tsErr={TimestampErrors} " +
        $"peak={PeakAmplitude}";
}
