using System.IO.Compression;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The screen half of "iki makine, tek kişi" — PLAN-IKINCI-TUR §7.2.
///
/// <see cref="TwoMachineImportTests"/> pins what a merge DECIDES. These pin what the user is
/// actually told about it, which is a separate thing and was the whole of what was missing: the
/// core landed with the schema, the manifest, the decision merge and the leftovers store all
/// working, and none of it visible anywhere.
///
/// Three screens, and every one of them exists to stop a particular silence — an archive that
/// cannot say which machine it is, an import that could only be understood after it had happened,
/// and a decision that went nowhere and said nothing.
/// </summary>
[Collection(InterfaceLanguageCollection.Name)]
public class TwoMachineScreenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-ekran-{Guid.NewGuid():N}");

    private readonly AppPaths _theirs;
    private readonly AppPaths _mine;
    private readonly Repository _theirRepository;
    private readonly Repository _myRepository;
    private readonly BackupService _theirBackup;
    private readonly BackupService _myBackup;

    private static readonly DateTimeOffset Shared = DateTimeOffset.Parse("2026-03-04T10:00:00+03:00");

    public TwoMachineScreenTests()
    {
        (_theirs, _theirRepository, _theirBackup) = Archive("oteki");
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
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The database files can still be held briefly.
        }

        GC.SuppressFinalize(this);
    }

    // ---- the first screen: which machine this archive is ---------------------

    /// <summary>
    /// Goes red when somebody who uses one computer is shown the two-machine card.
    ///
    /// The name and the size are for everybody; the twin block is for the person who actually
    /// carries an archive between two machines. An empty region headed "machines you have heard
    /// from" is not neutral — it is a feature advertised to somebody who does not have it, on the
    /// screen they came to for a straight answer about their data.
    ///
    /// The second half is the sentence §7.2 names as the one that matters. An archive that said
    /// only "last imported on the 20th" would let its owner believe the two copies are in step.
    /// </summary>
    [Fact]
    public void AnArchiveWithNoTwinShowsNoTwinLineAndOneWithATwinSaysWhatItDoesNotKnow()
    {
        var card = Card();

        card.RefreshArchiveIdentity();

        Assert.Empty(card.Twins);
        Assert.False(card.HasTwins);

        // And the two facts everybody gets are really there, with the counts the database holds.
        _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        card.RefreshArchiveIdentity();
        Assert.Contains("1 kişi", card.ArchiveSize, StringComparison.Ordinal);
        Assert.Contains(Schema.Version.ToString(), card.ArchiveSchema, StringComparison.Ordinal);

        _myRepository.RecordArchiveLink(
            "aaaabbbbccccdddd", "İş bilgisayarı",
            writtenAt: DateTimeOffset.Now.AddDays(-17),
            importedAt: DateTimeOffset.Now.AddDays(-17));

        card.RefreshArchiveIdentity();

        var line = Assert.Single(card.Twins);
        Assert.Contains("İş bilgisayarı", line, StringComparison.Ordinal);
        Assert.Contains("17 gün önce", line, StringComparison.Ordinal);
        Assert.Contains("bilmiyorum", line, StringComparison.Ordinal);
    }

    // ---- the second screen: a preview before importing -----------------------

    /// <summary>
    /// Goes red when a backup with no manifest is described as anything but "kaynak bilinmiyor".
    ///
    /// Every backup written before this feature looks exactly like this, and every one of them
    /// still imports perfectly. Calling such a file damaged — or refusing it — would be the
    /// application lying about its own older self, and the person it would lie to is the one
    /// holding the only copy of their archive.
    /// </summary>
    [Fact]
    public async Task ThePreviewSaysUnknownRatherThanBrokenWhenThereIsNoManifest()
    {
        Call(_theirRepository, _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp));

        var file = await TheirBackupAsync();
        Strip(file, BackupManifest.EntryName);

        var manifest = await _myBackup.ReadManifestAsync(file);
        Assert.Null(manifest);

        var text = HealthViewModel.PreviewText(manifest, _myRepository.ArchiveSize());

        Assert.Contains("Kaynak bilinmiyor", text, StringComparison.Ordinal);
        Assert.Contains("bozuk değil", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Yazan:", text, StringComparison.Ordinal);

        // And it still imports, which is the whole reason the reading is "unknown".
        var result = await _myBackup.ImportAsync(file);
        Assert.Equal(1, result.Calls);
    }

    /// <summary>
    /// Goes red when the preview stops answering the four questions somebody has before a merge:
    /// whose archive is this, when was it written, how much is in it, and IS THE AUDIO IN IT.
    ///
    /// The audio line is the one that was learned the hard way. The default backup button leaves
    /// the recordings out, so somebody carrying a file to their other computer arrives with
    /// conversations that cannot be played or transcribed again — and until now found that out by
    /// trying, weeks later, on the one call they wanted to hear.
    /// </summary>
    [Fact]
    public async Task ThePreviewReadsTheManifestAndSaysWhetherTheAudioIsThere()
    {
        _theirRepository.SetArchiveLabel("İş bilgisayarı");
        OneConversationOnBothMachines();

        var manifest = await _myBackup.ReadManifestAsync(await TheirBackupAsync());
        Assert.NotNull(manifest);

        var text = HealthViewModel.PreviewText(manifest, _myRepository.ArchiveSize());

        Assert.Contains("İş bilgisayarı", text, StringComparison.Ordinal);
        Assert.Contains("1 görüşme", text, StringComparison.Ordinal);
        Assert.Contains("ses kayıtları YOK", text, StringComparison.Ordinal);
        Assert.Contains($"Şema v{Schema.Version}", text, StringComparison.Ordinal);

        // Beside this archive's own, because "1 görüşme" only means something against a number.
        Assert.Contains("Bu arşiv:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Goes red when a backup from a newer build is refused, or is taken without a word.
    ///
    /// Both are wrong and for the same reason. The merge intersects the columns of the two
    /// databases, so a column the newer build added is left behind — which is the right behaviour
    /// and exactly the thing that must not happen quietly. The file is offered, and the sentence
    /// says which half will not come.
    /// </summary>
    [Fact]
    public async Task ABackupFromANewerBuildIsOfferedWithAWarningRatherThanRefused()
    {
        Call(_theirRepository, _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp));

        var file = await TheirBackupAsync();

        var ahead = new BackupManifest(
            "ffffeeeeddddcccc", "Yeni bilgisayar", DateTimeOffset.Now,
            Calls: 4, Contacts: 2, IncludesAudio: true,
            SchemaVersion: Schema.Version + 1, AppVersion: "9.9.9");

        Replace(file, BackupManifest.EntryName, ahead.ToJson());

        var read = await _myBackup.ReadManifestAsync(file);
        Assert.NotNull(read);
        Assert.True(read.FromANewerBuild);

        var text = HealthViewModel.PreviewText(read, _myRepository.ArchiveSize());
        Assert.Contains("daha yeni bir sürümle", text, StringComparison.Ordinal);
        Assert.Contains("geçmeyecek", text, StringComparison.Ordinal);

        // Offered, not refused: the import runs and brings what it can.
        var result = await _myBackup.ImportAsync(file);
        Assert.Equal(1, result.Calls);
    }

    // ---- the sentence afterwards --------------------------------------------

    /// <summary>
    /// Goes red when the sentence after an import stops being arithmetic.
    ///
    /// Every number in it is read straight off what the merge returned. A screen that rounded one,
    /// or worked one out for itself, would be making a claim about somebody's archive that the
    /// database does not support — and the two numbers nobody could see before are precisely the
    /// ones this package exists to expose.
    /// </summary>
    [Fact]
    public async Task TheImportSentenceSaysExactlyWhatTheMergeReturned()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        _theirRepository.SaveNote(theirCall, "Yazılı istemeyi unutma");
        _myRepository.SaveNote(myCall, "Telefonda hallettik");
        _theirRepository.Tag(theirCall, "önemli");

        var result = await _myBackup.ImportAsync(await TheirBackupAsync());
        var sentence = HealthViewModel.ImportSentence(result);

        Assert.Contains($"{result.Calls} görüşme geldi", sentence, StringComparison.Ordinal);
        Assert.Contains($"{result.AlreadyHere} tanesi zaten vardı", sentence, StringComparison.Ordinal);
        Assert.Contains($"{result.DecisionSummary.Carried} kararı getirdim", sentence, StringComparison.Ordinal);
        Assert.Contains($"{result.DecisionSummary.Left} tanesini getiremedim", sentence, StringComparison.Ordinal);

        // Nothing fell out on the way: the hard invariant, said on screen.
        Assert.True(result.DecisionSummary.Balances);
        Assert.Equal(result.DecisionSummary.Left, _myRepository.Leftovers().Count);
    }

    // ---- the third screen: answering a leftover ------------------------------

    /// <summary>
    /// Goes red when one of the three answers stops doing what its label says, or closes the row
    /// without doing it.
    ///
    /// The order matters as much as the effect: writing happens first and the row is closed only
    /// once the write succeeded. A row marked answered with nothing behind it is the silent loss
    /// this whole package was written to end, wearing the uniform of a fix.
    /// </summary>
    [Fact]
    public async Task EachOfTheThreeAnswersOnANoteBothAppliesAndClosesTheRow()
    {
        var myCall = await ANoteWrittenOnBothMachinesAsync();

        // Take theirs.
        var row = Assert.Single(_myRepository.Leftovers(), l => l.Kind == LeftoverKinds.Note);
        Assert.True(LeftoverResolution.Apply(_myRepository, row, LeftoverResolutions.Theirs));
        Assert.Equal("Yazılı istemeyi unutma", _myRepository.GetNote(myCall));
        Assert.Equal(LeftoverResolutions.Theirs, Answered(row.Id).Resolution);

        // And it can be taken back, which puts this machine's own words back on the conversation
        // and asks the question again.
        Assert.True(LeftoverResolution.Undo(_myRepository, row));
        Assert.Equal("Telefonda hallettik", _myRepository.GetNote(myCall));
        Assert.True(Answered(row.Id).IsOpen);

        // Keep mine: nothing is written, the row closes.
        Assert.True(LeftoverResolution.Apply(_myRepository, row, LeftoverResolutions.Mine));
        Assert.Equal("Telefonda hallettik", _myRepository.GetNote(myCall));
        Assert.Equal(LeftoverResolutions.Mine, Answered(row.Id).Resolution);

        _myRepository.ReopenLeftover(row.Id);

        // Keep both: two paragraphs, mine first, one after the other. That is what "both" can
        // mean for a note and cannot mean for a promise.
        Assert.True(LeftoverResolution.Apply(_myRepository, row, LeftoverResolutions.Both));
        Assert.Equal("Telefonda hallettik\n\nYazılı istemeyi unutma", _myRepository.GetNote(myCall));
        Assert.Equal(LeftoverResolutions.Both, Answered(row.Id).Resolution);
    }

    /// <summary>
    /// Goes red when a row offers an answer it cannot carry out.
    ///
    /// Two shapes of that, and both were possible before the choices were checked against the
    /// archive. "İkisi de tut" on a tag is the same act as "Ötekini al" — a tag is on or off — and
    /// on a promise it is not an act at all, because a promise cannot be both kept and turned
    /// down. A button that does nothing, or that silently does what its neighbour does, teaches
    /// people to distrust the other two.
    /// </summary>
    [Fact]
    public async Task NoAnswerIsOfferedThatCannotBeCarriedOut()
    {
        await ANoteWrittenOnBothMachinesAsync();

        var note = Assert.Single(_myRepository.Leftovers(), l => l.Kind == LeftoverKinds.Note);

        // Both sides wrote text, so all three are real answers.
        var choices = LeftoverResolution.Offer(_myRepository, note);
        Assert.True(choices.CanTakeTheirs);
        Assert.True(choices.CanKeepBoth);

        // A promise ruling is one state. Refused by name, and the refusal is a reason rather than
        // an absence.
        var promise = new ImportLeftover(
            note.Id, note.Fingerprint, null, LeftoverKinds.Promise, note.CallId, null,
            "karar", LeftoverValue.Promise(0, 0, null, null), LeftoverValue.Promise(1, 0, null, null),
            "cuma günü yollarım", DateTimeOffset.Now, null, null);

        Assert.Equal(LeftoverRefusal.SingleValued, LeftoverResolution.Offer(_myRepository, promise).Both);
        Assert.False(LeftoverResolution.Apply(_myRepository, promise, LeftoverResolutions.Both));

        var tag = promise with { Kind = LeftoverKinds.Tag, Field = "etiket", Mine = null, Theirs = "önemli" };
        Assert.Equal(LeftoverRefusal.OneOrTheOther, LeftoverResolution.Offer(_myRepository, tag).Both);
    }

    /// <summary>
    /// Goes red the day a question the user has already answered is asked again.
    ///
    /// This is what the answered rows are kept for. Somebody who carries an archive back and forth
    /// every week would otherwise be handed the same list of decisions every week, forever — the
    /// designer's own warning — and a queue that regrows is a queue people stop opening.
    /// </summary>
    [Fact]
    public async Task AnAnsweredLeftoverDoesNotComeBackOnTheNextImport()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        _theirRepository.SaveNote(theirCall, "Yazılı istemeyi unutma");
        _myRepository.SaveNote(myCall, "Telefonda hallettik");

        // One file, imported twice: that is what carrying an unchanged archive across a second
        // time actually is, and it is the moment the question would be asked again.
        var file = await TheirBackupAsync();
        await _myBackup.ImportAsync(file);

        var row = Assert.Single(_myRepository.Leftovers(), l => l.Kind == LeftoverKinds.Note);
        Assert.True(LeftoverResolution.Apply(_myRepository, row, LeftoverResolutions.Mine));

        // The same file again, which is what a second transfer of an unchanged archive is.
        var again = await _myBackup.ImportAsync(file);

        Assert.Empty(_myRepository.Leftovers().Where(l => l.Kind == LeftoverKinds.Note));

        var kept = Assert.Single(_myRepository.Leftovers(includeResolved: true), l => l.Kind == LeftoverKinds.Note);
        Assert.Equal(LeftoverResolutions.Mine, kept.Resolution);

        // The merge still COUNTS it as one it could not carry, and that is right: the other
        // machine's value genuinely did not land, this time as last time. What must not happen is
        // a second row, and there is none — the answered row is what the fingerprint collides
        // with, which is the whole reason answered rows are kept forever.
        Assert.Equal(1, again.DecisionSummary.Left);
        Assert.Single(_myRepository.Leftovers(includeResolved: true), l => l.Kind == LeftoverKinds.Note);
    }

    /// <summary>
    /// Goes red when the line a leftover stores a ruling as stops being readable back into
    /// columns.
    ///
    /// A promise ruling, a suggestion ruling and a board card are four, two and three fields
    /// written into one column, and the screen has to take them apart twice over — to say them in
    /// words a person can read, and to write them back when the user takes the other machine's
    /// side. The format and the parse live in one class for exactly this reason; this is the test
    /// that keeps the two ends of it honest.
    /// </summary>
    [Fact]
    public void EveryRulingWrittenAsOneLineCanBeReadBackIntoItsColumns()
    {
        var promise = LeftoverValue.ReadPromise(
            LeftoverValue.Promise(1, 1, "2026-03-20", "evrağı göndermek"));

        Assert.Equal(CommitmentStatus.Fulfilled, promise!.Value.Status);
        Assert.True(promise.Value.Dismissed);
        Assert.Equal(new DateOnly(2026, 3, 20), promise.Value.Deadline);
        Assert.Equal("evrağı göndermek", promise.Value.Obligation);

        // A wording with the separator in it is still one wording, because it is last and takes
        // everything after "söz=".
        Assert.Equal(
            "bir · iki",
            LeftoverValue.ReadPromise(LeftoverValue.Promise(0, 0, null, "bir · iki"))!.Value.Obligation);

        Assert.Null(LeftoverValue.ReadPromise(LeftoverValue.Promise(0, 0, null, null))!.Value.Deadline);

        var suggestion = LeftoverValue.ReadSuggestion(LeftoverValue.Suggestion(3, "panoya"));
        Assert.Equal(ActionStatus.Routed, suggestion!.Value.Status);
        Assert.Equal("panoya", suggestion.Value.RoutedNote);

        var card = LeftoverValue.ReadBoard(LeftoverValue.Board(BoardLane.Mine, "Evrak", "2026-03-20"));
        Assert.Equal(BoardLane.Mine, card!.Value.Lane);
        Assert.Equal("Evrak", card.Value.Title);
        Assert.Equal(new DateOnly(2026, 3, 20), card.Value.RemindOn);

        Assert.Null(LeftoverValue.ReadBoard(LeftoverValue.Board(BoardLane.Done, null, null))!.Value.Title);

        // Anything that is not one of these is refused rather than guessed at: a value this build
        // cannot read back is a button it must not draw.
        Assert.Null(LeftoverValue.ReadPromise("tutuldu"));
        Assert.Null(LeftoverValue.ReadBoard("bakilmayacak · Evrak"));
    }

    // ---- FOTO-YEDEK: the photos travel --------------------------------------

    /// <summary>
    /// Goes red when contact photos stop travelling in a backup.
    ///
    /// They never did, and that stopped being merely a gap the day person-card fields began
    /// crossing between machines: the profile carries the photo's FILE NAME, so the other computer
    /// took a name pointing at a file that only ever existed on this disk and drew an empty frame
    /// for a face its owner had chosen. The bytes are nothing — every photo is shrunk to 512
    /// pixels on the way in — so there was never a size argument for leaving them out.
    ///
    /// An older backup that has none is unaffected: there is no photos/ folder in it, nothing is
    /// adopted, and the import behaves exactly as it did before.
    /// </summary>
    [Fact]
    public async Task ABackupCarriesTheContactPhotosAndAnOlderOneWithoutThemStillImports()
    {
        var contact = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var photo = $"contact-{contact}-20260901120000123.jpg";

        Directory.CreateDirectory(_theirs.Photos);
        await File.WriteAllBytesAsync(Path.Combine(_theirs.Photos, photo), [0xFF, 0xD8, 0xFF, 0xDB]);
        _theirRepository.SetContactPhoto(contact, photo);

        var file = await TheirBackupAsync();

        using (var zip = ZipFile.OpenRead(file))
            Assert.Contains(zip.Entries, e => e.FullName == $"photos/{photo}");

        await _myBackup.ImportAsync(file);

        // The name that arrived names a file that is really here, which is the entire point.
        var here = Assert.Single(_myRepository.ListContacts());
        var profile = _myRepository.GetProfile(here.Id);

        Assert.Equal(photo, profile?.PhotoFile);
        Assert.True(File.Exists(Path.Combine(_mine.Photos, photo)));

        // And a backup written before this feature — no photos/ folder at all — still imports.
        var older = await TheirBackupAsync("eski-yedek.zip");
        Strip(older, $"photos/{photo}");

        var result = await _myBackup.ImportAsync(older);
        Assert.Equal(0, result.Calls);
    }

    // ---- the ground under all of it -----------------------------------------

    private HealthViewModel Card() => new(
        _mine,
        _myRepository,
        new VoiceTranscript.App.Services.EnvironmentSetup(_mine),
        new VoiceTranscript.App.Services.HardwareProbe(_mine, new VoiceTranscript.App.Services.EnvironmentSetup(_mine), _mine.Root),
        () => new AppSettings(),
        _mine.Root);

    private ImportLeftover Answered(long id) =>
        _myRepository.Leftovers(includeResolved: true).Single(l => l.Id == id);

    /// <summary>
    /// The one disagreement every answer can be tried on: two machines, one conversation, a
    /// different note written on each.
    /// </summary>
    private async Task<long> ANoteWrittenOnBothMachinesAsync()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        _theirRepository.SaveNote(theirCall, "Yazılı istemeyi unutma");
        _myRepository.SaveNote(myCall, "Telefonda hallettik");

        await _myBackup.ImportAsync(await TheirBackupAsync());
        return myCall;
    }

    /// <summary>The same conversation, recorded on both machines.</summary>
    private (long Theirs, long Mine) OneConversationOnBothMachines()
    {
        var themAyse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var meAyse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);

        return (Call(_theirRepository, themAyse), Call(_myRepository, meAyse));
    }

    private static long Call(Repository repository, long contactId)
    {
        var id = repository.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            StartedAt = Shared,
            Duration = TimeSpan.FromMinutes(3),
            State = ProcessingState.Analysed,
        });

        repository.ReplaceSegments(id, [new Segment
        {
            CallId = id, IsMe = false, StartMs = 0, EndMs = 1500, Text = "cuma günü yollarım",
        }]);

        return id;
    }

    private async Task<string> TheirBackupAsync(string name = "oteki-yedek.zip")
    {
        var file = Path.Combine(_root, name);
        await _theirBackup.BackupAsync(file, includeAudio: false);
        return file;
    }

    /// <summary>Takes one entry out of a written backup, to make an older file out of a new one.</summary>
    private static void Strip(string archivePath, string entryName)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        zip.GetEntry(entryName)?.Delete();
    }

    /// <summary>Rewrites one entry, to make a file a different build would have written.</summary>
    private static void Replace(string archivePath, string entryName, string content)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        zip.GetEntry(entryName)?.Delete();

        using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
        writer.Write(content);
    }
}
