using System.Globalization;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Turns a finished recording into something twenty times smaller, and back.
///
/// A call is captured as two 16 kHz mono PCM streams — the microphone and the speaker, kept
/// apart because that separation is what makes speaker attribution a fact rather than a guess.
/// PCM at that rate is 115 MB an hour per stream; a busy month of calls is tens of gigabytes
/// of audio that will be listened to for perhaps a few minutes in total, when a promise or a
/// figure is checked against the recording.
///
/// Opus was designed for exactly this signal: wideband speech at 16 kHz, where 24 kbit/s is
/// transparent for a voice call that already went through a messenger's own codec. The file
/// is about 10 MB an hour. The container is Ogg, and the extension is .ogg rather than .opus
/// so that Obsidian and the Windows shell play it without being told what it is.
///
/// Only ever applied after the words are out of the audio and in the archive: the first
/// transcription reads the PCM original, and nothing here runs before it has.
///
/// A later one does not. "Yeniden yazıya dök" decodes this archive back to PCM and transcribes
/// that, so whatever this throws away is thrown away for every transcript after the first — which
/// is why the bitrate below is a transcription decision and not only a storage one.
/// </summary>
public static class OpusArchive
{
    public const string Extension = ".ogg";

    /// <summary>
    /// Bits per second. VBR, so quiet stretches cost almost nothing.
    ///
    /// Was 24 kbps, chosen when the archive was only ever going to be listened to. It is not: the
    /// "Yeniden yazıya dök" path decodes this back to PCM and transcribes it again, so the number
    /// decides how good a second transcript can be — and 24 kbps is measurably on the wrong side
    /// of a cliff. One recording, four bitrates: 21.5 kbps gave 1624 words, 18.2 gave 330. Opus
    /// undershoots its target on speech with pauses in it, so 24 produces 18-21 in practice.
    ///
    /// It was the same mistake as the upload used to make, in a second place, and it explains what
    /// looked like a cloud problem: the good transcript people compared against was the first run,
    /// on the original recording, and every re-run afterwards read audio that had been through
    /// this. 64 kbps is close to transparent for 16 kHz mono speech and still a fifth of the size.
    ///
    /// Recordings already compressed at 24 cannot be recovered by changing this. What was thrown
    /// away is gone; only recordings compressed from here on are better.
    /// </summary>
    private const int Bitrate = 64_000;

    // Written into the Ogg comment header so the decoder can put the audio back on the
    // original clock.
    //
    // Measured, not assumed: Concentus.Oggfile writes pre-skip 0 into OpusHead, so a plain
    // decode hands out the encoder's lookahead (104 samples at 16 kHz) as audio and the whole
    // stream sits 6.5 ms late, with up to one codec frame of padding on the end. Every stored
    // timestamp was made from the PCM original; the decoded copy has to agree with them to the
    // sample, so the lookahead is recorded here and dropped on the way out, and the original
    // length is recorded and honoured.
    private const string PreSkipTag = "VT_PRESKIP";
    private const string FramesTag = "VT_FRAMES";

    public static bool IsCompressed(string? path) =>
        path is not null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The compressed sibling of a PCM path: call-7-mic.wav becomes call-7-mic.ogg.</summary>
    public static string CompressedPathFor(string wavPath) => Path.ChangeExtension(wavPath, Extension);

