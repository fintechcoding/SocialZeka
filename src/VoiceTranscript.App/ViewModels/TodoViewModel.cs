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
public sealed class TodoEntry(TodoEntryKind kind, long id, string text, DateOnly? due, string? contactName, long? callId, bool done)
{
    public TodoEntryKind Kind { get; } = kind;
    public long Id { get; } = id;
    public string Text { get; } = text;
    public DateOnly? Due { get; } = due;
    public string? ContactName { get; } = contactName;
    public long? CallId { get; } = callId;
    public bool IsDone { get; } = done;

    public bool HasCall => CallId is not null;
    public bool CanDelete => Kind == TodoEntryKind.Manual;

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
    /// </summary>
    [ObservableProperty] private string? _notice;

    private long _undoActionId;

    public bool CanUndo => _undoActionId != 0;

    /// <summary>Raised when a row wants its conversation opened; the page owns the window.</summary>
    public event EventHandler<TodoEntry>? OpenCallRequested;

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var entries = new List<TodoEntry>();

        foreach (var todo in repository.ListTodos(includeDone: true))
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Manual, todo.Id, todo.Text, todo.DueDate, todo.ContactName, todo.CallId, todo.DoneAt is not null));
        }

        foreach (var (action, contactName) in repository.AllOpenActions())
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Action, action.Id, action.Action, action.DeadlineDate, contactName, action.CallId, done: false));
        }

        // Ticked suggestions belong under "Bitenler" with everything else that got done. Always
        // read, so the section's checkbox can say how many there are; shown only when it is open.
        foreach (var (action, contactName) in repository.AllDoneActions())
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Action, action.Id, action.Action, action.DeadlineDate, contactName, action.CallId, done: true));
        }

        foreach (var (callId, contactName, title, day) in repository.RemindersBetween(today.AddYears(-1), today.AddYears(1)))
        {
            entries.Add(new TodoEntry(TodoEntryKind.Reminder, callId, title, day, contactName, callId, done: false));
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

        repository.AddTodo(text, NewDue is { } due ? DateOnly.FromDateTime(due) : null);

        NewText = "";
        NewDue = null;
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

        switch (entry.Kind)
        {
            case TodoEntryKind.Manual:
                repository.SetTodoDone(entry.Id, !entry.IsDone);
                break;

            case TodoEntryKind.Action:
                // Both ways, like the notes beside it. A suggestion ticked by mistake used to be
                // unreachable: it left the list and there was no row left to untick.
                repository.SetActionStatus(entry.Id, entry.IsDone ? ActionStatus.Open : ActionStatus.Done);
                break;

            case TodoEntryKind.Reminder:
                repository.RemindOn(entry.Id, null);
                break;
        }

        Refresh();

        // The other screens showing the same suggestion — the home screen, an open call window —
        // learn of the verdict the way they learn of a deleted call.
        Services.CallActions.NotifyChanged();
    }

    /// <summary>Turns down a suggestion, and remembers it long enough to be taken back.</summary>
    [RelayCommand]
    private void Dismiss(TodoEntry? entry)
    {
        if (entry is null || entry.Kind != TodoEntryKind.Action) return;

        repository.SetActionStatus(entry.Id, ActionStatus.Hidden);

        _undoActionId = entry.Id;
        Notice = string.Format(Localisation.T("todopage.reddedildi-n"), Shorten(entry.Text));

        OnPropertyChanged(nameof(CanUndo));
        Refresh();
        Services.CallActions.NotifyChanged();
    }

    [RelayCommand]
    private void UndoDismiss()
    {
        if (_undoActionId == 0) return;

        repository.SetActionStatus(_undoActionId, ActionStatus.Open);

        _undoActionId = 0;
        Notice = null;

        OnPropertyChanged(nameof(CanUndo));
        Refresh();
        Services.CallActions.NotifyChanged();
    }

    [RelayCommand]
    private void ClearNotice()
    {
        _undoActionId = 0;
        Notice = null;

        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Enough of the line to recognise it, not enough to fill the bar.</summary>
    private static string Shorten(string text) =>
        text.Length <= 46 ? text : text[..45].TrimEnd() + "…";

    [RelayCommand]
    private void Delete(TodoEntry? entry)
    {
        if (entry is null || entry.Kind != TodoEntryKind.Manual) return;

        repository.DeleteTodo(entry.Id);
        Refresh();
    }

    [RelayCommand]
    private void Open(TodoEntry? entry)
    {
        if (entry?.CallId is not null) OpenCallRequested?.Invoke(this, entry);
    }
}
