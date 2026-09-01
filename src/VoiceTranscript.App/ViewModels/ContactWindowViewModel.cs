using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One of this person's conversations, as the window lists it.</summary>
public sealed record ContactCall(
    Call Call, int SegmentCount, bool HasNote, int LedgerEntries, IReadOnlyList<string> Tags,
    DateOnly? RemindOn = null)
{
    // The detail strip, cell by cell: every row shows the same facts in the same order with
    // the same dress, which is what makes a list of mixed states readable as a table.

    /// <summary>First cell: did the audio become text, and how much of it.</summary>
    public string LineChip => SegmentCount > 0 ? $"{SegmentCount} satır" : "metin yok";

    public bool IsAnalysed => Call.State == ProcessingState.Analysed;

    /// <summary>Second cell: did the text become a ledger.</summary>
    public string AnalysisChip => Call.State switch
    {
        ProcessingState.Analysed => "çözümlendi",
        ProcessingState.Failed => "işlenemedi",
        ProcessingState.Skipped => "atlandı",
        _ => "çözümlenmedi",
    };

    public bool HasReminder => RemindOn is not null;

    /// <summary>Third cell: the reminder hanging on this conversation, when one is set.</summary>
    public string ReminderChip => RemindOn is { } day
        ? day.ToDateTime(TimeOnly.MinValue).ToString("d MMM")
        : "";

    public long Id => Call.Id;

    public string When => Call.StartedAt.ToLocalTime().ToString("d MMMM yyyy, HH:mm")
        + (Call.Direction switch
        {
            CallDirection.Incoming => "  ↓",
            CallDirection.Outgoing => "  ↑",
            _ => "",
        });

    /// <summary>The month heading this row sits under. Months, because a person talked with for
    /// years produces a list where day headings would outnumber the rows.</summary>
    public string Month => Call.StartedAt.ToLocalTime().ToString("MMMM yyyy");

    public bool HasTags => Tags.Count > 0;

    public string Length => $"{(int)Call.Duration.TotalMinutes:00}:{Call.Duration.Seconds:00}";

    public string AppName => Call.App.ToString();

    public bool HasTranscript => SegmentCount > 0;

    public string State => Call.State switch
    {
        ProcessingState.Analysed => $"{SegmentCount} satır · çözümlendi",
        ProcessingState.Transcribed => $"{SegmentCount} satır · çözümlenmedi",
        ProcessingState.Failed => "İşlenemedi",
        ProcessingState.Skipped => "Atlandı",
        _ when SegmentCount > 0 => $"{SegmentCount} satır",
        _ => "Metin yok",
    };

    public bool HasLedger => LedgerEntries > 0;
    public string LedgerText => $"{LedgerEntries} defter kaydı";
}

/// <summary>One matching line, with enough around it to be worth reading.</summary>
public sealed record ContactHit(SearchHit Hit)
{
    public long CallId => Hit.CallId;
    public int StartMs => Hit.StartMs;
    public string Text => Hit.Text;
    public bool IsMe => Hit.IsMe;
    public string Speaker => Hit.IsMe ? "Ben" : "Karşı taraf";

    public string When
    {
        get
        {
            // Hour-aware: "mm\:ss" drops the hour, and a match at minute 65 must not claim
            // to be at minute 5 — the timestamp is what makes the hit playable evidence.
            var t = TimeSpan.FromMilliseconds(Hit.StartMs);

            var position = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";

            return $"{Hit.CallStartedAt.ToLocalTime():d MMM yyyy} · {position}";
        }
    }
}

