using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.App.Views;

/// <summary>
/// One conversation, opened on its own.
///
/// The code here is only what a view model cannot do: turning a click on a bubble into a command
/// call, because the bubbles live inside an ItemsControl whose items are not the window's data
/// context and binding a command through two relative-source hops for a mouse event is harder to
/// read than four lines.
/// </summary>
/// <summary>Which surface of a conversation a click was about.</summary>
public enum CallTab
{
    /// <summary>The transcript, which is what somebody opening a call usually wants.</summary>
    Conversation,

    /// <summary>
    /// The suggested next moves.
    ///
    /// Clicking a suggestion on the first screen or on the to-do page opened the window on the
    /// transcript, and left the reader to find the tab holding the very thing they clicked.
    /// </summary>
    Actions,
}

public partial class CallWindow
{
    public CallWindow(CallWindowViewModel model)
    {
        InitializeComponent();
        Services.EscapeCloses.Attach(this);

        DataContext = model;

        model.CurrentTurnChanged += (_, turn) => Dispatcher.Invoke(() => FollowPlayhead(turn));

        // Dragging the thumb, clicking the track, clicking an arrow: all of them raise this on
        // the scrollbar inside the scroller's template, and none of them can be confused with a
        // scroll this window performed itself.
        TranscriptScroller.AddHandler(
            System.Windows.Controls.Primitives.ScrollBar.ScrollEvent,
            new System.Windows.Controls.Primitives.ScrollEventHandler(
                (_, _) => _scrolledByHandAt = Environment.TickCount64));
        // How this reader likes to read a conversation, remembered across windows and sessions.
        model.TimelineView = App.Settings.ConversationTimeline;

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(model.TimelineView)) return;
            if (App.Settings.ConversationTimeline == model.TimelineView) return;

