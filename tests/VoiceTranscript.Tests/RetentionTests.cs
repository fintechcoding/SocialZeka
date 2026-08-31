using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Deleting old recordings, which the product offered to do and then did not.
///
/// The settings screen has carried a retention period since the first version. Nothing ever read
/// it. Somebody could set it to thirty days, watch the disk fill for a year, and be right to
/// conclude the number was decorative — which it was.
///
/// The other half of that screen was worse: it promised pinned conversations were exempt, and
/// nothing in the product pins anything. An exemption nobody can invoke is not a safeguard. So
/// the rules here are the ones a person can actually reach — a card on the board, or a note they
/// wrote — and the sweep takes the audio only, because the transcript is the part worth keeping
/// and it is not what fills a disk.
/// </summary>
public sealed class RetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-keep-{Guid.NewGuid():N}");
    private readonly string _path;
    private readonly Repository _repo;

    public RetentionTests()
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

    /// <summary>A recording that many days old, with both streams present on disk.</summary>
    private long Recording(int daysOld, out string mic, out string far)
    {
        var id = Guid.NewGuid().ToString("N")[..8];

        mic = Path.Combine(_root, $"{id}-mic.wav");
        far = Path.Combine(_root, $"{id}-far.wav");

        File.WriteAllBytes(mic, new byte[64]);
        File.WriteAllBytes(far, new byte[64]);

        return _repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-daysOld),
            State = ProcessingState.Analysed,
            MicPath = mic,
            FarPath = far,
        });
    }

    private long Recording(int daysOld) => Recording(daysOld, out _, out _);

    /// <summary>
    /// Zero means forever, and it is the default. Getting this wrong deletes the archive of
    /// everyone who never opened the setting.
    /// </summary>
    [Fact]
    public void ZeroDaysSweepsNothing()
    {
        Recording(daysOld: 5000);

        Assert.Empty(_repo.AudioToSweep(0));
        Assert.Empty(_repo.AudioToSweep(-1));
    }

    [Fact]
    public void OnlyRecordingsPastThePeriodAreListed()
    {
        var old = Recording(daysOld: 40);
        Recording(daysOld: 10);

        var stale = _repo.AudioToSweep(30);

        Assert.Equal(old, Assert.Single(stale).Id);
    }

    /// <summary>
    /// A card on the board is the user having said this conversation matters. Sweeping it would
    /// leave them a card that opens a player with nothing to play.
    /// </summary>
    [Fact]
    public void AConversationOnTheBoardIsKept()
    {
        var call = Recording(daysOld: 400);
        _repo.PutOnBoard(call, BoardLane.Mine);

        Assert.Empty(_repo.AudioToSweep(30));
    }

    [Fact]
    public void AConversationTheUserWroteANoteAboutIsKept()
    {
        var call = Recording(daysOld: 400);
        _repo.SaveNote(call, "bunu saklamak istiyorum");

        Assert.Empty(_repo.AudioToSweep(30));
    }

    /// <summary>
    /// Removing the card puts it back in reach of the sweep. Otherwise the board would be a
    /// one-way door: anything ever placed on it would be kept forever, invisibly.
    /// </summary>
    [Fact]
    public void TakingItOffTheBoardMakesItSweepableAgain()
    {
        var call = Recording(daysOld: 400);

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.RemoveFromBoard(call);

        Assert.Equal(call, Assert.Single(_repo.AudioToSweep(30)).Id);
    }

    /// <summary>
    /// The point of the whole feature: the disk is freed and the conversation is not lost.
    /// </summary>
    [Fact]
    public void SweepingRemovesTheAudioAndKeepsTheConversation()
    {
        var call = Recording(daysOld: 400, out var mic, out var far);
        _repo.ReplaceSegments(call, [Line(call, "bunu sildikten sonra da okunabilmeli")]);

        var removed = _repo.ForgetAudio(call);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(mic));
        Assert.False(File.Exists(far));

        var after = _repo.GetCall(call);

        Assert.NotNull(after);
        Assert.Null(after.MicPath);
        Assert.Null(after.FarPath);

        var line = Assert.Single(_repo.GetSegments(call));
        Assert.Equal("bunu sildikten sonra da okunabilmeli", line.Text);
    }

    /// <summary>
    /// Once swept, it must not come back round. A sweep that keeps re-listing the same rows logs
    /// a deletion every startup for recordings that went months ago.
    /// </summary>
    [Fact]
    public void AlreadySweptRecordingsAreNotListedAgain()
    {
        var call = Recording(daysOld: 400);

        _repo.ForgetAudio(call);

        Assert.Empty(_repo.AudioToSweep(30));
    }

    /// <summary>Nothing to delete is not a failure.</summary>
    [Fact]
    public void ForgettingACallThatIsNotThereIsHarmless()
    {
        Assert.Equal(0, _repo.ForgetAudio(9999));
    }

    private static Segment Line(long callId, string text) => new()
    {
        CallId = callId,
        StartMs = 0,
        EndMs = 1200,
        Text = text,
        IsMe = true,
    };
}
