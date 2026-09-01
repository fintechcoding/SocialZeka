using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Capture;
using VoiceTranscript.Core.Configuration;
using Wpf.Ui.Controls;

namespace VoiceTranscript.App.ViewModels;

public enum SetupStepKind
{
    Python,
    Packages,

    /// <summary>
    /// The NVIDIA runtime the GPU path needs — in practice, one library.
    ///
    /// Its own row because it has its own answer. "Whisper packages are installed" and "the
    /// graphics card can actually be used" are different questions, and folding them together
    /// let a machine report everything green and then fail every transcription on a missing
    /// cublas64_12.dll — after the call had ended, when the recording was the only copy of the
    /// conversation left.
    ///
    /// Everything behind this row already existed: EnvironmentSetup.DescribeGpu works out the
    /// state and InstallGpuRuntimeAsync fixes it. What was missing was anywhere that said so,
    /// so the answer and its button were both unreachable.
    /// </summary>
    Gpu,

    Hardware,
    Audio,
    Model,
}

public sealed partial class SetupStep : ObservableObject
{
    public required SetupStepKind Kind { get; init; }
    public required string Title { get; init; }
    public required SymbolRegular Icon { get; init; }

    /// <summary>One sentence on why this step exists, shown before it has run.</summary>
    public required string Purpose { get; init; }

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private PrerequisiteState _state = PrerequisiteState.Unknown;
    [ObservableProperty] private string? _actionLabel;
    [ObservableProperty] private bool _canAct = true;

    /// <summary>
    /// True while this particular step is working.
    ///
    /// Per-step rather than one global flag, because the progress has to appear *in the row the
    /// user just clicked*. A spinner somewhere else on the page — or worse, below the fold —
    /// reads as nothing having happened at all, which is precisely how this screen failed
    /// before: the button greyed out and the machine went quiet for four minutes.
    /// </summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>The line the step is currently reporting. Shown under the title while it runs.</summary>
    [ObservableProperty] private string _activity = "";

    public bool HasAction => ActionLabel is not null;

    partial void OnActionLabelChanged(string? value) => OnPropertyChanged(nameof(HasAction));

    partial void OnStateChanged(PrerequisiteState value)
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(IsBlocking));
    }

    /// <summary>Whether this step failing stops the application from working at all.</summary>
    public bool IsBlocking => State is PrerequisiteState.Missing or PrerequisiteState.Failed
                              && Kind is SetupStepKind.Python or SetupStepKind.Packages;

    public SymbolRegular Glyph => State switch
    {
        PrerequisiteState.Working or PrerequisiteState.Present => SymbolRegular.CheckmarkCircle24,
        PrerequisiteState.Missing => SymbolRegular.Warning24,
        PrerequisiteState.Failed => SymbolRegular.DismissCircle24,
        _ => SymbolRegular.Circle24,
    };

    public Brush StateBrush
    {
        get
        {
            var key = State switch
            {
                PrerequisiteState.Working or PrerequisiteState.Present => "SystemFillColorSuccessBrush",
                PrerequisiteState.Missing => "SystemFillColorCautionBrush",
                PrerequisiteState.Failed => "SystemFillColorCriticalBrush",
                _ => "TextFillColorTertiaryBrush",
            };

            return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        }
    }

    public void Report(string message) => Activity = message;
}

