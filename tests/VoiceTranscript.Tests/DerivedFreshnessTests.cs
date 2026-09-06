using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// A derived note knows which transcript it was written from.
///
/// Complaint 7: transcribing a call again replaced its lines and left the reading, the
/// assessment, the summary and the suggestions standing on text that no longer existed —
/// quoting words the screen no longer showed, with nothing on any of them to say so. The
/// notes are not deleted when the text changes (a reading was paid for; a suggestion may have
/// been acted on); they are labelled, and the label rests on the pointer these tests pin.
///
/// Three honest answers and no fourth: fresh, stale, or unknown. Unknown — a note from before
/// the pointer existed, or a call that never recorded which transcript it shows — is never
/// reported as stale, because a wrong "bayat" would teach the user to ignore the label.
/// </summary>
public sealed class DerivedFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-fresh-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;
    private readonly long _callId;
    private readonly long _contactId;

    public DerivedFreshnessTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repository = new Repository(database);

        _contactId = _repository.UpsertContact("Gürhan", CallApp.WhatsApp);
        _callId = _repository.InsertCall(new Call
        {
            ContactId = _contactId,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse("2026-09-04T14:02:00+03:00"),
            Duration = TimeSpan.FromMinutes(18),
            State = ProcessingState.Analysed,
        });
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private List<Segment> Lines(params string[] texts) =>
        [.. texts.Select((text, i) => new Segment
        {
            CallId = _callId, IsMe = i % 2 == 0, StartMs = i * 3000, EndMs = i * 3000 + 2400, Text = text,
        })];

    private long Transcribe(string engine, params string[] texts)
    {
        var lines = Lines(texts);
        _repository.ReplaceSegments(_callId, lines);
        return _repository.SaveTranscriptVersion(_callId, engine, 0.8, lines);
    }

    private long Suggest(string action) =>
        _repository.InsertAction(new ActionItem { CallId = _callId, ContactId = _contactId, Action = action, Quote = "cumaya yollarım" });

    private long Find(string quote) =>
        _repository.InsertFlag(new Flag
        {
            CallId = _callId,
            ContactId = _contactId,
            Kind = FlagKind.Contradiction,
            Summary = "Rakam değişti",
            Quote = quote,
            Source = Flag.Sources.Consistency,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>
    /// Goes red when a note written after a transcription is not filed under that transcript —
    /// which is the whole mechanism: without the pointer, nothing can ever say "bayat".
    /// </summary>
    [Fact]
    public void ANoteWrittenNowIsFreshAgainstTheTranscriptOnScreen()
    {
        Transcribe("large-v3", "Alo", "Buyur");

        _repository.SaveReading(_callId, "{}", "qwen");
        _repository.SaveDeception(_callId, "{}", "qwen");
        _repository.SaveConsistencyNote(_callId, "not", "qwen");
        _repository.SaveSummary(new CallSummary { CallId = _callId, Summary = "özet" });
        Suggest("Tarihi yazılı iste");

        var freshness = _repository.DerivedFreshness(_callId);

        Assert.Equal(Staleness.Fresh, freshness.Reading);
        Assert.Equal(Staleness.Fresh, freshness.Deception);
        Assert.Equal(Staleness.Fresh, freshness.Consistency);
        Assert.Equal(Staleness.Fresh, freshness.Summary);
        Assert.Equal(Staleness.Fresh, freshness.Actions);
        Assert.False(freshness.AnyStale);
    }

    /// <summary>
    /// The complaint itself. Goes red when transcribing again silently keeps a reading current:
    /// the user opens the Okuma tab and reads quotes from a text that is no longer there.
    /// </summary>
    [Fact]
    public void TranscribingAgainMakesEveryEarlierNoteStale()
    {
        Transcribe("large-v3", "Alo ne yapıyon", "Buyur");
        _repository.SaveReading(_callId, "{}", "qwen");
        _repository.SaveSummary(new CallSummary { CallId = _callId, Summary = "özet" });
        Suggest("Tarihi yazılı iste");

        Transcribe("nova-3", "Alo, ne yapıyorsun?", "Buyur.");

        var freshness = _repository.DerivedFreshness(_callId);

        Assert.Equal(Staleness.Stale, freshness.Reading);
        Assert.Equal(Staleness.Stale, freshness.Summary);
        Assert.Equal(Staleness.Stale, freshness.Actions);
        Assert.Equal(Staleness.Absent, freshness.Deception);
        Assert.True(freshness.AnyStale);

        // And writing the note again puts it under the new text.
        _repository.SaveReading(_callId, "{}", "qwen");
        Assert.Equal(Staleness.Fresh, _repository.DerivedFreshness(_callId).Reading);
    }

    /// <summary>
    /// Goes red when a note with no pointer — every note from before v15 — is called stale. The
    /// column is NULL there, and NULL means "nothing recorded it", never "an older text".
    /// </summary>
    [Fact]
    public void ANoteWithoutAPointerIsUnknownNotStale()
    {
        Transcribe("large-v3", "Alo", "Buyur");
        _repository.SaveReading(_callId, "{}", "qwen");

        using (var connection = new Database(_paths.DatabaseFile).Open())
        {
            using var strip = connection.CreateCommand();
            strip.CommandText = "UPDATE reading_note SET transcript_version_id = NULL;";
            strip.ExecuteNonQuery();
        }

        Assert.Equal(Staleness.Unknown, _repository.DerivedFreshness(_callId).Reading);
    }

    /// <summary>
    /// Goes red when a call that never recorded which transcript it shows (older than v14) gets
    /// its notes called stale — there is nothing to compare against, and the honest word is
    /// "bilinmiyor".
    /// </summary>
    [Fact]
    public void ACallWithoutATranscriptPointerLeavesEverythingUnknown()
    {
        _repository.ReplaceSegments(_callId, Lines("Alo", "Buyur"));
        _repository.SaveReading(_callId, "{}", "qwen");
        Suggest("Ara");

        var freshness = _repository.DerivedFreshness(_callId);

        Assert.Null(freshness.CurrentVersionId);
        Assert.Equal(Staleness.Unknown, freshness.Reading);
        Assert.Equal(Staleness.Unknown, freshness.Actions);
        Assert.Equal(Staleness.Absent, freshness.Summary);
        Assert.False(freshness.AnyStale);
    }

    /// <summary>Goes red when a suggestion the user already ruled on counts as stale — only open ones do.</summary>
    [Fact]
    public void OnlyOpenSuggestionsAreJudged()
    {
        Transcribe("large-v3", "Alo", "Buyur");
        var done = Suggest("Ara");
        _repository.SetActionStatus(done, ActionStatus.Done);

        Transcribe("nova-3", "Alo.", "Buyur.");

        Assert.Equal(Staleness.Absent, _repository.DerivedFreshness(_callId).Actions);
    }

    /// <summary>
    /// Consistency findings are judged even when the run left no warning note.
    ///
    /// The note is written only when the evidence justified a warning, so a run that produced
    /// contradictions and nothing to say over them writes no row at all. Judging the tab by that
    /// row alone left those findings — sentences quoted out of a text a re-transcription has
    /// replaced — reading as current, on the surface that can least afford it: each one is an
    /// accusation about a person, and the product's whole claim is that every quote can be
    /// played back.
    ///
    /// Red means that path is open again: the mechanism exists and this is the way into it.
    /// </summary>
    [Fact]
    public void FindingsWithNoWarningNoteAreStillJudgedAfterARetranscription()
    {
        Transcribe("large-v3", "Kira on beş bin lira", "Anladım");
        Find("Kira on beş bin lira");

        Assert.Null(_repository.GetConsistencyNote(_callId));
        Assert.Equal(Staleness.Fresh, _repository.DerivedFreshness(_callId).Consistency);

        Transcribe("nova-3", "Kira on beş bin lira.", "Anladım.");

        Assert.Equal(Staleness.Stale, _repository.DerivedFreshness(_callId).Consistency);
        Assert.True(_repository.DerivedFreshness(_callId).AnyStale);
    }

    /// <summary>
    /// A finding that never recorded which transcript it came out of is "bilinmiyor", not "bayat".
    ///
    /// Every flag written before v21 is in that state and none of them can be backfilled — the
    /// run that wrote them did not record what it read. Red here means an unknown pointer has
    /// become an accusation about a person on the strength of nothing, which is the failure §4.9
    /// exists to forbid.
    /// </summary>
    [Fact]
    public void AFindingWithoutATranscriptPointerIsUnknownNotStale()
    {
        Transcribe("large-v3", "Kira on beş bin lira", "Anladım");
        Find("Kira on beş bin lira");

        using (var connection = new Database(_paths.DatabaseFile).Open())
        {
            using var strip = connection.CreateCommand();
            strip.CommandText = "UPDATE flag SET transcript_version_id = NULL;";
            strip.ExecuteNonQuery();
        }

        Transcribe("nova-3", "Kira on beş bin lira.", "Anladım.");

        Assert.Equal(Staleness.Unknown, _repository.DerivedFreshness(_callId).Consistency);
        Assert.False(_repository.DerivedFreshness(_callId).AnyStale);
    }

    /// <summary>
    /// The two halves of the consistency tab are judged together, worst first.
    ///
    /// A run writes a warning note and its findings from the same text, but the user's dismissals
    /// keep findings alive across re-runs, so the two can end up under different transcripts. One
    /// stale half is enough to warn about: the reader is one click away from a quote that is no
    /// longer in the conversation.
    /// </summary>
    [Fact]
    public void OneStaleHalfOfTheConsistencyTabIsEnoughToLabelIt()
    {
        Transcribe("large-v3", "Kira on beş bin lira", "Anladım");
        Find("Kira on beş bin lira");

        Transcribe("nova-3", "Kira on beş bin lira.", "Anladım.");
        _repository.SaveConsistencyNote(_callId, "Rakamı yazılı iste.", "qwen", []);

        // The note is of the text on screen; the finding it stands over is not.
        Assert.Equal(Staleness.Stale, _repository.DerivedFreshness(_callId).Consistency);
    }

    /// <summary>Goes red when deleting a call leaves its verdicts, notes or pointers behind (cascade).</summary>
    [Fact]
    public void RestoringAnOlderTranscriptMakesTheNotesWrittenSinceStale()
    {
        var first = Transcribe("large-v3", "Alo", "Buyur");
        Transcribe("nova-3", "Alo.", "Buyur.");
        _repository.SaveReading(_callId, "{}", "qwen");

        Assert.True(_repository.RestoreTranscriptVersion(first));

        Assert.Equal(Staleness.Stale, _repository.DerivedFreshness(_callId).Reading);
    }
}
