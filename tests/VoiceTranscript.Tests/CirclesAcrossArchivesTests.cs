using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Circles between the user's two computers.
///
/// They use this product on two machines and move the archive between them, so a concept that
/// exists only on the machine it was typed on is a concept they would have to build twice. Two
/// halves have to travel: the WORDS (contact_circle, keyed by the folded spelling, which is why
/// it is text and not an id — the same word is the same key on both computers, with no
/// translation table) and WHO IS IN THEM (contact_profile.circle_folded, which rides across
/// inside the person's card).
///
/// The second half is carried by machinery that already existed: the profile merge reads its
/// columns at run time, so a column added by a schema step travels without anybody adding a line
/// for it. That is a claim worth a test rather than a comment — it is exactly the kind of thing
/// that is true until somebody replaces the run-time column list with a hand-written one.
/// </summary>
public sealed class CirclesAcrossArchivesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-cevre-goc-{Guid.NewGuid():N}");

    private readonly AppPaths _theirs;
    private readonly AppPaths _mine;
    private readonly Repository _theirRepository;
    private readonly Repository _myRepository;
    private readonly BackupService _theirBackup;
    private readonly BackupService _myBackup;

    public CirclesAcrossArchivesTests()
    {
        (_theirs, _theirRepository, _theirBackup) = Archive("gelen");
        (_mine, _myRepository, _myBackup) = Archive("burada");
    }

    private (AppPaths, Repository, BackupService) Archive(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.EnsureCreated();

        var database = new Database(paths.DatabaseFile);
        database.Migrate();

        var repository = new Repository(database);
        return (paths, repository, new BackupService(paths, repository));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static long Call(Repository repository, long contactId, DateTimeOffset at)
    {
        var id = repository.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            StartedAt = at,
            Duration = TimeSpan.FromMinutes(3),
            State = ProcessingState.Analysed,
        });

        repository.AssignContact(id, contactId);
        return id;
    }

    /// <summary>
    /// Goes red when a circle stops crossing between the user's two archives — either the word
    /// or the filing.
    ///
    /// Three things are checked, and each is a different way it could fail. The definitions must
    /// arrive, keyed by the folded word, so the other machine's circles are not lost and one
    /// defined on both does not become two. The assignment must arrive on a person this machine
    /// had never heard of. And it must also arrive on a person BOTH machines know, where nobody
    /// here had said anything — writing into an empty place is a move, not a merge, and this is
    /// the case that would silently do nothing if the profile merge stopped reading its columns
    /// at run time.
    /// </summary>
    [Fact]
    public async Task ACircleAndItsPeopleCrossBetweenTwoArchives()
    {
        // The other computer: two circles, and two people filed in them.
        _theirRepository.SaveCircle(new Circle("Aile", "Home24", "#8764B8"));
        _theirRepository.SaveCircle(new Circle("İş", "Briefcase24", "#0078D4"));

        var theirUliana = _theirRepository.UpsertContact("Uliana", CallApp.WhatsApp);
        var theirVeli = _theirRepository.UpsertContact("Veli", CallApp.WhatsApp);

        _theirRepository.SetContactCircle(theirUliana, "Aile");
        _theirRepository.SetContactCircle(theirVeli, "İş");

        Call(_theirRepository, theirUliana, DateTimeOffset.Parse("2026-02-02T11:00:00+03:00"));
        Call(_theirRepository, theirVeli, DateTimeOffset.Parse("2026-02-03T11:00:00+03:00"));

        var file = Path.Combine(_root, "yedek.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        // This computer knows Uliana and has never said which circle she is in.
        var myUliana = _myRepository.UpsertContact("Uliana", CallApp.WhatsApp);
        Call(_myRepository, myUliana, DateTimeOffset.Parse("2026-01-01T10:00:00+03:00"));

        Assert.Null(_myRepository.CircleOf(myUliana));

        await _myBackup.ImportAsync(file);

        // The words arrived, once each and by their folded identity.
        Assert.Equal(["Aile", "İş"], _myRepository.Circles().Select(c => c.Name).Order().ToArray());

        // The person this machine already knew was filed by the other one, into a place this one
        // had left empty.
        Assert.Equal("Aile", _myRepository.CirclesByContact()[myUliana].Name);

        // And the person it had never heard of arrived already filed.
        var myVeli = Assert.Single(_myRepository.FindContacts("Veli")).Id;
        Assert.Equal("İş", _myRepository.CirclesByContact()[myVeli].Name);

        // Which means the counts on the first screen are right the moment the import finishes.
        var counts = _myRepository.CallCountsByCircle();

        Assert.Equal(2, counts.ByCircle["aile"]);
        Assert.Equal(1, counts.ByCircle["is"]);
        Assert.Equal(0, counts.Uncircled);
    }

    /// <summary>
    /// Goes red when an import overwrites a circle the user chose on THIS machine.
    ///
    /// The rule the whole profile merge rests on: a field this machine has filled keeps what it
    /// holds, whatever the other one says. Two filled fields that disagree are a conflict, and
    /// quietly picking a winner is how a merge loses something somebody typed.
    /// </summary>
    [Fact]
    public async Task AnImportNeverMovesSomebodyThisMachineHasAlreadyFiled()
    {
        _theirRepository.SaveCircle(new Circle("İş", "Briefcase24", "#0078D4"));

        var theirUliana = _theirRepository.UpsertContact("Uliana", CallApp.WhatsApp);
        _theirRepository.SetContactCircle(theirUliana, "İş");
        Call(_theirRepository, theirUliana, DateTimeOffset.Parse("2026-02-02T11:00:00+03:00"));

        var file = Path.Combine(_root, "yedek.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        _myRepository.SaveCircle(new Circle("Aile", "Home24", "#8764B8"));

        var myUliana = _myRepository.UpsertContact("Uliana", CallApp.WhatsApp);
        _myRepository.SetContactCircle(myUliana, "Aile");

        await _myBackup.ImportAsync(file);

        Assert.Equal("Aile", _myRepository.CirclesByContact()[myUliana].Name);
    }
}
