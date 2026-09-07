using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// One person, two computers, one archive carried between them by hand.
///
/// The user said it on 6 September 2026: "birden fazla PC'de kullanıyorum". The product was never
/// designed for it, and the audit found the losses that follow — a conversation that exists on
/// both machines took nothing but its transcript across, so every promise ruling, note, tag and
/// ear verdict made on the other computer stayed there, unreported.
///
/// <see cref="ArchiveMergeTests"/> pins what a merge ADDS. These pin what it DECIDES: that a
/// ruling arriving into an empty place is applied, that a ruling arriving onto one that is not
/// leaves what is here alone and goes into a list instead, that the two numbers add up to what
/// arrived, and that asking the same question twice is a defect rather than a feature.
/// </summary>
public class TwoMachineImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-iki-{Guid.NewGuid():N}");

    private readonly AppPaths _theirs;
    private readonly AppPaths _mine;
    private readonly Repository _theirRepository;
    private readonly Repository _myRepository;
    private readonly BackupService _theirBackup;
    private readonly BackupService _myBackup;

    /// <summary>The conversation both machines have. Everything below hangs off it.</summary>
    private static readonly DateTimeOffset Shared = DateTimeOffset.Parse("2026-03-04T10:00:00+03:00");

    public TwoMachineImportTests()
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

    private static long Call(Repository repository, long contactId, DateTimeOffset at, string[] lines)
    {
        var id = repository.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            StartedAt = at,
            Duration = TimeSpan.FromMinutes(3),
            State = ProcessingState.Analysed,
        });

        repository.ReplaceSegments(id, lines.Select((text, i) => new Segment
        {
            CallId = id, IsMe = i % 2 == 0, StartMs = i * 2000, EndMs = i * 2000 + 1500, Text = text,
        }));

        return id;
    }

    /// <summary>The same conversation, recorded on both machines with the same promise in it.</summary>
    private (long Theirs, long Mine) OneConversationOnBothMachines()
    {
        var themAyse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var meAyse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);

        var theirCall = Call(_theirRepository, themAyse, Shared, ["cuma günü yollarım", "tamam"]);
        var myCall = Call(_myRepository, meAyse, Shared, ["cuma günü yollarım", "tamam"]);

        foreach (var (repository, call, contact) in new[]
                 {
                     (_theirRepository, theirCall, themAyse),
                     (_myRepository, myCall, meAyse),
                 })
        {
            repository.InsertCommitment(new Commitment
            {
                CallId = call,
                ContactId = contact,
                ByMe = false,
                Quote = "cuma günü yollarım",
                QuoteStartMs = 0,
                Obligation = "evrağı göndermek",
            });
        }

        return (theirCall, myCall);
    }

    private async Task<string> TheirBackupAsync(string name = "oteki-yedek.zip")
    {
        var file = Path.Combine(_root, name);
        await _theirBackup.BackupAsync(file, includeAudio: false);
        return file;
    }

    private Commitment MyPromise(long callId) =>
        _myRepository.PromiseLedger(includeClosed: true).Single(p => p.Commitment.CallId == callId).Commitment;

    // ---- the decision crosses ----------------------------------------------

    /// <summary>
    /// Goes red the day a promise ruling stops crossing to a conversation that exists on both
    /// machines — which is the defect this package was written for.
    ///
    /// Before it, the copy reached only the children of NEW calls. On a two-machine archive most
    /// conversations are on both sides, so "tutuldu" pressed on the laptop simply never appeared
    /// on the desktop and nothing said it had not. The promise here is open on this machine and
    /// nothing else has been decided about it, so the incoming ruling displaces nothing: writing
    /// into an empty place is a move, and it is applied without asking anybody.
    /// </summary>
    [Fact]
    public async Task ARulingMadeOnTheOtherMachineReachesAConversationBothHave()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        var theirPromise = _theirRepository
            .PromiseLedger(includeClosed: true).Single(p => p.Commitment.CallId == theirCall).Commitment;

        _theirRepository.FulfilCommitment(theirPromise.Id);
        _theirRepository.SetUserDeadline(theirPromise.Id, new DateOnly(2026, 3, 20));

        var result = await _myBackup.ImportAsync(await TheirBackupAsync());

        Assert.Equal(0, result.Calls);
        Assert.Equal(1, result.AlreadyHere);

        var here = MyPromise(myCall);
        Assert.Equal(CommitmentStatus.Fulfilled, here.Status);
        Assert.Equal(new DateOnly(2026, 3, 20), here.UserDeadlineDate);
        Assert.NotNull(here.DecidedAt);

        // Counted, and counted as carried rather than as anything else.
        Assert.True(result.DecisionSummary.Carried >= 1);
        Assert.Equal(0, result.DecisionSummary.Left);
        Assert.Empty(_myRepository.Leftovers());
    }

    /// <summary>
    /// Goes red when a merge picks a winner between two rulings the user made, in either
    /// direction: by overwriting the local one, or by discarding the incoming one in silence.
    ///
    /// Both are the same failure. What is here is never replaced — that is the refusal at the top
    /// of the merge — and what could not be applied is never dropped, because a decision that
    /// disappears is indistinguishable from one that was never made.
    /// </summary>
    [Fact]
    public async Task ARulingMadeOnBothSidesKeepsTheLocalOneAndFilesTheOther()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        var theirPromise = _theirRepository
            .PromiseLedger(includeClosed: true).Single(p => p.Commitment.CallId == theirCall).Commitment;

        // There: kept. Here: turned down. Two people would call this a conflict; one person on two
        // computers calls it a thing they need to be shown.
        _theirRepository.FulfilCommitment(theirPromise.Id);
        _myRepository.DismissCommitment(MyPromise(myCall).Id);

        var result = await _myBackup.ImportAsync(await TheirBackupAsync());

        var here = MyPromise(myCall);
        Assert.True(here.DismissedByUser);
        Assert.Equal(CommitmentStatus.Open, here.Status);

        var left = Assert.Single(_myRepository.Leftovers(), l => l.Kind == LeftoverKinds.Promise);
        Assert.True(left.IsOpen);
        Assert.False(left.HadNowhereToLand);
        Assert.Contains("durum=1", left.Theirs);
        Assert.Contains("susturuldu=1", left.Mine);
        Assert.Equal("cuma günü yollarım", left.Quote);
        Assert.Equal(myCall, left.CallId);

        Assert.Equal(1, result.DecisionSummary.Left);
    }

    /// <summary>
    /// The other four kinds of decision, all onto a conversation both machines have.
    ///
    /// Goes red when a note, a tag, an ear verdict or a ruling on a suggestion stops crossing. The
    /// ear verdict is the expensive one: it costs somebody listening to a recording again, and it
    /// was among the things that never travelled.
    /// </summary>
    [Fact]
    public async Task TheNoteTheTagTheEarVerdictAndTheSuggestionRulingAllCross()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        _theirRepository.SaveNote(theirCall, "Bunu yazılı istemeyi unutma");
        _theirRepository.Tag(theirCall, "önemli");

        _theirRepository.SaveVerdict(new Verdict
        {
            CallId = theirCall,
            Kind = VerdictKind.Flag,
            QuoteFolded = "cuma gunu yollarim",
            StartMs = 0,
            Value = VerdictValue.Correct,
        });

        foreach (var repository in new[] { _theirRepository, _myRepository })
        {
            repository.InsertAction(new ActionItem
            {
                CallId = repository == _theirRepository ? theirCall : myCall,
                Action = "Evrağı yazılı iste",
                Quote = "cuma günü yollarım",
            });
        }

        var theirAction = _theirRepository.ActionsOf(theirCall).Single();
        _theirRepository.SetActionStatus(theirAction.Id, ActionStatus.Done);

        var result = await _myBackup.ImportAsync(await TheirBackupAsync());

        Assert.Equal("Bunu yazılı istemeyi unutma", _myRepository.GetNote(myCall));
        Assert.Equal(["önemli"], _myRepository.TagsOf(myCall));

        var verdict = Assert.Single(_myRepository.Verdicts(myCall));
        Assert.Equal(VerdictValue.Correct, verdict.Value);

        Assert.Equal(ActionStatus.Done, _myRepository.ActionsOf(myCall).Single().Status);

        Assert.Empty(_myRepository.Leftovers());
        Assert.True(result.DecisionSummary.Carried >= 4);
    }

    // ---- the arithmetic ----------------------------------------------------

    /// <summary>
    /// The hard invariant of §7.3: what was seen equals what was carried, plus what was already
    /// the same, plus what was left in the list.
    ///
    /// Goes red for the only thing it can go red for — a decision that reached none of the three,
    /// which is a decision dropped in silence. Break the merge so that one kind stops being
    /// applied AND stops being listed and this fails; break it so that a kind stops being applied
    /// but is still listed and this stays green, correctly, because nothing was lost.
    ///
    /// The mix is deliberate: one of each outcome, so the sum is not satisfied by three zeroes.
    /// </summary>
    [Fact]
    public async Task NothingIsDroppedInSilenceAcrossAMixedImport()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        var theirPromise = _theirRepository
            .PromiseLedger(includeClosed: true).Single(p => p.Commitment.CallId == theirCall).Commitment;

        // A conflict: both ruled, differently.
        _theirRepository.AbandonCommitment(theirPromise.Id);
        _myRepository.FulfilCommitment(MyPromise(myCall).Id);

        // A move: a note there, none here.
        _theirRepository.SaveNote(theirCall, "öteki makinede yazılmış");

        // An agreement: the same tag on both.
        _theirRepository.Tag(theirCall, "kira");
        _myRepository.Tag(myCall, "kira");

        // And a person's card, half filled on each side.
        var themAyse = _theirRepository.FindContacts("Ayşe").Single().Id;
        var meAyse = _myRepository.FindContacts("Ayşe").Single().Id;
        _theirRepository.SetBirthDate(themAyse, new DateOnly(1984, 3, 9));
        _theirRepository.SetContactPhoto(themAyse, "oteki.jpg");
        _myRepository.SetContactPhoto(meAyse, "burada.jpg");

        var result = await _myBackup.ImportAsync(await TheirBackupAsync());
        var counts = result.DecisionSummary;

        Assert.True(counts.Balances,
            $"görülen {counts.Seen} != {counts.Carried} + {counts.AlreadySame} + {counts.Left}");

        // Every outcome actually happened, so the equality is not three zeroes agreeing.
        Assert.True(counts.Carried > 0, "hiçbir karar getirilmedi");
        Assert.True(counts.AlreadySame > 0, "hiçbir karar zaten aynı değildi");
        Assert.True(counts.Left > 0, "hiçbir karar listeye düşmedi");

        // And the list is the same number the count claims.
        Assert.Equal(counts.Left, _myRepository.Leftovers().Count);
    }

    /// <summary>
    /// The round trip §7.3 asks for and had no test for: A to B, five kinds of decision made on B,
    /// B back to A. Every one of them is either applied here or listed here.
    ///
    /// Goes red the moment any kind of decision starts falling between the two — which, before
    /// this package, was all five of them.
    /// </summary>
    [Fact]
    public async Task TheRoundTripCarriesEveryKindOfDecisionOrListsIt()
    {
        // A → B. This machine is A; the other starts empty and receives everything.
        var ayse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var myCall = Call(_myRepository, ayse, Shared, ["cuma günü yollarım", "tamam"]);

        _myRepository.InsertCommitment(new Commitment
        {
            CallId = myCall, ContactId = ayse, ByMe = false,
            Quote = "cuma günü yollarım", Obligation = "evrağı göndermek",
        });

        _myRepository.InsertAction(new ActionItem
        {
            CallId = myCall, ContactId = ayse,
            Action = "Evrağı yazılı iste", Quote = "cuma günü yollarım",
        });

        var toB = Path.Combine(_root, "a-dan-b-ye.zip");
        await _myBackup.BackupAsync(toB, includeAudio: false);
        await _theirBackup.ImportAsync(toB);

        // Five decisions on B, on the conversation both now have.
        var theirCall = _theirRepository.ListCalls(limit: 10).Single(c => c.StartedAt == Shared).Id;

        var theirPromise = _theirRepository
            .PromiseLedger(includeClosed: true).Single(p => p.Commitment.CallId == theirCall).Commitment;

        _theirRepository.FulfilCommitment(theirPromise.Id);                       // 1 söz kararı
        _theirRepository.SaveNote(theirCall, "B'de yazılmış not");                // 2 not
        _theirRepository.Tag(theirCall, "acil");                                  // 3 etiket
        _theirRepository.SaveVerdict(new Verdict                                  // 4 kulak teyidi
        {
            CallId = theirCall, Kind = VerdictKind.Flag,
            QuoteFolded = "cuma gunu yollarim", StartMs = 0, Value = VerdictValue.NotThat,
        });
        _theirRepository.SetActionStatus(                                         // 5 öneri kararı
            _theirRepository.ActionsOf(theirCall).Single().Id, ActionStatus.Hidden);

        // B → A.
        var result = await _myBackup.ImportAsync(await TheirBackupAsync("b-den-a-ya.zip"));

        Assert.True(result.DecisionSummary.Balances);

        // Nothing here had been decided, so all five are moves and none is a question.
        Assert.Empty(_myRepository.Leftovers());

        Assert.Equal(CommitmentStatus.Fulfilled, MyPromise(myCall).Status);
        Assert.Equal("B'de yazılmış not", _myRepository.GetNote(myCall));
        Assert.Equal(["acil"], _myRepository.TagsOf(myCall));
        Assert.Equal(VerdictValue.NotThat, Assert.Single(_myRepository.Verdicts(myCall)).Value);
        Assert.Equal(ActionStatus.Hidden, _myRepository.ActionsOf(myCall).Single().Status);

        Assert.Equal(5, result.DecisionSummary.Carried);
        Assert.Equal(5, result.DecisionSummary.Seen);
    }

    /// <summary>
    /// Goes red when a second import of the same file writes anything, or — the failure the
    /// designer named specifically — when it asks the user a question they have already answered.
    ///
    /// Without an identity on the leftover row, "burada kalsın" would come back on every future
    /// backup, forever, and a weekly two-machine user would be handed the same list every week
    /// until they stopped reading it. The fingerprint is what makes the repeat a no-op, and the
    /// answered row staying on disk is what makes the answer stick.
    /// </summary>
    [Fact]
    public async Task ASecondImportOfTheSameFileChangesNothingAndAsksNothingAgain()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        var theirPromise = _theirRepository
            .PromiseLedger(includeClosed: true).Single(p => p.Commitment.CallId == theirCall).Commitment;

        _theirRepository.AbandonCommitment(theirPromise.Id);
        _myRepository.FulfilCommitment(MyPromise(myCall).Id);
        _theirRepository.SaveNote(theirCall, "öteki makineden");

        var file = await TheirBackupAsync();

        var first = await _myBackup.ImportAsync(file);
        Assert.Equal(1, first.DecisionSummary.Left);

        var asked = Assert.Single(_myRepository.Leftovers());
        Assert.True(_myRepository.ResolveLeftover(asked.Id, LeftoverResolutions.Mine));

        var again = await _myBackup.ImportAsync(file);

        Assert.Equal(0, again.Calls);
        Assert.Equal(0, again.Contacts);

        // The same disagreement is seen again — it is still there — and produces no new question.
        Assert.Empty(_myRepository.Leftovers());
        Assert.Single(_myRepository.Leftovers(includeResolved: true));

        // And the answered row kept the user's answer rather than being reopened.
        var answered = Assert.Single(_myRepository.Leftovers(includeResolved: true));
        Assert.Equal(LeftoverResolutions.Mine, answered.Resolution);
        Assert.NotNull(answered.ResolvedAt);

        // The note that had already been carried is not carried a second time either.
        Assert.Equal("öteki makineden", _myRepository.GetNote(myCall));
        Assert.True(again.DecisionSummary.Balances);
    }

    /// <summary>
    /// A decision with nowhere to land is still not lost.
    ///
    /// §7.4 names this as the weakest joint in the whole design: promises are matched across two
    /// archives by their quote, so a conversation transcribed again on one machine breaks the
    /// match. Goes red when such a ruling is simply discarded — which is the behaviour that makes
    /// the weak joint into a silent one.
    /// </summary>
    [Fact]
    public async Task ARulingWhoseQuoteNoLongerMatchesIsListedRatherThanDropped()
    {
        var themAyse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var meAyse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);

        var theirCall = Call(_theirRepository, themAyse, Shared, ["cuma günü yollarım"]);
        var myCall = Call(_myRepository, meAyse, Shared, ["cuma günü yollarım"]);

        // The same conversation, heard differently by the two machines' transcriptions.
        _theirRepository.InsertCommitment(new Commitment
        {
            CallId = theirCall, ContactId = themAyse, ByMe = false,
            Quote = "cuma günü yollarım", Obligation = "evrağı göndermek",
        });

        _myRepository.InsertCommitment(new Commitment
        {
            CallId = myCall, ContactId = meAyse, ByMe = false,
            Quote = "cumartesi yollarım", Obligation = "evrağı göndermek",
        });

        var theirPromise = _theirRepository
            .PromiseLedger(includeClosed: true).Single(p => p.Commitment.CallId == theirCall).Commitment;
        _theirRepository.FulfilCommitment(theirPromise.Id);

        var result = await _myBackup.ImportAsync(await TheirBackupAsync());

        // Nothing here was touched.
        Assert.Equal(CommitmentStatus.Open, MyPromise(myCall).Status);

        // And the ruling is a row with nothing on the local side, which says exactly what
        // happened: it arrived, and there was nowhere here to put it.
        var left = Assert.Single(_myRepository.Leftovers());
        Assert.True(left.HadNowhereToLand);
        Assert.Null(left.Mine);
        Assert.Equal(LeftoverKinds.Promise, left.Kind);

        Assert.True(result.DecisionSummary.Balances);
        Assert.Equal(0, result.DecisionSummary.Carried);
    }

    /// <summary>
    /// The rollback switch of §7.3, which has one property worth a test: turning the carrying off
    /// must not turn the silence back on.
    ///
    /// Goes red if switching the merge off also switches off the counting or the list. That would
    /// make the rollback path a return to the exact defect the package was written to remove, and
    /// it is the shape a rollback most easily takes if nobody pins it.
    /// </summary>
    [Fact]
    public async Task SwitchingTheCarryingOffStillCountsAndStillLists()
    {
        var (theirCall, myCall) = OneConversationOnBothMachines();

        _theirRepository.SaveNote(theirCall, "öteki makinede yazılmış");
        _theirRepository.Tag(theirCall, "acil");

        var file = await TheirBackupAsync();

        // Reaching past ImportAsync deliberately: the switch is on the merge, and this is the
        // shape a setting would drive.
        var staging = Path.Combine(_root, "acilan");
        System.IO.Compression.ZipFile.ExtractToDirectory(file, staging);

        var incoming = Path.Combine(staging, "data", "voicetranscript.db");
        new Database(incoming).Migrate();
        new Database(incoming).ClearPool();

        var merged = _myRepository.MergeArchive(incoming, carryDecisions: false);

        Assert.True(merged.Decisions.Balances);
        Assert.Equal(0, merged.Decisions.Carried);
        Assert.Equal(2, merged.Decisions.Left);

        // Nothing was written, and everything was said.
        Assert.Equal("", _myRepository.GetNote(myCall));
        Assert.Empty(_myRepository.TagsOf(myCall));
        Assert.Equal(2, _myRepository.Leftovers().Count);
    }

    // ---- the manifest ------------------------------------------------------

    /// <summary>
    /// A backup says who wrote it, when, how much is in it and whether the audio came along —
    /// the last of which nobody was answering, so somebody carrying the default backup to their
    /// other computer found out by trying to play something.
    ///
    /// Goes red when the manifest stops being written, stops being readable without importing, or
    /// starts lying about the audio.
    /// </summary>
    [Fact]
    public async Task ABackupSaysWhoWroteItAndWhetherTheAudioIsInIt()
    {
        _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        _theirRepository.SetArchiveLabel("İş bilgisayarı");

        var file = await TheirBackupAsync();

        var manifest = await _theirBackup.ReadManifestAsync(file);

        Assert.NotNull(manifest);
        Assert.Equal("İş bilgisayarı", manifest.Label);
        Assert.Equal(Schema.Version, manifest.SchemaVersion);
        Assert.False(manifest.IncludesAudio);
        Assert.False(manifest.FromANewerBuild);
        Assert.Equal(_theirRepository.EnsureArchiveIdentity().Id, manifest.ArchiveId);

        var withAudio = Path.Combine(_root, "sesli.zip");
        await _theirBackup.BackupAsync(withAudio, includeAudio: true);

        Assert.True((await _theirBackup.ReadManifestAsync(withAudio))!.IncludesAudio);
    }

    /// <summary>
    /// A backup written before manifests existed imports exactly as it always did, and the source
    /// reads as UNKNOWN.
    ///
    /// Goes red if such a file is refused, or called corrupt, or — the quieter failure — if the
    /// archive invents a link to a machine it cannot name. An import from an unidentifiable file
    /// must leave the twin list untouched: a row there is a claim about where this archive's
    /// conversations came from, and there is nothing here to base one on.
    /// </summary>
    [Fact]
    public async Task AManifestLessBackupImportsAndTheSourceStaysUnknown()
    {
        _theirRepository.UpsertContact("Kemal", CallApp.WhatsApp);
        Call(_theirRepository, _theirRepository.FindContacts("Kemal").Single().Id, Shared, ["eski yedekten"]);

        var file = await TheirBackupAsync("kunyesiz.zip");
        StripManifest(file);

        Assert.Null(await _myBackup.ReadManifestAsync(file));

        var result = await _myBackup.ImportAsync(file);

        Assert.Equal(1, result.Calls);
        Assert.Null(result.Source);

        // Nothing is claimed about where it came from.
        Assert.Empty(_myRepository.ArchiveLinks());
    }

    /// <summary>Rewrites the zip without its manifest — what a backup from an older build is.</summary>
    private static void StripManifest(string path)
    {
        var rebuilt = path + ".eski";

        using (var source = System.IO.Compression.ZipFile.OpenRead(path))
        using (var target = System.IO.Compression.ZipFile.Open(
                   rebuilt, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                if (entry.FullName == BackupManifest.EntryName) continue;

                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                from.CopyTo(to);
            }
        }

        File.Move(rebuilt, path, overwrite: true);
    }

    /// <summary>
    /// The archive knows its own name, and remembers the twins it has actually met.
    ///
    /// Goes red when the identity stops being a single row — two would mean two answers to "which
    /// computer is this" — or when a link starts being written for an import that never happened,
    /// or when the date a backup was written stops being distinguishable from a date that is not
    /// known. That last one is the whole point of §7.2's sentence: "o günden sonra orada ne
    /// olduğunu bilmiyorum" is only sayable while unknown and old are different things.
    /// </summary>
    [Fact]
    public async Task TheArchiveKnowsItsOwnNameAndWhoItHasHeardFrom()
    {
        var identity = _myRepository.EnsureArchiveIdentity();

        Assert.NotEmpty(identity.Id);
        Assert.Null(identity.Label);
        Assert.Equal(identity.Id, _myRepository.EnsureArchiveIdentity().Id);   // once, not per call

        _myRepository.SetArchiveLabel("Ev bilgisayarı");
        Assert.Equal("Ev bilgisayarı", _myRepository.ArchiveIdentityOrNull()!.Label);

        // The archive has met nobody yet, and says so rather than guessing.
        Assert.Empty(_myRepository.ArchiveLinks());

        _theirRepository.SetArchiveLabel("İş bilgisayarı");
        _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);

        await _myBackup.ImportAsync(await TheirBackupAsync());

        var link = Assert.Single(_myRepository.ArchiveLinks());
        Assert.Equal("İş bilgisayarı", link.Label);
        Assert.Equal(_theirRepository.EnsureArchiveIdentity().Id, link.ArchiveId);

        // Both dates are known here, because both events actually happened and were witnessed.
        Assert.NotNull(link.WrittenAt);
        Assert.True(link.ImportedAt > DateTimeOffset.UtcNow.AddMinutes(-5));

        // And the identity is still one row — the import brought another archive's name with it
        // and that name did not land here.
        Assert.Equal("Ev bilgisayarı", _myRepository.ArchiveIdentityOrNull()!.Label);
        Assert.Equal(1, SingleRowCount());
    }

    private int SingleRowCount()
    {
        using var connection = new Database(_mine.DatabaseFile).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM archive_identity;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Merging two spellings of one person keeps the questions still waiting about them.
    ///
    /// Goes red when import_leftover falls out of the ledger's follow-the-contact list. Its
    /// contact_id cascades, so a leftover filed against the absorbed spelling is DESTROYED the
    /// moment the merge deletes that contact — an unanswered decision disappearing inside the one
    /// operation in the archive whose stated purpose is to lose nothing.
    /// </summary>
    [Fact]
    public async Task MergingTwoSpellingsOfOnePersonKeepsTheQuestionsWaitingAboutThem()
    {
        var themAyse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var meAyse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);

        _theirRepository.SetContactPhoto(themAyse, "oteki.jpg");
        _myRepository.SetContactPhoto(meAyse, "burada.jpg");

        await _myBackup.ImportAsync(await TheirBackupAsync());

        var waiting = Assert.Single(_myRepository.Leftovers(), l => l.Kind == LeftoverKinds.Person);
        Assert.Equal(meAyse, waiting.ContactId);

        // The same person, entered a second time under a fuller spelling, and merged.
        var fuller = _myRepository.UpsertContact("Ayşe Yılmaz", CallApp.WhatsApp);
        _myRepository.MergeContacts(meAyse, fuller);

        var survived = Assert.Single(_myRepository.Leftovers(), l => l.Kind == LeftoverKinds.Person);
        Assert.Equal(waiting.Id, survived.Id);
        Assert.Equal(fuller, survived.ContactId);
    }

    /// <summary>
    /// An import must not bring the other machine's unanswered questions with it.
    ///
    /// Goes red if import_leftover is ever added to the merge's copy list. The rows are this
    /// computer's queue; carried across they would arrive as questions the user has to answer
    /// twice, about disagreements between two archives one of which is not even here.
    /// </summary>
    [Fact]
    public async Task TheOtherMachinesUnansweredQuestionsDoNotTravel()
    {
        var (theirCall, _) = OneConversationOnBothMachines();

        // Give the other machine a leftover of its own, by importing a conflicting archive there.
        var thirdRoot = Path.Combine(_root, "ucuncu");
        var thirdPaths = new AppPaths(thirdRoot);
        thirdPaths.EnsureCreated();

        var thirdDatabase = new Database(thirdPaths.DatabaseFile);
        thirdDatabase.Migrate();

        var third = new Repository(thirdDatabase);
        var thirdBackup = new BackupService(thirdPaths, third);

        var contact = third.UpsertContact("Ayşe", CallApp.WhatsApp);
        var thirdCall = Call(third, contact, Shared, ["cuma günü yollarım", "tamam"]);
        third.SaveNote(thirdCall, "üçüncü makinenin notu");

        _theirRepository.SaveNote(theirCall, "ötekinin kendi notu");

        var fromThird = Path.Combine(_root, "ucuncuden.zip");
        await thirdBackup.BackupAsync(fromThird, includeAudio: false);
        await _theirBackup.ImportAsync(fromThird);

        Assert.NotEmpty(_theirRepository.Leftovers());

        // Now carry the other machine here. Its question stays its own.
        await _myBackup.ImportAsync(await TheirBackupAsync("otekinden.zip"));

        Assert.Empty(_myRepository.Leftovers());
    }
}
