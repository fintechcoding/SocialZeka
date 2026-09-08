using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>A provider that refuses every request the same way, and counts how often it was asked.</summary>
file sealed class Refusing(Func<LlmException> refusal, string? firstReply = null) : ILlmClient
{
    public int Requests { get; private set; }

    public LlmProviderKind Kind => LlmProviderKind.OpenAi;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Requests++;

        return Requests == 1 && firstReply is { } reply
            ? Task.FromResult(new LlmResponse(reply, "stop", 100, 50))
            : Task.FromException<LlmResponse>(refusal());
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// What a run reports when the provider refuses it.
///
/// Since the pipeline stopped throwing for a refusal on one section, an account with no credit
/// produced a run in which every section was refused, the report came back empty, and the call
/// was filed as analysed with an empty ledger. The report has to say that nothing was read, and
/// why — and a refusal about money has to stop the run asking for the sections that remain.
/// </summary>
public sealed class AnalysisRefusalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-refusal-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public AnalysisRefusalTests()
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

    /// <summary>One section per line, so a three-line call is three paid requests.</summary>
    private static readonly AnalysisOptions Options = new()
    {
        Model = "test-model",
        ChunkTokens = 1,
        AdjudicateContradictions = false,
        WriteSummary = false,
    };

    private const string Empty = """{"taahhutler":[],"iddialar":[],"sorular":[],"baski_isaretleri":[]}""";

    private long Seed()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);

        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.Telegram,
            Kind = CallKind.OneToOne,
            StartedAt = DateTimeOffset.Parse("2026-09-07T20:00:00+03:00"),
            State = ProcessingState.Transcribed,
        });

        _repo.AssignContact(call, contact);

        _repo.ReplaceSegments(call, new[]
        {
            new Segment { CallId = call, IsMe = true, StartMs = 0, EndMs = 3000, Text = "Evrakları yarın gönderirim." },
            new Segment { CallId = call, IsMe = false, StartMs = 4000, EndMs = 7000, Text = "Fatura on sekiz bin." },
            new Segment { CallId = call, IsMe = true, StartMs = 8000, EndMs = 11000, Text = "Tamam, cuma görüşürüz." },
        });

        return call;
    }

    private static LlmException OutOfCredit() =>
        new("Çözümleme servisinin bakiyesi ya da kotası bitmiş görünüyor.") { QuotaExhausted = true };

    private static LlmException Overloaded() =>
        new("Çözümleme servisi şu an yoğun.");

    [Fact]
    public async Task AnAccountWithNoCreditIsAskedOnceAndTheReportSaysWhyNothingWasRead()
    {
        var provider = new Refusing(OutOfCredit);
        var call = Seed();

        var report = await new AnalysisPipeline(provider, _repo).AnalyseAsync(call, Options);

        Assert.True(report.NothingRead);
        Assert.True(report.QuotaExhausted);
        Assert.Equal(0, report.CommitmentsFound);

        // Three sections, one request: the other two were filed as unread without being sent.
        Assert.Equal(1, provider.Requests);
        Assert.Contains(report.Warnings, w => w.Contains("bakiye"));
    }

    [Fact]
    public async Task ABusyProviderIsAskedForEverySectionAndIsNotMistakenForAnEmptyAccount()
    {
        var provider = new Refusing(Overloaded);
        var call = Seed();

        var report = await new AnalysisPipeline(provider, _repo).AnalyseAsync(call, Options);

        Assert.True(report.NothingRead);
        Assert.False(report.QuotaExhausted);
        Assert.Equal(3, provider.Requests);

        // The row will carry the provider's own sentence, not the number of a section.
        Assert.Contains("yoğun", report.Refusal);
    }

    [Fact]
    public async Task CreditRunningOutPartwayKeepsWhatWasReadAndStillSaysTheAccountIsEmpty()
    {
        var provider = new Refusing(OutOfCredit, firstReply: Empty);
        var call = Seed();

        var report = await new AnalysisPipeline(provider, _repo).AnalyseAsync(call, Options);

        Assert.False(report.NothingRead);
        Assert.True(report.Partial);
        Assert.True(report.QuotaExhausted);

        // The first section was read, the second refused, the third never asked for.
        Assert.Equal(2, provider.Requests);
    }

    [Fact]
    public async Task ARunTheProviderAnswersInFullReportsNeither()
    {
        var provider = new Refusing(OutOfCredit, firstReply: Empty);
        var call = Seed();

        // Only one line: one section, one answered request, nothing refused.
        _repo.ReplaceSegments(call, new[]
        {
            new Segment { CallId = call, IsMe = true, StartMs = 0, EndMs = 3000, Text = "Evrakları yarın gönderirim." },
        });

        var report = await new AnalysisPipeline(provider, _repo).AnalyseAsync(call, Options);

        Assert.False(report.NothingRead);
        Assert.False(report.QuotaExhausted);
        Assert.False(report.Partial);
    }
}
