using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>Which of the three sources the list is showing.</summary>
public enum TodoSource
{
    /// <summary>Everything, which is the point of the page.</summary>
    All,

    /// <summary>Only what the analysis proposed.</summary>
    Suggestions,

    /// <summary>Only what the user wrote down or set themselves.</summary>
    Mine,
}

public enum TodoEntryKind
{
    /// <summary>Typed by the user on this page.</summary>
    Manual,

    /// <summary>A step the analysis suggested after a call, still open.</summary>
    Action,

    /// <summary>A reminder the user set on a call.</summary>
    Reminder,
}

/// <summary>
/// One line on the to-do page, whatever it came from.
///
/// Three sources, one list: the things the user wrote down, the steps the analysis suggested
/// and they have not dismissed, and the reminders they set on calls. Each keeps its own identity
/// so completing it goes back to the right table — a suggestion is marked done, a reminder is
/// cleared, a note is ticked — and each keeps its call, so the row is a way into the conversation
/// it came from.
/// </summary>
public sealed class TodoEntry(
    TodoEntryKind kind, long id, string text, DateOnly? due, string? contactName, long? callId, bool done,
    string? quote = null, int? quoteStartMs = null, bool quoteIsMe = false)
{
    public TodoEntryKind Kind { get; } = kind;

    /// <summary>
    /// The row's number IN ITS OWN SOURCE. Never read this without naming the source first.
    ///
    /// The three sources do not share a number space and cannot be made to: a note is a row of
    /// "todo", a suggestion a row of "action_item", and a reminder is not a row at all — it is
    /// the "remind_on" column of "board_card", whose key is the CALL. So a 7 here means three
    /// different things, and a note, a suggestion and a reminder all numbered 7 can be on the
    /// screen together. <see cref="TodoId"/>, <see cref="ActionId"/> and
    /// <see cref="ReminderCallId"/> exist so that reading the number states which space it
    /// belongs to and fails loudly when that is the wrong one.
    /// </summary>
    public long Id { get; } = id;

    public string Text { get; } = text;
    public DateOnly? Due { get; } = due;
    public string? ContactName { get; } = contactName;
    public long? CallId { get; } = callId;
    public bool IsDone { get; } = done;

    /// <summary>
    /// The verbatim sentence a suggestion rests on, and the millisecond it can be played from.
    ///
    /// Null on everything the user wrote themselves. That is not a gap to be filled with a
    /// plausible line: a note typed on this page has no moment in any conversation, and a row
    /// that borrowed one would be claiming evidence it does not have (PLAN-SOSYALZEKA §3.1).
    /// </summary>
    public string? Quote { get; } = string.IsNullOrWhiteSpace(quote) ? null : quote.Trim();

    public int? QuoteStartMs { get; } = quoteStartMs;

    /// <summary>Which side said it, so ▸ plays the right stream.</summary>
    public bool QuoteIsMe { get; } = quoteIsMe;

    public bool HasQuote => Quote is not null && QuoteStartMs is not null && CallId is not null;

    /// <summary>
    /// The moment, as a clock. Borrowed from <see cref="PromiseCard.Clock"/> on purpose: the
    /// product has one way of writing the instant a quote was said, and an eighth hand-rolled
    /// "ms / 60000" here would be one more line for the single-clock rule to hunt down.
    /// </summary>
    public string QuoteTimestamp => QuoteStartMs is { } ms ? PromiseCard.Clock(ms) : "";

    public bool HasCall => CallId is not null;
    public bool CanDelete => Kind == TodoEntryKind.Manual;

    /// <summary>The "todo" table's id. Reading it on any other kind is the mixed-up number bug.</summary>
    public long TodoId => Number(TodoEntryKind.Manual);

    /// <summary>The "action_item" table's id.</summary>
    public long ActionId => Number(TodoEntryKind.Action);

    /// <summary>The CALL the reminder hangs on — "board_card" is keyed by the call, not by a card id.</summary>
    public long ReminderCallId => Number(TodoEntryKind.Reminder);

    private long Number(TodoEntryKind expected) => Kind == expected
        ? Id
        : throw new InvalidOperationException(
            $"{Kind} satırının numarası {expected} numarası değildir; üç kaynağın sayıları ayrı uzaylarda.");

    /// <summary>
    /// A suggestion can be turned down. A note the user wrote is deleted, not refused.
    ///
    /// Two different verbs for two different things: refusing is a judgement about something the
    /// machine proposed, and the machine is told — a refused suggestion is not offered again.
    /// </summary>
    public bool CanDismiss => Kind == TodoEntryKind.Action && !IsDone;

    public string DueText => Due is { } d
        ? d == DateOnly.FromDateTime(DateTime.Today) ? Localisation.T("todopage.bugun")
        : d == DateOnly.FromDateTime(DateTime.Today).AddDays(1) ? Localisation.T("todopage.yarin")
        : d.ToString("d MMM")
        : "";

    public bool IsOverdue => !IsDone && Due is { } d && d < DateOnly.FromDateTime(DateTime.Today);

    public string SourceText => Kind switch
    {
        TodoEntryKind.Action => Localisation.T("todopage.oneri"),
        TodoEntryKind.Reminder => Localisation.T("todopage.hatirlatma"),
        _ => "",
    };

    public string Glyph => Kind switch
    {
        TodoEntryKind.Action => "💡",
        TodoEntryKind.Reminder => "⏰",
        _ => "☐",
    };
}

