using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Searching within one person's conversations.
///
/// The bug this covers is the worst answer this product can give: telling somebody a conversation
/// did not happen when it did. The search screen fetched the best five hundred matches from the
/// whole archive and then narrowed them to a person, a speaker or a date in memory — so on a
/// common word, one person's lines sat below that cut and were discarded before the filter ever
/// saw them. What appeared was a confident "sonuç yok", complete with a helpful note about Turkish
/// suffixes, which is exactly the tone that gets believed.
///
/// The hazard was already written down on Repository.CallsMentioning, which exists partly to avoid
/// it. The search screen did it anyway. These tests hold the filters in SQL.
/// </summary>
public sealed class ScopedSearchTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-search-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public ScopedSearchTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private long Contact(string name) => _repo.UpsertContact(name, CallApp.WhatsApp);

    private long Call(long contactId, string startedAt)
        => _repo.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse(startedAt),
            State = ProcessingState.Analysed,
        });

    private void Say(long callId, bool isMe, string text, int startMs = 1000)
        => _repo.ReplaceSegments(callId,
        [
            new Segment
            {
                CallId = callId,
                IsMe = isMe,
                StartMs = startMs,
                EndMs = startMs + 2000,
                Text = text,
                TextNormalised = VoiceTranscript.Core.Text.TurkishText.NormalizeForSearch(text),
            },
        ]);

    /// <summary>
    /// The exact failure, reproduced: one line from the person of interest, buried under many
    /// matches from somebody else. Filtering after a limit loses it; filtering in SQL does not.
    /// </summary>
    [Fact]
    public void APersonsLineIsFoundEvenWhenBuriedUnderOtherPeoplesMatches()
    {
        var loud = Contact("Gürültü");
        var wanted = Contact("Serdal");

        // Plenty of noise from someone else, all containing the word.
        for (var i = 0; i < 40; i++)
        {
            var call = Call(loud, "2026-08-01T10:00:00+03:00");
            Say(call, isMe: false, "ödeme konusunu yarın konuşuruz");
        }

        var theirs = Call(wanted, "2026-08-02T10:00:00+03:00");
        Say(theirs, isMe: false, "ödeme salı günü yapılacak");

        // A limit small enough that the wanted line cannot be in the global top slice.
        var hits = _repo.Search("ödeme", limit: 5, contactId: wanted);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(wanted, h.ContactId));
    }

    /// <summary>Same failure through the "only the other party" filter.</summary>
    [Fact]
    public void TheOtherPartysLineIsFoundEvenWhenBuriedUnderMyOwn()
    {
        var contact = Contact("Serdal");

        for (var i = 0; i < 40; i++)
        {
            var mine = Call(contact, "2026-08-01T10:00:00+03:00");
            Say(mine, isMe: true, "fiyat konusunda anlaştık");
        }

        var theirs = Call(contact, "2026-08-02T10:00:00+03:00");
        Say(theirs, isMe: false, "fiyat on sekiz bin olacak");

        var hits = _repo.Search("fiyat", limit: 5, isMe: false);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.False(h.IsMe));
    }

    /// <summary>And through the date filter.</summary>
    [Fact]
    public void ARecentLineIsFoundEvenWhenBuriedUnderOlderOnes()
    {
        var contact = Contact("Serdal");

        for (var i = 0; i < 40; i++)
        {
            var old = Call(contact, "2025-01-01T10:00:00+03:00");
            Say(old, isMe: false, "teslimat gecikti");
        }

        var recent = Call(contact, "2026-08-30T10:00:00+03:00");
        Say(recent, isMe: false, "teslimat tamamlandı");

        var hits = _repo.Search("teslimat", limit: 5,
            since: DateTimeOffset.Parse("2026-08-01T00:00:00+03:00"));

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(h.CallStartedAt >= DateTimeOffset.Parse("2026-08-01T00:00:00+03:00")));
    }

    /// <summary>Filters compose, and an empty result stays empty when it should be.</summary>
    [Fact]
    public void FiltersCombineAndAGenuineMissIsStillAMiss()
    {
        var serdal = Contact("Serdal");
        var uliana = Contact("Uliana");

        var call = Call(serdal, "2026-08-02T10:00:00+03:00");
        Say(call, isMe: false, "kira ödemesi");

        Assert.NotEmpty(_repo.Search("kira", contactId: serdal, isMe: false));

        // Right word, wrong person.
        Assert.Empty(_repo.Search("kira", contactId: uliana));

        // Right person, wrong speaker.
        Assert.Empty(_repo.Search("kira", contactId: serdal, isMe: true));

        // Right person, word never said.
        Assert.Empty(_repo.Search("helikopter", contactId: serdal));
    }

    /// <summary>Unfiltered search is unchanged — the new parameters all default to "no filter".</summary>
    [Fact]
    public void SearchingWithoutFiltersStillReturnsEverybody()
    {
        var a = Contact("Serdal");
        var b = Contact("Uliana");

        Say(Call(a, "2026-08-01T10:00:00+03:00"), isMe: false, "toplantı saat üçte");
        Say(Call(b, "2026-08-01T11:00:00+03:00"), isMe: false, "toplantı ertelendi");

        var hits = _repo.Search("toplantı");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.ContactId == a);
        Assert.Contains(hits, h => h.ContactId == b);
    }
}
