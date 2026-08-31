using VoiceTranscript.Core.Detection;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Every scenario here is a real thing that happens on a desktop while WhatsApp or Telegram is
/// running. The false-positive cases matter as much as the happy path: recording a voice note
/// or a notification chime as if it were a conversation would fill the archive with rubbish.
/// </summary>
public class CallDetectorTests
{
    private static DateTimeOffset T0 => new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock
    {
        private DateTimeOffset _now = T0;

        /// <summary>The current instant, without moving.</summary>
        public DateTimeOffset Now => _now;

        /// <summary>The current instant, then one second later. Matches the one-hertz poll.</summary>
        public DateTimeOffset Next()
        {
            var now = _now;
            _now = _now.AddSeconds(1);
            return now;
        }

        /// <summary>Jumps forward, for the cases where feeding a sample per second would be absurd.</summary>
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static DetectionSample Sample(
        DateTimeOffset at, bool render, bool capture, bool window = false, string? title = null)
        => new(at, render, capture, window, title, CallApp.Telegram);

    /// <summary>Feeds n samples and returns every event produced.</summary>
    private static List<CallEvent> Feed(
        CallDetector detector, Clock clock, int count, bool render, bool capture,
        bool window = false, string? title = null)
    {
        var events = new List<CallEvent>();

        for (var i = 0; i < count; i++)
        {
            var e = detector.Observe(Sample(clock.Next(), render, capture, window, title));
            if (e is not null) events.Add(e);
        }

        return events;
    }

    [Fact]
    public void StartsIdle() => Assert.Equal(CallState.Idle, new CallDetector().State);

    [Fact]
    public void AnsweredCallIsDetected()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        // Ringing: audio out, microphone still closed.
        Feed(detector, clock, 3, render: true, capture: false, window: true);
        Assert.Equal(CallState.Ringing, detector.State);

        // Answered: the microphone opens and stays open.
        var events = Feed(detector, clock, 3, render: true, capture: true, window: true);

        Assert.Equal(CallState.InCall, detector.State);
        Assert.Equal(CallEventKind.Started, Assert.Single(events).Kind);
    }

