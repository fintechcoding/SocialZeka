using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Refresh the screen the user is on; mark the rest; catch up when they arrive.
///
/// What used to happen: one ruling — a suggestion ticked, a finding refused — re-read TEN pages
/// in a row on the UI thread, of which the user was looking at one. Görüşmeler alone reads up to
/// two thousand conversations into memory; Sözler reads every promise, a verdict query per
/// conversation and the lines around every quote; and the two rail badges were harvested off the
/// rebuilt Defter and Sözler pages, which is why both had to be rebuilt whatever the user was
/// doing. On a working archive that made ticking a checkbox slow enough to complain about.
///
/// Three things could break silently while making it fast, and this file is here for those three:
/// a badge that stops counting, a page that arrives showing yesterday, and a recording that waits
/// on a redraw. Nothing here is about speed — it is about what may be skipped.
///
/// <see cref="ShellViewModel"/> cannot be built in a test: it takes a CallOrchestrator, which
/// opens capture devices and a Python worker. So the rule itself lives in
/// <see cref="PageRefresh"/>, which can be, and <see cref="Shell"/> below wires it to the real
/// pages and the real archive the way the window does. The handful of things that can only be
/// read out of the source say so in their own doc comments.
/// </summary>
public sealed class ShellRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-tazeleme-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Database _database;
    private readonly Repository _repo;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);

    public ShellRefreshTests()
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

    // ---- the shell, minus the half that cannot be built here --------------------------------

    /// <summary>
    /// The window's refresh wiring, with the real pages and the real archive behind it.
    ///
    /// It does exactly what <see cref="ShellViewModel"/> does and nothing more: RefreshAll hands
    /// the current page to <see cref="PageRefresh"/> and counts the two badges; arriving at a page
    /// calls Arrive; the reload dispatch names the same view models. The pages it does not build —
    /// Genel bakış, Takvim, Aynam and Durum's two tabs, which need settings, paths or an HTTP
    /// client — still pass through the dispatch and are recorded, so "which pages were re-read"
    /// is answered for all of them.
    /// </summary>
    private sealed class Shell : IDisposable
    {
        private readonly Repository _repository;

        public Shell(Repository repository, ShellPage page)
        {
            _repository = repository;

            Calls = new CallsViewModel(repository);
            Ledger = new LedgerViewModel(repository);
            Todo = new TodoViewModel(repository);
            Promises = new PromisesViewModel(repository);
            Contacts = new ContactsViewModel(repository);

            Refresh = new PageRefresh(Reload);
            Page = page;
        }

        public CallsViewModel Calls { get; }
        public LedgerViewModel Ledger { get; }
        public TodoViewModel Todo { get; }
        public PromisesViewModel Promises { get; }
        public ContactsViewModel Contacts { get; }

        public PageRefresh Refresh { get; }

        public ShellPage Page { get; private set; }

        /// <summary>Every page the shell re-read, in the order it re-read them.</summary>
        public List<ShellPage> Reloaded { get; } = [];

        public int OpenFlagCount { get; private set; }
        public int OverduePromiseCount { get; private set; }

        private void Reload(ShellPage page)
        {
            Reloaded.Add(page);

            switch (page)
            {
                case ShellPage.Calls: Calls.Refresh(); break;
                case ShellPage.Ledger: Ledger.Refresh(); break;
                case ShellPage.Todo: Todo.Refresh(); break;
                case ShellPage.Promises: Promises.Refresh(); break;
                case ShellPage.Contacts: Contacts.Refresh(); break;
            }
        }

        /// <summary>ShellViewModel.RefreshAll.</summary>
        public void RefreshAll()
        {
            Refresh.Everything(Page);
            RefreshBadges();
        }

        /// <summary>ShellViewModel.RefreshBadges.</summary>
        public void RefreshBadges()
        {
            OpenFlagCount = _repository.OpenFlagCount();
            OverduePromiseCount = _repository.OverduePromiseCount(DateOnly.FromDateTime(DateTime.Today));
        }

        /// <summary>Any of the ways Page is set: ShellViewModel.OnPageChanged calls Arrive.</summary>
        public void GoTo(ShellPage page)
        {
            Page = page;
            Refresh.Arrive(page);
        }

        /// <summary>Runs a ruling with the shell listening the way the window listens.</summary>
        public void While(Action ruling)
        {
            void OnChanged(object? sender, EventArgs e) => RefreshAll();

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

        public void Dispose() => Contacts.Dispose();
    }

    // ---- the archive under test --------------------------------------------------------------

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

    private long Promise(
        long call, long contact, string obligation,
        DateOnly? deadline = null, bool conditional = false)
        => _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, ByMe = false,
            Quote = $"{obligation} sözü", QuoteStartMs = 4000,
            Obligation = obligation, DeadlineDate = deadline, IsConditional = conditional,
        });

    private long Finding(long call, long contact, string quote) => _repo.InsertFlag(new Flag
    {
        CallId = call, ContactId = contact, Kind = FlagKind.PressureTactic,
        Summary = "Baskı işareti", Quote = quote, QuoteStartMs = 9000, CreatedAt = DateTimeOffset.UtcNow,
    });

    /// <summary>How many trips to the archive a piece of work cost. See RepeatedWorkTests.</summary>
    private long Cost(Action work)
    {
        var before = _database.ConnectionsOpened;
        work();
        return _database.ConnectionsOpened - before;
    }

    /// <summary>
    /// How many times a page re-read itself, counted by the one notification its refresh raises
    /// and nothing else does. Counted without subscribing to anything the rest of the suite can
    /// raise, so the number is the page's own doing.
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

    // ---- A: one ruling, one page ------------------------------------------------------------

    /// <summary>
    /// Goes red when a ruling made on one page rebuilds the others.
    ///
    /// Behavioural. This is the complaint itself: ticking a suggestion on Yapılacaklar used to
    /// re-read Genel bakış, Görüşmeler, Defter, Takvim, Sözler, Aynam, Kişiler and Durum's two
    /// tabs as well — nine screens the user could not see, on the thread that was supposed to be
    /// moving the checkbox. Red means the sweep is back.
    /// </summary>
    [Fact]
    public void ARulingReReadsThePageItWasMadeOnAndNoOther()
    {
        var contact = Person("Samet");
        var call = Call(contact, 2, "Alo");

        _repo.InsertAction(new ActionItem { CallId = call, Action = "Siteyi bugün aç", Quote = "bunu konuşmuştuk" });
        Promise(call, contact, "Dilekçeyi iletmek", deadline: _today.AddDays(-3));
        Finding(call, contact, "bugün karar vermezsen başkasına vereceğim");

        using var shell = new Shell(_repo, ShellPage.Todo);
        shell.Todo.Refresh();
        shell.Promises.Refresh();

        var row = shell.Todo.Undated.Single();

        // The Sözler page is asked whether it re-read itself, independently of the shell's own
        // record of what it dispatched.
        var sozler = Refreshes(shell.Promises, () => shell.While(() => shell.Todo.ToggleCommand.Execute(row)));

        Assert.Equal([ShellPage.Todo], shell.Reloaded);
        Assert.Equal(0, sozler);

        // And the page that was re-read shows the new state, which is the whole point of doing it
        // synchronously while the user's finger is still on the row.
        Assert.Empty(shell.Todo.Undated);
    }

    /// <summary>
    /// Goes red when the page the user is actually on is left showing the old row.
    ///
    /// Behavioural, and the other half of the test above: the saving must not come out of the one
    /// screen that has to answer. A ruling made on Defter re-reads Defter, now, before the verb
    /// returns.
    /// </summary>
    [Fact]
    public void TheRulingsOwnPageIsReReadBeforeTheVerbReturns()
    {
        var contact = Person("Bozkurt");
        var call = Call(contact, 4, "Alo");

        Finding(call, contact, "bugün karar vermezsen başkasına vereceğim");
        Finding(call, contact, "bunu kimseye söyleme");

        using var shell = new Shell(_repo, ShellPage.Ledger);
        shell.Ledger.Refresh();

        var entry = shell.Ledger.Entries.First();

        shell.While(() => shell.Ledger.DismissCommand.Execute(entry));

        Assert.Equal([ShellPage.Ledger], shell.Reloaded);
        Assert.Single(shell.Ledger.Entries);
        Assert.Equal(1, shell.Ledger.DismissedCount);
    }

    // ---- B: arriving ------------------------------------------------------------------------

    /// <summary>
    /// Goes red when a page changed while it was hidden shows yesterday when it is opened.
    ///
    /// Behavioural, and the failure this whole change is gambling against. Defter is one of the
    /// pages with no unconditional re-read on arrival — before the mark existed it relied entirely
    /// on the ten-page sweep — so if the mark is not set, or not spent on arrival, the user
    /// refuses a finding on one screen and finds it still sitting there on another.
    /// </summary>
    [Fact]
    public void APageChangedWhileHiddenIsReReadWhenTheUserArrivesAtIt()
    {
        var contact = Person("Serdal");
        var call = Call(contact, 5, "Alo");

        var finding = Finding(call, contact, "bugün karar vermezsen başkasına vereceğim");
        _repo.InsertAction(new ActionItem { CallId = call, Action = "Dosyayı gönder", Quote = "bunu konuşmuştuk" });

        using var shell = new Shell(_repo, ShellPage.Todo);
        shell.Todo.Refresh();
        shell.Ledger.Refresh();

        Assert.Single(shell.Ledger.Entries);

        // The finding is refused from a call window while the user stands on Yapılacaklar.
        var flag = _repo.RecentFlags(limit: 10).Single(f => f.Flag.Id == finding).Flag;
        shell.While(() => LedgerActions.Dismiss(_repo, flag));

        // Defter was not rebuilt then...
        Assert.Equal([ShellPage.Todo], shell.Reloaded);
        Assert.Single(shell.Ledger.Entries);

        // ...and is rebuilt the moment it is opened.
        shell.GoTo(ShellPage.Ledger);

        Assert.Equal([ShellPage.Todo, ShellPage.Ledger], shell.Reloaded);
        Assert.Empty(shell.Ledger.Entries);
    }

    /// <summary>
    /// Goes red when arriving at a page does not spend its mark.
    ///
    /// Behavioural. A mark that is never cleared turns every rail click into a full re-read of
    /// whatever it lands on, which is the old cost paid one page at a time — and it hides the bug
    /// above, because a page that always re-reads can never be caught showing yesterday.
    /// </summary>
    [Fact]
    public void ArrivingSpendsTheMarkSoTheSecondVisitCostsNothing()
    {
        var contact = Person("Uliana");
        var call = Call(contact, 3, "Alo");

        Finding(call, contact, "bunu kimseye söyleme");
        _repo.InsertAction(new ActionItem { CallId = call, Action = "Dosyayı gönder", Quote = "bunu konuşmuştuk" });

        using var shell = new Shell(_repo, ShellPage.Todo);
        shell.Todo.Refresh();

        shell.While(() => shell.Todo.ToggleCommand.Execute(shell.Todo.Undated.Single()));
        shell.Reloaded.Clear();

        shell.GoTo(ShellPage.Ledger);
        shell.GoTo(ShellPage.Contacts);
        shell.GoTo(ShellPage.Ledger);
        shell.GoTo(ShellPage.Contacts);

        // Each of the two was re-read on its first visit and left alone on its second.
        Assert.Equal([ShellPage.Ledger, ShellPage.Contacts], shell.Reloaded);
    }

    /// <summary>
    /// Goes red when the five pages that re-read on every arrival stop doing so.
    ///
    /// Behavioural, and deliberately the opposite of the test above. Things reach Görüşmeler,
    /// Takvim, Yapılacaklar, Sözler and Aynam without ever announcing themselves — a board card
    /// written from a call window, a reminder written in a dialog — and this unconditional re-read
    /// on arrival has been what covers that since long before the mark existed. Tidying it away in
    /// the name of one consistent rule would trade a slow screen for a wrong one.
    /// </summary>
    [Fact]
    public void ThePagesNobodyAnnouncesToStillReReadOnEveryArrival()
    {
        using var shell = new Shell(_repo, ShellPage.Overview);

        foreach (var page in new[]
                 {
                     ShellPage.Calls, ShellPage.Calendar, ShellPage.Todo,
                     ShellPage.Promises, ShellPage.Mirror,
                 })
        {
            shell.Reloaded.Clear();

            shell.GoTo(page);
            shell.GoTo(page);

            Assert.Equal([page, page], shell.Reloaded);
        }
    }

    /// <summary>
    /// Goes red when a page nothing changed is re-read anyway, or a page something changed is not.
    ///
    /// Behavioural. RefreshAll marks every page the shell re-reads and no others: Arama and Sor
    /// are not in the set, because nothing in the archive changes what they show until a question
    /// is asked, and both load themselves on arrival regardless.
    /// </summary>
    [Fact]
    public void RefreshAllMarksEveryPageTheShellReReadsAndNoOthers()
    {
        using var shell = new Shell(_repo, ShellPage.Overview);

        shell.RefreshAll();
        shell.Reloaded.Clear();

        foreach (var page in Enum.GetValues<ShellPage>()) shell.GoTo(page);

        // Read only the pages whose re-read can only have come from a mark — the other five would
        // have re-read on arrival anyway, and prove nothing here. Genel bakış is missing from both
        // sides because RefreshAll re-read it there and then: it was the page on screen.
        Assert.Equal(
            PageRefresh.Reloadable
                .Where(p => p != ShellPage.Overview && !PageRefresh.AlwaysOnArrival(p))
                .OrderBy(p => p),
            shell.Reloaded.Where(p => !PageRefresh.AlwaysOnArrival(p)).OrderBy(p => p));

        Assert.DoesNotContain(ShellPage.Search, shell.Reloaded);
        Assert.DoesNotContain(ShellPage.Ask, shell.Reloaded);
    }

    // ---- C: the two rail badges --------------------------------------------------------------

    /// <summary>
    /// Goes red when either rail badge disagrees with the page it is a badge for.
    ///
    /// Behavioural, and the reason both counts had to be rewritten rather than merely moved. They
    /// used to be <c>Ledger.FlagCount</c> and <c>Promises.OverdueCount</c> — read off two pages
    /// that had just been rebuilt in full, which is precisely why every ruling anywhere rebuilt
    /// them. Counting them separately is only safe if the two counts mean the same thing, and the
    /// conditions are fiddly: a promise that is conditional, refused, undated, postponed into the
    /// future, or one the user has said is not a promise at all is on neither side.
    ///
    /// Red means the badge and the page are now telling the user two different numbers.
    /// </summary>
    [Fact]
    public void TheBadgesAgreeWithThePagesTheyUsedToBeTakenFrom()
    {
        var contact = Person("Gürhan");
        var call = Call(contact, 9, "Alo", "Söz veriyorum");

        var stale = _today.AddDays(-5);

        Promise(call, contact, "Sözleşmeyi göndermek", deadline: stale);
        Promise(call, contact, "Parayı yatırmak", deadline: stale, conditional: true);
        Promise(call, contact, "İleride bakmak", deadline: _today.AddDays(9));
        Promise(call, contact, "Bir ara uğramak");

        var refused = Promise(call, contact, "Reddedilen iş", deadline: stale);
        var notAPromise = Promise(call, contact, "Söz olmayan söz", deadline: stale);
        var postponed = Promise(call, contact, "Ertelenen iş", deadline: stale);

        Finding(call, contact, "bugün karar vermezsen başkasına vereceğim");
        Finding(call, contact, "bunu kimseye söyleme");
        var dismissedFinding = Finding(call, contact, "kimseye anlatma bunu");

        var ledger = _repo.PromiseLedger(includeClosed: true);

        LedgerActions.Dismiss(_repo, ledger.Single(r => r.Commitment.Id == refused).Commitment);
        LedgerActions.JudgePromise(
            _repo, ledger.Single(r => r.Commitment.Id == notAPromise).Commitment, VerdictValue.NotThat);
        LedgerActions.SetUserDeadline(
            _repo, ledger.Single(r => r.Commitment.Id == postponed).Commitment, _today.AddDays(4));
        LedgerActions.Dismiss(_repo, _repo.RecentFlags(limit: 10).Single(f => f.Flag.Id == dismissedFinding).Flag);

        using var shell = new Shell(_repo, ShellPage.Todo);
        shell.Ledger.Refresh();
        shell.Promises.Refresh();
        shell.RefreshBadges();

        // Only the first promise is past its date and still a promise.
        Assert.Equal(1, shell.OverduePromiseCount);
        Assert.Equal(2, shell.OpenFlagCount);

        Assert.Equal(shell.Promises.OverdueCount, shell.OverduePromiseCount);
        Assert.Equal(shell.Ledger.FlagCount, shell.OpenFlagCount);
    }

    /// <summary>
    /// Goes red when a badge goes stale because the ruling was made somewhere else.
    ///
    /// Behavioural. Both badges sit on the rail, which is on screen on every page, so they cannot
    /// be allowed to wait for Defter or Sözler to be opened: a Defter badge still saying 2 after
    /// the second finding was refused is the rail lying about the one thing it is there to say.
    /// This is the case the change actually creates — the ruling is made on Yapılacaklar, and
    /// neither of the two pages behind the numbers is re-read at all.
    /// </summary>
    [Fact]
    public void BothBadgesAreRightAfterARulingMadeOnNeitherOfTheirPages()
    {
        var contact = Person("Mustafa");
        var call = Call(contact, 6, "Alo");

        var finding = Finding(call, contact, "bugün karar vermezsen başkasına vereceğim");
        Finding(call, contact, "bunu kimseye söyleme");

        var promise = Promise(call, contact, "Dosyayı göndermek", deadline: _today.AddDays(-2));
        Promise(call, contact, "Parayı yollamak", deadline: _today.AddDays(-1));

        using var shell = new Shell(_repo, ShellPage.Todo);
        shell.RefreshAll();

        Assert.Equal(2, shell.OpenFlagCount);
        Assert.Equal(2, shell.OverduePromiseCount);

        var flag = _repo.RecentFlags(limit: 10).Single(f => f.Flag.Id == finding).Flag;
        var commitment = _repo.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == promise).Commitment;

        shell.While(() => LedgerActions.Dismiss(_repo, flag));
        shell.While(() => LedgerActions.Fulfil(_repo, commitment));

        // Neither Defter nor Sözler was re-read; both numbers moved anyway.
        Assert.DoesNotContain(ShellPage.Ledger, shell.Reloaded);
        Assert.DoesNotContain(ShellPage.Promises, shell.Reloaded);

        Assert.Equal(1, shell.OpenFlagCount);
        Assert.Equal(1, shell.OverduePromiseCount);
    }

    /// <summary>
    /// Goes red when the badges start costing what a page costs.
    ///
    /// Behavioural. The whole reason for counting them separately is that they are counted on
    /// every change, whichever page is showing — so if the counts ever grow into page reads the
    /// change has bought nothing and the ten-page sweep is back under another name. Two counts,
    /// two trips to the archive, whatever the archive holds.
    /// </summary>
    [Fact]
    public void TheBadgesCostTwoQueriesRatherThanTwoPages()
    {
        var contact = Person("Sinan");

        for (var i = 1; i <= 12; i++)
        {
            var call = Call(contact, i, "Alo", "Söz veriyorum");

            Finding(call, contact, $"{i}. baskı cümlesi");
            Promise(call, contact, $"{i}. iş", deadline: _today.AddDays(-i));
        }

        using var shell = new Shell(_repo, ShellPage.Todo);

        Assert.Equal(2, Cost(shell.RefreshBadges));

        Assert.Equal(12, shell.OpenFlagCount);
        Assert.Equal(12, shell.OverduePromiseCount);
    }

    // ---- D: the recording path ----------------------------------------------------------------

    /// <summary>
    /// Goes red when a call finishing stops reaching the first screen.
    ///
    /// Behavioural. The recorder reports twice on its way through a conversation — once when the
    /// worker picks it up, once when it is done — and the first screen's row has to move from
    /// "Sırada" to "Yazıya dökülüyor" to "işlendi". Nothing about that may wait on a redraw, and
    /// nothing about it may be dropped: the page is re-read at once when it is the one on screen,
    /// and marked when it is not, so that opening it shows the finished call rather than the queue
    /// as it stood an hour ago.
    /// </summary>
    [Fact]
    public void ACallFinishingReachesTheFirstScreenWhetherOrNotItIsShowing()
    {
        using var elsewhere = new Shell(_repo, ShellPage.Todo);

        elsewhere.Refresh.Touch(ShellPage.Overview, elsewhere.Page);

        // Nothing rebuilt behind the user's back...
        Assert.Empty(elsewhere.Reloaded);

        // ...and the news is not lost either.
        elsewhere.GoTo(ShellPage.Overview);
        Assert.Equal([ShellPage.Overview], elsewhere.Reloaded);

        using var onIt = new Shell(_repo, ShellPage.Overview);

        onIt.Refresh.Touch(ShellPage.Overview, onIt.Page);

        // Watched, it moves there and then.
        Assert.Equal([ShellPage.Overview], onIt.Reloaded);
    }

    /// <summary>
    /// Goes red when the recorder's live reporting is made conditional on a page being visible.
    ///
    /// A source scan: the orchestrator's events cannot be raised here without a CallOrchestrator.
    /// Two lines have to stay unconditional whatever the shell is showing — the progress the
    /// worker reports several times a second, and the clearing of it when the call is done. A
    /// progress bar frozen at 80% because Durum happened to be hidden is the one screen that
    /// answers "is it still working" saying the wrong thing, and this file's whole change is an
    /// invitation to make exactly that mistake.
    /// </summary>
    [Fact]
    public void TheRecordersOwnReportingIsNeverSkipped()
    {
        var source = ShellSource();

        Assert.Contains(
            "Processing.ReportProgress(p.CallId, p.Stage, p.Percent, p.Engine);", source, StringComparison.Ordinal);

        var processed = Between(source, "orchestrator.CallProcessed += ", "orchestrator.LevelChanged += ");

        Assert.Contains("Processing.ClearProgress();", processed, StringComparison.Ordinal);

        // Not inside an "if". The clearing is the first thing the handler does.
        var clearing = processed.IndexOf("Processing.ClearProgress();", StringComparison.Ordinal);
        var firstIf = processed.IndexOf("if (", StringComparison.Ordinal);

        Assert.True(firstIf < 0 || clearing < firstIf, "ClearProgress bir koşulun içine girmiş.");
    }

    // ---- E: the wiring that cannot be driven from here ---------------------------------------

    /// <summary>
    /// Goes red when the sweep comes back.
    ///
    /// A source scan, and the plainest regression pin in the file: RefreshAll must not name the
    /// pages one by one any more. It used to be ten calls in a row — Overview, Calls, Ledger,
    /// Calendar, Todo, Promises, Mirror, Contacts, Processing, AiStatus — and the fastest way to
    /// undo everything here is for a helpful hand to put one of them back "just to be safe".
    /// </summary>
    [Fact]
    public void RefreshAllNoLongerReReadsThePagesOneByOne()
    {
        var body = Between(ShellSource(), "public void RefreshAll()", "private void RefreshBadges()");

        foreach (var call in new[]
                 {
                     "Overview.Refresh()", "Calls.Refresh()", "Ledger.Refresh()", "Calendar.Refresh()",
                     "Todo.Refresh()", "Promises.Refresh()", "Mirror.Refresh()", "Contacts.Refresh()",
                     "Processing.Refresh()", "AiStatus.Refresh()",
                 })
        {
            Assert.DoesNotContain(call, body, StringComparison.Ordinal);
        }

        Assert.Contains("_refresh.Everything(Page)", body, StringComparison.Ordinal);
        Assert.Contains("RefreshBadges()", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Goes red when the arrival is wired to the navigation command instead of to the page itself.
    ///
    /// A source scan, and the one that guards the longest list. Page is set from the rail buttons,
    /// the command palette, the digit shortcuts and Ctrl+F, from OpenContact, OpenFigureJourney,
    /// OpenAt and OpenCall, from the first screen's links and its "Göster", and from the two
    /// "Sözler sayfasında aç" buttons — the contact pane's and the contact window's, both of which
    /// assign Page directly and never touch NavigateCommand. Only the property setter sees all of
    /// them, so the arrival hangs off OnPageChanged; hang it off Navigate and half those routes
    /// land on a page that was marked and never re-read.
    /// </summary>
    [Fact]
    public void EveryWayOntoAPageGoesThroughTheArrival()
    {
        var source = ShellSource();

        var onChanged = Between(source, "partial void OnPageChanged(ShellPage value)", "[ObservableProperty] private string _statusText");

        Assert.Contains("Arrive(value)", onChanged, StringComparison.Ordinal);

        // Pressing the rail button for the page already open still means "reload this", and the
        // property change does not fire when nothing changed.
        var navigate = Between(source, "private void Navigate(string page)", "// ---- the refresh layer");

        Assert.Contains("Arrive(target)", navigate, StringComparison.Ordinal);

        // And nothing outside this file reaches past the property.
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "ShellViewModel.cs") continue;

            Assert.DoesNotContain("_page =", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Goes red when one of the three archive events stops reaching the refresh.
    ///
    /// A source scan. A call finishing, a call verb and a ledger ruling are the three things that
    /// change the archive under a running window, and each is one line in the constructor. Red
    /// means rulings are being written and no screen is being told: the complaint that started
    /// the previous round, and worse than the sweep it replaced.
    /// </summary>
    [Fact]
    public void TheThreeArchiveEventsStillReachTheRefresh()
    {
        var lines = ShellSource().Split('\n');

        foreach (var wiring in new[] { "orchestrator.CallFinished +=", "CallActions.Changed +=", "LedgerActions.Changed +=" })
        {
            var line = lines.SingleOrDefault(l => l.Contains(wiring, StringComparison.Ordinal));

            Assert.NotNull(line);
            Assert.Contains("RefreshAll", line, StringComparison.Ordinal);
        }
    }

    private static string ShellSource() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "VoiceTranscript.App", "ViewModels", "ShellViewModel.cs"));

    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        var end = source.IndexOf(to, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, $"'{from}' ile '{to}' arası bulunamadı.");

        return source[start..end];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
