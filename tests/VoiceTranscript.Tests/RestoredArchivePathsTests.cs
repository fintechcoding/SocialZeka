using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// A restored archive has to find its own audio.
///
/// The recording paths are absolute, so a backup carries the user name of the machine that wrote
/// it. Restoring onto another computer is the one thing the backup exists for, and it produced an
/// archive where every single call said "Ses yok" while the files sat, correctly unpacked, in the
/// recordings folder — 48 of 51 conversations, unreachable because of a prefix.
/// </summary>
public class RestoredArchivePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-rebase-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;

    public RestoredArchivePathsTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The database file can still be held briefly.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The path a backup taken on somebody else's machine would carry.</summary>
    private static string Foreign(string month, string name) =>
        Path.Combine(@"C:\Users\PC\AppData\Local\VoiceTranscript.Data\recordings", month, name);

    /// <summary>Writes a recording where a restore would have put it, and returns the path.</summary>
    private string Unpack(string month, string name)
    {
        var directory = Path.Combine(_paths.Recordings, month);
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, name);
        File.WriteAllBytes(file, [0x4F, 0x67, 0x67, 0x53]);

        return file;
    }

    private long InsertCall(string? mic, string? far) =>
        _repository.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            Direction = CallDirection.Outgoing,
            StartedAt = DateTimeOffset.Parse("2026-08-30T10:43:13+03:00"),
            Duration = TimeSpan.FromSeconds(26),
            State = ProcessingState.Analysed,
            MicPath = mic,
            FarPath = far,
        });

    [Fact]
    public void ARestoredCallFindsItsAudioOnThisMachine()
    {
        var mic = Unpack("2026-08", "call-5-mic.ogg");
        var far = Unpack("2026-08", "call-5-far.ogg");

        var id = InsertCall(Foreign("2026-08", "call-5-mic.ogg"), Foreign("2026-08", "call-5-far.ogg"));

        Assert.Equal(1, _repository.RebaseRecordingPaths(_paths.Recordings));

        var call = _repository.GetCall(id);
        Assert.Equal(mic, call!.MicPath);
        Assert.Equal(far, call.FarPath);
        Assert.True(File.Exists(call.MicPath));
    }

    /// <summary>
    /// The month folder is part of the answer. Flattening every recording into the root would
    /// point call 5 at nothing on an archive that spans two months.
    /// </summary>
    [Fact]
    public void TheMonthFolderIsKept()
    {
        Unpack("2026-08", "call-5-mic.ogg");
        var september = Unpack("2026-09", "call-40-mic.ogg");

        var id = InsertCall(Foreign("2026-09", "call-40-mic.ogg"), null);

        _repository.RebaseRecordingPaths(_paths.Recordings);

        Assert.Equal(september, _repository.GetCall(id)!.MicPath);
    }

    /// <summary>
    /// Audio the retention sweep deleted is genuinely gone, and rewriting the row would turn a
    /// truthful "Ses yok" into a path that promises a file nobody can open.
    /// </summary>
    [Fact]
    public void AudioThatIsNotOnThisDiskIsNotInvented()
    {
        var foreign = Foreign("2026-08", "call-99-mic.ogg");
        var id = InsertCall(foreign, null);

        Assert.Equal(0, _repository.RebaseRecordingPaths(_paths.Recordings));
        Assert.Equal(foreign, _repository.GetCall(id)!.MicPath);
    }

    /// <summary>An ordinary start rewrites nothing, whether or not the files are still there.</summary>
    [Fact]
    public void PathsAlreadyOnThisMachineAreLeftAlone()
    {
        var mic = Unpack("2026-08", "call-6-mic.ogg");
        InsertCall(mic, Path.Combine(_paths.Recordings, "2026-08", "call-6-far.ogg"));

        Assert.Equal(0, _repository.RebaseRecordingPaths(_paths.Recordings));
    }

    /// <summary>A call that never captured anything has nothing to re-root.</summary>
    [Fact]
    public void ACallWithNoAudioIsUntouched()
    {
        var id = InsertCall(null, null);

        Assert.Equal(0, _repository.RebaseRecordingPaths(_paths.Recordings));
        Assert.Null(_repository.GetCall(id)!.MicPath);
    }
}
