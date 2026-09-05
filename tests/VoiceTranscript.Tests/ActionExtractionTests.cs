using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

file sealed class ScriptedLlm(params string[] replies) : ILlmClient
{
    private int _next;

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        return Task.FromResult(new LlmResponse(reply, "stop", 100, 50));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The action layer, held to the same law as every other machine surface: an unanchored
/// suggestion dies, a restated promise dies, a hidden suggestion stays dead, and a re-run
/// never touches what the user already judged.
/// </summary>
public sealed class ActionExtractionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-act-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public ActionExtractionTests()
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

    private (long callId, long contactId) Seed(params (bool me, int ms, string text)[] lines)
        => SeedOn(DateTimeOffset.UtcNow, lines);

    /// <summary>The same call, placed on a chosen day — for the tests about when things were said.</summary>
    private (long callId, long contactId) SeedOn(DateTimeOffset startedAt, params (bool me, int ms, string text)[] lines)
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = startedAt,
            State = ProcessingState.Analysed,
        });
        _repo.AssignContact(call, contact);

        _repo.ReplaceSegments(call, lines.Select(l => new Segment
        {
            CallId = call, IsMe = l.me, StartMs = l.ms, EndMs = l.ms + 3000, Text = l.text,
        }));

        return (call, contact);
    }

    private static string Reply(params string[] items) =>
        $$"""{"aksiyonlar":[{{string.Join(",", items)}}]}""";

    private static string Item(
        string eylem, string alinti, string tur = "diger", string? tarihHam = null)
    {
        var tail = tarihHam is null ? "" : $$""","tarih_ham":"{{tarihHam}}" """;
        return $$"""{"eylem":"{{eylem}}","neden":"test","tur":"{{tur}}","alinti":"{{alinti}}"{{tail}}}""";
    }

    private Task<ActionReport> Run(long call, string reply) =>
        new ActionExtraction(new ScriptedLlm(reply), _repo)
            .RunAsync(call, "test-model", TestContext.Current.CancellationToken);

    [Fact]
    public async Task AVerifiedSuggestionIsStoredWithItsTimestampDeadlineAndUsageStage()
    {
        var (call, _) = Seed((false, 5_000, "Fiyatı bir de e-postayla teyit edelim."));

        var report = await Run(call, Reply(Item(
            "Fiyatı e-postayla yazılı teyit et",
            "Fiyatı bir de e-postayla teyit edelim",
            tur: "yazili_teyit", tarihHam: "yarın")));

        Assert.True(report.Ok, report.Problem);
        var action = Assert.Single(report.Actions);
        Assert.Equal(5_000, action.QuoteStartMs);
        Assert.Equal("yazili_teyit", action.Kind);
        Assert.Equal("yarın", action.DeadlineRaw);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now).AddDays(1), action.DeadlineDate);
        Assert.Equal(ActionStatus.Open, action.Status);

        // Persisted, not just returned — and the spend is booked under its own stage.
        Assert.Single(_repo.ActionsOf(call));
        Assert.NotNull(_repo.LastRun(call, ProcessingStage.Action));
    }

    /// <summary>
    /// "Yarın" in an old call is the day after that call, not the day after this test runs.
    ///
    /// The action layer resolved deadlines against the clock, so re-running it on an old call
    /// re-dated every suggestion into the present. The call date is pinned in the past: a
    /// fallback to today lands weeks away and fails.
    /// </summary>
    [Fact]
    public async Task ADeadlineIsCountedFromTheCallDateNotFromToday()
    {
        // Wednesday 12 August 2026, at noon local so no time zone can move the date.
        var (call, _) = SeedOn(
            new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(3)),
            (false, 5_000, "Fiyatı bir de e-postayla teyit edelim."));

        var report = await Run(call, Reply(Item(
            "Fiyatı e-postayla yazılı teyit et",
            "Fiyatı bir de e-postayla teyit edelim",
            tur: "yazili_teyit", tarihHam: "yarın")));

        Assert.True(report.Ok, report.Problem);
        var action = Assert.Single(report.Actions);
        Assert.Equal(new DateOnly(2026, 8, 13), action.DeadlineDate);
    }

    [Fact]
    public async Task ASuggestionWhoseQuoteIsNotInTheTranscriptDies()
    {
        var (call, _) = Seed((false, 5_000, "Fiyat konusunu sonra konuşalım."));

        var report = await Run(call, Reply(Item(
            "Sözleşmeyi imzala", "Sözleşmeyi hemen imzalayalım dedi")));

        Assert.Empty(report.Actions);
        Assert.Equal(1, report.RejectedCount);
        Assert.Empty(_repo.ActionsOf(call));
    }

    [Fact]
    public async Task ARestatedCommitmentDiesButAFollowUpOnItStands()
    {
        var (call, contact) = Seed((false, 3_000, "Belgeyi yarın sana göndereceğim"));

        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, ByMe = false,
            Quote = "Belgeyi yarın sana göndereceğim", QuoteStartMs = 3_000,
            Obligation = "Belgeyi gönderecek",
        });

        // "Send the document" anchored to the recorded promise is the promise restated.
        var copy = await Run(call, Reply(Item(
            "Belgeyi göndermesini bekle", "Belgeyi yarın sana göndereceğim", tur: "gonderme")));

        Assert.Empty(copy.Actions);
        Assert.Equal(0, copy.RejectedCount);

        // "Chase it if it does not arrive" anchored to the same words is a follow-up and stands.
        var followUp = await Run(call, Reply(Item(
            "Belge yarın gelmezse tekrar sor", "Belgeyi yarın sana göndereceğim", tur: "takip")));

        Assert.Single(followUp.Actions);
    }

    [Fact]
    public async Task AHiddenSuggestionIsNeverResurrectedByAReRun()
    {
        var (call, _) = Seed((false, 4_000, "Kaporayı bu hafta yatırmanız lazım."));

        var reply = Reply(Item("Kapora tutarını yazılı iste", "Kaporayı bu hafta yatırmanız lazım"));

        var first = await Run(call, reply);
        _repo.SetActionStatus(Assert.Single(first.Actions).Id, ActionStatus.Hidden);

        var second = await Run(call, reply);

        Assert.Empty(second.Actions);
        var stored = Assert.Single(_repo.ActionsOf(call, includeClosed: true));
        Assert.Equal(ActionStatus.Hidden, stored.Status);
    }

    [Fact]
    public async Task AReRunReplacesOpenRowsOnlyAndDoesNotMultiply()
    {
        var (call, _) = Seed(
            (false, 2_000, "Sözleşme taslağını bir avukata gösterin bence."),
            (false, 9_000, "Fiyat listesini de isteyin."));

        var first = await Run(call, Reply(Item(
            "Taslağı avukata göster", "Sözleşme taslağını bir avukata gösterin bence")));
        _repo.SetActionStatus(Assert.Single(first.Actions).Id, ActionStatus.Done);

        var second = await Run(call, Reply(Item(
            "Fiyat listesini iste", "Fiyat listesini de isteyin")));
        Assert.Single(second.Actions);

        // The done row is the user's history and survived; the open set was replaced.
        var all = _repo.ActionsOf(call, includeClosed: true);
        Assert.Equal(2, all.Count);
        Assert.Single(all, a => a.Status == ActionStatus.Done);

        // Running the same reply again replaces rather than appends.
        await Run(call, Reply(Item("Fiyat listesini iste", "Fiyat listesini de isteyin")));
        Assert.Equal(2, _repo.ActionsOf(call, includeClosed: true).Count);
    }

    [Fact]
    public async Task TheSixthSuggestionIsCroppedInCode()
    {
        var lines = Enumerable.Range(1, 6)
            .Select(i => (false, i * 2_000, $"Madde {i} için yazılı teyit isteyin."))
            .ToArray();
        var (call, _) = Seed(lines);

        var report = await Run(call, Reply(Enumerable.Range(1, 6)
            .Select(i => Item($"Madde {i} teyidini iste", $"Madde {i} için yazılı teyit isteyin"))
            .ToArray()));

        Assert.Equal(ActionExtraction.MaxActions, report.Actions.Count);
        Assert.Equal(ActionExtraction.MaxActions, _repo.ActionsOf(call).Count);
    }
}
