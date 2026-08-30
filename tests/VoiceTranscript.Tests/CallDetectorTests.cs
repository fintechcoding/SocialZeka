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
        private int _seconds;
        public DateTimeOffset Next() => T0.AddSeconds(_seconds++);
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

    [Fact]
    public void CallWindowClosingEndsTheCallImmediately()
    {
        var detector = new CallDetector();
        var clock = new Clock();

        Feed(detector, clock, 2, true, false, window: true);
        Feed(detector, clock, 3, true, true, window: true);

        var events = Feed(detector, clock, 1, render: true, capture: true, window: false);

        Assert.Equal(CallEventKind.Ended, Assert.Single(events).Kind);
        Assert.Equal(CallState.Idle, detector.State);
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
