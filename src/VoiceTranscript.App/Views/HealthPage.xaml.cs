using System.Windows;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Views;

public partial class HealthPage
{
    public HealthPage()
    {
        InitializeComponent();

        // The view picks the file because only it can show a dialog. Everything about what a
        // backup contains stays in the view model, so those rules live in one place rather than
        // in a click handler.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is HealthViewModel previous)
            {
                previous.DataActionRequested -= OnDataAction;
                previous.SettingsChangeRequested -= OnLogDetailChanged;
            }

            if (e.NewValue is HealthViewModel next)
            {
                next.DataActionRequested += OnDataAction;
                next.SettingsChangeRequested += OnLogDetailChanged;
            }
        };
    }

    /// <summary>
    /// Persists the log level straight away.
    ///
    /// This screen has no Save button, and the next thing somebody does after raising the level
    /// is reproduce the fault they are chasing — a setting that waited for confirmation would be
    /// off for precisely the run that mattered.
    ///
    /// This page is the setting's only home. It was briefly a switch here and a picker in
    /// Ayarlar, then a picker here bound to properties this page's view model did not have. WPF
    /// reports a broken binding by doing nothing at all, so the box was simply empty and the
    /// level could not be changed from anywhere.
    /// </summary>
    private void OnLogDetailChanged(object? sender, Core.Configuration.LogDetail level)
    {
        App.Settings = App.Settings with { LogDetail = level };
        App.Settings.Save(App.Paths.SettingsFile);

        Services.AppLog.Level = level;
        Services.AppLog.Write("app", $"gunluk ayrintisi: {level}");

        if (DataContext is HealthViewModel model)
            model.DataMessage = Localisation.T(
                "healthpage.gunluk-" + level.ToString().ToLowerInvariant() + "-secildi");
    }

    /// <summary>
    /// Brings the processing list forward.
    ///
    /// It lives here rather than in the shell, and that is the whole of the fault this exists to
    /// fix. The first screen's "N görüşme işlenemedi · Göster" navigated to a shell page called
    /// Processing — an entry in the enum that no view is bound to, because the list is a tab on
    /// this page. Every visibility binding said "not me", the content area emptied, and the
    /// button that promised to show four failures showed nothing at all. Twice: the first repair
    /// corrected which rows the list would hold and never checked that anybody could reach it.
    /// </summary>
    public void ShowProcessing() => Tabs.SelectedItem = ProcessingTab;

    private async void OnDataAction(object? sender, HealthViewModel.DataRequest request)
    {
        if (DataContext is not HealthViewModel model) return;

        var path = Ask(request);
        if (path is null) return;

        if (request == HealthViewModel.DataRequest.RestoreFromBackup)
        {
            // Asked plainly, because a restore from the wrong file is the one mistake here that
            // somebody would really regret. The current data is kept either way, and saying so
            // is what makes the answer easy to give.
            var confirmed = await Services.Dialogs.ConfirmAsync(
                Window.GetWindow(this), "Yedekten geri yükle",
                "Bu yedek uygulama yeniden başlatıldığında yerine konacak.\n\n" +
                "Şu anki verilerin silinmeyecek; yanında bir klasöre alınacak, " +
                "böylece yanlış dosya seçtiysen geri dönebilirsin.\n\nDevam edilsin mi?",
                okText: "Geri yükle");

            if (!confirmed) return;
        }

        if (request == HealthViewModel.DataRequest.ImportAndMerge)
        {
            // Said plainly because the two neighbouring buttons do opposite things, and the one
            // that keeps everything is the one that needs to say so out loud.
            var confirmed = await Services.Dialogs.ConfirmAsync(
                Window.GetWindow(this), "Yedeği içe aktar",
                "Bu yedekteki görüşmeler mevcut arşivine EKLENİR. Şu anki hiçbir şey silinmez " +
                "ve değiştirilmez; aynı görüşme her ikisinde de varsa buradaki olduğu gibi kalır.\n\n" +
                "Yeniden başlatma gerekmez.\n\nDevam edilsin mi?",
                okText: "İçe aktar");

            if (!confirmed) return;
        }

        await model.RunDataActionAsync(request, path);
    }

    private static string? Ask(HealthViewModel.DataRequest request)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");

        switch (request)
        {
            case HealthViewModel.DataRequest.BackupWithoutAudio:
            case HealthViewModel.DataRequest.BackupWithAudio:
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Yedeği nereye kaydedelim?",
                    FileName = $"VoiceTranscript-{stamp}.zip",
                    Filter = "Yedek dosyası (*.zip)|*.zip",
                    DefaultExt = ".zip",
                };

                return dialog.ShowDialog() == true ? dialog.FileName : null;
            }

            case HealthViewModel.DataRequest.ExportEverything:
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Görüşmeler hangi klasöre yazılsın?",
                };

                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            }

            case HealthViewModel.DataRequest.ImportAndMerge:
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Hangi yedek içe aktarılsın?",
                    Filter = "Yedek dosyası (*.zip)|*.zip",
                    CheckFileExists = true,
                };

                return dialog.ShowDialog() == true ? dialog.FileName : null;
            }

            case HealthViewModel.DataRequest.RestoreFromBackup:
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Hangi yedekten geri yüklensin?",
                    Filter = "Yedek dosyası (*.zip)|*.zip",
                    CheckFileExists = true,
                };

                return dialog.ShowDialog() == true ? dialog.FileName : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Puts the whole report on the clipboard.
    ///
    /// Asking for help should be one click rather than a dozen screenshots. The report carries no
    /// conversation data, no contact names and no paths beyond the drive letter, so it is safe to
    /// paste anywhere.
    /// </summary>
    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not HealthViewModel { HardwareReport: { } report }) return;

        try
        {
            Clipboard.SetText(report.ToPlainText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open. Not worth an error dialog.
        }
    }

    /// <summary>
    /// Puts the last few days of log on the clipboard.
    ///
    /// The last few days rather than only today, because a fault noticed on Monday was often
    /// caused on Friday — and asking somebody to work out which file to attach is asking them to
    /// diagnose it before they report it.
    /// </summary>
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Services.AppLog.Collect());
            Report("Son üç günün günlüğü panoya kopyalandı.");
        }
        catch (Exception exception)
        {
            // The clipboard can be held by another process; a very long log can also fail here.
            // Either way the folder button is the way through, so it is named.
            Report($"Kopyalanamadı ({exception.Message}). \"Klasörü aç\" ile dosyayı gönderebilirsin.");
        }
    }

    /// <summary>
    /// Empties the log, after asking.
    ///
    /// Asked because it is not recoverable and the log is the only record of what the application
    /// did — including the failure somebody may be about to report. Worth having anyway: clearing
    /// before reproducing a fault is how you get a log about one thing rather than three days of
    /// unrelated history.
    /// </summary>
    private async void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await Services.Dialogs.ConfirmAsync(
            Window.GetWindow(this), "Günlüğü temizle",
            "Günlük dosyaları silinecek. Bu, uygulamanın ne yaptığının tek kaydı — bildirmek "
            + "istediğin bir hata varsa önce \"Günlüğü kopyala\" ile al.\n\nDevam edilsin mi?",
            okText: "Temizle");

        if (!confirmed) return;

        var (removed, kept) = Services.AppLog.Clear();

        Report(kept == 0
            ? $"Günlük temizlendi ({removed} dosya)."
            : $"{removed} dosya silindi, {kept} tanesi kullanımda olduğu için kaldı.");
    }

    /// <summary>Says something back, in the same place data actions report themselves.</summary>
    private void Report(string message)
    {
        if (DataContext is HealthViewModel model) model.DataMessage = message;
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Services.AppLog.Directory;

        if (directory is null || !System.IO.Directory.Exists(directory))
        {
            Report("Günlük klasörü henüz oluşmadı.");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }
}
