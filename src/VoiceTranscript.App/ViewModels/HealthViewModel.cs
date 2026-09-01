using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Capture;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using Wpf.Ui.Controls;

namespace VoiceTranscript.App.ViewModels;

/// <summary>How a component is doing, in the only three states that matter to a person.</summary>
public enum HealthState
{
    Unknown,
    Good,
    Warning,
    Bad,
}

/// <summary>One thing that can be working or not.</summary>
public sealed partial class HealthItem : ObservableObject
{
    public required string Title { get; init; }
    public required SymbolRegular Icon { get; init; }

    /// <summary>What this is for, in a sentence. Shown before it has been checked.</summary>
    public required string Purpose { get; init; }

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private HealthState _state = HealthState.Unknown;
    [ObservableProperty] private string? _actionLabel;
    [ObservableProperty] private bool _isBusy;

    public bool HasAction => ActionLabel is not null;

    partial void OnActionLabelChanged(string? value) => OnPropertyChanged(nameof(HasAction));

    partial void OnStateChanged(HealthState value)
    {
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(StateBrushKey));
    }

    public SymbolRegular StateIcon => State switch
    {
        HealthState.Good => SymbolRegular.CheckmarkCircle24,
        HealthState.Warning => SymbolRegular.Warning24,
        HealthState.Bad => SymbolRegular.DismissCircle24,
        _ => SymbolRegular.Circle24,
    };

    public string StateBrushKey => State switch
    {
        HealthState.Good => "SystemFillColorSuccessBrush",
        HealthState.Warning => "SystemFillColorCautionBrush",
        HealthState.Bad => "SystemFillColorCriticalBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    /// <summary>What the button does. Assigned by the page rather than the item.</summary>
    public Func<HealthItem, Task>? Action { get; set; }
}

/// <summary>
/// One page that answers "is this thing actually working".
///
/// A recorder fails in ways that look exactly like success. The capture path can be pointed at
/// the wrong endpoint and record an hour of digital silence; the GPU can be present but have no
/// usable runtime; a hosted service can run out of credit overnight; the disk can fill. Each of
/// those produces a perfectly ordinary-looking application right up until somebody goes looking
/// for a conversation that was never kept.
///
/// So there is a page that asks all of it out loud, and each answer comes with the one button
/// that fixes it. This is also the page to screenshot when asking for help.
/// </summary>
public sealed partial class HealthViewModel : ObservableObject
{
    /// <summary>
    /// Starts processing whatever is queued. Wired by the application, which owns the recorder.
    ///
    /// "Başarısızları tekrar dene" set the rows to Queued and stopped there, so the message said
    /// the work was under way while nothing would touch it until the next launch.
    /// </summary>
    public Func<Task>? Requeue { get; set; }

    private readonly AppPaths _paths;
    private readonly Repository _repository;
    private readonly EnvironmentSetup _setup;
    private readonly HardwareProbe _hardware;
    private readonly Func<AppSettings> _settings;
    private readonly string _workerDirectory;

    public HealthViewModel(
        AppPaths paths,
        Repository repository,
        EnvironmentSetup setup,
        HardwareProbe hardware,
        Func<AppSettings> settings,
        string workerDirectory)
    {
        _paths = paths;
        _repository = repository;
        _setup = setup;
        _hardware = hardware;
        _settings = settings;
        _workerDirectory = workerDirectory;

        Items =
        [
            new HealthItem
            {
                Title = "Ses yakalama",
                Icon = SymbolRegular.Mic24,
                Purpose = "İki akıştan da gerçekten ses geliyor mu. Sessiz bir kayıt başarılı bir kayda benziyor.",
                ActionLabel = "Sına",
                Action = TestCaptureAsync,
            },
            new HealthItem
            {
                Title = "Yazıya dökme",
                Icon = SymbolRegular.DocumentText24,
                Purpose = "Model hazır mı, hangi cihazda çalışıyor.",
                ActionLabel = "Denetle",
                Action = CheckWorkerAsync,
            },
            new HealthItem
            {
                Title = "İşlem kuyruğu",
                Icon = SymbolRegular.ClipboardTaskListLtr24,
                Purpose = "Bekleyen ve başarısız olmuş kayıtlar.",
                ActionLabel = "Başarısızları tekrar dene",
                Action = RetryFailedAsync,
            },
            new HealthItem
            {
                Title = "Disk",
                Icon = SymbolRegular.Folder24,
                Purpose = "Kayıtların kapladığı yer ve kalan boş alan.",
                ActionLabel = "Klasörü aç",
                Action = OpenDataFolderAsync,
            },
            new HealthItem
            {
                Title = "Bulut servisleri",
                Icon = SymbolRegular.Cloud24,
                Purpose = "Yapılandırılmış yazıya dökme servisleri ve sıraları.",
            },
        ];

        foreach (var item in Items) item.Detail = item.Purpose;
    }

