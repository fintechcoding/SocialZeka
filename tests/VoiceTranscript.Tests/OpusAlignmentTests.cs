using Concentus;
using Concentus.Enums;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// Does the archive codec keep time?
///
/// The transcript's timestamps, the speaker attribution and the two-file mix all assume that a
/// recording decoded from the archive sits on the same clock as the PCM it replaced. A codec
/// that returned the audio a few tens of milliseconds late would not be audible, would pass
/// every "is it the same sound" check, and would still slide every word against the other
/// side of the conversation. So this measures it: a speech-shaped signal goes through
/// <see cref="OpusArchive.Encode"/> and <see cref="OpusArchive.Decode"/>, and the decoded
/// waveform is cross-correlated against the original to find the lag, the correlation at that
/// lag, and the signal-to-noise ratio once the lag is taken out.
///
/// The signal is band-limited noise bursts with gaps plus one chirp, not a tone: a periodic
/// signal correlates with itself at every period and would hide an offset.
///
/// Measured (Concentus 2.2.2 / Concentus.Oggfile 1.0.7, 24 kbit/s VOIP, 16 kHz):
/// <list type="bullet">
/// <item>decoded is 104 samples (6.5 ms) late, at both ends of the file — a constant offset,
/// not drift. 104 samples is exactly the encoder's reported lookahead (Fs/400 + Fs/250). The
/// Ogg writer records a pre-skip of 0 in OpusHead instead of that lookahead, so neither the
/// archive's reader nor any other player knows to drop it;</item>
/// <item>the decode is 320 frames (one 20 ms Opus frame) longer than the source: those 104 up
/// front and the padding of the last frame at the end;</item>
/// <item>correlation at the lag ~0.91 overall — ~0.99 on the chirp, lower on the noise bursts,
/// where the codec reproduces the sound but not the waveform; SNR ~7.6 dB.</item>
/// </list>
/// Both files of a call go through the same path, so the offset is identical on both sides and
/// their relative alignment is untouched; absolute times shift by 6.5 ms, a third of one
/// Whisper timestamp tick. The bounds below are what that behaviour must stay within: never
/// early, never more than one codec frame late, and either no offset or exactly the codec's
/// own lookahead.
/// </summary>
public sealed class OpusAlignmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vt-opus-align-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _output;

    private const int Rate = 16_000;
    private const int Seconds = 4;
    private const int Frames = Rate * Seconds;

    /// <summary>How far the search looks in either direction: 50 ms.</summary>
    private const int MaxLag = 800;

    /// <summary>One Opus frame at 16 kHz: 20 ms. The most any correct decode may be off by.</summary>
    private const int OneCodecFrame = Rate / 50;

    public OpusAlignmentTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        AudioMaterialiser.CacheDirectory = Path.Combine(_dir, "cache");
    }

    public void Dispose()
    {
        AudioMaterialiser.CacheDirectory = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void DecodedAudioSitsOnTheOriginalClock()
    {
        var (original, active, chirp) = SpeechLikeSignal();

        var wav = Path.Combine(_dir, "call-1-mic.wav");
        using (var sink = new WavPcmSink(wav, AudioFormat.WhisperPcm))
            sink.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(original.AsSpan()));

        var ogg = OpusArchive.CompressedPathFor(wav);
        var encoded = OpusArchive.Encode(wav, ogg);
        var preSkip48k = ReadPreSkip(ogg);
        var preSkip = preSkip48k * Rate / 48_000;

        // What the codec itself says its delay is, with the archive's own settings.
        var lookahead = OpusCodecFactory.CreateEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP, null).Lookahead;

        var back = Path.Combine(_dir, "back.wav");
        var decodedFrames = OpusArchive.Decode(ogg, back);

        short[] decoded;
        using (var reader = PcmReader.Open(back))
        {
            decoded = new short[reader.Frames];
            var got = reader.Read(decoded);
            Assert.Equal(decoded.Length, got);
        }

        var (lag, correlation) = BestLag(original, decoded, 0, Frames);
        var (lagHead, _) = BestLag(original, decoded, 0, Frames / 2);
        var (lagTail, _) = BestLag(original, decoded, Frames / 2, Frames);

        var noise = new bool[Frames];
        for (var i = 0; i < Frames; i++) noise[i] = active[i] && !chirp[i];

        var chirpCorrelation = CorrelationAt(original, decoded, lag, chirp);
        var noiseCorrelation = CorrelationAt(original, decoded, lag, noise);
        var snrActive = SnrDb(original, decoded, lag, active);
        var snrAll = SnrDb(original, decoded, lag, null);

        var report =
            $"source frames {Frames}, encoded frames {encoded}, decoded frames {decodedFrames} (reader {decoded.Length}); " +
            $"encoder lookahead {lookahead}, OpusHead pre-skip {preSkip48k} @48k = {preSkip} @16k; " +
            $"lag {lag} samples ({lag * 1000.0 / Rate:0.00} ms, positive = decoded late), first half {lagHead}, second half {lagTail}; " +
            $"normalised correlation at lag {correlation:0.0000} (chirp {chirpCorrelation:0.0000}, noise bursts {noiseCorrelation:0.0000}); " +
            $"SNR over speech-like part {snrActive:0.00} dB, over everything {snrAll:0.00} dB";

        _output.WriteLine(report);

        // The frame count in and out is what the archive relies on: an encode is trusted only
        // when the decode counts the same frames, give or take the codec's own padding.
        Assert.True(encoded == Frames, $"encoded {encoded} != source {Frames}; {report}");
        Assert.True(decodedFrames >= Frames && decodedFrames <= Frames + OneCodecFrame,
            $"decoded {decodedFrames} vs source {Frames}: lost audio or more than one frame of padding; {report}");
        Assert.True(decodedFrames == decoded.Length, $"Decode returned {decodedFrames} but the file holds {decoded.Length}; {report}");

        // The offset must be the same at the start and the end of the file. A drift would mean
        // the decoded clock runs at a different rate, and no constant correction could fix it.
        Assert.True(Math.Abs(lagHead - lagTail) <= 2, $"lag drifts from {lagHead} to {lagTail}; {report}");

        // Never early, never more than one codec frame late, and the offset is either nothing
        // (the container declares the lookahead and the reader drops it) or exactly the
        // encoder's lookahead (it does not). Anything else is a real fault in the codec path.
        Assert.True(lag >= 0 && lag <= OneCodecFrame, $"decoded audio is off by {lag} samples; {report}");
        Assert.True(Math.Abs(lag) <= 2 || Math.Abs(lag - lookahead) <= 2,
            $"lag {lag} is neither zero nor the encoder lookahead {lookahead}; {report}");

        // The same sound, once the offset is taken out. The chirp is a waveform the codec can
        // follow closely; the noise bursts are where a perceptual codec is allowed to differ.
        Assert.True(chirpCorrelation > 0.95, $"chirp correlates at only {chirpCorrelation:0.000}; {report}");
        Assert.True(correlation > 0.8, $"decoded audio correlates at only {correlation:0.000}; {report}");
        Assert.True(snrActive > 3, $"SNR over speech is only {snrActive:0.0} dB; {report}");
    }

    /// <summary>
    /// Four seconds of what a voice does to a microphone, in shape if not in meaning: bursts of
    /// band-limited noise with pauses between them, and one rising chirp. Deterministic, so a
    /// failure reproduces. Also returns which samples count as speech, and which of those are
    /// the chirp.
    /// </summary>
    private static (short[] Samples, bool[] Active, bool[] Chirp) SpeechLikeSignal()
    {
        var rng = new Random(20260901);
        var samples = new double[Frames];
        var active = new bool[Frames];
        var chirpMask = new bool[Frames];

        // (start ms, length ms, chirp?)
        (int Start, int Length, bool Chirp)[] bursts =
        [
            (200, 220, false), (550, 180, false), (900, 300, false), (1400, 150, false),
            (1700, 250, false), (2100, 200, false), (2500, 280, true), (2900, 160, false),
            (3300, 240, false), (3650, 200, false),
        ];

        // 1-pole low-pass at ~3.4 kHz and 1-pole high-pass at ~250 Hz: telephone band.
        var lowA = 1 - Math.Exp(-2 * Math.PI * 3400 / Rate);
        var highR = Math.Exp(-2 * Math.PI * 250 / Rate);

        foreach (var (startMs, lengthMs, chirp) in bursts)
        {
            var start = startMs * Rate / 1000;
            var length = lengthMs * Rate / 1000;
            var ramp = 20 * Rate / 1000;

            double low = 0, highIn = 0, highOut = 0, phase = 0;
            var burst = new double[length];

            for (var i = 0; i < length; i++)
            {
                double x;

                if (chirp)
                {
                    var t = (double)i / length;
                    var hz = 300 + (3000 - 300) * t;
                    phase += 2 * Math.PI * hz / Rate;
                    x = Math.Sin(phase);
                }
                else
                {
                    var white = rng.NextDouble() * 2 - 1;
                    low += lowA * (white - low);
                    highOut = highR * highOut + (low - highIn);
                    highIn = low;
                    x = highOut;
                }

                burst[i] = x;
            }

            // Every burst at the same loudness, about -20 dBFS RMS.
            var rms = Math.Sqrt(burst.Sum(v => v * v) / length);
            var gain = 3000 / Math.Max(rms, 1e-9);

            for (var i = 0; i < length; i++)
            {
                var envelope = i < ramp ? 0.5 - 0.5 * Math.Cos(Math.PI * i / ramp)
                    : i >= length - ramp ? 0.5 - 0.5 * Math.Cos(Math.PI * (length - 1 - i) / ramp)
                    : 1.0;

                samples[start + i] = burst[i] * gain * envelope;
                active[start + i] = envelope > 0.5;
                chirpMask[start + i] = chirp && envelope > 0.5;
            }
        }

        var pcm = new short[Frames];
        for (var i = 0; i < Frames; i++)
            pcm[i] = (short)Math.Clamp(Math.Round(samples[i]), short.MinValue, short.MaxValue);

        return (pcm, active, chirpMask);
    }

    /// <summary>
    /// The pre-skip field of the OpusHead packet on the first Ogg page: how many 48 kHz samples
    /// of encoder delay a player is meant to drop before the audio starts. -1 if not found.
    /// </summary>
    private static int ReadPreSkip(string oggPath)
    {
        var bytes = File.ReadAllBytes(oggPath);

        // Page header is 27 bytes plus one byte per segment; the OpusHead packet follows.
        if (bytes.Length < 27 || bytes[0] != (byte)'O' || bytes[1] != (byte)'g') return -1;

        var packet = 27 + bytes[26];
        if (bytes.Length < packet + 12) return -1;
        if (System.Text.Encoding.ASCII.GetString(bytes, packet, 8) != "OpusHead") return -1;

        return BitConverter.ToUInt16(bytes, packet + 10);
    }

    /// <summary>
    /// The lag at which the decoded signal best matches the original over original[from..to),
    /// and the normalised cross-correlation there. Positive lag: decoded[n + lag] lines up with
    /// original[n], so the decoded audio is late.
    /// </summary>
    private static (int Lag, double Correlation) BestLag(short[] original, short[] decoded, int from, int to)
    {
        var bestLag = 0;
        var best = double.NegativeInfinity;

        for (var lag = -MaxLag; lag <= MaxLag; lag++)
        {
            var start = Math.Max(from, -lag);
            var end = Math.Min(to, decoded.Length - lag);
            if (end - start < Rate) continue;

            double dot = 0, energyOriginal = 0, energyDecoded = 0;

            for (var n = start; n < end; n++)
            {
                double o = original[n], d = decoded[n + lag];
                dot += o * d;
                energyOriginal += o * o;
                energyDecoded += d * d;
            }

            var ncc = dot / Math.Sqrt(Math.Max(energyOriginal * energyDecoded, 1e-9));

            if (ncc > best)
            {
                best = ncc;
                bestLag = lag;
            }
        }

        return (bestLag, best);
    }

    /// <summary>Normalised correlation at a fixed lag, over the masked samples only.</summary>
    private static double CorrelationAt(short[] original, short[] decoded, int lag, bool[] mask)
    {
        var from = Math.Max(0, -lag);
        var to = Math.Min(original.Length, decoded.Length - lag);

        double dot = 0, energyOriginal = 0, energyDecoded = 0;

        for (var n = from; n < to; n++)
        {
            if (!mask[n]) continue;

            double o = original[n], d = decoded[n + lag];
            dot += o * d;
            energyOriginal += o * o;
            energyDecoded += d * d;
        }

        return dot / Math.Sqrt(Math.Max(energyOriginal * energyDecoded, 1e-9));
    }

    /// <summary>Signal-to-noise of the decoded audio once shifted by the lag, over the masked samples (or all).</summary>
    private static double SnrDb(short[] original, short[] decoded, int lag, bool[]? mask)
    {
        var from = Math.Max(0, -lag);
        var to = Math.Min(original.Length, decoded.Length - lag);

        double signal = 0, noise = 0;

        for (var n = from; n < to; n++)
        {
            if (mask is not null && !mask[n]) continue;

            double o = original[n], d = decoded[n + lag];
            signal += o * o;
            noise += (o - d) * (o - d);
        }

        return 10 * Math.Log10(signal / Math.Max(noise, 1e-9));
    }
}
