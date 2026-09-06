using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.App.Services;

/// <summary>What the strip is allowed to say about the level, relative to the user's own baseline.</summary>
public enum TalkMeterTrend
{
    /// <summary>Not enough of the user's own speech yet, or the first thirty seconds.</summary>
    Unknown,

    /// <summary>Quieter than this person's own median over the last two minutes.</summary>
    Below,

    /// <summary>Within the plan's own smallest step of the baseline.</summary>
    Level,

    /// <summary>Louder than this person's own median over the last two minutes.</summary>
    Above,
}

/// <summary>Whether the reading means anything, and if not, why not.</summary>
public enum TalkMeterState
{
    /// <summary>Nobody has said enough yet. The strip shows nothing rather than a made-up number.</summary>
    Warming,

    /// <summary>
    /// The far side is coming out of the speakers and back into the microphone, so the share
    /// would count them as the user. Nothing measurable; the strip says so in one line.
    /// </summary>
    Bleeding,

    /// <summary>There is a share and it can be shown.</summary>
    Measured,
}

/// <summary>
/// One second's worth of answer, read off the counters without stopping them.
/// </summary>
/// <param name="State">Whether this reading means anything.</param>
/// <param name="Share">
/// The user's part of the speaking time over the last minute, 0-1. Zero is a real answer: it
/// means the other person did all the talking.
/// </param>
/// <param name="FarSideSpoke">Whether the other person was heard at all in the same minute.</param>
/// <param name="Trend">The user's level against their own baseline. Never a number.</param>
public readonly record struct TalkMeterReading(
    TalkMeterState State,
    double Share,
    bool FarSideSpoke,
    TalkMeterTrend Trend);

/// <summary>
/// How much of the last minute was the user talking, measured while the call is happening.
///
/// <b>Why this exists and why it says so little.</b> Everything else this application knows about
/// a conversation arrives after it is over, which is the wrong moment for the one fact a person
/// can still act on: that they have been talking for four minutes straight. A share of the last
/// sixty seconds is the only live figure here that is arithmetic rather than an opinion — it
/// needs no model, makes no claim about how somebody sounded, and cannot be wrong in a way that
/// insults them. That is the whole reason the tone rule allows it on screen at all.
///
/// It therefore has no voice. There is no alarm, no colour that means "too much", and no words
/// about how the user is speaking. A live warning is a separate thing behind a measurement that
/// has not been run (the plan's "isterdim ≥ %70" gate): until somebody has looked at their own
/// call and said they would have wanted to be interrupted, interrupting them is a guess.
///
/// <b>No dB ever reaches the screen.</b> Windows' communications pipeline gains, ducks and
/// noise-suppresses the microphone before this code sees a single sample, so a decibel figure
/// here describes what the operating system did as much as what the person did. A relative arrow
/// against the same person's own median minutes earlier survives that, because both sides of the
/// comparison went through the same processing. An absolute number would not.
///
/// <b>Nothing here may touch the recording.</b> This is a third subscriber to the capture
/// backend's multicast packet event, exactly as <see cref="SpeakerIdentifier"/> and
/// CaptureSelfTest are: it copies nothing, allocates nothing, takes no lock, and returns. The
/// recorder is another subscriber to the same event and runs on the same thread, so anything slow
/// or throwing in here stalls or breaks the recording of a conversation that cannot be had again.
/// Hence the counters are interlocked adds into arrays allocated once, and the handler's whole
/// body is wrapped: a fault stops the meter for the rest of the call rather than reaching the
/// recorder.
/// </summary>
public sealed class LiveTalkMeter : IDisposable
{
    /// <summary>
    /// The same speech gate the voice identifier uses, referenced rather than restated.
    ///
    /// A second threshold for the same question would drift from the first the moment either was
    /// tuned, and then two parts of the application would disagree about whether somebody was
    /// talking. There are three gates in this product already; this is not a fourth.
    /// </summary>
    public const double SpeechFloorDbfs = SpeakerIdentifier.SpeechFloorDbfs;

    /// <summary>Seconds of history kept. Two minutes, because the baseline is a two-minute median.</summary>
    private const int Buckets = 120;

    /// <summary>The share's window: the last minute, as the strip's wireframe says.</summary>
    private const int ShareSeconds = 60;

