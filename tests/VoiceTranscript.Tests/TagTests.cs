using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Labels the user puts on conversations.
///
/// The vocabulary is theirs — "tehdit edildik", "önemli", whatever the archive needs words for —
/// so the rules here are about identity and ownership, not about which labels exist. Identity is
/// Turkish-folded (İ/ı casing must not split one tag into two), and the table is user data: the
/// pipeline never writes it, so no test here involves reprocessing.
/// </summary>
public sealed class TagTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-tag-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public TagTests()
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

    private long Call(long? contactId = null) => _repo.InsertCall(new Call
    {
        App = CallApp.WhatsApp,
        StartedAt = DateTimeOffset.Parse("2026-08-30T15:00:00+03:00"),
        State = ProcessingState.Analysed,
        ContactId = contactId,
    });

    [Fact]
    public void ATagSticksAndComesBackAsTyped()
    {
        var call = Call();

        _repo.Tag(call, "tehdit edildik");

        Assert.Equal("tehdit edildik", Assert.Single(_repo.TagsOf(call)));
    }

    /// <summary>
    /// "Önemli" and "ONEMLI" are one tag. Two rows here would mean filtering by one of them
    /// silently misses conversations labelled with the other spelling of the same word.
    /// </summary>
    [Fact]
    public void SpellingVariantsOfTheSameWordAreOneTag()
    {
        var call = Call();

        _repo.Tag(call, "Önemli");
        _repo.Tag(call, "ONEMLI");
        _repo.Tag(call, "önemli");

        // The survivor is the spelling the user chose first.
        Assert.Equal("Önemli", Assert.Single(_repo.TagsOf(call)));
    }

    [Fact]
    public void UntaggingByAnySpellingRemovesTheTag()
    {
        var call = Call();
        _repo.Tag(call, "Önemli");

        _repo.Untag(call, "ONEMLI");

        Assert.Empty(_repo.TagsOf(call));
    }

    [Fact]
    public void BlankTagsAreRefusedQuietly()
    {
        var call = Call();

        _repo.Tag(call, "   ");

        Assert.Empty(_repo.TagsOf(call));
    }

    [Fact]
    public void FilteringByTagFindsTheRightConversations()
    {
        var tagged = Call();
        var other = Call();

        _repo.Tag(tagged, "önemli");
        _repo.Tag(other, "fatura");

        Assert.Equal(tagged, Assert.Single(_repo.CallsTagged("ÖNEMLİ")).Id);
    }

    [Fact]
    public void FilteringCanBeScopedToOneContact()
    {
        var contact = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        var theirs = Call(contact);
        var someoneElses = Call();

        _repo.Tag(theirs, "önemli");
        _repo.Tag(someoneElses, "önemli");

        Assert.Equal(theirs, Assert.Single(_repo.CallsTagged("önemli", contact)).Id);
    }

    [Fact]
    public void SuggestionsComeMostUsedFirstWithCounts()
    {
        var a = Call();
        var b = Call();
        var c = Call();

        _repo.Tag(a, "önemli");
        _repo.Tag(b, "önemli");
        _repo.Tag(c, "fatura");

        var all = _repo.AllTags();

        Assert.Equal(("önemli", 2), all[0]);
        Assert.Equal(("fatura", 1), all[1]);
    }

    [Fact]
    public void BulkLookupCoversManyCallsInOneQuery()
    {
        var a = Call();
        var b = Call();
        var untagged = Call();

        _repo.Tag(a, "önemli");
        _repo.Tag(a, "fatura");
        _repo.Tag(b, "önemli");

        var map = _repo.TagsOf([a, b, untagged]);

        Assert.Equal(2, map[a].Count);
        Assert.Equal("önemli", Assert.Single(map[b]));
        Assert.False(map.ContainsKey(untagged));
    }

    /// <summary>Deleting the conversation takes its labels with it — nothing dangles.</summary>
    [Fact]
    public void TagsGoWithTheirConversation()
    {
        var call = Call();
        _repo.Tag(call, "önemli");

        _repo.DeleteCall(call);

        Assert.Empty(_repo.AllTags());
    }

    // ---- definitions: the tag wardrobe -------------------------------------------------------

    [Fact]
    public void ADefinitionRoundTripsAndUpdatesInPlace()
    {
        _repo.SaveTagDef(new TagDef("Önemli", "Flag24", "#E81123", 0));
        _repo.SaveTagDef(new TagDef("İş", "Briefcase24", "#0078D4", 1));

        // Spelling variants are one definition: identity is the folded form.
        _repo.SaveTagDef(new TagDef("ÖNEMLİ", "Star24", "#107C10", 0));

        var defs = _repo.TagDefs();

        Assert.Equal(2, defs.Count);
        Assert.Equal("ÖNEMLİ", defs[0].Tag);
        Assert.Equal("Star24", defs[0].Icon);
        Assert.Equal("#107C10", defs[0].Color);
    }

    [Fact]
    public void DeletingADefinitionNeverTouchesTheTaggings()
    {
        var call = Call();
        _repo.Tag(call, "Tehdit");
        _repo.SaveTagDef(new TagDef("Tehdit", "Warning24", "#D13438", 0));

        _repo.DeleteTagDef("tehdit"); // folded identity

        Assert.Empty(_repo.TagDefs());
        Assert.Contains("Tehdit", _repo.TagsOf(call));
    }

    [Fact]
    public void SeedingFillsAnEmptyTableOnceAndNeverAgain()
    {
        _repo.SeedDefaultTagDefs();
        var seeded = _repo.TagDefs();
        Assert.NotEmpty(seeded);

        // The user prunes the vocabulary; a restart must not push the defaults back.
        foreach (var def in seeded) _repo.DeleteTagDef(def.Tag);
        _repo.SaveTagDef(new TagDef("Benim", "Flag24", "#E81123", 0));

        _repo.SeedDefaultTagDefs();

        Assert.Equal(["Benim"], _repo.TagDefs().Select(d => d.Tag));
    }
}
