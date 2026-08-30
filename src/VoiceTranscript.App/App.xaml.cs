using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Text;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Worker;

namespace VoiceTranscript.App;

public partial class App : Application
{
    /// <summary>
    /// Guards against a second copy running.
    ///
    /// Two instances would both record the same call into different files and both hold the
    /// database, which is a confusing mess to unpick afterwards. The autostart entry and a
    /// manual launch collide often enough that this matters.
    /// </summary>
    private static Mutex? _singleInstance;

    public static AppPaths Paths { get; private set; } = null!;
    public static Repository Repository { get; private set; } = null!;
    public static CallOrchestrator Orchestrator { get; private set; } = null!;
    public static PythonWorkerHost Worker { get; private set; } = null!;
    public static EnvironmentSetup Setup { get; private set; } = null!;
    public static HardwareProbe Hardware { get; private set; } = null!;
    public static string WorkerDirectory { get; private set; } = "";
    public static AppSettings Settings { get; set; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>The one shared client. Settings uses it for connection tests and balance probes.</summary>
    public static HttpClient HttpClient => Http;

    /// <summary>
    /// Records the failures nobody is there to see.
    ///
    /// A tray application runs for weeks. Most of what goes wrong with one goes wrong while the
    /// window is closed, on a background thread, at three in the morning — and an unhandled
    /// exception in WPF closes the process with a dialog the user dismisses and cannot describe
    /// afterwards. Written down, it is a fault report; not written down, it is "it crashed".
    ///
    /// The dispatcher handler marks the exception handled so the application survives it. A
    /// recorder that dies because a page failed to render has lost the conversation it was
    /// keeping, which is a far worse outcome than a screen that is briefly wrong.
    /// </summary>
    private void HookCrashReporting()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("çökme", args.Exception, "Arayüzde yakalanmamış hata");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                AppLog.Error("çökme", exception, "Yakalanmamış hata");
        };

