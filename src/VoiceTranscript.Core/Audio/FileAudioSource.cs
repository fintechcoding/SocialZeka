using System.Buffers.Binary;

namespace VoiceTranscript.Core.Audio;

/// <summary>
/// Replays prepared WAV files as if they were live capture.
///
/// The development machine has no NVIDIA GPU and no audio hardware whatsoever, so real
/// dual-stream capture cannot be exercised there. This makes everything downstream of capture —
/// detection wiring, timeline alignment, file writing, transcription, analysis — testable
/// without a microphone, a call, or a second machine.
///
/// It reproduces the behaviour that actually matters, not just the data: with
/// <see cref="SkipSilence"/> the loopback stream withholds packets while it is quiet, exactly
/// as a real WASAPI loopback client does. That is the behaviour that silently shortens
/// recordings, so a test harness that did not reproduce it would be lying.
/// </summary>
public sealed class FileAudioSource : IAudioCaptureBackend
{
    private readonly string? _micPath;
    private readonly string? _farPath;
    private readonly int _packetMs;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _disposed;

    public FileAudioSource(string? micPath, string? farPath, int packetMs = 10)
    {
        if (micPath is null && farPath is null)
            throw new ArgumentException("At least one of the two streams must be supplied.");

        _micPath = micPath;
        _farPath = farPath;
        _packetMs = packetMs;
    }

    public string Name => "Dosyadan (geliştirme)";

    public AudioFormat Format { get; init; } = AudioFormat.WhisperPcm;

    public bool IsProcessIsolated => true;

    /// <summary>
    /// Withhold packets from the loopback stream while it is silent, the way a real loopback
    /// client does. On by default because reproducing that is the entire point.
    /// </summary>
    public bool SkipSilence { get; init; } = true;

    /// <summary>
    /// Replay in real time rather than as fast as possible. Off by default so tests run quickly.
    /// </summary>
    public bool RealTime { get; init; }

    public event PacketHandler? PacketReady;

    /// <summary>Never raised: replaying a file cannot lose a device mid-stream.</summary>
#pragma warning disable CS0067
    public event EventHandler<string>? Interrupted;
#pragma warning restore CS0067

    /// <summary>Begins replaying. Idempotent, so starting an already-running source is harmless.</summary>
    public Task StartAsync(int? targetProcessId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pump is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _pump = Task.Run(() => Pump(token), token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts if necessary, then blocks until both files are exhausted.
    ///
    /// Deliberately not a second replay pass: the recorder already starts the source, and
    /// pumping twice would deliver every packet to it twice.
    /// </summary>
    public void Replay(TimeSpan? timeout = null)
    {
        StartAsync().GetAwaiter().GetResult();

        try
        {
            _pump?.Wait(timeout ?? TimeSpan.FromMinutes(2));
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            // Surface the real failure — an unreadable file, say — rather than a wrapper that
            // hides it. Callers should see what a live backend would have thrown.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw();
        }
    }

    private void Pump(CancellationToken token)
    {
        var mic = _micPath is null ? null : ReadPcm(_micPath);
        var far = _farPath is null ? null : ReadPcm(_farPath);

        var frames = Format.SampleRate * _packetMs / 1000;
        var bytes = frames * Format.BytesPerFrame;
        var packets = Math.Max(mic is null ? 0 : mic.Length / bytes, far is null ? 0 : far.Length / bytes);

        for (var i = 0; i < packets && !token.IsCancellationRequested; i++)
        {
            var offset = i * bytes;

            // 100 ns units, matching what WASAPI reports.
            var qpc = (long)i * _packetMs * 10_000;

            if (mic is not null && offset + bytes <= mic.Length)
                PacketReady?.Invoke(StreamRole.Microphone, new CapturedPacket(mic.AsSpan(offset, bytes), frames, qpc, CaptureFlags.None));

            if (far is not null && offset + bytes <= far.Length)
            {
                var slice = far.AsSpan(offset, bytes);

                // A silent stretch means the real device would have sent nothing at all. Staying
                // quiet here is what makes the gap-filling path get exercised.
                if (!SkipSilence || !IsSilent(slice))
                    PacketReady?.Invoke(StreamRole.Loopback, new CapturedPacket(slice, frames, qpc, CaptureFlags.None));
            }

            if (RealTime) Thread.Sleep(_packetMs);
        }
    }

    private static bool IsSilent(ReadOnlySpan<byte> pcm)
    {
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            // A tiny threshold rather than exact zero: encoded silence is rarely all zeroes.
            if (Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm[i..])) > 16) return false;
        }

        return true;
    }

    /// <summary>Reads the data chunk of a WAV file, walking the chunk list rather than assuming
    /// a 44-byte header — recorders emit LIST and fact chunks too.</summary>
    private static byte[] ReadPcm(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"{path} is not a RIFF/WAVE file.");

        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(position, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4));
            var body = position + 8;

            if (id.SequenceEqual("data"u8))
            {
                var length = Math.Min(size, bytes.Length - body);
                return bytes.AsSpan(body, length).ToArray();
            }

            position = body + size + (size % 2); // chunks are word-aligned
        }

        throw new InvalidDataException($"{path} contains no data chunk.");
    }

    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation.
        }

        _cts?.Dispose();
        _cts = null;
        _pump = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
