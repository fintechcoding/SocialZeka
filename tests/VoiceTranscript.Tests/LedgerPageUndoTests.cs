using System.Reflection;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The ledger page's verbs, from the user's side of the screen.
///
/// Complaint 1 was "Defter temizlenemiyor / silinemiyor / düzenlenemiyor": one icon dismissed a
/// row for good with no way back, "Tutuldu" was dead on half the rows, and a wrong click was a
/// permanent loss. The repository half is pinned in <see cref="LedgerUndoTests"/>; this pins what
/// the page does with it — every ruling can be taken back, the tombstones are listed rather than
/// hidden, select mode touches exactly what was ticked, and the verbs that do not belong here
/// (anything about promises) are gone with the promises themselves.
/// </summary>
public sealed class LedgerPageUndoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-ledgerpage-{Guid.NewGuid():N}");
    private readonly Database _database;
    private readonly Repository _repository;
    private readonly LedgerViewModel _model;
    private readonly long _contact;
    private readonly long _call;

    public LedgerPageUndoTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        _database = new Database(paths.DatabaseFile);
        _database.Migrate();
        _repository = new Repository(_database);

        (_contact, _call) = Person("Gürhan");

        _model = new LedgerViewModel(_repository);
    }

    public void Dispose()
    {
        _database.ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A person with one analysed call, ten days back.</summary>
    private (long Contact, long Call) Person(string name)
    {
        var contact = _repository.UpsertContact(name, CallApp.WhatsApp);
        var call = _repository.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
            Duration = TimeSpan.FromMinutes(4),
            State = ProcessingState.Analysed,
        });
        _repository.AssignContact(call, contact);

        return (contact, call);
    }

    private long Finding(string quote, (long Contact, long Call)? of = null) =>
        _repository.InsertFlag(new Flag
        {
            CallId = of?.Call ?? _call,
            ContactId = of?.Contact ?? _contact,
            Kind = FlagKind.PressureTactic,
            Summary = "Baskı işareti",
            Quote = quote,
            QuoteStartMs = 9000,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private LedgerEntry FindingRow(long id) => _model.Entries.Single(e => e.Flag?.Id == id);

    private bool Shows(long flagId) => _model.Entries.Any(e => e.Flag?.Id == flagId);

    private List<long> OpenFindings() =>
        _repository.GetFlags(_contact).Select(f => f.Id).Order().ToList();

    /// <summary>
    /// Goes red when Reddet is one way again. The row leaves the list, the notice offers the way
    /// back, and Geri al puts the same row — the same database row — back on the page.
    /// </summary>
    [Fact]
    public void DismissedThenUndoneTheRowIsBack()
    {
        var id = Finding("bugün karar vermezsen başkasına vereceğim");
        _model.Refresh();

        _model.DismissCommand.Execute(FindingRow(id));

        Assert.False(Shows(id));
        Assert.True(_model.CanUndo);
        Assert.NotNull(_model.Notice);
        Assert.DoesNotContain(id, OpenFindings());

        _model.UndoCommand.Execute(null);

        Assert.True(Shows(id));
        Assert.False(_model.CanUndo);
        Assert.Null(_model.Notice);
        Assert.Contains(id, OpenFindings());
    }

    /// <summary>
    /// Goes red when a dismissal hides instead of filing: what was turned down is listed under
    /// Reddedilenler, counted on the chip, offers no second Reddet, and Geri getir lifts the
    /// tombstone.
    /// </summary>
    [Fact]
    public void TheDismissedChipListsWhatWasTurnedDownAndBringsItBack()
    {
        var first = Finding("bugün karar vermezsen başkasına vereceğim");
        var second = Finding("bunu kimseye söyleme");
        _model.Refresh();

        _model.DismissCommand.Execute(FindingRow(first));
        _model.DismissCommand.Execute(FindingRow(second));

        _model.Filter = LedgerFilter.Dismissed;

        Assert.Equal(2, _model.DismissedCount);
        Assert.Equal(2, _model.Entries.Count);
        Assert.All(_model.Entries, e => Assert.True(e.IsDismissed));
        Assert.All(_model.Entries, e => Assert.False(e.CanDismiss));

        _model.RestoreCommand.Execute(FindingRow(first));

        Assert.Contains(first, OpenFindings());
        Assert.True(_model.CanUndo);

        _model.Refresh();
        Assert.Equal(1, _model.DismissedCount);
        Assert.Equal(second, Assert.Single(_model.Entries).Flag!.Id);

        _model.Filter = LedgerFilter.Everything;
        Assert.True(Shows(first));
    }

    /// <summary>
    /// Goes red when select mode touches a row that was not ticked, or misses one that was — and
    /// when the one Geri al for the batch does not bring the whole batch back.
    /// </summary>
    [Fact]
    public void SelectModeDismissesExactlyTheTickedRows()
    {
        var a = Finding("a");
        var b = Finding("b");
        var c = Finding("c");
        _model.Refresh();

        _model.IsSelecting = true;
        Assert.All(_model.Entries, e => Assert.True(e.ShowSelector));
        Assert.False(_model.DismissSelectedCommand.CanExecute(null));

        FindingRow(a).IsSelected = true;
        FindingRow(c).IsSelected = true;

        Assert.Equal(2, _model.SelectedCount);
        Assert.Contains("(2)", _model.DismissSelectedText);
        Assert.True(_model.DismissSelectedCommand.CanExecute(null));

        _model.DismissSelectedCommand.Execute(null);

        Assert.False(_model.IsSelecting);
        Assert.Equal([b], OpenFindings());
        Assert.Equal(2, _model.DismissedCount);

        _model.UndoCommand.Execute(null);

        Assert.Equal([a, b, c], OpenFindings());
        Assert.Equal(0, _model.DismissedCount);
    }

    /// <summary>
    /// Goes red when a promise creeps back onto the ledger — as a row, a chip, or the "Tutuldu"
    /// verb. Promises are kept, postponed and refused on the Sözler page; this page's job is what
    /// went wrong. Checked in the view model and in the markup together, because a command with
    /// no button and a button with no command are both the bug.
    /// </summary>
    [Fact]
    public void TheLedgerHoldsNoPromises()
    {
        _repository.InsertCommitment(new Commitment
        {
            CallId = _call, ContactId = _contact, Quote = "cumaya yollarım", Obligation = "Sözleşmeyi göndermek",
        });
        Finding("bugün karar vermezsen başkasına vereceğim");
        _model.Refresh();

        Assert.Single(_model.Entries);
        Assert.NotNull(_model.Entries[0].Flag);

        Assert.Null(typeof(LedgerViewModel).GetProperty("FulfilCommand"));
        Assert.DoesNotContain(Enum.GetNames<LedgerFilter>(), n => n.Contains("Promise", StringComparison.Ordinal));

        var markup = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "VoiceTranscript.App", "Views", "LedgerPage.xaml"));

        Assert.DoesNotContain("FulfilCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("tutuldu", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CommandParameter=\"Promises\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Goes red when Kişi order is ordinal. Ç sorts after Z in code points and before D in
    /// Turkish; a list of people in the wrong alphabet is a list nobody can scan.
    /// </summary>
    [Fact]
    public void SortedByPersonTheRowsFollowTheTurkishAlphabet()
    {
        Finding("ara", Person("Zeynep"));
        Finding("yaz", Person("Çetin"));
        Finding("gönder", Person("Ayşe"));
        Finding("sözleşmeyi gönder");

        _model.Sort = LedgerSort.Contact;

        Assert.Equal(["Ayşe", "Çetin", "Gürhan", "Zeynep"], _model.Entries.Select(e => e.ContactName).ToList());
    }

    /// <summary>
    /// A changed figure has no Reddet, and it does have Yolculuk.
    ///
    /// The plan states that pair as one rule and only its negative half was built, so the single
    /// row on this page that cannot be ruled on was also the single row with nothing at all to
    /// press: "kira: 15.000 → 18.000" and no way to reach the dates, the quotes or the
    /// milliseconds behind those numbers, every one of which the contact card already holds.
    ///
    /// Red means either half of the rule has slipped — a Reddet appearing on a computed row, or
    /// Yolculuk gone from it — or that pressing it no longer names the person whose card the
    /// journey lives on, in which case the button leads somewhere with no journey in it. A
    /// finding keeps the opposite pair: Yolculuk there would promise a figure history that does
    /// not exist.
    /// </summary>
    [Fact]
    public void AChangedFigureOffersJourneyWhereEveryOtherRowOffersReddet()
    {
        var second = _repository.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-2),
            State = ProcessingState.Analysed,
        });
        _repository.AssignContact(second, _contact);

        foreach (var (call, value, amount, ms) in new[]
                 {
                     (_call, "15.000", 15_000m, 4_000),
                     (second, "18.000", 18_000m, 6_000),
                 })
        {
            _repository.InsertClaim(new Claim
            {
                CallId = call,
                ContactId = _contact,
                Quote = $"kira {value}",
                QuoteStartMs = ms,
                Entity = "kira",
                Attribute = "tutar",
                Value = value,
                NumericValue = amount,
                Unit = "TL",
            });
        }

        var flag = Finding("bugün karar vermezsen başkasına vereceğim");
        _model.Refresh();

        var figure = _model.Entries.Single(e => e.Kind == LedgerFilter.Changes);

        Assert.False(figure.CanDismiss);
        Assert.True(figure.CanShowJourney);

        // A finding is the other way round: it can be ruled on, and has no figure history.
        Assert.True(FindingRow(flag).CanDismiss);
        Assert.False(FindingRow(flag).CanShowJourney);

        long? asked = null;
        _model.JourneyRequested += (_, contactId) => asked = contactId;

        _model.JourneyCommand.Execute(figure);
        Assert.Equal(_contact, asked);

        // And it does nothing on a row that has no journey, rather than opening a card at a
        // section that would be empty.
        asked = null;
        _model.JourneyCommand.Execute(FindingRow(flag));
        Assert.Null(asked);

        // The page actually draws it, under the same rule the view model states.
        var markup = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "VoiceTranscript.App", "Views", "LedgerPage.xaml"));

        Assert.Contains("JourneyCommand", markup, StringComparison.Ordinal);
        Assert.Contains("CanShowJourney", markup, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