/// <summary>
/// One person, on their own.
///
/// The contact page already lists somebody's calls beside everything else, and that is the right
/// shape for browsing. This is for the other thing people do: working on one person. Three
/// capabilities justify a window rather than a fourth tab, and each was chosen because it cannot
/// live on the page:
///
///   <b>Line-level search within this person.</b> The box on the contact page narrows the call
///   list — a different question, documented as such where it is implemented. A second box in the
///   same place would either overload one control with two meanings or sit beside a nearly
///   identical one. And the archive-wide search screen answered this badly until recently: it
///   fetched the best five hundred matches from everybody and filtered afterwards, so a common
///   word made one person's lines disappear entirely.
///
///   <b>Notes about the person.</b> The <c>notes</c> column has existed since the first schema and
///   nothing ever wrote to it. Notes about a person are not notes about a call, and the page has
///   no concept of either.
///
///   <b>Two people at once.</b> The page holds a single selected contact and a single player, in
///   one instance owned by the shell. Comparing two people, or keeping one open while working in
///   the ledger, is only possible with windows.
///
/// What it deliberately does not do: identity repair. Renaming, merging, moving and deleting stay
/// on the page, where the thing being repaired is the thing under the pointer. A window that also
/// offered them would be a second place to do the same job, and the two would drift.
/// </summary>
public sealed partial class ContactWindowViewModel : ObservableObject
{
    private readonly Repository _repository;
    private readonly string _photosDirectory;