    [Fact]
    public void CallEndsWhenBothStreamsStopForLongEnough()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true);
        Feed(detector, clock, 3, true, true, window: true);
        Feed(detector, clock, 30, true, true, window: true);

        var events = Feed(detector, clock, 8, render: false, capture: false, window: false);

        var ended = Assert.Single(events);
        Assert.Equal(CallEventKind.Ended, ended.Kind);
        Assert.Equal(CallState.Idle, detector.State);
        Assert.True(ended.Duration > TimeSpan.Zero);
    }

    /// <summary>
    /// The confirmation delay must not be billed as conversation: the call ended when the audio
    /// stopped, not six seconds later when we became sure of it.
    /// </summary>
    [Fact]
    public void DurationExcludesTheSilenceUsedToConfirmTheEnd()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true);
        Feed(detector, clock, 3, true, true, window: true);
        Feed(detector, clock, 20, true, true, window: true);

        var ended = Feed(detector, clock, 8, false, false).Single();

        // 3 ringing + 3 answering + 20 talking, minus the ringing before the call started.
        Assert.InRange(ended.Duration.TotalSeconds, 20, 26);
    }

    /// <summary>
    /// The single most common way this goes wrong. WebRTC stops transmitting during silence and
    /// the mute button closes the stream outright, so a healthy call flickers Inactive
    /// repeatedly. Without hysteresis one conversation becomes a dozen fragments.
    /// </summary>
    [Fact]
    public void BriefDropoutsDuringACallDoNotEndIt()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true);
        Feed(detector, clock, 3, true, true, window: true);

        for (var i = 0; i < 10; i++)
        {
            Feed(detector, clock, 5, true, true, window: true);
            Feed(detector, clock, 2, false, false, window: true); // dropout, shorter than the timeout
            Assert.Equal(CallState.InCall, detector.State);
        }
    }

    /// <summary>
    /// A window disappearing must NOT end a call, and this is a deliberate reversal.
    ///
    /// It used to, on the reasoning that the call UI going away is conclusive. The flag never
    /// meant that: it was set by any visible window the messenger had, so minimising Telegram to
    /// the tray during a conversation — an entirely ordinary thing to do — ended the recording on
    /// a single sample. The remainder was then filed as a separate call, and if the first fragment
    /// came in under five seconds it was deleted outright without a word.
    ///
    /// Audio decides when a call is over. Windows only say who it was with.
    /// </summary>
    [Fact]
    public void MinimisingTheMessengerDoesNotEndTheCall()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true);
        Feed(detector, clock, 3, true, true, window: true);

        var events = Feed(detector, clock, 10, render: true, capture: true, window: false);

        Assert.DoesNotContain(events, e => e.Kind == CallEventKind.Ended);
        Assert.Equal(CallState.InCall, detector.State);
    }

    /// <summary>
    /// Silence on both streams is what ends a call, with or without a window.
    /// </summary>
    [Fact]
    public void SustainedSilenceEndsTheCall()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true);
        Feed(detector, clock, 3, true, true, window: true);

        var events = Feed(detector, clock, 10, render: false, capture: false, window: true);

        Assert.Equal(CallEventKind.Ended, Assert.Single(events).Kind);
        Assert.Equal(CallState.Idle, detector.State);
    }

    /// <summary>
    /// An open chat window is not a ringing telephone.
    ///
    /// This is the fault that quietly destroyed hand-started recordings. The window flag alone
    /// could declare Ringing, and it was set by any window the messenger had — so leaving Telegram
    /// open held the detector in Ringing indefinitely, and three minutes later the unanswered-ring
    /// timeout fired Abandoned, which deletes the recording in progress. Every three minutes,
    /// silently, for a call that never existed.
    /// </summary>
    [Fact]
    public void AnOpenChatWindowAloneIsNotARing()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        var events = Feed(detector, clock, 30, render: false, capture: false, window: true);

        Assert.Empty(events);
        Assert.Equal(CallState.Idle, detector.State);
    }

    /// <summary>
    /// The call panel closing ends the call, even while audio is still flowing.
    ///
    /// Reported from real use on Telegram: the call window closed and the recorder carried on,
    /// because the client kept its audio session open afterwards. Waiting for silence therefore
    /// left the recording running past the end of the conversation.
    ///
    /// This is deliberately not the rule that used to exist and was removed. That one watched a
    /// flag meaning "the application has any window", so minimising the messenger to the tray cut
    /// a live recording in half. This watches the specific window identified as the call panel,
    /// which only became distinguishable once a newly appeared window could be told apart from the
    /// one showing whichever conversation happened to be open.
    /// </summary>
    [Fact]
    public void TheCallPanelClosingEndsTheCallEvenWhileAudioContinues()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        // Ring, answer, and see the call panel.
        WithCallWindow(detector, clock, 2, render: true, capture: false);
        WithCallWindow(detector, clock, 3, render: true, capture: true);

        Assert.Equal(CallState.InCall, detector.State);

        // The panel closes. Both streams are still active — this is the case audio alone misses.
        var events = new List<CallEvent>();
        for (var i = 0; i < 3; i++)
        {
            var e = detector.Observe(new DetectionSample(
                clock.Next(), true, true, AppWindowPresent: true,
                WindowTitle: null, App: CallApp.Telegram,
                TitleTrust: TitleTrust.None, CallWindowPresent: false));

            if (e is not null) events.Add(e);
        }

        Assert.Equal(CallEventKind.Ended, Assert.Single(events).Kind);
        Assert.Equal(CallState.Idle, detector.State);
    }

    /// <summary>
    /// One missing sample is not a closed window.
    ///
    /// Qt and Chromium both recreate top-level windows during a call — going full screen, a layout
    /// change — and reacting to a single absent sample would split one conversation into two
    /// recordings, with the join in the middle of a sentence.
    /// </summary>
    [Fact]
    public void AWindowThatBlinksDoesNotEndTheCall()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        WithCallWindow(detector, clock, 2, render: true, capture: false);
        WithCallWindow(detector, clock, 3, render: true, capture: true);

        // Gone for exactly one sample, then back.
        var blink = detector.Observe(new DetectionSample(
            clock.Next(), true, true, AppWindowPresent: true, WindowTitle: null,
            App: CallApp.Telegram, TitleTrust: TitleTrust.None, CallWindowPresent: false));

        Assert.Null(blink);

        WithCallWindow(detector, clock, 5, render: true, capture: true);

        Assert.Equal(CallState.InCall, detector.State);
    }

    /// <summary>Feeds samples in which the identified call panel is open.</summary>
    private static void WithCallWindow(
        CallDetector detector, Clock clock, int count, bool render, bool capture)
    {
        for (var i = 0; i < count; i++)
        {
            detector.Observe(new DetectionSample(
                clock.Next(), render, capture, AppWindowPresent: true,
                WindowTitle: "Serdal", App: CallApp.Telegram,
                TitleTrust: TitleTrust.Likely, CallWindowPresent: true));
        }
    }

    /// <summary>
    /// A call that never falls silent still has to end.
    ///
    /// Silence was the only remaining way out of InCall, and a client or driver that leaves its
    /// session nominally active defeats it — so the recorder ran until the application was closed:
    /// an ever-growing file, a microphone left open, and no finished call ever offered to the user.
    /// The ceiling produces an ordinary Ended, so the recording is kept rather than lost.
    /// </summary>
    [Fact]
    public void ACallThatNeverGoesQuietIsClosedOffAtTheCeiling()
    {
        var detector = new CallDetector();
        var clock = new Clock();
        var options = new CallDetectorOptions();

        Feed(detector, clock, 2, true, false, window: true);
        Feed(detector, clock, 3, true, true, window: true);

        // One sample per minute, past the ceiling, never quiet.
        var events = new List<CallEvent>();
        for (var i = 0; i < (int)options.MaxCallDuration.TotalMinutes + 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            if (detector.Observe(new DetectionSample(clock.Now, true, true, true)) is { } e) events.Add(e);
        }

        var ended = Assert.Single(events, e => e.Kind == CallEventKind.Ended);
        Assert.True(ended.Duration >= options.MaxCallDuration);

        // And then it starts again, which is correct rather than a leak.
        //
        // The audio really is still flowing, and refusing to re-enter would mean the next genuine
        // call went unrecorded because a stuck session had poisoned the state. So a session that
        // never goes quiet produces a series of bounded recordings rather than one that grows
        // without limit — each of which is finished, written to its row, and offered for labelling.
        Assert.Contains(events, e => e.Kind == CallEventKind.Started && e.At > ended.At);
    }

    // ---- false positives ----------------------------------------------------

    /// <summary>Recording a voice note opens the microphone but there is no call window.</summary>
    [Fact]
    public void RecordingAVoiceNoteIsNotACall()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        var events = Feed(detector, clock, 15, render: false, capture: true, window: false);

        Assert.DoesNotContain(events, e => e.Kind == CallEventKind.Started);
        Assert.Equal(CallState.Idle, detector.State);
    }

    /// <summary>Playing a voice note renders audio but never opens the microphone.</summary>
    [Fact]
    public void PlayingAVoiceNoteIsNotACall()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        var events = Feed(detector, clock, 15, render: true, capture: false, window: false);

        Assert.DoesNotContain(events, e => e.Kind == CallEventKind.Started);
        Assert.NotEqual(CallState.InCall, detector.State);
    }

    [Fact]
    public void ANotificationChimeIsNotACall()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 1, render: true, capture: false);
        var events = Feed(detector, clock, 10, render: false, capture: false);

        Assert.DoesNotContain(events, e => e.Kind == CallEventKind.Started);
        Assert.Equal(CallState.Idle, detector.State);
    }

    [Fact]
    public void AnUnansweredCallIsAbandonedRatherThanRecorded()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 8, render: true, capture: false, window: true);
        Assert.Equal(CallState.Ringing, detector.State);

        var events = Feed(detector, clock, 8, render: false, capture: false, window: false);

        Assert.Equal(CallEventKind.Abandoned, Assert.Single(events).Kind);
        Assert.Equal(CallState.Idle, detector.State);
    }

    [Fact]
    public void AStuckRingingSessionEventuallyGivesUp()
    {
        var detector = new CallDetector(new CallDetectorOptions { MaxRingingDuration = TimeSpan.FromSeconds(30) });
        var clock = new Clock();

        var events = Feed(detector, clock, 60, render: true, capture: false, window: true);

        Assert.Contains(events, e => e.Kind == CallEventKind.Abandoned);
    }

    // ---- contact naming -----------------------------------------------------

    /// <summary>
    /// The title has to be captured while the call is up: by the time it ends the window is
    /// gone, and reading it then returns nothing.
    /// </summary>
    [Fact]
    public void TheContactNameIsCapturedDuringTheCallAndSurvivesToTheEndEvent()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true, title: "Ahmet Yılmaz");
        Feed(detector, clock, 3, true, true, window: true, title: "Ahmet Yılmaz");

        Assert.Equal("Ahmet Yılmaz", detector.ObservedTitle);

        var ended = Feed(detector, clock, 8, false, false).Single();

        Assert.Equal("Ahmet Yılmaz", ended.WindowTitle);
    }

    /// <summary>Telegram renames its call window as participants join; the first name is the one.</summary>
    [Fact]
    public void TheFirstTitleSeenWins()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true, title: "Ahmet Yılmaz");
        Feed(detector, clock, 3, true, true, window: true, title: "Grup Görüşmesi");

        Assert.Equal("Ahmet Yılmaz", detector.ObservedTitle);
    }

    [Fact]
    public void StateIsFullyResetBetweenCalls()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true, title: "Birinci Kişi");
        Feed(detector, clock, 3, true, true, window: true, title: "Birinci Kişi");
        Feed(detector, clock, 8, false, false);

        Assert.Null(detector.ObservedTitle);
        Assert.Equal(CallApp.Unknown, detector.App);

        Feed(detector, clock, 2, true, false, window: true, title: "İkinci Kişi");
        Feed(detector, clock, 3, true, true, window: true, title: "İkinci Kişi");

        Assert.Equal("İkinci Kişi", detector.ObservedTitle);
    }

    /// <summary>
    /// The application can start while a call is already in progress — after a restart, or when
    /// the app was only just added to the watch list. That must be picked up, not ignored.
    /// </summary>
    [Fact]
    public void ACallAlreadyInProgressAtStartupIsPickedUp()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        var events = Feed(detector, clock, 4, render: true, capture: true, window: true);

        Assert.Contains(events, e => e.Kind == CallEventKind.Started);
        Assert.Equal(CallState.InCall, detector.State);
    }

    [Fact]
    public void OutgoingCallIsRecordedFromDialling()
    {
        // On an outgoing call both streams go active before the callee answers, so ringback is
        // captured too. That is harmless, and trying to separate it would be guesswork.
        var detector = new CallDetector();
        var clock = new Clock();

        var events = Feed(detector, clock, 5, render: true, capture: true, window: true);

        Assert.Contains(events, e => e.Kind == CallEventKind.Started);
    }
}
