using System.Diagnostics;
using System.IO;
using System.Windows;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Update;

namespace VoiceTranscript.App.Views;

/// <summary>What the user decided.</summary>
public enum UpdateChoice
{
    /// <summary>Closed without deciding. Asked again next time.</summary>
    Later,

    /// <summary>Not this version. Asked again when a newer one appears.</summary>
    Skip,

    /// <summary>Downloaded, verified, and the installer was started.</summary>
    Installing,
}

/// <summary>
/// Offers an update, and installs it only if the user says so.
///
/// The user's decision, taken explicitly: <b>check and notify, never install silently.</b> So this
/// window exists at all, it shows what changed before asking, and nothing runs until the button is
/// pressed.
///
/// The order of operations at the end is the part worth reading. The installer is configured to
/// wait for the running application and never to close it — because closing it means killing it,
/// since the main window cancels its own close, and killing a tray recorder mid-call ends the
/// recording with its WAV headers unwritten. So the application has to get out of the way itself:
/// stop detecting, let anything in flight finish, write a marker so a silent failure can be
/// noticed afterwards, start the installer, and only then exit.
/// </summary>
public partial class UpdateWindow
{
    private readonly UpdateService _updates;
    private readonly Release _release;
    private readonly UpdateGuard _guard;

    public UpdateWindow(UpdateService updates, Release release, UpdateGuard guard)
    {
        InitializeComponent();

        _updates = updates;
        _release = release;
        _guard = guard;

        var running = AppVersion.OfRunningApplication();

        HeadlineText.Text = $"{release.Version} sürümü yayınlandı. Şu an {running} kullanıyorsun.";
        SizeText.Text = $"İndirilecek: {release.SizeBytes / (1024.0 * 1024.0):0} MB";

        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "Bu sürüm için not yazılmamış."
            : release.Notes.Trim();

        // A refusal is explained rather than expressed as a disabled button. "Şu anda bir görüşme
        // kaydediliyor" tells somebody what to do; a greyed-out button tells them nothing and
        // reads as the application being broken.
        if (guard.Explain() is { } blocked)
        {
            BlockedPanel.Visibility = Visibility.Visible;
            BlockedText.Text = blocked;
            UpdateButton.IsEnabled = false;
        }
    }

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        // Re-checked at the moment of pressing, not only when the window opened. A call can start
        // while somebody reads the release notes, and the guard is worthless if it only reflects
        // how things were a minute ago.
        if (Reevaluate() is { } blocked)
        {
            BlockedPanel.Visibility = Visibility.Visible;
            BlockedText.Text = blocked;
            UpdateButton.IsEnabled = false;
            return;
        }

        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "İndiriliyor…";

        var progress = new Progress<double>(fraction => ProgressBar.Value = fraction);

        var (path, failure) = await _updates.DownloadAsync(_release, progress);

        if (path is null)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            BlockedPanel.Visibility = Visibility.Visible;
            BlockedText.Text = failure ?? "İndirilemedi.";
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            return;
        }

        ProgressText.Text = "Kuruluyor. Uygulama kapanıp yeni sürümle açılacak.";

        if (!StartInstaller(path))
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            BlockedPanel.Visibility = Visibility.Visible;
            BlockedText.Text = "Kurulum başlatılamadı. Dosyayı elle çalıştırabilirsin:\n" + path;
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            return;
        }

        Choice = UpdateChoice.Installing;
        DialogResult = true;
        Close();
    }

    /// <summary>The guard as it stands right now, or null when the way is clear.</summary>
    private string? Reevaluate()
    {
        var orchestrator = App.Orchestrator;

        if (orchestrator is null) return _guard.Explain();

        return (_guard with
        {
            IsRecording = orchestrator.State == OrchestratorState.Recording || orchestrator.IsManualRecording,
            IsProcessing = orchestrator.State == OrchestratorState.Processing,
        }).Explain();
    }

    /// <summary>
    /// Hands over to the installer and stands aside.
    ///
    /// Silent, because the user has already agreed and seen what changed; making them click
    /// through a wizard they did not ask for would be asking twice. The installer restarts the
    /// application afterwards — an entry that exists solely for this path, because the ordinary
    /// ones are skipped during a silent install and the machine would otherwise be left with no
    /// recorder running at all.
    /// </summary>
    private bool StartInstaller(string installer)
    {
        try
        {
            _updates.RecordAttempt(AppVersion.OfRunningApplication(), _release.Version);

            // Stop watching before the installer starts. A call detected between here and process
            // exit would begin a recording that the exit then abandons.
            App.Orchestrator?.Dispose();

            Process.Start(new ProcessStartInfo(installer)
            {
                // /SILENT rather than /VERYSILENT so a progress window is visible: the application
                // is about to vanish, and something on screen is what distinguishes an update from
                // a crash.
                Arguments = "/SILENT /NOCANCEL /NORESTART",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer) ?? "",
            });

            AppLog.Write("güncelleme", $"{_release.Version} kurulumu başlatıldı, uygulama kapanıyor");

            // Exit rather than shut down the window: the installer is waiting on this process's
            // mutex and will sit there until it is released.
            Application.Current.Shutdown();

            return true;
        }
        catch (Exception e)
        {
            AppLog.Error("güncelleme", e, "kurulum başlatılamadı");
            return false;
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Skip;
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.Later;
        DialogResult = false;
        Close();
    }
}