    public ContactWindowViewModel(Repository repository, long contactId, string? photosDirectory = null)
    {
        _repository = repository;
        _photosDirectory = photosDirectory ?? "";
        ContactId = contactId;

        // Month headings over the live collection: the view regroups itself as rows change, so
        // filtering by tag keeps its headings without any bookkeeping here.
        var view = new System.Windows.Data.ListCollectionView(Calls);
        view.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(ContactCall.Month)));
        CallsView = view;

        Load();
    }

    /// <summary>The call list as the window shows it: grouped under month headings.</summary>
    public System.ComponentModel.ICollectionView CallsView { get; }

    public long ContactId { get; }

    public ObservableCollection<ContactCall> Calls { get; } = [];
    public ObservableCollection<ContactHit> Hits { get; } = [];
    public ObservableCollection<Commitment> Commitments { get; } = [];
    public ObservableCollection<Claim> Claims { get; } = [];
    public ObservableCollection<Flag> Flags { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _appName = "";

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _searchMessage;
    [ObservableProperty] private bool _onlyOtherParty;

    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _noteSaved = true;

    // ---- profile: what the user knows about this person ---------------------
    //
    // Everything below is user-entered. The pipeline never writes it; the caption on the tab
    // says so, because the distinction is the product's spine: the ledger is the machine's,
    // quotes and all — this is the user's, and needs none.

    /// <summary>Absolute path of the stored photo, or null for the initials avatar.</summary>
    [ObservableProperty] private string? _photoPath;

    public bool HasPhoto => PhotoPath is not null;

    [ObservableProperty] private DateTime? _birthDatePick;

    /// <summary>"14 Mart 1988 · 38 yaşında · 12 gün sonra doğum günü", from the user's own entry.</summary>
    [ObservableProperty] private string? _birthdayLine;

    [ObservableProperty] private string? _profileMessage;

    public ObservableCollection<ContactField> Fields { get; } = [];

    public bool HasFields => Fields.Count > 0;

    [ObservableProperty] private string _newFieldLabel = "";
    [ObservableProperty] private string _newFieldValue = "";

    // ---- tag filter over the call list --------------------------------------

    /// <summary>"Hepsi" plus every tag this person's conversations carry.</summary>
    public ObservableCollection<string> TagChoices { get; } = [];

    [ObservableProperty] private string _tagFilter = AllTags;

    public const string AllTags = "Hepsi";

    public bool HasCalls => Calls.Count > 0;
    public bool HasHits => Hits.Count > 0;
    public bool HasLedger => Commitments.Count > 0 || Claims.Count > 0 || Flags.Count > 0;

    private void Load()
    {
        var contact = _repository.GetContact(ContactId);
        if (contact is null) return;

        Name = contact.Name;
        AppName = contact.App.ToString();
        Note = contact.Notes ?? "";
        NoteSaved = true;

        // Counted rather than derived from the list: ListCalls caps at two hundred rows, and the
        // people this window exists for are exactly the ones who exceed it.
        var totals = _repository.ContactTotals(ContactId);

        Subtitle = totals.Calls == 0
            ? "Henüz kayıtlı görüşme yok."
            : $"{totals.Calls} görüşme · {Span(totals.Recorded)} kayıt"
              + (totals is { First: { } first, Last: { } last }
                  ? $" · {first.ToLocalTime():d MMM yyyy} – {last.ToLocalTime():d MMM yyyy}"
                  : "");

        LoadProfile();
        LoadCalls();
        LoadLedger();
    }

    private void LoadProfile()
    {
        var profile = _repository.GetProfile(ContactId);

        PhotoPath = Services.ContactPhotoStore.PathFor(profile?.PhotoFile, _photosDirectory);

        _loadingProfile = true;
        BirthDatePick = profile?.BirthDate?.ToDateTime(TimeOnly.MinValue);
        _loadingProfile = false;

        BirthdayLine = BirthdayLineFor(profile?.BirthDate, DateOnly.FromDateTime(DateTime.Today));

        Fields.Clear();
        foreach (var field in _repository.GetFields(ContactId)) Fields.Add(field);

        OnPropertyChanged(nameof(HasPhoto));
        OnPropertyChanged(nameof(HasFields));
    }

    /// <summary>All arithmetic from the user's own entry — the application infers nothing.</summary>
    public static string? BirthdayLineFor(DateOnly? birth, DateOnly today)
    {
        if (birth is not { } day) return null;

        var age = today.Year - day.Year;
        var thisYears = new DateOnly(today.Year, day.Month, Math.Min(day.Day, DateTime.DaysInMonth(today.Year, day.Month)));

        if (thisYears > today) age--;

        var next = thisYears >= today
            ? thisYears
            : new DateOnly(today.Year + 1, day.Month, Math.Min(day.Day, DateTime.DaysInMonth(today.Year + 1, day.Month)));

        var away = next.DayNumber - today.DayNumber;

        var line = $"{day:d MMMM yyyy} · {age} yaşında";

        if (away == 0) return line + " · bugün doğum günü 🎂";
        if (away <= 30) return line + $" · {away} gün sonra doğum günü";

        return line;
    }

    // ---- the filter bar -----------------------------------------------------
    //
    // Built for the person this window exists for: months of conversations with one contact.
    // Every filter answers a question that archive actually gets asked — when was it, did it
    // get analysed, what did I label it, was it a long one, did I write anything on it — and
    // they compose, because "geçen ayki, 'tehdit' etiketli, uzun görüşme" is one question.

    [ObservableProperty] private DateTime? _filterFrom;
    [ObservableProperty] private DateTime? _filterTo;
    [ObservableProperty] private string _stateFilter = AllStates;
    [ObservableProperty] private int _minMinutes;
    [ObservableProperty] private bool _onlyNoted;
    [ObservableProperty] private string _sortOrder = SortNewest;

    public const string AllStates = "Hepsi";
    public const string SortNewest = "Yeni → eski";
    public const string SortOldest = "Eski → yeni";
    public const string SortLongest = "En uzun";

    public IReadOnlyList<string> StateChoices { get; } =
        [AllStates, "Çözümlenmiş", "Çözümlenmemiş", "Başarısız"];

    public IReadOnlyList<string> SortChoices { get; } = [SortNewest, SortOldest, SortLongest];

    // The 95% case is "browse, maybe pick a period" — so the period is one visible row of
    // chips, and everything rarer folds into a labelled panel behind a badge that counts what
    // is on. Seven bare controls in a row taught nobody anything; the user said so.

    public const string PresetAll = "Tümü";
    public const string PresetMonth = "Bu ay";
    public const string PresetQuarter = "3 ay";
    public const string PresetYear = "1 yıl";

    [ObservableProperty] private string _periodPreset = PresetAll;

    partial void OnPeriodPresetChanged(string value)
    {
        // One reload, not three: the preset writes both dates through the fields and refreshes
        // itself, so choosing "Bu ay" is one click and one query.
        _reloading = true;

        var today = DateTime.Today;

        (FilterFrom, FilterTo) = value switch
        {
            PresetMonth => (new DateTime(today.Year, today.Month, 1), (DateTime?)null),
            PresetQuarter => (today.AddMonths(-3), (DateTime?)null),
            PresetYear => (today.AddYears(-1), (DateTime?)null),
            _ => ((DateTime?)null, (DateTime?)null),
        };

        _reloading = false;
        LoadCalls();
    }

    /// <summary>How many advanced filters are on — the number on the Filtreler badge.</summary>
    public int ActiveFilterCount =>
        (StateFilter != AllStates ? 1 : 0)
        + (TagFilter != AllTags && TagFilter is not null ? 1 : 0)
        + (MinMinutes > 0 ? 1 : 0)
        + (OnlyNoted ? 1 : 0)
        + (FilterFrom is not null || FilterTo is not null ? 1 : 0);

    /// <summary>Whether the advanced panel is open. State only; the view draws it.</summary>
    [ObservableProperty] private bool _filtersOpen;

    [RelayCommand]
    private void SetPreset(string preset) => PeriodPreset = preset;

    /// <summary>One line saying what the list is currently NOT showing — a silent filter is a
    /// list that looks like data loss.</summary>
    public bool FiltersActive =>
        FilterFrom is not null || FilterTo is not null || StateFilter != AllStates
        || MinMinutes > 0 || OnlyNoted || TagFilter != AllTags;

    [RelayCommand]
    private void ResetFilters()
    {
        _reloading = true;
        FilterFrom = null;
        FilterTo = null;
        StateFilter = AllStates;
        MinMinutes = 0;
        OnlyNoted = false;
        TagFilter = AllTags;
        SortOrder = SortNewest;
        _reloading = false;

        LoadCalls();
    }

    partial void OnFilterFromChanged(DateTime? value) => LoadCalls();
    partial void OnFilterToChanged(DateTime? value) => LoadCalls();
    partial void OnStateFilterChanged(string value) => LoadCalls();
    partial void OnMinMinutesChanged(int value) => LoadCalls();
    partial void OnOnlyNotedChanged(bool value) => LoadCalls();
    partial void OnSortOrderChanged(string value) => LoadCalls();
    partial void OnTagFilterChanged(string value) => LoadCalls();

    /// <summary>
    /// Guards LoadCalls against its own side effects. Clearing TagChoices while the ComboBox's
    /// SelectedItem is bound makes WPF push null into TagFilter synchronously, which re-entered
    /// LoadCalls mid-rebuild and filtered every row out — the list emptied itself the moment it
    /// refreshed. The guard makes the rebuild atomic from the bindings' point of view.
    /// </summary>
    private bool _reloading;

    private void LoadCalls()
    {
        if (_reloading) return;

        _reloading = true;
        try
        {
            LoadCallsCore();
        }
        finally
        {
            _reloading = false;
        }

        OnPropertyChanged(nameof(FiltersActive));
        OnPropertyChanged(nameof(ActiveFilterCount));
    }

    /// <summary>How many conversations one click of "daha eski" adds to the list.</summary>
    private const int CallPageSize = 100;

    /// <summary>How many pages the user has asked to see so far.</summary>
    private int _visibleCallPages = 1;

    /// <summary>True while the archive holds conversations older than what is loaded.</summary>
    [ObservableProperty] private bool _hasMoreCalls;

    /// <summary>
    /// Loads the next page of history. Months of calls with one person used to be capped at a
    /// silent 200 — conversations older than that simply did not exist on screen, which on an
    /// archive whose whole promise is remembering is the worst possible failure to have quietly.
    /// </summary>
    [RelayCommand]
    private void LoadMoreCalls()
    {
        _visibleCallPages++;
        LoadCalls();
    }

    private void LoadCallsCore()
    {
        // One row past the window says whether older history exists, without counting it all.
        var window = CallPageSize * _visibleCallPages;
        var calls = _repository.ListCalls(ContactId, limit: window + 1);

        HasMoreCalls = calls.Count > window;
        if (HasMoreCalls) calls = [.. calls.Take(window)];

        // Batched: one query each for notes, tags and segment counts. A query per row would be
        // two hundred round trips every time the window opens.
        var ids = calls.Select(c => c.Id).ToList();
        var withNotes = _repository.CallsWithNotes(ids);
        var tags = _repository.TagsOf(ids);
        var segmentCounts = _repository.SegmentCounts(ids);
        var reminders = _repository.RemindersOf(ids);

        // "N defter kaydı" counts everything the ledger holds for the call — commitments,
        // claims AND flags. It used to count flags alone, so a call with three commitments and
        // no flags wore no badge at all: a promise the row didn't keep.
        var ledger = new Dictionary<long, int>();

        void Bump(long callId) => ledger[callId] = ledger.GetValueOrDefault(callId) + 1;

        foreach (var f in _repository.GetFlags(ContactId)) Bump(f.CallId);
        foreach (var c in _repository.GetOpenCommitments(ContactId)) Bump(c.CallId);
        foreach (var c in _repository.GetAllClaims(ContactId)) Bump(c.CallId);

        var wanted = TagFilter ?? AllTags;

        var filtered = calls.Where(call =>
        {
            if (FilterFrom is { } from && call.StartedAt.ToLocalTime().Date < from.Date) return false;
            if (FilterTo is { } to && call.StartedAt.ToLocalTime().Date > to.Date) return false;

            if (MinMinutes > 0 && call.Duration.TotalMinutes < MinMinutes) return false;

            if (OnlyNoted && !withNotes.Contains(call.Id)) return false;

            if (wanted != AllTags && !tags.GetValueOrDefault(call.Id, []).Contains(wanted)) return false;

            return StateFilter switch
            {
                "Çözümlenmiş" => call.State == ProcessingState.Analysed,
                "Çözümlenmemiş" => call.State == ProcessingState.Transcribed,
                "Başarısız" => call.State == ProcessingState.Failed,
                _ => true,
            };
        });

        filtered = SortOrder switch
        {
            SortOldest => filtered.OrderBy(c => c.StartedAt),
            SortLongest => filtered.OrderByDescending(c => c.Duration),
            _ => filtered.OrderByDescending(c => c.StartedAt),
        };

        Calls.Clear();

        foreach (var call in filtered)
        {
            Calls.Add(new ContactCall(
                call,
                segmentCounts.GetValueOrDefault(call.Id),
                withNotes.Contains(call.Id),
                ledger.GetValueOrDefault(call.Id),
                tags.GetValueOrDefault(call.Id, []),
                reminders.TryGetValue(call.Id, out var remindOn) ? remindOn : null));
        }

        // The filter list holds what this person's conversations actually carry — offering the
        // whole archive's vocabulary here would mostly offer empty results.
        var choices = tags.Values.SelectMany(t => t).Distinct().OrderBy(t => t).ToList();

        TagChoices.Clear();
        TagChoices.Add(AllTags);
        foreach (var tag in choices) TagChoices.Add(tag);

        if (!TagChoices.Contains(TagFilter ?? ""))
        {
#pragma warning disable MVVMTK0034
            _tagFilter = AllTags;
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(TagFilter));
        }

        OnPropertyChanged(nameof(HasCalls));
    }

    private void LoadLedger()
    {
        Commitments.Clear();
        Claims.Clear();
        Flags.Clear();

        foreach (var c in _repository.GetOpenCommitments(ContactId)) Commitments.Add(c);
        foreach (var c in _repository.GetAllClaims(ContactId)) Claims.Add(c);
        foreach (var f in _repository.GetFlags(ContactId)) Flags.Add(f);

        OnPropertyChanged(nameof(HasLedger));
    }

    // ---- search -------------------------------------------------------------

    /// <summary>
    /// Finds a word in what this person and the user said to each other.
    ///
    /// Scoped in SQL rather than filtered afterwards. Doing it the other way is what made the
    /// archive-wide screen report "sonuç yok" for things that had been said: a common word fills
    /// the result limit with everybody else's lines before this person's are reached.
    /// </summary>
    [RelayCommand]
    private void Search()
    {
        var query = Query?.Trim();

        Hits.Clear();
        OnPropertyChanged(nameof(HasHits));

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchMessage = null;
            return;
        }

        var found = _repository.Search(
            query,
            limit: 300,
            contactId: ContactId,
            isMe: OnlyOtherParty ? false : null);

        foreach (var hit in found) Hits.Add(new ContactHit(hit));

        OnPropertyChanged(nameof(HasHits));

        SearchMessage = found.Count == 0
            ? $"\"{query}\" bu kişiyle olan görüşmelerde geçmiyor. Türkçe ekler otomatik taranır."
            : $"{found.Count} satır bulundu.";
    }

    partial void OnOnlyOtherPartyChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(Query)) Search();
    }

    // ---- notes --------------------------------------------------------------

    /// <summary>
    /// Saves what the user thinks about this person.
    ///
    /// Explicit rather than on every keystroke, for the same reason call notes are: a note is a
    /// considered thing, and writing per character means writing while somebody is still deciding.
    /// </summary>
    [RelayCommand]
    private void SaveNote()
    {
        _repository.SaveContactNote(ContactId, Note);
        NoteSaved = true;
    }

    partial void OnNoteChanged(string value) => NoteSaved = false;

    // ---- profile commands ---------------------------------------------------

    /// <summary>Brings a picked photo in: copied, shrunk, EXIF stripped. Never referenced in place.</summary>
    public void SetPhoto(string sourcePath)
    {
        var old = _repository.GetProfile(ContactId)?.PhotoFile;

        var stored = Services.ContactPhotoStore.Import(sourcePath, ContactId, _photosDirectory);

        if (stored is null)
        {
            ProfileMessage = "Fotoğraf okunamadı. Başka bir dosya dene.";
            return;
        }

        _repository.SetContactPhoto(ContactId, stored);
        Services.ContactPhotoStore.Delete(old, _photosDirectory);

        ProfileMessage = null;
        LoadProfile();
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        var old = _repository.GetProfile(ContactId)?.PhotoFile;

        _repository.SetContactPhoto(ContactId, null);
        Services.ContactPhotoStore.Delete(old, _photosDirectory);

        LoadProfile();
    }

    partial void OnBirthDatePickChanged(DateTime? value)
    {
        // The load guard matters here: LoadProfile sets this property from the database, and
        // without the guard every window-open wrote the value straight back — a needless write
        // that also stamped updated_at as though the user had edited something.
        if (!_loadingProfile)
            _repository.SetBirthDate(ContactId, value is { } day ? DateOnly.FromDateTime(day) : null);

        BirthdayLine = BirthdayLineFor(
            value is { } d ? DateOnly.FromDateTime(d) : null, DateOnly.FromDateTime(DateTime.Today));
    }

    private bool _loadingProfile;

    [RelayCommand]
    private void AddField()
    {
        if (string.IsNullOrWhiteSpace(NewFieldLabel) || string.IsNullOrWhiteSpace(NewFieldValue))
        {
            ProfileMessage = "Etiket ve değer birlikte gerekir — örneğin 'Meslek: Mimar'.";
            return;
        }

        _repository.AddField(ContactId, NewFieldLabel, NewFieldValue);

        NewFieldLabel = "";
        NewFieldValue = "";
        ProfileMessage = null;

        LoadProfile();
    }

    public void RemoveField(ContactField field)
    {
        _repository.RemoveField(field.Id);
        LoadProfile();
    }

    /// <summary>Reloads after something changed elsewhere — a reprocess, a moved call.</summary>
    [RelayCommand]
    public void Refresh() => Load();

    private static string Span(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} sa {span.Minutes} dk"
        : $"{(int)span.TotalMinutes} dk";
}
