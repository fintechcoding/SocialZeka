using System.Diagnostics;
using System.IO;
using System.Text;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.App.Services;

public enum PrerequisiteState
{
    Unknown,
    Missing,
    Present,
    Working,
    Failed,
}

public sealed record Prerequisite(
    string Name,
    PrerequisiteState State,
    string Detail,
    bool CanInstall = false)
{
    public bool IsSatisfied => State is PrerequisiteState.Present or PrerequisiteState.Working;
}

public sealed record EnvironmentReport(
    Prerequisite Python,
    Prerequisite Packages,
    Prerequisite Cuda,
    Prerequisite Model)
{
    public IEnumerable<Prerequisite> All => [Python, Packages, Cuda, Model];

    /// <summary>
    /// Whether transcription can run at all.
    ///
    /// CUDA is deliberately not required: without a usable GPU the same models run on the
    /// processor, slowly but correctly, and the cloud route needs neither. Treating a missing
    /// GPU as a blocking failure would lock out machines that work perfectly well.
    /// </summary>
    public bool CanTranscribeLocally => Python.IsSatisfied && Packages.IsSatisfied && Model.IsSatisfied;
}

/// <summary>
/// Gets the machine into a state where the application actually works.
///
/// This exists because the first thing a new user meets otherwise is a Python interpreter that
/// is not installed, reported through a Windows Store stub error that explains nothing about
/// what this application needs. Asking somebody to install Python, create a virtual environment
/// and pip-install a pinned dependency set before their recorder works is not a reasonable
/// opening, so the application does it.
///
/// Everything here is reversible and visible: each step reports what it is doing, nothing is
/// installed silently, and the whole environment lives in one folder the user can delete.
/// </summary>
public sealed class EnvironmentSetup(AppPaths paths)
{
    private const string PythonPackageId = "Python.Python.3.12";

    /// <summary>Where this installation keeps everything. Exposed so callers need no global.</summary>
    public AppPaths Paths => paths;

    public string VenvDirectory => Path.Combine(paths.Root, "python");

    public string VenvPython => Path.Combine(VenvDirectory, "Scripts", "python.exe");

    /// <summary>Checks every prerequisite without changing anything.</summary>
    public async Task<EnvironmentReport> CheckAsync(
        string workerDirectory,
        string modelRef,
        CancellationToken cancellationToken = default)
    {
        var python = await CheckPythonAsync(cancellationToken);

        // Nothing downstream can be judged without an interpreter, so report the rest as unknown
        // rather than as failing — a red row for something that was never tried is misleading.
        if (!python.IsSatisfied)
        {
            return new EnvironmentReport(
                python,
                new Prerequisite("Paketler", PrerequisiteState.Unknown, "Python kurulduktan sonra denetlenir."),
                new Prerequisite("Ekran kartı", PrerequisiteState.Unknown, "Python kurulduktan sonra denetlenir."),
                new Prerequisite("Model", PrerequisiteState.Unknown, "Python kurulduktan sonra denetlenir."));
        }

        var probe = await ProbeWorkerAsync(workerDirectory, cancellationToken);

        if (probe is null)
        {
            return new EnvironmentReport(
                python,
                new Prerequisite("Paketler", PrerequisiteState.Missing,
                    "Whisper paketleri kurulmamış.", CanInstall: true),
                new Prerequisite("Ekran kartı", PrerequisiteState.Unknown, "Paketler kurulduktan sonra denetlenir."),
                new Prerequisite("Model", PrerequisiteState.Unknown, "Paketler kurulduktan sonra denetlenir."));
        }

        var engine = probe.Engines.FirstOrDefault(e => e.Name == "faster-whisper");

        var packages = engine?.Available == true
            ? new Prerequisite("Paketler", PrerequisiteState.Working, $"faster-whisper {engine.Version}")
            : new Prerequisite("Paketler", PrerequisiteState.Missing,
                engine?.Detail ?? "Kurulu değil.", CanInstall: true);

        var cuda = DescribeGpu(probe.Cuda);

        var model = probe.DownloadedModels.Contains(modelRef)
            ? new Prerequisite("Model", PrerequisiteState.Present, $"{modelRef} indirilmiş")
            : new Prerequisite("Model", PrerequisiteState.Missing, $"{modelRef} indirilmemiş.", CanInstall: true);

        return new EnvironmentReport(python, packages, cuda, model);
    }

