using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using VoiceTranscript.Capture;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Worker;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Views;

public partial class SettingsWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        Services.EscapeCloses.Attach(this);
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
            _ = Services.Dialogs.InfoAsync(this, "Veri klasörü", $"Klasör açılamadı: {ex.Message}");
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

        PageGeneral.Visibility = Visible(tag == "General");
        PageRecording.Visibility = Visible(tag == "Recording");
        PageTranscription.Visibility = Visible(tag == "Transcription");
        PageAnalysis.Visibility = Visible(tag == "Analysis");
        PageConsistency.Visibility = Visible(tag == "Consistency");
        PageData.Visibility = Visible(tag == "Data");
        PageExport.Visibility = Visible(tag == "Export");

        // Back to the top of the section just chosen. Without this the new section opened at
        // wherever the last one was scrolled to — a page that starts in its own middle, with
        // its heading somewhere above the fold, reads as broken layout rather than as scrolled.
        PageScroll?.ScrollToTop();

        static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Opens the window on a named section — the other half of every message that says
    /// "Ayarlar bölümünden…". A message that names the destination and a window that always
    /// opens at the front door were, together, a broken promise.
    /// </summary>
    public void ShowSection(string tag)
    {
        var radio = tag switch
        {
            "Recording" => NavRecording,
            "Transcription" => NavTranscription,
            "Analysis" => NavAnalysis,
            "Consistency" => NavConsistency,
            "Data" => NavData,
            "Export" => NavExport,
            _ => NavGeneral,
        };

        radio.IsChecked = true;
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
    /// <summary>The analysis model box asks the provider when opened, like the transcription one.</summary>
    private void LlmModelBox_DropDownOpened(object sender, EventArgs e)
    {
        if (_viewModel.RefreshLlmModelsCommand.CanExecute(null)) _viewModel.RefreshLlmModelsCommand.Execute(null);
    }

    /// <summary>The hosted-transcription model box fetches the service's list when opened.</summary>
    private void EndpointModelBox_DropDownOpened(object sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ViewModels.SttEndpointViewModel endpoint
            && endpoint.RefreshModelsCommand.CanExecute(null))
        {
            endpoint.RefreshModelsCommand.Execute(null);
        }
    }

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
            // Usable, not Available — this line was the exact green claim CudaReport.Usable
            // was added to prevent, still printed from the driver's device count.
            var device = hello.Cuda?.Usable == true
                ? (hello.Cuda.SelectedName is { } card ? $"Ekran kartında çalışacak: {card}" : "Ekran kartında çalışacak")
                : "CUDA yok, işlemcide çalışacak";

            _viewModel.DeviceSummary = device;

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

    /// <summary>
    /// Forgets every voice the application has learned.
    ///
    /// Beside the switch rather than buried, because a switch that starts collecting something
    /// derived from a person's body should have its undo within reach of the hand that turned it
    /// on. Immediate and not deferred to Save: somebody pressing this wants the data gone now,
    /// not after they remember to confirm a dialog.
    ///
    /// The contacts, calls and transcripts are untouched — only the voiceprints go, and the next
    /// call will simply ask who it was, as it did before the feature existed.
    /// </summary>
    private void ForgetVoices_Click(object sender, RoutedEventArgs e)
    {
        var removed = App.Repository?.DeleteAllVoiceprints() ?? 0;

        ShowVoiceStatus(removed > 0
            ? $"{Localisation.T("settingswindow.ses-izleri-silindi")} ({removed})"
            : Localisation.T("settingswindow.ses-izleri-silindi"));
    }

    /// <summary>
    /// Learns every voice the archive can teach, from calls the user has already labelled.
    ///
    /// The material is free — weeks of two-sided recordings, filed by hand — so the feature does
    /// not begin by asking anybody to read a sentence into a microphone. What it reports back is
    /// as much about the archive as about the voices: a recording that does not sound like the
    /// person it is filed under is usually filed under the wrong person, and this is the first
    /// time anything in this application has been able to say so.
    /// </summary>
    private async void LearnVoices_Click(object sender, RoutedEventArgs e)
    {
        if (App.Repository is not { } repository || App.Paths is not { } paths) return;

        LearnVoicesButton.IsEnabled = false;
        ShowVoiceStatus(Localisation.T("settingswindow.ses-izleri-kuruluyor"));

        try
        {
            var enrolment = new Services.VoiceEnrolment(
                repository,
                () => App.Worker,
                paths.Models,
                line => Dispatcher.InvokeAsync(() => ShowVoiceStatus(line)));

            var results = await enrolment.LearnEverybodyAsync(
                new Progress<(int Done, int Total)>(p =>
                    ShowVoiceStatus($"{p.Done}/{p.Total}")));

            var learned = results.Count(r => r.Learned);
            var suspect = results.Sum(r => r.Rejected.Count);

            ShowVoiceStatus(suspect > 0
                ? $"{learned} kişinin sesi öğrenildi. {suspect} görüşme, yazıldığı kişiye benzemiyor "
                  + "— etiketleri yanlış olabilir."
                : $"{learned} kişinin sesi öğrenildi.");
        }
        catch (Exception ex)
        {
            ShowVoiceStatus($"Ses izleri kurulamadı: {ex.Message}");
        }
        finally
        {
            LearnVoicesButton.IsEnabled = true;
        }
    }

    private void ShowVoiceStatus(string message)
    {
        VoiceStatus.Text = message;
        VoiceStatus.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
