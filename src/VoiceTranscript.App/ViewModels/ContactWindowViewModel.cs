using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One of this person's conversations, as the window lists it.</summary>
public sealed record ContactCall(Call Call, int SegmentCount, bool HasNote, int LedgerEntries)
{
    public long Id => Call.Id;

    public string When => Call.StartedAt.ToLocalTime().ToString("d MMMM yyyy, HH:mm");

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

    public ContactWindowViewModel(Repository repository, long contactId)
    {
        _repository = repository;
        ContactId = contactId;

        Load();
    }

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

        LoadCalls();
        LoadLedger();
    }

    private void LoadCalls()
    {
        var calls = _repository.ListCalls(ContactId, limit: 200);

        // One query for the notes and one for the ledger, then grouped here. A query per row would
        // be two hundred round trips every time the window opens.
        var withNotes = _repository.CallsWithNotes(calls.Select(c => c.Id));

        var ledger = _repository.GetFlags(ContactId)
            .GroupBy(f => f.CallId)
            .ToDictionary(g => g.Key, g => g.Count());

        Calls.Clear();

        foreach (var call in calls)
        {
            Calls.Add(new ContactCall(
                call,
                _repository.CountSegments(call.Id),
                withNotes.Contains(call.Id),
                ledger.GetValueOrDefault(call.Id)));
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

    /// <summary>Reloads after something changed elsewhere — a reprocess, a moved call.</summary>
    [RelayCommand]
    public void Refresh() => Load();

    private static string Span(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} sa {span.Minutes} dk"
        : $"{(int)span.TotalMinutes} dk";
}
