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
/// hidden, select mode touches exactly what was ticked, and the verb that does not belong here
/// is gone.
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

    private long Promise(string obligation, (long Contact, long Call)? of = null) =>
        _repository.InsertCommitment(new Commitment
        {
            CallId = of?.Call ?? _call,
            ContactId = of?.Contact ?? _contact,
            Quote = $"{obligation} diye söz verdi",
            QuoteStartMs = 4200,
            Obligation = obligation,
            Status = CommitmentStatus.Open,
        });

    private long Finding(string quote) =>
        _repository.InsertFlag(new Flag
        {
            CallId = _call,
            ContactId = _contact,
            Kind = FlagKind.PressureTactic,
            Summary = "Baskı işareti",
            Quote = quote,
            QuoteStartMs = 9000,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private LedgerEntry PromiseRow(long id) => _model.Entries.Single(e => e.Commitment?.Id == id);

    private LedgerEntry FindingRow(long id) => _model.Entries.Single(e => e.Flag?.Id == id);

    private bool Shows(long promiseId) => _model.Entries.Any(e => e.Commitment?.Id == promiseId);

    private List<long> OpenPromises() =>
        _repository.AllOpenCommitments().Select(r => r.Commitment.Id).Order().ToList();

    /// <summary>
    /// Goes red when Reddet is one way again. The row leaves the list, the notice offers the way
    /// back, and Geri al puts the same row — the same database row — back on the page.
    /// </summary>
    [Fact]
    public void DismissedThenUndoneTheRowIsBack()
    {
        var id = Promise("Sözleşmeyi göndermek");
        _model.Refresh();

        _model.DismissCommand.Execute(PromiseRow(id));

        Assert.False(Shows(id));
        Assert.True(_model.CanUndo);
        Assert.NotNull(_model.Notice);
        Assert.DoesNotContain(id, OpenPromises());

        _model.UndoCommand.Execute(null);

        Assert.True(Shows(id));
        Assert.False(_model.CanUndo);
        Assert.Null(_model.Notice);
        Assert.Contains(id, OpenPromises());
    }

    /// <summary>
    /// Goes red when a dismissal hides instead of filing: what was turned down is listed under
    /// Reddedilenler, counted on the chip, offers no second Reddet, and Geri getir lifts the
    /// tombstone.
    /// </summary>
    [Fact]
    public void TheDismissedChipListsWhatWasTurnedDownAndBringsItBack()
    {
        var promise = Promise("Sözleşmeyi göndermek");
        var finding = Finding("bugün karar vermezsen başkasına vereceğim");
        _model.Refresh();

        _model.DismissCommand.Execute(PromiseRow(promise));
        _model.DismissCommand.Execute(FindingRow(finding));

        _model.Filter = LedgerFilter.Dismissed;

        Assert.Equal(2, _model.DismissedCount);
        Assert.Equal(2, _model.Entries.Count);
        Assert.All(_model.Entries, e => Assert.True(e.IsDismissed));
        Assert.All(_model.Entries, e => Assert.False(e.CanDismiss));

        _model.RestoreCommand.Execute(PromiseRow(promise));

        Assert.Contains(promise, OpenPromises());
        Assert.True(_model.CanUndo);

        _model.Refresh();
        Assert.Equal(1, _model.DismissedCount);
        Assert.NotNull(Assert.Single(_model.Entries).Flag);

        _model.Filter = LedgerFilter.Everything;
        Assert.True(Shows(promise));
    }

    /// <summary>
    /// Goes red when select mode touches a row that was not ticked, or misses one that was — and
    /// when the one Geri al for the batch does not bring the whole batch back.
    /// </summary>
    [Fact]
    public void SelectModeDismissesExactlyTheTickedRows()
    {
        var a = Promise("a");
        var b = Promise("b");
        var c = Promise("c");
        var f = Finding("bir");
        _model.Refresh();

        _model.IsSelecting = true;
        Assert.All(_model.Entries, e => Assert.True(e.ShowSelector));
        Assert.False(_model.DismissSelectedCommand.CanExecute(null));

        PromiseRow(a).IsSelected = true;
        FindingRow(f).IsSelected = true;

        Assert.Equal(2, _model.SelectedCount);
        Assert.Contains("(2)", _model.DismissSelectedText);
        Assert.True(_model.DismissSelectedCommand.CanExecute(null));

        _model.DismissSelectedCommand.Execute(null);

        Assert.False(_model.IsSelecting);
        Assert.Equal([b, c], OpenPromises());
        Assert.Empty(_repository.GetFlags(_contact));
        Assert.Equal(2, _model.DismissedCount);

        _model.UndoCommand.Execute(null);

        Assert.Equal([a, b, c], OpenPromises());
        Assert.Single(_repository.GetFlags(_contact));
        Assert.Equal(0, _model.DismissedCount);
    }

    /// <summary>
    /// Goes red when "Tutuldu" creeps back onto the ledger. A promise is kept on the Sözler side
    /// of the product; this page's job is what went wrong. Checked in the view model and in the
    /// markup together, because a command with no button and a button with no command are both
    /// the bug.
    /// </summary>
    [Fact]
    public void TheLedgerDoesNotOfferKept()
    {
        Assert.Null(typeof(LedgerViewModel).GetProperty("FulfilCommand"));
        Assert.DoesNotContain(
            typeof(LedgerViewModel).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            m => m.Name == "Fulfil");

        var markup = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "VoiceTranscript.App", "Views", "LedgerPage.xaml"));

        Assert.DoesNotContain("FulfilCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("tutuldu", markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Goes red when Kişi order is ordinal. Ç sorts after Z in code points and before D in
    /// Turkish; a list of people in the wrong alphabet is a list nobody can scan.
    /// </summary>
    [Fact]
    public void SortedByPersonTheRowsFollowTheTurkishAlphabet()
    {
        Promise("Aramak", Person("Zeynep"));
        Promise("Yazmak", Person("Çetin"));
        Promise("Göndermek", Person("Ayşe"));
        Promise("Sözleşmeyi göndermek");

        _model.Sort = LedgerSort.Contact;

        Assert.Equal(["Ayşe", "Çetin", "Gürhan", "Zeynep"], _model.Entries.Select(e => e.ContactName).ToList());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