/// <summary>
/// Drives first-run setup, and does the work rather than asking the user to.
///
/// The whole chain runs on its own the moment this opens. That is the point: somebody who has
/// just installed a call recorder has no reason to know what a virtual environment is, and the
/// error they meet otherwise is a Windows Store stub complaining about an app execution alias,
/// which says nothing whatsoever about what is needed. The buttons remain for retrying a step
/// that failed, not for driving the process.
///
/// It also verifies the two things a specification sheet cannot tell you: that the GPU is
/// genuinely reachable and fast enough to be worth using, and that both audio streams actually
/// carry sound. A card with no usable CUDA runtime and a capture path that returns silence both
/// look like success until a real conversation has already been lost.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly EnvironmentSetup _setup;
    private readonly HardwareProbe _hardware;
    private readonly Func<AppSettings> _settings;
    private readonly string _workerDirectory;

    public SetupViewModel(
        EnvironmentSetup setup,
        HardwareProbe hardware,
        Func<AppSettings> settings,
        string workerDirectory)
    {
        _setup = setup;
        _hardware = hardware;
        _settings = settings;
        _workerDirectory = workerDirectory;

        LogFile = Path.Combine(setup.Paths.Logs, $"kurulum-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Steps =
        [
            new SetupStep
            {
                Kind = SetupStepKind.Python,
                Title = "Python",
                Icon = SymbolRegular.CodeBlock24,
                Purpose = "Whisper'ın olgun kütüphaneleri Python'da. Yoksa kendisi kurar.",
            },
            new SetupStep
            {
                Kind = SetupStepKind.Packages,
                Title = "Whisper paketleri",
                Icon = SymbolRegular.Box24,
                Purpose = "Sabitlenmiş sürümler ve CUDA kitaplık yolu, ayrı bir ortama kurulur.",
            },
            new SetupStep
            {
                Kind = SetupStepKind.Gpu,
                Title = "Ekran kartı",
                Icon = SymbolRegular.Flash24,
                Purpose = "NVIDIA kartı varsa hesaplama kütüphanesi kurulur — CUDA Toolkit gerekmez.",
            },
            new SetupStep
            {
                Kind = SetupStepKind.Hardware,
                Title = "Donanım testi",
                Icon = SymbolRegular.DeveloperBoard24,
                Purpose = "Bu makine hangi modeli kaldırır ve ne kadar sürer — tahmin değil, ölçüm.",
            },
            new SetupStep
            {
                Kind = SetupStepKind.Model,
                Title = "Model dosyaları",
                Icon = SymbolRegular.BrainCircuit24,
                Purpose = "Önceden indirilir ki ilk görüşme indirme beklemesin.",
            },
            new SetupStep
            {
                Kind = SetupStepKind.Audio,
                Title = "Ses yakalama",
                Icon = SymbolRegular.Mic24,
                Purpose = "İki akıştan da gerçekten ses geliyor mu — sessiz kayıt başarıya benziyor.",
            },
        ];

        foreach (var step in Steps) step.Detail = step.Purpose;
    }

    public ObservableCollection<SetupStep> Steps { get; }

    /// <summary>Rolling log of everything that happened, so a long install is visibly alive.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>
    /// Cancels whatever the wizard is doing.
    ///
    /// Every call it makes already accepted a token and none of them were given one, so pressing
    /// anything simply hid the window while pip, a Python child process and a download with a
    /// two-hour timeout carried on somewhere nobody could see or stop.
    /// </summary>
    private CancellationTokenSource? _work;

    public bool CanCancel => IsBusy;

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyTitle = "";
    [ObservableProperty] private string _lastMessage = "";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _hasRunOnce;
    [ObservableProperty] private HardwareReport? _hardwareReport;

    public bool IsIdle => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanCancel));
    }

    private CancellationToken Token => _work?.Token ?? CancellationToken.None;

    private SetupStep Step(SetupStepKind kind) => Steps.First(s => s.Kind == kind);

    /// <summary>
    /// Where everything this window does is written down.
    ///
    /// On disk rather than only on screen. An install that fails on somebody else's machine is
    /// unfixable without the actual error, and asking a person to expand a panel, scroll it and
    /// retype what they see is not a way to get one. This file is the answer to "how do we work
    /// out what went wrong".
    /// </summary>
    /// <summary>
    /// This run's own log file, shown inside the wizard.
    ///
    /// Built from the paths handed in rather than from the application's global, which was a
    /// field initialiser reading <c>App.Paths</c> — so this view model could not be constructed
    /// at all before startup had run, and nothing could build the wizard in a test. The one
    /// screen whose entire job is installing things was therefore the one screen never checked.
    /// </summary>
    public string LogFile { get; }

    private void Say(string message)
    {
        LastMessage = message;

        // Newest first: the interesting line is the one that just happened, and a log that
        // grows downwards puts it under the fold on exactly the long installs that need it.
        Log.Insert(0, message);
        while (Log.Count > 400) Log.RemoveAt(Log.Count - 1);

        Append(message);
    }

    private void Append(string message)
    {
        try
        {
            Directory.CreateDirectory(_setup.Paths.Logs);
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // A log that cannot be written must not stop an install that otherwise would work.
        }
    }

    /// <summary>
    /// True once something has failed, which opens the log rather than leaving it collapsed.
    ///
    /// A detail panel somebody has to know to expand is a detail panel nobody expands. The one
    /// moment the log matters is the moment a step goes red, so that is when it opens itself.
    /// </summary>
    [ObservableProperty] private bool _showLog;

    /// <summary>Everything in the log as one block, for the clipboard.</summary>
    public string LogText => string.Join(Environment.NewLine, Log.Reverse());

    // ---- the automatic run --------------------------------------------------

    /// <summary>
    /// Checks everything, then fixes whatever is missing, in dependency order.
    ///
    /// Runs by itself when the window opens. Nothing here needs a decision from the user: there
    /// is exactly one correct answer to "is Python installed" and exactly one correct response
    /// to "no". Asking would only be a way of making somebody responsible for a choice they have
    /// no information about.
    /// </summary>
    [RelayCommand]
    public async Task RunAllAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Problem = null;
        HasRunOnce = true;

        _work?.Dispose();
        _work = new CancellationTokenSource();

        try
        {
            Say($"Kurulum başladı. Günlük: {LogFile}");
            Say($"Worker klasörü: {_workerDirectory}");
            Say($"Python ortamı: {_setup.VenvPython}");
            Say($"Model klasörü: {_setup.Paths.Models}");

            await RefreshAsync(alreadyBusy: true);

            foreach (var kind in new[]
                     {
                         SetupStepKind.Python,
                         SetupStepKind.Packages,

                         // After the packages, because it installs into the environment they
                         // create; before the model and the measurement, because both are far
                         // slower on a processor and there is no reason to measure a machine in
                         // a state it is about to leave.
                         SetupStepKind.Gpu,

                         SetupStepKind.Model,
                         SetupStepKind.Hardware,
                     })
            {
                var step = Step(kind);

                // Hardware is measured every time; the rest only when they are actually missing.
                var needed = kind == SetupStepKind.Hardware
                    ? Step(SetupStepKind.Packages).State is PrerequisiteState.Present or PrerequisiteState.Working
                    : step.State is PrerequisiteState.Missing or PrerequisiteState.Unknown;

                if (!needed) continue;

                var ok = await ExecuteAsync(step);

                // Nothing downstream can succeed without an interpreter and its packages, so
                // stop rather than producing a column of red for one root cause.
                if (!ok && step.IsBlocking) break;
            }

            await RefreshAsync(alreadyBusy: true);
        }
        catch (OperationCanceledException)
        {
            Say("Kurulum durduruldu.");
        }
        finally
        {
            IsBusy = false;
            BusyTitle = "";
        }
    }

    [RelayCommand]
    public Task RefreshAsync() => RefreshAsync(alreadyBusy: false);

    private async Task RefreshAsync(bool alreadyBusy)
    {
        if (IsBusy && !alreadyBusy) return;

        if (!alreadyBusy) IsBusy = true;
        BusyTitle = "Denetleniyor";

        try
        {
            var report = await _setup.CheckAsync(
                _workerDirectory, _settings().AsrModel.ModelRef, Token);

            Apply(Step(SetupStepKind.Python), report.Python, "Kur");
            Apply(Step(SetupStepKind.Packages), report.Packages, "Kur");
            Apply(Step(SetupStepKind.Gpu), report.Cuda, "Kur");
            Apply(Step(SetupStepKind.Model), report.Model, "İndir");

            Append($"Denetim: Python={report.Python.State} ({report.Python.Detail})");
            Append($"Denetim: Paketler={report.Packages.State} ({report.Packages.Detail})");
            Append($"Denetim: Model={report.Model.State} ({report.Model.Detail})");
            Append($"Denetim: CUDA={report.Cuda.State} ({report.Cuda.Detail})");

            var hardware = Step(SetupStepKind.Hardware);
            if (HardwareReport is null)
            {
                hardware.State = report.Cuda.State;
                hardware.Detail = report.Cuda.IsSatisfied
                    ? report.Cuda.Detail
                    : report.Cuda.Detail + " Engel değil: işlemcide de çalışır, ya da buluta gönderilebilir.";
                hardware.ActionLabel = "Ölç";
            }

            var audio = Step(SetupStepKind.Audio);
            if (audio.State == PrerequisiteState.Unknown)
            {
                audio.Detail = audio.Purpose;
                audio.ActionLabel = "Sına";
            }

            IsReady = report.CanTranscribeLocally || _settings().AsrMode != TranscriptionMode.LocalOnly;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            Say($"Denetim başarısız: {e}");
            ShowLog = true;
        }
        finally
        {
            if (!alreadyBusy) IsBusy = false;
        }
    }

    private static void Apply(SetupStep step, Prerequisite prerequisite, string actionLabel)
    {
        step.State = prerequisite.State;
        step.Detail = prerequisite.Detail;
        step.ActionLabel = prerequisite.CanInstall ? actionLabel : null;
    }

    /// <summary>Runs one step from its own button.</summary>
    [RelayCommand]
    private async Task RunStepAsync(SetupStep step)
    {
        if (IsBusy) return;

        IsBusy = true;
        Problem = null;

        try
        {
            await ExecuteAsync(step);
            if (step.Kind != SetupStepKind.Audio) await RefreshAsync(alreadyBusy: true);
        }
        finally
        {
            IsBusy = false;
            BusyTitle = "";
        }
    }

    private async Task<bool> ExecuteAsync(SetupStep step)
    {
        BusyTitle = step.Title;
        step.IsRunning = true;
        step.Activity = "Başlıyor…";

        var progress = new Progress<string>(message =>
        {
            step.Report(message);
            Say($"{step.Title}: {message}");
        });

        try
        {
            var ok = step.Kind switch
            {
                SetupStepKind.Python => await _setup.InstallPythonAsync(progress, Token),
                SetupStepKind.Packages => await _setup.CreateEnvironmentAsync(_workerDirectory, progress, Token),
                SetupStepKind.Gpu => await _setup.InstallGpuRuntimeAsync(_workerDirectory, progress, Token),
                SetupStepKind.Model => await DownloadModelAsync(progress),
                SetupStepKind.Hardware => await MeasureHardwareAsync(progress),
                SetupStepKind.Audio => await TestAudioAsync(),
                _ => true,
            };

            if (ok)
            {
                // Recorded on success too.
                //
                // Without this the state stays whatever the first check said, and the next step
                // in the chain reads it and decides it is not needed — which is exactly how the
                // hardware measurement came to be skipped after a successful package install.
                // The steps that verify themselves set their own state and are left alone.
                if (step.Kind is SetupStepKind.Python or SetupStepKind.Packages
                    or SetupStepKind.Gpu or SetupStepKind.Model)
                    step.State = PrerequisiteState.Working;
            }
            else
            {
                step.State = PrerequisiteState.Failed;
                Problem ??= step.Activity;
                ShowLog = true;
            }

            return ok;
        }
        catch (OperationCanceledException)
        {
            // Somebody stopping the install is not an error, and painting the row red for it
            // would say the opposite.
            step.State = PrerequisiteState.Unknown;
            step.Report("İptal edildi.");
            Say($"{step.Title}: iptal edildi.");
            throw;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            step.State = PrerequisiteState.Failed;
            step.Report(e.Message);

            // The whole exception, not a summary. The worker puts Python stderr in here, and
            // that tail is usually the only thing that says what actually went wrong.
            Say($"{step.Title} BAŞARISIZ: {e}");
            ShowLog = true;

            return false;
        }
        finally
        {
            step.IsRunning = false;
        }
    }

    // ---- individual steps ---------------------------------------------------

    private async Task<bool> DownloadModelAsync(IProgress<string> progress)
    {
        var model = _settings().AsrModel;

        // Nothing can be fetched without the environment that fetches it. Said plainly rather
        // than letting the worker fail with a path error that means nothing to a person.
        if (!File.Exists(_setup.VenvPython))
        {
            progress.Report(
                "Python ortamı hazır olmadan model indirilemez. Önceki adımlar tamamlanmalı.");
            return false;
        }

        progress.Report($"{model.DisplayName} indiriliyor ({model.DownloadGb} GB). Bu uzun sürebilir…");
        Say($"Model indirme başladı: {model.ModelRef} → {_setup.Paths.Models}");

        var host = new Worker.PythonWorkerHost(new Worker.PythonWorkerOptions
        {
            PythonExecutable = _setup.VenvPython,
            WorkerDirectory = _workerDirectory,
            ModelCacheDirectory = _setup.Paths.Models,
            Timeout = TimeSpan.FromHours(2),
        });

        var result = await host.DownloadModelAsync(new Core.Asr.TranscriptionRequest
        {
            Id = "setup-download",
            ModelRef = model.ModelRef,
            CacheDir = _setup.Paths.Models,
        }, cancellationToken: Token);

        // Checked rather than assumed. A worker that answers without having fetched anything
        // would otherwise be reported as a successful download, and the failure would surface
        // much later as a call that will not transcribe.
        if (result.SizeMb < 20)
        {
            progress.Report(
                $"İndirme tamamlanmış görünmüyor: {result.SizeMb:0} MB. " +
                "Disk dolu olabilir ya da bağlantı kesilmiş olabilir.");
            return false;
        }

        progress.Report($"İndirildi: {result.Repository}, {result.SizeMb:0} MB");
        return true;
    }

    /// <summary>
    /// Measures rather than reads a specification.
    ///
    /// "Bu makine yeterli mi" cannot be answered from the card's name. The same 6 GB card is a
    /// different machine with an old driver, on battery, or with a browser holding video memory,
    /// so a short piece of audio is actually transcribed and timed. What the user gets is a
    /// sentence with a real number in it: how long a one-hour call will take on *their* machine.
    /// </summary>
    private async Task<bool> MeasureHardwareAsync(IProgress<string> progress)
    {
        var step = Step(SetupStepKind.Hardware);
        var report = await _hardware.MeasureAsync(_settings(), progress, Token);

        HardwareReport = report;
        step.Detail = report.Verdict;
        step.ActionLabel = "Yeniden ölç";

        step.State = report.MeasuredSpeedFactor is > 0
            ? PrerequisiteState.Working
            : PrerequisiteState.Missing;

        Say(report.CudaWorks
            ? $"Donanım: {report.GpuName} kullanılabiliyor."
            : $"Donanım: ekran kartı kullanılamıyor, işlemci ile devam.");

        return true;
    }

    /// <summary>
    /// Records briefly and checks that both streams carried real audio.
    ///
    /// The samples are examined, not just the packet count: the wrong endpoint yields a stream of
    /// pure silence and the per-process path has been seen returning zero-filled buffers, both of
    /// which look like success from the outside.
    /// </summary>
    private async Task<bool> TestAudioAsync()
    {
        var step = Step(SetupStepKind.Audio);
        step.Report("Konuş ve aynı anda bir ses çaldır…");

        using var backend = new WasapiCaptureBackend(
            _settings().UseEchoCancellation,
            _settings().MicrophoneDeviceId,
            _settings().OutputDeviceId);
        var result = await CaptureSelfTest.RunAsync(backend, TimeSpan.FromSeconds(5));

        step.Detail = result.Summary;
        step.State = result is { MicrophoneWorks: true, LoopbackWorks: true }
            ? PrerequisiteState.Working
            : PrerequisiteState.Missing;

        step.ActionLabel = "Tekrar sına";
        Say($"Ses yakalama: {result.Summary}");

        return step.State == PrerequisiteState.Working;
    }
}
