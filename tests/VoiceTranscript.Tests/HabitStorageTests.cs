using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The three v16 tables through the repository: the habit cache (the machine's), the dictionary
/// and the intent card (the user's).
///
/// The cache carries which transcript and which dictionary it was counted from, so a screen can
/// say "bayat" honestly; the series is one SELECT with the filters the mirror page has; and the
/// user's two tables survive every re-run and every wipe of the machine's output.
/// </summary>
public sealed class HabitStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-habit-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly long _contact;
    private readonly long _call;

    private static readonly DateTimeOffset June = DateTimeOffset.Parse("2026-06-10T10:00:00+03:00");

    public HabitStorageTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);

        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
        _call = Call(_contact, June);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private long Call(long contact, DateTimeOffset at) => _repo.InsertCall(new Call
    {
        ContactId = contact, App = CallApp.WhatsApp, StartedAt = at, Duration = TimeSpan.FromMinutes(5), State = ProcessingState.Analysed,
    });

    private long Transcribe(long call, string engine)
    {
        List<Segment> lines = [new() { CallId = call, IsMe = true, StartMs = 0, EndMs = 2000, Text = "Alo" }];
        _repo.ReplaceSegments(call, lines);
        return _repo.SaveTranscriptVersion(call, engine, 0.8, lines);
    }

    /// <summary>Goes red when a second save sits beside the first, or when the row is not filed under the transcript on screen.</summary>
    [Fact]
    public void SavingTwiceKeepsOneRowUnderTheTranscriptOnScreen()
    {
        var version = Transcribe(_call, "large-v3");

        _repo.SaveHabits(_call, 11, "{\"a\":1}");
        _repo.SaveHabits(_call, 12, "{\"a\":2}");

        var stored = _repo.GetHabits(_call);
        Assert.NotNull(stored);
        Assert.Equal((12, "{\"a\":2}", version), (stored.LexiconVersion, stored.Json, stored.TranscriptVersionId));
        Assert.NotEqual(default, stored.CreatedAt);

        Assert.Single(_repo.HabitSeries(new DateOnly(2026, 1, 1)));
    }

    /// <summary>
    /// The staleness signal. Goes red when transcribing again silently keeps the counts current:
    /// the pointer must now differ from the call's, which is what the screen reads as "bayat".
    /// </summary>
    [Fact]
    public void TranscribingAgainLeavesTheCountsUnderTheOldTranscript()
    {
        var old = Transcribe(_call, "large-v3");
        _repo.SaveHabits(_call, 1, "{}");

        var current = Transcribe(_call, "nova-3");

        Assert.Equal(old, _repo.GetHabits(_call)!.TranscriptVersionId);
        Assert.NotEqual(current, _repo.GetHabits(_call)!.TranscriptVersionId);

        // And the series still names the engine the counts came from.
        Assert.Equal("large-v3", Assert.Single(_repo.HabitSeries(new DateOnly(2026, 1, 1))).Engine);
    }

    /// <summary>A call that never recorded which transcript it shows gives a null pointer: "bilinmiyor", not a constraint failure.</summary>
    [Fact]
    public void CountsWithoutATranscriptPointerAreNullNotAnError()
    {
        _repo.SaveHabits(_call, 1, "{}");

        Assert.Null(_repo.GetHabits(_call)!.TranscriptVersionId);
        Assert.Null(Assert.Single(_repo.HabitSeries(new DateOnly(2026, 1, 1))).Engine);
        Assert.Null(_repo.GetHabits(_call + 100));
    }

    /// <summary>The three filters the mirror page has, and the order it draws in.</summary>
    [Fact]
    public void TheSeriesFiltersBySinceContactAndEngine()
    {
        var other = _repo.UpsertContact("Ayşe", CallApp.WhatsApp);

        var august = Call(other, June.AddMonths(2));
        var september = Call(_contact, June.AddMonths(3));

        Transcribe(_call, "large-v3");
        Transcribe(august, "nova-3");
        Transcribe(september, "nova-3");

        _repo.SaveHabits(_call, 1, "{\"m\":6}");
        _repo.SaveHabits(august, 1, "{\"m\":8}");
        _repo.SaveHabits(september, 1, "{\"m\":9}");

        Assert.Equal([_call, august, september], _repo.HabitSeries(new DateOnly(2026, 1, 1)).Select(r => r.CallId));
        Assert.Equal([august, september], _repo.HabitSeries(new DateOnly(2026, 7, 1)).Select(r => r.CallId));
        Assert.Equal([_call, september], _repo.HabitSeries(new DateOnly(2026, 1, 1), contactId: _contact).Select(r => r.CallId));
        Assert.Equal([august, september], _repo.HabitSeries(new DateOnly(2026, 1, 1), engine: "nova-3").Select(r => r.CallId));
        Assert.Equal([september], _repo.HabitSeries(new DateOnly(2026, 7, 1), contactId: _contact, engine: "nova-3").Select(r => r.CallId));

        var row = _repo.HabitSeries(new DateOnly(2026, 9, 1)).Single();
        Assert.Equal((_contact, "nova-3", "{\"m\":9}", 1), (row.ContactId, row.Engine, row.Json, row.LexiconVersion));
        Assert.Equal(June.AddMonths(3), row.StartedAt);
        Assert.False(row.LikelyNoHeadphones);
    }

    /// <summary>
    /// The rule that makes the user's tables worth anything. Goes red when a re-analysis, a
    /// consistency re-run or a suggestion sweep deletes the counts, the dictionary or the intent.
    /// </summary>
    [Fact]
    public void ReRunsTouchNoneOfTheThree()
    {
        _repo.SaveHabits(_call, 1, "{}");
        _repo.UpsertLexeme(HabitKind.Filler, "yani");
        _repo.SaveCallIntent(_call, "kira rakamını söylemeyeceğim");

        _repo.ClearAnalysis(_call);
        _repo.ClearConsistency(_call);
        _repo.ClearOpenActions(_call);
        _repo.SweepLedger();

        Assert.NotNull(_repo.GetHabits(_call));
        Assert.Single(_repo.Lexicon());
        Assert.NotNull(_repo.GetCallIntent(_call));
    }

    /// <summary>Goes red when the cache or the intent outlives its call (cascade), or when the dictionary goes with it (it must not — it is not the call's).</summary>
    [Fact]
    public void DeletingTheCallTakesItsCountsAndIntentButNotTheDictionary()
    {
        _repo.SaveHabits(_call, 1, "{}");
        _repo.SaveCallIntent(_call, "not");
        _repo.UpsertLexeme(HabitKind.Filler, "yani");

        _repo.DeleteCall(_call);

        Assert.Null(_repo.GetHabits(_call));
        Assert.Null(_repo.GetCallIntent(_call));
        Assert.Single(_repo.Lexicon());
    }

    /// <summary>
    /// Identity is the kind and the folded stem, and the endings are folded on the way in —
    /// the matcher compares them against folded text, and an ending stored with a Turkish
    /// letter in it would never match anything.
    /// </summary>
    [Fact]
    public void TheDictionaryFoldsItsKeysAndEndings()
    {
        var id = _repo.UpsertLexeme(HabitKind.Filler, "Şey", ["Ler", "İ", ""], 3);
        var again = _repo.UpsertLexeme(HabitKind.Filler, "şey", ["i"], 4);

        Assert.Equal(id, again);

        var row = Assert.Single(_repo.Lexicon());
        Assert.Equal(("dolgu", "şey", "sey", 4), (row.Kind, row.Lexeme, row.LexemeFolded, row.Position));
        Assert.Equal(["i"], row.Suffixes);

        // The same stem under another kind is another row: an exclusion beside a swear word.
        _repo.UpsertLexeme(HabitKind.Exclusion, "şey");
        Assert.Equal(2, _repo.Lexicon().Count);

        _repo.DeleteLexeme(id);
        Assert.Equal(HabitKind.Exclusion, Assert.Single(_repo.Lexicon()).Kind);

        Assert.Throws<ArgumentException>(() => _repo.UpsertLexeme(HabitKind.Filler, "   "));
    }

    /// <summary>Rows come back in the user's order within each kind, bare stems with an empty ending list.</summary>
    [Fact]
    public void TheDictionaryKeepsTheUsersOrder()
    {
        _repo.UpsertLexeme(HabitKind.Filler, "işte", position: 2);
        _repo.UpsertLexeme(HabitKind.Filler, "yani", position: 0);
        _repo.UpsertLexeme(HabitKind.Profanity, "lan", position: 5);

        var rows = _repo.Lexicon();

        Assert.Equal(["yani", "işte", "lan"], rows.Select(r => r.Lexeme));
        Assert.All(rows, r => Assert.Empty(r.Suffixes));
        Assert.All(rows, r => Assert.True(r.Id > 0));
    }

    [Fact]
    public void TheIntentIsSavedReplacedAndClearedByBlankText()
    {
        Assert.Null(_repo.GetCallIntent(_call));

        _repo.SaveCallIntent(_call, "  kira rakamını söylemeyeceğim  ");
        var first = _repo.GetCallIntent(_call);
        Assert.Equal("kira rakamını söylemeyeceğim", first!.Value.Text);
        Assert.NotEqual(default, first.Value.UpdatedAt);

        _repo.SaveCallIntent(_call, "sadece dinleyeceğim");
        Assert.Equal("sadece dinleyeceğim", _repo.GetCallIntent(_call)!.Value.Text);

        _repo.SaveCallIntent(_call, "   ");
        Assert.Null(_repo.GetCallIntent(_call));

        _repo.SaveCallIntent(_call, "yine");
        _repo.DeleteCallIntent(_call);
        Assert.Null(_repo.GetCallIntent(_call));
    }
}
