using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// A real database on disk, for the tests below that drive the same calls the two coaching
/// windows and the orchestrator make.
/// </summary>
public abstract class CoachingFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-kocluk-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;

    protected readonly Repository Repo;

    protected CoachingFixture()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        Repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    protected long NewCall(DateTimeOffset? at = null) => Repo.InsertCall(new Call
    {
        App = CallApp.WhatsApp,
        StartedAt = at ?? DateTimeOffset.Parse("2026-06-10T10:00:00+03:00"),
        Duration = TimeSpan.FromMinutes(5),
        State = ProcessingState.Analysed,
    });

    /// <summary>Writes lines and files them as a transcript version, the way the transcription tail does.</summary>
    protected long Transcribe(long call, string engine, params Segment[] lines)
    {
        Repo.ReplaceSegments(call, lines);
        return Repo.SaveTranscriptVersion(call, engine, 0.9, Repo.GetSegments(call));
    }

    protected static Segment Mine(long call, int start, int end, string text) => new()
    {
        CallId = call, IsMe = true, StartMs = start, EndMs = end, Text = text,
    };
}

/// <summary>
/// The dictionary as the Sözlük window edits it, through the repository and back out as a matcher.
///
/// The window writes with UpsertLexeme and DeleteLexeme and nothing else; what these pin is that
/// an edit made that way is visible to the next count, and visible as a CHANGED VERSION — which
/// is the only signal anything has that stored counts need redoing. They are the repository half
/// of the rules HabitLexiconTests pins in memory.
/// </summary>
public sealed class HabitLexiconEditTests : CoachingFixture
{
    /// <summary>
    /// Goes red when an added stem leaves the version alone: every stored report would then go on
    /// looking current, and a word the user added would never be counted in anything already
    /// filed. The version is the whole staleness mechanism for the dictionary.
    /// </summary>
    [Fact]
    public void AddingAStemChangesTheVersion()
    {
        HabitLexicon.Seed(Repo);
        var before = HabitLexicon.Load(Repo).LexiconVersion;

        Repo.UpsertLexeme(HabitKind.Filler, "höyle", ["si"]);
        var after = HabitLexicon.Load(Repo).LexiconVersion;

        Assert.NotEqual(before, after);

        // And so does changing the endings of a stem that is already there, because that changes
        // what matches — an ending list is not decoration.
        Repo.UpsertLexeme(HabitKind.Filler, "höyle", ["si", "ce"]);
        Assert.NotEqual(after, HabitLexicon.Load(Repo).LexiconVersion);
    }

    /// <summary>
    /// Goes red when deleting a row leaves its hits behind — the user took the word out of the
    /// list and the next count still finds it, which makes the window a lie.
    /// </summary>
    [Fact]
    public void DeletingAStemRemovesItsHits()
    {
        var id = Repo.UpsertLexeme(HabitKind.Filler, "höyle", ["si"]);
        Repo.UpsertLexeme(HabitKind.Filler, "böyle");

        // Folded first, as the counters hand it over: the stored stems are folded, so a line with
        // its Turkish letters intact would match nothing and the test would pass for the wrong
        // reason after the delete.
        var line = TurkishText.NormalizeForSearch("höylesi böyle");

        var before = HabitLexicon.Load(Repo);
        Assert.Equal(2, before.Matches(line).Count);

        Repo.DeleteLexeme(id);

        var after = HabitLexicon.Load(Repo);
        Assert.Equal("böyle", Assert.Single(after.Matches(line)).Lexeme);
        Assert.Equal(1, after.CountedRows);
    }

    /// <summary>
    /// Goes red when a start puts back what the user pruned.
    ///
    /// The seed writes into an EMPTY table only. An edited table is the user's, and a seeding that
    /// topped it up would resurrect every deleted word on the next launch — the one behaviour that
    /// would make the window pointless, because nothing the user removes would stay removed.
    /// </summary>
    [Fact]
    public void TheSeedIsNotReappliedOverAnEditedTable()
    {
        var seeded = HabitLexicon.Seed(Repo);
        Assert.True(seeded > 0);

        var pruned = Repo.Lexicon()[0];
        Repo.DeleteLexeme(pruned.Id);
        Repo.UpsertLexeme(HabitKind.Exclusion, "höyle");

        var mine = Repo.Lexicon().Select(r => (r.Kind, r.Lexeme)).ToList();

        Assert.Equal(0, HabitLexicon.Seed(Repo));
        Assert.Equal(0, HabitLexicon.Seed(Repo));

        Assert.Equal(mine, Repo.Lexicon().Select(r => (r.Kind, r.Lexeme)));
        Assert.DoesNotContain(Repo.Lexicon(), r => r.Kind == pruned.Kind && r.Lexeme == pruned.Lexeme);
    }
}

