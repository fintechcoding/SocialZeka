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
/// Only ever applied after the words are out of the audio and in the archive: transcription
/// reads the PCM original, and nothing here runs before it has.
/// </summary>
public static class OpusArchive
{
    public const string Extension = ".ogg";

    /// <summary>Bits per second. VBR, so quiet stretches cost almost nothing.</summary>
    private const int Bitrate = 24_000;

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

        while (ogg.HasNextPacket)
        {
            var packet = ogg.DecodeNextPacket();
            if (packet is null || packet.Length == 0) continue;

            frames += packet.Length;
            sink?.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(packet.AsSpan()));
        }

        if (frames == 0 && ogg.LastError is { Length: > 0 } error)
            throw new InvalidDataException($"Opus çözülemedi: {error}");

        return frames;
    }
}