/// <summary>
/// The to-do page: everything the user has to do, from every source, in one list.
///
/// Asked for as "Todoist gibi": a place to write things down, see them by day, tick them off.
/// The point of having it inside this application rather than beside it is that most of what
/// somebody has to do after a call is already known here — the promise deadlines, the suggested
/// steps, the reminders — and a list that only held the typed items would be the smaller half.
/// </summary>
public sealed partial class TodoViewModel(Repository repository, bool showDone = false) : ObservableObject
{
    public ObservableCollection<TodoEntry> Overdue { get; } = [];
    public ObservableCollection<TodoEntry> Today { get; } = [];
    public ObservableCollection<TodoEntry> Upcoming { get; } = [];
    public ObservableCollection<TodoEntry> Undated { get; } = [];
    public ObservableCollection<TodoEntry> Done { get; } = [];

    public bool HasOverdue => Overdue.Count > 0;
    public bool HasToday => Today.Count > 0;
    public bool HasUpcoming => Upcoming.Count > 0;
    public bool HasUndated => Undated.Count > 0;
    public bool HasDone => Done.Count > 0;
    public bool IsEmpty => !HasOverdue && !HasToday && !HasUpcoming && !HasUndated;

    public int OpenCount => Overdue.Count + Today.Count + Upcoming.Count + Undated.Count;

    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private DateTime? _newDue;

    /// <summary>
    /// Who a new note is about. "Kişi yok" is the first entry and stays the default.
    ///
    /// The column has always existed and the list has always shown the name it joins; nothing in
    /// the interface could put a value in it, so the name never appeared on anything the user
    /// typed. Optional on purpose: a note to self is a whole to-do, and forcing a person onto it
    /// would be asking for a fact to satisfy a schema.
    /// </summary>
    public ObservableCollection<ContactChoice> ContactChoices { get; } = [];

    [ObservableProperty] private ContactChoice? _newContact;

    /// <summary>
    /// Whether the finished section is open. Starts from the saved setting: it is a way of
    /// reading the list, not a per-visit decision, so it is remembered like the timeline view.
    /// </summary>
    [ObservableProperty] private bool _showDone = showDone;

    /// <summary>
    /// How many things are finished, whether or not the section is open.
    ///
    /// The checkbox that opens the section says the number, so "did Yaptım leave a trace" is
    /// answered without opening it. That means reading the done rows on every refresh; they are
    /// few, and a box reading "Bitenler" beside twelve finished things it did not count is the
    /// fault this replaces.
    /// </summary>
    public int DoneCount { get; private set; }

    public string ShowDoneText => string.Format(Localisation.T("todopage.bitenler-n"), DoneCount);

    /// <summary>
    /// Which source is on screen.
    ///
    /// The page mixes three on purpose — most of what somebody has to do after a call is already
    /// known here, and a list holding only the typed items would be the smaller half. But mixing
    /// is not the same as being unable to separate: the suggestions are the machine's, they
    /// arrive in bursts after an analysis, and "show me only what the conversations proposed" is
    /// a real question the one blended list could not answer.
    /// </summary>
    [ObservableProperty] private TodoSource _source = TodoSource.All;

    /// <summary>
    /// What just happened to a row, with a way to undo it.
    ///
    /// Refusing a suggestion is permanent — it is recorded so the same one is never proposed
    /// again — which is exactly the kind of act that must not be one misplaced click away with no
    /// way back. The pattern is the one every list application settled on: do it immediately,
    /// say so quietly, and offer to undo for as long as the line is on screen. A confirmation
    /// dialog would be worse: it interrupts the list to ask about the least consequential thing
    /// on it, and people learn to dismiss it without reading.
    ///
    /// The shared slot, not a third pair of commands: this page used to call the same two verbs
    /// UndoDismiss and ClearNotice, so a page that behaved exactly like the ledger and the
    /// promises had to be bound differently from both.
    /// </summary>
    public UndoSlot Undo { get; } = new();

