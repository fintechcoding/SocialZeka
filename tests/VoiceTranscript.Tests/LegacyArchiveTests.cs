using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// The takeover offer VoiceTranscript's archive gets on SocialZeka's first start.
///
/// SocialZeka is VoiceTranscript forked, with its own data folder so the two can be installed
/// side by side. The user's recordings sit under the old name, and the one moment they can be
/// handed over safely is before this application has written a database of its own. These tests
/// go red if the offer fires with nothing to take over, fires after a fresh start was already
/// chosen (which would move an archive onto a live database), or names the same folder twice
/// (which a move would destroy).
/// </summary>
public class LegacyArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vt-legacy-" + Guid.NewGuid().ToString("N"));

    private string Old => Path.Combine(_root, "VoiceTranscript.Data");
    private string New => Path.Combine(_root, "SocialZeka.Data");

    public LegacyArchiveTests()
    {
        Directory.CreateDirectory(Old);
        Directory.CreateDirectory(New);
    }

    private void WriteDatabase(string root) =>
        File.WriteAllText(Path.Combine(root, AppPaths.DatabaseFileName), "");

    [Fact]
    public void NothingToTakeOverWhenTheOldApplicationNeverRan()
    {
        Assert.Null(AppPaths.LegacyArchiveToTakeOver(New, Old));
    }

    [Fact]
    public void TheOldArchiveIsOfferedWhileThisRootHasNoDatabase()
    {
        WriteDatabase(Old);

        Assert.Equal(Old, AppPaths.LegacyArchiveToTakeOver(New, Old));
    }

    [Fact]
    public void AFreshStartAlreadyChosenIsNeverOverwritten()
    {
        WriteDatabase(Old);
        WriteDatabase(New);

        Assert.Null(AppPaths.LegacyArchiveToTakeOver(New, Old));
    }

    [Fact]
    public void TheSameFolderIsNeverMovedOntoItself()
    {
        WriteDatabase(Old);

        Assert.Null(AppPaths.LegacyArchiveToTakeOver(Old, Old));
    }

    /// <summary>
    /// The identity the fork changed and the identity it kept, pinned. A future rename of either
    /// constant must be a decision, not a side effect: the data folder name is what the takeover
    /// looks for, and the database file name is what a moved archive is opened by.
    /// </summary>
    [Fact]
    public void TheForkKeptTheDatabaseNameAndChangedTheFolderName()
    {
        Assert.Equal("SocialZeka", AppPaths.ApplicationName);
        Assert.Equal("VoiceTranscript", AppPaths.LegacyApplicationName);
        Assert.Equal("voicetranscript.db", AppPaths.DatabaseFileName);
        Assert.EndsWith("SocialZeka.Data", new AppPaths().Root);
        Assert.EndsWith(AppPaths.DatabaseFileName, new AppPaths(_root).DatabaseFile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
