namespace VoiceTranscript.Core.Audio;

/// <summary>Keeps subscriptions intact when process capture cannot start and device capture is used.</summary>
public sealed class FallbackCaptureBackend(
    Func<IAudioCaptureBackend> processFactory,
    Func<IAudioCaptureBackend> deviceFactory) : IAudioCaptureBackend
{
    private bool _disposed;
    public IAudioCaptureBackend? ActiveBackend { get; private set; }
    public string Name => ActiveBackend?.Name ?? "Uygulama loopback (cihaz yedekli)";
    public AudioFormat Format => AudioFormat.WhisperPcm;
    // Before startup, the caller must resolve a target process for the preferred backend.
    public bool IsProcessIsolated => ActiveBackend?.IsProcessIsolated ?? true;
    public (string? Microphone, string? Output) DevicesInUse => ActiveBackend?.DevicesInUse ?? (null, null);
    public event PacketHandler? PacketReady;
    public event EventHandler<string>? Interrupted;

    public async Task StartAsync(int? targetProcessId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        cancellationToken.ThrowIfCancellationRequested();
        var hasTarget = targetProcessId is > 0;
        if (!hasTarget)
            Interrupted?.Invoke(this, "Uygulama işlemi bulunamadı; cihaz yakalama kullanılacak. Bu çıkıştaki diğer sesler de kaydedilir.");

        ActiveBackend = await CaptureStartup.StartAsync(
            hasTarget,
            preferred => Task.FromResult(preferred ? processFactory() : deviceFactory()),
            async backend =>
            {
                if (backend.Format != Format)
                    throw new InvalidOperationException("Yakalama biçimi kayıt biçimiyle uyuşmuyor.");
                backend.PacketReady += ForwardPacket;
                backend.Interrupted += ForwardInterruption;
                try
                {
                    await backend.StartAsync(backend.IsProcessIsolated ? targetProcessId : null, cancellationToken);
                }
                catch
                {
                    backend.PacketReady -= ForwardPacket;
                    backend.Interrupted -= ForwardInterruption;
                    throw;
                }
            },
            e => Interrupted?.Invoke(this, $"Uygulama yakalama başlatılamadı; cihaz yakalama deneniyor. Bu çıkıştaki diğer sesler de kaydedilir: {e.Message}"),
            cancellationToken);
    }

    private void ForwardPacket(StreamRole role, CapturedPacket packet) => PacketReady?.Invoke(role, packet);
    private void ForwardInterruption(object? sender, string reason) => Interrupted?.Invoke(this, reason);

    public void Stop()
    {
        var backend = ActiveBackend;
        ActiveBackend = null;
        if (backend is null) return;
        backend.PacketReady -= ForwardPacket;
        backend.Interrupted -= ForwardInterruption;
        try { backend.Stop(); }
        finally { backend.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
