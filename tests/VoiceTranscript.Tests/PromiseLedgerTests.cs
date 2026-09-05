using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// One query for every promise screen.
///
/// The Sözler page, the calendar, the caller strip and the home screen all list promises, and
/// four lists built four ways disagree the moment one of them forgets a filter. PromiseLedger is
/// the single source they read; these tests pin what it narrows and what it never drops.
/// </summary>
public sealed class PromiseLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-ledger-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;

    public PromiseLedgerTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private (long call, long contact) Seed(string name, DateTimeOffset at)
    {
        var contact = _repo.UpsertContact(name, CallApp.WhatsApp);
        var call = _repo.InsertCall(new Call { ContactId = contact, App = CallApp.WhatsApp, StartedAt = at, State = ProcessingState.Analysed });
        _repo.AssignContact(call, contact);
        return (call, contact);
    }

    private long Promise(long call, long contact, bool byMe, string obligation) =>
        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, ByMe = byMe, Quote = $"{obligation} sözü", Obligation = obligation,
        });

    /// <summary>Goes red when either direction, the name, or the call's date goes missing from a row.</summary>
    [Fact]
    public void BothDirectionsComeBackWithWhoseAndWhen()
    {
        var when = DateTimeOffset.Parse("2026-08-28T07:12:00+03:00");
        var (call, contact) = Seed("Gürhan", when);
        Promise(call, contact, byMe: true, "Sözleşme taslağını göndermek");
        Promise(call, contact, byMe: false, "Dilekçeyi iletmek");

        var rows = _repo.PromiseLedger();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("Gürhan", r.ContactName));
        Assert.All(rows, r => Assert.Equal(when, r.CallStartedAt));
        Assert.Single(rows, r => r.Commitment.ByMe);
        Assert.Single(rows, r => !r.Commitment.ByMe);
    }

    /// <summary>Goes red when a kept or dismissed promise leaks into the open list, or is lost from the full one.</summary>
    [Fact]
    public void ClosedRowsAreOutUnlessAskedFor()
    {
        var (call, contact) = Seed("Avukat", DateTimeOffset.UtcNow);
        var open = Promise(call, contact, false, "açık");
        var kept = Promise(call, contact, false, "tutulan");
        var dismissed = Promise(call, contact, false, "reddedilen");

        _repo.FulfilCommitment(kept);
        _repo.DismissCommitment(dismissed);

        Assert.Equal([open], _repo.PromiseLedger().Select(r => r.Commitment.Id).ToList());

        var all = _repo.PromiseLedger(includeClosed: true).Select(r => r.Commitment.Id).ToList();
        Assert.Equal(3, all.Count);
        Assert.Contains(kept, all);
        Assert.Contains(dismissed, all);
    }

    /// <summary>Goes red when the person filter or the date filter lets the wrong rows through.</summary>
    [Fact]
    public void CanBeNarrowedToAPersonAndAPeriod()
    {
        var (oldCall, gurhan) = Seed("Gürhan", DateTimeOffset.UtcNow.AddDays(-40));
        var (newCall, avukat) = Seed("Avukat", DateTimeOffset.UtcNow.AddDays(-2));
        Promise(oldCall, gurhan, false, "eski");
        Promise(newCall, avukat, false, "yeni");

        var recent = _repo.PromiseLedger(since: DateOnly.FromDateTime(DateTime.Today.AddDays(-7)));
        Assert.Single(recent);
        Assert.Equal("yeni", recent[0].Commitment.Obligation);

        var theirs = _repo.PromiseLedger(contactId: gurhan);
        Assert.Single(theirs);
        Assert.Equal("eski", theirs[0].Commitment.Obligation);
    }

    /// <summary>Goes red when a promise from a call with no contact is dropped for want of a name.</summary>
    [Fact]
    public void ANamelessCallStillCounts()
    {
        var call = _repo.InsertCall(new Call { App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Analysed });
        _repo.InsertCommitment(new Commitment { CallId = call, Quote = "bakarız", Obligation = "bakmak" });

        var rows = _repo.PromiseLedger();

        Assert.Single(rows);
        Assert.Equal("Bilinmeyen", rows[0].ContactName);
    }
}
