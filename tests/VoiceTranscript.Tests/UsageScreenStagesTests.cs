using System.Net.Http;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The usage screen against the eight stages the database actually holds.
///
/// <c>processing_run</c> records transcription, analysis, questions, the consistency check, the
/// action suggestions, the free reading, the opt-in assessment and the reading of a person. The
/// screen read three of them, so more than half of what this application spends was written down
/// and never shown — and a check that failed on every attempt was invisible, because its failures
/// were not added up either.
/// </summary>
public sealed class UsageScreenStagesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-stages-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public UsageScreenStagesTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private long Call() => _repo.InsertCall(new Call
    {
        App = CallApp.WhatsApp,
        StartedAt = DateTimeOffset.UtcNow,
        State = ProcessingState.Analysed,
    });

    private void Record(string stage, bool succeeded = true, int prompt = 400, int completion = 120)
        => _repo.RecordRun(
            Call(), stage, "qwen-test", DateTimeOffset.UtcNow,
            elapsed: TimeSpan.FromSeconds(4), audio: TimeSpan.Zero,
            promptTokens: prompt, completionTokens: completion, succeeded: succeeded);

    private AiStatusViewModel Screen()
    {
        var settings = new AppSettings();
        return new AiStatusViewModel(() => settings, new HttpClient(), _repo);
    }

    /// <summary>
    /// Money spent on a secondary reading is money the screen has to account for.
    ///
    /// Goes red when the screen goes back to reading only transcription, analysis and questions:
    /// an archive whose only paid work was a consistency check and a couple of readings then says
    /// "henüz ölçülecek bir iş yapılmadı" while holding the record of what it cost.
    /// </summary>
    [Fact]
    public void EveryStageWithRunsIsCounted()
    {
        Record(ProcessingStage.Consistency);
        Record(ProcessingStage.Action);
        Record(ProcessingStage.Reading);
        Record(ProcessingStage.Deception);
        Record(ProcessingStage.ContactReading);

        var screen = Screen();
        screen.Refresh();

        Assert.True(screen.HasUsage);
        Assert.True(screen.HasSecondaryUsage);
        Assert.Equal(5, screen.SecondaryUsage.Count);

        // The names come out of the dictionary, so a mistyped key would show as itself.
        Assert.All(screen.SecondaryUsage, line => Assert.DoesNotContain("aistatuspage.", line.Name));

        // And each line states what its stage cost, from the row rather than from an estimate.
        Assert.All(screen.SecondaryUsage, line => Assert.Contains("400", line.Detail));
    }

    /// <summary>
    /// A stage that has never run is left out, so five extra lines do not appear on every screen.
    ///
    /// Goes red if the list starts printing zeros: this screen says what happened, and a row
    /// reading "0" for something the user has never switched on is noise in the one place that
    /// has to stay readable.
    /// </summary>
    [Fact]
    public void AStageThatHasNeverRunTakesNoSpace()
    {
        Record(ProcessingStage.Reading);

        var screen = Screen();
        screen.Refresh();

        var only = Assert.Single(screen.SecondaryUsage);
        Assert.Contains("400", only.Detail);
    }

    /// <summary>
    /// An assessment that fails every single time it is asked for.
    ///
    /// This is the case the failure line exists for and the case it could not see: three paid
    /// attempts, three failures, and a total that only ever counted three stages reported none of
    /// them. Goes red when the line narrows again.
    /// </summary>
    [Fact]
    public void FailuresInASecondaryStageReachTheFailureLine()
    {
        Record(ProcessingStage.Deception, succeeded: false, prompt: 900, completion: 0);
        Record(ProcessingStage.Deception, succeeded: false, prompt: 900, completion: 0);
        Record(ProcessingStage.Deception, succeeded: false, prompt: 900, completion: 0);

        var screen = Screen();
        screen.Refresh();

        Assert.Contains("3", screen.FailureLine);

        var line = Assert.Single(screen.SecondaryUsage);
        Assert.Contains("3", line.Detail);
    }
}
