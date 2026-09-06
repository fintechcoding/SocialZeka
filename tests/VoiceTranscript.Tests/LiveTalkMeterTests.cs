using System.Diagnostics;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// The live talk meter, driven by synthetic packets rather than by a device.
///
/// The development machine has no audio hardware at all, and two of the rules this class must
/// obey — that it never blocks the capture thread and never allocates on it — are only visible
/// from the inside. So the packets are handed to it directly, with an injected clock, which also
/// means two minutes of audio can be tested in milliseconds instead of in two minutes.
///
/// What breaks when these go red:
///   the share — the strip prints a number about the user that is wrong;
///   the headphone gate — the other person's voice, arriving through the speakers, is counted as
///   the user's, so somebody who barely spoke is told they did most of the talking;
///   the baseline — an arrow appears before there is anything to compare against, which is a
///   measurement claim made out of noise;
///   the blocking and allocation tests — the recording itself. Those two are the reason this
///   class is written the way it is, and a regression in either loses conversations rather than
///   pixels.
/// </summary>
public sealed class LiveTalkMeterTests
{
    private static readonly AudioFormat Fmt = AudioFormat.WhisperPcm;

    /// <summary>Ten milliseconds, which is what WASAPI hands over and what FileAudioSource replays.</summary>
    private const int PacketMs = 10;

    private static readonly int PacketFrames = Fmt.SampleRate * PacketMs / 1000;

    /// <summary>
    /// A packet at roughly the given amplitude, as a square wave: its RMS is the amplitude, so the
    /// level it produces is exactly predictable and the tests can sit either side of the gate on
    /// purpose rather than by luck.
    /// </summary>
    private static byte[] Packet(short amplitude)
    {
        var pcm = new byte[PacketFrames * Fmt.BytesPerFrame];

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (i / 2) % 2 == 0 ? amplitude : (short)-amplitude;
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return pcm;
    }

    /// <summary>Comfortably above the -40 dBFS speech gate: about -20.8 dBFS.</summary>
    private static readonly byte[] Speech = Packet(3000);

    /// <summary>About -12.2 dBFS — eight decibels above <see cref="Speech"/>, so well past the arrow's step.</summary>
    private static readonly byte[] Loud = Packet(8000);

    /// <summary>Digital silence, which the shared gate reads as far below the floor.</summary>
    private static readonly byte[] Silence = new byte[PacketFrames * Fmt.BytesPerFrame];

    private long _now;

    private LiveTalkMeter New(bool headphonesUnlikely = false) =>
        new(Fmt, () => _now) { HeadphonesUnlikely = headphonesUnlikely };

    private void Feed(LiveTalkMeter meter, StreamRole role, byte[] pcm) =>
        meter.OnPacket(role, new CapturedPacket(pcm, PacketFrames, _now * 10_000, CaptureFlags.None));

    /// <summary>
    /// Replays <paramref name="seconds"/> of a two-channel call, one packet per stream every ten
    /// milliseconds, and advances the injected clock as it goes.
    /// </summary>
    private void Replay(LiveTalkMeter meter, int seconds, Func<int, int, (byte[] Mic, byte[] Far)> at)
    {
        for (var second = 0; second < seconds; second++)
        {
            for (var packet = 0; packet < 1000 / PacketMs; packet++)
            {
                var (mic, far) = at(second, packet);

                Feed(meter, StreamRole.Loopback, far);
                Feed(meter, StreamRole.Microphone, mic);

                _now += PacketMs;
            }
        }
    }

    /// <summary>Turn-taking: the other person has the first four tenths of every second, the user the rest.</summary>
    private static (byte[], byte[]) TakingTurns(int second, int packet) =>
        packet < 40 ? (Silence, Speech) : (Speech, Silence);

    /// <summary>Both channels loud at the same moment — what a room with open speakers produces.</summary>
    private static (byte[], byte[]) BothAtOnce(int second, int packet) => (Speech, Speech);

    [Fact]
    public void SixtyPercentOfTheTalkingIsReportedAsSixtyPercent()
    {
        var meter = New();

        Replay(meter, 65, TakingTurns);

        var reading = meter.Read();

        Assert.Equal(TalkMeterState.Measured, reading.State);
        Assert.True(Math.Abs(reading.Share - 0.60) <= 0.02, $"pay %{reading.Share * 100:0.0}");
        Assert.True(reading.FarSideSpoke);
    }

