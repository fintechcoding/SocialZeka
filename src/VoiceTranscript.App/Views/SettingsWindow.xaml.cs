using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using VoiceTranscript.Capture;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Worker;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class SettingsWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Null in the smoke test, which constructs every window without an App instance.
        DataFolderPath.Text = App.Paths?.Root ?? "";

        if (_lastSize is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }

        Closed += (_, _) => _lastSize = new Size(Width, Height);

        // Show up front which weights are already present, so nobody discovers mid-call that
        // a two-gigabyte download is about to start.
        _ = RefreshModelStatusAsync();
    }

    /// <summary>
    /// Opens the data folder in Explorer — the difference between "the archive is safe" and
    /// having to take the application's word for it.
    /// </summary>
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.Paths.Root)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Klasör açılamadı: {ex.Message}", "Veri klasörü",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Switches the visible category.
    ///
    /// The pages are collapsed siblings rather than a TabControl because the settings that matter
    /// here are long and scrolling: a tab strip puts a horizontal row of headers above content
    /// that is already dense, which is exactly the cramped shape this screen used to have.
    /// </summary>
    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        // Fires once during InitializeComponent, before the pages themselves exist.
        if (PageRecording is null) return;

        var tag = (sender as RadioButton)?.Tag as string;

        PageRecording.Visibility = Visible(tag == "Recording");
        PageTranscription.Visibility = Visible(tag == "Transcription");
        PageAnalysis.Visibility = Visible(tag == "Analysis");
        PageData.Visibility = Visible(tag == "Data");
        PageExport.Visibility = Visible(tag == "Export");

        // Back to the top of the section just chosen. Without this the new section opened at
        // wherever the last one was scrolled to — a page that starts in its own middle, with
        // its heading somewhere above the fold, reads as broken layout rather than as scrolled.
        PageScroll?.ScrollToTop();

        static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Last size the user made this window, kept for the session.
    ///
    /// Settings get opened repeatedly while tuning providers, and re-dragging the window larger
    /// every single time is friction with no compensating value. Session-scoped on purpose:
    /// persisting it would be another settings field for something a fresh start resets anyway.
    /// </summary>
    private static Size? _lastSize;

    private void BrowseVault_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Obsidian vault klasörünü seçin",
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.ObsidianVaultPath = dialog.FolderName;
        }
    }

    /// <summary>
    /// Opens the provider's catalogue so a model can be found by searching rather than recalled.
    ///
    /// The key is passed through because two of the three providers refuse to list anything
    /// without one. When it is missing the dialog says so plainly instead of returning an empty
    /// list, since "no models" and "you have not entered your key" are different problems with
    /// very different fixes.
    /// </summary>
    private void BrowseModels_Click(object sender, RoutedEventArgs e)
    {
        var provider = _viewModel.SelectedProvider;

        var baseUrl = string.IsNullOrWhiteSpace(_viewModel.LlmBaseUrl)
            ? provider.DefaultBaseUrl
            : _viewModel.LlmBaseUrl.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // Reported through LlmStatus, not Report(). Report writes into a label that lives on
            // the transcription page, and only one category page is visible at a time — so from
            // the analysis page this message was written to a collapsed TextBlock and the button
            // did nothing at all, visibly. Which is exactly the state you reach by picking
            // "Diğer (OpenAI uyumlu)", whose default address is deliberately empty.
            _viewModel.LlmStatus = "Önce sağlayıcının adresini gir.";
            _viewModel.LlmStatusIsGood = false;
            return;
        }

        var dialog = new ModelPickerWindow(
            App.HttpClient,
            provider.Kind,
            provider.DisplayName,
            baseUrl,
            string.IsNullOrWhiteSpace(_viewModel.LlmApiKey) ? null : _viewModel.LlmApiKey,
            _viewModel.LlmRemoteModel)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || dialog.ChosenModel is not { Length: > 0 } chosen) return;

        _viewModel.LlmRemoteModel = chosen;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Revalidate();

        // Refusing to save an invalid configuration rather than warning: a recorder pointed at a
        // cloud folder or a provider with no key fails silently at the worst possible moment.
        if (!_viewModel.IsValid) return;

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Records briefly and reports whether both streams carried real audio.
    ///
    /// Deliberately checks the samples, not just the packet count: the wrong endpoint yields a
    /// stream of pure silence and the per-process path has been seen returning zero-filled
    /// buffers, both of which look like success from the outside. Finding that out after an
    /// important conversation is too late.
    /// </summary>
    private async void TestCapture_Click(object sender, RoutedEventArgs e)
    {
        TestCaptureButton.IsEnabled = false;
        CaptureTestResult.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        CaptureTestResult.Text = "Sınanıyor… konuşun ve bir ses çaldırın.";

        try
        {
            var settings = _viewModel.ToSettings();

            using var backend = settings.PreferProcessLoopback
                ? new ProcessLoopbackCaptureBackend()
                : (Core.Audio.IAudioCaptureBackend)new WasapiCaptureBackend(
                    settings.UseEchoCancellation,
                    settings.MicrophoneDeviceId,
                    settings.OutputDeviceId);

            var result = await CaptureSelfTest.RunAsync(backend, TimeSpan.FromSeconds(5));

            CaptureTestResult.Text = result.Summary;
            CaptureTestResult.Foreground = (Brush)FindResource(
                result is { MicrophoneWorks: true, LoopbackWorks: true } ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
        }
        catch (Exception ex)
        {
            CaptureTestResult.Text = $"Sınama yapılamadı: {ex.Message}";
            CaptureTestResult.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
        }
        finally
        {
            TestCaptureButton.IsEnabled = true;
        }
    }

    // ---- model files -------------------------------------------------------

    private TranscriptionRequest ModelRequest(string id) => new()
    {
        Id = id,
        Engine = _viewModel.SelectedAsrModel.Engine == AsrEngineKind.WhisperCpp ? "whisper.cpp" : "faster-whisper",
        ModelRef = _viewModel.SelectedAsrModel.ModelRef,
        Device = _viewModel.AsrDevice,
        Language = "tr",
        CacheDir = App.Paths.Models,
    };

    private void SetBusy(bool busy, bool showProgress = false)
    {
        DownloadButton.IsEnabled = !busy;
        SelfTestButton.IsEnabled = !busy;
        RefreshStatusButton.IsEnabled = !busy;
        ModelProgress.Visibility = busy && showProgress ? Visibility.Visible : Visibility.Collapsed;
        ModelProgress.IsIndeterminate = busy && showProgress;
    }

    private void Report(string message, bool isProblem = false)
    {
        ModelStatus.Text = message;
        ModelStatus.Foreground = (Brush)FindResource(isProblem ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
    }

    private async Task RefreshModelStatusAsync()
    {
        try
        {
            var hello = await App.Worker.ProbeAsync();
            var model = _viewModel.SelectedAsrModel;

            // Every row gets its answer, not just the selected one — the table is where the
            // choice is made.
            _viewModel.DownloadedModelRefs = hello.DownloadedModels;

            var present = hello.DownloadedModels.Contains(model.ModelRef);
            var device = hello.Cuda?.Available == true
                ? $"CUDA hazır ({hello.Cuda.DeviceCount} cihaz)"
                : "CUDA yok, işlemcide çalışacak";

            Report(present
                ? $"{model.DisplayName} indirilmiş. {device}."
                : $"{model.DisplayName} henüz indirilmemiş ({model.DownloadGb} GB). {device}.");
        }
        catch (Exception e)
        {
            Report(Explain(e), isProblem: true);
        }
    }

    /// <summary>
    /// Turns a worker failure into something a person can act on.
    ///
    /// The message that used to appear here was <c>The worker exited with code 9009. Python was
    /// not found; run without arguments to install from the Microsoft Store</c> — the Windows
    /// app-execution-alias stub talking, in English, about a Store page that is not what this
    /// application needs. It is the single most likely error on a fresh machine and it explains
    /// nothing, so it is translated and pointed at the wizard that fixes it.
    /// </summary>
    private static string Explain(Exception e)
    {
        var text = e.Message;

        if (text.Contains("9009") || text.Contains("Python was not found", StringComparison.OrdinalIgnoreCase))
        {
            return
                "Python kurulu değil, bu yüzden yerel model çalıştırılamıyor. " +
                "Kurulum ve testler penceresini aç: gerekenleri kendisi kuruyor. " +
                "Ya da yukarıdan bulut seçeneğini seçersen Python hiç gerekmez.";
        }

        if (text.Contains("faster_whisper", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase))
        {
            return
                "Whisper paketleri kurulu değil. Kurulum ve testler penceresinden " +
                "kurabilirsin; birkaç dakika sürüyor.";
        }

        return $"Worker durumu alınamadı: {text}";
    }

    private async void RefreshModelStatus_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await RefreshModelStatusAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        var model = _viewModel.SelectedAsrModel;

        // A repository we have not confirmed must not be fetched on one click: the name may be
        // wrong, and a failed multi-gigabyte download is a poor way to find that out.
        if (model.RepositoryUnconfirmed)
        {
            Report(
                $"{model.DisplayName} için depo adresi doğrulanmadı. Bu modeli indirmeden önce " +
                "adresin doğruluğunu teyit etmek gerekiyor.",
                isProblem: true);
            return;
        }

        SetBusy(true, showProgress: true);
        Report($"{model.DisplayName} indiriliyor ({model.DownloadGb} GB)…");

        try
        {
            var result = await App.Worker.DownloadModelAsync(ModelRequest("download"));
            Report($"İndirildi: {result.Repository}, {result.SizeMb:0} MB.");
        }
        catch (Exception ex)
        {
            Report($"İndirilemedi: {ex.Message}", isProblem: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SelfTestModel_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, showProgress: true);
        Report($"{_viewModel.SelectedAsrModel.DisplayName} sınanıyor…");

        try
        {
            var result = await App.Worker.SelfTestAsync(ModelRequest("selftest"));
            Report($"{result.Summary} {result.Note}");
        }
        catch (Exception ex)
        {
            Report($"Sınama başarısız: {ex.Message}", isProblem: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
