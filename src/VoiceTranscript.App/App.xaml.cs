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

        // Where everything is kept is settled before anything opens a file, because the log
        // itself has to go in the right place.
        //
        // Development happens on the machine this is actually used on: a real call, a real
        // capture device and a real call window exist nowhere else, so most faults can be
        // reproduced nowhere else either. That machine is also the one holding an archive of real
        // conversations, and an experimental build writing into it is how a month of recordings
        // is lost to a half-finished idea. `--data C:\vt-dev` keeps the two apart, and being
        // careful is not a substitute for that.
        var defaults = new AppPaths();

        // Read from the default location on purpose. DataRoot is stored in settings.json, which
        // lives inside the directory it names, so something has to break the circle — and it has
        // to be the fixed location, or a relocated archive could never be found again.
        var relocated = AppSettings.Load(defaults.SettingsFile).DataRoot;

        if (AppPaths.DataDirectoryFrom(e.Args) is null && AppPaths.AsksForDataDirectory(e.Args))
        {
            // Refused rather than ignored. Carrying on with the default would point a development
            // build straight at the real recordings — exactly what the switch was typed to avoid,
            // and the failure would be silent until something had already been written.
            MessageBox.Show(
                $"{AppPaths.DataSwitch} verildi ama arkasında bir klasör yok.\n\n" +
                $"Doğru kullanım:  VoiceTranscript.exe {AppPaths.DataSwitch} C:\\vt-dev\n\n" +
                "Varsayılan klasörle devam edilmedi: amaç zaten oraya dokunmamaktı.",
                "VoiceTranscript", MessageBoxButton.OK, MessageBoxImage.Warning);

            Shutdown();
            return;
        }

        Paths = new AppPaths(AppPaths.ResolveRoot(e.Args, relocated, defaults.Root));

        try
        {
            Paths.EnsureCreated();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            // Said out loud rather than crashed on. A mistyped path or a folder on a drive that is
            // not there would otherwise surface as an unhandled exception before the log exists,
            // which reports nothing to anybody.
            MessageBox.Show(
                $"Veri klasörü hazırlanamadı:\n\n{Paths.Root}\n\n{exception.Message}",
                "VoiceTranscript", MessageBoxButton.OK, MessageBoxImage.Error);

            Shutdown();
            return;
        }

        // Opened before anything else that can fail.
        //
        // This is developed on one machine and used on another, and the used one is the only
        // place capture, CUDA and real calls exist at all. Without a file on disk the entire
        // channel for reporting a fault is a screenshot of whatever happened to be on screen,
        // which is how a missing graphics library spent a day looking like a broken download.
        AppLog.Start(Paths.Logs, VersionString());
        HookCrashReporting();

        // A one-shot dump of every window the watched applications have, then exit.
        //
        // Which window a call puts a name in cannot be established where this is written — the
        // development machine is a virtual one with no audio hardware and no signed-in messenger.
        // Guessing at it has already cost this project a wrong contact for every unread count, so
        // the way to settle it is to look, on the machine where calls happen, while a call is
        // actually up.
        //
        // Not folded into the ordinary log on purpose: this output can contain a contact's name,
        // and that log is offered to the user to send to somebody else on the written promise that
        // it carries no such thing. This is produced only when asked for, into a file the user
        // chooses to share or not.
        if (e.Args.Any(a => string.Equals(a, WindowDumpSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            DumpWindowsAndExit();
            return;
        }

        Settings = AppSettings.Load(Paths.SettingsFile);

        // Before any window exists. The strings are resolved by a markup extension while the
        // markup is parsed, so a language chosen after a page has been built does not reach it —
        // which is why changing it asks for a restart rather than pretending to apply live.
        Localisation.Use(Settings.UiLanguage);

        // The data directory is logged first, and by name.
        //
        // Everything else in the log is about a database, a recording or a setting, and all three
        // depend on which directory is in use. A log that does not say which one it was is
        // ambiguous the moment a development build with --data exists, and that ambiguity would
        // land exactly when somebody is trying to work out why a conversation is not where they
        // left it.
        AppLog.Write("app", Paths.Root == new AppPaths().Root
            ? $"Veri klasörü: {Paths.Root}"
            : $"Veri klasörü: {Paths.Root}  (varsayılan DEĞİL)");

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

    /// <summary>Asks what windows WhatsApp, Telegram and Signal currently have, then quits.</summary>
    public const string WindowDumpSwitch = "--pencereler";

    /// <summary>
    /// Writes what the window watcher can see to a file and opens it.
    ///
    /// Run this <i>while a call is ringing or connected</i>. What it prints is the entire input to
    /// the naming decision — every visible top-level window of a watched application, its title,
    /// class, size, whether it is in front, and the verdict reached for each — followed by the name
    /// that would have been chosen. If a conversation is filed under the wrong person, this says
    /// why in one step instead of a round trip of guesses.
    /// </summary>
    private void DumpWindowsAndExit()
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "voicetranscript-pencereler.txt");

        try
        {
            using var watcher = new Capture.AudioSessionWatcher();

            var report =
                "VoiceTranscript — pencere tanısı" + Environment.NewLine
                + $"{DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss}" + Environment.NewLine
                + Environment.NewLine
                + "UYARI: aşağıdaki başlıklar kişi adı içerebilir. Paylaşmadan önce oku."
                + Environment.NewLine + Environment.NewLine
                + watcher.DescribeWindows(DateTimeOffset.Now);

            File.WriteAllText(file, report);

            MessageBox.Show(
                $"Pencere listesi yazıldı:\n\n{file}\n\nEn iyi sonuç için bunu bir arama "
                + "çalarken veya görüşme sürerken çalıştır.",
                "VoiceTranscript", MessageBoxButton.OK, MessageBoxImage.Information);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Pencere listesi alınamadı:\n\n{exception.Message}",
                "VoiceTranscript", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Shutdown();
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