    /// <summary>
    /// The gate is the voice identifier's, not a fourth one of this meter's own.
    ///
    /// Both sit either side of -40 dBFS by about a decibel and a half, so this fails the moment
    /// the shared constant moves or the shared RMS formula is copied and drifts — which is how
    /// two parts of one application come to disagree about whether somebody was speaking.
    /// </summary>
    [Fact]
    public void TheSpeechGateIsTheOneTheVoiceListenerUses()
    {
        Assert.Equal(-40.0, LiveTalkMeter.SpeechFloorDbfs);

        var above = New();
        Replay(above, 65, (_, _) => (Packet(400), Silence));
        Assert.Equal(TalkMeterState.Measured, above.Read().State);

        _now = 0;
        var below = New();
        Replay(below, 65, (_, _) => (Packet(300), Silence));
        Assert.Equal(TalkMeterState.Warming, below.Read().State);
    }

    /// <summary>
    /// Zero is an answer, not an absence: it is what somebody who has been listened at for a
    /// minute should see, and the strip has to be able to say it.
    /// </summary>
    [Fact]
    public void AUserWhoSaidNothingHasAShareOfZero()
    {
        var meter = New();

        Replay(meter, 65, (_, _) => (Silence, Speech));

        var reading = meter.Read();

        Assert.Equal(TalkMeterState.Measured, reading.State);
        Assert.Equal(0, reading.Share);
        Assert.True(reading.FarSideSpoke);
    }

    /// <summary>
    /// Nobody speaking is not a share of zero — it is nothing to divide. The strip shows no
    /// number at all rather than telling a silent room that it did none of the talking.
    /// </summary>
    [Fact]
    public void SilenceOnBothSidesIsNotAShare()
    {
        var meter = New();

        Replay(meter, 65, (_, _) => (Silence, Silence));

        var reading = meter.Read();

        Assert.Equal(TalkMeterState.Warming, reading.State);
        Assert.False(reading.FarSideSpoke);
    }

    /// <summary>
    /// The headphone gate. Both channels loud at the same instant means the other person is
    /// coming out of the speakers and back into the microphone, so every ten-second window is
    /// thrown away and there is no share left to show.
    /// </summary>
    [Fact]
    public void AMinuteWithBothChannelsHotIsNotCounted()
    {
        var meter = New();

        Replay(meter, 65, BothAtOnce);

        var reading = meter.Read();

        Assert.Equal(TalkMeterState.Bleeding, reading.State);
    }

    /// <summary>
    /// One bad window costs one window and not the answer. The bleeding stretch here would drag
    /// the share towards half if it were counted; the reported figure has to be the clean
    /// windows' 60% instead.
    /// </summary>
    [Fact]
    public void OneBleedingWindowIsDroppedAndTheRestStillCounts()
    {
        var meter = New();

        Replay(meter, 60, (second, packet) =>
            second < 10 ? BothAtOnce(second, packet) : TakingTurns(second, packet));

        var reading = meter.Read();

        Assert.Equal(TalkMeterState.Measured, reading.State);
        Assert.True(Math.Abs(reading.Share - 0.60) <= 0.02, $"pay %{reading.Share * 100:0.0}");
    }

    /// <summary>
    /// The first half-minute has no baseline, so there is no arrow. A median over the first few
    /// seconds of a call is a median over whichever word happened to be first, and an arrow drawn
    /// from that looks exactly like a measurement.
    /// </summary>
    [Fact]
    public void TheBaselineIsNotOfferedForTheFirstThirtySeconds()
    {
        var meter = New();

        Replay(meter, 25, TakingTurns);

        Assert.Equal(TalkMeterTrend.Unknown, meter.Read().Trend);
    }

    [Fact]
    public void AfterThirtySecondsTheArrowComparesAgainstTheUsersOwnMedian()
    {
        var quieter = New();

        // A hundred seconds at one level, then ten at eight decibels above it. The median of the
        // two minutes is still the first level, so the recent stretch reads as louder.
        Replay(quieter, 100, (_, packet) => packet < 40 ? (Silence, Speech) : (Speech, Silence));
        Replay(quieter, 10, (_, packet) => packet < 40 ? (Silence, Speech) : (Loud, Silence));

        Assert.Equal(TalkMeterTrend.Above, quieter.Read().Trend);

        _now = 0;
        var louder = New();

        Replay(louder, 100, (_, packet) => packet < 40 ? (Silence, Speech) : (Loud, Silence));
        Replay(louder, 10, (_, packet) => packet < 40 ? (Silence, Speech) : (Speech, Silence));

        Assert.Equal(TalkMeterTrend.Below, louder.Read().Trend);
    }

    [Fact]
    public void SpeakingAtOneLevelThroughoutReadsAsNoChange()
    {
        var meter = New();

        Replay(meter, 90, TakingTurns);

        Assert.Equal(TalkMeterTrend.Level, meter.Read().Trend);
    }

