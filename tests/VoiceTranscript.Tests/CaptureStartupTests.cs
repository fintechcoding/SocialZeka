using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

public sealed class CaptureStartupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreferredFailure_DuringBuildOrStart_RetriesPlainCapture(bool failDuringBuild)
    {
        var first = new Backend { Failure = new NotSupportedException("AEC reference unsupported") };
        var plain = new Backend();
        var notices = 0;
        var result = await CaptureStartup.StartAsync(true,
            preferred => preferred && failDuringBuild
                ? throw new NotSupportedException("Build failed")
                : Task.FromResult(preferred ? first : plain),
            backend => backend.StartAsync(),
            _ => notices++);
        Assert.Same(plain, result);
        Assert.Equal(1, notices);
        Assert.Equal(1, plain.Starts);
        Assert.Equal(!failDuringBuild, first.Disposed);
        Assert.False(plain.Disposed);
    }

    [Fact]
    public async Task FailedResource_IsDisposedBeforeReplacementStarts()
    {
        var first = new Backend { Failure = new NotSupportedException() };
        var result = await CaptureStartup.StartAsync(true,
            preferred =>
            {
                if (!preferred) Assert.True(first.Disposed);
                return Task.FromResult(preferred ? first : new Backend());
            }, backend => backend.StartAsync(), _ => { });
        result.Dispose();
    }

    [Fact]
    public async Task BothAttemptsFail_PreservesFallbackErrorAndDisposesBoth()
    {
        var first = new Backend { Failure = new NotSupportedException() };
        var failure = new IOException("Device disconnected");
        var plain = new Backend { Failure = failure };
        var actual = await Assert.ThrowsAsync<IOException>(() => CaptureStartup.StartAsync(true,
            preferred => Task.FromResult(preferred ? first : plain),
            backend => backend.StartAsync(), _ => { }));
        Assert.Same(failure, actual);
        Assert.True(first.Disposed);
        Assert.True(plain.Disposed);
    }

    [Fact]
    public async Task Cancellation_DoesNotStartFallback()
    {
        var first = new Backend { Failure = new OperationCanceledException() };
        var attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => CaptureStartup.StartAsync(true,
            _ => { attempts++; return Task.FromResult(first); },
            backend => backend.StartAsync(), _ => Assert.Fail("Must not retry cancellation")));
        Assert.Equal(1, attempts);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task CancelledBeforeStart_DoesNotOpenDevices()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CaptureStartup.StartAsync<Backend>(true,
            _ => throw new Exception("Must not create"),
            _ => Task.CompletedTask, _ => Assert.Fail("Must not retry"), cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MissingProcess_UsesDeviceAndForwardsBothChannels(int? pid)
    {
        var device = new Backend();
        using var wrapper = new FallbackCaptureBackend(() => throw new Exception("No process expected"), () => device);
        var roles = new List<StreamRole>();
        var notices = new List<string>();
        wrapper.PacketReady += (role, _) => roles.Add(role);
        wrapper.Interrupted += (_, reason) => notices.Add(reason);
        await wrapper.StartAsync(pid);
        device.Emit(StreamRole.Microphone);
        device.Emit(StreamRole.Loopback);
        device.Warn();
        Assert.Equal(new[] { StreamRole.Microphone, StreamRole.Loopback }, roles);
        Assert.False(wrapper.IsProcessIsolated);
        Assert.Equal(device.DevicesInUse, wrapper.DevicesInUse);
        Assert.Null(device.Pid);
        Assert.Equal(2, notices.Count);
    }

    [Fact]
    public async Task ProcessStartupFailure_FallsBackAndDetachesOldEvents()
    {
        var process = new Backend { IsProcessIsolated = true, Failure = new IOException("Process vanished") };
        var device = new Backend();
        using var wrapper = new FallbackCaptureBackend(() => process, () => device);
        var packets = 0;
        wrapper.PacketReady += (_, _) => packets++;
        await wrapper.StartAsync(123);
        Assert.Equal(123, process.Pid);
        Assert.True(process.Disposed);
        Assert.Same(device, wrapper.ActiveBackend);
        process.Emit(StreamRole.Loopback);
        device.Emit(StreamRole.Loopback);
        Assert.Equal(1, packets);
        wrapper.Stop();
        Assert.True(device.Disposed);
        device.Emit(StreamRole.Loopback);
        Assert.Equal(1, packets);
    }

    [Fact]
    public async Task SuccessfulProcess_DoesNotOpenDeviceFallback()
    {
        var process = new Backend { IsProcessIsolated = true };
        using var wrapper = new FallbackCaptureBackend(() => process, () => throw new Exception("Unexpected fallback"));
        await wrapper.StartAsync(123);
        Assert.True(wrapper.IsProcessIsolated);
        Assert.Same(process, wrapper.ActiveBackend);
    }

    private sealed class Backend : IAudioCaptureBackend
    {
        public string Name => "test";
        public AudioFormat Format => AudioFormat.WhisperPcm;
        public bool IsProcessIsolated { get; init; }
        public (string? Microphone, string? Output) DevicesInUse => ("mic", "headset");
        public event PacketHandler? PacketReady;
        public event EventHandler<string>? Interrupted;
        public Exception? Failure { get; init; }
        public bool Disposed { get; private set; }
        public int Starts { get; private set; }
        public int? Pid { get; private set; }
        public Task StartAsync(int? targetProcessId = null, CancellationToken cancellationToken = default)
        {
            Starts++;
            Pid = targetProcessId;
            return Failure is { } e ? Task.FromException(e) : Task.CompletedTask;
        }
        public void Emit(StreamRole role) => PacketReady?.Invoke(role, new CapturedPacket(new byte[2], 1, 1, CaptureFlags.None));
        public void Warn() => Interrupted?.Invoke(this, "device warning");
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}
