using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using VoiceTranscript.Core.Asr;

namespace VoiceTranscript.Worker;

public sealed record PythonWorkerOptions
{
    /// <summary>
    /// Where the worker's diagnostics go, line by line, as they arrive.
    ///
    /// The worker writes to stderr and the tail is kept for the error message, but the tail is
    /// only read when a job *fails*. The lines that explain a job which merely came out wrong —
    /// which device was chosen, that the GPU was refused and the processor used instead, that a
    /// download fell back to a mirror — were written and then discarded.
    ///
    /// A hook rather than a reference to the log, so that this project stays free of a logging
    /// dependency and the tests can pass a list.
    /// </summary>
    public Action<string>? Diagnostic { get; init; }

    /// <summary>Interpreter to run. Must be python.exe, never pythonw.exe — see StartInfo below.</summary>
    public required string PythonExecutable { get; init; }

    /// <summary>Directory containing the vt_worker package.</summary>
    public required string WorkerDirectory { get; init; }

    /// <summary>
    /// Where model weights are cached. Kept outside the application directory on purpose: the
    /// installer replaces its own directory on every update, and re-downloading gigabytes of
    /// weights each time would be intolerable.
    /// </summary>
    public string? ModelCacheDirectory { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(2);
}

public sealed class WorkerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Runs the Python transcription worker as a supervised child process.
///
/// One job per process, by design. Process exit is the only mechanism that reliably returns all
/// GPU memory to the driver: deleting the model object and collecting garbage does not, because
/// CTranslate2 does not use torch and keeps its own caching allocator. Exiting also guarantees a
/// CUDA context is never held across a machine suspend, which invalidates it unrecoverably.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonWorkerHost(PythonWorkerOptions options)
{
    /// <summary>
    /// Asks the worker what it can actually do here: which engines import, whether CUDA is
    /// usable, and which runtime DLLs are missing. Drives what the settings UI offers, so the
    /// user is never shown a choice that cannot work on their machine.
    /// </summary>
    public async Task<WorkerHello> ProbeAsync(CancellationToken cancellationToken = default)
    {
        WorkerHello? hello = null;

        await RunAsync(
            "probe",
            requestJson: null,
            onEvent: e => { if (e is WorkerHello h) hello = h; },
            cancellationToken);

        return hello ?? throw new WorkerException("no_response", "The worker did not report its capabilities.");
    }

    /// <summary>
    /// Fetches model weights, reporting progress.
    ///
    /// Separate from transcription so that a multi-gigabyte download is a visible, cancellable
    /// step the user chose, rather than something that appears to hang the first real call.
    /// </summary>
    public async Task<WorkerDownloaded> DownloadModelAsync(
        TranscriptionRequest request,
        IProgress<WorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        WorkerDownloaded? downloaded = null;
        WorkerFailure? failure = null;

        await RunAsync(
            "download",
            WorkerProtocol.SerialiseRequest(request),
            onEvent: e =>
            {
                switch (e)
                {
                    case WorkerProgress p: progress?.Report(p); break;
                    case WorkerDownloaded d: downloaded = d; break;
                    case WorkerFailure f: failure = f; break;
                }
            },
            cancellationToken);

        if (failure is not null) throw new WorkerException(failure.Code, failure.Message);

        return downloaded ?? throw new WorkerException("no_result", "İndirme sonucu alınamadı.");
    }

    /// <summary>
    /// Loads a model and runs it once, so a real call is not the first time anything is tried.
    ///
    /// Proves the weights are intact, the device is reachable and the chain executes. It proves
    /// nothing about Turkish accuracy, and says so.
    /// </summary>
    public async Task<WorkerSelfTest> SelfTestAsync(
        TranscriptionRequest request,
        IProgress<WorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        WorkerSelfTest? result = null;
        WorkerFailure? failure = null;

        await RunAsync(
            "selftest",
            WorkerProtocol.SerialiseRequest(request),
            onEvent: e =>
            {
                switch (e)
                {
                    case WorkerProgress p: progress?.Report(p); break;
                    case WorkerSelfTest s: result = s; break;
                    case WorkerFailure f: failure = f; break;
                }
            },
            cancellationToken);

        if (failure is not null) throw new WorkerException(failure.Code, failure.Message);

        return result ?? throw new WorkerException("no_result", "Sınama sonucu alınamadı.");
    }

    /// <summary>Transcribes one call. Progress is reported as it arrives.</summary>
    public async Task<WorkerResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<WorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        WorkerResult? result = null;
        WorkerFailure? failure = null;

        await RunAsync(
            "transcribe",
            WorkerProtocol.SerialiseRequest(request),
            onEvent: e =>
            {
                switch (e)
                {
                    case WorkerProgress p: progress?.Report(p); break;
                    case WorkerResult r: result = r; break;
                    case WorkerFailure f: failure = f; break;
                }
            },
            cancellationToken);

        if (failure is not null) throw new WorkerException(failure.Code, failure.Message);

        return result ?? throw new WorkerException("no_result", "The worker exited without producing a transcript.");
    }

