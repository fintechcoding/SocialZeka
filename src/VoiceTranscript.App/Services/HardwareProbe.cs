using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.App.Services;

/// <summary>What the machine is, and what that means for this application.</summary>
public sealed record HardwareReport
{
    public string Cpu { get; init; } = "";
    public int Cores { get; init; }
    public double RamGb { get; init; }
    public double FreeDiskGb { get; init; }
    public string DataDrive { get; init; } = "";

    public string? GpuName { get; init; }
    public double? GpuVramGb { get; init; }

    /// <summary>Whether CUDA is genuinely reachable, not merely whether a card is present.</summary>
    public bool CudaWorks { get; init; }

    public string? CudaProblem { get; init; }

    /// <summary>
    /// Measured speed, as a multiple of real time. Null until the benchmark has been run.
    ///
    /// Measured rather than inferred, because it cannot be inferred. The same card is a
    /// different machine depending on its driver, its power profile, and what else is holding
    /// video memory — and a laptop card throttles under a power budget it shares with the video
    /// encoder the call itself is using.
    /// </summary>
    public double? MeasuredSpeedFactor { get; init; }

    public string? MeasuredWith { get; init; }

    /// <summary>The model this machine should actually use.</summary>
    public AsrModel? RecommendedAsr { get; init; }

    public LocalLlmModel? RecommendedLlm { get; init; }

    public required string Verdict { get; init; }

    /// <summary>How long a one-hour call would take, from the measured figure.</summary>
    public TimeSpan? HourlyCallCost => MeasuredSpeedFactor is > 0
        ? TimeSpan.FromHours(1) / MeasuredSpeedFactor.Value
        : null;

    /// <summary>
    /// The whole report as text.
    ///
    /// Exists so that asking for help is one click rather than a dozen screenshots. Deliberately
    /// contains no conversation data, no contact names and no paths beyond the drive letter.
    /// </summary>
    public string ToPlainText()
    {
        var text = new StringBuilder();

        text.AppendLine("VoiceTranscript donanım raporu");
        text.AppendLine(new string('-', 34));
        text.AppendLine($"İşlemci      : {Cpu} ({Cores} çekirdek)");
        text.AppendLine($"Bellek       : {RamGb:0.#} GB");
        text.AppendLine($"Boş disk     : {FreeDiskGb:0.#} GB ({DataDrive})");
        text.AppendLine($"Ekran kartı  : {GpuName ?? "NVIDIA kart bulunamadı"}");

        if (GpuVramGb is { } vram) text.AppendLine($"Kart belleği : {vram:0.#} GB");

        text.AppendLine($"CUDA         : {(CudaWorks ? "çalışıyor" : CudaProblem ?? "kullanılamıyor")}");

        if (MeasuredSpeedFactor is { } speed)
        {
            text.AppendLine($"Ölçülen hız  : {speed:0.#}x gerçek zaman ({MeasuredWith})");

            if (HourlyCallCost is { } cost)
                text.AppendLine($"60 dk arama  : yaklaşık {cost.TotalMinutes:0.#} dakikada yazıya dökülür");
        }

        if (RecommendedAsr is not null) text.AppendLine($"Önerilen model: {RecommendedAsr.DisplayName}");
        if (RecommendedLlm is not null) text.AppendLine($"Önerilen LLM  : {RecommendedLlm.DisplayName}");

        text.AppendLine();
        text.AppendLine(Verdict);

        return text.ToString();
    }
}