    /// <summary>
    /// How much of the past is judged for microphone bleed at a time.
    ///
    /// Ten seconds rather than the whole minute so that one moment of both people talking costs
    /// a sixth of the answer instead of all of it.
    /// </summary>
    private const int GateWindowSeconds = 10;

    /// <summary>
    /// Before the baseline means anything.
    ///
    /// A median over the first few seconds of a call is a median over whichever word happened to
    /// be first. The strip shows no arrow at all until this has passed — an arrow that is noise is
    /// worse than no arrow, because it looks like a measurement.
    /// </summary>
    private const long BaselineWarmupMs = 30_000;

    /// <summary>
    /// How far from the baseline counts as a different level.
    ///
    /// Three decibels is the plan's own hysteresis figure for the (unbuilt) live alarm. Reused
    /// here rather than picked, so the arrow and any later warning agree about what "changed"
    /// means, and because anything smaller is inside the processing the operating system applies
    /// to the microphone anyway.
    /// </summary>
    private const double TrendStepDb = 3.0;

    /// <summary>Level histogram bins: one decibel each, from the speech gate up to full scale.</summary>
    private const int Bins = 40;

    private readonly AudioFormat _format;
    private readonly Func<long> _clock;

    // Allocated once, in the constructor. Nothing in the packet path may allocate: a new object
    // per packet is a hundred per second per stream feeding a garbage collection that pauses the
    // thread WASAPI is waiting on.

    /// <summary>Which absolute second each bucket currently holds. Stale buckets read as empty.</summary>
    private readonly long[] _second = new long[Buckets];

    /// <summary>Frames of the user's own speech, per second.</summary>
    private readonly long[] _mine = new long[Buckets];

    /// <summary>Frames of the other person's speech, per second.</summary>
    private readonly long[] _theirs = new long[Buckets];

    /// <summary>Frames where both were above the gate at the same moment, per second.</summary>
    private readonly long[] _contested = new long[Buckets];

    /// <summary>Levels of the user's own speech frames: one histogram of <see cref="Bins"/> per second.</summary>
    private readonly long[] _levels = new long[Buckets * Bins];

    /// <summary>
    /// Through when the far channel was last known to be above the gate.
    ///
    /// A microphone frame that lands inside this is not attributable: it may be the user, or it
    /// may be the other person arriving through the speakers. Written by the loopback thread,
    /// read by the microphone thread, which is why it is a single interlocked word rather than
    /// anything that would need a lock between the two.
    /// </summary>
    private long _farHotThroughMs;

    private long _startedAtMs;
    private IAudioCaptureBackend? _backend;
    private volatile bool _stopped;

    /// <param name="format">The format packets arrive in; only the sample rate is used.</param>
    /// <param name="clock">
    /// Milliseconds since some fixed point. Defaults to the wall clock, deliberately: the packet's
    /// own QPC stamp is the obvious alternative and is wrong here, because the process-loopback
    /// backend hands out a packet counter rather than a clock (IAudioCaptureBackend says so), and
    /// a window that is supposed to be "the last sixty seconds" cannot be built on a number that
    /// is sometimes not time. Injectable so the tests can drive two minutes of audio in
    /// milliseconds instead of waiting for them.
    /// </param>
    public LiveTalkMeter(AudioFormat? format = null, Func<long>? clock = null)
    {
        _format = format ?? AudioFormat.WhisperPcm;
        _clock = clock ?? (() => Environment.TickCount64);
        _startedAtMs = _clock();
    }

    /// <summary>
    /// Known in advance that the other side is not on headphones, from the last call that was
    /// measured. The meter then never attaches at all — it would only produce a share that counts
    /// the other person as the user, and a wrong number costs more than no number.
    /// </summary>
    public bool HeadphonesUnlikely { get; init; }

    /// <summary>Whether the meter gave up. Something threw on the capture thread and was swallowed.</summary>
    public bool Stopped => _stopped;

    /// <summary>
    /// Starts counting.
    ///
    /// A second subscriber to a multicast event, so the capture chain is exactly as it was and
    /// detaching leaves it that way. Nothing is added to the recorder, to its level reporting, or
    /// to the detection loop.
    /// </summary>
    public void Listen(IAudioCaptureBackend backend)
    {
        if (HeadphonesUnlikely) return;

        _backend = backend;
        _startedAtMs = _clock();
        backend.PacketReady += OnPacket;
    }

