namespace VoiceTranscript.Core.Audio;

public sealed record RecordingResult(
    string? MicPath,
    string? FarPath,
    TimeSpan MicDuration,
    TimeSpan FarDuration,
    TimelineStats MicStats,
    TimelineStats FarStats)
{
    /// <summary>
    /// Whether the two streams line up well enough to trust who said what.
    ///
    /// Length is the check that matters. Both files are written on the same wall clock, so if
    /// one is materially shorter than the other, something withheld packets that were never
    /// accounted for — and every attribution after that point is suspect.
    /// </summary>
    public bool StreamsAreAligned =>
        (MicDuration - FarDuration).Duration() < TimeSpan.FromSeconds(1);

    public bool IsClean => MicStats.IsClean && FarStats.IsClean && StreamsAreAligned;

    public TimeSpan Duration => MicDuration > FarDuration ? MicDuration : FarDuration;

    /// <summary>Whether the user's own microphone actually carried sound.</summary>
    public bool MicCarriedAudio => MicStats.CarriedAudio;

    /// <summary>Whether anything was heard from the other party.</summary>
    public bool FarCarriedAudio => FarStats.CarriedAudio;

    /// <summary>True when at least one side is silent, which makes the recording partly useless.</summary>
    public bool HasSilentStream => !MicCarriedAudio || !FarCarriedAudio;

    /// <summary>
    /// One sentence saying, in plain words, whether this recording is usable.
    ///
    /// It is deliberately phrased around what the user can do about it. "Karşı taraf duyulmuyor"
    /// is actionable — check the output device — where a packet count is not, and a recorder
    /// that quietly captures one hour of silence is worse than one that admits it failed.
    /// </summary>
    public string AudioSummary => (MicCarriedAudio, FarCarriedAudio) switch
    {
        (true, true) => "Her iki taraf da kaydedildi.",
        (true, false) =>
            "Senin sesin kaydedildi ama karşı taraftan hiç ses gelmedi. Windows'un ses çıkışı " +
            "arama sırasında başka bir cihaza geçmiş olabilir.",
        (false, true) =>
            "Karşı taraf kaydedildi ama mikrofonundan hiç ses gelmedi. Mikrofon kapalı ya da " +
            "başka bir cihaz seçili olabilir.",
        (false, false) =>
            "Kayıt boyunca hiçbir akıştan ses gelmedi. Ayarlar bölümündeki ses yakalama " +
            "sınamasını çalıştırmakta fayda var.",
    };
}

/// <summary>
/// Records one call to two WAV files, one per speaker.
///
/// The whole design rests on this separation. The microphone is the user and the loopback is the
/// other party, so who said what is a fact about which file the audio landed in rather than a
/// prediction from a model. No diarization is needed, it costs no VRAM, and it stays correct
/// when both people talk at once — which is precisely where diarization fails worst.
///
/// Each stream goes through its own <see cref="TimelineWriter"/>, which is what keeps the two
/// files the same length even though the loopback device stops sending packets whenever the far
/// end is quiet.
/// </summary>
public sealed class CallRecorder : IDisposable
{
    private readonly IAudioCaptureBackend _backend;
    private readonly AudioFormat _format;
    private readonly object _gate = new();

    private TimelineWriter? _mic;
    private TimelineWriter? _far;
    private WavPcmSink? _micSink;
    private WavPcmSink? _farSink;
    private string? _micPath;
    private string? _farPath;
    private long _lastQpc;
    private long? _firstQpc;

    /// <summary>How much of the previous level survives each packet, so the meter falls smoothly.</summary>
    private const double LevelDecay = 0.75;

    private double _micLevel;
    private double _farLevel;
    private long _lastLevelReport;
    private Timer? _checkpoint;
    private bool _disposed;

    public CallRecorder(IAudioCaptureBackend backend)
    {
        _backend = backend;
        _format = backend.Format;
        _backend.PacketReady += OnPacket;
        _backend.Interrupted += (_, reason) => Interrupted?.Invoke(this, reason);
    }

    public bool IsRecording { get; private set; }

    public event EventHandler<string>? Interrupted;

    /// <summary>
    /// Current loudness of each stream, 0-1, roughly ten times a second.
    ///
    /// Exists so the application can show, while a call is happening, that it is actually
    /// hearing something. Every other report of this — the summary after the call, the capture
    /// self-test — arrives too late to save the conversation it was wrong about.
    /// </summary>
    public event EventHandler<(double Mic, double Far)>? LevelChanged;

    public async Task StartAsync(
        string directory,
        string callId,
        int? targetProcessId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (IsRecording) throw new InvalidOperationException("Already recording.");

            Directory.CreateDirectory(directory);

            _micPath = Path.Combine(directory, $"{callId}-mic.wav");
            _farPath = Path.Combine(directory, $"{callId}-far.wav");

            _micSink = new WavPcmSink(_micPath, _format);
            _farSink = new WavPcmSink(_farPath, _format);
            _mic = new TimelineWriter(_micSink, _format);
            _far = new TimelineWriter(_farSink, _format);
            _lastQpc = 0;
            _firstQpc = null;

            IsRecording = true;
        }

