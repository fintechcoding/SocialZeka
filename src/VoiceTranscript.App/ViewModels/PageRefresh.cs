namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// Which pages need re-reading from the archive, and when.
///
/// What used to happen, so that nobody puts it back: the shell's RefreshAll re-read TEN pages, in
/// a row, on the UI thread — Genel bakış, Görüşmeler, Defter, Takvim, Yapılacaklar, Sözler, Aynam,
/// Kişiler and Durum's two tabs — and it was wired to three events that fire on every ruling
/// anybody makes anywhere: a call finishing, CallActions.Changed, LedgerActions.Changed. Ticking
/// one suggestion on Yapılacaklar rebuilt nine screens the user could not see.
///
/// The cost was not theoretical. Görüşmeler reads up to two thousand conversations into memory;
/// Sözler reads every promise in the ledger, a verdict query per conversation and the lines around
/// every quote; Kişiler reads every person. On a working archive — seventy-eight conversations,
/// ten people, a hundred and sixty-three open promises — one tick took long enough that the user
/// said the application's performance was very bad. It was.
///
/// So: re-read what is on screen, mark the rest, and spend the mark when the user arrives. The
/// mark is per page rather than per kind of change, deliberately. A table saying "a promise ruling
/// touches Sözler, Defter and Genel bakış" would be a second, hand-kept truth about what each page
/// reads, and it would go wrong silently the first time a page started reading something new — the
/// failure being a screen that quietly shows yesterday, which is the one failure this product
/// cannot afford. One bit per page cannot be wrong in that direction: the worst it can do is
/// re-read a page that did not need it.
///
/// Separate from <see cref="ShellViewModel"/> because that cannot be built in a test — it takes a
/// CallOrchestrator, which opens capture devices and a Python worker — and a rule about what does
/// and does not get re-read is exactly the kind of rule that has to be checked rather than read.
/// The shell hands it the one thing it cannot know, which is how to re-read a page.
/// </summary>
/// <param name="reload">Re-reads one page. The shell's own dispatch; called on the UI thread.</param>
public sealed class PageRefresh(Action<ShellPage> reload)
{
    /// <summary>The pages that have been changed underneath the user and not re-read since.</summary>
    private readonly HashSet<ShellPage> _stale = [];

    /// <summary>
    /// Every page the shell re-reads from the archive, in rail order.
    ///
    /// Arama and Sor are not here: nothing in the archive changes what they show until a question
    /// is asked, and both load themselves on arrival anyway. Durum stands for its own two tabs,
    /// İşlemler and Yapay zekâ, which are on screen exactly when it is.
    /// </summary>
    public static readonly ShellPage[] Reloadable =
    [
        ShellPage.Overview, ShellPage.Calls, ShellPage.Ledger, ShellPage.Calendar,
        ShellPage.Todo, ShellPage.Promises, ShellPage.Mirror, ShellPage.Contacts,
        ShellPage.Health,
    ];

    /// <summary>
    /// The five pages that re-read on arrival whether they were marked or not.
    ///
    /// Not an oversight. Things reach these without ever announcing themselves — a board card
    /// written from a call window, a reminder written in a dialog — and the unconditional re-read
    /// on arrival is what has been covering that since long before the mark existed. Dropping it
    /// to make the rule tidier would trade a slow screen for a wrong one.
    ///
    /// Genel bakış, Kişiler and Durum's tabs are the other side of it: they had no arrival
    /// re-read at all and relied entirely on the ten-page sweep, which is why the mark had to be
    /// invented in the first place.
    /// </summary>
    public static bool AlwaysOnArrival(ShellPage page) =>
        page is ShellPage.Calls or ShellPage.Calendar or ShellPage.Todo
            or ShellPage.Promises or ShellPage.Mirror;

    /// <summary>
    /// Something in the archive changed: mark every page, and re-read the one on screen.
    ///
    /// The visible page is re-read synchronously, on the UI thread, on purpose. The user has just
    /// acted on it — ticked a suggestion, refused a finding — and the row has to answer under
    /// their hand; deferring that would show them their own click not taking effect. Only one page
    /// is ever visible (MainWindow draws exactly one, through PageVisibility), so "visible" and
    /// "current" are the same thing here, with Durum's two tabs the single exception and handled
    /// together by the shell's dispatch.
    /// </summary>
    public void Everything(ShellPage current)
    {
        foreach (var page in Reloadable) _stale.Add(page);

        Arrive(current);
    }

    /// <summary>
    /// One page has news: re-read it if the user is looking at it, mark it if not.
    ///
    /// The recording path's way of saying "the first screen has changed" without paying for a
    /// screen nobody is looking at. Nothing a recorder or a worker does waits on this — what it
    /// skips is a redraw, and the page it skips is re-read the moment it is opened.
    /// </summary>
    public void Touch(ShellPage page, ShellPage current)
    {
        if (page == current) reload(page);
        else _stale.Add(page);
    }

    /// <summary>
    /// The user has landed on a page. Spends the mark, and re-reads if the mark or the page's own
    /// standing rule says to.
    ///
    /// The mark is spent either way, including for the five that would have re-read anyway: a page
    /// that has just been read is not waiting for anything.
    /// </summary>
    public void Arrive(ShellPage page)
    {
        var marked = _stale.Remove(page);

        if (marked || AlwaysOnArrival(page)) reload(page);
    }
}
