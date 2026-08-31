using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// What the per-contact window and the quality line rest on.
///
/// These queries are new and each one exists because the interface was about to state something
/// as a fact. A figure shown to a person has to be counted rather than sampled, or it is a claim
/// the archive cannot back up — which is the one thing this product must not do.
/// </summary>
public sealed class ContactWindowTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-cw-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public ContactWindowTests()
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

    private long Call(long contactId, string startedAt, int minutes)
        => _repo.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse(startedAt),
            Duration = TimeSpan.FromMinutes(minutes),
            State = ProcessingState.Transcribed,
        });

    // ---- the heading figures -------------------------------------------------

    /// <summary>
    /// Counted from the whole table, not from the page of calls the list happens to show.
    ///
    /// ListCalls caps at two hundred rows, and the people this window exists for are exactly the
    /// ones who exceed it — so deriving the heading from the list would under-report for them and
    /// nobody else, which is the worst possible distribution of a wrong number.
    /// </summary>
    [Fact]
    public void TheHeadingCountsEverythingRatherThanTheVisiblePage()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        for (var i = 0; i < 250; i++) Call(contact, "2026-08-01T10:00:00+03:00", minutes: 2);

        var totals = _repo.ContactTotals(contact);

        Assert.Equal(250, totals.Calls);
        Assert.Equal(TimeSpan.FromMinutes(500), totals.Recorded);
        Assert.True(_repo.ListCalls(contact, limit: 200).Count < totals.Calls);
    }

    [Fact]
    public void TheHeadingSpansTheFirstAndLastConversation()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        Call(contact, "2025-02-04T10:00:00+03:00", minutes: 5);
        Call(contact, "2026-08-28T10:00:00+03:00", minutes: 5);

        var totals = _repo.ContactTotals(contact);

        Assert.Equal(2025, totals.First!.Value.Year);
        Assert.Equal(2026, totals.Last!.Value.Year);
    }

    [Fact]
    public void AContactWithNoCallsReportsNothingRatherThanFailing()
    {
        var contact = _repo.UpsertContact("Yeni", CallApp.WhatsApp);

        var totals = _repo.ContactTotals(contact);

        Assert.Equal(0, totals.Calls);
        Assert.Null(totals.First);
    }

    // ---- notes about the person ---------------------------------------------

    /// <summary>
    /// The column has existed since the first schema and nothing ever wrote to it. Using it works
    /// on every database that already exists, which a new column would not.
    /// </summary>
    [Fact]
    public void ANoteAboutThePersonIsKept()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        _repo.SaveContactNote(contact, "  Ödeme konusunda dikkatli ol.  ");

        Assert.Equal("Ödeme konusunda dikkatli ol.", _repo.GetContact(contact)?.Notes);
    }

    [Fact]
    public void ClearingTheNoteLeavesNothingRatherThanBlankSpace()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        _repo.SaveContactNote(contact, "bir şey");
        _repo.SaveContactNote(contact, "   ");

        Assert.Null(_repo.GetContact(contact)?.Notes);
    }

    /// <summary>
    /// A note is the one thing here a person wrote. Renaming must not touch it — the whole reason
    /// renaming exists is that automatic attribution gets names wrong, and losing the note as the
    /// price of fixing one would be a poor trade.
    /// </summary>
    [Fact]
    public void RenamingKeepsTheNote()
    {
        var contact = _repo.UpsertContact("Serdaal", CallApp.WhatsApp);

        _repo.SaveContactNote(contact, "hatırla");
        _repo.RenameContact(contact, "Serdal");

        Assert.Equal("Serdal", _repo.GetContact(contact)?.Name);
        Assert.Equal("hatırla", _repo.GetContact(contact)?.Notes);
    }

    // ---- transcript quality --------------------------------------------------

    [Fact]
    public void QualityCountsTheLinesTheModelWasUnsureAbout()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = Call(contact, "2026-08-01T10:00:00+03:00", minutes: 3);

        _repo.ReplaceSegments(call,
        [
            Line(call, "kesin", low: false),
            Line(call, "belirsiz", low: true),
            Line(call, "yine belirsiz", low: true),
        ]);

        var (lines, low, _) = _repo.TranscriptQuality(call);

        Assert.Equal(3, lines);
        Assert.Equal(2, low);
    }

    [Fact]
    public void ACallWithNoTranscriptHasNoQualityToReport()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = Call(contact, "2026-08-01T10:00:00+03:00", minutes: 3);

        Assert.Equal(0, _repo.TranscriptQuality(call).Lines);
    }

    private static Segment Line(long callId, string text, bool low) => new()
    {
        CallId = callId,
        IsMe = false,
        StartMs = 0,
        EndMs = 1000,
        Text = text,
        TextNormalised = TurkishText.NormalizeForSearch(text),
        LowConfidence = low,
    };

    // ---- which engine did it -------------------------------------------------

    /// <summary>The latest run wins, so a call redone with a different engine says the new one.</summary>
    [Fact]
    public void TheLatestRunIsTheOneReported()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = Call(contact, "2026-08-01T10:00:00+03:00", minutes: 10);

        _repo.RecordRun(call, ProcessingStage.Transcribe, "yerel-small",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(10));

        _repo.RecordRun(call, ProcessingStage.Transcribe, "bulut-openai",
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(10));

        var run = _repo.LastRun(call, ProcessingStage.Transcribe);

        Assert.Equal("bulut-openai", run!.Engine);
        Assert.True(run.SpeedFactor > 1);
    }

    [Fact]
    public void ACallThatWasNeverProcessedHasNoRun()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        Assert.Null(_repo.LastRun(Call(contact, "2026-08-01T10:00:00+03:00", 3), ProcessingStage.Transcribe));
    }

    // ---- the daily chart -----------------------------------------------------

    /// <summary>
    /// Empty days are rows too. A chart drawn only from days that have work compresses a fortnight
    /// of silence into nothing and makes a sporadic month look continuous — the opposite of what
    /// somebody looks at it to find out.
    /// </summary>
    [Fact]
    public void TheChartIncludesDaysWithNothingInThem()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = Call(contact, "2026-08-01T10:00:00+03:00", minutes: 10);

        _repo.RecordRun(call, ProcessingStage.Transcribe, "whisper",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));

        var days = _repo.DailyUsage(ProcessingStage.Transcribe, days: 7);

        Assert.Equal(7, days.Count);
        Assert.Contains(days, d => d.IsEmpty);
        Assert.Contains(days, d => d.Runs > 0);

        // Oldest first, so the chart reads left to right as time does.
        Assert.True(days[0].Day < days[^1].Day);
    }

    [Fact]
    public void AnArchiveWithNoWorkStillProducesAFullRowOfEmptyDays()
    {
        var days = _repo.DailyUsage(ProcessingStage.Transcribe, days: 30);

        Assert.Equal(30, days.Count);
        Assert.All(days, d => Assert.True(d.IsEmpty));
    }
}