    /// <summary>
    /// Turns what the worker reported about the graphics card into something true.
    ///
    /// The distinction this draws is the one that cost a real conversation. The device count is
    /// answered by the driver and comes back as 1 on any machine with a working NVIDIA card —
    /// including one where cublas64_12.dll cannot be loaded. The old check read that count,
    /// showed a green "CUDA hazır", and the recording then failed partway through transcription
    /// with a missing-library error, by which time the call was over.
    ///
    /// So there are four separate situations here, and only one of them is a dead end:
    ///
    ///   card present, library loads      — working, named
    ///   card present, library missing    — INSTALLABLE, and the common case after an upgrade
    ///   card present, CUDA not visible   — installable; usually the same missing runtime
    ///   no NVIDIA card                   — not a fault, and must not be presented as one
    ///
    /// The middle two used to render identically to the last one, which is why a machine with an
    /// RTX 4050 in it was being told to use the processor.
    /// </summary>
    private static Prerequisite DescribeGpu(CudaReport? cuda)
    {
        var missing = cuda?.MissingDlls is { Count: > 0 } list ? list : null;
        var cards = cuda?.Devices;
        var named = cuda?.SelectedName ?? cards?.FirstOrDefault()?.Name;

        if (cuda?.Usable == true)
        {
            var detail = named is not null
                ? $"{named} kullanılacak"
                : $"CUDA hazır, {cuda.DeviceCount} cihaz";

            // Worth saying out loud on a laptop: the integrated Intel or AMD chip is never a
            // candidate — CUDA does not enumerate it — so this is the discrete card by
            // construction, and when there are two the one with more memory was chosen.
            if (cards is { Count: > 1 }) detail += $" ({cards.Count} karttan en güçlüsü)";

            return new Prerequisite("Ekran kartı", PrerequisiteState.Working, detail);
        }

        if (missing is not null)
        {
            var card = named is not null ? $"{named} bulundu ama " : "";

            return new Prerequisite("Ekran kartı", PrerequisiteState.Missing,
                $"{card}hesaplama kütüphanesi eksik: {string.Join(", ", missing)}. " +
                "Kurulabilir — CUDA Toolkit gerekmez, yönetici yetkisi de istemez.",
                CanInstall: true);
        }

        if (cards is { Count: > 0 })
        {
            return new Prerequisite("Ekran kartı", PrerequisiteState.Missing,
                $"{named} bulundu ama CUDA görünmüyor. Çalışma kütüphanesi kurulacak; " +
                "sonrasında da görünmezse ekran kartı sürücüsü güncellenmeli.",
                CanInstall: true);
        }

        return new Prerequisite("Ekran kartı", PrerequisiteState.Missing,
            "NVIDIA ekran kartı bulunamadı. Engel değil: işlemcide çalışır ya da buluta gönderilebilir.");
    }

    /// <summary>
    /// Installs the NVIDIA runtime the GPU path needs, into the worker's own environment.
    ///
    /// Worth being precise about what this does and does not do, because the usual advice is
    /// wrong in an expensive way. It does <b>not</b> install the CUDA Toolkit — that is several
    /// gigabytes, wants administrator rights, and is a build-time dependency we have no use for.
    /// It does not install a driver either; the driver is already there, which is precisely how
    /// the card was detected. What is missing is one library, cublas64_12.dll, which ships as an
    /// ordinary Python package and installs into a folder the user owns.
    ///
    /// Nor is cuDNN needed. CTranslate2 4.6.3 moved conv1d to pure CUDA and dropped it, but both
    /// the faster-whisper README and the CTranslate2 install page still tell you to install
    /// cuDNN 9 — following that advice on this version costs a gigabyte and fixes nothing.
    ///
    /// The whole requirements file is installed rather than the one package, so an environment
    /// built by an older version of this application comes out matching the current pin set
    /// instead of merely gaining one library. pip is idempotent, so it costs seconds when there
    /// is nothing to do.
    /// </summary>
    public async Task<bool> InstallGpuRuntimeAsync(
        string workerDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(VenvPython))
        {
            progress?.Report("Önce Whisper paketleri kurulmalı.");
            return false;
        }

        progress?.Report("NVIDIA hesaplama kütüphanesi kuruluyor (yaklaşık 500 MB)...");

        var installed = await CreateEnvironmentAsync(workerDirectory, progress, cancellationToken);
        if (!installed) return false;

