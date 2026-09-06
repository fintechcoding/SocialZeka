using System.IO;
using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Voice;
using VoiceTranscript.Worker;

namespace VoiceTranscript.App.Services;

/// <summary>Who the far end is, once there has been enough of them to say.</summary>
public sealed record SpeakerHypothesis(VoiceMatch Match, Contact? Contact);

/// <summary>
/// Listening to the other end of a live call for long enough to recognise them.
///
/// The application already knows which side is speaking — the microphone is the user and the
/// loopback is the other party, and that comes from which file the audio arrived in rather than
/// from any model. What it has never known is *who* the other party is. That has come from the
/// call window's title, and the archive records the cost: one generic "Voice call" title spread
/// across eight different contacts.
///
/// <b>Thirty seconds of speech, once per call.</b> Measured over this application's own archive,
/// an embedding built from less than that is noise — the error rate is thirteen times what it is
/// above the line. So nothing is asked until the other person has actually said thirty seconds'
/// worth, and then it is asked exactly once: the worker runs one job per process, so this is a
/// two-second spawn rather than something that can be repeated as the call goes on.
///
/// <b>Nothing slow happens on the capture thread.</b> The packet handler runs synchronously inside
/// WASAPI's callback and the span it is handed is only valid for that call. Anything expensive
/// there stalls capture for *both* streams, so the handler copies bytes into a buffer and returns;
/// everything else happens on a worker thread.
/// </summary>
public sealed class SpeakerIdentifier : IDisposable
{
    /// <summary>How much of the other person is needed before asking. See vt_worker/speaker.py.</summary>
    private const double RequiredSpeechSeconds = 30.0;

    /// <summary>
    /// Loud enough to be somebody talking rather than the room they are in.
    ///
    /// Internal rather than private because <see cref="LiveTalkMeter"/> asks the same question of
    /// the same packets and has to get the same answer. A second copy of this number would drift
    /// from this one the first time either was tuned, and then two parts of the application would
    /// disagree about whether anybody was speaking.
    /// </summary>
    internal const double SpeechFloorDbfs = -40.0;

    /// <summary>
    /// The most audio held at once, in seconds of speech.
    ///
    /// Bounded because a four-hour call must not grow a four-hour buffer for a question that was
    /// answered in the first minute. Speech beyond this is dropped, not accumulated.
    /// </summary>
    private const double BufferCeilingSeconds = 90.0;

    private readonly Repository _repository;
    private readonly Func<PythonWorkerHost> _worker;
    private readonly string _cacheDirectory;
    private readonly string _modelDirectory;
    private readonly Action<string> _log;

    private readonly Lock _gate = new();
    private readonly List<byte> _speech = [];

    private IAudioCaptureBackend? _backend;
    private AudioFormat _format = AudioFormat.WhisperPcm;
    private bool _asked;
    private bool _disposed;

    /// <summary>Seconds of far-end speech seen, for the line written when a call ends short.</summary>
    private double _heard;

    /// <summary>Raised once per call, on a worker thread, when the far end has been recognised.</summary>
    public event EventHandler<SpeakerHypothesis>? Identified;

    /// <summary>What the voice concluded, for the code that files the call when it ends.</summary>
    public SpeakerHypothesis? Result { get; private set; }