/// <summary>
/// The intent card's row, through the calls the Niyet window makes.
///
/// Three buttons, three outcomes: Kaydet writes, Kaydet with an emptied box removes (which is why
/// the window has no separate "are you sure"), Kaldır removes. And the row is the user's, so
/// nothing the machine re-runs may touch it — that last one is the rule the whole table exists
/// for, and it goes red the moment somebody adds the intent to a clear-down.
/// </summary>
public sealed class CallIntentTests : CoachingFixture
{
    [Fact]
    public void SavingOverwritesAndABlankBoxRemoves()
    {
        var call = NewCall();

        Assert.Null(Repo.GetCallIntent(call));

        Repo.SaveCallIntent(call, "  rakamı ben söylemeyeceğim  ");
        Assert.Equal("rakamı ben söylemeyeceğim", Repo.GetCallIntent(call)!.Value.Text);

        Repo.SaveCallIntent(call, "yalnız dinleyeceğim");
        Assert.Equal("yalnız dinleyeceğim", Repo.GetCallIntent(call)!.Value.Text);

        // What the window's Kaydet does when the user has emptied the box: an empty card is no
        // card, so there is no row rather than a row holding "".
        Repo.SaveCallIntent(call, "   ");
        Assert.Null(Repo.GetCallIntent(call));

        // And the Kaldır button, on a card that is there.
        Repo.SaveCallIntent(call, "yine");
        Repo.DeleteCallIntent(call);
        Assert.Null(Repo.GetCallIntent(call));
    }

    /// <summary>
    /// Goes red when a re-analysis, a consistency re-run or a recount erases what the user wrote
    /// before the call. The pipeline does not write to the user's tables, and it does not clear
    /// them either.
    /// </summary>
    [Fact]
    public void ClearingTheAnalysisAndRecountingLeaveTheIntentAlone()
    {
        var call = NewCall();
        Transcribe(call, "large-v3", Mine(call, 0, 2000, "alo"));

        Repo.SaveCallIntent(call, "rakamı ben söylemeyeceğim");

        Repo.ClearAnalysis(call);
        Repo.ClearConsistency(call);
        HabitCounter.CountIfStale(Repo, call);

        Assert.Equal("rakamı ben söylemeyeceğim", Repo.GetCallIntent(call)!.Value.Text);
    }

    /// <summary>Each conversation has its own note; writing one must not disturb another's.</summary>
    [Fact]
    public void TwoConversationsKeepTheirOwnNotes()
    {
        var first = NewCall();
        var second = NewCall(DateTimeOffset.Parse("2026-06-11T10:00:00+03:00"));

        Repo.SaveCallIntent(first, "birinci");
        Repo.SaveCallIntent(second, "ikinci");
        Repo.DeleteCallIntent(first);

        Assert.Null(Repo.GetCallIntent(first));
        Assert.Equal("ikinci", Repo.GetCallIntent(second)!.Value.Text);
    }
}

/// <summary>
/// What the orchestrator does after a transcript is replaced, tested where it can be reached.
///
/// The orchestrator itself is not constructible here — it opens capture devices and a Python
/// worker — so the arithmetic and the staleness rule live in <see cref="HabitCounter"/> and the
/// orchestrator's tail is one call to it. These pin that call's contract: a conversation that has
/// just been transcribed ends up with a stored snapshot whose figures are exactly what
/// <see cref="SpeechHabits.Count"/> gives for the same lines, and a conversation whose transcript
/// or dictionary has moved on is counted again rather than left showing old numbers.
/// </summary>
public sealed class HabitSnapshotCountingTests : CoachingFixture
{
    private long Transcribed(out long call)
    {
        call = NewCall();

        return Transcribe(
            call, "large-v3",
            Mine(call, 0, 4000, "yani tamam yani gittik"),
            Mine(call, 4000, 9000, "işte öyle oldu"),
            new Segment { CallId = call, IsMe = false, StartMs = 9000, EndMs = 12000, Text = "peki" });
    }