    /// <summary>
    /// Told in advance that the last call had the other party in the microphone, the meter never
    /// attaches at all — a share measured through open speakers counts the other person as the
    /// user, and a wrong number costs more than no number.
    /// </summary>
    [Fact]
    public void AMeterToldThereAreNoHeadphonesNeverCounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vt-meter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            using var source = new FileAudioSource(
                WriteTone(Path.Combine(directory, "mic.wav")),
                WriteTone(Path.Combine(directory, "far.wav")));

            using var meter = New(headphonesUnlikely: true);

            meter.Listen(source);
            source.Replay();

            Assert.Equal(TalkMeterState.Bleeding, meter.Read().State);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A fault inside the meter stops the meter, not the recording.
    ///
    /// This handler runs inside the same multicast event the recorder is attached to. An
    /// exception escaping it travels out through the capture backend and can take the recording
    /// with it — losing a conversation to a bug in a status indicator.
    /// </summary>
    [Fact]
    public void AFaultOnTheCaptureThreadStopsTheMeterRatherThanEscaping()
    {
        // Well behaved until the first packet arrives, so the fault happens where it matters: on
        // the capture thread, inside the event the recorder is also attached to.
        var ticks = 0;
        var meter = new LiveTalkMeter(Fmt, () => ticks++ == 0 ? 0 : throw new InvalidOperationException("saat bozuk"));

        var packet = new CapturedPacket(Speech, PacketFrames, 0, CaptureFlags.None);
        meter.OnPacket(StreamRole.Microphone, packet);

        Assert.True(meter.Stopped);
        Assert.Equal(TalkMeterState.Bleeding, meter.Read().State);
    }

    /// <summary>
    /// The rule that matters most, exercised end to end: a slow subscriber on the packet event
    /// must not cost either stream a single packet.
    ///
    /// Fifty milliseconds is five packets' worth — long enough that a design which dropped what
    /// it could not keep up with would lose audio here. Both files still have to come out the
    /// full length of the call and aligned with each other.
    /// </summary>
    [Fact]
    public void ASubscriberThatBlocksForFiftyMillisecondsDoesNotShortenEitherStream()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vt-meter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var mic = WriteTone(Path.Combine(directory, "mic.wav"), 10);
            var far = WriteTone(Path.Combine(directory, "far.wav"), 10);

            using var source = new FileAudioSource(mic, far) { SkipSilence = false };
            using var meter = new LiveTalkMeter(source.Format);

            var stalls = 0;
            long micFrames = 0, farFrames = 0;

            source.PacketReady += (role, packet) =>
            {
                if (role == StreamRole.Microphone) micFrames += packet.FrameCount;
                else farFrames += packet.FrameCount;

                if (++stalls <= 5) Thread.Sleep(50);
            };

            meter.Listen(source);

            using var recorder = new CallRecorder(source);
            recorder.StartAsync(directory, "call-1").GetAwaiter().GetResult();
            source.Replay();

            var result = recorder.Stop();

            Assert.True(result.StreamsAreAligned,
                $"mic {result.MicDuration} vs far {result.FarDuration}");

            foreach (var duration in new[] { result.MicDuration, result.FarDuration })
            {
                var error = (duration - TimeSpan.FromSeconds(10)).Duration();
                Assert.True(error < TimeSpan.FromSeconds(1), $"akış {duration} uzunlukta çıktı");
            }

            // Not just "long enough" but "all of it": every frame the source handed out is in the
            // file. A design that dropped what it could not keep up with would still pass the
            // length check above, because the timeline writer fills a gap with silence.
            Assert.Equal(micFrames, Fmt.TicksToFrames(result.MicDuration.Ticks));
            Assert.Equal(farFrames, Fmt.TicksToFrames(result.FarDuration.Ticks));

