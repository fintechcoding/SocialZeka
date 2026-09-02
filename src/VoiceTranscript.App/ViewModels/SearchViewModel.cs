using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One matched line, with enough around it to be worth reading.</summary>
/// <summary>How far back a search reaches.</summary>
public enum SearchPeriod
{
    Anytime,

    /// <summary>Since midnight. A busy day can hold a dozen calls and they all read as "Bugün".</summary>
    Today,

    Yesterday,
    LastWeek,
    LastMonth,
    LastYear,
}

public static class SearchPeriodExtensions
{
    /// <summary>The earliest moment a result may come from, or null for no limit.</summary>
    public static DateTimeOffset? Since(this SearchPeriod period) => period switch
    {
        SearchPeriod.Today => DateTimeOffset.Now.Date,
        SearchPeriod.Yesterday => DateTimeOffset.Now.Date.AddDays(-1),
        SearchPeriod.LastWeek => DateTimeOffset.Now.AddDays(-7),
        SearchPeriod.LastMonth => DateTimeOffset.Now.AddMonths(-1),
        SearchPeriod.LastYear => DateTimeOffset.Now.AddYears(-1),
        _ => null,
    };

    /// <summary>The latest moment a result may come from, or null for no limit.</summary>
    public static DateTimeOffset? Until(this SearchPeriod period) => period switch
    {
        // Yesterday is the only bounded one: every other period runs up to now.
        SearchPeriod.Yesterday => DateTimeOffset.Now.Date,
        _ => null,
    };

    public static string Label(this SearchPeriod period) => period switch
    {
        SearchPeriod.Today => "Bugün",
        SearchPeriod.Yesterday => "Dün",
        SearchPeriod.LastWeek => "Son bir hafta",
        SearchPeriod.LastMonth => "Son bir ay",
        SearchPeriod.LastYear => "Son bir yıl",
        _ => "Her zaman",
    };
}

/// <summary>One entry in the contact filter. Null identity means everybody.</summary>
public sealed record ContactChoice(long? Id, string Name);

public sealed record SearchResult(SearchHit Hit, string Query = "")
{
    /// <summary>
    /// The line, split into the part before the match, the match, and the part after.
    ///
    /// Highlighting is what makes a page of results scannable: without it every row is a
    /// paragraph of similar-looking text and the eye has to read all of them. The split is done
    /// on the Turkish-folded form so that a search for "ışık" highlights "IŞIK" — the same rule
    /// the index itself uses, so what is highlighted is always what actually matched.
    /// </summary>
    public (string Before, string Match, string After) Split()
    {
        var text = Text;
        var term = Query.Trim();

        if (term.Length == 0) return (text, "", "");

        var haystack = Core.Text.TurkishText.NormalizeForSearch(text);
        var needle = Core.Text.TurkishText.NormalizeForSearch(term);

        if (needle.Length == 0) return (text, "", "");

        // Normalisation can change length, so the index is only usable when it did not.
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0 || haystack.Length != text.Length) return (text, "", "");

        return (text[..at], text.Substring(at, needle.Length), text[(at + needle.Length)..]);
    }

    public string Before => Split().Before;
    public string Match => Split().Match;
    public string After => Split().After;

    public bool HasMatch => Match.Length > 0;

    public string Who => SpeakerText.For(Hit.IsMe, Hit.ContactName);
    public string ContactName => Hit.ContactName ?? "İsimsiz görüşme";
    public string When => Hit.CallStartedAt.ToLocalTime().ToString("d MMMM yyyy");
    public string Timestamp => $"{Hit.StartMs / 60000:00}:{Hit.StartMs / 1000 % 60:00}";
    public string Text => Hit.Text.Trim();
    public bool IsMe => Hit.IsMe;
}

