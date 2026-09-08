using System.Net;
using System.Net.Http;
using System.Text;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Worker;

namespace VoiceTranscript.Tests;

/// <summary>An analysis provider whose account is empty: every request refused in OpenAI's own words.</summary>
internal sealed class QuotaEmptyAccount : HttpMessageHandler
{
    private const string Body =
        """{"error":{"message":"You have no credits remaining. Add credits to continue using the API.","type":"insufficient_quota","param":null,"code":"credit_balance_exhausted"}}""";

    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// What the queue does with a transcript when the analysis account is out of money.
///
/// In the log this came from, every conversation behind the first refusal was sent to be refused
/// the same way, and each came back marked "işlenemedi" over a transcript that was fine. Driven
/// through the real orchestrator and the real pipeline against a provider that only ever answers
/// 429 insufficient_quota; no worker is needed because the calls are already transcribed.
/// </summary>
public sealed class QuotaExhaustedQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-quota-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repository;

    public QuotaExhaustedQueueTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder is swept anyway.
        }
    }

    private static AppSettings Settings(string key = "sk-test") => new()
    {
        AnalyseAutomatically = true,
        LlmProvider = LlmProviderKind.OpenAi,
        LlmApiKey = key,
        LlmRemoteModel = "gpt-test",
        GpuCooldownSeconds = 0,
        ExportToObsidian = false,
        ExportToNotion = false,
        CompressAudioAfterProcessing = false,
        HabitCountingEnabled = false,
        ProsodyMeasurementEnabled = false,
        ExtractActions = false,
        ConsistencyAutomatically = false,
    };

    /// <summary>A call whose words are already in the database, waiting only for its ledger.</summary>
    private long Transcribed(string text)
    {
        var contact = _repository.UpsertContact("Ahmet", CallApp.WhatsApp);

        var call = _repository.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            Kind = CallKind.OneToOne,
            StartedAt = DateTimeOffset.Now.AddMinutes(-5),
            Duration = TimeSpan.FromMinutes(2),
            State = ProcessingState.Transcribed,
        });

        _repository.ReplaceSegments(call, new[]
        {
            new Segment { CallId = call, IsMe = true, StartMs = 0, EndMs = 3000, Text = text },
            new Segment { CallId = call, IsMe = false, StartMs = 4000, EndMs = 7000, Text = "Tamam, bekliyorum." },
        });

        return call;
    }

    private (CallOrchestrator Orchestrator, QuotaEmptyAccount Account, List<string> Notices) Build(AppSettings settings)
    {
        var account = new QuotaEmptyAccount();

        // Never reached: the calls are transcribed already and prosody is off, so nothing asks
        // the worker for anything. It exists because the orchestrator wants one.
        var worker = new PythonWorkerHost(new PythonWorkerOptions
        {
            PythonExecutable = "python",
            WorkerDirectory = _root,
            ModelCacheDirectory = _paths.Models,
            Timeout = TimeSpan.FromSeconds(2),
        });

        var orchestrator = new CallOrchestrator(_paths, _repository, () => settings, () => worker, new HttpClient(account));

        var notices = new List<string>();
        orchestrator.Notice += (_, text) => notices.Add(text);

        return (orchestrator, account, notices);
    }

    [Fact]
    public async Task AnAccountWithNoCreditLeavesTheTranscriptStandingAndSaysWhy()
    {
        var settings = Settings();
        var (orchestrator, account, notices) = Build(settings);
        using var _ = orchestrator;

        var call = Transcribed("Evrakları yarın gönderirim.");

        await orchestrator.ProcessAsync(call, settings);

        var row = _repository.GetCall(call)!;

        Assert.Equal(ProcessingState.Transcribed, row.State);
        Assert.Contains("bakiye", row.FailureReason);
        Assert.Equal(1, account.Requests);
        Assert.Contains(notices, n => n.Contains("bakiye"));
    }

    [Fact]
    public async Task TheCallsBehindItAreNotSentToBeRefusedTheSameWay()
    {
        var settings = Settings();
        var (orchestrator, account, _) = Build(settings);
        using var __ = orchestrator;

        await orchestrator.ProcessAsync(Transcribed("Birinci görüşme."), settings);

        var second = Transcribed("İkinci görüşme.");
        await orchestrator.ProcessAsync(second, settings);

        // One refusal was the whole answer; the second conversation was filed without a request.
        Assert.Equal(1, account.Requests);

        var row = _repository.GetCall(second)!;
        Assert.Equal(ProcessingState.Transcribed, row.State);
        Assert.Contains("bakiye", row.FailureReason);
    }

    [Fact]
    public async Task ADifferentKeyIsADifferentAccountAndIsAskedAtOnce()
    {
        var (orchestrator, account, _) = Build(Settings());
        using var __ = orchestrator;

        await orchestrator.ProcessAsync(Transcribed("Birinci görüşme."), Settings());
        await orchestrator.ProcessAsync(Transcribed("İkinci görüşme."), Settings(key: "sk-replaced"));

        Assert.Equal(2, account.Requests);
    }
}