    /// <summary>
    /// Goes red when the stored figures drift from the counter — a cache that says something the
    /// live count does not is worse than no cache, because every screen believes it.
    /// </summary>
    [Fact]
    public void ATranscribedCallEndsUpWithASnapshotMatchingTheCounter()
    {
        Repo.UpsertLexeme(HabitKind.Filler, "yani");
        Repo.UpsertLexeme(HabitKind.Filler, "işte");

        Transcribed(out var call);

        Assert.True(HabitCounter.CountIfStale(Repo, call));

        var lexicon = HabitLexicon.Load(Repo);
        var segments = Repo.GetSegments(call);
        var expected = SpeechHabits.Count(segments, lexicon, wordThreshold: null, Repo.Verdicts(call));
        var expectedTalk = TalkStats.Compute(segments);

        var stored = Repo.GetHabits(call);
        Assert.NotNull(stored);
        Assert.Equal(lexicon.LexiconVersion, stored.LexiconVersion);

        var snapshot = HabitSnapshot.FromJson(stored.Json);
        Assert.NotNull(snapshot);

        Assert.Equal(
            expected.CountOf(HabitKind.Filler).Certain,
            snapshot.Habits.CountOf(HabitKind.Filler).Certain);

        Assert.Equal(3, snapshot.Habits.CountOf(HabitKind.Filler).Certain);
        Assert.Equal(expected.MyLines, snapshot.Habits.MyLines);
        Assert.Equal(expected.MyWords, snapshot.Habits.MyWords);
        Assert.Equal(expected.MySpokenMs, snapshot.Habits.MySpokenMs);
        Assert.Equal(expected.Moments.Select(m => m.StartMs), snapshot.Habits.Moments.Select(m => m.StartMs));
        Assert.Equal(expectedTalk.MineMs, snapshot.Talk.MineMs);
        Assert.Equal(expectedTalk.TheirsMs, snapshot.Talk.TheirsMs);
    }

    /// <summary>
    /// Counting twice over an unchanged call does nothing the second time: the tail runs on every
    /// transcription, and re-writing an identical row on every pass would churn the cache and
    /// move its timestamp for no reason.
    /// </summary>
    [Fact]
    public void CountingAgainWithNothingChangedIsSkipped()
    {
        Repo.UpsertLexeme(HabitKind.Filler, "yani");
        Transcribed(out var call);

        Assert.True(HabitCounter.CountIfStale(Repo, call));
        Assert.False(HabitCounter.CountIfStale(Repo, call));
    }

    /// <summary>
    /// The two things that make a stored count stale, one at a time. Goes red when an edited
    /// dictionary or a re-transcription leaves last week's numbers standing.
    /// </summary>
    [Fact]
    public void ANewTranscriptOrAnEditedDictionaryForcesARecount()
    {
        Repo.UpsertLexeme(HabitKind.Filler, "yani");
        Transcribed(out var call);

        HabitCounter.CountIfStale(Repo, call);
        var first = Repo.GetHabits(call)!.LexiconVersion;

        // The dictionary moves.
        Repo.UpsertLexeme(HabitKind.Filler, "işte");
        Assert.True(HabitCounter.CountIfStale(Repo, call));
        Assert.NotEqual(first, Repo.GetHabits(call)!.LexiconVersion);
        Assert.False(HabitCounter.CountIfStale(Repo, call));

        // The transcript moves.
        Transcribe(call, "nova-3", Mine(call, 0, 3000, "yani işte yani"));
        Assert.True(HabitCounter.CountIfStale(Repo, call));

        var snapshot = HabitSnapshot.FromJson(Repo.GetHabits(call)!.Json);
        Assert.Equal(3, snapshot!.Habits.CountOf(HabitKind.Filler).Certain);
    }

    /// <summary>
    /// What the archive sweep works through, and what it must leave alone.
    ///
    /// Goes red two ways, and the second one is the reason it exists: a recording that was never
    /// transcribed appearing on the list would mean the button never finishes — the sweep would
    /// report the same number of "counted" conversations on a fully up-to-date archive, for ever.
    /// </summary>
    [Fact]
    public void TheArchiveSweepListsOnlyTranscribedCallsThatNeedCounting()
    {
        Repo.UpsertLexeme(HabitKind.Filler, "yani");

        Transcribed(out var counted);

        var recorded = Repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse("2026-06-12T10:00:00+03:00"),
            State = ProcessingState.Recorded,
        });

        var version = HabitLexicon.Load(Repo).LexiconVersion;

        Assert.Contains(counted, HabitCounter.NeedingCount(Repo, version));
        Assert.DoesNotContain(recorded, HabitCounter.NeedingCount(Repo, version));

        foreach (var id in HabitCounter.NeedingCount(Repo, version))
            HabitCounter.Count(Repo, id, HabitLexicon.Load(Repo));

        Assert.Empty(HabitCounter.NeedingCount(Repo, version));
        Assert.Null(Repo.GetHabits(recorded));
    }

    /// <summary>Goes red when a call with no lines throws instead of being skipped — the sweep would stop at the first empty row.</summary>
    [Fact]
    public void ACallWithNoLinesIsSkippedRatherThanFailing()
    {
        var empty = NewCall();

        Assert.False(HabitCounter.CountIfStale(Repo, empty));
        Assert.Null(Repo.GetHabits(empty));
    }
}
