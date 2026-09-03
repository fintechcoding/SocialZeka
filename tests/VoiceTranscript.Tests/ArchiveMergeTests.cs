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
}