/// <summary>
/// Search across every transcript.
///
/// The results are the matched lines themselves, grouped by the person they came from. The
/// earlier version filtered the contact list instead, which threw away the one thing the user
/// was looking for: the sentence. Finding the right person is easy; finding the moment they said
/// something is the whole reason to keep recordings at all.
/// </summary>
public sealed partial class SearchViewModel(Repository repository) : ObservableObject
{
    public ObservableCollection<SearchGroup> Groups { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private string? _message;

    /// <summary>Limit results to one person. Null searches everybody.</summary>
    [ObservableProperty] private long? _contactFilter;

    /// <summary>Only show what the other party said. Useful for "what did they promise".</summary>
    [ObservableProperty] private bool _onlyOtherParty;

    /// <summary>"Hepsi" plus every label in the archive: the user's own vocabulary as a filter.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> TagChoices { get; } = ["Hepsi"];

    [ObservableProperty] private string _tagChoice = "Hepsi";

    /// <summary>One tag as a clickable chip: the word, how often it was used, whether it is on.</summary>
    public sealed record TagChip(string Tag, int Count, bool IsActive)
    {
        public string Label => Count > 0 ? $"{Tag} ({Count})" : Tag;
    }

    /// <summary>
    /// The archive's whole tag vocabulary as chips — defined looks first, then anything else in
    /// use. A dropdown hid this list behind a click; a query surface should show its own axes.
    /// </summary>
    public ObservableCollection<TagChip> TagChips { get; } = [];

    /// <summary>Chip click: picks the tag, or turns it off when it was already on.</summary>
    [RelayCommand]
    private void SetTag(string tag)
        => TagChoice = TagChoice == tag ? "Hepsi" : tag;

    partial void OnTagChoiceChanged(string value)
    {
        RebuildChips();
        Search();
    }

    private void RebuildChips()
    {
        // Keyed by the folded spelling: "Önemli" defined in the manager and "önemli" typed on a
        // call are one tag, and must not become two chips with split counts.
        var counts = repository.AllTags()
            .ToDictionary(
                t => Core.Text.TurkishText.NormalizeForSearch(t.Tag),
                t => (t.Tag, t.Count),
                StringComparer.Ordinal);

        var activeFolded = Core.Text.TurkishText.NormalizeForSearch(TagChoice);

        // Defined vocabulary first, in the user's order; then tags in use with no definition.
        List<TagChip> chips = [];
        foreach (var def in Services.TagPalette.All)
        {
            var folded = Core.Text.TurkishText.NormalizeForSearch(def.Tag);
            counts.Remove(folded, out var used);
            chips.Add(new TagChip(def.Tag, used.Count, folded == activeFolded));
        }

        foreach (var (folded, used) in counts.OrderByDescending(c => c.Value.Count))
            chips.Add(new TagChip(used.Tag, used.Count, folded == activeFolded));

        TagChips.Clear();
        foreach (var chip in chips) TagChips.Add(chip);
    }

    /// <summary>
    /// How far back to look.
    ///
    /// A date range rather than a free-form pair of pickers, because the question people
    /// actually have is "was this recent or was it months ago", and two calendar controls make
    /// them do arithmetic to ask it.
    /// </summary>
    [ObservableProperty] private SearchPeriod _period = SearchPeriod.Anytime;

    public IReadOnlyList<SearchPeriod> Periods { get; } =
        [SearchPeriod.Anytime, SearchPeriod.Today, SearchPeriod.Yesterday, SearchPeriod.LastWeek, SearchPeriod.LastMonth, SearchPeriod.LastYear];

    /// <summary>Names of everybody who has a recording, for the contact filter.</summary>
    public ObservableCollection<ContactChoice> ContactChoices { get; } = [];

    [ObservableProperty] private ContactChoice? _selectedContact;

    partial void OnPeriodChanged(SearchPeriod value) => Search();

    partial void OnSelectedContactChanged(ContactChoice? value)
    {
        ContactFilter = value?.Id;
        Search();
    }

    partial void OnOnlyOtherPartyChanged(bool value) => Search();

    /// <summary>Fills the contact filter. Called when the page is shown.</summary>
    public void LoadContacts()
    {
        var previous = SelectedContact?.Id;

        ContactChoices.Clear();
        ContactChoices.Add(new ContactChoice(null, "Herkes"));

        foreach (var contact in repository.ListContacts())
            ContactChoices.Add(new ContactChoice(contact.Id, contact.Name));

        SelectedContact = ContactChoices.FirstOrDefault(c => c.Id == previous) ?? ContactChoices[0];

        // The tag filter alongside the contact filter: both are ways the user already sliced
        // their archive by hand, and a slice you cannot search is a slice that stops mattering.
        var keptTag = TagChoice;
        TagChoices.Clear();
        TagChoices.Add("Hepsi");
        foreach (var (tag, _) in repository.AllTags()) TagChoices.Add(tag);
        foreach (var def in Services.TagPalette.All)
            if (!TagChoices.Contains(def.Tag)) TagChoices.Add(def.Tag);
        TagChoice = TagChoices.Contains(keptTag) ? keptTag : "Hepsi";

        RebuildChips();
    }

    public sealed record SearchGroup(string ContactName, long? ContactId, IReadOnlyList<SearchResult> Results)
    {
        public string Header => $"{ContactName} — {Results.Count} sonuç";
    }

    [RelayCommand]
    private void Search()
    {
        Groups.Clear();
        Message = null;

        if (string.IsNullOrWhiteSpace(Query))
        {
            // No words, but a tag: the tag IS the query. Every conversation wearing it, newest
            // first — which is what "etikete göre sorgulama" means when nothing was typed.
            if (TagChoice != "Hepsi")
            {
                BrowseTag();
                return;
            }

            // A period or a person with no words is also a question: "dünkü görüşmeler",
            // "Uliana ile bu hafta". The list is the answer.
            if (Period != SearchPeriod.Anytime || ContactFilter is not null)
            {
                BrowseCalls();
                return;
            }

            HasSearched = false;
            ResultCount = 0;
            return;
        }

        HasSearched = true;

        var since = Period.Since();

        // Filtered by the database, not afterwards.
        //
        // Narrowing in memory meant the filters were applied to the best five hundred matches in
        // the whole archive: on a common word, one person's lines sit below that and were
        // discarded before the filter saw them, and the screen then said "sonuç yok" about
        // something that was said.
        var hits = repository.Search(
            Query,
            limit: 500,
            contactId: ContactFilter,
            isMe: OnlyOtherParty ? false : null,
            since: since,
            tag: TagChoice == "Hepsi" ? null : TagChoice)
            .ToList();

        ResultCount = hits.Count;

        if (hits.Count == 0)
        {
            Message = $"\"{Query}\" için sonuç yok. Türkçe ekler otomatik taranıyor, yani " +
                      "\"ödeme\" araması \"ödemeyi\" ve \"ödemeden\" sonuçlarını da getirir.";
            return;
        }

        foreach (var group in hits
                     .GroupBy(h => (h.ContactId, h.ContactName))
                     .OrderByDescending(g => g.Count()))
        {
            Groups.Add(new SearchGroup(
                group.Key.ContactName ?? "İsimsiz görüşme",
                group.Key.ContactId,
                [.. group.OrderByDescending(h => h.CallStartedAt).Select(h => new SearchResult(h, Query))]));
        }
    }

    /// <summary>The tag as a query: every conversation carrying it, grouped by person.</summary>
    private void BrowseCalls()
    {
        HasSearched = true;

        var calls = repository.BrowseCalls(ContactFilter, Period.Since(), Period.Until());
        ResultCount = calls.Count;

        if (calls.Count == 0)
        {
            Message = "Bu aralıkta görüşme yok.";
            return;
        }

        Message = $"{calls.Count} görüşme. Bir sonuca çift tıklayınca görüşme açılır; sözcük yazarsan içinde arar.";

        foreach (var group in calls
                     .GroupBy(h => (h.ContactId, h.ContactName))
                     .OrderByDescending(g => g.Max(h => h.CallStartedAt)))
        {
            Groups.Add(new SearchGroup(
                group.Key.ContactName ?? "İsimsiz görüşme",
                group.Key.ContactId,
                [.. group.Select(h => new SearchResult(h))]));
        }
    }

    private void BrowseTag()
    {
        HasSearched = true;

        var calls = repository.TaggedCalls(
            TagChoice, contactId: ContactFilter, since: Period.Since());

        ResultCount = calls.Count;

        if (calls.Count == 0)
        {
            Message = $"\"{TagChoice}\" etiketli görüşme yok.";
            return;
        }

        Message = $"\"{TagChoice}\" etiketli {calls.Count} görüşme. "
                + "Bir sonuca çift tıklayınca görüşme açılır; sözcük yazarsan bu etiketin "
                + "içinde arar.";

        foreach (var group in calls
                     .GroupBy(h => (h.ContactId, h.ContactName))
                     .OrderByDescending(g => g.Max(h => h.CallStartedAt)))
        {
            Groups.Add(new SearchGroup(
                group.Key.ContactName ?? "İsimsiz görüşme",
                group.Key.ContactId,
                [.. group.Select(h => new SearchResult(h))]));
        }
    }

    /// <summary>Raised when a result should be opened in the contact view, at that moment.</summary>
    public event EventHandler<(long? ContactId, long CallId, int StartMs, bool IsMe)>? OpenRequested;

    /// <summary>
    /// Jumps from a search result to the conversation it came from.
    ///
    /// Without this a search answers "was this said" but not "what was said around it", and the
    /// surrounding sentences are usually the point.
    /// </summary>
    [RelayCommand]
    private void Open(SearchResult result)
    {
        // The moment travels with the click. An unnamed call has no contact page; the shell opens
        // the call window directly for it instead of doing nothing.
        OpenRequested?.Invoke(this, (result.Hit.ContactId, result.Hit.CallId, result.Hit.StartMs, result.Hit.IsMe));
    }

    [RelayCommand]
    private void Clear()
    {
        Query = "";
        Groups.Clear();
        HasSearched = false;
        ResultCount = 0;
        Message = null;
    }
}
