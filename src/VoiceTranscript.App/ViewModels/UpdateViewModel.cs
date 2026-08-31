using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Update;

namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// Which version is running, and whether there is a newer one.
///
/// The check already existed and ran once at startup, silently, offering a dialog only when it
/// found something. That leaves two ordinary questions unanswerable from inside the application:
/// <b>which version am I running</b>, and <b>is there a newer one right now</b>. The first matters
/// every time somebody reports a problem — "it does this" is not useful without knowing what
/// "it" is. The second matters because a startup-only check means somebody who leaves the
/// application running for a fortnight, which is exactly how this one is meant to be used, is
/// told about nothing in between.
///
/// The check is deliberately manual here and stays that way. Automatic <i>installation</i> was
/// ruled out by the owner: the application may look, and must ask.
/// </summary>
public sealed partial class UpdateViewModel(
    Func<UpdateService?> service, Func<AppSettings> settings, Action<AppSettings> save) : ObservableObject
{
    /// <summary>What is running now. Reported from the assembly rather than from a constant.</summary>
    public string CurrentVersion
    {
        get
        {
            var version = AppVersion.OfRunningApplication();

            // A development build has no meaningful number and saying "0.0.0" invites a bug
            // report about the wrong thing.
            return version.IsDevelopmentBuild ? "geliştirme sürümü" : version.ToString();
        }
    }

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsGood;

    /// <summary>The release found, if any. Held so the install button has something to act on.</summary>
    [ObservableProperty] private Release? _found;

    public bool HasUpdate => Found is not null;

    partial void OnFoundChanged(Release? value) => OnPropertyChanged(nameof(HasUpdate));

    /// <summary>When the last check ran, in words rather than as a timestamp nobody parses.</summary>
    public string LastCheckedText => settings().LastUpdateCheck is not { } when
        ? "Henüz denetlenmedi."
        : $"Son denetim: {when.ToLocalTime():d MMMM HH:mm}";

    /// <summary>
    /// Whether the application looks for new versions on its own.
    ///
    /// Read at startup and, until this existed, settable nowhere — so somebody who did not want
    /// their machine contacting GitHub had no way to say so. It only ever looks; nothing installs
    /// without being asked.
    /// </summary>
    public bool CheckAutomatically
    {
        get => settings().CheckForUpdates;
        set
        {
            if (settings().CheckForUpdates == value) return;

            save(settings() with { CheckForUpdates = value });
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking || service() is not { } updates) return;

        IsChecking = true;
        Status = "Denetleniyor…";
        StatusIsGood = false;
        Found = null;

        try
        {
            var check = await updates.CheckAsync();

            if (check.Available && check.Release is { } release)
            {
                Found = release;
                Status = $"Yeni sürüm var: {release.Version}";
                StatusIsGood = true;
            }
            else
            {
                // A failed check and an up-to-date application are different answers, and the
                // service distinguishes them. Reporting a network failure as "up to date" would
                // be the same class of lie as a green tick over a key that does not work.
                Status = check.Message ?? "En güncel sürümü kullanıyorsun.";
                StatusIsGood = check.Message is null;
            }

            save(settings() with { LastUpdateCheck = DateTimeOffset.UtcNow });
            OnPropertyChanged(nameof(LastCheckedText));
        }
        catch (Exception e)
        {
            Status = $"Denetlenemedi: {e.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Raised when the user asks to install what was found.
    ///
    /// The window that does the downloading needs an owner and lives in the view layer, so this
    /// says what happened and lets the screen open it. A view model that shows its own dialogs
    /// cannot be tested without one.
    /// </summary>
    public event EventHandler<Release>? InstallRequested;

    [RelayCommand]
    private void Install()
    {
        if (Found is { } release) InstallRequested?.Invoke(this, release);
    }

    /// <summary>Opens the releases page, for somebody who would rather see it themselves.</summary>
    [RelayCommand]
    private static void OpenReleases()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                UpdateService.ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error("güncelleme", e, "Sürümler sayfası açılamadı");
        }
    }
}