    /// <summary>
    /// Counts one packet.
    ///
    /// Public because the tests drive it directly. The development machine has no audio hardware
    /// at all, and the rule this class must never break — that a slow or throwing subscriber
    /// cannot stall the two streams being written — is only testable by handing it packets.
    /// </summary>
    public void OnPacket(StreamRole role, CapturedPacket packet)
    {
        if (_stopped) return;

        try
        {
            Count(role, packet.Data, packet.FrameCount);
        }
        catch
        {
            // Swallowed on purpose, and the meter stops for the rest of the call.
            //
            // This handler runs inside the same event the recorder is attached to. An exception
            // escaping here would travel out through the capture backend's invocation of the
            // multicast delegate and could take the recording with it — losing a conversation to
            // a bug in a status indicator. A missing meter is a cosmetic fault; a missing
            // recording is the fault this whole application exists to prevent.
            _stopped = true;
        }
    }

    private void Count(StreamRole role, ReadOnlySpan<byte> pcm, int frameCount)
    {
        var now = _clock();
        var second = now / 1000;
        var slot = (int)(second % Buckets);

        Roll(slot, second);

        var frames = frameCount > 0 ? frameCount : pcm.Length / Math.Max(_format.BytesPerFrame, 1);
        var level = SpeakerIdentifier.Dbfs(pcm);

        if (level <= SpeechFloorDbfs) return;

        if (role == StreamRole.Loopback)
        {
            Interlocked.Add(ref _theirs[slot], frames);

            // Held open for one extra packet beyond the audio it covers. The two streams are
            // delivered by two threads and a packet of jitter between them is normal, so a
            // microphone frame that really was simultaneous can arrive a packet late.
            var through = now + 2L * frames * 1000 / _format.SampleRate;
            if (through > Interlocked.Read(ref _farHotThroughMs))
                Interlocked.Exchange(ref _farHotThroughMs, through);

            return;
        }

        Interlocked.Add(ref _mine[slot], frames);

        if (now <= Interlocked.Read(ref _farHotThroughMs))
            Interlocked.Add(ref _contested[slot], frames);

        // Weighted by duration rather than by packet, so a median over this is a median over
        // seconds of speech and not over however WASAPI happened to chop them up.
        var bin = (int)Math.Clamp(level - SpeechFloorDbfs, 0, Bins - 1);
        Interlocked.Add(ref _levels[slot * Bins + bin], frames);
    }

    /// <summary>
    /// Empties a bucket the first time a new second lands in it.
    ///
    /// Two capture threads can arrive on the same rollover; the compare-and-exchange picks one to
    /// do the clearing. The loser may add its frames a few nanoseconds before the winner zeroes
    /// them, losing at most one packet — ten milliseconds out of a sixty-second window — which is
    /// far cheaper than the lock that would prevent it, on a thread that is holding up audio.
    /// </summary>
    private void Roll(int slot, long second)
    {
        var held = Interlocked.Read(ref _second[slot]);
        if (held == second) return;

        if (Interlocked.CompareExchange(ref _second[slot], second, held) != held) return;

        Interlocked.Exchange(ref _mine[slot], 0);
        Interlocked.Exchange(ref _theirs[slot], 0);
        Interlocked.Exchange(ref _contested[slot], 0);
        Array.Clear(_levels, slot * Bins, Bins);
    }

