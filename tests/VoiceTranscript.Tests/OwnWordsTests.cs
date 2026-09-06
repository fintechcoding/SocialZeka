using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// "Elindeki kayıtlar": what the other person has said, in their own words, grouped by subject.
///
/// This is the evidence-side answer to "give me arguments I can use", which is a prompt this
/// product refuses to write. What it can honestly hand somebody is the record — their claims and
/// their promises, dated, each with the sentence and the moment that plays it — so these tests
/// pin down that it is a record: the user's own words are not in it, a promise the user threw
/// out is not in it, and the user's own corrections win where they made them.
/// </summary>
public sealed class OwnWordsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-own-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;
    private readonly long _contact;

    public OwnWordsTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
        _contact = _repo.UpsertContact("Avukat", CallApp.WhatsApp);
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

    private long Call(DateTimeOffset at)
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = at,
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);
        return call;
    }

    private static readonly DateTimeOffset June = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset August = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Claims and promises land under the same subject, newest first, with their dates.
    ///
    /// Red means the list has stopped being usable as a record: a subject split in two, an order
    /// that does not follow the conversations, or a promise that lost the date and the status
    /// that say whether it is still open.
    /// </summary>
    [Fact]
    public void ClaimsAndPromisesAreGroupedBySubjectNewestFirst()
    {
        var june = Call(June);
        var august = Call(August);

        _repo.InsertClaim(new Claim
        {
            CallId = june, ContactId = _contact, ByMe = false,
            Quote = "Kira on beş bin dedik", QuoteStartMs = 4_000,
            Entity = "kira", Attribute = "tutar", Value = "15.000", NumericValue = 15_000,
        });

        _repo.InsertClaim(new Claim
        {
            CallId = august, ContactId = _contact, ByMe = false,
            Quote = "Yirmi binin altı olmaz", QuoteStartMs = 8_000,
            Entity = "kira", Attribute = "tutar", Value = "20.000", NumericValue = 20_000,
        });

        _repo.InsertCommitment(new Commitment
        {
            CallId = august, ContactId = _contact, ByMe = false,
            Quote = "Hafta içinde gönderiyorum", QuoteStartMs = 2_400,
            Obligation = "dilekçeyi iletmek",
            DeadlineDate = new DateOnly(2026, 8, 23),
            Status = CommitmentStatus.Open,
        });

        var groups = _repo.OwnWords(_contact);

        Assert.Equal(2, groups.Count);

        var promise = groups.Single(g => g.Subject == "dilekçeyi iletmek");
        var word = Assert.Single(promise.Words);
        Assert.True(word.IsPromise);
        Assert.Equal(CommitmentStatus.Open, word.Status);
        Assert.Equal(new DateOnly(2026, 8, 23), word.Deadline);
        Assert.Equal("Hafta içinde gönderiyorum", word.Quote);
        Assert.Equal(2_400, word.StartMs);

        var kira = groups.Single(g => g.Subject == "kira");
        Assert.Equal(2, kira.Words.Count);
        Assert.Equal("Yirmi binin altı olmaz", kira.Words[0].Quote);
        Assert.Equal(August, kira.Words[0].CallStartedAt);
        Assert.Equal("tutar", kira.Words[0].Attribute);
        Assert.Equal("20.000", kira.Words[0].Value);
        Assert.All(kira.Words, w => Assert.False(w.IsPromise));
    }

    /// <summary>
    /// The user's own words are not evidence about the other person, and a promise the user
    /// threw out is not a promise.
    ///
    /// Red means the card is about to hand somebody a "record" that includes things they said
    /// themselves, or a line they already ruled was never a commitment.
    /// </summary>
    [Fact]
    public void TheUsersOwnLinesAndDismissedPromisesAreLeftOut()
    {
        var call = Call(August);

        _repo.InsertClaim(new Claim
        {
            CallId = call, ContactId = _contact, ByMe = true,
            Quote = "Ben on bin demiştim", QuoteStartMs = 1_000,
            Entity = "kira", Attribute = "tutar", Value = "10.000", NumericValue = 10_000,
        });

        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = _contact, ByMe = true,
            Quote = "Sözleşmeyi ben yollarım", QuoteStartMs = 2_000,
            Obligation = "sözleşme göndermek",
            Status = CommitmentStatus.Open,
        });

        var theirs = _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = _contact, ByMe = false,
            Quote = "Bir ara bakarız artık", QuoteStartMs = 3_000,
            Obligation = "bakmak",
            Status = CommitmentStatus.Open,
        });

        _repo.DismissCommitment(theirs);

        Assert.Empty(_repo.OwnWords(_contact));
    }

    /// <summary>
    /// A promise the user reworded or postponed reads back the way they left it.
    ///
    /// Red means the record has gone back to the machine's wording and the machine's date, which
    /// is the postponement being held against the other person as a missed deadline.
    /// </summary>
    [Fact]
    public void TheUsersOwnWordingAndDateWin()
    {
        var call = Call(August);

        var promise = _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = _contact, ByMe = false,
            Quote = "Hafta içinde gönderiyorum", QuoteStartMs = 2_400,
            Obligation = "dilekçe",
            DeadlineDate = new DateOnly(2026, 8, 23),
            Status = CommitmentStatus.Open,
        });

        _repo.SetUserObligation(promise, "Dilekçeyi Polonya'ya iletmek");
        _repo.SetUserDeadline(promise, new DateOnly(2026, 9, 15));

        var group = Assert.Single(_repo.OwnWords(_contact));
        Assert.Equal("Dilekçeyi Polonya'ya iletmek", group.Subject);

        var word = Assert.Single(group.Words);
        Assert.Equal(new DateOnly(2026, 9, 15), word.Deadline);

        // The quote itself is never edited — that is what makes it evidence.
        Assert.Equal("Hafta içinde gönderiyorum", word.Quote);
    }

    /// <summary>A person who has said nothing yields an empty list rather than an empty group.</summary>
    [Fact]
    public void APersonWithNoRecordHasNoGroups()
    {
        Call(August);
        Assert.Empty(_repo.OwnWords(_contact));
    }
}