    private async Task RunAsync(
        string command,
        string? requestJson,
        Action<WorkerEvent> onEvent,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutable,
            WorkingDirectory = options.WorkerDirectory,
            UseShellExecute = false,   // required for redirection
            CreateNoWindow = true,     // this, not pythonw.exe, is what hides the console
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Both ends must be UTF-8 or Turkish text is destroyed: a Windows console defaults
            // to cp1254 or cp857, and the dotted and dotless i do not survive the round trip.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("vt_worker");
        startInfo.ArgumentList.Add(command);

        // Python block-buffers stdout when it is not a terminal, so without this no progress
        // arrives until the process exits.
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        if (!string.IsNullOrWhiteSpace(options.ModelCacheDirectory))
        {
            // HF_HUB_CACHE is the cache; HF_HOME is its parent, and setting only that put the
            // weights in a "hub" subfolder that the presence check never looked in. Both are set
            // so that the tokens directory and the cache end up somewhere sensible and, more
            // importantly, so that every command in the worker agrees on one location.
            startInfo.Environment["HF_HUB_CACHE"] = options.ModelCacheDirectory;
            startInfo.Environment["HF_HOME"] = options.ModelCacheDirectory;
            startInfo.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // The job object is created before the process starts so that the process cannot outlive
        // us even if we are killed in the window between Start and Assign.
        using var job = new JobObject();

        var stderr = new StringBuilder();
        var events = Channel.CreateUnbounded<WorkerEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { events.Writer.TryComplete(); return; }

            // Anything that is not a protocol line is ignored rather than fatal. A dependency
            // printing a warning must not abort a transcription that is otherwise fine.
            var parsed = WorkerProtocol.ParseLine(e.Data);
            if (parsed is not null) events.Writer.TryWrite(parsed);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            // Keep only the tail: a stack trace is useful, megabytes of warnings are not.
            lock (stderr)
            {
                if (stderr.Length > 16_384) stderr.Clear();
                stderr.AppendLine(e.Data);
            }

            // Passed on as it arrives as well as kept, so that a job which succeeds but chose
            // the wrong device still leaves a trace of having done so.
            try
            {
                options.Diagnostic?.Invoke(e.Data);
            }
            catch (Exception)
            {
                // A logger that throws must not take a transcription down with it.
            }
        };

        if (!process.Start())
            throw new WorkerException("start_failed", $"Could not start {options.PythonExecutable}");

        try
        {
            job.Assign(process.Handle);
        }
        catch (InvalidOperationException)
        {
            // Losing the safety net is not a reason to fail the job; it only means an orphan is
            // possible if this application is force-killed mid-transcription.
        }

        // Both pipes must be drained continuously. Waiting for exit with a full 4 KB pipe
        // buffer deadlocks the child, which is the classic way this goes wrong.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (requestJson is not null)
        {
            await process.StandardInput.WriteAsync(requestJson.AsMemory(), linked.Token);
        }

        process.StandardInput.Close(); // the worker reads stdin to EOF

        try
        {
            await foreach (var workerEvent in events.Reader.ReadAllAsync(linked.Token))
                onEvent(workerEvent);

            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            throw timeout.IsCancellationRequested
                ? new WorkerException("timeout", $"The worker exceeded its {options.Timeout} limit.")
                : new WorkerException("cancelled", "The transcription was cancelled.");
        }

        if (process.ExitCode != 0)
        {
            string tail;
            lock (stderr) tail = stderr.ToString().Trim();

            throw new WorkerException(
                "worker_failed",
                $"The worker exited with code {process.ExitCode}." +
                (tail.Length > 0 ? $"{Environment.NewLine}{tail}" : ""));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* already exiting */ }
    }
}
