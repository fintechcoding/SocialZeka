using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// What the user knows about a person — photo, birth date, labelled facts.
///
/// All of it user-entered, none of it inferred, which is why no test here involves the pipeline:
/// there is no path from a transcript to these tables, by design. The rules under test are the
/// ordinary ones for user data — it round-trips, it merges with the person, it dies with them.
/// </summary>
public sealed class ContactProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-prof-{Guid.NewGuid():N}");
    private readonly string _path;
    private readonly Repository _repo;

    public ContactProfileTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "calls.db");

        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private long Contact(string name = "Uliana") => _repo.UpsertContact(name, CallApp.WhatsApp);

    [Fact]
    public void AnUntouchedContactHasNoProfile()
    {
        Assert.Null(_repo.GetProfile(Contact()));
    }

    [Fact]
    public void PhotoAndBirthDateRoundTripIndependently()
    {
        var id = Contact();

        _repo.SetContactPhoto(id, "contact-1-x.jpg");
        _repo.SetBirthDate(id, new DateOnly(1988, 3, 14));

        var profile = _repo.GetProfile(id);

        Assert.NotNull(profile);
        Assert.Equal("contact-1-x.jpg", profile.PhotoFile);
        Assert.Equal(new DateOnly(1988, 3, 14), profile.BirthDate);

        // Clearing one leaves the other.
        _repo.SetContactPhoto(id, null);

        profile = _repo.GetProfile(id);
        Assert.Null(profile!.PhotoFile);
        Assert.Equal(new DateOnly(1988, 3, 14), profile.BirthDate);
    }

    [Fact]
    public void FieldsKeepTheOrderTheyWereAddedIn()
    {
        var id = Contact();

        _repo.AddField(id, "Meslek", "Mimar");
        _repo.AddField(id, "Şehir", "İzmir");

        var fields = _repo.GetFields(id);

        Assert.Equal(["Meslek", "Şehir"], fields.Select(f => f.Label));
        Assert.Equal(["Mimar", "İzmir"], fields.Select(f => f.Value));
    }

    [Fact]
    public void ABlankHalfIsRefused()
    {
        var id = Contact();

        Assert.Throws<ArgumentException>(() => _repo.AddField(id, " ", "Mimar"));
        Assert.Throws<ArgumentException>(() => _repo.AddField(id, "Meslek", ""));
    }

    [Fact]
    public void EditingAndRemovingAFact()
    {
        var id = Contact();
        var field = _repo.AddField(id, "Meslek", "Mimar");

        _repo.UpdateField(field, "Meslek", "İç mimar");
        Assert.Equal("İç mimar", Assert.Single(_repo.GetFields(id)).Value);

        _repo.RemoveField(field);
        Assert.Empty(_repo.GetFields(id));
    }

    /// <summary>Facts follow the person through a merge; the kept contact's entries win.</summary>
    [Fact]
    public void MergingMovesFactsAndKeepsTheDestinationsProfile()
    {
        var kept = Contact("Uliana");
        var dup = Contact("Uliana W");

        _repo.SetBirthDate(kept, new DateOnly(1988, 3, 14));
        _repo.SetBirthDate(dup, new DateOnly(1990, 1, 1));
        _repo.SetContactPhoto(dup, "contact-dup.jpg");
        _repo.AddField(dup, "Şehir", "İzmir");

        _repo.MergeContacts(dup, kept);

        var profile = _repo.GetProfile(kept);

        // The kept contact's own birth date survives; the photo it lacked arrives.
        Assert.Equal(new DateOnly(1988, 3, 14), profile!.BirthDate);
        Assert.Equal("contact-dup.jpg", profile.PhotoFile);

        Assert.Equal("Şehir", Assert.Single(_repo.GetFields(kept)).Label);
        Assert.Null(_repo.GetContact(dup));
    }

    [Fact]
    public void DeletingAContactTakesProfileFieldsAndPhotoFile()
    {
        var photos = Path.Combine(_root, "photos");
        Directory.CreateDirectory(photos);

        var photoFile = "contact-9.jpg";
        File.WriteAllBytes(Path.Combine(photos, photoFile), new byte[16]);

        var id = Contact();
        _repo.SetContactPhoto(id, photoFile);
        _repo.AddField(id, "Meslek", "Mimar");

        _repo.DeleteContactCompletely(id, photos);

        Assert.Null(_repo.GetProfile(id));
        Assert.Empty(_repo.GetFields(id));
        Assert.False(File.Exists(Path.Combine(photos, photoFile)));
    }

    /// <summary>The birthday line is arithmetic on the user's entry, nothing more.</summary>
    [Theory]
    [InlineData("1988-03-14", "2026-03-14", "bugün doğum günü")]
    [InlineData("1988-03-14", "2026-03-02", "12 gün sonra doğum günü")]
    [InlineData("1988-03-14", "2026-08-31", "38 yaşında")]
    public void TheBirthdayLineSaysWhatTheDateImplies(string birth, string today, string expected)
    {
        var line = VoiceTranscript.App.ViewModels.ContactWindowViewModel.BirthdayLineFor(
            DateOnly.Parse(birth), DateOnly.Parse(today));

        Assert.Contains(expected, line);
    }

    [Fact]
    public void NoBirthDateMeansNoLine()
    {
        Assert.Null(VoiceTranscript.App.ViewModels.ContactWindowViewModel.BirthdayLineFor(
            null, new DateOnly(2026, 8, 31)));
    }
}
