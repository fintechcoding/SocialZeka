using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The call window's Aynam tab: one conversation's counts, where they came from, and what the
/// user can do to them.
///
/// The tab reads the STORED snapshot rather than counting on every open, so these pin the three
/// ways that could go wrong: figures that drift from what was filed, a re-transcription that
/// leaves the old counts standing with nothing saying so, and a recount that does not actually
/// rewrite them. The fourth is the product rule — the other party is never counted — which is
/// enforced in the counter and asserted here because it is the promise the tab makes in words.
/// </summary>
public sealed class CallWindowMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-cw-aynam-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly HttpClient _http = new();
    private readonly long _contact;

    /// <summary>A stem invented for the test. The real ones live in the dictionary and nowhere else.</summary>
    private const string Swear = "zaark";

    public CallWindowMirrorTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);

        _repo.UpsertLexeme(HabitKind.Profanity, Swear, ["dim"]);

        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
    }

    public void Dispose()
    {
        _http.Dispose();
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private long Seed(string engine, string[] mine, string[]? theirs = null)
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact, App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddDays(-1),
            Duration = TimeSpan.FromMinutes(2), State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);
        Transcribe(call, engine, mine, theirs);
        HabitRecount.Run(_repo, call);

        return call;
    }

    private void Transcribe(long call, string engine, string[] mine, string[]? theirs = null)
    {
        var lines = new List<Segment>();

        for (var i = 0; i < mine.Length; i++)
        {
            lines.Add(new Segment
            {
                CallId = call, IsMe = true,
                StartMs = i * 12000, EndMs = i * 12000 + 6000, Text = mine[i],
            });
        }

        for (var i = 0; i < (theirs?.Length ?? 0); i++)
        {
            lines.Add(new Segment
            {
                CallId = call, IsMe = false,
                StartMs = i * 12000 + 6000, EndMs = i * 12000 + 12000, Text = theirs![i],
            });
        }

        _repo.ReplaceSegments(call, lines);
        _repo.SaveTranscriptVersion(call, engine, 0.95, lines);
    }

    private CallWindowViewModel Window(long callId)
    {
        var settings = new AppSettings();
        return new CallWindowViewModel(_repo, () => settings, _http, callId);
    }

    /// <summary>
    /// Goes red when the tab counts for itself instead of reading what was filed. The two would
    /// then disagree the moment the dictionary changed, and the stamp under the figures — which
    /// names the day and the transcript they came from — would be describing something else.
    /// </summary>
    [Fact]
    public void TheFiguresComeFromTheStoredSnapshot()
    {
        var call = Seed("nova-3", [$"{Swear} dedim", $"{Swear} yine"], ["Anladım."]);

        using var model = Window(call);

        Assert.True(model.HasHabits);
        Assert.Equal(2, model.Habits!.Habits.CountOf(HabitKind.Profanity).Certain);
        Assert.Equal(2, model.HabitMoments.Count);
        Assert.NotNull(model.HabitLine);
        Assert.NotNull(model.HabitStamp);

        // The stamp names the transcript the counts were made from.
        Assert.Contains("nova-3", model.HabitStamp);

        // And the same figures the stored snapshot holds, not a second computation of them.
        var stored = HabitSnapshot.FromJson(_repo.GetHabits(call)!.Json)!;
        Assert.Equal(stored.Habits.CountOf(HabitKind.Profanity).Certain,
            model.Habits.Habits.CountOf(HabitKind.Profanity).Certain);
        Assert.Equal(stored.Talk.MineMs, model.Habits.Talk.MineMs);
    }

    /// <summary>
    /// Goes red when transcribing again leaves the old counts standing with nothing on the tab
    /// saying so — complaint 7, in the one place the numbers look most like facts.
    /// </summary>
    [Fact]
    public void ARetranscriptionMarksTheCountsStale()
    {
        var call = Seed("nova-3", [$"{Swear} dedim"]);

        using var fresh = Window(call);
        Assert.False(fresh.IsHabitsStale);

        Transcribe(call, "large-v3", [$"{Swear} dedim", $"{Swear} yine"]);

        using var stale = Window(call);

        Assert.True(stale.IsHabitsStale);

        // The counts are not silently corrected either: they still say what the old text said.
        Assert.Equal(1, stale.Habits!.Habits.CountOf(HabitKind.Profanity).Certain);
    }

    /// <summary>Goes red when "Yeniden say" leaves the stored counts alone, or leaves the stale bar up.</summary>
    [Fact]
    public void RecountRewritesTheStoredCounts()
    {
        var call = Seed("nova-3", [$"{Swear} dedim"]);
        Transcribe(call, "large-v3", [$"{Swear} dedim", $"{Swear} yine"]);

        using var model = Window(call);
        Assert.True(model.IsHabitsStale);

        model.RecountCommand.Execute(null);

        Assert.False(model.IsHabitsStale);
        Assert.Equal(2, model.Habits!.Habits.CountOf(HabitKind.Profanity).Certain);
        Assert.Equal(2, model.HabitMoments.Count);

        // Written through, not held in the window: the mirror page reads the same row.
        var stored = HabitSnapshot.FromJson(_repo.GetHabits(call)!.Json)!;
        Assert.Equal(2, stored.Habits.CountOf(HabitKind.Profanity).Certain);
    }

    /// <summary>
    /// Goes red the day the other party starts being counted. The tab says in words that only
    /// the user's lines are counted, and a product that measured somebody who never agreed to be
    /// measured would be a different product.
    /// </summary>
    [Fact]
    public void TheOtherPartyIsNeverCounted()
    {
        var call = Seed("nova-3", ["Sakin konuştum"], [$"{Swear} dedi bana", $"{Swear} yine"]);

        using var model = Window(call);

        Assert.Equal(0, model.Habits!.Habits.CountOf(HabitKind.Profanity).Certain);
        Assert.Empty(model.HabitMoments);
    }

    /// <summary>
    /// Goes red when a ruling made on the tab does not reach the figures — the same rule the
    /// mirror page relies on, applied by recounting rather than by the screen filtering rows.
    /// </summary>
    [Fact]
    public void AMishearingRuledHereLeavesTheFigures()
    {
        var call = Seed("nova-3", [$"{Swear} dedim", $"{Swear} yine"]);

        using var model = Window(call);
        Assert.Equal(2, model.Habits!.Habits.CountOf(HabitKind.Profanity).Certain);

        model.HabitMisheardCommand.Execute(model.HabitMoments[0]);

        Assert.Equal(1, model.Habits!.Habits.CountOf(HabitKind.Profanity).Certain);
        Assert.Equal(2, model.HabitMoments.Count);
        Assert.Single(model.HabitMoments, m => m.IsDismissed);
    }

    /// <summary>
    /// Goes red when an uncounted conversation shows zeroes instead of saying nothing was
    /// counted — "0 küfür" is a claim, and an unmeasured call has not earned it.
    /// </summary>
    [Fact]
    public void AnUncountedConversationSaysSoRatherThanShowingZeroes()
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact, App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now, Duration = TimeSpan.FromMinutes(1),
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);
        Transcribe(call, "nova-3", [$"{Swear} dedim"]);

        using var model = Window(call);

        Assert.False(model.HasHabits);
        Assert.Null(model.HabitLine);
        Assert.Empty(model.HabitMoments);
        Assert.False(model.IsHabitsStale);
    }

    /// <summary>
    /// Goes red when the intent card leaks a line the user never wrote. It is read-only here and
    /// absent when there is nothing: an empty card is no card.
    /// </summary>
    [Fact]
    public void TheIntentLineIsShownOnlyWhenTheUserWroteOne()
    {
        var call = Seed("nova-3", ["Sakin konuştum"]);

        using var without = Window(call);
        Assert.Null(without.IntentText);

        _repo.SaveCallIntent(call, "Kira rakamını söylemeyeceğim");

        using var with = Window(call);
        Assert.Equal("Kira rakamını söylemeyeceğim", with.IntentText);
    }

    /// <summary>
    /// Goes red when the window's talk figures stop coming from TalkStats — the rule that used to
    /// live twice, here and on the contacts page, where the two copies could drift apart.
    /// </summary>
    [Fact]
    public void TheTalkFiguresComeFromTheSharedRule()
    {
        var call = Seed("nova-3", ["Bir şey söyledim"], ["Anladım."]);

        using var model = Window(call);

        var expected = TalkStats.Compute(_repo.GetSegments(call));

        Assert.Equal(expected, model.Talk);
        Assert.Equal(expected.MyShare, model.TalkRatio);
    }
}
