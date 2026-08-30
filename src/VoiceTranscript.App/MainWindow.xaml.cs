using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.App.Views;

namespace VoiceTranscript.App;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Notices are shown as a snackbar rather than bound to a bar that stays on screen. The
        // view model still owns the text; the window owns how it appears.
        // Player keys, handled at the window so they work wherever the pointer happens to be.
        PreviewKeyDown += OnPreviewKeyDown;

        // The tray tick has to match the stored setting, and it cannot be set in markup because
        // the setting is not loaded until startup has run.
        Loaded += (_, _) => SyncAutoRecordMenu();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ShellViewModel previous)
            {
                previous.PropertyChanged -= OnShellPropertyChanged;
                previous.Overview.ActionRequested -= OnAttentionAction;
            }

            if (e.NewValue is ShellViewModel next)
            {
                next.PropertyChanged += OnShellPropertyChanged;

                // The three most prominent buttons on the first screen used to raise an event
                // nobody listened to. Pressing "İsimlendir", "Tekrar dene" or "Ayarlar" did
                // nothing at all — and there is no more expensive kind of defect in a product
                // than a button that quietly does not work, because it is only pressed once.
                //
                // Subscribed here rather than in the view model because two of these open a
                // window, and a dialog with no owner falls behind the main window.
                next.Overview.ActionRequested += OnAttentionAction;
            }
        };

        // Guarded so that the window can be constructed without the rest of the application
        // graph. That is what lets the markup be smoke-tested — every screen is really built in
        // a test, which is the only way to catch a renamed resource key or a bad icon name
        // before somebody meets it as a crash.
        //
        // The prompt is raised from a background thread, so it has to be marshalled onto the UI.
        if (App.Orchestrator is { } orchestrator)
        {
            orchestrator.CallFinished += (_, finished) =>
                Dispatcher.Invoke(() => PromptForLabel(finished));

            orchestrator.StateChanged += (_, state) =>
                Dispatcher.Invoke(() => ShowRecordingStrip(state));
        }
    }

    /// <summary>The strip that says the microphone is open. Built on first use, then kept.</summary>
    private RecordingOverlay? _strip;

    /// <summary>
    /// Puts the recording strip on screen while a recording is running, and takes it away after.
    ///
    /// Created lazily because most sessions never record anything — this application spends
    /// nearly all its life idle in the tray — and an always-present topmost window is a thing
    /// that can go wrong for no benefit.
    /// </summary>
    private void ShowRecordingStrip(OrchestratorState state)
    {
        if (state != OrchestratorState.Recording)
        {
            _strip?.End();
            return;
        }

        if (!App.Settings.ShowRecordingBar) return;

        if (_strip is null)
        {
            _strip = new RecordingOverlay();

            // Stopping from the strip is the same action as stopping from the window. Somebody
            // deciding mid-call not to keep a conversation should not have to find a window
            // first — by the time they have, the part they wanted kept out is recorded.
            _strip.StopRequested += async (_, _) =>
            {
                if (App.Orchestrator is { } running) await running.StopManualRecordingAsync();
            };
        }

        _strip.Begin(App.Orchestrator?.RecordingStartedAt ?? DateTimeOffset.Now);
    }

    /// <summary>
    /// Turns automatic recording on and off from the tray.
    ///
    /// In the tray rather than only in settings because of when it is used: somebody decides
    /// they do not want the next call recorded seconds before it starts, and a settings window
    /// four clicks deep is not reachable in that moment.
    /// </summary>
    private void ToggleAutoRecord_Click(object sender, RoutedEventArgs e)
    {
        var enabled = !App.Settings.RecordAutomatically;

        App.Settings = App.Settings with { RecordAutomatically = enabled };
        App.Settings.Save(App.Paths.SettingsFile);

        AppLog.Write("kayıt", $"otomatik kayıt {(enabled ? "açıldı" : "kapatıldı")}");
        SyncAutoRecordMenu();

        if (DataContext is ShellViewModel shell)
            shell.Notice = enabled
                ? "Otomatik kayıt açık — aramalar kendiliğinden kaydedilecek."
                : "Otomatik kayıt kapalı. \"Kaydı başlat\" ile elle kaydedebilirsin.";
    }

    private void SyncAutoRecordMenu()
    {
        if (AutoRecordItem is not null) AutoRecordItem.IsChecked = App.Settings.RecordAutomatically;
    }

    /// <summary>
    /// Asks who the call was with, once it is over.
    ///
    /// Only when the contact is not already known: Telegram supplies the name in its window
    /// title, and a WhatsApp title that has been labelled once is remembered thereafter, so in
    /// practice this appears for genuinely new contacts and nobody else.
    /// </summary>
    private void PromptForLabel(CallFinished finished)
    {
        if (!finished.NeedsLabel) return;

        var dialog = new LabelCallWindow(
            App.Repository,
            finished.CallId,
            finished.Duration,
            finished.ObservedTitle,
            finished.App,
            finished.AudioSummary,
            finished.HasSilentStream)
        {
            Owner = IsVisible ? this : null,
        };

        dialog.ShowDialog();

        if (DataContext is ShellViewModel viewModel) viewModel.RefreshAll();
    }

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        App.ShowSetup(this);
        if (DataContext is ShellViewModel viewModel) viewModel.RefreshAll();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var viewModel = new SettingsViewModel(App.Settings, App.Paths, App.HttpClient);
        var dialog = new SettingsWindow(viewModel) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        // Carried across explicitly. ToSettings builds a fresh record from the fields the window
        // shows, so anything it does not show would be silently reset — and resetting the
        // first-run stamp would bring the setup wizard back every time somebody saved settings.
        App.Settings = viewModel.ToSettings() with
        {
            SetupCompletedAt = App.Settings.SetupCompletedAt,
            TranscribeGroupCalls = App.Settings.TranscribeGroupCalls,
            Language = App.Settings.Language,
        };

        App.Settings.Save(App.Paths.SettingsFile);

        // The tray tick and the settings page are two views of one switch and have to agree.
        SyncAutoRecordMenu();
    }

    /// <summary>
    /// Carries out whatever the first screen offered to do.
    /// </summary>
    private void OnAttentionAction(object? sender, AttentionAction action)
    {
        if (DataContext is not ShellViewModel shell) return;

        switch (action)
        {
            case AttentionAction.ShowUnlabelled:
                LabelPending(shell);
                break;

            case AttentionAction.RetryFailed:
            {
                var requeued = shell.Overview.RequeueFailed();

                // The count is in the message on purpose: it tells the user something, and it
                // makes the text differ between presses so a second click is visibly acted on.
                shell.Notice = requeued == 0
                    ? "Tekrar denenecek bir kayıt yok."
                    : $"{requeued} kayıt yeniden kuyruğa alındı.";

                _ = App.Orchestrator?.ProcessBacklogAsync();
                break;
            }

            case AttentionAction.OpenSettings:
                OpenSettings();
                break;
        }
    }

    /// <summary>
    /// Walks the recordings nobody has named yet.
    ///
    /// The loop stops the moment somebody postpones one. Twelve unnamed recordings would
    /// otherwise be twelve modal dialogs in a row with no way out, which is a worse experience
    /// than never having offered.
    /// </summary>
    private void LabelPending(ShellViewModel shell)
    {
        foreach (var call in shell.Overview.Unlabelled())
        {
            var dialog = new LabelCallWindow(
                App.Repository,
                call.Id,
                call.Duration,
                call.ObservedTitle,
                call.App,
                audioSummary: "",
                hasSilentStream: false)
            {
                Owner = this,
            };

            dialog.ShowDialog();

            if (dialog.Outcome == LabelOutcome.Postponed) break;
        }

        shell.RefreshAll();
    }

    /// <summary>
    /// Closing the window hides it rather than quitting.
    ///
    /// The application has to keep watching for calls, and a recorder that stops the first time
    /// somebody clicks the X would miss exactly the conversations it exists to catch. Quitting
    /// is available from the tray menu, deliberately and explicitly.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Tray_Click(object sender, RoutedEventArgs e) => ShowWindow_Click(sender, e);

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as ShellViewModel)?.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Shows a notice, then lets it go.
    ///
    /// Five seconds for ordinary news and ten for a warning: long enough to read a sentence
    /// twice, short enough that somebody working through a list of calls is not interrupted by a
    /// stack of messages they have already understood.
    /// </summary>
    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.Notice)) return;
        if (DataContext is not ShellViewModel model || model.Notice is not { } message) return;

        var isProblem = model.HasProblem
                        || message.Contains("başarısız")
                        || message.Contains("gelmedi")
                        || message.Contains("edilemedi");

        var snackbar = new Wpf.Ui.Controls.Snackbar(Notices)
        {
            Title = isProblem ? "Dikkat" : "Bilgi",
            Content = message,
            Appearance = isProblem
                ? Wpf.Ui.Controls.ControlAppearance.Caution
                : Wpf.Ui.Controls.ControlAppearance.Secondary,
            Icon = new Wpf.Ui.Controls.SymbolIcon(
                isProblem ? Wpf.Ui.Controls.SymbolRegular.Warning24 : Wpf.Ui.Controls.SymbolRegular.Info24),
            Timeout = TimeSpan.FromSeconds(isProblem ? 10 : 5),
        };

        snackbar.Show();

        // Cleared so that the same message twice in a row still shows twice.
        model.Notice = null;
    }

    /// <summary>
    /// Transport keys for the player.
    ///
    /// Space to play or pause, arrows to move ten seconds. Checking a quote means listening to
    /// the same few seconds several times, and reaching for the mouse each time is exactly the
    /// friction that stops people verifying anything.
    ///
    /// Ignored while a text box has focus, because a space in the middle of a search term must
    /// stay a space.
    /// </summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox)
        {
            return;
        }

        if (DataContext is not ShellViewModel { Page: ShellPage.Contacts } shell) return;

        var playback = shell.Contacts.Playback;
        if (!playback.IsLoaded) return;

        switch (e.Key)
        {
            case Key.Space:
                playback.TogglePlayCommand.Execute(null);
                break;

            case Key.Left:
                playback.SkipBackCommand.Execute(null);
                break;

            case Key.Right:
                playback.SkipForwardCommand.Execute(null);
                break;

            default:
                return;
        }

        e.Handled = true;
    }
}
