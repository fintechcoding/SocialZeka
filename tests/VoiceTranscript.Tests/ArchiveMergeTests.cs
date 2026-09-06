using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Adding one archive to another, instead of choosing between them.
///
/// The restore answers "the laptop died" and answers it by replacing everything, which is why it
/// waits for a restart. That is the wrong operation for what people actually do: the same person
/// on two machines, or a backup from last month opened beside three newer weeks. Replacing there
/// is the damage — one of the two halves is deliberately thrown away.
///
/// These tests pin the three properties that make a merge safe to offer: what is already here is
/// never touched, the same conversation does not arrive twice, and the audio that comes with it
/// ends up somewhere this installation can actually read.
/// </summary>
public class ArchiveMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-merge-{Guid.NewGuid():N}");

    private readonly AppPaths _theirs;
    private readonly AppPaths _mine;
    private readonly Repository _theirRepository;
    private readonly Repository _myRepository;
    private readonly BackupService _theirBackup;
    private readonly BackupService _myBackup;

    private static readonly DateTimeOffset Shared = DateTimeOffset.Parse("2026-01-01T10:00:00+03:00");
    private static readonly DateTimeOffset Only = DateTimeOffset.Parse("2026-02-02T11:00:00+03:00");

    public ArchiveMergeTests()
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

    private static long Call(
        Repository repository, AppPaths paths, long contactId, DateTimeOffset at,
        string[] lines, string? audio = null)
    {
        string? mic = null;

        if (audio is not null)
        {
            var directory = paths.RecordingDirectoryFor(at);
            Directory.CreateDirectory(directory);

            mic = Path.Combine(directory, audio);
            File.WriteAllBytes(mic, [0x4F, 0x67, 0x67, 0x53]);
        }

        var id = repository.InsertCall(new Core.Domain.Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            StartedAt = at,
            Duration = TimeSpan.FromMinutes(2),
            State = ProcessingState.Analysed,
            MicPath = mic,
        });

        repository.ReplaceSegments(id, lines.Select((text, i) => new Segment
        {
            CallId = id, IsMe = i % 2 == 0, StartMs = i * 2000, EndMs = i * 2000 + 1500, Text = text,
        }));

        return id;
    }

    /// <summary>The archive being brought in: two people, two conversations, one with audio.</summary>
    private async Task<string> TheirBackupAsync(bool withAudio = true)
    {
        var ayse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var veli = _theirRepository.UpsertContact("Veli", CallApp.WhatsApp);

        Call(_theirRepository, _theirs, ayse, Shared, ["onların kopyası", "iki satır"]);

        var theirs = Call(
            _theirRepository, _theirs, veli, Only,
            ["yalnız onlarda", "olan", "üç satır"], audio: "call-9-mic.ogg");

        _theirRepository.InsertAction(new ActionItem
        {
            CallId = theirs,
            ContactId = veli,
            Action = "Faturayı gönder",
            Quote = "faturayı yarın atarım",
        });

        _theirRepository.AddTodo("Aynı not", null);

        var file = Path.Combine(_root, "yedek.zip");
        await _theirBackup.BackupAsync(file, includeAudio: withAudio);

        return file;
    }

    [Fact]
    public async Task WhatIsAlreadyHereIsLeftExactlyAsItIs()
    {
        var file = await TheirBackupAsync();

        var ayse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var mine = Call(_myRepository, _mine, ayse, Shared, ["benim kopyam"]);

        var result = await _myBackup.ImportAsync(file);

        Assert.Equal(1, result.AlreadyHere);
        Assert.Equal(1, result.Calls);

        // The transcript of the conversation that was already here is untouched — not merged
        // line by line with the incoming copy, which would produce a conversation nobody had.
        var kept = _myRepository.GetSegments(mine);
        Assert.Equal(["benim kopyam"], kept.Select(s => s.Text));
    }

    [Fact]
    public async Task TheSamePersonIsNotCreatedTwice()
    {
        var file = await TheirBackupAsync();
        _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);

        var result = await _myBackup.ImportAsync(file);

        Assert.Equal(1, result.Contacts);
        Assert.Single(_myRepository.FindContacts("Ayşe"));
        Assert.Single(_myRepository.FindContacts("Veli"));
    }

    /// <summary>The new-machine case: an empty archive gets everything, with no restart.</summary>
    [Fact]
    public async Task AnEmptyArchiveReceivesAllOfIt()
    {
        var file = await TheirBackupAsync();

        var result = await _myBackup.ImportAsync(file);

        Assert.Equal(2, result.Calls);
        Assert.Equal(2, result.Contacts);
        Assert.Equal(5, result.Segments);
        Assert.Equal(0, result.AlreadyHere);

        var calls = _myRepository.ListCalls(limit: 100);
        Assert.Equal(2, calls.Count);

        // The suggestion travelled with its conversation.
        var withActions = calls.Single(c => c.StartedAt == Only);
        Assert.Equal("Faturayı gönder", _myRepository.ActionsOf(withActions.Id).Single().Action);
    }

    /// <summary>
    /// The audio has to land where this installation keeps audio, under the identifier the call
    /// was given HERE. The archive's call-9 and this machine's call-9 are different
    /// conversations, and the first one written would otherwise be overwritten by the second.
    /// </summary>
    [Fact]
    public async Task TheAudioComesWithItUnderItsNewName()
    {
        var file = await TheirBackupAsync();

        // Deliberately occupying the name the incoming recording had on the other machine.
        var mineToo = _myRepository.UpsertContact("Zeynep", CallApp.WhatsApp);
        Call(_myRepository, _mine, mineToo, Only.AddDays(1), ["başka biri"], audio: "call-9-mic.ogg");

        var result = await _myBackup.ImportAsync(file);
        Assert.Equal(1, result.Recordings);

        var imported = _myRepository.ListCalls(limit: 100).Single(c => c.StartedAt == Only);

        Assert.NotNull(imported.MicPath);
        Assert.True(File.Exists(imported.MicPath));
        Assert.StartsWith(_mine.Recordings, imported.MicPath);
        Assert.Equal($"call-{imported.Id}-mic.ogg", Path.GetFileName(imported.MicPath));
    }

    /// <summary>
    /// A backup without audio is the default, and the ordinary outcome of importing one is a
    /// conversation with its words and no recording. It must not be left pointing at a file on
    /// the other machine's disk, which reads as "the audio is here" everywhere in the interface.
    /// </summary>
    [Fact]
    public async Task ACallWhoseAudioWasNotInTheBackupHasNone()
    {
        var file = await TheirBackupAsync(withAudio: false);

        var result = await _myBackup.ImportAsync(file);

        Assert.Equal(0, result.Recordings);
        Assert.All(_myRepository.ListCalls(limit: 100), c => Assert.Null(c.MicPath));
    }

    /// <summary>
    /// The transcript pointers point at rows in the OTHER database. Goes red when a merged call
    /// or note carries the archive's row id unmapped — which either points at a stranger's
    /// transcript here or, with foreign keys on, refuses the whole import — or when a verdict
    /// the user gave on the other machine does not arrive.
    /// </summary>
    [Fact]
    public async Task TranscriptPointersAndVerdictsAreRemappedOnTheWayIn()
    {
        // Something already here, so the incoming ids cannot happen to coincide with ours.
        var mine = _myRepository.UpsertContact("Zeynep", CallApp.WhatsApp);
        var myCall = Call(_myRepository, _mine, mine, Only.AddDays(3), ["benim", "satırlarım"]);
        _myRepository.SaveTranscriptVersion(myCall, "large-v3", 0.7, [.. _myRepository.GetSegments(myCall)]);

        var ayse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var theirs = Call(_theirRepository, _theirs, ayse, Only, ["onların", "dökümü", "üç satır"]);

        var lines = _theirRepository.GetSegments(theirs);
        _theirRepository.SaveTranscriptVersion(theirs, "nova-3", 0.9, [.. lines]);
        _theirRepository.SaveTranscriptVersion(theirs, "large-v3", 0.8, [.. lines]);
        _theirRepository.SaveReading(theirs, "{}", "qwen");
        _theirRepository.SaveSummary(new CallSummary { CallId = theirs, Summary = "özet" });
        _theirRepository.InsertAction(new ActionItem { CallId = theirs, ContactId = ayse, Action = "Ara", Quote = "üç satır" });
        _theirRepository.SaveVerdict(new Verdict
        {
            CallId = theirs, Kind = VerdictKind.Flag, QuoteFolded = "uc satir", StartMs = 4000, Value = VerdictValue.Correct,
        });

        var file = Path.Combine(_root, "yedek-v15.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        await _myBackup.ImportAsync(file);

        var imported = _myRepository.ListCalls(limit: 100).Single(c => c.StartedAt == Only);

        // The call shows the transcript it showed over there — the newer of the two — and it is
        // a row of THIS database.
        var current = _myRepository.CurrentTranscriptVersion(imported.Id);
        Assert.NotNull(current);
        Assert.Equal("large-v3", current!.Engine);
        Assert.Equal(2, _myRepository.ListTranscriptVersions(imported.Id).Count);

        // Every note came filed under that transcript, not under a foreign id.
        var freshness = _myRepository.DerivedFreshness(imported.Id);
        Assert.Equal(Staleness.Fresh, freshness.Reading);
        Assert.Equal(Staleness.Fresh, freshness.Summary);
        Assert.Equal(Staleness.Fresh, freshness.Actions);

        // And what the user heard came with the call.
        var verdict = Assert.Single(_myRepository.Verdicts(imported.Id));
        Assert.Equal("uc satir", verdict.QuoteFolded);

        // Ours is untouched.
        Assert.Equal("large-v3", _myRepository.CurrentTranscriptVersion(myCall)!.Engine);
    }

    [Fact]
    public async Task ImportingTheSameFileTwiceChangesNothingTheSecondTime()
    {
        var file = await TheirBackupAsync();

        await _myBackup.ImportAsync(file);
        var again = await _myBackup.ImportAsync(file);

        Assert.Equal(0, again.Calls);
        Assert.Equal(0, again.Contacts);
        Assert.Equal(2, again.AlreadyHere);
        Assert.Equal(2, _myRepository.ListCalls(limit: 100).Count);
    }

    /// <summary>
    /// Goes red when the questions somebody asked, and the answers they paid for, do not survive
    /// the move to another machine — or when the identifiers inside them are copied raw.
    ///
    /// Both halves matter and they fail differently. A call-scoped answer whose call_id was not
    /// remapped points at whatever conversation happens to hold that number here, so a stored
    /// answer appears under a call it was never about. An archive-wide answer has no call at all,
    /// and the filter every other derived table uses would silently drop it.
    /// </summary>
    [Fact]
    public async Task TheQuestionsAndTheirAnsweredQuotesComeAcross()
    {
        var veli = _theirRepository.UpsertContact("Veli", CallApp.WhatsApp);
        var theirCall = Call(_theirRepository, _theirs, veli, Only, ["fiyat on sekiz bin"]);

        var quote = new Core.Analysis.Excerpt(1, theirCall, "Veli", Only, 4000, false, "fiyat on sekiz bin");

        _theirRepository.SaveAskExchange(
            theirCall, veli, "fiyat ne oldu", "On sekiz bin lira.",
            Core.Analysis.StoredExcerpts.Write([quote]), insufficient: false, "test-model");

        _theirRepository.SaveAskExchange(
            callId: null, veli, "arşivde fiyat", "On sekiz bin lira.",
            Core.Analysis.StoredExcerpts.Write([quote]), insufficient: false, "test-model");

        var file = Path.Combine(_root, "sorulu-yedek.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        await _myBackup.ImportAsync(file);

        var here = _myRepository.FindContacts("Veli").Single();
        var mine = _myRepository.ListCalls(here.Id).Single(c => c.StartedAt == Only);

        // The call-scoped one landed on the conversation it was actually about.
        var scoped = Assert.Single(_myRepository.AskExchangesOf(mine.Id));
        Assert.Equal("fiyat ne oldu", scoped.Question);
        Assert.Equal(here.Id, scoped.ContactId);

        // Its quote points at the call as this archive numbers it, so it is still playable.
        var restored = Assert.Single(Core.Analysis.StoredExcerpts.Read(scoped.Citations));
        Assert.Equal(4000, restored.StartMs);

        // And the one that belongs to no conversation came too.
        var wide = Assert.Single(_myRepository.ArchiveAskExchanges());
        Assert.Equal("arşivde fiyat", wide.Question);
        Assert.Equal(here.Id, wide.ContactId);

        // A second import of the same file adds nothing. The call-scoped row is protected by its
        // call already being here; the archive-wide one has no call to protect it and is caught
        // on the question and the instant it was asked.
        await _myBackup.ImportAsync(file);

        Assert.Single(_myRepository.AskExchangesOf(mine.Id));
        Assert.Single(_myRepository.ArchiveAskExchanges());
    }

    /// <summary>Notes carry over, and a note that is already written down does not double.</summary>
    [Fact]
    public async Task NotesAreCarriedOverWithoutDuplicating()
    {
        var file = await TheirBackupAsync();
        _myRepository.AddTodo("Aynı not", null);

        await _myBackup.ImportAsync(file);

        Assert.Single(_myRepository.ListTodos(includeDone: true), t => t.Text == "Aynı not");
    }

    /// <summary>Counters are denormalised, so a merge that does not correct them makes them lie.</summary>
    [Fact]
    public async Task TheContactCountersAgreeWithTheCallsAfterwards()
    {
        var file = await TheirBackupAsync();
        var ayse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        Call(_myRepository, _mine, ayse, Shared.AddDays(3), ["burada olan"]);

        await _myBackup.ImportAsync(file);

        foreach (var name in new[] { "Ayşe", "Veli" })
        {
            var contact = _myRepository.FindContacts(name).Single();

            Assert.Equal(
                _myRepository.ListCalls(contact.Id).Count,
                contact.CallCount);
        }
    }

    [Fact]
    public async Task AFileThatIsNotABackupIsRefusedWithoutTouchingAnything()
    {
        var ayse = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        Call(_myRepository, _mine, ayse, Shared, ["duruyor"]);

        var rubbish = Path.Combine(_root, "degil.zip");
        File.WriteAllText(rubbish, "bu bir yedek değil");

        await Assert.ThrowsAnyAsync<Exception>(() => _myBackup.ImportAsync(rubbish));

        Assert.Single(_myRepository.ListCalls(limit: 100));
    }

    /// <summary>
    /// The three v16 tables travel: the habit cache under the remapped call AND the remapped
    /// transcript, the intent card under the remapped call, and the dictionary row by row with
    /// what is already here winning on the folded stem. Goes red when any of them is left behind,
    /// arrives pointing at a foreign transcript row, or overwrites the user's own dictionary row.
    /// </summary>
    [Fact]
    public async Task HabitsIntentAndTheDictionaryComeWithTheArchive()
    {
        // Something already here, so the incoming ids cannot happen to coincide with ours; and a
        // dictionary row of ours that the archive also has, spelled and listed differently.
        var mine = _myRepository.UpsertContact("Zeynep", CallApp.WhatsApp);
        Call(_myRepository, _mine, mine, Only.AddDays(3), ["benim"]);
        _myRepository.UpsertLexeme(HabitKind.Filler, "yani", [], 0);

        var ayse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var theirs = Call(_theirRepository, _theirs, ayse, Only, ["onların", "dökümü"]);
        _theirRepository.SaveTranscriptVersion(theirs, "nova-3", 0.9, [.. _theirRepository.GetSegments(theirs)]);
        _theirRepository.SaveHabits(theirs, 7, "{\"a\":1}");
        _theirRepository.SaveCallIntent(theirs, "kira rakamını söylemeyeceğim");
        _theirRepository.UpsertLexeme(HabitKind.Filler, "Yani", ["ler"], 5);
        _theirRepository.UpsertLexeme(HabitKind.Filler, "hani", [], 1);

        var file = Path.Combine(_root, "yedek-v16.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        await _myBackup.ImportAsync(file);

        var imported = _myRepository.ListCalls(limit: 100).Single(c => c.StartedAt == Only);

        var habits = _myRepository.GetHabits(imported.Id);
        Assert.NotNull(habits);
        Assert.Equal(7, habits.LexiconVersion);
        Assert.Equal("{\"a\":1}", habits.Json);
        Assert.Equal(_myRepository.CurrentTranscriptVersion(imported.Id)!.Id, habits.TranscriptVersionId);

        Assert.Equal("kira rakamını söylemeyeceğim", _myRepository.GetCallIntent(imported.Id)!.Value.Text);

        var lexicon = _myRepository.Lexicon();
        var ours = Assert.Single(lexicon, l => l.LexemeFolded == "yani");
        Assert.Equal("yani", ours.Lexeme);
        Assert.Empty(ours.Suffixes);
        Assert.Single(lexicon, l => l.LexemeFolded == "hani");
    }

    /// <summary>
    /// The contact card's evidence travels, remapped onto this machine's identifiers.
    ///
    /// Both tables are filed against a person as well as a call, so both have to be remapped
    /// twice; left on the call map alone, a tactic quote would arrive pointing at whichever
    /// contact happened to hold that id here — one person's sentences counted on another's card,
    /// silently, by an operation whose whole purpose is to lose nothing. Goes red also when a
    /// row is left behind entirely, or when the tactic quote arrives filed under a transcript
    /// row belonging to the other database.
    /// </summary>
    [Fact]
    public async Task TheContactCardsEvidenceComesWithTheArchive()
    {
        // Something already here, so the incoming ids cannot happen to coincide with ours.
        var mine = _myRepository.UpsertContact("Zeynep", CallApp.WhatsApp);
        var myCall = Call(_myRepository, _mine, mine, Only.AddDays(3), ["benim", "satırlarım"]);
        _myRepository.SaveTranscriptVersion(myCall, "large-v3", 0.7, [.. _myRepository.GetSegments(myCall)]);

        var ayse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var theirs = Call(_theirRepository, _theirs, ayse, Only, ["onların", "dökümü", "üç satır"]);
        _theirRepository.SaveTranscriptVersion(theirs, "nova-3", 0.9, [.. _theirRepository.GetSegments(theirs)]);

        _theirRepository.ReplaceTacticEvidence(theirs, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence
            {
                CallId = theirs, Tactic = "aciliyet",
                Quote = "bugün karar vermen lazım", QuoteStartMs = 4000, ModelUsed = "qwen",
            },
        ]);

        _theirRepository.ReplaceSpeechActs(theirs,
        [
            new SpeechAct
            {
                CallId = theirs, ByMe = true, Kind = SpeechAct.Kinds.Question,
                AnswerStatus = SpeechAct.Statuses.Evasive,
                Quote = "tarihi netleştirebilir miyiz", QuoteStartMs = 2000,
            },
        ]);

        var file = Path.Combine(_root, "yedek-v17.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        await _myBackup.ImportAsync(file);

        var imported = _myRepository.ListCalls(limit: 100).Single(c => c.StartedAt == Only);
        var contact = _myRepository.FindContacts("Ayşe").Single();

        var tactic = Assert.Single(_myRepository.TacticEvidenceOf(imported.Id));
        Assert.Equal("aciliyet", tactic.Tactic);
        Assert.Equal(contact.Id, tactic.ContactId);
        Assert.Equal(_myRepository.CurrentTranscriptVersion(imported.Id)!.Id, tactic.TranscriptVersionId);

        var question = Assert.Single(_myRepository.SpeechActsOf(imported.Id));
        Assert.Equal(SpeechAct.Statuses.Evasive, question.AnswerStatus);
        Assert.Equal(contact.Id, question.ContactId);

        // And the card can find both under the person they were said to.
        Assert.Equal("aciliyet", Assert.Single(_myRepository.ContactPatterns(contact.Id)).Kind);
        Assert.Equal(1, _myRepository.SpeechActs(contact.Id).CallsMeasured);
    }

    /// <summary>
    /// A consistency run arrives whole: the finding filed under the transcript it was read out
    /// of, and the balancing observations beside the warning.
    ///
    /// The pointer is the part a merge can quietly ruin. Copied raw it names whichever transcript
    /// row happens to hold that id on this machine — a foreign text — and the tab then calls the
    /// finding stale or current entirely by accident. Both answers are wrong, and both are about
    /// an accusation against a person.
    ///
    /// Red also when the observations are dropped on the way in, which would land the imported
    /// conversation in exactly the state the storage was added to end: the accusing half here,
    /// the exonerating half nowhere.
    /// </summary>
    [Fact]
    public async Task AConsistencyRunArrivesWithItsTranscriptPointerAndItsObservations()
    {
        // Ours first, so the incoming transcript ids cannot happen to coincide with ours.
        var mine = _myRepository.UpsertContact("Zeynep", CallApp.WhatsApp);
        var myCall = Call(_myRepository, _mine, mine, Only.AddDays(3), ["benim", "satırlarım"]);
        _myRepository.SaveTranscriptVersion(myCall, "large-v3", 0.7, [.. _myRepository.GetSegments(myCall)]);

        var ayse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var theirs = Call(_theirRepository, _theirs, ayse, Only, ["onların", "dökümü", "üç satır"]);
        _theirRepository.SaveTranscriptVersion(theirs, "nova-3", 0.9, [.. _theirRepository.GetSegments(theirs)]);

        _theirRepository.InsertFlag(new Flag
        {
            CallId = theirs,
            ContactId = ayse,
            Kind = FlagKind.Contradiction,
            Summary = "Rakam değişti",
            Quote = "onların dökümü",
            Source = Flag.Sources.Consistency,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        _theirRepository.SaveConsistencyNote(
            theirs, "Rakamı yazılı iste.", "qwen", ["Tarihler baştan sona tutarlı"]);

        var file = Path.Combine(_root, "yedek-v21.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);
        await _myBackup.ImportAsync(file);

        var imported = _myRepository.ListCalls(limit: 100).Single(c => c.StartedAt == Only);

        var finding = Assert.Single(_myRepository.FlagsOf(imported.Id));
        Assert.Equal(Flag.Sources.Consistency, finding.Source);
        Assert.Equal(_myRepository.CurrentTranscriptVersion(imported.Id)!.Id, finding.TranscriptVersionId);

        // Which is what lets the imported conversation be judged at all.
        Assert.Equal(Staleness.Fresh, _myRepository.DerivedFreshness(imported.Id).Consistency);

        var note = _myRepository.GetConsistencyNote(imported.Id);
        Assert.NotNull(note);
        Assert.Equal("Tarihler baştan sona tutarlı", Assert.Single(note.Observations!));
    }

    /// <summary>
    /// The model's reading of a person travels, remapped onto this machine's identifiers, and a
    /// reading already here is left alone.
    ///
    /// It hangs off a contact and points at the newest call it covered, so both identifiers have
    /// to be rewritten — left raw, the pointer would name whatever conversation happens to hold
    /// that id here. Goes red also when the incoming copy overwrites one of ours, which would
    /// throw away a [Katılmıyorum] the user pressed, and with it the measurement that decides
    /// whether the feature stays switched on.
    /// </summary>
    [Fact]
    public async Task TheModelsReadingOfAPersonComesWithTheArchive()
    {
        // The same person on both machines, each with a reading. The archive's is written first,
        // so this machine's is the newer of the two and the one the card must go on showing.
        var ayse = _theirRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        var theirCall = Call(_theirRepository, _theirs, ayse, Only, ["onların", "dökümü"]);

        _theirRepository.SaveContactReading(
            ayse, """{"CounterReading":"gelen okuma"}""", "bulut-model", 9, theirCall, "gelendeki", 60, 2);

        var here = _myRepository.UpsertContact("Ayşe", CallApp.WhatsApp);
        Call(_myRepository, _mine, here, Only.AddDays(3), ["benim", "satırlarım"]);

        var kept = _myRepository.SaveContactReading(
            here, """{"CounterReading":"burada duran okuma"}""", "yerel-model", 5, null, "buradaki", 40, 1);
        _myRepository.SetContactReadingVerdict(kept, ContactReadingAnalysis.Disagree);

        var file = Path.Combine(_root, "yedek-v19.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        await _myBackup.ImportAsync(file);

        var contact = _myRepository.FindContacts("Ayşe").Single();
        var imported = _myRepository.ListCalls(limit: 100).Single(c => c.StartedAt == Only);

        // Both are here, and ours — the newest, and the one carrying the user's verdict — is what
        // the card reads.
        var newest = _myRepository.LatestContactReading(contact.Id)!;
        Assert.Equal(kept, newest.Id);
        Assert.Equal(ContactReadingAnalysis.Disagree, newest.UserVerdict);

        // And the incoming one arrived pointing at the conversation it actually covered here.
        var verdicts = _myRepository.RecentContactReadingVerdicts(limit: 10);
        Assert.Single(verdicts);   // one row per person, and there is one person

        using var connection = new Database(_mine.DatabaseFile).Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT latest_call_id FROM contact_reading WHERE input_hash = 'gelendeki';";
        Assert.Equal(imported.Id, Convert.ToInt64(command.ExecuteScalar()));
    }

    /// <summary>
    /// Puts the database of an older build back where it belongs, and goes red the day it stops
    /// working.
    ///
    /// This is the promise the product makes to anyone who used the version before this one: the
    /// conversations you already recorded are not stranded. Every other test here builds its
    /// incoming archive at the current schema, so nothing held that promise; this one populates
    /// an archive and THEN pushes it back to 14 — dropping the tables the later steps own and
    /// rewinding the stored version — which is the shape a real old backup arrives in.
    ///
    /// What it pins is the user-visible outcome, not one mechanism, and deliberately so: the
    /// import is backward compatible twice over. <see cref="BackupService.ImportAsync"/> migrates
    /// the unpacked COPY before the merge reads it, and <c>Copy</c> builds its column list from
    /// the intersection of both databases, so a table or a column only one side knows about is
    /// left behind rather than failing the import. Commenting either one out on its own leaves
    /// this test green. That is belt and braces working as intended — but it means this test is
    /// a guard on the promise, and whoever removes the second mechanism must not read its green
    /// as proof that the first one is doing the work.
    /// </summary>
    [Fact]
    public async Task ABackupFromAnOlderBuildIsBroughtForwardRatherThanRefused()
    {
        var kemal = _theirRepository.UpsertContact("Kemal", CallApp.WhatsApp);
        var call = Call(_theirRepository, _theirs, kemal, Only, ["eski yedekten", "iki satır"]);

        _theirRepository.InsertCommitment(new Commitment
        {
            CallId = call,
            ContactId = kemal,
            ByMe = true,
            Quote = "yarın gönderirim",
            Obligation = "evrağı göndermek",
        });

        // Back to 14: drop what v15 and after introduced, then rewind the recorded version. The
        // ALTERs of v15 are idempotent, so the columns they add may stay; what must be re-created
        // is every table the later steps own, and that is what proves they run.
        using (var connection = new Database(_theirs.DatabaseFile).Open())
        {
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText =
                """
                DROP TABLE IF EXISTS verdict;
                DROP TABLE IF EXISTS speech_habit;
                DROP TABLE IF EXISTS habit_lexicon;
                DROP TABLE IF EXISTS call_intent;
                DROP TABLE IF EXISTS tactic_evidence;
                DROP TABLE IF EXISTS speech_act;
                DROP TABLE IF EXISTS prosody;
                DROP TABLE IF EXISTS audio_event;
                DROP TABLE IF EXISTS contact_reading;
                UPDATE setting SET value = '14' WHERE key = 'schema_version';
                """;
            downgrade.ExecuteNonQuery();
        }

        new Database(_theirs.DatabaseFile).ClearPool();

        var file = Path.Combine(_root, "eski-yedek.zip");
        await _theirBackup.BackupAsync(file, includeAudio: false);

        var result = await _myBackup.ImportAsync(file);

        // It arrived. Refusing an old backup, or importing it half-shaped, would both be worse
        // than saying so out loud — and neither happens.
        Assert.Equal(1, result.Calls);
        Assert.Equal(2, result.Segments);

        var imported = _myRepository.ListCalls(limit: 100).Single(c => c.StartedAt == Only);
        Assert.Equal(["eski yedekten", "iki satır"], _myRepository.GetSegments(imported.Id).Select(s => s.Text));

        var promise = _myRepository.PromiseLedger(includeClosed: true)
            .Single(p => p.Commitment.CallId == imported.Id)
            .Commitment;

        Assert.True(promise.ByMe);
        Assert.Equal("evrağı göndermek", promise.Obligation);

        // And the archive this installation keeps is still whole: the merge did not leave the
        // incoming file's older shape behind in it.
        using var connection2 = new Database(_mine.DatabaseFile).Open();
        Assert.Equal(Schema.Version, Database.StoredVersion(connection2));
    }
}
