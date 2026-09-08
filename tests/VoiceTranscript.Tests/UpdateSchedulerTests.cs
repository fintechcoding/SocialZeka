using System.Net;
using System.Text.Json;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.Tests;

/// <summary>
/// The daily re-check for a release, while the application stays running.
///
/// The check used to run only at startup, and a copy left open in the tray since yesterday
/// afternoon never learned that this morning's release carried the fixes its user was still
/// complaining about. These tests pin the decisions that close that gap without loosening the
/// rule around it: it checks and asks, it never installs, and it never runs beside a recording.
///
/// The service is real and the network is not: the double is an <see cref="HttpMessageHandler"/>
/// that counts what would have reached GitHub. A scheduler that "checked" without a request is
/// not a scheduler.
/// </summary>
public sealed class UpdateSchedulerTests
{
    /// <summary>A release that reads as an answer with nothing to offer: a draft.</summary>
    private const string NothingToOffer = """{"draft":true}""";

    /// <summary>A release the running (0.0.0-dev) build is behind, with everything a real one has.</summary>
    private const string Found = """
        {"tag_name":"v99.0.0","body":"Yenilikler","assets":[
          {"name":"SocialZeka-Setup-99.0.0-win-x64.exe","browser_download_url":"https://example/setup.exe","size":68000000},
          {"name":"SHA256SUMS","browser_download_url":"https://example/SHA256SUMS","size":100}]}
        """;

    private sealed class Counting : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = NothingToOffer;

