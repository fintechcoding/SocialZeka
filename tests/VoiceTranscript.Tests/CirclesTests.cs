using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Circles: the user's own groups of people, and the tab strip over the first screen's list.
///
/// The user asked for this in their own words — "son görüşmelerde aile diye de ayrım olsa, bazı
/// kişileri aileye aktarsam, tablı olsa, aileyi seçince ayrı filtrelenmiş hâlini görsem". What
/// makes it safe rather than merely nice is a short list of promises, and each test below is one
/// of them:
///
///   * the strip narrows ONE section and lies about nothing else on the page;
///   * a tab shows its own newest twelve, asked of the database, not a sieve over a shared twelve;
///   * the counts are of the whole archive, before any filter;
///   * "Çevresiz" is always there and always holds whoever is in no circle;
///   * deleting a word never deletes what somebody filed under it.
/// </summary>
public sealed class CirclesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-cevre-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Database _database;
    private readonly Repository _repo;

    public CirclesTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        _database = new Database(_paths.DatabaseFile);
        _database.Migrate();
        _repo = new Repository(_database);
    }

    public void Dispose()
    {
        _database.ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private long Person(string name) => _repo.UpsertContact(name, CallApp.WhatsApp);

    private long Call(long? contact, int minutesAgo)
    {
        var id = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddMinutes(-minutesAgo),
            Duration = TimeSpan.FromMinutes(4),
            State = ProcessingState.Analysed,
        });

        if (contact is { } id2) _repo.AssignContact(id, id2);

        return id;
    }

    private OverviewViewModel Screen()
    {
        var settings = new AppSettings();
        var screen = new OverviewViewModel(_repo, () => settings, _paths);

        screen.Refresh();
        return screen;
    }

    private static CircleTab TabNamed(OverviewViewModel screen, string name) =>
        screen.Circles.Single(t => t.Name == name);

    // ---- the promise the whole design rests on ---------------------------------------------

    /// <summary>
    /// Goes red the moment the strip starts narrowing anything but the list of recent calls.
    ///
    /// The four figures at the top are one sentence about the ARCHIVE. A number that shrinks
    /// because a tab is selected tells the user their archive shrank, and that is not a display
    /// choice, it is a lie. The attention cards are worse still: one of their reasons — a
    /// recording nobody has named — belongs to no person and therefore to no circle, so filtering
    /// them would hide it for ever behind a tab it can never appear on. And a family promise is
    /// still overdue while the İş tab is showing.
    /// </summary>
    [Fact]
    public void TheStripNarrowsTheListAndNothingElseOnThePage()
    {
        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");

        var serdal = Person("Serdal");
        _repo.SetContactCircle(serdal, "İş");

        Call(uliana, 10);
        Call(serdal, 20);

        // Nobody named this one: it belongs to no person and so to no circle.
        Call(null, 30);

        var call = Call(uliana, 40);
        _repo.InsertCommitment(new Commitment
        {
            CallId = call,
            ContactId = uliana,
            Quote = "evrakı yarın yollarım",
            QuoteStartMs = 1000,
            Obligation = "Evrakı yollamak",
            DeadlineDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-3)),
        });

        var screen = Screen();

        var calls = screen.TotalCalls;
        var contacts = screen.TotalContacts;
        var recorded = screen.TotalRecorded;
        var pending = screen.PendingWork;
        var attention = screen.Attention.Select(a => a.Title).ToList();
        var overdue = screen.OverdueLine;

        Assert.Equal(4, calls);
        Assert.NotEmpty(attention);

        foreach (var tab in screen.Circles.ToList())
        {
            screen.SelectCircleCommand.Execute(tab);

            // And again after a refresh with that tab open — the case a call arriving while
            // somebody reads the Aile tab would put the page through.
            screen.Refresh();

            Assert.Equal(calls, screen.TotalCalls);
            Assert.Equal(contacts, screen.TotalContacts);
            Assert.Equal(recorded, screen.TotalRecorded);
            Assert.Equal(pending, screen.PendingWork);
            Assert.Equal(attention, screen.Attention.Select(a => a.Title));
            Assert.Equal(overdue, screen.OverdueLine);
        }
    }

    /// <summary>
    /// Goes red when a tab filters the twelve rows the screen already had instead of asking the
    /// database for its own.
    ///
    /// This is the failure that would have killed the design. The overview asks for twelve rows
    /// and the cut happens BEFORE any filter: sieve those twelve and the "Aile" tab of an archive
    /// holding twenty family conversations shows the handful that happen to be in the shared
    /// twelve, and the user says their family calls disappeared. Red here means the circle has
    /// stopped travelling into the query.
    /// </summary>
    [Fact]
    public void EachTabAsksTheDatabaseForItsOwnNewestTwelve()
    {
        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");

        var serdal = Person("Serdal");
        _repo.SetContactCircle(serdal, "İş");

        // Twenty family conversations, all older than the work ones — so the shared newest twelve
        // is entirely work, and an in-memory filter would leave the Aile tab empty.
        for (var i = 0; i < 20; i++) Call(uliana, 100 + i);
        for (var i = 0; i < 12; i++) Call(serdal, i + 1);

        var screen = Screen();

        Assert.Equal(12, screen.Recent.Count);
        Assert.All(screen.Recent, row => Assert.Equal("Serdal", row.ContactName));

        screen.SelectCircleCommand.Execute(TabNamed(screen, "Aile"));

        Assert.Equal(12, screen.Recent.Count);
        Assert.All(screen.Recent, row => Assert.Equal("Uliana", row.ContactName));
        Assert.False(screen.RecentIsEmpty);
    }

    /// <summary>
    /// Goes red when a tab counts what it is showing instead of what the archive holds.
    ///
    /// "Aile 20" beside a list of twelve is the point: the number answers "how much of this do I
    /// have", which is not a question the visible rows can answer. The ledger and the promises
    /// page have counted this way from the beginning.
    ///
    /// Also red when the buckets stop adding up. Every conversation is counted in exactly one
    /// tab, so the tabs always sum to the total — the arithmetic that makes it impossible for a
    /// call to be reachable from no tab at all.
    /// </summary>
    [Fact]
    public void TheTabCountsComeFromTheWholeArchiveAndAddUpToIt()
    {
        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");

        var serdal = Person("Serdal");
        _repo.SetContactCircle(serdal, "İş");

        var mustafa = Person("Mustafa");

        for (var i = 0; i < 20; i++) Call(uliana, 100 + i);
        for (var i = 0; i < 3; i++) Call(serdal, i + 1);
        Call(mustafa, 50);
        Call(null, 60);

        var screen = Screen();

        Assert.Equal(20, TabNamed(screen, "Aile").Count);
        Assert.Equal(3, TabNamed(screen, "İş").Count);

        // Mustafa is in no circle, and so is the conversation nobody has named.
        Assert.Equal(2, screen.Circles.Single(t => t.Kind == CircleTabKind.Uncircled).Count);

        var all = screen.Circles.Single(t => t.Kind == CircleTabKind.All);

        Assert.Equal(25, all.Count);
        Assert.Equal(screen.TotalCalls, all.Count);
        Assert.Equal(all.Count, screen.Circles.Where(t => t.Kind != CircleTabKind.All).Sum(t => t.Count));

        // And the count does not follow the list: the Aile tab still says twenty over twelve rows.
        screen.SelectCircleCommand.Execute(TabNamed(screen, "Aile"));

        Assert.Equal(12, screen.Recent.Count);
        Assert.Equal(20, TabNamed(screen, "Aile").Count);
    }

    /// <summary>
    /// Goes red when "Çevresiz" can be taken off the strip, or when somebody in no circle cannot
    /// be found under it.
    ///
    /// A person recorded five minutes ago is in no circle, because nobody has filed them yet.
    /// If the tab holding them could be removed — by deleting every circle, by there being none
    /// yet — their conversations would be reachable from no tab, which is the one thing this
    /// screen may never do.
    /// </summary>
    [Fact]
    public void ThereIsAlwaysAnUncircledTabAndItHoldsWhoeverIsInNoCircle()
    {
        var screen = Screen();

        // An archive with no circles at all still has both fixed tabs.
        Assert.Equal(CircleTabKind.All, screen.Circles[0].Kind);
        Assert.Equal(CircleTabKind.Uncircled, screen.Circles[^1].Kind);

        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");
        Call(uliana, 10);

        var newcomer = Person("Sinan");
        Call(newcomer, 5);

        screen.Refresh();
        screen.SelectCircleCommand.Execute(screen.Circles.Single(t => t.Kind == CircleTabKind.Uncircled));

        Assert.Equal("Sinan", Assert.Single(screen.Recent).ContactName);

        // Every circle deleted, and the tab is still there with everybody in it.
        foreach (var circle in _repo.Circles()) _repo.DeleteCircle(circle.Name);

        screen.Refresh();

        Assert.Equal(2, screen.Circles.Count);
        Assert.Equal(CircleTabKind.Uncircled, screen.Circles[^1].Kind);
        Assert.Equal(2, screen.Circles.Single(t => t.Kind == CircleTabKind.Uncircled).Count);
    }

    /// <summary>
    /// Goes red when a circle stops folding Turkish case the way the tag dictionary does.
    ///
    /// "İş", "iş" and "IŞ" are one word, and a user who types the second one after filing under
    /// the first must not end up with two circles that look identical on screen. The identity is
    /// the folded spelling, exactly as <c>tag_def</c> has always done it, which is also what
    /// makes a circle the same circle on two computers.
    /// </summary>
    [Fact]
    public void ACircleFoldsTurkishCaseTheWayTheTagDictionaryDoes()
    {
        _repo.SaveCircle(new Circle("İş", "Briefcase24", "#0078D4"));
        _repo.SaveCircle(new Circle("iş", "Briefcase24", "#0078D4"));

        // One row, not two: the second write found the first.
        Assert.Equal("iş", Assert.Single(_repo.Circles()).Name);

        var serdal = Person("Serdal");
        _repo.SetContactCircle(serdal, "IŞ");

        Assert.Equal("iş", _repo.CirclesByContact()[serdal].Name);

        Call(serdal, 5);

        var counts = _repo.CallCountsByCircle();

        Assert.Equal(1, counts.ByCircle["is"]);
        Assert.Equal(0, counts.Uncircled);
    }

    /// <summary>
    /// Goes red when deleting a circle deletes what the user filed under it.
    ///
    /// The assignment is the user's own data and the word is only its label — the same
    /// relationship a tagging has to a tag definition. Somebody who deletes "Aile" has deleted a
    /// label, not an evening's filing, and writing the word again brings every one of them back.
    ///
    /// While the word is gone those people read as being in no circle, everywhere: on the strip,
    /// in the counts and on the row. One rule, so two screens cannot disagree about where
    /// somebody is.
    /// </summary>
    [Fact]
    public void DeletingACircleDefinitionKeepsEveryAssignment()
    {
        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");
        Call(uliana, 10);

        _repo.DeleteCircle("Aile");

        // The column still says where the user put them.
        Assert.Equal("aile", _repo.CircleOf(uliana));

        // And with no definition to point at, they read as being in no circle.
        Assert.Empty(_repo.CirclesByContact());

        var counts = _repo.CallCountsByCircle();

        Assert.Empty(counts.ByCircle);
        Assert.Equal(1, counts.Uncircled);

        var screen = Screen();

        screen.SelectCircleCommand.Execute(screen.Circles.Single(t => t.Kind == CircleTabKind.Uncircled));
        Assert.Equal("Uliana", Assert.Single(screen.Recent).ContactName);

        // The word written again, and everybody is back in it.
        _repo.SaveCircle(new Circle("Aile", "Home24", "#8764B8"));

        Assert.Equal(1, _repo.CallCountsByCircle().ByCircle["aile"]);
    }

    /// <summary>
    /// Goes red when renaming a circle leaves its people behind.
    ///
    /// A circle's identity is its folded spelling, so "Aile" → "Ailem" is a new key: written
    /// naively, the tab that said 20 would say 0 and the user — who changed one letter — would
    /// have to file twenty people again.
    /// </summary>
    [Fact]
    public void RenamingACircleTakesItsPeopleWithIt()
    {
        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");
        Call(uliana, 10);

        _repo.RenameCircle("aile", new Circle("Ailem", "Home24", "#8764B8"));

        Assert.Equal("Ailem", _repo.CirclesByContact()[uliana].Name);
        Assert.Equal(1, _repo.CallCountsByCircle().ByCircle["ailem"]);
        Assert.DoesNotContain(_repo.Circles(), c => c.Name == "Aile");
    }

    /// <summary>
    /// Goes red when the first screen remembers which tab was open last time.
    ///
    /// Deliberately forgotten. A remembered selection would leave last night's family call behind
    /// a closed door: the user opens the application to a screen that looks complete and is not.
    /// The cost of forgetting is one click; the cost of remembering is a conversation nobody sees.
    /// </summary>
    [Fact]
    public void EveryLaunchOpensOnHepsi()
    {
        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");
        Call(uliana, 10);

        var first = Screen();
        first.SelectCircleCommand.Execute(TabNamed(first, "Aile"));

        Assert.Equal(CircleTabKind.Circle, first.SelectedCircle?.Kind);

        // Within the session the choice survives a refresh — a call arriving while somebody reads
        // the Aile tab must not throw them back to Hepsi.
        first.Refresh();
        Assert.Equal(CircleTabKind.Circle, first.SelectedCircle?.Kind);

        // A new screen is a new launch, and it starts where every launch starts.
        Assert.Equal(CircleTabKind.All, Screen().SelectedCircle?.Kind);
    }

    /// <summary>
    /// Goes red when the strip stops turning into a dropdown past four circles.
    ///
    /// Written before it happens rather than after: seven pills wrap onto a second line, and a
    /// wrapped tab strip stops reading as tabs. Two tabs — Hepsi and Çevresiz with nothing
    /// between them — is not a choice at all, so the strip is not drawn.
    /// </summary>
    [Fact]
    public void PastFourCirclesTheStripBecomesADropdown()
    {
        var screen = Screen();

        Assert.False(screen.HasCircleStrip);

        foreach (var name in new[] { "Aile", "İş", "Komşular", "Okul" })
            _repo.SaveCircle(new Circle(name, "Home24", "#8764B8"));

        screen.Refresh();

        Assert.True(screen.CirclesAreAStrip);
        Assert.False(screen.CirclesAreADropdown);

        _repo.SaveCircle(new Circle("Kulüp", "Home24", "#107C10"));
        screen.Refresh();

        Assert.True(screen.CirclesAreADropdown);
        Assert.False(screen.CirclesAreAStrip);
    }

    /// <summary>
    /// Goes red when the Görüşmeler page's dropdown and the first screen's strip disagree about
    /// where somebody is.
    ///
    /// The two narrow in different layers on purpose — in SQL there, in memory here, because this
    /// page already holds its rows and its search box has to keep beating every filter. Two
    /// layers is this design's own risk, so the answers are compared here directly.
    /// </summary>
    [Fact]
    public void TheCallsPageNarrowsToTheSameRowsTheStripDoes()
    {
        _repo.SeedDefaultCircles();

        var uliana = Person("Uliana");
        _repo.SetContactCircle(uliana, "Aile");

        var serdal = Person("Serdal");
        _repo.SetContactCircle(serdal, "İş");

        var mustafa = Person("Mustafa");

        Call(uliana, 10);
        Call(uliana, 11);
        Call(serdal, 12);
        Call(mustafa, 13);

        var page = new CallsViewModel(_repo);
        page.Refresh();

        page.CircleChoice = page.CircleChoices.Single(c => c.Name == "Aile");
        Assert.Equal(2, page.Count);
        Assert.All(page.Groups.SelectMany(g => g.Calls), row => Assert.Equal("Uliana", row.ContactName));

        page.CircleChoice = page.CircleChoices.Single(c => c.Kind == CircleTabKind.Uncircled);
        Assert.Equal(1, page.Count);

        page.CircleChoice = page.CircleChoices.Single(c => c.Kind == CircleTabKind.All);
        Assert.Equal(4, page.Count);

        // The archive's own total never moves with the filter.
        Assert.Equal(4, page.Total);
    }
}