    /// <summary>Raised when a row wants its conversation opened; the page owns the window.</summary>
    public event EventHandler<TodoEntry>? OpenCallRequested;

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var entries = new List<TodoEntry>();

        LoadContactChoices();

        foreach (var todo in repository.ListTodos(includeDone: true))
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Manual, todo.Id, todo.Text, todo.DueDate, todo.ContactName, todo.CallId, todo.DoneAt is not null));
        }

        // The quote travels with the suggestion.
        //
        // "quote" and "quote_start_ms" are NOT NULL in the schema because every suggestion is
        // anchored to a real, playable sentence — that anchoring is the product's load-bearing
        // rule (PLAN-SOSYALZEKA §3.1). This page read neither column, so the one screen where
        // suggestions are met in bulk showed "Faturayı gönder · öneri Uliana" and left the user
        // to go hunting in the conversation for what was actually said.
        foreach (var (action, contactName) in repository.AllOpenActions())
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Action, action.Id, action.Action, action.DeadlineDate, contactName, action.CallId, done: false,
                action.Quote, action.QuoteStartMs, action.QuoteIsMe));
        }

        // Ticked suggestions belong under "Bitenler" with everything else that got done. Always
        // read, so the section's checkbox can say how many there are; shown only when it is open.
        foreach (var (action, contactName) in repository.AllDoneActions())
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Action, action.Id, action.Action, action.DeadlineDate, contactName, action.CallId, done: true,
                action.Quote, action.QuoteStartMs, action.QuoteIsMe));
        }

        foreach (var (callId, contactName, title, day) in repository.RemindersBetween(today.AddYears(-1), today.AddYears(1)))
        {
            // A board card is allowed to have no title: the reminder is set on the conversation,
            // not on a sentence. Passing that empty string through drew a row with a tick box, a
            // date and nothing to read — a line the user could not act on because it did not say
            // what it was. It names itself instead; whose conversation it is already stands in
            // the caption beside it.
            entries.Add(new TodoEntry(
                TodoEntryKind.Reminder, callId,
                string.IsNullOrWhiteSpace(title) ? Localisation.T("todopage.gorusme-hatirlatmasi") : title.Trim(),
                day, contactName, callId, done: false));
        }

        Overdue.Clear(); Today.Clear(); Upcoming.Clear(); Undated.Clear(); Done.Clear();

        DoneCount = entries.Count(e => e.IsDone);

        var shown = Source switch
        {
            TodoSource.Suggestions => entries.Where(e => e.Kind == TodoEntryKind.Action),
            TodoSource.Mine => entries.Where(e => e.Kind != TodoEntryKind.Action),
            _ => entries,
        };

        foreach (var entry in shown
                     .OrderBy(e => e.Due ?? DateOnly.MaxValue)
                     .ThenBy(e => e.Kind)
                     .ThenBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase))
        {
            if (entry.IsDone)
            {
                if (ShowDone) Done.Add(entry);
            }
            else if (entry.Due is null) Undated.Add(entry);
            else if (entry.Due < today) Overdue.Add(entry);
            else if (entry.Due == today) Today.Add(entry);
            else Upcoming.Add(entry);
        }

        OnPropertyChanged(nameof(HasOverdue));
        OnPropertyChanged(nameof(HasToday));
        OnPropertyChanged(nameof(HasUpcoming));
        OnPropertyChanged(nameof(HasUndated));
        OnPropertyChanged(nameof(HasDone));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(ShowDoneText));
    }

    /// <summary>
    /// Rebuilds the person picker, keeping whoever was chosen.
    ///
    /// The page re-reads itself whenever anything anywhere rules on a suggestion, and a picker
    /// that lost its selection on every one of those would drop the person out of a note halfway
    /// through being typed.
    /// </summary>
    private void LoadContactChoices()
    {
        var chosen = NewContact?.Id;

        ContactChoices.Clear();
        ContactChoices.Add(new ContactChoice(null, Localisation.T("todopage.kisi-yok")));

        foreach (var contact in repository.ListContacts())
            ContactChoices.Add(new ContactChoice(contact.Id, contact.Name));

        NewContact = ContactChoices.FirstOrDefault(c => c.Id == chosen) ?? ContactChoices[0];
    }

    partial void OnShowDoneChanged(bool value) => Refresh();
    partial void OnSourceChanged(TodoSource value) => Refresh();

    [RelayCommand]
    private void ShowAllSources() => Source = TodoSource.All;

    [RelayCommand]
    private void ShowSuggestions() => Source = TodoSource.Suggestions;

    [RelayCommand]
    private void ShowMine() => Source = TodoSource.Mine;

    [RelayCommand]
    private void Add()
    {
        var text = NewText.Trim();
        if (text.Length == 0) return;

        repository.AddTodo(text, NewDue is { } due ? DateOnly.FromDateTime(due) : null, NewContact?.Id);

        NewText = "";
        NewDue = null;

        // The person is cleared too. Keeping it would silently attach the next three notes to
        // somebody the user chose once, and a wrong name on a note is worse than none.
        NewContact = ContactChoices.FirstOrDefault();

        Refresh();
    }

    /// <summary>
    /// Ticks a row, whatever it is: a note is marked done, a suggestion is marked done, a
    /// reminder is cleared. Ticking a done note un-does it.
    /// </summary>
    [RelayCommand]
    private void Toggle(TodoEntry? entry)
    {
        if (entry is null) return;

        // The kind is read before the number, always. The three sources number themselves
        // independently, so "7" is a note, a suggestion and a conversation at once; each branch
        // asks for the number by the name of its own space and gets an exception rather than
        // another table's row if the dispatch is ever wrong.
        switch (entry.Kind)
        {
            case TodoEntryKind.Manual:
                repository.SetTodoDone(entry.TodoId, !entry.IsDone);
                break;

            case TodoEntryKind.Action:
                // Both ways, like the notes beside it. A suggestion ticked by mistake used to be
                // unreachable: it left the list and there was no row left to untick.
                repository.SetActionStatus(entry.ActionId, entry.IsDone ? ActionStatus.Open : ActionStatus.Done);
                break;

            case TodoEntryKind.Reminder:
                repository.RemindOn(entry.ReminderCallId, null);
                break;
        }

        // The other screens showing the same suggestion — the home screen, an open call window —
        // learn of the verdict the way they learn of a deleted call, and the shell answers that
        // by re-reading every page, this one included. Re-reading here first as well meant one
        // tick read the to-do list twice.
        Services.CallActions.NotifyChanged();
    }

    /// <summary>Turns down a suggestion, and remembers it long enough to be taken back.</summary>
    [RelayCommand]
    private void Dismiss(TodoEntry? entry)
    {
        if (entry is null || entry.Kind != TodoEntryKind.Action) return;

        var id = entry.ActionId;

        repository.SetActionStatus(id, ActionStatus.Hidden);

        // The notice is put up before the change is announced, so the one refresh the
        // announcement causes sees the page exactly as it will be drawn. The slot takes the
        // notice down before running the inverse, for the same reason.
        Undo.Offer(new Services.PendingUndo(
            Services.LedgerVerb.Dismiss,
            string.Format(Localisation.T("todopage.reddedildi-n"), Shorten(entry.Text)),
            () =>
            {
                repository.SetActionStatus(id, ActionStatus.Open);
                Services.CallActions.NotifyChanged();
            }));

        Services.CallActions.NotifyChanged();
    }

    /// <summary>Enough of the line to recognise it, not enough to fill the bar.</summary>
    private static string Shorten(string text) =>
        text.Length <= 46 ? text : text[..45].TrimEnd() + "…";

    [RelayCommand]
    private void Delete(TodoEntry? entry)
    {
        if (entry is null || entry.Kind != TodoEntryKind.Manual) return;

        repository.DeleteTodo(entry.TodoId);
        Refresh();
    }

    [RelayCommand]
    private void Open(TodoEntry? entry)
    {
        if (entry?.CallId is not null) OpenCallRequested?.Invoke(this, entry);
    }

    /// <summary>
    /// ▸ on a suggestion: the conversation, at the second the sentence was said.
    ///
    /// Separate from <see cref="Open"/> because they answer different questions. Clicking the
    /// line asks "show me this suggestion"; clicking the moment asks "let me hear it", and only
    /// the second one should start audio.
    /// </summary>
    [RelayCommand]
    private void PlayQuote(TodoEntry? entry)
    {
        if (entry is { HasQuote: true }) PlayQuoteRequested?.Invoke(this, entry);
    }

    /// <summary>Raised by a row's ▸; the page opens the window, because a view model cannot.</summary>
    public event EventHandler<TodoEntry>? PlayQuoteRequested;
}
