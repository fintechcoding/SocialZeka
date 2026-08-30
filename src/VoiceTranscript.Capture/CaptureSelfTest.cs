using System.Runtime.Versioning;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Capture;

public sealed record CaptureTestResult(
    string BackendName,
    bool Succeeded,
    long MicPackets,
    long FarPackets,
    double MicPeak,
    double FarPeak,
    string? Error)
{
    public bool MicrophoneWorks => MicPackets > 0 && MicPeak > 0.005;

    /// <summary>
    /// Whether the far-end stream produced real audio rather than silence.
    ///
    /// Packet count alone is not enough. The process-loopback path has been observed handing
    /// back correctly-sized buffers full of zeroes, which looks exactly like success from the
    /// outside — and would mean recording an entire call of nothing.
    /// </summary>
    public bool LoopbackWorks => FarPackets > 0 && FarPeak > 0.005;

    /// <summary>Plain-language verdict for the settings window.</summary>
    /// <summary>
    /// Which endpoints were actually recorded from.
    ///
    /// Named in every result, because "no sound arrived" and "no sound arrived *from the laptop
    /// microphone while you were talking into your earphones*" are the same sentence to the
    /// application and completely different problems to the person reading it. Naming the device
    /// is what turns a dead end into an obvious fix.
    /// </summary>
    public string? MicrophoneDevice { get; init; }

    public string? OutputDevice { get; init; }

    private string Devices
    {
        get
        {
            if (MicrophoneDevice is null && OutputDevice is null) return "";

            return $" Mikrofon: {MicrophoneDevice ?? "bilinmiyor"}. " +
                   $"Çıkış: {OutputDevice ?? "bilinmiyor"}.";
        }
    }

    public string Summary
    {
        get
        {
            if (!Succeeded) return $"Yakalama başlatılamadı: {Error}";

            if (!MicrophoneWorks && !LoopbackWorks)
                return "Hiçbir akıştan ses gelmedi. Konuşurken ve bir şey çalarken tekrar deneyin." + Devices;

            if (!MicrophoneWorks)
            {
                return "Karşı taraf akışı çalışıyor ama mikrofondan ses gelmedi. "
                       + "Doğru mikrofon seçili mi?" + Devices;
            }

            if (!LoopbackWorks)
            {
                return "Mikrofon çalışıyor ama hoparlöre giden sesten kayıt alınamadı. "
                       + "Sınama sırasında bir ses çalıyor olmalı — ve o ses aşağıdaki çıkış "
                       + "cihazından çıkıyor olmalı. Kulaklıkla dinleyip bilgisayarın "
                       + "hoparlöründen çalıyorsan seçim yanlış demektir." + Devices;
            }

            return $"Her iki akış da çalışıyor (mikrofon {MicPackets} paket, "
                   + $"karşı taraf {FarPackets} paket)." + Devices;
        }
    }
}

/// <summary>
/// Records for a few seconds and reports whether both streams actually carried audio.
///
/// Worth having as an explicit button rather than trusting the first real call. Capture fails in
/// ways that produce no error at all: the wrong endpoint yields a file of pure silence, and the
/// per-process path has been seen returning zero-filled buffers that are indistinguishable from
/// success unless the samples are examined. Discovering either of those after an important
/// conversation is too late.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class CaptureSelfTest
{
    public static async Task<CaptureTestResult> RunAsync(
        IAudioCaptureBackend backend,
        TimeSpan duration,
        int? targetProcessId = null,
        CancellationToken cancellationToken = default)
    {
        long micPackets = 0, farPackets = 0;
        double micPeak = 0, farPeak = 0;

        void OnPacket(StreamRole role, CapturedPacket packet)
        {
            var peak = Peak(packet.Data);

            if (role == StreamRole.Microphone)
            {
                micPackets++;
                if (peak > micPeak) micPeak = peak;
            }
            else
            {
                farPackets++;
                if (peak > farPeak) farPeak = peak;
            }
        }

        backend.PacketReady += OnPacket;

        try
        {
            await backend.StartAsync(targetProcessId, cancellationToken);
            await Task.Delay(duration, cancellationToken);
            backend.Stop();
        }
        catch (Exception e)
        {
            return new CaptureTestResult(backend.Name, false, 0, 0, 0, 0, e.Message) with
            {
                MicrophoneDevice = Microphone(backend),
                OutputDevice = Output(backend),
            };
        }
        finally
        {
            backend.PacketReady -= OnPacket;
        }

        // Read after the run, because the backend only knows which endpoints it resolved once it
        // has actually opened them.
        return new CaptureTestResult(backend.Name, true, micPackets, farPackets, micPeak, farPeak, null) with
        {
            MicrophoneDevice = Microphone(backend),
            OutputDevice = Output(backend),
        };
    }

    private static string? Microphone(IAudioCaptureBackend backend) =>
        backend is WasapiCaptureBackend wasapi ? wasapi.MicrophoneInUse : null;

    private static string? Output(IAudioCaptureBackend backend) =>
        backend is WasapiCaptureBackend wasapi ? wasapi.OutputInUse : null;

    /// <summary>Loudest sample in a 16-bit PCM buffer, normalised to 0..1.</summary>
    private static double Peak(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(pcm[i..(i + 2)]));
            if (sample > peak) peak = sample;
        }

        return peak / 32768.0;
    }
}
