using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceTranscript.Core.Audio;
using CoreCaptureFlags = VoiceTranscript.Core.Audio.CaptureFlags;

namespace VoiceTranscript.Capture;

/// <summary>
/// Captures the microphone and the far end as two independent WASAPI streams.
///
/// This is the default backend. It records whatever the output endpoint is playing rather than
/// isolating one application, which is a deliberate trade: it needs no driver and no special
/// permission, and — the part that actually matters — its packets carry a real device clock.
/// The per-process alternative looks cleaner on paper but on Windows 11 build 26200 its virtual
/// device refuses format negotiation, always reports a device position of zero, and hands out
/// QPC values that count packets rather than measure time. Alignment cannot be verified against
/// a clock that does not exist.
///
/// Capturing the whole endpoint means anything else playing is recorded too. That stays
/// theoretical because recording only runs while a call is detected, and people do not usually
/// play music through a conversation.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WasapiCaptureBackend : IAudioCaptureBackend
{
    private readonly bool _useEchoCancellation;
    private readonly string? _microphoneDeviceId;
    private readonly string? _outputDeviceId;
    private WasapiRecorder? _microphone;
    private WasapiRecorder? _loopback;
    private WasapiPlayer? _keepAlive;
    private MMDeviceEnumerator? _enumerator;
    private bool _disposed;

    /// <param name="useEchoCancellation">
    /// Ask Windows to cancel the far end out of the microphone stream. Only relevant when the
    /// user is on loudspeakers: Windows does not echo-cancel a second, independent capture
    /// client, so without this both streams end up containing the same voice and speaker
    /// attribution silently degrades.
    /// </param>
    /// <param name="microphoneDeviceId">
    /// Endpoint to record the user from, or null to follow the communications default.
    /// </param>
    /// <param name="outputDeviceId">
    /// Endpoint to capture the far end from, or null to follow the communications default.
    ///
    /// Worth choosing explicitly in one very ordinary case: listening on Bluetooth earphones
    /// while talking into the laptop microphone. Windows records those as two unrelated
    /// defaults, and a recorder that assumes one device does both captures an hour of silence
    /// from the far end — with no error, because an idle endpoint and a quiet conversation look
    /// exactly the same to a loopback client.
    /// </param>
    public WasapiCaptureBackend(
        bool useEchoCancellation = true,
        string? microphoneDeviceId = null,
        string? outputDeviceId = null)
    {
        _useEchoCancellation = useEchoCancellation;
        _microphoneDeviceId = microphoneDeviceId;
        _outputDeviceId = outputDeviceId;
    }

    /// <summary>Endpoints the last start actually used, for the self-test to report.</summary>
    public string? MicrophoneInUse { get; private set; }

    public string? OutputInUse { get; private set; }

    public string Name => "Cihaz loopback (WASAPI)";

    public AudioFormat Format { get; } = AudioFormat.WhisperPcm;

    public bool IsProcessIsolated => false;

    public event PacketHandler? PacketReady;

    public event EventHandler<string>? Interrupted;

    /// <summary>
    /// Opens both streams.
    ///
    /// Built asynchronously on purpose. Asking the stream to follow the default output device —
    /// which is what keeps a call alive when a headset is plugged in mid-conversation — makes
    /// NAudio activate the client through a callback, and it refuses a synchronous build rather
    /// than blocking on it.
    /// </summary>
    public async Task StartAsync(int? targetProcessId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        _enumerator = new MMDeviceEnumerator();

        // A chosen endpoint wins; otherwise the communications role, because a call runs on the
        // headset and that is frequently not the multimedia default. Getting this wrong records
        // an hour of silence with no error at all.
        var renderDevice = AudioDeviceCatalog.Find(_enumerator, _outputDeviceId)
            ?? Endpoint(DataFlow.Render)
            ?? throw new InvalidOperationException("Etkin bir ses çıkış cihazı bulunamadı.");

        var captureDevice = AudioDeviceCatalog.Find(_enumerator, _microphoneDeviceId)
            ?? Endpoint(DataFlow.Capture)
            ?? throw new InvalidOperationException("Etkin bir mikrofon bulunamadı.");

        MicrophoneInUse = NameOf(captureDevice);
        OutputInUse = NameOf(renderDevice);

        // Said out loud when a chosen device is missing. Recording continues on the default
        // rather than refusing, because an unplugged headset must not cost a conversation — but
        // silently recording the wrong endpoint is the failure this whole class is built around.
        if (_microphoneDeviceId is not null && AudioDeviceCatalog.Find(_enumerator, _microphoneDeviceId) is null)
            Interrupted?.Invoke(this, $"Seçilen mikrofon bulunamadı, {MicrophoneInUse} kullanılıyor.");

        if (_outputDeviceId is not null && AudioDeviceCatalog.Find(_enumerator, _outputDeviceId) is null)
            Interrupted?.Invoke(this, $"Seçilen çıkış cihazı bulunamadı, {OutputInUse} kullanılıyor.");

        var format = new WaveFormat(Format.SampleRate, Format.BitsPerSample, Format.Channels);

        _microphone = await BuildMicrophoneAsync(captureDevice, renderDevice, format);

        _loopback = await new WasapiRecorderBuilder()
            .WithDevice(renderDevice)
            .WithLoopbackCapture()
            .WithFormat(format)
            .WithEventSync()
            .WithSharedMode()
            .BuildAsync();

        _microphone.DataAvailable += (buffer, flags, _, qpc) =>
            Emit(StreamRole.Microphone, buffer, flags, qpc);

        _loopback.DataAvailable += (buffer, flags, _, qpc) =>
            Emit(StreamRole.Loopback, buffer, flags, qpc);

        _microphone.RecordingStopped += (_, e) => OnStopped("Mikrofon", e);
        _loopback.RecordingStopped += (_, e) => OnStopped("Hoparlör", e);

        StartKeepAlive(renderDevice);

        _microphone.StartRecording();
        _loopback.StartRecording();
    }

    /// <summary>
    /// Opens the microphone, with echo cancellation when the endpoint will allow it.
    ///
    /// Three things here are load-bearing and each one was got wrong once.
    ///
    /// The device is named explicitly, on the <c>Communications</c> role, rather than asking for
    /// automatic stream routing. Routing follows whatever Windows calls the *default* capture
    /// device, which on a machine with a headset is frequently not the device a call runs on —
    /// and listening to the wrong endpoint records an hour of silence with no error at all.
    /// NAudio also refuses the two together outright, which is what "Automatic stream routing
    /// follows the default device, so it cannot be combined with WithDevice()" means.
    ///
    /// <c>WithCommunicationsMode</c> is what asks Windows for the communications capture
    /// pipeline, and it is also what makes the echo-cancellation control available in the first
    /// place. Setting an AEC reference without it silently does nothing on most endpoints.
    ///
    /// The reference endpoint is requested, not assumed. It needs Windows 11 build 22621 and a
    /// driver that supports it, and asking on a machine without either throws during the build —
    /// so a refusal has to cost the echo cancellation rather than the whole recording.
    /// </summary>
    private async Task<WasapiRecorder> BuildMicrophoneAsync(
        MMDevice captureDevice,
        MMDevice renderDevice,
        WaveFormat format)
    {
        WasapiRecorderBuilder Basic() => new WasapiRecorderBuilder()
            .WithDevice(captureDevice)
            .WithFormat(format)
            .WithEventSync()
            .WithSharedMode()
            .WithCommunicationsMode();

        if (_useEchoCancellation)
        {
            try
            {
                return await Basic()
                    .WithEchoCancellationReferenceEndpoint(renderDevice)
                    .BuildAsync();
            }
            catch (Exception e)
            {
                // Not every driver or Windows build supports choosing the reference endpoint.
                // Losing echo cancellation means advising headphones; losing the recording
                // means losing the conversation.
                Interrupted?.Invoke(this, $"Yankı engelleme açılamadı, kayıt onsuz sürüyor: {e.Message}");
            }
        }

        return await Basic().BuildAsync();
    }

    /// <summary>
    /// Plays continuous silence into the render endpoint for as long as recording lasts.
    ///
    /// A loopback client receives no packets at all while nothing is being rendered. The
    /// timeline writer compensates by filling gaps from the QPC stamps, but keeping the audio
    /// engine running means most of those gaps never appear, which leaves far less to correct
    /// and far less that can go wrong.
    /// </summary>
    private void StartKeepAlive(MMDevice renderDevice)
    {
        try
        {
            _keepAlive = new WasapiPlayerBuilder()
                .WithDevice(renderDevice)
                .WithSharedMode()
                .WithLatency(200)
                .Build();

            _keepAlive.Init(new SilenceProvider(new WaveFormat(Format.SampleRate, 16, 2)));
            _keepAlive.Play();
        }
        catch (Exception)
        {
            // Purely an optimisation. Without it the gap filling simply does more work.
            _keepAlive = null;
        }
    }

    private void Emit(StreamRole role, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpc)
    {
        var handler = PacketReady;
        if (handler is null) return;

        var frames = buffer.Length / Format.BytesPerFrame;
        if (frames <= 0 && (flags & AudioClientBufferFlags.Silent) == 0) return;

        // The NAudio flags mirror AUDCLNT_BUFFERFLAGS_* one for one, and so does CaptureFlags.
        handler(role, new CapturedPacket(buffer, frames, qpc, (CoreCaptureFlags)(int)flags));
    }

    private void OnStopped(string which, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Interrupted?.Invoke(this, $"{which} akışı kesildi: {e.Exception.Message}");
    }

    private static string NameOf(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception)
        {
            return "bilinmeyen cihaz";
        }
    }

    private MMDevice? Endpoint(DataFlow flow)
    {
        if (_enumerator is null) return null;

        if (_enumerator.TryGetDefaultAudioEndpoint(flow, Role.Communications, out var communications))
            return communications;

        return _enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var multimedia) ? multimedia : null;
    }

    public void Stop()
    {
        TryStop(_microphone);
        TryStop(_loopback);

        _microphone?.Dispose();
        _loopback?.Dispose();
        _microphone = null;
        _loopback = null;

        try
        {
            _keepAlive?.Stop();
            _keepAlive?.Dispose();
        }
        catch (Exception)
        {
            // Already gone.
        }

        _keepAlive = null;

        _enumerator?.Dispose();
        _enumerator = null;

        static void TryStop(WasapiRecorder? recorder)
        {
            try
            {
                recorder?.StopRecording();
            }
            catch (Exception)
            {
                // Device already invalidated; nothing left to stop.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