    /// <summary>
    /// Encodes a PCM WAV to Ogg/Opus and returns how many sample frames were encoded.
    /// Writes to a temporary name and renames into place, so a crash never leaves a file that
    /// looks finished.
    /// </summary>
    public static long Encode(string wavPath, string oggPath)
    {
        using var reader = PcmReader.Open(wavPath);

        if (reader.Format.Channels != 1 || reader.Format.BitsPerSample != 16)
            throw new InvalidDataException("Yalnızca 16 bit tek kanallı kayıt sıkıştırılır.");

        var temporary = $"{oggPath}.{Guid.NewGuid():N}.partial";

        try
        {
            var encoder = OpusCodecFactory.CreateEncoder(reader.Format.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP, null);
            encoder.Bitrate = Bitrate;
            encoder.UseVBR = true;
            encoder.Complexity = 10;

            var tags = new OpusTags();
            tags.Fields["ENCODER"] = "VoiceTranscript";
            tags.Fields[PreSkipTag] = encoder.Lookahead.ToString(CultureInfo.InvariantCulture);
            tags.Fields[FramesTag] = reader.Frames.ToString(CultureInfo.InvariantCulture);

            long frames = 0;

            using (var output = File.Create(temporary))
            {
                var ogg = new OpusOggWriteStream(encoder, output, tags, reader.Format.SampleRate, 0, leaveOpen: false);

                // A few hundred milliseconds at a time; the writer buffers to whole Opus frames.
                var buffer = new short[reader.Format.SampleRate / 50 * 8];
                int read;

                while ((read = reader.Read(buffer)) > 0)
                {
                    ogg.WriteSamples(buffer, 0, read);
                    frames += read;
                }

                ogg.Finish();
            }

            File.Move(temporary, oggPath, overwrite: true);
            return frames;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }

            throw;
        }
    }

    /// <summary>
    /// Decodes Ogg/Opus back to a PCM WAV and returns how many sample frames came out.
    /// </summary>
    public static long Decode(string oggPath, string wavPath, AudioFormat? format = null)
    {
        var pcm = format ?? AudioFormat.WhisperPcm;
        var temporary = $"{wavPath}.{Guid.NewGuid():N}.partial";

        try
        {
            long frames;

            using (var input = File.OpenRead(oggPath))
            using (var sink = new WavPcmSink(temporary, pcm))
            {
                frames = DecodeInto(input, pcm.SampleRate, sink);
            }

            File.Move(temporary, wavPath, overwrite: true);
            return frames;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }

            throw;
        }
    }

    /// <summary>Counts the frames a file decodes to, without keeping them. The integrity check.</summary>
    public static long CountFrames(string oggPath, int sampleRate)
    {
        using var input = File.OpenRead(oggPath);
        return DecodeInto(input, sampleRate, null);
    }

    private static long DecodeInto(Stream input, int sampleRate, WavPcmSink? sink)
    {
        var decoder = OpusCodecFactory.CreateDecoder(sampleRate, 1, null);
        var ogg = new OpusOggReadStream(decoder, input);

        long frames = 0;
        long skip = 0;
        long? limit = null;
        var tagsRead = false;

        while (ogg.HasNextPacket)
        {
            var packet = ogg.DecodeNextPacket();

            // The comment header is parsed with the first page; a file written before the tags
            // existed simply decodes the way it always did.
            if (!tagsRead)
            {
                tagsRead = true;
                (skip, limit) = ReadClock(ogg.Tags);
            }

            if (packet is null || packet.Length == 0) continue;

            var span = packet.AsSpan();

            if (skip > 0)
            {
                var drop = (int)Math.Min(skip, span.Length);
                span = span[drop..];
                skip -= drop;
            }

            if (limit is { } total)
            {
                var room = total - frames;
                if (room <= 0) break;
                if (span.Length > room) span = span[..(int)room];
            }

            if (span.IsEmpty) continue;

            frames += span.Length;
            sink?.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
        }

        if (frames == 0 && ogg.LastError is { Length: > 0 } error)
            throw new InvalidDataException($"Opus çözülemedi: {error}");

        return frames;
    }

    private static (long Skip, long? Frames) ReadClock(OpusTags? tags)
    {
        if (tags?.Fields is not { } fields) return (0, null);

        long skip = 0;
        long? frames = null;

        if (fields.TryGetValue(PreSkipTag, out var rawSkip)
            && long.TryParse(rawSkip, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSkip)
            && parsedSkip >= 0)
        {
            skip = parsedSkip;
        }

        if (fields.TryGetValue(FramesTag, out var rawFrames)
            && long.TryParse(rawFrames, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrames)
            && parsedFrames > 0)
        {
            frames = parsedFrames;
        }

        return (skip, frames);
    }
}
