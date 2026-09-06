using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// A paid run leaves a complete trace of itself.
///
/// The fault behind every test here is one shape seen four times: work the user paid for
/// produced an answer, the answer was rendered once, and the window forgot the run had ever
/// happened. Rows are the ordinary evidence that a check ran — and a check that honestly finds
/// nothing writes none. So the tab came back looking exactly like one nobody had ever pressed,
/// and the ordinary response to that is to press it again. The consistency check sends the whole
/// transcript in a single request; it is the most expensive click in the application, and a
/// short, clean conversation is the case where it legitimately finds nothing.
///
/// The balancing observations were the other half: produced by the same request, shown once, and
/// never written down — so a reopened window carried the accusing half about a person and
/// silently dropped the exonerating one.
/// </summary>
public sealed class PaidRunTraceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-iz-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly HttpClient _http = new();

    private readonly long _callId;

    public PaidRunTraceTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);

        var contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);

        _callId = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse("2026-06-11T10:15:00+03:00"),
            Duration = TimeSpan.FromMinutes(4),
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(_callId, contact);
        Transcribe("large-v3", "Kira on beş bin lira.", "Anladım, teşekkürler.");
    }

    public void Dispose()
    {
        _http.Dispose();
        new Database(_paths.DatabaseFile).ClearPool();

        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }

        GC.SuppressFinalize(this);
    }

    /// <summary>An LLM that answers with whatever the test handed it.</summary>
    private sealed class ScriptedLlm(string reply) : ILlmClient
    {
        public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmResponse(reply, "stop", 100, 40));

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private long Transcribe(string engine, params string[] lines)
    {
        var segments = lines.Select((text, i) => new Segment
        {
            CallId = _callId, IsMe = i % 2 == 1, StartMs = i * 4000, EndMs = i * 4000 + 3000, Text = text,
        }).ToList();

        _repo.ReplaceSegments(_callId, segments);
        return _repo.SaveTranscriptVersion(_callId, engine, 0.9, segments);
    }

    /// <summary>A window opened onto this call from scratch — the "reopened it tomorrow" state.</summary>
    private CallWindowViewModel Window()
    {
        var settings = new AppSettings();
        return new CallWindowViewModel(_repo, () => settings, _http, _callId);
    }

    private Task<ConsistencyReport> Check(string reply, string model = "qwen3-32b") =>
        new ConsistencyAnalysis(new ScriptedLlm(reply), _repo).RunAsync(
            _callId, model, cancellationToken: TestContext.Current.CancellationToken);

    private Task<ActionReport> Extract(string reply, string model = "qwen3-32b") =>
        new ActionExtraction(new ScriptedLlm(reply), _repo).RunAsync(
            _callId, model, TestContext.Current.CancellationToken);

    // ---- A: a check that found nothing still says it happened ----------------------------

    /// <summary>
    /// Goes red when a consistency run that produced no findings and no warning leaves the
    /// reopened tab unable to say that it ran, when it ran, or which model was paid to do it.
    ///
    /// That is the application's most expensive single click, and "nothing to show" is the
    /// ordinary outcome on a short, ordinary conversation. With no trace, the tab is
    /// indistinguishable from one that was never checked — so the honest reading of the screen
    /// is "press the button", and the same money is spent to be told the same nothing.
    /// </summary>
    [Fact]
    public async Task ACheckThatFoundNothingStillSaysWhenItRanAndWithWhichModel()
    {
        var report = await Check(
            """{"bulgular":[],"tutarli_gozlemler":[],"genel_uyari":"","yetersiz":false}""",
            model: "qwen3-32b");

        Assert.True(report.Ok);
        Assert.Empty(report.Findings);

        // Nothing was written to the tables the tab reads: this is exactly the blind spot.
        Assert.Empty(_repo.FlagsOf(_callId));
        Assert.Null(_repo.GetConsistencyNote(_callId));

        var window = Window();

        Assert.True(window.HasConsistencyRun);
        Assert.Contains("qwen3-32b", window.ConsistencyStamp!, StringComparison.Ordinal);
        Assert.Contains("2026", window.ConsistencyStamp!, StringComparison.Ordinal);

        Assert.NotNull(window.ConsistencyMessage);
        Assert.NotEqual("callwindow.denetim-kosuldu-bulgu-cikmadi", window.ConsistencyMessage);
    }

    /// <summary>
    /// Goes red when a run that could not be completed is presented as one that found nothing.
    ///
    /// Failed runs are recorded because they cost money. Reading them back as "checked, and the
    /// answer was no" would be the same lie as forgetting the run, pointed the other way: the
    /// user would stop pressing a button that has never actually produced an answer.
    /// </summary>
    [Fact]
    public void AFailedRunIsNotReportedAsACheckThatFoundNothing()
    {
        _repo.RecordRun(
            _callId, ProcessingStage.Consistency, "qwen3-32b",
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), TimeSpan.Zero, succeeded: false);

        Assert.Null(_repo.LastSuccessfulRun(_callId, ProcessingStage.Consistency));

        var window = Window();

        Assert.False(window.HasConsistencyRun);
        Assert.Null(window.ConsistencyStamp);
    }

    // ---- B: the balancing half survives the window closing --------------------------------

    /// <summary>
    /// Goes red when the observations a paid run produced do not come back on reopening.
    ///
    /// They are the balancing half of a judgement about a person: the findings say what did not
    /// add up, the observations say what held. Storing only the first is how the screen came to
    /// show the accusation and not the defence — from one run, at one price, with nothing saying
    /// the other half had ever existed.
    /// </summary>
    [Fact]
    public async Task TheObservationsOfAPaidRunComeBackWhenTheWindowIsReopened()
    {
        var report = await Check(
            """
            {"bulgular":[{"tur":"celiski","konusan":"KARSI","alinti":"Kira on beş bin lira",
                          "aciklama":"Rakam değişti","gerekce":"...","guven":"orta"}],
             "tutarli_gozlemler":[{"aciklama":"Tarihler baştan sona tutarlı",
                                   "alinti":"Anladım, teşekkürler"}],
             "genel_uyari":"Rakamı yazılı iste.","yetersiz":false}
            """);

        var live = Assert.Single(report.Observations);

        var stored = _repo.GetConsistencyNote(_callId);
        Assert.NotNull(stored);
        Assert.Equal(live, Assert.Single(stored.Observations!));

        var window = Window();

        var reopened = Assert.Single(window.ConsistencyObservations);
        Assert.Equal(live, reopened);
        Assert.Contains("Tarihler baştan sona tutarlı", reopened, StringComparison.Ordinal);

        // And the sentence apologising for not keeping them is gone with the fault.
        Assert.Null(window.ConsistencyMessage);
    }

    /// <summary>
    /// Goes red when a run written before the observations were kept is presented as a run that
    /// found nothing in the person's favour.
    ///
    /// NULL in that column means "not recorded" and an empty list means "none were produced".
    /// Collapsing the two would let an old row silently make a one-sided case, which is the very
    /// thing storing the observations was meant to stop (§4.9: unknown is never an accusation).
    /// </summary>
    [Fact]
    public async Task ARunFromBeforeTheObservationsWereKeptSaysSoRatherThanShowingNone()
    {
        await Check(
            """
            {"bulgular":[{"tur":"celiski","konusan":"KARSI","alinti":"Kira on beş bin lira",
                          "aciklama":"Rakam değişti","gerekce":"...","guven":"orta"}],
             "tutarli_gozlemler":[],"genel_uyari":"Rakamı yazılı iste.","yetersiz":false}
            """);

        // What a row written by an older build looks like.
        using (var connection = new Database(_paths.DatabaseFile).Open())
        {
            using var strip = connection.CreateCommand();
            strip.CommandText = "UPDATE consistency_note SET observations = NULL;";
            strip.ExecuteNonQuery();
        }

        Assert.Null(_repo.GetConsistencyNote(_callId)!.Observations);

        var window = Window();

        Assert.Empty(window.ConsistencyObservations);
        Assert.NotNull(window.ConsistencyMessage);
        Assert.NotEqual("callwindow.bu-kosumun-gozlemleri-saklanmamis", window.ConsistencyMessage);
    }

    // ---- C: the cheaper run, and the age of a suggestion ---------------------------------

    /// <summary>
    /// Goes red when an extraction that proposed nothing leaves the reopened tab unable to say
    /// that it ran. The same fault as the consistency check one price band down, and the same
    /// consequence: a button pressed again to be told the same nothing.
    /// </summary>
    [Fact]
    public async Task AnExtractionThatProposedNothingStillSaysWhenItRan()
    {
        var report = await Extract("""{"aksiyonlar":[]}""", model: "qwen3-8b");

        Assert.True(report.Ok);
        Assert.Empty(report.Actions);
        Assert.Empty(_repo.ActionsOf(_callId));

        var window = Window();

        Assert.NotNull(window.ActionsStamp);
        Assert.Contains("qwen3-8b", window.ActionsStamp!, StringComparison.Ordinal);
        Assert.Contains("2026", window.ActionsStamp!, StringComparison.Ordinal);
        Assert.NotEqual("callwindow.son-cikarim-imzasi", window.ActionsStamp);

        Assert.NotNull(window.ActionsMessage);
        Assert.NotEqual("callwindow.aksiyon-cikmadi", window.ActionsMessage);
    }

    /// <summary>
    /// Goes red when a suggestion is shown without the date it was made or the model that made
    /// it. Both were stored from the first day and neither reached the screen, so a suggestion
    /// from three months ago — about a deadline long past, from a model since replaced — read
    /// exactly like one made yesterday.
    /// </summary>
    [Fact]
    public void ASuggestionSaysWhenItWasMadeAndByWhichModel()
    {
        _repo.InsertAction(new ActionItem
        {
            CallId = _callId,
            Action = "Kira rakamını yazılı iste",
            Quote = "Kira on beş bin lira.",
            ModelUsed = "qwen3-8b",
            CreatedAt = DateTimeOffset.Parse("2026-03-02T09:00:00+03:00"),
        });

        _repo.InsertAction(new ActionItem
        {
            CallId = _callId,
            Action = "Sözleşmeyi gönder",
            Quote = "Anladım, teşekkürler.",
            ModelUsed = "gemma3-12b",
            CreatedAt = DateTimeOffset.Parse("2026-06-11T09:00:00+03:00"),
        });

        var rows = Window().Actions;
        Assert.Equal(2, rows.Count);

        var older = rows.Single(r => r.Action == "Kira rakamını yazılı iste").Stamp;
        var newer = rows.Single(r => r.Action == "Sözleşmeyi gönder").Stamp;

        Assert.Contains("qwen3-8b", older, StringComparison.Ordinal);
        Assert.Contains("gemma3-12b", newer, StringComparison.Ordinal);
        Assert.Contains("2026", older, StringComparison.Ordinal);

        // Three months apart, and the card says so: the whole point is that they cannot be read
        // as the same age, nor as coming from the same model.
        Assert.NotEqual(older, newer);
    }

    // ---- D: a re-run clears its own staleness warning --------------------------------------

    /// <summary>
    /// Every re-run on the call window refreshes the staleness labels afterwards.
    ///
    /// The consistency check was the one that did not. So the sequence was: the tab says "this
    /// is from an earlier transcript", the user presses the most expensive button in the
    /// application, the findings update against the text on screen — and the warning stays up,
    /// with a [Yeniden denetle] button underneath it. The obvious next move is to press it
    /// again, for the same money, to the same effect.
    ///
    /// The first half of this is behavioural: the stored state really does go from stale to
    /// fresh when the check runs again. The second is read out of the source, because the
    /// command builds its own HTTP client from the settings and no unit test can drive it —
    /// delete the call from any of the four and this goes red.
    /// </summary>
    [Fact]
    public async Task EveryRerunOnTheCallWindowRefreshesTheStalenessLabels()
    {
        await Check(
            """
            {"bulgular":[{"tur":"celiski","konusan":"KARSI","alinti":"Kira on beş bin lira",
                          "aciklama":"Rakam değişti","gerekce":"...","guven":"orta"}],
             "tutarli_gozlemler":[],"genel_uyari":"Rakamı yazılı iste.","yetersiz":false}
            """);

        Assert.Equal(Staleness.Fresh, _repo.DerivedFreshness(_callId).Consistency);

        // Same words, another engine: only the pointer moves, so what this pins is the pointer.
        Transcribe("nova-3", "Kira on beş bin lira.", "Anladım, teşekkürler.");
        Assert.Equal(Staleness.Stale, _repo.DerivedFreshness(_callId).Consistency);
        Assert.True(Window().IsConsistencyStale);

        await Check(
            """
            {"bulgular":[{"tur":"celiski","konusan":"KARSI","alinti":"Kira on beş bin lira",
                          "aciklama":"Rakam değişti","gerekce":"...","guven":"orta"}],
             "tutarli_gozlemler":[],"genel_uyari":"Rakamı yazılı iste.","yetersiz":false}
            """);

        Assert.Equal(Staleness.Fresh, _repo.DerivedFreshness(_callId).Consistency);
        Assert.False(Window().IsConsistencyStale);

        // The call site itself, which no unit test can reach.
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "VoiceTranscript.App", "ViewModels", "CallWindowViewModel.cs"));

        foreach (var (method, next) in new[]
                 {
                     ("private async Task CheckConsistencyAsync", "// ---- suggested actions"),
                     ("private void LoadActions", "/// <summary>The user's verdict on one suggestion"),
                     ("private async Task RunReadingAsync", "// ---- the opt-in deception assessment"),
                     ("private async Task RunDeceptionAsync", "// ---- notes"),
                 })
        {
            var start = source.IndexOf(method, StringComparison.Ordinal);
            var end = source.IndexOf(next, StringComparison.Ordinal);

            Assert.True(start > 0, $"{method} bulunamadı.");
            Assert.True(end > start, $"{method} için bitiş işareti bulunamadı: {next}");

            Assert.Contains("RefreshFreshness()", source[start..end], StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
