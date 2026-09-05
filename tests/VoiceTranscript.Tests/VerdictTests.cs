using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// What the user heard, kept.
///
/// Every precision figure the coaching screens will show is a ratio over the verdict table, so
/// the table has to hold exactly one verdict per moment, survive every re-run, and be found
/// again by the words and the millisecond rather than by a row id that a recount or a merge
/// changes.
/// </summary>
public sealed class VerdictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-verdict-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly long _call;
    private readonly long _contact;

    public VerdictTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);

        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
        _call = _repo.InsertCall(new Call { ContactId = _contact, App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Analysed });
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private Verdict Heard(string quote, int ms, VerdictValue value, string kind = VerdictKind.Profanity) => new()
    {
        CallId = _call,
        Kind = kind,
        QuoteFolded = TurkishText.NormalizeForSearch(quote),
        StartMs = ms,
        Value = value,
    };

    /// <summary>Goes red when a second verdict on the same moment sits beside the first instead of replacing it.</summary>
    [Fact]
    public void OneVerdictPerMomentTheLatestWins()
    {
        _repo.SaveVerdict(Heard("lan", 12_410, VerdictValue.Correct));
        _repo.SaveVerdict(Heard("Lan", 12_410, VerdictValue.NotThat));

        var verdicts = _repo.Verdicts(_call, VerdictKind.Profanity);

        Assert.Single(verdicts);
        Assert.Equal(VerdictValue.NotThat, verdicts[0].Value);
        Assert.NotEqual(default, verdicts[0].DecidedAt);
    }

    /// <summary>Goes red when kinds bleed into each other: a flag verdict must not count as a swear-word verdict.</summary>
    [Fact]
    public void KindsAreKeptApart()
    {
        _repo.SaveVerdict(Heard("lan", 1000, VerdictValue.Correct));
        _repo.SaveVerdict(Heard("başkasına vereceğim", 2000, VerdictValue.Misheard, VerdictKind.Flag));

        Assert.Single(_repo.Verdicts(_call, VerdictKind.Profanity));
        Assert.Single(_repo.Verdicts(_call, VerdictKind.Flag));
        Assert.Equal(2, _repo.Verdicts(_call).Count);
    }

    /// <summary>The precision figure's two numbers, and what they exclude.</summary>
    [Fact]
    public void TheTallyCountsListenedAndCorrect()
    {
        _repo.SaveVerdict(Heard("a", 1, VerdictValue.Correct));
        _repo.SaveVerdict(Heard("b", 2, VerdictValue.Correct));
        _repo.SaveVerdict(Heard("c", 3, VerdictValue.Misheard));
        _repo.SaveVerdict(Heard("d", 4, VerdictValue.NotThat));

        Assert.Equal((4, 2), _repo.VerdictTally(VerdictKind.Profanity));
        Assert.Equal((4, 2), _repo.VerdictTally(VerdictKind.Profanity, _contact));
        Assert.Equal((0, 0), _repo.VerdictTally(VerdictKind.Tone));
    }

    /// <summary>
    /// The rule that makes the table worth anything. Goes red when a re-analysis or a consistency
    /// re-run deletes what the user heard: the machine's output is replaced, the user's is not.
    /// </summary>
    [Fact]
    public void ReRunsNeverTouchAVerdict()
    {
        _repo.SaveVerdict(Heard("lan", 1000, VerdictValue.Correct));

        _repo.ClearAnalysis(_call);
        _repo.ClearConsistency(_call);
        _repo.ClearOpenActions(_call);
        _repo.SweepLedger();

        Assert.Single(_repo.Verdicts(_call));
    }

    /// <summary>Goes red when a verdict outlives its call, or when deleting one deletes its neighbours.</summary>
    [Fact]
    public void AVerdictGoesWithItsCallAndOnlyItself()
    {
        var a = _repo.SaveVerdict(Heard("a", 1, VerdictValue.Correct));
        _repo.SaveVerdict(Heard("b", 2, VerdictValue.Correct));

        _repo.DeleteVerdict(a);
        Assert.Single(_repo.Verdicts(_call));

        _repo.DeleteCall(_call);
        Assert.Empty(_repo.Verdicts(_call));
    }
}