        /// <summary>When set, every request waits here before answering.</summary>
        public TaskCompletionSource? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);

            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }

    /// <summary>
    /// Starts never checked, which is due by definition — so a test about idleness, the stamp,
    /// overlap or delivery does not also depend on how the day is measured. The tests about the
    /// day set a stamp themselves.
    /// </summary>
    private sealed class Harness
    {
        public Counting Http { get; } = new();
        public AppSettings Settings { get; set; } = new();
        public DateTimeOffset Now { get; set; } = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);
        public bool Idle { get; set; } = true;
        public List<UpdateCheck> Delivered { get; } = [];
        public UpdateScheduler Scheduler { get; }

        public Harness()
        {
            var service = new UpdateService(
                new HttpClient(Http),
                new AppPaths(Path.Combine(Path.GetTempPath(), $"vt-schedule-{Guid.NewGuid():N}")));

            Scheduler = new UpdateScheduler(
                service,
                () => Settings,
                saved => Settings = saved,
                () => Idle,
                check =>
                {
                    Delivered.Add(check);
                    return Task.CompletedTask;
                },
                () => Now,
                resumeGrace: TimeSpan.Zero);
        }

        /// <summary>The stamp of a check made this long ago.</summary>
        public void LastCheckedAgo(TimeSpan ago) => Settings = Settings with { LastUpdateCheck = Now - ago };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Red means the interval no longer fires: a copy left running learns of a release only at its
    /// next start, which is the day this scheduler exists to end.
    /// </summary>
    [Fact]
    public async Task ADayAfterTheLastCheckTheTickChecks()
    {
        var h = new Harness();
        h.LastCheckedAgo(TimeSpan.FromHours(25));

        Assert.True(await h.Scheduler.TickAsync(Ct));
        Assert.Equal(1, h.Http.Requests);
    }

    /// <summary>
    /// Red means the tick checks on every evaluation — ninety-six requests a day against a limit
    /// of sixty an hour that is shared with everything else on the address — or that the day is
    /// counted from something other than the last check.
    /// </summary>
    [Fact]
    public async Task ATickBeforeTheDayIsUpDoesNothing()
    {
        var h = new Harness();
        h.LastCheckedAgo(TimeSpan.FromHours(1));

        Assert.False(await h.Scheduler.TickAsync(Ct));

        h.LastCheckedAgo(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));

        Assert.False(await h.Scheduler.TickAsync(Ct));
        Assert.Equal(0, h.Http.Requests);
    }

    /// <summary>
    /// Red means the switch stopped governing the daily check: somebody who turned off "check
    /// automatically" so that their machine does not contact GitHub is contacted every day
    /// anyway — through the tick, the idle notice or the resume, whichever is broken.
    /// </summary>
    [Fact]
    public async Task TheSwitchOffMeansNoPeriodicCheckByAnyRoute()
    {
        var h = new Harness();
        h.Settings = h.Settings with { CheckForUpdates = false };
        h.LastCheckedAgo(TimeSpan.FromDays(30));

        Assert.False(await h.Scheduler.TickAsync(Ct));
        await h.Scheduler.NotifyIdleAsync();
        await h.Scheduler.ResumedAsync(Ct);

        Assert.Equal(0, h.Http.Requests);
        Assert.Empty(h.Delivered);
    }

    /// <summary>
    /// Red on the first assertion means a check runs beside a recording or a transcription — a
    /// network call and an offer dialog on top of the one thing the application is for. Red on the
    /// second means a check postponed by a call waits for the next tick rather than running the
    /// moment the recorder says idle, or never runs at all.
    /// </summary>
    [Fact]
    public async Task NoCheckWhileACallIsInProgressAndOneOnceIdle()
    {
        var h = new Harness { Idle = false };

        Assert.False(await h.Scheduler.TickAsync(Ct));
        Assert.Equal(0, h.Http.Requests);

        h.Idle = true;
        await h.Scheduler.NotifyIdleAsync();

        Assert.Equal(1, h.Http.Requests);
    }

    /// <summary>
    /// Red means "Son denetim" on the Güncelleme tab stays at the last button press, and the user
    /// has no way to see that the daily check is alive — the exact blindness that cost the day.
    /// </summary>
    [Fact]
    public async Task TheStampRecordsThePeriodicCheck()
    {
        var h = new Harness();
        h.Now += TimeSpan.FromMinutes(7);

        Assert.Null(h.Settings.LastUpdateCheck);

        await h.Scheduler.TickAsync(Ct);

        Assert.Equal(h.Now, h.Settings.LastUpdateCheck);
    }

    /// <summary>
    /// Red means the laptop closed for a week waits another day from when it woke, or never
    /// checks on wake at all: the stamp is a week old and nothing looked at it.
    /// </summary>
    [Fact]
    public async Task ResumeAfterALongSleepChecksOnWake()
    {
        var h = new Harness();
        h.LastCheckedAgo(TimeSpan.Zero);

        // The lid closes; nothing ticks for a week; the lid opens.
        h.Now += TimeSpan.FromDays(7);
        await h.Scheduler.ResumedAsync(Ct);

        Assert.Equal(1, h.Http.Requests);
    }

    /// <summary>
    /// Red means the wake-up hook no longer hands a resume to <c>ResumedAsync</c>.
    ///
    /// The test above drives <c>ResumedAsync</c> directly and pins what a resume DOES; it
    /// cannot see whether Windows' resume ever reaches it, because <c>SystemEvents</c> is a
    /// static event with no seam and the handler is private. Disabling that handler outright
    /// left every behavioural test green — measured, not assumed — so the bridge is pinned the
    /// way this repository pins wiring it cannot drive: by reading the source. The fifteen-minute
    /// tick remains the guarantee; the hook is what makes a week-closed laptop check on the
    /// second rather than on the quarter-hour, and losing it silently is still a regression.
    /// </summary>
    [Fact]
    public void TheWakeUpHookStillForwardsAResumeToTheCheck()
    {
        var source = File.ReadAllText(Path.Combine(
            InterfaceContractSources.Root, "src", "VoiceTranscript.App", "Services", "UpdateScheduler.cs"));

        var handler = source.IndexOf("private void OnPowerModeChanged(", StringComparison.Ordinal);
        Assert.True(handler >= 0, "OnPowerModeChanged yok: uyanma kancası kaldırılmış.");

        var end = source.IndexOf("public void Dispose()", handler, StringComparison.Ordinal);
        var body = source[handler..(end < 0 ? source.Length : end)];

        Assert.Contains("PowerModes.Resume", body);
        Assert.Contains("ResumedAsync(", body);
        Assert.Contains("SystemEvents.PowerModeChanged += OnPowerModeChanged", source);
    }

    /// <summary>
    /// Red means a check on a network that was not up yet — the first minute after a resume — is
    /// stamped as the day's check, and the next look is a day away. The stamp still moves, because
    /// the tab reports that a check was made, not that it was answered.
    /// </summary>
    [Fact]
    public async Task ACheckThatGotNoAnswerIsRetriedWithinTheHourNotTheDay()
    {
        var h = new Harness();
        h.Http.Status = HttpStatusCode.ServiceUnavailable;

        Assert.True(await h.Scheduler.TickAsync(Ct));
        Assert.Equal(h.Now, h.Settings.LastUpdateCheck);

        h.Now += TimeSpan.FromMinutes(30);
        Assert.False(await h.Scheduler.TickAsync(Ct));

        h.Now += TimeSpan.FromMinutes(31);
        Assert.True(await h.Scheduler.TickAsync(Ct));
        Assert.Equal(2, h.Http.Requests);

        // Answered. From here the day applies again.
        h.Http.Status = HttpStatusCode.OK;
        h.Now += TimeSpan.FromHours(1);
        Assert.True(await h.Scheduler.TickAsync(Ct));

        h.Now += TimeSpan.FromHours(2);
        Assert.False(await h.Scheduler.TickAsync(Ct));
        Assert.Equal(3, h.Http.Requests);
    }

    /// <summary>
    /// Red means a tick, an idle notice and a resume landing together make three requests and
    /// could open three offers for the same release.
    /// </summary>
    [Fact]
    public async Task TwoOverlappingTicksMakeOneRequest()
    {
        var h = new Harness();
        h.Http.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = h.Scheduler.TickAsync(Ct);
        Assert.Equal(1, h.Http.Requests);

        Assert.False(await h.Scheduler.TickAsync(Ct));

        h.Http.Hold.SetResult();

        Assert.True(await first);
        Assert.Equal(1, h.Http.Requests);
    }

    /// <summary>
    /// Red means the daily check finds a release and tells nobody — or hands it to something other
    /// than the path the startup check uses, which is the only path allowed to open the offer.
    /// </summary>
    [Fact]
    public async Task AFoundReleaseIsHandedToTheSameNoticeAsTheStartupCheck()
    {
        var h = new Harness();
        h.Http.Body = Found;

        await h.Scheduler.TickAsync(Ct);

        var check = Assert.Single(h.Delivered);
        Assert.True(check.Available);
        Assert.Equal("99.0.0", check.Release!.Version.ToString());
        Assert.False(check.Failed);
    }

    /// <summary>
    /// The startup check goes through this. Red on the request means startup stopped checking
    /// because somebody pressed the button five minutes before the restart; red on the stamp
    /// means the startup check no longer moves it, so the day is counted from the last button
    /// press rather than the last look and a copy started this morning is checked again at noon.
    /// </summary>
    [Fact]
    public async Task CheckingNowIgnoresTheScheduleAndStillStamps()
    {
        var h = new Harness();
        h.LastCheckedAgo(TimeSpan.FromMinutes(5));

        var check = await h.Scheduler.CheckNowAsync(Ct);

        Assert.NotNull(check);
        Assert.Equal(1, h.Http.Requests);
        Assert.Equal(h.Now, h.Settings.LastUpdateCheck);
    }

    /// <summary>
    /// Red means the switch on the Güncelleme tab still says "at startup" while it governs the
    /// daily check too, so somebody switches it off believing the daily one stays — or the tab
    /// went back to the old label in one language and not the other.
    /// </summary>
    [Fact]
    public void TheSwitchLabelSaysWhatItNowGoverns()
    {
        const string key = "healthpage.kendiliginden-denetle-acilista-ve-gunde-bir";

        var root = FindRepositoryRoot();
        var tr = ReadStrings(root, "tr");
        var en = ReadStrings(root, "en");

        Assert.Contains("açılışta", tr[key], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("günde bir", tr[key], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startup", en[key], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once a day", en[key], StringComparison.OrdinalIgnoreCase);

        var markup = File.ReadAllText(Path.Combine(root, "src", "VoiceTranscript.App", "Views", "HealthPage.xaml"));

        Assert.Contains($"{{loc:T {key}}}", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("{loc:T healthpage.acilista-kendiliginden-denetle}", markup, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ReadStrings(string root, string code)
    {
        var path = Path.Combine(root, "src", "VoiceTranscript.Core", "Resources", $"strings.{code}.json");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
               ?? throw new InvalidOperationException(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