    public SpeakerIdentifier(
        Repository repository,
        Func<PythonWorkerHost> worker,
        string cacheDirectory,
        string modelDirectory,
        Action<string>? log = null)
    {
        _repository = repository;
        _worker = worker;
        _cacheDirectory = cacheDirectory;
        _modelDirectory = modelDirectory;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Starts listening to the far end of a call.
    ///
    /// Attached to the backend's own event rather than to the recorder, so the recording is not
    /// touched: this is a second subscriber to a multicast event, exactly as CaptureSelfTest does
    /// it, and removing it leaves the capture chain as it was.
    /// </summary>
    public void Listen(IAudioCaptureBackend backend)
    {
        _backend = backend;
        _format = backend.Format;
        backend.PacketReady += OnPacket;

        // Said at the start, because the alternative is what happened on the first real call: the
        // feature was on, the log held not one line about it, and there was no way to tell whether
        // it had failed, never attached, or simply not been given enough of the other person to
        // work with. A service that only speaks when it succeeds is a service that cannot be
        // diagnosed when it does not.
        _log($"dinlemeye başlandı · karşı taraf {RequiredSpeechSeconds:0} sn konuşunca sorulacak");
    }

    private void OnPacket(StreamRole role, CapturedPacket packet)
    {
        // The microphone is deliberately ignored. Checked against itself — the same person by
        // construction, no labels involved — it fails to match a third of the time at every
        // duration, because the capture hardware changes between calls and channel mismatch is
        // what wrecks speaker verification. Only the far end is usable.
        if (role != StreamRole.Loopback || _disposed) return;

        if (!IsSpeech(packet.Data)) return;

        lock (_gate)
        {
            if (_asked || _speech.Count >= BufferCeilingSeconds * _format.BytesPerSecond) return;

            // Copied, not referenced. CapturedPacket.Data is a span over WASAPI's own buffer and
            // is valid only inside this call.
            _speech.AddRange(packet.Data);
            _heard = (double)_speech.Count / _format.BytesPerSecond;

            if (_speech.Count < RequiredSpeechSeconds * _format.BytesPerSecond) return;

            _asked = true;
        }

        // Off the capture thread before anything expensive. A slow handler here stalls both
        // streams, which would corrupt the recording this feature is meant to be invisible to.
        _ = Task.Run(IdentifyAsync);
    }

    /// <summary>Whether this packet is somebody talking, by level. Cheap on purpose.</summary>
    private static bool IsSpeech(ReadOnlySpan<byte> pcm) => Dbfs(pcm) > SpeechFloorDbfs;

    /// <summary>
    /// Loudness of one packet in dBFS. Cheap on purpose: this runs inside WASAPI's callback.
    ///
    /// Split out of <see cref="IsSpeech"/>, unchanged, because <see cref="LiveTalkMeter"/> needs
    /// the level itself and not only the verdict — its baseline is a median of these. Two
    /// implementations of the same arithmetic over the same packets is how two screens end up
    /// disagreeing about the same second of audio.
    ///
    /// Negative infinity for a packet too short to have a level, which is below every threshold
    /// and so reads as silence exactly as the earlier length check did.
    /// </summary>
    internal static double Dbfs(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2) return double.NegativeInfinity;

        double squared = 0;
        var count = pcm.Length / 2;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            double sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            squared += sample * sample;
        }

        var rms = Math.Sqrt(squared / count);
        return 20 * Math.Log10(Math.Max(rms, 1e-9) / short.MaxValue);
    }

    private async Task IdentifyAsync()
    {
        byte[] pcm;
        lock (_gate) pcm = [.. _speech];

        var path = Path.Combine(_cacheDirectory, $"voice-{Guid.NewGuid():N}.wav");

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            WriteWav(path, pcm, _format);

            var voiceprint = await _worker().EmbedSpeakerAsync(
                new Core.Asr.SpeakerRequest { Id = "live", WavPath = path, CacheDir = _modelDirectory });

            if (!voiceprint.Usable)
            {
                _log($"ses tanıma: karşı taraf tanınamadı ({voiceprint.Reason ?? "vektör yok"})");
                return;
            }

            var known = _repository.Voiceprints(voiceprint.Model);
            var match = VoiceMatcher.Match(voiceprint.Vector!, known);

            if (match.Verdict == VoiceVerdict.Unknown)
            {
                _log($"ses tanıma: {known.Count} kayıtlı sesin hiçbiri tutmadı");
                return;
            }

            var contact = _repository.GetContact(match.ContactId);
            Result = new SpeakerHypothesis(match, contact);

            // The name is deliberately absent from the log. This file is written to be shared and
            // promises to carry no contact's name; the score and the verdict are what diagnose it.
            _log($"ses tanıma: {match.Verdict} · benzerlik {match.Score:0.00} · fark {match.Margin:0.00}");

            Identified?.Invoke(this, Result);
        }
        catch (Exception e)
        {
            // Recognising somebody is a courtesy. It must never be the reason a recording fails.
            _log($"ses tanıma yapılamadı: {e.Message}");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>A 16 kHz mono 16-bit WAV, which is the only shape the embedder accepts.</summary>
    private static void WriteWav(string path, byte[] pcm, AudioFormat format)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(format.BytesPerSecond);
        writer.Write((short)format.BytesPerFrame);
        writer.Write((short)format.BitsPerSample);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_backend is not null) _backend.PacketReady -= OnPacket;
        _backend = null;

        // Why nothing was recognised, when nothing was.
        //
        // Silence is the common outcome and it has three different causes — the call was short,
        // the other person barely spoke, or the identification itself failed — and until this line
        // existed the log looked identical in all three. The first real call with the feature on
        // produced not one line about it, so there was no way to tell it from the feature being
        // broken. This is the line that tells them apart.
        if (!_asked)
        {
            _log($"tanınmadı: karşı taraf {_heard:0} sn konuştu, {RequiredSpeechSeconds:0} sn gerekiyor "
                 + "— bu görüşme sesten tanınamayacak kadar kısa");
        }

        lock (_gate) _speech.Clear();
    }
}
