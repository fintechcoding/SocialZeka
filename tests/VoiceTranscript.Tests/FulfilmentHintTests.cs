using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The "tutuldu mu?" offer under a promise card, and the "was there a chance" count beside it.
///
/// Both are offers, not marks: the machine cannot hear whether a promise was kept, so the most
/// it may do is point at a later line that sounds like the same subject and ask. These tests pin
/// what it points at, and what it refuses to.
/// </summary>
public sealed class FulfilmentHintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-hint-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly long _contact;

    public FulfilmentHintTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);

        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private long Call(long contact, DateTimeOffset at, params string[] lines)
    {
        var id = _repo.InsertCall(new Call { ContactId = contact, App = CallApp.WhatsApp, StartedAt = at, State = ProcessingState.Analysed });
        _repo.AssignContact(id, contact);
        _repo.ReplaceSegments(id, lines.Select((text, i) => new Segment
        {
            CallId = id, IsMe = i % 2 == 0, StartMs = i * 4000, EndMs = i * 4000 + 3000, Text = text,
        }));
        return id;
    }

    private long Promise(long call, string obligation) =>
        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = _contact, Quote = "cumaya yollarım", QuoteStartMs = 4000, Obligation = obligation,
        });

    /// <summary>Goes red when a later line about the same subject is not offered, or the offer points at the wrong moment.</summary>
    [Fact]
    public void ALaterLineAboutTheSameThingIsOffered()
    {
        var first = Call(_contact, DateTimeOffset.Parse("2026-08-28T07:12:00+03:00"), "Alo", "Sözleşme taslağını cumaya yollarım");
        var id = Promise(first, "Sözleşme taslağını göndermek");

        var later = Call(_contact, DateTimeOffset.Parse("2026-09-04T14:02:00+03:00"),
            "Merhaba abi", "Sözleşme taslağını dün akşam gönderdim sana", "Tamam bakarım");

        var hint = _repo.SuggestFulfilment(id);

        Assert.NotNull(hint);
        Assert.Equal(later, hint!.CallId);
        Assert.Equal(4000, hint.StartMs);
        Assert.False(hint.IsMe);
        Assert.Contains("gönderdim", hint.Quote);
    }

    /// <summary>
    /// Goes red when one shared word is enough — "sözleşme" alone comes up in every call with a
    /// lawyer — or when another person's calls are searched for this person's promise.
    /// </summary>
    [Fact]
    public void OneSharedWordOrSomebodyElsesCallIsNotAnOffer()
    {
        var first = Call(_contact, DateTimeOffset.Parse("2026-08-28T07:12:00+03:00"), "Alo", "Sözleşme taslağını cumaya yollarım");
        var id = Promise(first, "Sözleşme taslağını göndermek");

        Call(_contact, DateTimeOffset.Parse("2026-09-01T10:00:00+03:00"), "Sözleşme konusunu sonra konuşuruz");

        var other = _repo.UpsertContact("Avukat", CallApp.WhatsApp);
        Call(other, DateTimeOffset.Parse("2026-09-02T10:00:00+03:00"), "Sözleşme taslağını gönderdim");

        Assert.Null(_repo.SuggestFulfilment(id));
    }

    /// <summary>Goes red when a call BEFORE the promise counts, or one on the deadline day itself.</summary>
    [Fact]
    public void CallsSinceCountsOnlyWhatCameAfterTheDay()
    {
        var deadline = new DateOnly(2026, 9, 1);

        Call(_contact, DateTimeOffset.Parse("2026-08-28T07:12:00+03:00"), "önce");
        Call(_contact, DateTimeOffset.Parse("2026-09-01T18:00:00+03:00"), "o gün");
        Call(_contact, DateTimeOffset.Parse("2026-09-04T14:02:00+03:00"), "sonra");

        Assert.Equal(1, _repo.CountCallsSince(_contact, deadline));
        Assert.Equal(0, _repo.CountCallsSince(_contact, new DateOnly(2026, 9, 10)));
    }
}
