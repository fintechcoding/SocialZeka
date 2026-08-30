using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Storage;

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

    public string Who => Hit.IsMe ? "Ben" : Hit.ContactName ?? "Karşı taraf";
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

    /// <summary>
    /// How far back to look.
    ///
    /// A date range rather than a free-form pair of pickers, because the question people
    /// actually have is "was this recent or was it months ago", and two calendar controls make
    /// them do arithmetic to ask it.
    /// </summary>
    [ObservableProperty] private SearchPeriod _period = SearchPeriod.Anytime;

    public IReadOnlyList<SearchPeriod> Periods { get; } =
        [SearchPeriod.Anytime, SearchPeriod.LastWeek, SearchPeriod.LastMonth, SearchPeriod.LastYear];

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
            HasSearched = false;
            ResultCount = 0;
            return;
        }

        HasSearched = true;

        var since = Period.Since();

        var hits = repository.Search(Query, limit: 500)
            .Where(h => ContactFilter is null || h.ContactId == ContactFilter)
            .Where(h => !OnlyOtherParty || !h.IsMe)
            .Where(h => since is null || h.CallStartedAt >= since)
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

    /// <summary>Raised when a result should be opened in the contact view, at that moment.</summary>
    public event EventHandler<(long ContactId, long CallId)>? OpenRequested;

    /// <summary>
    /// Jumps from a search result to the conversation it came from.
    ///
    /// Without this a search answers "was this said" but not "what was said around it", and the
    /// surrounding sentences are usually the point.
    /// </summary>
    [RelayCommand]
    private void Open(SearchResult result)
    {
        if (result.Hit.ContactId is { } contactId)
            OpenRequested?.Invoke(this, (contactId, result.Hit.CallId));
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
