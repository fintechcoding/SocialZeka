using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One of this person's conversations, as the window lists it.</summary>
public sealed record ContactCall(
    Call Call, int SegmentCount, bool HasNote, int LedgerEntries, IReadOnlyList<string> Tags)
{
    public long Id => Call.Id;

    public string When => Call.StartedAt.ToLocalTime().ToString("d MMMM yyyy, HH:mm");

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

    public string When =>
        $"{Hit.CallStartedAt.ToLocalTime():d MMM yyyy} · {TimeSpan.FromMilliseconds(Hit.StartMs):mm\\:ss}";
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
        BirthDatePick = profile?.BirthDate?.ToDateTime(TimeOnly.MinValue);
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

    private void LoadCalls()
    {
        var calls = _repository.ListCalls(ContactId, limit: 200);

        // One query for the notes, one for the ledger, one for the tags, then grouped here.
        // A query per row would be two hundred round trips every time the window opens.
        var withNotes = _repository.CallsWithNotes(calls.Select(c => c.Id));
        var tags = _repository.TagsOf(calls.Select(c => c.Id));

        var ledger = _repository.GetFlags(ContactId)
            .GroupBy(f => f.CallId)
            .ToDictionary(g => g.Key, g => g.Count());

        Calls.Clear();

        var wanted = TagFilter;

        foreach (var call in calls)
        {
            var callTags = tags.GetValueOrDefault(call.Id, []);

            if (wanted != AllTags && !callTags.Contains(wanted)) continue;

            Calls.Add(new ContactCall(
                call,
                _repository.CountSegments(call.Id),
                withNotes.Contains(call.Id),
                ledger.GetValueOrDefault(call.Id),
                callTags));
        }

        // The filter list holds what this person's conversations actually carry — offering the
        // whole archive's vocabulary here would mostly offer empty results.
        var choices = tags.Values.SelectMany(t => t).Distinct().OrderBy(t => t).ToList();

        TagChoices.Clear();
        TagChoices.Add(AllTags);
        foreach (var tag in choices) TagChoices.Add(tag);

        // Written to the field on purpose: going through the property would re-enter LoadCalls.
        if (!TagChoices.Contains(TagFilter))
        {
#pragma warning disable MVVMTK0034
            _tagFilter = AllTags;
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(TagFilter));
        }

        OnPropertyChanged(nameof(HasCalls));
    }

    partial void OnTagFilterChanged(string value) => LoadCalls();

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
        _repository.SetBirthDate(ContactId, value is { } day ? DateOnly.FromDateTime(day) : null);
        BirthdayLine = BirthdayLineFor(
            value is { } d ? DateOnly.FromDateTime(d) : null, DateOnly.FromDateTime(DateTime.Today));
    }

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