        // Otherwise a failed background task raises nothing until the garbage collector notices,
        // by which point the stack no longer says where it came from.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("çökme", args.Exception, "Beklenmeyen görev hatası");
            args.SetObserved();
        };
    }

    private static string VersionString() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Global\VoiceTranscript.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "VoiceTranscript zaten çalışıyor. Simgesi görev çubuğunun bildirim alanında.",
                "VoiceTranscript", MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // Follow whatever the user has chosen for Windows itself, including their accent
        // colour. An application that forces its own palette reads as a web page in a window
        // frame rather than as part of the desktop.
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();

        Paths = new AppPaths();
        Paths.EnsureCreated();

        // Opened before anything else that can fail.
        //
        // This is developed on one machine and used on another, and the used one is the only
        // place capture, CUDA and real calls exist at all. Without a file on disk the entire
        // channel for reporting a fault is a screenshot of whatever happened to be on screen,
        // which is how a missing graphics library spent a day looking like a broken download.
        AppLog.Start(Paths.Logs, VersionString());
        HookCrashReporting();

        Settings = AppSettings.Load(Paths.SettingsFile);

        // Before any window exists. The strings are resolved by a markup extension while the
        // markup is parsed, so a language chosen after a page has been built does not reach it —
        // which is why changing it asks for a restart rather than pretending to apply live.
        Localisation.Use(Settings.UiLanguage);

        AppLog.Write("app", $"Ayarlar okundu: mod={Settings.AsrMode}, model={Settings.AsrModelId}, " +
                            $"otomatik kayıt={Settings.RecordAutomatically}, dil={Settings.UiLanguage}");

        // Recording into a synced folder would upload every conversation without a single
        // visible symptom, so it is refused rather than warned about.
        var cloud = AppPaths.DetectCloudSync(Paths.Recordings);
        if (cloud.Count > 0)
        {
            MessageBox.Show(
                $"Kayıt klasörü {string.Join(" ve ", cloud)} içinde görünüyor. Görüşme kayıtları " +
                "buluta yüklenirdi, bu yüzden uygulama başlatılmadı.\n\n" +
                $"Klasör: {Paths.Recordings}",
                "VoiceTranscript", MessageBoxButton.OK, MessageBoxImage.Warning);

            Shutdown();
            return;
        }

        // Before anything opens the database. A restore cannot be applied while the files are
        // held, which is why it is staged when the user asks and put into place here.
        if (Core.Storage.BackupService.ApplyPendingRestore(Paths) is { } aside)
        {
            MessageBox.Show(
                "Yedek geri yüklendi.\n\nÖnceki verilerin silinmedi, şu klasöre alındı:\n" + aside,
                "VoiceTranscript", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        var database = new Database(Paths.DatabaseFile);
        database.Migrate();
        Repository = new Repository(database);

        WorkerDirectory = ResolveWorkerDirectory();
        Setup = new EnvironmentSetup(Paths);
        Hardware = new HardwareProbe(Paths, Setup, WorkerDirectory);

        Worker = new PythonWorkerHost(new PythonWorkerOptions
        {
            PythonExecutable = ResolvePython(),
            WorkerDirectory = WorkerDirectory,
            ModelCacheDirectory = Paths.Models,

            // The worker's own account of what it did. This is where "gpu: RTX 4050 (6 GB)" and
            // "cuda unusable: cublas64_12.dll ... falling back to the processor" come from, and
            // both are invisible anywhere else.
            Diagnostic = line => AppLog.Write("worker", line),
        });

        Orchestrator = new CallOrchestrator(Paths, Repository, () => Settings, Worker, Http);

        // Everything the recorder says out loud is also written down. These are the messages
        // that explain a call that did not get recorded, and they are exactly the ones that
        // vanish when a notification bar is dismissed or the window is closed.
        Orchestrator.Notice += (_, message) => AppLog.Write("kayıt", message);
        Orchestrator.StateChanged += (_, state) => AppLog.Write("kayıt", $"durum → {state}");

        Orchestrator.CallFinished += (_, finished) => AppLog.Write("kayıt",
            $"görüşme #{finished.CallId} bitti · {finished.Duration:mm\\:ss} · {finished.App} " +
            $"· başlık {(finished.ObservedTitle is null ? "yok" : "var")}");

        var health = new ViewModels.HealthViewModel(
            Paths, Repository, Setup, Hardware, () => Settings, WorkerDirectory);

        var window = new MainWindow
        {
            DataContext = new ShellViewModel(Repository, Orchestrator, () => Settings, health, Paths),
        };
        MainWindow = window;

        // Watch after the window exists, not before: the watcher needs a window to hook, and
        // this application lives in the tray for weeks at a time, so it will be running when the
        // user switches Windows between light and dark — or when the scheduled switch does it
        // for them at sunset.
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(window);

        window.Show();

        // The wizard opens on a first run, and whenever the installer asks for it.
        //
        // The installer passes --setup so that the prerequisites are fetched as part of
        // installing rather than the first time somebody opens the window. Otherwise the first
        // thing a new user meets is a settings screen reporting exit code 9009 from a Windows
        // Store stub, which tells them nothing about what is missing.
        var wantsSetup = e.Args.Any(a =>
            string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase));

        if (wantsSetup || !File.Exists(Paths.SettingsFile)) ShowSetup(window);

        Orchestrator.Start();

        // Anything left queued by a crash or a shutdown mid-transcription is picked up now.
        _ = Task.Run(() => Orchestrator.ProcessBacklogAsync());
    }

    /// <summary>Opens the setup wizard. Also reachable from the main window at any time.</summary>
    public static void ShowSetup(Window? owner = null)
    {
        var wizard = new Views.SetupWindow(
            new ViewModels.SetupViewModel(Setup, Hardware, () => Settings, WorkerDirectory))
        {
            Owner = owner is { IsVisible: true } ? owner : null,
        };

        wizard.ShowDialog();

        // The wizard may have built the environment, so the worker has to be pointed at it.
        Worker = new PythonWorkerHost(new PythonWorkerOptions
        {
            PythonExecutable = ResolvePython(),
            WorkerDirectory = WorkerDirectory,
            ModelCacheDirectory = Paths.Models,

            // The worker's own account of what it did. This is where "gpu: RTX 4050 (6 GB)" and
            // "cuda unusable: cublas64_12.dll ... falling back to the processor" come from, and
            // both are invisible anywhere else.
            Diagnostic = line => AppLog.Write("worker", line),
        });
    }

    /// <summary>
    /// Locates the bundled Python, falling back to whatever is on PATH.
    ///
    /// AppContext.BaseDirectory rather than Assembly.Location: in a single-file publish the
    /// latter returns an empty string, which would silently resolve every bundled path to the
    /// wrong place.
    /// </summary>
    private static string ResolvePython()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        if (File.Exists(bundled)) return bundled;

        var venv = Path.Combine(Paths.Root, "python", "Scripts", "python.exe");
        return File.Exists(venv) ? venv : "python";
    }

    private static string ResolveWorkerDirectory()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "worker");
        if (Directory.Exists(bundled)) return bundled;

        // Running from a development checkout.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "worker");
            if (File.Exists(Path.Combine(candidate, "vt_worker", "__main__.py"))) return candidate;
            directory = directory.Parent;
        }

        return bundled;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Orchestrator?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