        // Verified by loading it, not by trusting pip's exit code. A package can install
        // perfectly and still be invisible to the loader, which is the entire reason this
        // problem exists on Windows.
        // The worker directory is put on the path explicitly rather than relied on as a working
        // directory, because RunAsync does not set one and a bare "python -c" resolves imports
        // against wherever the application happens to have been started from.
        var check = await RunAsync(
            VenvPython,
            $"-c \"import sys; sys.path.insert(0, r'{workerDirectory}'); " +
            "from vt_worker import dll_paths; m = dll_paths.missing_cuda_dlls(); " +
            "print('MISSING:' + ','.join(m) if m else 'OK')\"",
            cancellationToken: cancellationToken);

        if (check.Output.Contains("OK", StringComparison.Ordinal))
        {
            progress?.Report("Ekran kartı kullanıma hazır.");
            return true;
        }

        progress?.Report(
            "Kütüphane kuruldu ama hâlâ yüklenemiyor. Ekran kartı sürücüsü güncellenmeli. " +
            $"Ayrıntı: {Tail(check.Output)}");

        return false;
    }

    private static async Task<Prerequisite> CheckPythonAsync(CancellationToken cancellationToken)
    {
        var found = await FindSystemPythonAsync(cancellationToken);

        return found is null
            ? new Prerequisite("Python", PrerequisiteState.Missing,
                "Kurulu değil. Whisper bunsuz çalışamaz.", CanInstall: true)
            : new Prerequisite("Python", PrerequisiteState.Present, found);
    }

    /// <summary>
    /// Finds a real Python, ignoring the Microsoft Store stub.
    ///
    /// Windows ships an alias at python.exe that does nothing but open the Store, and it reports
    /// success in ways that look like a working interpreter until something actually runs. The
    /// launcher is tried first because it never resolves to the stub.
    /// </summary>
    private static async Task<string?> FindSystemPythonAsync(CancellationToken cancellationToken)
    {
        foreach (var (exe, args) in new[] { ("py", "-3.12 --version"), ("py", "-3 --version"), ("python", "--version") })
        {
            var result = await RunAsync(exe, args, cancellationToken: cancellationToken);

            if (result.ExitCode == 0 && result.Output.Contains("Python", StringComparison.OrdinalIgnoreCase))
                return result.Output.Trim();
        }

        return null;
    }

    /// <summary>
    /// Installs Python through the Windows package manager.
    ///
    /// winget rather than a downloaded installer: it is present on Windows 11, verifies what it
    /// fetches, and installs for the current user without demanding administrator rights — so
    /// the application never has to ask for elevation it does not otherwise need.
    /// </summary>
    public async Task<bool> InstallPythonAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Windows paket yöneticisi aranıyor…");

        var winget = await RunAsync("winget", "--version", cancellationToken: cancellationToken);
        if (winget.ExitCode != 0)
        {
            // Not an error worth stopping for. winget is absent on older Windows 10 builds and
            // switched off by policy on plenty of managed machines, and there is a perfectly
            // good second route.
            progress?.Report("winget yok, python.org üzerinden kurulacak…");
            return await InstallPythonFromPythonOrgAsync(progress, cancellationToken);
        }

        progress?.Report("Python 3.12 indiriliyor ve kuruluyor. Bu birkaç dakika sürebilir…");

        var install = await RunAsync(
            "winget",
            $"install --id {PythonPackageId} --scope user --silent " +
            "--accept-package-agreements --accept-source-agreements",
            timeout: TimeSpan.FromMinutes(15),
            cancellationToken: cancellationToken);

        // winget reports "already installed" as a failure code; that is a success for our purpose.
        if (install.ExitCode != 0 && !install.Output.Contains("already installed", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("winget kuramadı, doğrudan python.org yolu deneniyor…");

            if (!await InstallPythonFromPythonOrgAsync(progress, cancellationToken))
            {
                progress?.Report($"Kurulum başarısız: {Tail(install.Output)}");
                return false;
            }
        }

        progress?.Report("Python kuruldu, doğrulanıyor…");

        // A freshly installed interpreter is not on this process's PATH, which was captured at
        // launch. Re-reading the environment avoids telling the user it failed when it did not.
        RefreshPathFromRegistry();

        var found = await FindSystemPythonAsync(cancellationToken);
        if (found is null)
        {
            progress?.Report(
                "Python kuruldu ama bu oturumda görünmüyor. Uygulamayı kapatıp yeniden açın.");
            return false;
        }

        progress?.Report($"Hazır: {found}");
        return true;
    }

    /// <summary>
    /// Builds the virtual environment and installs the pinned dependency set.
    ///
    /// A dedicated environment rather than the system interpreter, because the pins here are
    /// deliberate and load-bearing — installing them globally would fight whatever else the user
    /// has, and the reasons are documented in requirements.txt.
    /// </summary>
    public async Task<bool> CreateEnvironmentAsync(
        string workerDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var requirements = Path.Combine(workerDirectory, "requirements.txt");
        if (!File.Exists(requirements))
        {
            progress?.Report($"requirements.txt bulunamadı: {requirements}");
            return false;
        }

        if (!File.Exists(VenvPython))
        {
            progress?.Report("Sanal ortam oluşturuluyor…");

            var launcher = await RunAsync("py", $"-3.12 -m venv \"{VenvDirectory}\"",
                timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);

            if (launcher.ExitCode != 0)
            {
                var fallback = await RunAsync("python", $"-m venv \"{VenvDirectory}\"",
                    timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);

                if (fallback.ExitCode != 0)
                {
                    progress?.Report($"Sanal ortam kurulamadı: {Tail(fallback.Output)}");
                    return false;
                }
            }
        }

        progress?.Report("Paketler indiriliyor. Yaklaşık 300 MB, birkaç dakika sürebilir…");

        var install = await RunAsync(
            VenvPython,
            $"-m pip install --disable-pip-version-check -r \"{requirements}\"",
            timeout: TimeSpan.FromMinutes(30),
            cancellationToken: cancellationToken,
            onLine: line =>
            {
                // pip is verbose; surface only the lines that mean something to a person.
                if (line.StartsWith("Collecting", StringComparison.Ordinal) ||
                    line.StartsWith("Installing", StringComparison.Ordinal) ||
                    line.StartsWith("Successfully", StringComparison.Ordinal))
                {
                    progress?.Report(line.Trim());
                }
            });

        if (install.ExitCode != 0)
        {
            progress?.Report($"Paket kurulumu başarısız: {Tail(install.Output)}");
            return false;
        }

        progress?.Report("CUDA kitaplık yolu ayarlanıyor…");
        WriteSiteCustomize();

        progress?.Report("Paketler hazır.");
        return true;
    }

    /// <summary>
    /// Makes the pip-installed NVIDIA runtime findable.
    ///
    /// Since Python 3.8 the Windows loader no longer searches PATH for the dependencies of C
    /// extension modules, so installing nvidia-cublas-cu12 is not enough on its own: the DLL
    /// lands somewhere valid that ctranslate2.dll will never look. Writing sitecustomize.py is
    /// more reliable than expecting every entry point to remember to register the directory.
    /// The LD_LIBRARY_PATH advice in the upstream README is Linux-only and silently does nothing
    /// here, which is why this failure is usually misdiagnosed as a broken CUDA install.
    /// </summary>
    private void WriteSiteCustomize()
    {
        var sitePackages = Directory
            .EnumerateDirectories(Path.Combine(VenvDirectory, "Lib"), "site-packages", SearchOption.AllDirectories)
            .FirstOrDefault() ?? Path.Combine(VenvDirectory, "Lib", "site-packages");

        Directory.CreateDirectory(sitePackages);

        File.WriteAllText(
            Path.Combine(sitePackages, "sitecustomize.py"),
            """
            import os, sys, glob, site

            if sys.platform == "win32":
                for _root in site.getsitepackages():
                    for _directory in glob.glob(os.path.join(_root, "nvidia", "*", "bin")):
                        try:
                            os.add_dll_directory(_directory)
                        except OSError:
                            pass
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private async Task<Core.Asr.WorkerHello?> ProbeWorkerAsync(
        string workerDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(VenvPython)) return null;

        try
        {
            var host = new Worker.PythonWorkerHost(new Worker.PythonWorkerOptions
            {
                PythonExecutable = VenvPython,
                WorkerDirectory = workerDirectory,
                ModelCacheDirectory = paths.Models,
                Timeout = TimeSpan.FromMinutes(2),
            });

            return await host.ProbeAsync(cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads PATH from the registry.
    ///
    /// A process inherits its environment at launch, so anything installed afterwards is
    /// invisible to it. Without this the application would report that the Python it just
    /// installed is missing, and tell the user to restart for no reason.
    /// </summary>
    private static void RefreshPathFromRegistry()
    {
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";

        Environment.SetEnvironmentVariable("PATH", $"{machine};{user}");
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan? timeout = null,
        Action<string>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var output = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            void Capture(string? line)
            {
                if (line is null) return;

                lock (output)
                {
                    if (output.Length < 32_768) output.AppendLine(line);
                }

                onLine?.Invoke(line);
            }

            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);

            if (!process.Start()) return new ProcessResult(-1, "Başlatılamadı.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return new ProcessResult(-1, "Zaman aşımı.");
            }

            string text;
            lock (output) text = output.ToString();

            return new ProcessResult(process.ExitCode, text);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The executable is not on PATH at all.
            return new ProcessResult(-1, $"{fileName} bulunamadı.");
        }
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.TakeLast(3)).Trim();
    }

    // ---- python.org fallback ------------------------------------------------

    /// <summary>Version fetched when winget cannot be used. Pinned deliberately.</summary>
    private const string PythonVersion = "3.12.10";

    private static string PythonInstallerUrl =>
        $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-amd64.exe";

    /// <summary>
    /// Downloads the official installer and runs it unattended.
    ///
    /// Needed because winget is not universal: it is missing on older Windows 10 builds and
    /// disabled by policy on many managed machines. Leaving those users at "install Python
    /// yourself" is exactly the wall this whole class exists to remove.
    ///
    /// The download is verified by its Authenticode signature rather than a hash pinned in
    /// source. A pinned hash goes stale the moment the version moves and then has to be either
    /// updated or quietly ignored, whereas the signature check keeps working and actually
    /// answers the question that matters: did the Python Software Foundation sign this file.
    /// Nothing is executed before that check passes.
    /// </summary>
    private async Task<bool> InstallPythonFromPythonOrgAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(Path.GetTempPath(), $"python-{PythonVersion}-amd64.exe");

        try
        {
            progress?.Report($"Python {PythonVersion} indiriliyor (~25 MB)…");

            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            using (var response = await http.GetAsync(
                       PythonInstallerUrl,
                       System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                await using var file = File.Create(target);
                await response.Content.CopyToAsync(file, cancellationToken);
            }

            progress?.Report("İmza doğrulanıyor…");

            var signer = await AuthenticodeSignerAsync(target, cancellationToken);
            if (signer is null || !signer.Contains("Python Software Foundation", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(
                    "İndirilen dosyanın imzası doğrulanamadı, çalıştırılmadı. " +
                    "Python'u python.org adresinden elle kurabilirsin.");
                return false;
            }

            progress?.Report("Kuruluyor. Bu birkaç dakika sürebilir…");

            // Per-user, no elevation, and on PATH so the launcher finds it afterwards.
            var run = await RunAsync(
                target,
                "/quiet InstallAllUsers=0 PrependPath=1 Include_pip=1 Include_test=0 " +
                "Include_launcher=1 SimpleInstall=1",
                timeout: TimeSpan.FromMinutes(20),
                cancellationToken: cancellationToken);

            if (run.ExitCode != 0)
            {
                progress?.Report($"Kurucu {run.ExitCode} koduyla çıktı. {Tail(run.Output)}");
                return false;
            }

            RefreshPathFromRegistry();

            var found = await FindSystemPythonAsync(cancellationToken);
            if (found is null)
            {
                progress?.Report("Python kuruldu ama bu oturumda görünmüyor. Uygulamayı yeniden başlat.");
                return false;
            }

            progress?.Report($"Hazır: {found}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            progress?.Report($"İndirilemedi: {e.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(target)) File.Delete(target); }
            catch (IOException) { /* the installer may still hold it; the temp folder is swept anyway */ }
        }
    }

    /// <summary>
    /// Returns the subject of the certificate a file is signed with, or null when it is not
    /// validly signed. Uses the Windows trust chain rather than reimplementing it.
    /// </summary>
    private static async Task<string?> AuthenticodeSignerAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "powershell",
            "-NoProfile -NonInteractive -Command " +
            $"\"$s = Get-AuthenticodeSignature -LiteralPath '{path}'; " +
            "if ($s.Status -eq 'Valid') { $s.SignerCertificate.Subject } else { '' }\"",
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);

        var subject = result.Output.Trim();
        return result.ExitCode == 0 && subject.Length > 0 ? subject : null;
    }
}
