using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The same work, done once.
///
/// Six screens were paying twice for what they show: the promises page scanned whole transcripts
/// for offers nobody had asked to see, one click re-read the same page two and three times over,
/// the calls page asked the database for names it had just read, the contacts page read a
/// conversation's transcript once for the rows and again for the talk share, and the contact
/// window's timeline issued a query per conversation. None of it is visible — which is exactly
/// why it needs pinning: a screen that is merely slow looks like a screen that is fine, and the
/// next helpful loop puts the work straight back.
///
/// The measure is <see cref="Database.ConnectionsOpened"/>. Every repository call opens exactly
/// one connection and pays for its pragmas, so counting them is the honest unit of "how much did
/// that screen just ask for" — and, unlike a stopwatch, it fails the same way on every machine.
/// Where the question is "how many times did the page re-read itself" rather than "how much did
/// it read", the count is of the page's own <c>IsEmpty</c> notification, which every one of these
/// view models raises once per refresh and nowhere else.
/// </summary>
public sealed class RepeatedWorkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-tekrar-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Database _database;
    private readonly Repository _repo;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);

    public RepeatedWorkTests()
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

    // ---- the archive under test ------------------------------------------------------------

    private long Person(string name) => _repo.UpsertContact(name, CallApp.WhatsApp);

    private long Call(long contact, int daysAgo, params string[] lines)
    {
        var id = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddDays(-daysAgo),
            Duration = TimeSpan.FromMinutes(4),
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(id, contact);

        if (lines.Length > 0)
        {
            _repo.ReplaceSegments(id, lines.Select((text, i) => new Segment
            {
                CallId = id, IsMe = i % 2 == 0, StartMs = i * 4000, EndMs = i * 4000 + 3000, Text = text,
            }));
        }

        return id;
    }

    private long Promise(long call, long contact, string obligation, bool byMe = false, DateOnly? deadline = null) =>
        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, ByMe = byMe,
            Quote = $"{obligation} sözü", QuoteStartMs = 4000,
            Obligation = obligation, DeadlineDate = deadline,
        });

    // ---- the two rulers --------------------------------------------------------------------

    /// <summary>How many connections — that is, how many trips to the archive — a piece of work cost.</summary>
    private long Cost(Action work)
    {
        var before = _database.ConnectionsOpened;
        work();
        return _database.ConnectionsOpened - before;
    }

    /// <summary>
    /// How many times a page re-read itself while the work ran, counted by the one notification
    /// its refresh raises and nothing else does — "IsEmpty" on the promises and to-do pages,
    /// "HasAnything" on the ledger.
    ///
    /// Counted without subscribing to anything the rest of the suite can raise, so the number is
    /// the page's own doing and nothing else's.
    /// </summary>
    private static int Refreshes(
        System.ComponentModel.INotifyPropertyChanged page, Action work, string marker = "IsEmpty")
    {
        var count = 0;

        void Seen(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == marker) count++;
        }

        page.PropertyChanged += Seen;

        try
        {
            work();
        }
        finally
        {
            page.PropertyChanged -= Seen;
        }

        return count;
    }

    /// <summary>Runs a ruling with the pages wired the way the shell wires them.</summary>
    private static void WhileTheShellIsListening(Action refresh, Action ruling)
    {
        void OnChanged(object? sender, EventArgs e) => refresh();

        LedgerActions.Changed += OnChanged;
        CallActions.Changed += OnChanged;

        try
        {
            ruling();
        }
        finally
        {
            LedgerActions.Changed -= OnChanged;
            CallActions.Changed -= OnChanged;
        }
    }

    // ---- A: the "tutuldu mu?" offer --------------------------------------------------------

    /// <summary>
    /// Goes red when the promises page looks for a "tutuldu mu?" offer while it is building rows.
    ///
    /// One lookup reads the promise, the next five conversations with that person and every line
    /// of each of their transcripts, folding all of it through the archive-question tokeniser. The
    /// page builds a card for every promise the ledger holds — open, kept, refused, and the ones
    /// folded inside a group — and draws a handful of them. Paying for the scan while building
    /// meant paying it for cards behind a chip nobody had clicked, on every refresh, of a page the
    /// user was not necessarily looking at; and since the shell re-reads this page whenever
    /// anything anywhere changes, that scan ran on clicks that had nothing to do with promises.
    ///
    /// Red here means the offer is being computed eagerly again: the card would already hold it,
    /// so asking for it would cost nothing.
    /// </summary>
    [Fact]
    public void TheOfferIsLookedForOnlyWhenACardIsAskedForIt()
    {
        var contact = Person("Gürhan");
        var call = Call(contact, 8, "Alo", "Sözleşme taslağını cumaya yollarım");
        Promise(call, contact, "Sözleşme taslağını göndermek", deadline: _today.AddDays(-3));

        Call(contact, 1, "Merhaba abi", "Sözleşme taslağını dün akşam gönderdim sana");

        var page = new PromisesViewModel(_repo);
        page.Refresh();

        var card = Assert.Single(page.Theirs);

        // Building the rows did not go looking; asking does, exactly once.
        Assert.Equal(1, Cost(() => Assert.True(card.HasHint)));

        // And having looked, it does not look again — the card holds the answer for as long as
        // it lives, which is until the next refresh.
        Assert.Equal(0, Cost(() => Assert.NotNull(card.HintText)));
        Assert.Equal(0, Cost(() => Assert.NotNull(card.Hint)));
    }

    /// <summary>
    /// Goes red when the page scans transcripts for a card that can never show the offer.
    ///
    /// A moment the user has said is not a promise carries no "tutuldu mu?" line — the card's own
    /// HintText refuses to draw one — so it must not pay for the scan either. This is the case the
    /// archive actually holds: a conversational aside sitting in the ledger as a promise.
    /// </summary>
    [Fact]
    public void APromiseTheUserSaysIsNotOneIsNeverScannedFor()
    {
        var contact = Person("Uliana");
        var call = Call(contact, 6, "Sesim kötü geliyor", "Dur Whatsapp'tan ayırayım seni bekle");
        var promise = Promise(call, contact, "Whatsapp'tan ayırmak", byMe: true);

        Call(contact, 1, "Merhaba", "Whatsapp'tan ayırdım seni");

        var page = new PromisesViewModel(_repo);
        page.Refresh();

        // Before the ruling the question can be asked, so the lookup is paid for once.
        Assert.Equal(1, Cost(() => Assert.True(Assert.Single(page.Mine).HasHint)));

        var judged = _repo.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == promise);
        LedgerActions.JudgePromise(_repo, judged.Commitment, VerdictValue.NotThat);

        page.Filter = PromiseFilter.Dismissed;

        var refused = Assert.Single(page.Mine);
        Assert.True(refused.IsNotAPromise);

        // After it, the row is out of every promise count and out of the scan with them.
        Assert.Equal(0, Cost(() => Assert.False(refused.HasHint)));
    }

    // ---- B: one click, one refresh ---------------------------------------------------------

    /// <summary>
    /// Goes red when a verb on the promises page re-reads the promises page.
    ///
    /// It has already been re-read by then. Every verb here goes through LedgerActions, which
    /// writes the row and raises its own Changed, and the shell answers that by re-reading all ten
    /// pages — this one included — before the verb returns. The page then refreshed itself and
    /// announced the change a second time, which sent the shell round all ten pages again: one
    /// "Tutuldu" re-read this page three times and the other nine twice, each pass carrying the
    /// ledger query, a verdict query per conversation and (until the fix above) the transcript
    /// scan.
    /// </summary>
    [Fact]
    public void ARulingDoesNotReReadTheSozlerPageItself()
    {
        var contact = Person("Serdal");
        var call = Call(contact, 5, "Alo");
        Promise(call, contact, "Dilekçeyi iletmek");

        var page = new PromisesViewModel(_repo);
        page.Refresh();

        var card = Assert.Single(page.Theirs);

        Assert.Equal(0, Refreshes(page, () => page.DismissCommand.Execute(card)));
        Assert.NotNull(page.Notice);
        Assert.True(page.CanUndo);

        Assert.Equal(0, Refreshes(page, () => page.UndoCommand.Execute(null)));
        Assert.False(page.CanUndo);

        // The one refresh is the shell's, and after it the page says what it should.
        WhileTheShellIsListening(page.Refresh, () => page.DismissCommand.Execute(card));

        Assert.Empty(page.Theirs);
        Assert.Equal(1, page.DismissedCount);
    }

    /// <summary>
    /// Goes red when a verb on the ledger page re-reads the ledger page, and when select mode is
    /// left after the ruling rather than before it.
    ///
    /// The order is the whole point. The announcement is what re-reads the page, so anything the
    /// refresh has to see must be true before the write: leaving select mode afterwards meant the
    /// single refresh drew the page still in select mode and a second one had to be run to undo
    /// that — the whole ledger read twice for one click.
    /// </summary>
    [Fact]
    public void ARulingDoesNotReReadTheDefterPageItself()
    {
        var contact = Person("Bozkurt");
        var call = Call(contact, 4, "Alo");

        foreach (var quote in new[] { "bugün karar vermezsen başkasına vereceğim", "bunu kimseye söyleme" })
        {
            _repo.InsertFlag(new Flag
            {
                CallId = call, ContactId = contact, Kind = FlagKind.PressureTactic,
                Summary = "Baskı işareti", Quote = quote, QuoteStartMs = 9000, CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var page = new LedgerViewModel(_repo);
        page.Refresh();

        page.IsSelecting = true;
        foreach (var entry in page.Entries) entry.IsSelected = true;

        var selectingWhenRefreshed = new List<bool>();

        WhileTheShellIsListening(
            () => { selectingWhenRefreshed.Add(page.IsSelecting); page.Refresh(); },
            () => page.DismissSelectedCommand.Execute(null));

        // One refresh, and it saw select mode already left.
        Assert.Equal([false], selectingWhenRefreshed);
        Assert.False(page.IsSelecting);
        Assert.Equal(2, page.DismissedCount);

        Assert.Equal(0, Refreshes(page, () => page.UndoCommand.Execute(null), "HasAnything"));
    }

    /// <summary>
    /// Goes red when ticking a suggestion re-reads the to-do page itself.
    ///
    /// This one writes without going through LedgerActions, so it announces the change itself —
    /// and that announcement is what re-reads every page, this one among them. Doing both was one
    /// tick, two full reads of the list.
    /// </summary>
    [Fact]
    public void TickingASuggestionDoesNotReReadTheYapilacaklarPageItself()
    {
        var contact = Person("Samet");
        var call = Call(contact, 2, "Alo");

        _repo.InsertAction(new ActionItem { CallId = call, Action = "Siteyi bugün aç", Quote = "bunu konuşmuştuk" });

        var page = new TodoViewModel(_repo);
        page.Refresh();

        var row = page.Undated.Single();

        Assert.Equal(0, Refreshes(page, () => page.ToggleCommand.Execute(row)));

        // With the shell listening it is read once, and what it then shows is the new state.
        page.ShowDone = true;
        WhileTheShellIsListening(page.Refresh, () => page.ToggleCommand.Execute(page.Done.Single()));

        Assert.Single(page.Undated);
        Assert.Empty(page.Done);
    }

    /// <summary>
    /// And the shell really is the one listener the three tests above stand in for.
    ///
    /// <see cref="ShellViewModel"/> cannot be built here — it takes a CallOrchestrator, which
    /// opens capture devices and a Python worker — so the wiring is read out of the source, the
    /// way <see cref="SuggestionsOnTheTodoPageTests"/> reads it. Red means the pages have stopped
    /// re-reading themselves and nothing has taken over: a ruling written and three lists that do
    /// not move, which is worse than the double work this replaced.
    /// </summary>
    [Fact]
    public void TheShellIsTheOneThatReReadsThePages()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "VoiceTranscript.App", "ViewModels", "ShellViewModel.cs"));

        var ledger = source
            .Split('\n')
            .SingleOrDefault(line => line.Contains("LedgerActions.Changed", StringComparison.Ordinal));

        Assert.NotNull(ledger);
        Assert.Contains("RefreshAll", ledger, StringComparison.Ordinal);

        var start = source.IndexOf("public void RefreshAll()", StringComparison.Ordinal);
        var end = source.IndexOf("public void OpenContact(", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, "RefreshAll gövdesi bulunamadı.");

        var body = source[start..end];

        Assert.Contains("Promises.Refresh()", body, StringComparison.Ordinal);
        Assert.Contains("Ledger.Refresh()", body, StringComparison.Ordinal);
        Assert.Contains("Todo.Refresh()", body, StringComparison.Ordinal);
    }

    // ---- C: the calls page's names ---------------------------------------------------------

    /// <summary>
    /// Goes red when the calls page asks the database for names it has already read, and when the
    /// rows it builds from the list in hand are not the rows it built before.
    ///
    /// The cost of the page must not depend on how many people are in the archive: the contact
    /// filter above it is built from the whole contact list, and every name any row needs is
    /// already in that list.
    /// </summary>
    [Fact]
    public void TheCallsPageNamesItsRowsFromTheListItAlreadyRead()
    {
        var gurhan = Person("Gürhan");
        Call(gurhan, 3);
        Call(gurhan, 2);

        var uliana = Person("Uliana");
        Call(uliana, 1);

        var page = new CallsViewModel(_repo);

        var twoPeople = Cost(page.Refresh);

        var names = page.Groups.SelectMany(g => g.Calls).Select(r => r.ContactName).ToList();

        Assert.Equal(3, names.Count);
        Assert.Equal(2, names.Count(n => n == "Gürhan"));
        Assert.Equal(1, names.Count(n => n == "Uliana"));
        Assert.Equal(3, page.Total);

        // Three more people, each with a conversation of their own. Three more rows to name, and
        // not one more question asked.
        foreach (var name in new[] { "Serdal", "Samet", "Sinan" }) Call(Person(name), 4);

        var fivePeople = Cost(page.Refresh);

        Assert.Equal(twoPeople, fivePeople);
        Assert.Equal(6, page.Total);
        Assert.Contains("Sinan", page.Groups.SelectMany(g => g.Calls).Select(r => r.ContactName));
    }

    // ---- D: the contacts page's transcript -------------------------------------------------

    /// <summary>
    /// Goes red when the contacts page reads a conversation's transcript twice — once for the
    /// lines and once for the talk share — or when reading it once changes what the share says.
    ///
    /// Arrowing down the call list did this per row.
    /// </summary>
    [Fact]
    public void TheContactsPageReadsAConversationsTranscriptOnce()
    {
        var contact = Person("Mustafa");
        var call = Call(contact, 3, "Alo", "Merhaba", "Nasılsın");

        using var page = new ContactsViewModel(_repo);
        page.Refresh();
        page.Select(contact);

        var row = page.Calls.Single(c => c.Call.Id == call);

        // Selecting the person already opened the conversation, as the screen does; the
        // measurement wants the click itself, from nothing.
        page.SelectedCall = null;

        // The three reads this costs are the suggestions, the transcript and the summary. A
        // fourth is the transcript a second time.
        Assert.Equal(3, Cost(() => page.SelectedCall = row));

        Assert.Equal(3, page.Transcript.Count);

        // Two lines of mine against one of theirs, six seconds against three.
        Assert.Equal(2.0 / 3, page.TalkRatio, 3);
        Assert.NotNull(page.TalkSummary);
    }

    // ---- E and F: the contact window's flow ------------------------------------------------

    /// <summary>
    /// Goes red when the contact window's timeline asks per conversation rather than per person.
    ///
    /// The notes and the suggestions used to be fetched inside the loop — a connection and a
    /// pragma batch each, up to two hundred of them, to draw a screen that exists precisely
    /// because this person has a long history. Batched, the cost is the same six queries whether
    /// the person has three conversations or thirty.
    /// </summary>
    [Fact]
    public void TheContactFlowAsksPerPersonNotPerConversation()
    {
        var contact = Person("Bozkurt");

        for (var i = 1; i <= 3; i++)
        {
            var call = Call(contact, i, "Alo");
            _repo.SaveNote(call, $"{i}. görüşmenin notu");
            _repo.InsertAction(new ActionItem { CallId = call, Action = $"{i}. öneri", Quote = "bunu konuşmuştuk" });
        }

        var window = new ContactWindowViewModel(_repo, contact, _paths.Photos);

        var three = Cost(() => window.LoadMoreFlowCommand.Execute(null));

        for (var i = 4; i <= 8; i++)
        {
            var call = Call(contact, i, "Alo");
            _repo.SaveNote(call, $"{i}. görüşmenin notu");
            _repo.InsertAction(new ActionItem { CallId = call, Action = $"{i}. öneri", Quote = "bunu konuşmuştuk" });
        }

        var eight = Cost(() => window.LoadMoreFlowCommand.Execute(null));

        Assert.Equal(three, eight);

        // And it is still the same timeline: a conversation, its note and its suggestion, for
        // every one of the eight.
        Assert.Equal(8, window.Flow.Count(f => f.Kind == "gorusme"));
        Assert.Equal(8, window.Flow.Count(f => f.Kind == "not"));
        Assert.Equal(8, window.Flow.Count(f => f.Kind == "aksiyon"));

        Assert.Contains(window.Flow, f => f.Kind == "not" && f.Detail == "4. görüşmenin notu");
        Assert.Contains(window.Flow, f => f.Kind == "aksiyon" && f.Title == "Öneri: 4. öneri");

        // Newest first, as it always was.
        Assert.Equal(
            window.Flow.Select(f => f.When).OrderByDescending(w => w).ToList(),
            window.Flow.Select(f => f.When).ToList());
    }

    /// <summary>
    /// Goes red when the timeline is cut and does not say so.
    ///
    /// It read the newest two hundred conversations and stopped, with no line and no button, so
    /// for a long-standing contact the stream simply ended and everything before it — calls,
    /// notes, promises, findings — read as things that never happened. Silently truncated
    /// evidence is the failure this product exists to avoid; the Görüşmeler tab beside it had
    /// already solved the same cap the same way.
    /// </summary>
    [Fact]
    public void TheContactFlowSaysWhenItWasCut()
    {
        var contact = Person("Uliana");

        // Two hundred and one, so exactly one conversation falls off the first page.
        for (var i = 1; i <= 201; i++) Call(contact, i, "Alo");

        var oldest = _repo.ListCalls(contact, limit: 1000).OrderBy(c => c.StartedAt).First().Id;

        var window = new ContactWindowViewModel(_repo, contact, _paths.Photos);

        Assert.True(window.HasMoreFlow);
        Assert.Equal(200, window.Flow.Count(f => f.Kind == "gorusme"));
        Assert.DoesNotContain(window.Flow, f => f.CallId == oldest);

        Assert.NotEmpty(window.FlowCutLine);
        Assert.Contains("200", window.FlowCutLine, StringComparison.Ordinal);

        // And the way to the rest of it.
        window.LoadMoreFlowCommand.Execute(null);

        Assert.False(window.HasMoreFlow);
        Assert.Equal("", window.FlowCutLine);
        Assert.Equal(201, window.Flow.Count(f => f.Kind == "gorusme"));
        Assert.Contains(window.Flow, f => f.CallId == oldest);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