/// <summary>
/// Answers the only hardware question that matters: will this machine actually do the job.
///
/// The specification sheet does not answer it. Two laptops with the same card behave completely
/// differently depending on the driver, on whether another application is holding video memory,
/// and on the power profile — and a card that is present but has no usable CUDA runtime looks
/// identical to a working one right up until the first call fails. So the card is asked what it
/// is, and then a real piece of audio is actually transcribed and timed.
/// </summary>
public sealed class HardwareProbe(AppPaths paths, EnvironmentSetup setup, string workerDirectory)
{
    /// <summary>Collects everything cheap: no GPU work, no model loading.</summary>
    public HardwareReport Describe()
    {
        var (cpu, cores) = Cpu();
        var (gpu, vram) = Nvidia();
        var drive = new DriveInfo(Path.GetPathRoot(paths.Root) ?? "C:\\");

        var report = new HardwareReport
        {
            Cpu = cpu,
            Cores = cores,
            RamGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024, 1),
            FreeDiskGb = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024 / 1024, 1),
            DataDrive = drive.Name,
            GpuName = gpu,
            GpuVramGb = vram,
            Verdict = "Henüz ölçülmedi.",
        };

        return report with { Verdict = Judge(report) };
    }

    /// <summary>
    /// The full test: describe the machine, ask the worker whether CUDA is real, then transcribe
    /// a short sample and time it.
    /// </summary>
    public async Task<HardwareReport> MeasureAsync(
        AppSettings settings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Donanım okunuyor…");
        var report = Describe();

        if (!File.Exists(setup.VenvPython))
        {
            return report with
            {
                CudaProblem = "Python ortamı kurulu değil, ölçüm yapılamadı.",
                Verdict =
                    "Ölçüm için önce Python ve Whisper paketleri gerekiyor. " +
                    "Bunları kurmadan da bulut üzerinden yazıya dökebilirsin.",
            };
        }

        var host = new Worker.PythonWorkerHost(new Worker.PythonWorkerOptions
        {
            PythonExecutable = setup.VenvPython,
            WorkerDirectory = workerDirectory,
            ModelCacheDirectory = paths.Models,
            Timeout = TimeSpan.FromMinutes(20),
        });

        progress?.Report("Ekran kartı sınanıyor…");

        try
        {
            var hello = await host.ProbeAsync(ct);

            report = report with
            {
                CudaWorks = hello.Cuda?.Available == true,
                CudaProblem = hello.Cuda?.Available == true
                    ? null
                    : hello.Cuda?.MissingDlls is { Count: > 0 } missing
                        ? $"Eksik kitaplık: {string.Join(", ", missing)}"
                        : hello.Cuda?.Hint ?? hello.Cuda?.Error ?? "CUDA cihazı bulunamadı",
            };
        }
        catch (Exception e)
        {
            report = report with { CudaProblem = e.Message };
        }

        var model = PickAsr(report);
        report = report with { RecommendedAsr = model, RecommendedLlm = PickLlm(report) };

        progress?.Report($"{model.DisplayName} ile gerçek bir ölçüm yapılıyor. İlk kez model indirilecekse sürebilir…");

        try
        {
            var result = await host.SelfTestAsync(new TranscriptionRequest
            {
                Id = "hardware-probe",
                Engine = model.Engine == AsrEngineKind.WhisperCpp ? "whisper.cpp" : "faster-whisper",
                ModelRef = model.ModelRef,
                Device = report.CudaWorks ? "cuda" : "cpu",
                Language = settings.Language,
                CacheDir = paths.Models,
            }, progress: null, ct);

            report = report with
            {
                MeasuredSpeedFactor = result.SpeedFactor > 0 ? result.SpeedFactor : null,
                MeasuredWith = $"{model.DisplayName}, {(report.CudaWorks ? "ekran kartı" : "işlemci")}",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            report = report with { CudaProblem = report.CudaProblem ?? $"Ölçüm başarısız: {e.Message}" };
        }

        return report with { Verdict = Judge(report) };
    }

    // ---- judgement ----------------------------------------------------------

    /// <summary>
    /// The model this machine should use.
    ///
    /// Video memory is the constraint, and the usable figure is well under the printed one:
    /// Windows itself holds half a gigabyte or more for the desktop compositor before anything
    /// else asks. Recommending a model that only fits on paper produces an out-of-memory failure
    /// on the first real call, which is the worst possible moment to discover it.
    /// </summary>
    private static AsrModel PickAsr(HardwareReport report)
    {
        if (!report.CudaWorks)
            return AsrCatalog.CpuCapable.OrderBy(m => m.VramGb).FirstOrDefault() ?? AsrCatalog.Default;

        var usable = Math.Max(0, (report.GpuVramGb ?? 6) - 1.2);
        var fitting = AsrCatalog.FittingIn(usable).ToList();

        // Among the models that fit, the most accurate on Turkish wins — that is the entire
        // reason the catalogue carries measured error rates rather than parameter counts.
        return fitting
                   .Where(m => m.Wer?.MediaSpeech is > 0)
                   .OrderBy(m => m.Wer!.MediaSpeech)
                   .FirstOrDefault()
               ?? fitting.OrderByDescending(m => m.VramGb).FirstOrDefault()
               ?? AsrCatalog.Default;
    }

    private static LocalLlmModel? PickLlm(HardwareReport report)
    {
        if (!report.CudaWorks) return null;

        var usable = Math.Max(0, (report.GpuVramGb ?? 6) - 1.2);
        return LocalLlmCatalog.FittingIn(usable).OrderByDescending(m => m.TotalGb).FirstOrDefault();
    }

    private static string Judge(HardwareReport report)
    {
        var text = new StringBuilder();

        if (report.CudaWorks && report.RecommendedAsr is { } model)
        {
            text.Append($"Bu makine {model.DisplayName} modelini yerelde çalıştırabilir");

            if (report.GpuVramGb is { } vram)
                text.Append($" ({model.VramGb:0.#} GB gerekiyor, kartta {vram:0.#} GB var)");

            text.Append(". ");
        }
        else if (report.GpuName is not null)
        {
            text.Append(
                $"{report.GpuName} görünüyor ama CUDA kullanılamıyor" +
                (report.CudaProblem is null ? "" : $" ({report.CudaProblem})") +
                ". Kart olmadan da çalışır, sadece daha yavaş. ");
        }
        else
        {
            text.Append("NVIDIA ekran kartı bulunamadı. Yazıya dökme işlemcide çalışır. ");
        }

        if (report.MeasuredSpeedFactor is { } speed && report.HourlyCallCost is { } cost)
        {
            text.Append(
                $"Ölçülen hız {speed:0.#}x gerçek zaman: 60 dakikalık bir arama yaklaşık " +
                $"{cost.TotalMinutes:0.#} dakikada yazıya dökülür. ");

            if (cost > TimeSpan.FromMinutes(30))
            {
                text.Append(
                    "Bu, bir görüşmenin ardından uzun bir bekleme demek. Daha küçük bir model " +
                    "seçebilir ya da bulut üzerinden yazıya döktürebilirsin. ");
            }
        }
        else
        {
            text.Append("Hız henüz ölçülmedi. ");
        }

        if (report.FreeDiskGb < 10)
        {
            text.Append(
                $"Diskte yalnızca {report.FreeDiskGb:0.#} GB boş yer var; model dosyaları ve ses " +
                "kayıtları için bu dar. ");
        }

        if (report.RecommendedLlm is { } llm)
            text.Append($"Çözümleme için {llm.DisplayName} bu karta sığar.");
        else if (report.CudaWorks)
            text.Append("Çözümleme için yerel model sığmıyor; bir API sağlayıcısı kullanılabilir.");

        return text.ToString().Trim();
    }

    // ---- machine facts ------------------------------------------------------

    private static (string Name, int Cores) Cpu()
    {
        var cores = Environment.ProcessorCount;

        // The registry rather than WMI: it is the same string, available instantly, and does not
        // pull in a management dependency for one line of text.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            if (key?.GetValue("ProcessorNameString") is string name)
                return (name.Trim(), cores);
        }
        catch (Exception)
        {
            // Not worth failing a hardware report over a missing registry key.
        }

        return ($"{cores} çekirdekli işlemci", cores);
    }

    /// <summary>
    /// Asks the NVIDIA driver directly.
    ///
    /// nvidia-smi rather than WMI, because Win32_VideoController reports AdapterRAM as a 32-bit
    /// value and silently wraps for anything above 4 GB — which is every card this application
    /// cares about, and would put "0 GB" or "2 GB" against a 6 GB card.
    /// </summary>
    private static (string? Name, double? VramGb) Nvidia()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return (null, null);

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) return (null, null);

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is null) return (null, null);

            var parts = first.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return (parts.Length == 1 ? parts[0] : null, null);

            return double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var mb)
                ? (parts[0], Math.Round(mb / 1024.0, 1))
                : (parts[0], null);
        }
        catch (Exception)
        {
            // No driver, no NVIDIA card, or nvidia-smi not on PATH. All mean the same thing here.
            return (null, null);
        }
    }
}