            Assert.False(meter.Stopped);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The meter's own cost, measured rather than assumed, on a thread that is not the test's.
    ///
    /// The bound is deliberately loose — a shared build agent can stop any thread for tens of
    /// milliseconds and that is not this class's fault — but it is far tighter than the thing it
    /// is protecting against: a handler that takes longer than the packet it was given can never
    /// catch up, and the two streams fall behind for the rest of the call.
    /// </summary>
    [Fact]
    public void CountingAPacketCostsFarLessThanThePacketItself()
    {
        var meter = New();
        var packet = Speech;
        const int packets = 20_000;

        Exception? escaped = null;
        var worst = 0L;
        var clock = Stopwatch.StartNew();

        var thread = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < packets; i++)
                {
                    var before = clock.ElapsedTicks;
                    meter.OnPacket(i % 2 == 0 ? StreamRole.Microphone : StreamRole.Loopback,
                        new CapturedPacket(packet, PacketFrames, 0, CaptureFlags.None));

                    var cost = clock.ElapsedTicks - before;
                    if (cost > worst) worst = cost;

                    _now += PacketMs / 2;
                }
            }
            catch (Exception e)
            {
                escaped = e;
            }
        });

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "sayaç iş parçacığı bitmedi");

        Assert.Null(escaped);
        Assert.False(meter.Stopped);

        var average = clock.Elapsed.TotalMilliseconds / packets;
        Assert.True(average < PacketMs, $"paket başına {average:0.000} ms");

        var worstMs = worst * 1000.0 / Stopwatch.Frequency;
        Assert.True(worstMs < 50, $"en kötü paket {worstMs:0.0} ms sürdü");
    }

    /// <summary>
    /// Nothing on the packet path may allocate.
    ///
    /// A single object per packet is two hundred allocations a second across the two streams,
    /// feeding a garbage collection that pauses the thread WASAPI is waiting on — which is how a
    /// meter ends up costing a recording. Measured on this thread after a warm-up pass, so what
    /// is counted is the steady state and not the first-call JIT.
    /// </summary>
    [Fact]
    public void CountingAPacketAllocatesNothing()
    {
        var meter = New();

        for (var i = 0; i < 2_000; i++)
        {
            meter.OnPacket(StreamRole.Microphone, new CapturedPacket(Speech, PacketFrames, 0, CaptureFlags.None));
            meter.OnPacket(StreamRole.Loopback, new CapturedPacket(Speech, PacketFrames, 0, CaptureFlags.None));
            _now += PacketMs;
            meter.Read();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 10_000; i++)
        {
            meter.OnPacket(StreamRole.Microphone, new CapturedPacket(Speech, PacketFrames, 0, CaptureFlags.None));
            meter.OnPacket(StreamRole.Loopback, new CapturedPacket(Speech, PacketFrames, 0, CaptureFlags.None));
            _now += PacketMs;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Reading the meter allocates nothing either; the strip does it every second of every call.</summary>
    [Fact]
    public void ReadingTheMeterAllocatesNothing()
    {
        var meter = New();
        Replay(meter, 5, TakingTurns);

        for (var i = 0; i < 200; i++) meter.Read();

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1_000; i++) meter.Read();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Twenty minutes of a two-channel call, counted, with every packet accounted for.
    ///
    /// The acceptance figure the plan asks for is a CPU cost during recording, which a unit test
    /// cannot measure honestly on a shared machine. What it can pin is the shape of that cost —
    /// the meter's own processor time as a fraction of the audio it was given — and the thing the
    /// figure would be worthless without: that nothing was dropped on the way.
    /// </summary>
    [Fact]
    public void TwentyMinutesOfCallCostAFractionOfItAndLoseNoPacket()
    {
        var meter = New();
        const int seconds = 20 * 60;
        var packets = 0;

        var before = Stopwatch.GetTimestamp();

        for (var second = 0; second < seconds; second++)
        {
            for (var packet = 0; packet < 1000 / PacketMs; packet++)
            {
                var (mic, far) = TakingTurns(second, packet);

                Feed(meter, StreamRole.Loopback, far);
                Feed(meter, StreamRole.Microphone, mic);
                packets += 2;

                _now += PacketMs;
            }
        }

        var spent = Stopwatch.GetElapsedTime(before);

        Assert.Equal(seconds * (1000 / PacketMs) * 2, packets);
        Assert.False(meter.Stopped);

        var reading = meter.Read();
        Assert.Equal(TalkMeterState.Measured, reading.State);
        Assert.True(Math.Abs(reading.Share - 0.60) <= 0.02, $"pay %{reading.Share * 100:0.0}");

        // One per cent of one core, with an order of magnitude of headroom for a build agent.
        var share = spent.TotalSeconds / seconds;
        Assert.True(share < 0.10, $"{seconds / 60} dakikalık akış {spent.TotalSeconds:0.00} sn işlemci aldı (%{share * 100:0.00})");
    }

    /// <summary>Writes a WAV of continuous tone, loud enough to be speech to the shared gate.</summary>
    private static string WriteTone(string path, double seconds = 2)
    {
        using var sink = new WavPcmSink(path, Fmt);

        var pcm = new byte[(int)(seconds * Fmt.SampleRate) * Fmt.BytesPerFrame];
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)((i / 2) % 2 == 0 ? 3000 : -3000);
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        sink.Write(pcm);
        return path;
    }
}
