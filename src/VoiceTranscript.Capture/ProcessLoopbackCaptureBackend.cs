using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceTranscript.Core.Audio;
using CoreCaptureFlags = VoiceTranscript.Core.Audio.CaptureFlags;

namespace VoiceTranscript.Capture;

/// <summary>
/// Captures one application in isolation, plus the microphone.
///
/// The far end is taken from the target process tree rather than from the output device, so
/// music, notification sounds and anything else playing stay out of the recording entirely.
/// That is genuinely better — when it works.
///
/// It is not the default, for reasons that were measured rather than assumed. On Windows 11
/// build 26200 the virtual device behind this API returns E_NOTIMPL for format negotiation, so
/// the format has to be asserted rather than agreed; reports a device position that is always
/// zero; and hands out QPC values that advance a fixed amount per packet instead of tracking a
/// clock. There are also reports of it delivering correctly-sized buffers full of nothing but
/// zeroes for some VoIP clients. Silence is indistinguishable from success, which is the worst
/// possible failure mode for a recorder.
///
/// So it is offered, verified against real audio at startup by <see cref="CaptureBackendSelector"/>,
/// and used only once it has proven itself on that machine.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ProcessLoopbackCaptureBackend : IAudioCaptureBackend
{
    private WasapiRecorder? _microphone;
    private WasapiRecorder? _loopback;
    private MMDeviceEnumerator? _enumerator;
    private bool _disposed;

    public string Name => "Uygulama loopback (process)";

    public AudioFormat Format { get; } = AudioFormat.WhisperPcm;

    public bool IsProcessIsolated => true;

    /// <summary>
    /// Only the microphone is a named endpoint here. The far end comes from a process tree, not
    /// from a device, so there is nothing to name — which is itself the answer worth recording:
    /// a call captured this way did not go through the output endpoint at all.
    /// </summary>
    public (string? Microphone, string? Output) DevicesInUse => (_microphoneInUse, "uygulamadan (cihaz yok)");

    private string? _microphoneInUse;

    public event PacketHandler? PacketReady;

    public event EventHandler<string>? Interrupted;

    /// <summary>
    /// Opens the process-loopback stream and the microphone.
    ///
    /// Must be asynchronous: WASAPI activates the process-loopback virtual device through a
    /// completion callback, so there is no synchronous build to call at all here.
    /// </summary>
    public async Task StartAsync(int? targetProcessId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (targetProcessId is not { } pid)
            throw new ArgumentNullException(nameof(targetProcessId), "Process loopback needs a target process.");

        Stop();
        _enumerator = new MMDeviceEnumerator();

        var format = new WaveFormat(Format.SampleRate, Format.BitsPerSample, Format.Channels);

        // Format is stated, not negotiated: this virtual device does not implement GetMixFormat
        // or IsFormatSupported, so asking would only produce E_NOTIMPL.
        _loopback = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(format)
            .BuildAsync();

        var captureDevice = Endpoint(DataFlow.Capture)
            ?? throw new InvalidOperationException("No active microphone was found.");

        _microphoneInUse = NameOf(captureDevice);

        _microphone = await new WasapiRecorderBuilder()
            .WithDevice(captureDevice)
            .WithFormat(format)
            .WithEventSync()
            .WithSharedMode()
            .WithDefaultDeviceStreamRouting()
            .BuildAsync();

        _microphone.DataAvailable += (buffer, flags, _, qpc) => Emit(StreamRole.Microphone, buffer, flags, qpc);
        _loopback.DataAvailable += (buffer, flags, _, qpc) => Emit(StreamRole.Loopback, buffer, flags, qpc);

        _microphone.RecordingStopped += (_, e) => OnStopped("Mikrofon", e);
        _loopback.RecordingStopped += (_, e) => OnStopped("Uygulama sesi", e);

        _microphone.StartRecording();
        _loopback.StartRecording();
    }

    private void Emit(StreamRole role, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpc)
    {
        var handler = PacketReady;
        if (handler is null) return;

        var frames = buffer.Length / Format.BytesPerFrame;
        if (frames <= 0 && (flags & AudioClientBufferFlags.Silent) == 0) return;

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
        foreach (var recorder in new[] { _microphone, _loopback })
        {
            try
            {
                recorder?.StopRecording();
                recorder?.Dispose();
            }
            catch (Exception)
            {
                // Device already invalidated.
            }
        }

        _microphone = null;
        _loopback = null;

        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
