using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// These tests pin down the single behaviour that decides whether speaker attribution works:
/// a loopback stream that goes silent must still advance in time.
/// </summary>
public class TimelineWriterTests
{
    private static readonly AudioFormat Fmt = AudioFormat.WhisperPcm; // 16 kHz mono 16-bit

    private const long QpcPerSecond = 10_000_000;
    private const int PacketMs = 10;
    private static int PacketFrames => Fmt.SampleRate * PacketMs / 1000; // 160 frames

    /// <summary>Non-zero PCM, so inserted silence is distinguishable from real audio.</summary>
    private static byte[] Tone(int frames) =>
        [.. Enumerable.Range(0, frames * Fmt.BytesPerFrame).Select(i => (byte)(i % 251 + 1))];

    private static long QpcAt(TimeSpan t) => (long)(t.TotalSeconds * QpcPerSecond);

    [Fact]
    public void ContiguousPackets_ProduceExactDuration()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var packet = Tone(PacketFrames);

        for (var i = 0; i < 500; i++) // 5 seconds
            writer.Write(packet, PacketFrames, QpcAt(TimeSpan.FromMilliseconds(i * PacketMs)));

        Assert.Equal(TimeSpan.FromSeconds(5), writer.Duration);
        Assert.Equal(0, writer.Stats.GapsFilled);
        Assert.True(writer.Stats.IsClean, writer.Stats.ToString());
    }

    /// <summary>
    /// The regression this whole class exists for. A far-end stream that only delivers packets
    /// while somebody is speaking must still end up the full length of the call.
    /// </summary>
    [Fact]
    public void SilentStretches_AreFilled_SoStreamsStayAligned()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var packet = Tone(PacketFrames);

        // Far end talks 1 s, is quiet 4 s (no packets at all), talks 1 s.
        // Elapsed time is 6 s; a naive appender would produce a 2 s file.
        for (var i = 0; i < 100; i++)
            writer.Write(packet, PacketFrames, QpcAt(TimeSpan.FromMilliseconds(i * PacketMs)));

        for (var i = 0; i < 100; i++)
        {
            writer.Write(packet, PacketFrames,
                QpcAt(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(i * PacketMs)));
        }

        Assert.Equal(TimeSpan.FromSeconds(6), writer.Duration);
        Assert.Equal(1, writer.Stats.GapsFilled);
        Assert.Equal(4 * Fmt.SampleRate, writer.Stats.SilenceFramesInserted);
    }

    /// <summary>
    /// The realistic version, at the scale where the bug actually bites: a one-hour call in which
    /// the remote party speaks under half the time. Concatenation would lose over half an hour.
    /// </summary>
    [Fact]
    public void OneHourCall_WithMostlySilence_KeepsFullWallClockDuration()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var packet = Tone(PacketFrames);

        var elapsed = TimeSpan.Zero;
        var audible = TimeSpan.Zero;
        var oneHour = TimeSpan.FromHours(1);

        // Alternate 4.5 s of speech with 5.5 s of silence.
        while (elapsed < oneHour)
        {
            for (var i = 0; i < 450 && elapsed < oneHour; i++)
            {
                writer.Write(packet, PacketFrames, QpcAt(elapsed));
                elapsed += TimeSpan.FromMilliseconds(PacketMs);
                audible += TimeSpan.FromMilliseconds(PacketMs);
            }

            elapsed += TimeSpan.FromMilliseconds(5500); // silence: the device sends nothing
        }

        writer.PadTo(QpcAt(oneHour));

        Assert.True(audible < TimeSpan.FromMinutes(30),
            $"scenario must be mostly silent, audible was {audible}");

        var error = (writer.Duration - oneHour).Duration();
        Assert.True(error < TimeSpan.FromMilliseconds(20),
            $"expected ~1h, got {writer.Duration} (error {error})");
    }

    [Fact]
    public void SilentFlaggedPacket_WritesZeros_NotBufferContents()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);

        // Buffer is full of non-zero bytes, but the flag says its contents are undefined.
        writer.Write(Tone(PacketFrames), PacketFrames, QpcAt(TimeSpan.Zero), CaptureFlags.Silent);

        Assert.All(sink.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(1, writer.Stats.SilentPackets);
    }

    [Fact]
    public void DataDiscontinuity_ReAnchors_InsteadOfFillingAnUnknownGap()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var packet = Tone(PacketFrames);

        writer.Write(packet, PacketFrames, QpcAt(TimeSpan.Zero));

        // Device reports it lost data and resumes with a stamp far in the future. We must not
        // fabricate ten minutes of silence on the strength of a stamp we were just warned about.
        writer.Write(packet, PacketFrames, QpcAt(TimeSpan.FromMinutes(10)), CaptureFlags.DataDiscontinuity);

        Assert.Equal(1, writer.Stats.Discontinuities);
        Assert.Equal(1, writer.Stats.ReAnchors);
        Assert.Equal(0, writer.Stats.SilenceFramesInserted);
        Assert.Equal(PacketFrames * 2, writer.FramesWritten);
        Assert.False(writer.Stats.IsClean);
    }

    [Fact]
    public void TimestampError_DoesNotMovePacketsOnTheTimeline()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var packet = Tone(PacketFrames);

        writer.Write(packet, PacketFrames, QpcAt(TimeSpan.Zero));
        writer.Write(packet, PacketFrames, QpcAt(TimeSpan.FromHours(3)), CaptureFlags.TimestampError);

        Assert.Equal(PacketFrames * 2, writer.FramesWritten);
        Assert.Equal(1, writer.Stats.TimestampErrors);
    }

    [Fact]
    public void ImplausibleGap_ReAnchors_RatherThanWritingHoursOfZeros()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt, maxSilenceFill: TimeSpan.FromMinutes(5));
        var packet = Tone(PacketFrames);

        writer.Write(packet, PacketFrames, QpcAt(TimeSpan.Zero));
        writer.Write(packet, PacketFrames, QpcAt(TimeSpan.FromHours(2))); // machine was asleep

        Assert.Equal(1, writer.Stats.OversizedGaps);
        Assert.Equal(0, writer.Stats.SilenceFramesInserted);
        Assert.Equal(PacketFrames * 2, writer.FramesWritten);
    }

    [Fact]
    public void SubPacketJitter_IsToleratedWithoutFillingOrTrimming()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt, driftTolerance: TimeSpan.FromMilliseconds(10));
        var packet = Tone(PacketFrames);

        // Stamps wobble a few ms either way, as real device stamps do.
        int[] wobble = [0, 3, -2, 4, -3, 1, 2, -1];
        for (var i = 0; i < wobble.Length; i++)
            writer.Write(packet, PacketFrames, QpcAt(TimeSpan.FromMilliseconds(i * PacketMs + wobble[i])));

        Assert.Equal(0, writer.Stats.GapsFilled);
        Assert.Equal(0, writer.Stats.Overlaps);
        Assert.Equal(PacketFrames * wobble.Length, writer.FramesWritten);
    }

    /// <summary>
    /// The property that actually matters: two streams started together and stamped from the same
    /// clock must agree on length, however differently they behave in between.
    /// </summary>
    [Fact]
    public void TwoStreams_WithVeryDifferentActivity_EndAtTheSameLength()
    {
        using var micSink = new MemoryPcmSink(Fmt);
        using var farSink = new MemoryPcmSink(Fmt);
        using var mic = new TimelineWriter(micSink, Fmt);
        using var far = new TimelineWriter(farSink, Fmt);
        var packet = Tone(PacketFrames);

        var callLength = TimeSpan.FromMinutes(20);
        var t = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(PacketMs);
        var tick = 0;

        while (t < callLength)
        {
            // Microphone hardware delivers packets continuously, silence or not.
            mic.Write(packet, PacketFrames, QpcAt(t));

            // Loopback only delivers while the far end is actually rendering audio.
            if (tick % 100 < 30)
                far.Write(packet, PacketFrames, QpcAt(t));

            t += step;
            tick++;
        }

        far.PadTo(QpcAt(t));

        Assert.Equal(mic.FramesWritten, far.FramesWritten);
    }

    // ---- did anything actually get heard -----------------------------------

    /// <summary>
    /// PCM at a given amplitude, so the level check can be exercised at the boundary rather
    /// than only with obvious extremes.
    /// </summary>
    private static byte[] ToneAt(int frames, short amplitude)
    {
        var bytes = new byte[frames * Fmt.BytesPerFrame];

        for (var i = 0; i < frames; i++)
        {
            // Alternating sign, so the magnitude rather than the sign is what is measured.
            var sample = (i % 2 == 0) ? amplitude : (short)-amplitude;
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), sample);
        }

        return bytes;
    }

    [Fact]
    public void AStreamOfDigitalSilence_IsReportedAsCarryingNoAudio()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var quiet = new byte[PacketFrames * Fmt.BytesPerFrame];

        for (var i = 0; i < 300; i++)
            writer.Write(quiet, PacketFrames, QpcAt(TimeSpan.FromMilliseconds(i * PacketMs)));

        // Every other statistic looks perfect, which is precisely the trap: a capture pointed at
        // the wrong endpoint delivers its packets on time and produces an hour of nothing.
        Assert.True(writer.Stats.IsClean, writer.Stats.ToString());
        Assert.Equal(0, writer.Stats.PeakAmplitude);
        Assert.False(writer.Stats.CarriedAudio);
    }

    [Fact]
    public void AStreamWithSpeech_IsReportedAsCarryingAudio()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var speech = ToneAt(PacketFrames, 8000);

        for (var i = 0; i < 300; i++)
            writer.Write(speech, PacketFrames, QpcAt(TimeSpan.FromMilliseconds(i * PacketMs)));

        Assert.Equal(8000, writer.Stats.PeakAmplitude);
        Assert.True(writer.Stats.CarriedAudio);
    }

    /// <summary>
    /// A live but unspoken-into microphone still has a noise floor, and that must not be
    /// mistaken for speech — otherwise a muted microphone reports success.
    /// </summary>
    [Fact]
    public void ARoomToneNoiseFloor_DoesNotCountAsAudio()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var hiss = ToneAt(PacketFrames, 40); // about -58 dBFS

        for (var i = 0; i < 300; i++)
            writer.Write(hiss, PacketFrames, QpcAt(TimeSpan.FromMilliseconds(i * PacketMs)));

        Assert.False(writer.Stats.CarriedAudio);
    }

    /// <summary>
    /// Packets flagged SILENT carry undefined bytes that must be treated as silence, so they
    /// must never raise the measured peak whatever the buffer happens to hold.
    /// </summary>
    [Fact]
    public void SilentFlaggedPackets_DoNotCountTowardsTheLevel()
    {
        using var sink = new MemoryPcmSink(Fmt);
        using var writer = new TimelineWriter(sink, Fmt);
        var loud = ToneAt(PacketFrames, short.MaxValue);

        for (var i = 0; i < 100; i++)
        {
            writer.Write(loud, PacketFrames, QpcAt(TimeSpan.FromMilliseconds(i * PacketMs)),
                CaptureFlags.Silent);
        }

        Assert.Equal(0, writer.Stats.PeakAmplitude);
        Assert.False(writer.Stats.CarriedAudio);
    }
}
