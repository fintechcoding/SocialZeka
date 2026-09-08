using System.Net.Http;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// When a person's reading is worth paying for again.
///
/// The reading now refreshes itself after a conversation instead of waiting for somebody to press
/// a button, which turns a deliberate purchase into an automatic one — so the refusals matter more
/// than they did. Each of these is a request NOT sent: the switch is off, the archive has not
/// moved since the stored reading, or the call has nobody attached to it.
///
/// The one that is not tested here is the third refusal, "too little on record": it lives inside
/// <see cref="ContactReadingAnalysis"/> and has its own test there. What this class protects is
/// the decision to ask at all.
/// </summary>
public sealed class ContactReadingRefresherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-okuma-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Database _database;
    private readonly Repository _repo;
    private readonly HttpClient _http = new();
    private readonly long _contact;

    public ContactReadingRefresherTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        _database = new Database(_paths.DatabaseFile);
        _database.Migrate();
        _repo = new Repository(_database);

        _contact = _repo.UpsertContact("Samet", CallApp.WhatsApp);
    }

    public void Dispose()
    {
        _http.Dispose();
        _database.ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A configured cloud provider, so the only thing under test is the refresher's own rules.</summary>
    private static AppSettings Configured(bool enabled = true) => new()
    {
        ContactReadingEnabled = enabled,
        LlmProvider = LlmProviderKind.OpenAi,
        LlmApiKey = "sk-test",
        LlmRemoteModel = "gpt-test",
    };

    private long Call(CallKind kind = CallKind.OneToOne)
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            Kind = kind,
            StartedAt = DateTimeOffset.Now.AddMinutes(-30),
            Duration = TimeSpan.FromMinutes(4),
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);

        _repo.ReplaceSegments(call, new[]
        {
            new Segment { CallId = call, IsMe = true, StartMs = 0, EndMs = 3000, Text = "Evrakları yarın gönderirim." },
            new Segment { CallId = call, IsMe = false, StartMs = 4000, EndMs = 7000, Text = "Tamam, bekliyorum." },
        });

        return call;
    }

    // ---- the refusals ---------------------------------------------------------------------

    [Fact]
    public async Task TheSwitchBeingOffIsTheFirstRefusal()
    {
        var call = Call();

        Assert.False(await ContactReadingRefresher.RefreshIfStaleAsync(
            _repo, _http, call, Configured(enabled: false), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The feature that turned itself off stays off, and the automatic path must honour that too.
    ///
    /// Three people in a row saying "Katılmıyorum" disables the reading. That rule was written for
    /// a button somebody presses; an automatic refresh that ignored it would quietly buy the very
    /// readings the measurement just rejected.
    /// </summary>
    [Fact]
    public async Task AFeatureThatMeasuredBadlyIsNotRestartedByTheAutomaticPath()
    {
        var call = Call();
        var settings = Configured() with { ContactReadingMeasuredNegative = true };

        Assert.False(await ContactReadingRefresher.RefreshIfStaleAsync(
            _repo, _http, call, settings, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACallWithNobodyAttachedIsNobodyToRead()
    {
        var orphan = _repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now,
            Duration = TimeSpan.FromMinutes(2),
            State = ProcessingState.Analysed,
        });

        Assert.False(await ContactReadingRefresher.RefreshIfStaleAsync(
            _repo, _http, orphan, Configured(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoProviderConfiguredIsNoRequestToMake()
    {
        var call = Call();

        Assert.False(await ContactReadingRefresher.RefreshIfStaleAsync(
            _repo, _http, call, new AppSettings { ContactReadingEnabled = true, LlmApiKey = null },
            TestContext.Current.CancellationToken));
    }

    // ---- staleness ------------------------------------------------------------------------

    /// <summary>
    /// With no reading at all, everything is stale: the first conversation that reaches the floor
    /// is the one that earns the first reading.
    /// </summary>
    [Fact]
    public void WithNoStoredReadingThePersonIsStale()
    {
        Call();

        Assert.True(ContactReadingRefresher.IsStale(_repo, _contact));
    }

    /// <summary>
    /// A reading made from exactly today's history is not bought again.
    ///
    /// This is the refusal that keeps an automatic refresh from becoming a bill: a person is read
    /// once per new conversation, not once per conversation ending anywhere in the archive.
    /// </summary>
    [Fact]
    public void AReadingMadeFromTodaysHistoryIsNotStale()
    {
        var call = Call();

        var versions = _repo.TranscriptVersionsOf(_contact);
        var hash = ContactReadingAnalysis.InputHash(
            _repo.ContactSeries(_contact).Select(p => (p.CallId, versions.GetValueOrDefault(p.CallId))));

        _repo.SaveContactReading(_contact, "{}", "gpt-test", 1, call, hash, 20, 0);

        Assert.False(ContactReadingRefresher.IsStale(_repo, _contact));
    }

    /// <summary>And a new conversation makes it stale again — which is the whole trigger.</summary>
    [Fact]
    public void ANewConversationMakesTheReadingStale()
    {
        var call = Call();

        var versions = _repo.TranscriptVersionsOf(_contact);
        var hash = ContactReadingAnalysis.InputHash(
            _repo.ContactSeries(_contact).Select(p => (p.CallId, versions.GetValueOrDefault(p.CallId))));

        _repo.SaveContactReading(_contact, "{}", "gpt-test", 1, call, hash, 20, 0);
        Assert.False(ContactReadingRefresher.IsStale(_repo, _contact));

        Call();

        Assert.True(ContactReadingRefresher.IsStale(_repo, _contact));
    }

    /// <summary>
    /// A group call is nobody's conversation, so it neither ages a reading nor earns one: the far
    /// channel carries several voices and nothing on it belongs to one person.
    /// </summary>
    [Fact]
    public void AGroupCallDoesNotAgeTheReading()
    {
        var call = Call();

        var versions = _repo.TranscriptVersionsOf(_contact);
        var hash = ContactReadingAnalysis.InputHash(
            _repo.ContactSeries(_contact).Select(p => (p.CallId, versions.GetValueOrDefault(p.CallId))));

        _repo.SaveContactReading(_contact, "{}", "gpt-test", 1, call, hash, 20, 0);

        Call(CallKind.Group);

        Assert.False(ContactReadingRefresher.IsStale(_repo, _contact));
    }
}