    /// <summary>
    /// What to show right now.
    ///
    /// Called once a second from the strip's existing timer, on the UI thread. It reads the
    /// counters without stopping them, so a figure may be a packet out of date; over a
    /// sixty-second window that is invisible and it costs the capture thread nothing.
    /// </summary>
    public TalkMeterReading Read()
    {
        if (HeadphonesUnlikely || _stopped)
            return new TalkMeterReading(TalkMeterState.Bleeding, 0, false, TalkMeterTrend.Unknown);

        var now = _clock();
        var second = now / 1000;

        long mine = 0, theirs = 0;
        var dropped = 0;
        var counted = 0;

        // The last minute, judged ten seconds at a time.
        for (var window = 0; window < ShareSeconds / GateWindowSeconds; window++)
        {
            long windowMine = 0, windowTheirs = 0, windowContested = 0;

            for (var offset = 0; offset < GateWindowSeconds; offset++)
            {
                var wanted = second - (window * GateWindowSeconds + offset);
                if (wanted < 0) continue;

                var slot = (int)(wanted % Buckets);
                if (Interlocked.Read(ref _second[slot]) != wanted) continue;

                windowMine += Interlocked.Read(ref _mine[slot]);
                windowTheirs += Interlocked.Read(ref _theirs[slot]);
                windowContested += Interlocked.Read(ref _contested[slot]);
            }

            // The headphone gate. A whole second of the two channels being loud at the same
            // moment, inside ten, is the other person arriving through the speakers rather than
            // two people interrupting each other — so the share over this window would count them
            // as the user, and the window is thrown away instead. One second is the smallest
            // amount of simultaneity this meter can see, its buckets being one second wide;
            // anything shorter it declines to call anything.
            if (windowContested >= _format.SampleRate)
            {
                dropped++;
                continue;
            }

            counted++;
            mine += windowMine;
            theirs += windowTheirs;
        }

        if (counted == 0)
            return new TalkMeterReading(TalkMeterState.Bleeding, 0, false, TalkMeterTrend.Unknown);

        var spoken = mine + theirs;
        var trend = Trend(second, now);

        if (spoken == 0)
            return new TalkMeterReading(TalkMeterState.Warming, 0, false, trend);

        // Windows lost to bleed are not silently averaged away: if most of the minute could not
        // be judged, what is left is not a share of the minute and is not offered as one.
        if (dropped > counted)
            return new TalkMeterReading(TalkMeterState.Bleeding, 0, theirs > 0, trend);

        return new TalkMeterReading(TalkMeterState.Measured, (double)mine / spoken, theirs > 0, trend);
    }

    /// <summary>
    /// The user's level now against their own median over the last two minutes.
    ///
    /// Against themselves and never against anybody else: the gain of a microphone is a property
    /// of the hardware, so one person's decibels have no meaning next to another's — and the same
    /// person's have no meaning across two calls on two headsets either. Within one call, with one
    /// device and one pipeline, the comparison holds.
    /// </summary>
    private TalkMeterTrend Trend(long second, long now)
    {
        if (now - Volatile.Read(ref _startedAtMs) < BaselineWarmupMs) return TalkMeterTrend.Unknown;

        var baseline = Median(second, Buckets);
        var current = Median(second, GateWindowSeconds);

        if (baseline is null || current is null) return TalkMeterTrend.Unknown;

        var delta = current.Value - baseline.Value;

        if (delta > TrendStepDb) return TalkMeterTrend.Above;
        if (delta < -TrendStepDb) return TalkMeterTrend.Below;

        return TalkMeterTrend.Level;
    }

    /// <summary>
    /// Median level of the user's own speech frames over the last <paramref name="seconds"/>, or
    /// null when they did not speak in them.
    ///
    /// Median rather than mean because one door slamming into an open microphone moves a mean by
    /// several decibels and a median not at all.
    /// </summary>
    private double? Median(long second, int seconds)
    {
        Span<long> histogram = stackalloc long[Bins];
        long total = 0;

        for (var offset = 0; offset < seconds; offset++)
        {
            var wanted = second - offset;
            if (wanted < 0) break;

            var slot = (int)(wanted % Buckets);
            if (Interlocked.Read(ref _second[slot]) != wanted) continue;

            for (var bin = 0; bin < Bins; bin++)
            {
                var count = Interlocked.Read(ref _levels[slot * Bins + bin]);
                histogram[bin] += count;
                total += count;
            }
        }

        if (total == 0) return null;

        long seen = 0;
        for (var bin = 0; bin < Bins; bin++)
        {
            seen += histogram[bin];

            // The middle of the bin: the histogram is one decibel wide per bin, so this is
            // accurate to half a decibel, which is well inside what the arrow can distinguish.
            if (seen * 2 >= total) return SpeechFloorDbfs + bin + 0.5;
        }

        return null;
    }

    /// <summary>Detaches. The capture chain is left exactly as it was found.</summary>
    public void Dispose()
    {
        if (_backend is not null) _backend.PacketReady -= OnPacket;

        _backend = null;
        _stopped = true;
    }
}
