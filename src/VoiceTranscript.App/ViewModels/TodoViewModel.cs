using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

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
public sealed partial class TodoViewModel(Repository repository) : ObservableObject
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
    [ObservableProperty] private bool _showDone;

    /// <summary>Raised when a row wants its conversation opened; the page owns the window.</summary>
    public event EventHandler<long>? OpenCallRequested;

    public void Refresh()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var entries = new List<TodoEntry>();

        foreach (var todo in repository.ListTodos(includeDone: ShowDone))
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Manual, todo.Id, todo.Text, todo.DueDate, todo.ContactName, todo.CallId, todo.DoneAt is not null));
        }

        foreach (var (action, contactName) in repository.AllOpenActions())
        {
            entries.Add(new TodoEntry(
                TodoEntryKind.Action, action.Id, action.Action, action.DeadlineDate, contactName, action.CallId, done: false));
        }

        foreach (var (callId, contactName, title, day) in repository.RemindersBetween(today.AddYears(-1), today.AddYears(1)))
        {
            entries.Add(new TodoEntry(TodoEntryKind.Reminder, callId, title, day, contactName, callId, done: false));
        }

        Overdue.Clear(); Today.Clear(); Upcoming.Clear(); Undated.Clear(); Done.Clear();

        foreach (var entry in entries
                     .OrderBy(e => e.Due ?? DateOnly.MaxValue)
                     .ThenBy(e => e.Kind)
                     .ThenBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase))
        {
            if (entry.IsDone) Done.Add(entry);
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
    }

    partial void OnShowDoneChanged(bool value) => Refresh();

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
                repository.SetActionStatus(entry.Id, ActionStatus.Done);
                break;

            case TodoEntryKind.Reminder:
                repository.RemindOn(entry.Id, null);
                break;
        }

        Refresh();
    }

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
        if (entry?.CallId is { } callId) OpenCallRequested?.Invoke(this, callId);
    }
}
