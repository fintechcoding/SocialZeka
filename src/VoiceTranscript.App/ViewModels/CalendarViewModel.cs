using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// What one calendar entry IS. Declared weightiest first: within a day the user's own reminders
/// lead and machine suggestions trail, so ordering by kind is ordering by authority.
/// </summary>
public enum CalendarEntryKind
{
    /// <summary>A reminder the user set themselves.</summary>
    Reminder,

    /// <summary>The deadline of a promise the USER made.</summary>
    OwnPromise,

    /// <summary>The deadline of a promise the other side made.</summary>
    TheirPromise,

    /// <summary>A birthday the user typed onto a profile.</summary>
    Birthday,

    /// <summary>The deadline of a machine-suggested action — displayed, never acted on here.</summary>
    ActionSuggestion,
}

/// <summary>One thing on one day, however it got there.</summary>
public sealed record CalendarEntry(
    CalendarEntryKind Kind,
    DateOnly Day,
    string Text,
    string ContactName,
    long? CallId,
    long? ContactId = null)
{
    public string Glyph => Kind switch
    {
        CalendarEntryKind.Reminder => "🔔",
        CalendarEntryKind.OwnPromise or CalendarEntryKind.TheirPromise => "🤝",
        CalendarEntryKind.Birthday => "🎂",

        // Hollow on purpose: a suggestion the user never confirmed must read weaker than
        // anything they wrote themselves.
        _ => "○",
    };

    /// <summary>
    /// Colour follows the product's existing language: red is a reminder, MeBrush is the user's
    /// own promise, ThemBrush the other side's, accent a birthday — and a suggestion gets the
    /// tertiary text colour, visibly the quietest thing in the cell.
    /// </summary>
    public string BrushKey => Kind switch
    {
        CalendarEntryKind.Reminder => "SystemFillColorCriticalBrush",
        CalendarEntryKind.OwnPromise => "MeBrush",
        CalendarEntryKind.TheirPromise => "ThemBrush",
        CalendarEntryKind.Birthday => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    public string KindLabel => Kind switch
    {
        CalendarEntryKind.Reminder => "Hatırlatıcı",
        CalendarEntryKind.OwnPromise => "Senin sözün",
        CalendarEntryKind.TheirPromise => "Karşı tarafın sözü",
        CalendarEntryKind.Birthday => "Doğum günü",
        _ => "Aksiyon önerisi",
    };

    /// <summary>The suggestion rows are dimmed wholesale, not just their glyph.</summary>
    public bool IsSuggestion => Kind == CalendarEntryKind.ActionSuggestion;

    /// <summary>The one-line form a day cell shows.</summary>
    public string Line => Kind switch
    {
        CalendarEntryKind.Reminder => Text.Length > 0 ? Text : ContactName,
        CalendarEntryKind.OwnPromise => $"Sen: {Text}",
        CalendarEntryKind.Birthday => ContactName,
        _ => $"{ContactName}: {Text}",
    };

    /// <summary>The agenda's second line: what kind of thing, and with whom.</summary>
    public string Detail => Kind == CalendarEntryKind.Birthday
        ? KindLabel
        : $"{KindLabel} · {ContactName}";

    public string OpenHint => CallId is not null ? "Bağlı görüşmeyi açar" : "Kişiyi açar";
}

/// <summary>
/// One cell of the month grid. A class rather than a record because selection is state the cell
/// itself carries — rebuilding 42 rows to move a highlight would be churn for nothing.
/// </summary>
public sealed partial class CalendarCell(
    DateOnly date, bool inMonth, bool isToday, IReadOnlyList<CalendarEntry> entries) : ObservableObject
{
    public DateOnly Date { get; } = date;
    public bool InMonth { get; } = inMonth;
    public bool IsToday { get; } = isToday;
    public IReadOnlyList<CalendarEntry> Entries { get; } = entries;

    [ObservableProperty] private bool _isSelected;

    public string Label => Date.Day.ToString();

    /// <summary>At most three lines fit a cell; the rest become "+N daha".</summary>
    public IReadOnlyList<CalendarEntry> Preview { get; } = [.. entries.Take(3)];

    public int MoreCount => Math.Max(0, Entries.Count - 3);
    public bool HasMore => MoreCount > 0;
    public string MoreText => $"+{MoreCount} daha";
}

/// <summary>
/// The Takvim page: a full month of everything date-bearing in the archive — the user's
/// reminders, both sides' promise deadlines, birthdays, and (dimmest) suggested actions.
///
/// Display only, by the product's iron law: nothing here writes into the user's spaces. The
/// mini calendar on the overview answers "yarın ne var?" in a corner; this page answers it
/// with room for the whole month and a day's full agenda beside it.
/// </summary>
public sealed partial class CalendarViewModel(Repository repository) : ObservableObject
{
    /// <summary>First day of the month being shown.</summary>
    [ObservableProperty] private DateOnly _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty] private string _title = "";

    /// <summary>The 42 cells of a Monday-first six-week grid.</summary>
    public ObservableCollection<CalendarCell> Days { get; } = [];

    /// <summary>The day whose agenda fills the right-hand panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedDayHeader))]
    [NotifyPropertyChangedFor(nameof(SelectedDayIsEmpty))]
    private CalendarCell? _selected;

    /// <summary>The selected day's items in full, weightiest first.</summary>
    public ObservableCollection<CalendarEntry> Agenda { get; } = [];

    public bool HasSelection => Selected is not null;

    public bool SelectedDayIsEmpty => Selected is { Entries.Count: 0 };

    public string SelectedDayHeader => Selected is { } day
        ? day.Date.ToDateTime(TimeOnly.MinValue).ToString("d MMMM dddd")
        : "";

    [RelayCommand]
    private void PrevMonth()
    {
        Month = Month.AddMonths(-1);
        Refresh();
    }

    [RelayCommand]
    private void NextMonth()
    {
        Month = Month.AddMonths(1);
        Refresh();
    }

    [RelayCommand]
    private void Today()
    {
        Month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        Refresh();

        if (Days.FirstOrDefault(d => d.IsToday) is { } today) SelectDay(today);
    }

    /// <summary>Any day is selectable — an empty agenda saying so beats a dead click.</summary>
    public void SelectDay(CalendarCell cell)
    {
        if (Selected is { } old) old.IsSelected = false;

        cell.IsSelected = true;
        Selected = cell;

        Agenda.Clear();
        foreach (var entry in cell.Entries) Agenda.Add(entry);

        // Selected changed before the agenda was refilled, so this one is re-announced.
        OnPropertyChanged(nameof(SelectedDayIsEmpty));
    }

    [RelayCommand]
    public void Refresh()
    {
        // Monday-first, same arithmetic as the overview's mini calendar: a calendar that
        // disagrees with the one on the user's wall reads as broken.
        var lead = ((int)Month.DayOfWeek + 6) % 7;
        var start = Month.AddDays(-lead);
        var end = start.AddDays(41);

        var entries = new List<CalendarEntry>();

        foreach (var (callId, name, title, day) in repository.RemindersBetween(start, end))
            entries.Add(new CalendarEntry(CalendarEntryKind.Reminder, day, title, name, callId));

        foreach (var (callId, name, obligation, day) in repository.OwnCommitmentsBetween(start, end))
            entries.Add(new CalendarEntry(CalendarEntryKind.OwnPromise, day, obligation, name, callId));

        foreach (var (callId, name, obligation, day) in repository.TheirCommitmentsBetween(start, end))
            entries.Add(new CalendarEntry(CalendarEntryKind.TheirPromise, day, obligation, name, callId));

        foreach (var (contactId, name, day, _) in repository.UpcomingBirthdays(start, withinDays: 41))
            entries.Add(new CalendarEntry(CalendarEntryKind.Birthday, day, "", name, null, contactId));

        foreach (var (callId, name, action, day) in repository.ActionsDueBetween(start, end))
            entries.Add(new CalendarEntry(CalendarEntryKind.ActionSuggestion, day, action, name, callId));

        var byDay = entries
            .GroupBy(e => e.Day)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CalendarEntry>)[.. g.OrderBy(e => e.Kind)]);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var picked = Selected?.Date;

        Days.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);

            Days.Add(new CalendarCell(
                date,
                inMonth: date.Month == Month.Month && date.Year == Month.Year,
                isToday: date == today,
                byDay.GetValueOrDefault(date, [])));
        }

        Title = Month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy");

        // The pick survives a rebuild by date; arriving fresh on the current month it lands on
        // today, so the agenda has something honest to say immediately.
        Selected = null;
        var restore = picked is { } date2 ? Days.FirstOrDefault(d => d.Date == date2) : null;

        if ((restore ?? Days.FirstOrDefault(d => d.IsToday)) is { } cell) SelectDay(cell);
    }
}