    public ObservableCollection<HealthItem> Items { get; }

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private HardwareReport? _hardwareReport;

    /// <summary>Everything that is currently wrong, for the summary line at the top.</summary>
    public int ProblemCount => Items.Count(i => i.State is HealthState.Warning or HealthState.Bad);

    public bool IsHealthy => ProblemCount == 0 && Items.All(i => i.State != HealthState.Unknown);

    private HealthItem Item(string title) => Items.First(i => i.Title == title);

    /// <summary>
    /// Checks everything that can be checked without touching hardware.
    ///
    /// The capture test is deliberately not part of this: it opens the microphone for five
    /// seconds, and doing that every time somebody glances at this page would be intrusive.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsChecking) return;

        IsChecking = true;

        try
        {
            UpdateQueue();
            UpdateDisk();
            UpdateCloud();

            await CheckWorkerAsync(Item("Yazıya dökme"));
        }
        finally
        {
            IsChecking = false;
            Announce();
        }
    }

    private void Announce()
    {
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(IsHealthy));
    }

    // ---- individual checks ---------------------------------------------------

    private void UpdateQueue()
    {
        var item = Item("İşlem kuyruğu");

        var pending = _repository.PendingWorkCount();
        var failed = _repository.FailedCalls(limit: 100).Count;

        item.Detail = (pending, failed) switch
        {
            (0, 0) => "Kuyruk boş, her şey işlendi.",
            (_, 0) => $"{pending} kayıt sırada.",
            (0, _) => $"{failed} kayıt işlenemedi. Ses diskte duruyor, tekrar denenebilir.",
            _ => $"{pending} kayıt sırada, {failed} tanesi başarısız oldu.",
        };

        item.State = failed > 0 ? HealthState.Warning : HealthState.Good;
        item.ActionLabel = failed > 0 ? "Başarısızları tekrar dene" : null;
    }

    private void UpdateDisk()
    {
        var item = Item("Disk");

        try
        {
            var used = DirectorySize(_paths.Recordings);
            var drive = new DriveInfo(Path.GetPathRoot(_paths.Root) ?? "C:\\");
            var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;

            item.Detail =
                $"Kayıtlar {Human(used)} yer kaplıyor. {_paths.DataDriveName(drive)} sürücüsünde " +
                $"{freeGb:0.#} GB boş.";

            // Roughly two gigabytes an hour once the archive copies are counted, so five is
            // about two more days of ordinary use — enough warning to do something about it.
            item.State = freeGb switch
            {
                < 2 => HealthState.Bad,
                < 5 => HealthState.Warning,
                _ => HealthState.Good,
            };
        }
        catch (IOException e)
        {
            item.Detail = $"Disk okunamadı: {e.Message}";
            item.State = HealthState.Warning;
        }
    }

    private void UpdateCloud()
    {
        var item = Item("Bulut servisleri");
        var settings = _settings();

        if (settings.AsrMode == TranscriptionMode.LocalOnly)
        {
            item.Detail = "Yazıya dökme yerelde çalışıyor; hiçbir ses makineden çıkmıyor.";
            item.State = HealthState.Good;
            return;
        }

        var endpoints = settings.UsableSttEndpoints;

        item.Detail = endpoints.Count switch
        {
            0 => "Bulut modu seçili ama kullanılabilir bir servis yok. Ayarlardan bir servis ekle.",
            1 => $"Tek servis: {endpoints[0].ResolvedName}. Cevap vermezse yedek yok.",
            _ => $"{endpoints.Count} servis sırayla denenecek: {string.Join(", ", endpoints.Select(e => e.ResolvedName))}.",
        };

        item.State = endpoints.Count switch
        {
            0 => HealthState.Bad,
            1 => HealthState.Warning,
            _ => HealthState.Good,
        };
    }

    private async Task CheckWorkerAsync(HealthItem item)
    {
        item.IsBusy = true;

        try
        {
            var settings = _settings();
            var report = await _setup.CheckAsync(_workerDirectory, settings.AsrModel.ModelRef);

            if (!report.Python.IsSatisfied)
            {
                item.Detail = "Python kurulu değil. Kurulum penceresinden kurulabilir, ya da bulut kullanılabilir.";
                item.State = settings.AsrMode == TranscriptionMode.LocalOnly ? HealthState.Bad : HealthState.Warning;
                return;
            }

            if (!report.Packages.IsSatisfied)
            {
                item.Detail = "Whisper paketleri kurulu değil.";
                item.State = settings.AsrMode == TranscriptionMode.LocalOnly ? HealthState.Bad : HealthState.Warning;
                return;
            }

            var device = report.Cuda.IsSatisfied ? "ekran kartında" : "işlemcide";

            item.Detail = report.Model.IsSatisfied
                ? $"{settings.AsrModel.DisplayName} hazır, {device} çalışacak."
                : $"{settings.AsrModel.DisplayName} indirilmemiş ({settings.AsrModel.DownloadGb} GB). " +
                  "İlk görüşmede inecek ve o görüşmeyi bekletecek.";

            item.State = report.Model.IsSatisfied ? HealthState.Good : HealthState.Warning;
        }
        catch (Exception e)
        {
            item.Detail = $"Denetlenemedi: {e.Message}";
            item.State = HealthState.Warning;
        }
        finally
        {
            item.IsBusy = false;
            Announce();
        }
    }

    private async Task TestCaptureAsync(HealthItem item)
    {
        item.IsBusy = true;
        item.Detail = "Beş saniye kayıt alınıyor. Konuş ve aynı anda bir ses çaldır…";

        try
        {
            var settings = _settings();

            using var backend = new WasapiCaptureBackend(
                settings.UseEchoCancellation,
                settings.MicrophoneDeviceId,
                settings.OutputDeviceId);

            var result = await CaptureSelfTest.RunAsync(backend, TimeSpan.FromSeconds(5));

            // Honest about what was just tested. Per-application capture cannot be exercised
            // outside a real call — it needs the messenger's process to follow — so this test
            // can only ever speak for the device path. Saying "green" without saying which path
            // let a configuration that never records at all pass its own check.
            item.Detail = settings.PreferProcessLoopback
                ? result.Summary +
                  " (Cihaz yakalama sınandı. \"Uygulama bazlı yakalama\" yalnızca gerçek bir " +
                  "arama sırasında sınanabilir — ilk aramadan sonra günlüğe bak.)"
                : result.Summary;
            item.State = result is { MicrophoneWorks: true, LoopbackWorks: true }
                ? HealthState.Good
                : HealthState.Bad;

            item.ActionLabel = "Tekrar sına";
        }
        catch (Exception e)
        {
            item.Detail = $"Sınama yapılamadı: {e.Message}";
            item.State = HealthState.Bad;
        }
        finally
        {
            item.IsBusy = false;
            Announce();
        }
    }

    private Task RetryFailedAsync(HealthItem item)
    {
        var failed = _repository.FailedCalls(limit: 100);

        foreach (var call in failed)
            _repository.SetCallState(call.Id, ProcessingState.Queued);

        item.Detail = failed.Count == 0
            ? "Tekrar denenecek bir şey yok."
            : $"{failed.Count} kayıt yeniden kuyruğa alındı.";

        UpdateQueue();

        // The queue is also started. Setting the rows to Queued and stopping there told the user
        // the work was under way while nothing would touch it until the next launch — the same
        // button on the overview does start the backlog, and this one has to as well.
        if (failed.Count > 0 && Requeue is { } requeue) _ = requeue();
        Announce();

        return Task.CompletedTask;
    }

    private Task OpenDataFolderAsync(HealthItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            item.Detail = $"Klasör açılamadı: {e.Message}";
        }

        return Task.CompletedTask;
    }

    // ---- data ownership ------------------------------------------------------

    [ObservableProperty] private string? _dataMessage;
    [ObservableProperty] private bool _isArchiving;

    /// <summary>Raised when an action needs a file or folder chosen by the user.</summary>
    public event EventHandler<DataRequest>? DataActionRequested;

    /// <summary>What the page should ask the user to pick.</summary>
    public enum DataRequest
    {
        BackupWithoutAudio,
        BackupWithAudio,
        ExportEverything,
        RestoreFromBackup,
    }

    [RelayCommand]
    private void Backup() => DataActionRequested?.Invoke(this, DataRequest.BackupWithoutAudio);

    [RelayCommand]
    private void BackupWithAudio() => DataActionRequested?.Invoke(this, DataRequest.BackupWithAudio);

    [RelayCommand]
    private void ExportEverything() => DataActionRequested?.Invoke(this, DataRequest.ExportEverything);

    [RelayCommand]
    private void Restore() => DataActionRequested?.Invoke(this, DataRequest.RestoreFromBackup);

    /// <summary>
    /// Performs whichever data action the page collected a path for.
    ///
    /// The view picks the file because only it can show a dialog; everything else happens here,
    /// so the rules about what a backup contains live in one place rather than in a click handler.
    /// </summary>
    public async Task RunDataActionAsync(DataRequest request, string path)
    {
        if (IsArchiving) return;

        IsArchiving = true;
        var progress = new Progress<string>(message => DataMessage = message);

        try
        {
            var service = new Core.Storage.BackupService(_paths, _repository);

            switch (request)
            {
                case DataRequest.BackupWithoutAudio:
                case DataRequest.BackupWithAudio:
                {
                    var result = await service.BackupAsync(
                        path, includeAudio: request == DataRequest.BackupWithAudio, progress);

                    DataMessage = $"Yedek yazıldı: {result.Files} dosya, {result.SizeText}.";
                    break;
                }

                case DataRequest.ExportEverything:
                {
                    var result = await service.ExportEverythingAsync(path, progress);
                    DataMessage = $"{result.Files} görüşme markdown olarak yazıldı.";
                    break;
                }

                case DataRequest.RestoreFromBackup:
                {
                    var staged = await service.StageRestoreAsync(path, progress);
                    DataMessage =
                        $"{staged} dosya hazırlandı. Uygulamayı kapatıp açtığında geri yüklenecek. " +
                        "Şu anki verilerin silinmeyecek, kenara alınacak.";
                    break;
                }
            }
        }
        catch (Exception e)
        {
            DataMessage = $"İşlem tamamlanamadı: {e.Message}";
        }
        finally
        {
            IsArchiving = false;
        }
    }

    /// <summary>Runs the full hardware measurement. Slow, so it has its own button.</summary>
    [RelayCommand]
    private async Task MeasureHardwareAsync()
    {
        if (IsChecking) return;

        IsChecking = true;

        try
        {
            HardwareReport = await _hardware.MeasureAsync(_settings());
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task RunItemAsync(HealthItem item)
    {
        if (item.Action is { } action) await action(item);
    }

    // ---- helpers -------------------------------------------------------------

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;

        var total = 0L;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // A file being written right now. Its size is not worth failing the page over.
            }
        }

        return total;
    }

    private static string Human(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }
}

internal static class AppPathsExtensions
{
    /// <summary>The drive letter, for a sentence about free space.</summary>
    public static string DataDriveName(this AppPaths _, DriveInfo drive) => drive.Name.TrimEnd('\\');
}