        try
        {
            await _backend.StartAsync(targetProcessId, cancellationToken);
        }
        catch
        {
            // The sinks are already open. Close them so a failed start does not leave two
            // zero-length WAV files behind and the recorder stuck believing it is running.
            lock (_gate)
            {
                IsRecording = false;
                _mic?.Dispose();
                _far?.Dispose();
                _mic = null;
                _far = null;
                _micSink = null;
                _farSink = null;
            }

            throw;
        }

        // Patch the WAV headers periodically so that a crash, a forced shutdown or a closing lid
        // costs seconds of audio rather than the whole call.
        _checkpoint = new Timer(_ => Checkpoint(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Peak of this packet, decayed towards zero so the meter falls back rather than sticking.</summary>
    private void TrackLevel(StreamRole role, CapturedPacket packet)
    {
        var level = (packet.Flags & CaptureFlags.Silent) != 0 ? 0 : Peak(packet.Data, packet.FrameCount);

        if (role == StreamRole.Microphone)
            _micLevel = Math.Max(level, _micLevel * LevelDecay);
        else
            _farLevel = Math.Max(level, _farLevel * LevelDecay);

        // Roughly ten times a second. Faster than that is invisible to the eye and only costs
        // dispatcher work on a thread that is writing audio.
        var now = Environment.TickCount64;
        if (now - _lastLevelReport < 100) return;

        _lastLevelReport = now;
        LevelChanged?.Invoke(this, (_micLevel, _farLevel));
    }

    private double Peak(ReadOnlySpan<byte> pcm, int frameCount)
    {
        if (_format.BitsPerSample != 16) return 0;

        var wanted = Math.Min(pcm.Length, frameCount * _format.BytesPerFrame);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm[..(wanted - wanted % 2)]);

        var peak = 0;
        foreach (var sample in samples)
        {
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }

        return peak / (double)short.MaxValue;
    }

    private void OnPacket(StreamRole role, CapturedPacket packet)
    {
        lock (_gate)
        {
            if (!IsRecording) return;

            // Both streams share one origin: the first packet seen from either of them.
            //
            // Anchoring each stream to its own first packet would be wrong in a way that is easy
            // to miss. The far end usually starts speaking seconds after the microphone does, so
            // its file would begin at that later moment — shorter than the call, and with every
            // timestamp inside it shifted by however long the other person waited before saying
            // anything. Sharing the origin means a late start is written as leading silence and
            // both files describe the same stretch of time.
            if (_firstQpc is null)
            {
                _firstQpc = packet.QpcPosition;
                _mic?.AnchorAt(packet.QpcPosition);
                _far?.AnchorAt(packet.QpcPosition);
            }

            var writer = role == StreamRole.Microphone ? _mic : _far;
            writer?.Write(packet.Data, packet.FrameCount, packet.QpcPosition, packet.Flags);

            TrackLevel(role, packet);

            if (packet.QpcPosition > _lastQpc) _lastQpc = packet.QpcPosition;
        }
    }

    private void Checkpoint()
    {
        lock (_gate)
        {
            if (!IsRecording) return;

            try
            {
                _micSink?.Checkpoint();
                _farSink?.Checkpoint();
            }
            catch (Exception e)
            {
                Interrupted?.Invoke(this, $"Ara kayıt yazılamadı: {e.Message}");
            }
        }
    }

    public RecordingResult Stop()
    {
        _backend.Stop();

        _checkpoint?.Dispose();
        _checkpoint = null;

        lock (_gate)
        {
            if (!IsRecording) throw new InvalidOperationException("Not recording.");
            IsRecording = false;

            // A stream that never received a packet has no origin of its own. Give it the
            // moment the call started so it can be padded: a far end that stayed silent for the
            // whole call must still produce a file as long as the call, or the two timelines
            // cannot be compared at all.
            if (_firstQpc is { } origin)
            {
                _mic?.AnchorAt(origin);
                _far?.AnchorAt(origin);
            }

            // Pad both streams out to the same instant. Without this the stream that fell silent
            // last simply ends early, and the two files disagree about how long the call was.
            _mic?.PadTo(_lastQpc);
            _far?.PadTo(_lastQpc);

            var result = new RecordingResult(
                _micPath,
                _farPath,
                _mic?.Duration ?? TimeSpan.Zero,
                _far?.Duration ?? TimeSpan.Zero,
                _mic?.Stats ?? new TimelineStats(),
                _far?.Stats ?? new TimelineStats());

            _mic?.Dispose(); // disposes the sink, which patches the final header
            _far?.Dispose();
            _mic = null;
            _far = null;
            _micSink = null;
            _farSink = null;

            return result;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording)
        {
            try
            {
                Stop();
            }
            catch (InvalidOperationException)
            {
                // Raced with an explicit Stop.
            }
        }

        _checkpoint?.Dispose();
        _backend.PacketReady -= OnPacket;
    }
}
