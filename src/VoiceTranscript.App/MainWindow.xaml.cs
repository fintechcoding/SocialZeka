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
                previous.AiStatus.SettingsRequested -= OnSettingsRequested;
                previous.PaletteRequested -= OnPaletteRequested;
                previous.ShortcutsRequested -= OnShortcutsRequested;
            }

            if (e.NewValue is ShellViewModel next)
            {
                next.PropertyChanged += OnShellPropertyChanged;
                next.PaletteRequested += OnPaletteRequested;
                next.ShortcutsRequested += OnShortcutsRequested;

                // The three most prominent buttons on the first screen used to raise an event
                // nobody listened to. Pressing "İsimlendir", "Tekrar dene" or "Ayarlar" did
                // nothing at all — and there is no more expensive kind of defect in a product
                // than a button that quietly does not work, because it is only pressed once.
                //
                // Subscribed here rather than in the view model because two of these open a
                // window, and a dialog with no owner falls behind the main window.
                next.Overview.ActionRequested += OnAttentionAction;

                // The status screen regularly answers "this is not working". Making somebody go
                // and find Settings at that moment is telling them there is a problem and then
                // asking them to look for the door.
                next.AiStatus.SettingsRequested += OnSettingsRequested;
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
            // InvokeAsync, not Invoke.
            //
            // The blocking form made the caller wait for the delegate, and the delegate opens a
            // modal dialog — so the orchestrator's thread sat inside this line for as long as the
            // window was on screen. That thread was the detection loop, which meant no call was
            // detected while the dialog was open, and a conversation started in that window was
            // never recorded at all. The loop no longer does this work, but the blocking call
            // would still be wrong: nothing on a background thread should wait on the interface.
            orchestrator.CallFinished += (_, finished) =>
                Dispatcher.InvokeAsync(() => PromptForLabel(finished));

            orchestrator.StateChanged += (_, state) =>
                Dispatcher.InvokeAsync(() => ShowRecordingStrip(state));

            // Raised from a worker thread, once per call, when the far end has been recognised.
            orchestrator.SpeakerIdentified += (_, who) =>
                Dispatcher.InvokeAsync(() => ShowCaller(who));

            orchestrator.CallProcessed += (_, processed) =>
                Dispatcher.InvokeAsync(() => AnnounceProcessed(processed));
        }
    }

    /// <summary>The strip that says the microphone is open. Built on first use, then kept.</summary>
    private RecordingOverlay? _strip;

    /// <summary>The panel that says who is on the other end. Built the first time one is recognised.</summary>
    private CallerOverlay? _caller;

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
            _caller?.End();
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
    /// Shows who the far end is, once the voice has been recognised.
    ///
    /// Late on purpose. Recognising somebody needs thirty seconds of them actually speaking, which
    /// in a real call is usually a minute in — below that the measurement says the answer is
    /// noise, and a name that might be wrong is worse than no name at all on a panel somebody
    /// glances at mid-sentence.
    ///
    /// A suggestion is shown as readily as a confident match: the panel is for reading, not for
    /// filing, and the score is on it so the reader can weigh it themselves. Filing is a separate
    /// decision made when the call ends, under a stricter rule.
    /// </summary>
    private void ShowCaller(SpeakerHypothesis hypothesis)
    {
        if (!App.Settings.IdentifySpeakers || hypothesis.Contact is not { } contact) return;
        if (App.Repository is not { } repository) return;

        _caller ??= new CallerOverlay();

        // Commitments are read here rather than in the identifier so the panel shows what the
        // ledger holds at this moment, and so nothing about contacts has to travel through the
        // audio path.
        var commitments = repository.GetOpenCommitments(contact.Id)
            .Take(3)
            .Select(CallerOverlay.Line)
            .ToList();

        _caller.Begin(
            contact.Name,
            hypothesis.Match.Score,
            contact.LastCallAt,
            contact.Notes,
            commitments);
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
            shell.Post(enabled
                    ? "Otomatik kayıt açık — aramalar kendiliğinden kaydedilecek."
                    : "Otomatik kayıt kapalı. \"Kaydı başlat\" ile elle kaydedebilirsin.",
                Services.NoticeSeverity.Info);
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
    /// <summary>
    /// Says what became of a recording, once it has been transcribed and analysed.
    ///
    /// This is the answer to the question people actually ask after a call — who that was and what
    /// it was about. Everything needed to answer it was already being produced and written to the
    /// database; what was missing was anybody saying so. The summary is shown on the contact's
    /// page, and the only way to learn it was there was to go looking.
    ///
    /// Kept to one line on purpose. It arrives minutes after the conversation, unprompted, while
    /// the user is doing something else; a card that has to be dismissed would be an interruption,
    /// and the full text is a click away in the archive.
    /// </summary>
    private void AnnounceProcessed(CallProcessed processed)
    {
        if (DataContext is not ShellViewModel shell) return;

        // Only the refresh. The announcement itself is made by ShellViewModel, which subscribes
        // to the same event and posts it with the right severity — this method used to post a
        // second copy, so every processed call produced two snackbars, and this one carried
        // whatever colour the previous message had left behind.
        shell.RefreshAll();
    }


    /// <summary>True while a labelling dialog is on screen, so a second one cannot stack on it.</summary>
    private bool _labelling;

    private void PromptForLabel(CallFinished finished)
    {
        if (!finished.NeedsLabel) return;

        // One dialog at a time.
        //
        // Two calls finishing close together used to open two modal windows on top of each other,
        // and since the dialog does not appear in the taskbar the one underneath was unreachable
        // until the top one was dealt with. The second call is not lost by skipping it here: it
        // stays unlabelled and appears on the first screen under "isimlendirilmemiş", which is a
        // list the user can work through when they choose to.
        if (_labelling) return;

        _labelling = true;

        try
        {
            var dialog = new LabelCallWindow(
                App.Repository,
                finished.CallId,
                finished.Duration,
                finished.ObservedTitle,
                finished.App,
                finished.AudioSummary,
                finished.HasSilentStream,
                finished.SuggestedContactId)
            {
                Owner = IsVisible ? this : null,
            };

            dialog.ShowDialog();
        }
        catch (Exception e)
        {
            // A dialog that cannot be built must not take anything else down with it. Without
            // this, a missing resource key or a bad binding would surface as an unhandled
            // exception the crash handler swallows, and the call would simply never be labelled
            // with no explanation anywhere.
            Services.AppLog.Error("arayüz", e, $"görüşme #{finished.CallId} isimlendirme penceresi açılamadı");
        }
        finally
        {
            _labelling = false;
        }

        if (DataContext is ShellViewModel viewModel) viewModel.RefreshAll();
    }

    private void OnPaletteRequested(object? sender, EventArgs e)
    {
        if (DataContext is ShellViewModel shell) Views.PaletteWindow.Open(this, shell);
    }

    private async void OnShortcutsRequested(object? sender, EventArgs e)
        => await Services.Dialogs.InfoAsync(this, "Klavye kısayolları",
            "Ctrl+K — komut paleti (komut ya da kişi ara)\n" +
            "Ctrl+1…8 — sayfalar (Genel bakış, Defter, Kişiler, Arama, Sor, Durum, Takvim, Yapılacaklar)\n" +
            "Ctrl+F — arama\n" +
            "F5 — yenile\n" +
            "F2 — kişiyi yeniden adlandır (Kişiler)\n" +
            "Esc — pencereyi kapat\n" +
            "Ctrl+? — bu liste");

    /// <summary>Opens the notice history; whatever it holds counts as seen from here on.</summary>
    private void Bell_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as ShellViewModel)?.MarkNoticesSeen();

        BellMenu.PlacementTarget = BellButton;
        BellMenu.DataContext = DataContext;
        BellMenu.IsOpen = true;
    }

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        App.ShowSetup(this);
        if (DataContext is ShellViewModel viewModel) viewModel.RefreshAll();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnSettingsRequested(object? sender, string? section) => OpenSettings(section);

    /// <summary>
    /// Opens the settings, optionally straight at one section.
    ///
    /// Public and sectioned because "the analysis service is not answering" is discovered on
    /// other screens, and a message that names the fix must be able to take you to it: a status
    /// row or a failure note passes "Analysis" here instead of dropping the user at "Kayıt" to
    /// go find the right page themselves.
    /// </summary>
    /// <summary>
    /// Jumps to the search page with one tag as the whole query.
    ///
    /// This is what clicking a tag pill anywhere in the application does: the pill is a word
    /// the user attached to conversations, so clicking it asks the obvious question — "which
    /// other conversations did I mark with this?" — and the answer lives on the search screen.
    /// </summary>
    public void OpenSearchForTag(string tag)
    {
        if (DataContext is not ShellViewModel shell) return;

        shell.NavigateCommand.Execute("Search");

        shell.Search.LoadContacts();
        shell.Search.Query = "";
        shell.Search.TagChoice = tag;

        // The pill can be clicked from a call window while this window sits hidden in the tray —
        // Activate() alone on a hidden window returns false and the navigation lands nowhere
        // the user can see.
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>The pill click, wherever the pill lives — windows route here too.</summary>
    public static void SearchTagFromAnywhere(string tag)
        => (System.Windows.Application.Current.MainWindow as MainWindow)?.OpenSearchForTag(tag);

    public void OpenSettings(string? section = null)
    {
        var viewModel = new SettingsViewModel(App.Settings, App.Paths, App.HttpClient);

        // Owner only when this window has actually been on screen — WPF refuses a never-shown
        // owner, and settings must stay reachable if a tray entry for it ever appears.
        var dialog = new SettingsWindow(viewModel) { Owner = IsVisible ? this : null };

        if (section is not null) dialog.ShowSection(section);

        if (dialog.ShowDialog() != true) return;

        // No list of settings to carry across by hand any more.
        //
        // ToSettings now amends the record the window opened on instead of building a fresh one,
        // so anything this screen does not display — the first-run stamp, the transcription
        // language, the retention periods, the data directory — survives on its own. The list
        // that used to be here had to be extended every time a setting was added, and the two
        // that were missed were quietly wiped on every save.
        App.Settings = viewModel.ToSettings();

        // Applied before anything else reads it, so a level changed in the window takes effect on
        // the very next line rather than after a restart — which is when somebody who has just
        // turned Debug on is about to reproduce the thing they turned it on for.
        Services.AppLog.Level = App.Settings.LogDetail;

        App.Settings.Save(App.Paths.SettingsFile);

        // Applied here rather than only at the next start, because a switch that does nothing
        // until you reboot is one nobody can tell they have actually thrown.
        Services.AutoStart.Apply(App.Settings.StartWithWindows);

        // The theme too — and through the same helper the start-up path uses, so switching back
        // to "sistemi izle" re-attaches the watcher instead of leaving a pinned palette behind.
        App.ApplyTheme(App.Settings.ThemeChoice, this);

        // The tray tick and the settings page are two views of one switch and have to agree.
        SyncAutoRecordMenu();

        // Every page that shows configuration re-reads it. Without this the Yapay zekâ screen
        // kept describing the provider that was just replaced — "bağladım ama ekran hâlâ eskiyi
        // söylüyor" is the moment somebody stops believing the status screen entirely.
        (DataContext as ShellViewModel)?.RefreshAll();
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
                shell.Post(requeued == 0
                        ? "Tekrar denenecek bir kayıt yok."
                        : $"{requeued} kayıt yeniden kuyruğa alındı.",
                    Services.NoticeSeverity.Info);

                _ = App.Orchestrator?.ProcessBacklogAsync();
                break;
            }

            case AttentionAction.OpenSettings:
                OpenSettings();
                break;

            case AttentionAction.ShowProcessing:
                // The list, filtered to what has not finished: every failure with its own reason
                // and its own retry, instead of one blind "try them all again".
                shell.Processing.TranscriptFilter = TranscriptFilter.Unfinished;
                shell.NavigateCommand.Execute("Processing");
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

    private void ShowWindow_Click(object sender, RoutedEventArgs e) => BringToFront();

    /// <summary>
    /// Shows the window and puts it in front, whether it was hidden in the tray, minimised, or
    /// buried under other windows. Windows refuses to hand focus to a process that was not the
    /// last one the user interacted with; toggling Topmost is the accepted way around it.
    /// </summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
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

        // Severity arrives with the message now — the substring guess this replaced styled a
        // failure as information the first time one was phrased politely.
        var severity = model.NoticeSeverity;

        var (title, appearance, icon) = severity switch
        {
            Services.NoticeSeverity.Success => ("Tamamlandı",
                Wpf.Ui.Controls.ControlAppearance.Success, Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24),
            Services.NoticeSeverity.Warning => ("Dikkat",
                Wpf.Ui.Controls.ControlAppearance.Caution, Wpf.Ui.Controls.SymbolRegular.Warning24),
            Services.NoticeSeverity.Error => ("Hata",
                Wpf.Ui.Controls.ControlAppearance.Danger, Wpf.Ui.Controls.SymbolRegular.DismissCircle24),
            _ => ("Bilgi",
                Wpf.Ui.Controls.ControlAppearance.Secondary, Wpf.Ui.Controls.SymbolRegular.Info24),
        };

        var snackbar = new Wpf.Ui.Controls.Snackbar(Notices)
        {
            Title = title,
            Content = message,
            Appearance = appearance,
            Icon = new Wpf.Ui.Controls.SymbolIcon(icon),
            Timeout = TimeSpan.FromSeconds(
                severity is Services.NoticeSeverity.Warning or Services.NoticeSeverity.Error ? 10 : 5),
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