            App.Settings = App.Settings with { ConversationTimeline = model.TimelineView };
        };

        model.Playback.PropertyChanged += (_, e) =>
        {
            // Pressing play is the other half of "take me to the audio".
            if (e.PropertyName == nameof(model.Playback.IsPlaying) && model.Playback.IsPlaying)
                Dispatcher.Invoke(ResumeFollowing);
        };

        // The pipeline reports progress and completion for whichever recording it is working on;
        // this window forwards the ones about its own. Both events arrive on worker threads, so
        // they hop to the dispatcher here — and both are let go when the window closes, because a
        // static orchestrator holding handlers into dead windows is a leak that also keeps their
        // view models alive.
        EventHandler<Services.CallProgress>? onProgress = null;
        EventHandler<Services.CallProcessed>? onProcessed = null;
        EventHandler<long>? onTranscript = null;

        if (App.Orchestrator is { } orchestrator)
        {
            onProgress = (_, p) => Dispatcher.InvokeAsync(() => model.OnProgress(p));
            onProcessed = (_, p) => Dispatcher.InvokeAsync(() => model.OnProcessed(p));

            // The new lines land minutes before the summary does, and this window is the one
            // place somebody is watching for them.
            onTranscript = (_, id) => Dispatcher.InvokeAsync(() =>
            {
                if (id == model.CallId) model.Reload();
            });

            orchestrator.ProgressChanged += onProgress;
            orchestrator.CallProcessed += onProcessed;
            orchestrator.TranscriptReplaced += onTranscript;
        }

        // Enter in the tag box must reach our handler even when the suggestion dropdown is
        // open: the ComboBox's own class handler consumes the key while closing the list, and a
        // plain KeyDown binding then never fires — typing a tag and pressing Enter did nothing
        // precisely when the suggestions were showing.
        TagBox.AddHandler(KeyDownEvent, new KeyEventHandler(TagBox_KeyDown), handledEventsToo: true);

        // The player holds a file handle and a wave device. Left alive, a window somebody opened
        // and closed keeps the recording locked, and the next thing that tries to delete or
        // re-process it fails for a reason nobody could guess from the message.
        Closed += (_, _) =>
        {
            if (App.Orchestrator is { } o)
            {
                if (onProgress is not null) o.ProgressChanged -= onProgress;
                if (onProcessed is not null) o.CallProcessed -= onProcessed;
                if (onTranscript is not null) o.TranscriptReplaced -= onTranscript;
            }

            model.Dispose();
        };
    }

    private CallWindowViewModel? ViewModel => DataContext as CallWindowViewModel;

    // ---- following the playhead --------------------------------------------
    //
    // The line being spoken was already marked; nothing moved to it. On a ten-minute call that
    // means the highlight spends almost all of its life below the fold, so the one thing the
    // marking is for — reading along, and seeing which sentence the voice is on — only worked for
    // the first screenful.

    /// <summary>
    /// When the reader last scrolled by hand, or 0 if they have not.
    ///
    /// A timestamp rather than a flag: following yields to the reader and then comes back, instead
    /// of yielding once and staying gone until the player is pressed again. See
    /// <see cref="CallWindowViewModel.ShouldFollow"/> for why.
    /// </summary>
    private long _scrolledByHandAt;

    /// <summary>
    /// Moves the transcript to the line being spoken, unless the reader has taken it over.
    ///
    /// Following has to yield. Somebody scrolling back to check what was said a minute ago is
    /// doing the thing this window exists for, and a view that drags them forward twice a second
    /// makes that impossible. So the wheel, the scrollbar and the navigation keys stop it, and
    /// pressing play or clicking a line — both of which say "take me to the audio" — start it
    /// again.
    ///
    /// <b>Why it is not BringIntoView.</b> It was, and it stopped following after a line or two.
    /// That call scrolls asynchronously, sometimes over two layout passes, so the flag saying
    /// "this move was ours" had to be cleared on a guess about timing — and when the second pass
    /// landed after the guess, the window read its OWN scroll as the reader reaching for the bar
    /// and switched following off for good. Setting the offset directly removes the race: what
    /// the user does is known from what the user does, not inferred from the scrollbar moving.
    /// </summary>
    private void FollowPlayhead(ChatTurn? turn)
    {
        if (turn is null) return;
        if (!CallWindowViewModel.ShouldFollow(Environment.TickCount64, _scrolledByHandAt)) return;
        if (TranscriptTurns.ItemContainerGenerator.ContainerFromItem(turn) is not FrameworkElement bubble) return;
        if (!bubble.IsVisible || bubble.ActualHeight <= 0) return;

        double top;

        try
        {
            // Relative to the panel rather than to the viewport, so it does not depend on where
            // the scroller happens to be right now.
            top = bubble.TransformToAncestor(TranscriptTurns).Transform(default).Y;
        }
        catch (InvalidOperationException)
        {
            // The bubble is not in the tree yet; the next tick is fifty milliseconds away.
            return;
        }

        var target = CallWindowViewModel.FollowOffset(
            top, bubble.ActualHeight,
            TranscriptScroller.VerticalOffset,
            TranscriptScroller.ViewportHeight,
            TranscriptScroller.ExtentHeight);

        if (Math.Abs(target - TranscriptScroller.VerticalOffset) < 1) return;

        TranscriptScroller.ScrollToVerticalOffset(target);
    }

    /// <summary>The reader reaching for the wheel. Unambiguous, unlike the scrollbar moving.</summary>
    private void OnTranscriptWheel(object sender, MouseWheelEventArgs e) =>
        _scrolledByHandAt = Environment.TickCount64;

    private void OnTranscriptKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            _scrolledByHandAt = Environment.TickCount64;
    }

    /// <summary>Anything that means "take me to the audio" puts the transcript back in step now.</summary>
    private void ResumeFollowing() => _scrolledByHandAt = 0;

    /// <summary>
    /// Opens the list of transcripts this call has had.
    ///
    /// Re-reads afterwards only when something was actually put back, so looking is free.
    /// </summary>
    private void Versions_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;

        var window = new TranscriptVersionsWindow(App.Repository, model.CallId) { Owner = this };
        window.ShowDialog();

        if (window.Restored) model.Reload();
    }

    /// <summary>Clicking a line plays from it.</summary>
    private void Turn_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn }) return;

        // Clicking a line is a request to hear it; the transcript should travel with it again.
        ResumeFollowing();

        ViewModel?.PlayTurnCommand.Execute(turn);
    }

    /// <summary>Clicking a quote plays the moment it came from.</summary>
    /// <summary>The windows currently open, by call — so a second click brings one forward.</summary>
    private static readonly Dictionary<long, CallWindow> Open = [];

    /// <summary>
    /// Shows the window for a call, creating it or bringing the existing one forward, and seeks
    /// to a moment when one is given.
    ///
    /// Six places used to build their own window, and every click on a row, a to-do or a
    /// calendar promise stacked another copy of the same conversation. One doorway, one window.
    /// </summary>
    public static CallWindow Show(
        Window? owner, long callId, int? startMs = null, bool isMe = false,
        CallTab tab = CallTab.Conversation)
    {
        if (Open.TryGetValue(callId, out var existing))
        {
            existing.Show();
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();

            existing.SelectTab(tab);

            if (startMs is { } at) existing.ViewModel?.Playback.PlayFrom(at, isMe);
            return existing;
        }

        var model = new CallWindowViewModel(App.Repository, () => App.Settings, App.HttpClient, callId);
        var window = new CallWindow(model) { Owner = owner };

        Open[callId] = window;
        window.Closed += (_, _) => Open.Remove(callId);

        window.Show();
        window.SelectTab(tab);

        if (startMs is { } seek) model.Playback.PlayFrom(seek, isMe);
        return window;
    }

    /// <summary>Brings the named surface forward. The transcript is already the default.</summary>
    private void SelectTab(CallTab tab)
    {
        if (tab == CallTab.Actions) MainTabs.SelectedItem = ActionsTab;
    }

    private void Citation_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Excerpt excerpt }) return;

        ViewModel?.PlayExcerptCommand.Execute(excerpt);
    }

    private void CommitmentQuote_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Commitment c }) return;

        ViewModel?.PlayExcerptCommand.Execute(new Excerpt(0, c.CallId, null, default, c.QuoteStartMs, c.ByMe, c.Quote));
        e.Handled = true;
    }

    private void FlagQuote_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Flag f }) return;

        ViewModel?.PlayExcerptCommand.Execute(new Excerpt(0, f.CallId, null, default, f.QuoteStartMs, IsMe: false, f.Quote));
        e.Handled = true;
    }

    /// <summary>Plays from the line the menu was opened on.</summary>
    private void PlayFromHere_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn }) return;

        ViewModel?.PlayTurnCommand.Execute(turn);
    }

    /// <summary>Copies one line's words.</summary>
    private void CopyTurn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn }) return;

        try
        {
            Clipboard.SetText($"[{turn.Time}] {turn.Speaker}: {turn.Text}");
        }
        catch (Exception)
        {
            // The clipboard is held by another process often enough that failing loudly here
            // would be the more surprising behaviour.
        }
    }

    /// <summary>
    /// Cuts a line and the replies after it out as an audio file.
    ///
    /// A save dialog rather than a folder set once in settings: these files exist to be sent to
    /// somebody, so the user is choosing where to put something they are about to share, and being
    /// asked is what makes that a decision rather than a side effect.
    /// </summary>
    private async void ExportExchange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatTurn turn, Tag: string tag }) return;
        if (ViewModel is not { } model) return;

        var following = int.TryParse(tag, out var parsed) ? parsed : 0;
        var (from, to) = model.ExchangeRange(turn, following);

        var exporter = new Services.ClipExporter(App.Repository);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Ses kesiti nereye kaydedilsin?",
            FileName = exporter.NameFor(model.CallId, from),
            Filter = "Ses dosyası (*.wav)|*.wav",
            DefaultExt = ".wav",
        };

        if (dialog.ShowDialog() != true) return;

        var result = exporter.ExportExchange(
            model.CallId, from, to, dialog.FileName, model.ContactName);

        await Services.Dialogs.InfoAsync(this, "Ses kesiti", result.Message);

        Services.AppLog.Write(
            "kesit", result.Ok ? "görüşmeden kesit yazıldı" : $"kesit alınamadı: {result.Message}");
    }

    /// <summary>
    /// Analyses this conversation, with a model the user picks.
    ///
    /// Offered here because a transcript with no ledger is the ordinary state when no model was
    /// connected at the time, and the tab was otherwise a dead end. It works from the text, so it
    /// costs a minute rather than the hours re-transcribing would.
    /// </summary>
    private void Analyse_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model || App.Orchestrator is null) return;

        // The analyse-only dialog: models, availability and — where published — balance.
        // Its title, list and verb all follow the button that opened it; nothing asks "hangi
        // yarı?" a second time.
        var dialog = new ReprocessWindow(
            App.Repository, App.Settings, model.Title, count: 1, ReprocessKind.Analyse)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true) return;

        var choice = dialog.Choice;

        App.Repository.SetCallState(model.CallId, VoiceTranscript.Core.Domain.ProcessingState.Queued);
        App.Orchestrator.EnqueueWith(model.CallId, choice.AsrModelId, choice.AnalyseOnly, choice.LlmModel,
            choice.LlmRouteKind, choice.LlmRouteUrl);

        // No dialog. The strip at the bottom of the window carries it from here: progress while
        // it runs, and the tabs refill themselves when it finishes. The dialog this replaces told
        // the user to close the window and open it again — which was the application describing
        // its own missing feature.
        model.MarkQueued();
    }

    /// <summary>
    /// Produces this transcript again, with an engine the user picks.
    ///
    /// Offered beside the quality line because that is where somebody decides the text is not good
    /// enough — and on this machine the choice is consequential: a local model managed a fifth of
    /// real time and a hosted one two hundred times it, on the same recordings.
    /// </summary>
    private void Retranscribe_Click(object sender, RoutedEventArgs e) => Reprocess(ReprocessKind.Transcribe);

    private void Reanalyse_Click(object sender, RoutedEventArgs e) => Reprocess(ReprocessKind.Analyse);

    private void Reprocess(ReprocessKind kind)
    {
        if (ViewModel is not { } model || App.Repository.GetCall(model.CallId) is not { } call) return;

        if (Services.CallActions.Reprocess(this, call, model.Title, kind)) model.MarkQueued();
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model || App.Repository.GetCall(model.CallId) is not { } call) return;

        // Moved: this window's title and ledger belong to the old person; it closes and the
        // lists behind it re-read.
        if (Services.CallActions.Move(this, call, model.Title)) Close();
    }

    private async void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model || App.Repository.GetCall(model.CallId) is not { } call) return;

        await Services.CallActions.ShowInFolderAsync(this, call);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model || App.Repository.GetCall(model.CallId) is not { } call) return;

        if (await Services.CallActions.DeleteAsync(this, call, model.Title)) Close();
    }

    private void RemoveTag_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string tag) ViewModel?.RemoveTag(tag);

        e.Handled = true;
    }

    /// <summary>Onto the important pile, from inside the conversation.</summary>
    private void ToBoard_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;

        App.Repository.PutOnBoard(model.CallId, Core.Domain.BoardLane.ToLookAt);
    }

    /// <summary>The attention strip's promise: one click lands on the evidence it counted.</summary>
    private void Attention_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => MainTabs.SelectedItem = ConsistencyTab;

    /// <summary>A reminder in one step: onto the pile if needed, and dated.</summary>
    private void Remind_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;

        RemindWindow.Open(this, App.Repository, model.CallId, model.Title);
    }

    /// <summary>
    /// A suggestion picked from the open dropdown tags the call immediately — the styled pill
    /// in the list is the choice, not a draft of one. Watched on DropDownClosed rather than
    /// SelectionChanged: arrow-keying through the open list changes the selection on every
    /// press, and tagging while browsing would be a menu that fires on hover.
    /// </summary>
    private void TagBox_DropDownClosed(object sender, EventArgs e)
    {
        if (ViewModel is not { } model) return;
        if (TagBox.SelectedItem is not string picked || string.IsNullOrWhiteSpace(picked)) return;

        model.NewTag = picked;
        if (model.AddTagCommand.CanExecute(null)) model.AddTagCommand.Execute(null);

        TagBox.SelectedIndex = -1;
    }

    /// <summary>Opens the tag wardrobe; on save the palette reloads, so pills repaint themselves.</summary>
    private void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TagManagerWindow(App.Repository) { Owner = this };

        if (dialog.ShowDialog() == true) ViewModel?.ReloadTags();
    }

    private void TagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } model) return;

        // The editable box commits its text on Enter via the binding update below.
        if (sender is System.Windows.Controls.ComboBox box) model.NewTag = box.Text;

        model.AddTagCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Enter asks, because a single-line question box that needs the mouse is not used.</summary>
    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ViewModel is not { } model) return;

        if (model.AskCommand.CanExecute(null)) model.AskCommand.Execute(null);

        e.Handled = true;
    }

    /// <summary>A tag pill is a question: "which other conversations did I mark with this?"</summary>
    private void TagPill_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string tag) return;

        MainWindow.SearchTagFromAnywhere(tag);
        e.Handled = true;
    }

    /// <summary>
    /// The other end of a consistency finding. In this conversation it plays; in an earlier
    /// one it opens that conversation at the quoted moment — the archive is the point.
    /// </summary>
    private void ConsistencyCounter_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Excerpt excerpt }) return;
        if (ViewModel is not { } model) return;

        if (excerpt.CallId == model.CallId)
        {
            model.PlayExcerptCommand.Execute(excerpt);
            return;
        }

        var counterpart = new CallWindowViewModel(
            App.Repository, () => App.Settings, App.HttpClient, excerpt.CallId);

        var window = new CallWindow(counterpart) { Owner = this };
        window.Show();

        counterpart.Playback.PlayFrom(excerpt.StartMs, excerpt.IsMe);
    }

    /// <summary>A finding becomes a follow-up: the reminder dialog opens with the reason drafted.</summary>
    private void ConsistencyRemind_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConsistencyRow row) return;
        if (ViewModel is not { } model) return;

        RemindWindow.Open(this, App.Repository, model.CallId, model.Title, row.ReminderDraft);
    }

    // ---- suggested actions: the user routes, the machine never writes -------

    private ActionRow? ActionOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ActionRow;

    /// <summary>Suggestion → reminder: the dialog opens with the action as the drafted reason.</summary>
    private void ActionRemind_Click(object sender, RoutedEventArgs e)
    {
        if (ActionOf(sender) is not { } row || ViewModel is not { } model) return;

        // Only when something was actually scheduled. Routed removes the row from the open list
        // for good, and a suggestion cancelled out of is a suggestion still outstanding.
        if (RemindWindow.Open(this, App.Repository, model.CallId, model.Title, row.Action))
            model.SetActionStatus(row, Core.Domain.ActionStatus.Routed, "hatırlatıcı");
    }

    /// <summary>Suggestion → the important pile. Existing card titles are never overwritten.</summary>
    private void ActionBoard_Click(object sender, RoutedEventArgs e)
    {
        if (ActionOf(sender) is not { } row || ViewModel is not { } model) return;

        var existing = App.Repository.BoardCardOf(model.CallId);
        App.Repository.PutOnBoard(
            model.CallId, Core.Domain.BoardLane.ToLookAt,
            title: existing?.Title is null ? row.Action : null);

        model.SetActionStatus(row, Core.Domain.ActionStatus.Routed, "önemliler");
    }

    private void ActionDone_Click(object sender, RoutedEventArgs e)
    {
        if (ActionOf(sender) is { } row) ViewModel?.SetActionStatus(row, Core.Domain.ActionStatus.Done);
    }

    private void ActionHide_Click(object sender, RoutedEventArgs e)
    {
        if (ActionOf(sender) is { } row) ViewModel?.SetActionStatus(row, Core.Domain.ActionStatus.Hidden);
    }

    /// <summary>A reading line's quote plays when it verified — and only then carries a link.</summary>
    private void ReadingQuote_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Core.Analysis.ReadingLine line) return;
        if (line.StartMs is not { } at || ViewModel is not { } model) return;

        model.PlayExcerptCommand.Execute(
            new Excerpt(0, model.CallId, null, default, at, line.IsMe, line.Quote ?? ""));
    }

    /// <summary>An asserted tactic plays its own evidence — that is what earns it the row.</summary>
    private void DeceptionQuote_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Core.Analysis.DeceptionLine line) return;
        if (ViewModel is not { } model) return;

        model.PlayExcerptCommand.Execute(
            new Excerpt(0, model.CallId, null, default, line.StartMs, line.IsMe, line.Quote));
    }
}
