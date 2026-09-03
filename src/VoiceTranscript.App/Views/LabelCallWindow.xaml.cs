using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Views;

public enum LabelOutcome
{
    Saved,
    Postponed,
    Discarded,
}

/// <summary>
/// Asks who a just-finished call was with.
///
/// Telegram puts the counterpart's name in its call window title, so that case answers itself and
/// this window never appears. WhatsApp titles its window "WhatsApp" and nothing else, so the
/// first call with someone has to be labelled by hand — after which the title is remembered and
/// the question is not asked again. Accuracy matters more than automation here: the whole ledger
/// is organised per contact, and one call filed under the wrong person corrupts two histories.
/// </summary>
public partial class LabelCallWindow
{
    private readonly Repository _repository;
    private readonly long _callId;
    private readonly string? _observedTitle;

    /// <summary>Who the call is probably about, from a recognised title or the voice.</summary>
    private readonly long? _suggestedContactId;
    private readonly CallApp _app;

    /// <summary>Set while a suggestion is being applied, so the edit does not reopen the list.</summary>
    private bool _choosing;

    public LabelCallWindow(
        Repository repository,
        long callId,
        TimeSpan duration,
        string? observedTitle,
        CallApp app,
        string audioSummary,
        bool hasSilentStream,
        long? suggestedContactId = null)
    {
        InitializeComponent();
        Services.EscapeCloses.Attach(this);

        _repository = repository;
        _callId = callId;
        _observedTitle = observedTitle;
        _suggestedContactId = suggestedContactId;
        _app = app;

        HeadlineText.Text = $"{duration:mm\\:ss} uzunluğunda bir {app} görüşmesi kaydedildi.";

        AudioText.Text = audioSummary;
        AudioText.Foreground = (System.Windows.Media.Brush)FindResource(
            hasSilentStream ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");

        RecentList.ItemsSource = repository.RecentContacts();

        // Telegram gives the name for free; pre-filling it turns this into a confirmation.
        // The prefilled answer: a contact recognised from the title, else the title itself when
        // it looks like a name. "Voice call" is offered as neither — typing over a wrong guess is
        // work, and accepting one by reflex is how two people's histories merge.
        var suggested = suggestedContactId is { } id ? repository.GetContact(id) : null;

        if (suggested is not null)
            NameBox.Text = suggested.Name;
        else if (!string.IsNullOrWhiteSpace(observedTitle) && !Core.Detection.GenericTitles.IsGeneric(observedTitle))
            NameBox.Text = observedTitle;

        NameBox.Focus();
        NameBox.SelectAll();

        ShowWhetherTheTitleCanBeRemembered();
    }

    /// <summary>
    /// Settles the "remember this title" offer before the user answers, rather than after.
    ///
    /// This used to be a message box shown once the call had already been filed: the user picked
    /// a name, pressed save, and was told the title had not been remembered after all. Two
    /// complaints about it, both fair. It reported a decision instead of asking for one, it
    /// arrived when the window they wanted gone was gone, and — the part that made it feel like a
    /// fault — it appeared under a heading that read like an error for something that had worked.
    ///
    /// Everything it said is known before the question is asked, so it is said there: the offer
    /// is withdrawn, greyed, and labelled with the reason. Nothing to acknowledge, nothing to
    /// dismiss, and the promise is never made in the first place.
    /// </summary>
    private void ShowWhetherTheTitleCanBeRemembered()
    {
        if (string.IsNullOrWhiteSpace(_observedTitle))
        {
            // No title was seen at all — there is nothing to offer, so the offer is not made.
            RememberBox.Visibility = Visibility.Collapsed;
            return;
        }

        if (Core.Detection.GenericTitles.IsGeneric(_observedTitle))
        {
            Withdraw($"“{_observedTitle}” kimi aradığını söylemiyor, hatırlanamaz");
            return;
        }

        // Asked on behalf of whoever this call is most likely about, which is the prefilled
        // answer when there is one. Asking on behalf of nobody reported a title already bound to
        // THIS person as belonging to somebody else — true of the question asked, wrong about the
        // situation, and the sentence a user would read as the application having lost track.
        if (!_repository.CanRememberTitle(_observedTitle, _app, forContactId: _suggestedContactId ?? 0))
            Withdraw($"“{_observedTitle}” başka bir kişiye bağlı, hatırlanamaz");

        void Withdraw(string reason)
        {
            RememberBox.IsChecked = false;
            RememberBox.IsEnabled = false;
            RememberBox.ToolTip = reason;
            RememberNote.Text = reason;
            RememberNote.Visibility = Visibility.Visible;
        }
    }

    public LabelOutcome Outcome { get; private set; } = LabelOutcome.Postponed;

    private void PickRecent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is Contact contact) Choose(contact);
    }

    /// <summary>
    /// Offers the contacts already in the archive that match what is being typed.
    ///
    /// The point is not convenience, it is correctness. Every name typed by hand is a chance to
    /// spell one differently from last time — "Ahmet Bey" against "ahmet bey", or a name with an
    /// ı where an i was used before — and each of those creates a second contact holding half of
    /// one person's history. Splitting a history is invisible: both halves look complete, the
    /// ledger simply stops noticing that a price changed between them.
    /// </summary>
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_choosing) return;

        var typed = NameBox.Text?.Trim() ?? "";
        var matches = _repository.SearchContacts(typed);

        // An exact match is not worth offering: it is already what the box says.
        var offers = matches
            .Where(c => !string.Equals(c.Name, typed, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        MatchList.ItemsSource = offers;
        MatchList.Visibility = offers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Down arrow moves into the suggestions; Escape dismisses them.
    ///
    /// Without a keyboard route the list is decoration for anybody who is typing, which is
    /// everybody it exists for.
    /// </summary>
    private void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MatchList.Visibility != Visibility.Visible) return;

        switch (e.Key)
        {
            case Key.Down:
                MatchList.SelectedIndex = 0;
                (MatchList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;

            case Key.Escape:
                MatchList.Visibility = Visibility.Collapsed;
                e.Handled = true;
                break;
        }
    }

    private void MatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchList.SelectedItem is Contact contact) Choose(contact);
    }

    /// <summary>Puts a chosen name in the box without the change re-opening the suggestions.</summary>
    private void Choose(Contact contact)
    {
        _choosing = true;

        try
        {
            NameBox.Text = contact.Name;
            NameBox.CaretIndex = NameBox.Text.Length;

            MatchList.Visibility = Visibility.Collapsed;
            MatchList.SelectedItem = null;

            NameBox.Focus();
        }
        finally
        {
            _choosing = false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.Focus();
            return;
        }

        var contactId = _repository.UpsertContact(name, _app);
        _repository.AssignContact(_callId, contactId);

        // Remembering the title is what makes the next call with this person automatic — when the
        // title identifies a person at all.
        //
        // It frequently does not. Telegram names the counterpart in its call window; WhatsApp
        // shows whatever chat was open, or a WebView2 page title, which is the same string for
        // everybody. Bound to the first person labelled, such a title then swallowed every call
        // that followed — "her konuşmayı Uliana zannediyor" — silently, because the binding was
        // consulted before anybody was asked.
        //
        // The box is only reachable when the title is free, so a refusal here is a race — the
        // same title claimed by another window in between — and not something to interrupt
        // somebody over. See ShowWhetherTheTitleCanBeRemembered.
        if (RememberBox.IsChecked == true && !string.IsNullOrWhiteSpace(_observedTitle))
            _repository.RememberTitle(_observedTitle, contactId, _app);

        // Now that this call belongs to somebody, it is also an example of what they sound like.
        //
        // Fire and forget, and deliberately not awaited: the user has answered the question and
        // wants the window gone, and learning a voice means expanding an Opus archive and running
        // a model over it. It also refuses to learn from anything the voice recogniser filed
        // itself — only the answers a person gave, which is what has just happened here.
        if (App.Settings?.IdentifySpeakers == true && App.Paths is { } paths)
        {
            _ = Task.Run(() => new Services.VoiceEnrolment(
                    _repository, () => App.Worker, paths.Models,
                    line => Services.AppLog.Write("ses", line))
                .LearnAsync(contactId));
        }

        Outcome = LabelOutcome.Saved;
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        // The recording is kept and stays unlabelled; it can be assigned from the main window.
        Outcome = LabelOutcome.Postponed;
        DialogResult = true;
        Close();
    }

    /// <summary>The same delete as every list, with the same words and the same warning.</summary>
    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_repository.GetCall(_callId) is not { } call) return;

        if (!await Services.CallActions.DeleteAsync(this, call, "İsimsiz görüşme")) return;

        Outcome = LabelOutcome.Discarded;
        DialogResult = true;
        Close();
    }
}
