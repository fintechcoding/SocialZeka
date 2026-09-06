using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One day's calls on the archive page.</summary>
public sealed record CallDayGroup(string Day, IReadOnlyList<RecentCall> Calls)
{
    public string Count => Calls.Count == 1 ? "1 görüşme" : $"{Calls.Count} görüşme";
}

/// <summary>
/// Every call, in one list.
///
/// There was none: the first screen stopped at twelve, the rest lived under whichever person
/// they were filed with, and a call nobody had named was reachable from the first screen or
/// not at all. "Dünkü görüşme neydi?" had no page to go to. This is that page — the same row
/// and the same menu as everywhere else, grouped by day, narrowed by whatever the user knows
/// about the call they are looking for: who, when, which app, what state, which tag.
/// </summary>
public sealed partial class CallsViewModel(Repository repository) : ObservableObject
{
    public const string Any = "Hepsi";

    public ObservableCollection<CallDayGroup> Groups { get; } = [];
    public ObservableCollection<ContactChoice> ContactChoices { get; } = [];
    public ObservableCollection<string> TagChoices { get; } = [];

    public IReadOnlyList<SearchPeriod> Periods { get; } =
        [SearchPeriod.Anytime, SearchPeriod.Today, SearchPeriod.Yesterday, SearchPeriod.LastWeek, SearchPeriod.LastMonth, SearchPeriod.LastYear];

    public IReadOnlyList<string> AppChoices { get; } = [Any, "WhatsApp", "Telegram", "Signal"];

    public const string StateFailed = "İşlenemedi";
    public const string StateBusy = "Sırada / işleniyor";
    public const string StateUnanalysed = "Çözümlenmedi";
    public const string StateDone = "Hazır";
    public const string StateUnnamed = "İsimsiz";

    public IReadOnlyList<string> StateChoices { get; } = [Any, StateFailed, StateBusy, StateUnanalysed, StateDone, StateUnnamed];

    [ObservableProperty] private ContactChoice? _selectedContact;
    [ObservableProperty] private SearchPeriod _period = SearchPeriod.Anytime;
    [ObservableProperty] private string _appChoice = Any;
    [ObservableProperty] private string _stateChoice = Any;
    [ObservableProperty] private string _tagChoice = Any;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private int _count;
    [ObservableProperty] private int _total;

    public bool IsEmpty => Count == 0;
    public bool IsFiltered => SelectedContact?.Id is not null || Period != SearchPeriod.Anytime
                              || AppChoice != Any || StateChoice != Any || TagChoice != Any || Query.Trim().Length > 0;

    public string CountText => Count == Total ? $"{Total} görüşme" : $"{Count} / {Total} görüşme";

    /// <summary>True while several filters are being set at once; one rebuild at the end.</summary>
    private bool _settingFilters;

    partial void OnSelectedContactChanged(ContactChoice? value) => RebuildUnlessSetting();
    partial void OnPeriodChanged(SearchPeriod value) => RebuildUnlessSetting();
    partial void OnAppChoiceChanged(string value) => RebuildUnlessSetting();
    partial void OnStateChoiceChanged(string value) => RebuildUnlessSetting();
    partial void OnTagChoiceChanged(string value) => RebuildUnlessSetting();
    partial void OnQueryChanged(string value) => RebuildUnlessSetting();

    private void RebuildUnlessSetting()
    {
        if (!_settingFilters) Rebuild();
    }

    private IReadOnlyList<RecentCall> _all = [];

    /// <summary>Re-reads the archive. Called on arrival and whenever a call changes anywhere.</summary>
    public void Refresh()
    {
        var previousContact = SelectedContact?.Id;
        var previousTag = TagChoice;

        var contacts = repository.ListContacts();

        ContactChoices.Clear();
        ContactChoices.Add(new ContactChoice(null, "Herkes"));
        foreach (var contact in contacts)
            ContactChoices.Add(new ContactChoice(contact.Id, contact.Name));

        var calls = repository.ListCalls(limit: 2000);
        var tags = repository.TagsOf(calls.Select(c => c.Id));

        // Every name is already in hand: the filter above was built from the same list. This used
        // to ask the database for each contact by id again, one query per person in the archive,
        // for names read two lines earlier. A row whose contact is missing from the list falls
        // through to "İsimsiz" exactly as it did when the lookup came back empty.
        var names = contacts.ToDictionary(c => c.Id, c => c.Name);

        _all = [.. calls.Select(call => new RecentCall(
            call,
            call.ContactId is { } id ? names.GetValueOrDefault(id) ?? "İsimsiz" : "İsimsiz",
            tags.GetValueOrDefault(call.Id, [])))];

        TagChoices.Clear();
        TagChoices.Add(Any);
        foreach (var tag in tags.Values.SelectMany(t => t).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(t => t, StringComparer.CurrentCulture))
            TagChoices.Add(tag);

        Total = _all.Count;

        // Restore what the user had chosen; one rebuild for both.
        _settingFilters = true;
        SelectedContact = ContactChoices.FirstOrDefault(c => c.Id == previousContact) ?? ContactChoices[0];
        TagChoice = TagChoices.Contains(previousTag) ? previousTag : Any;
        _settingFilters = false;

        Rebuild();
    }

    /// <summary>The list, narrowed and grouped by day, newest first.</summary>
    private void Rebuild()
    {
        var rows = Filter(_all, SelectedContact?.Id, Period, AppChoice, StateChoice, TagChoice, Query);

        Groups.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);

        foreach (var group in rows.GroupBy(r => DateOnly.FromDateTime(r.Call.StartedAt.ToLocalTime().DateTime)).OrderByDescending(g => g.Key))
        {
            var day = group.Key == today ? "Bugün"
                : group.Key == today.AddDays(-1) ? "Dün"
                : group.Key.ToString("d MMMM yyyy, dddd");

            Groups.Add(new CallDayGroup(day, [.. group.OrderByDescending(r => r.Call.StartedAt)]));
        }

        Count = rows.Count;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Pure, so it can be tested without a window: which rows survive which filters.</summary>
    public static IReadOnlyList<RecentCall> Filter(
        IReadOnlyList<RecentCall> all, long? contactId, SearchPeriod period, string app, string state, string tag, string query)
    {
        var since = period.Since();
        var until = period.Until();
        var needle = TurkishText.NormalizeForSearch(query.Trim());

        return [.. all.Where(r =>
            (contactId is null || r.Call.ContactId == contactId)
            && (since is null || r.Call.StartedAt >= since)
            && (until is null || r.Call.StartedAt < until)
            && (app == Any || string.Equals(r.Call.App.ToString(), app, StringComparison.OrdinalIgnoreCase))
            && MatchesState(r, state)
            && (tag == Any || r.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
            && (needle.Length == 0 || TurkishText.NormalizeForSearch(r.ContactName).Contains(needle)))];
    }

    private static bool MatchesState(RecentCall row, string state) => state switch
    {
        Any => true,
        StateFailed => row.IsFailed,
        StateBusy => row.IsWaiting || row.IsWorking,
        StateUnanalysed => row.Call.State == ProcessingState.Transcribed,
        StateDone => row.Call.State == ProcessingState.Analysed,
        StateUnnamed => row.NeedsLabel,
        _ => true,
    };

    public void ClearFilters()
    {
        _settingFilters = true;
        SelectedContact = ContactChoices.FirstOrDefault();
        Period = SearchPeriod.Anytime;
        AppChoice = Any;
        StateChoice = Any;
        TagChoice = Any;
        Query = "";
        _settingFilters = false;

        Rebuild();
    }
}
