using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Detection;

public enum CallState
{
    Idle,

    /// <summary>The app is rendering audio but not capturing: a ringtone or ringback.</summary>
    Ringing,

    /// <summary>Both directions are live. This is when recording runs.</summary>
    InCall,
}

/// <summary>One observation of the world, taken roughly once a second.</summary>
public readonly record struct DetectionSample(
    DateTimeOffset At,

    /// <summary>A target process has an active render session — audio is going to the speakers.</summary>
    bool Rendering,

    /// <summary>A target process has an active capture session — the microphone is open.</summary>
    bool Capturing,

    /// <summary>A window matching the app's call-window signature exists.</summary>
    bool CallWindowPresent,

    /// <summary>Title of that window, when there is one. Telegram puts the contact's name here.</summary>
    string? WindowTitle = null,

    CallApp App = CallApp.Unknown);

public enum CallEventKind
{
    Started,
    Ended,
    Abandoned,
}

public sealed record CallEvent(
    CallEventKind Kind,
    DateTimeOffset At,
    CallApp App,
    string? WindowTitle,
    TimeSpan Duration);

/// <summary>
/// Turns a stream of audio-session observations into call start and end events.
///
/// Why audio sessions rather than window titles: the user runs Windows in Turkish, and every
/// heuristic based on button or window text is a localisation trap waiting to fire. Whether a
/// process holds an active capture session is language-independent, needs no elevation, and
/// works while the app sits in the tray.
///
/// Why hysteresis is not optional: AudioSessionStateActive means "at least one stream is
/// running", and WebRTC discontinuous transmission plus the app's own mute button drop that to
/// Inactive in the middle of a perfectly healthy call. Reacting to a single sample would chop
/// one conversation into a dozen fragments. Entering a state takes several consecutive
/// confirmations, and leaving one takes several seconds of sustained silence.
///
/// This class is pure: it holds no timers and touches nothing outside itself, so the whole
/// state machine is testable by feeding it samples.
/// </summary>
public sealed class CallDetector(CallDetectorOptions? options = null)
{
    private readonly CallDetectorOptions _options = options ?? new CallDetectorOptions();

    private int _renderStreak;
    private int _captureStreak;
    private DateTimeOffset? _quietSince;
    private DateTimeOffset? _callStartedAt;
    private DateTimeOffset? _ringingSince;
    private string? _observedTitle;
    private CallApp _app;

    public CallState State { get; private set; } = CallState.Idle;

    /// <summary>
    /// Best title seen while the call was up.
    ///
    /// Captured during the call on purpose: by the time it ends the window is already gone, so
    /// reading it afterwards returns nothing.
    /// </summary>
    public string? ObservedTitle => _observedTitle;

    public CallApp App => _app;

    public TimeSpan CurrentDuration(DateTimeOffset now) =>
        _callStartedAt is { } started ? now - started : TimeSpan.Zero;

    /// <summary>Feeds one observation. Returns an event when the state changed meaningfully.</summary>
    public CallEvent? Observe(DetectionSample sample)
    {
        _renderStreak = sample.Rendering ? _renderStreak + 1 : 0;
        _captureStreak = sample.Capturing ? _captureStreak + 1 : 0;

        // Only attribute the app when there is actually a signal. An idle sample carries no
        // information about which app we are watching, and letting it write here would leave
        // stale attribution behind after a call ends.
        if (sample.App != CallApp.Unknown && (sample.Rendering || sample.Capturing || sample.CallWindowPresent))
            _app = sample.App;

        // Keep the first non-empty title: Telegram renames its window as participants change,
        // and the earliest value is the one that names the person who was called.
        if (string.IsNullOrEmpty(_observedTitle) && !string.IsNullOrWhiteSpace(sample.WindowTitle))
            _observedTitle = sample.WindowTitle;

        var quiet = !sample.Rendering && !sample.Capturing;
        if (quiet) _quietSince ??= sample.At;
        else _quietSince = null;

        return State switch
        {
            CallState.Idle => FromIdle(sample),
            CallState.Ringing => FromRinging(sample),
            CallState.InCall => FromInCall(sample),
            _ => null,
        };
    }

    private CallEvent? FromIdle(DetectionSample sample)
    {
        // A call window appearing is instant and unambiguous, so it does not wait for a streak.
        var ringing = sample.CallWindowPresent || _renderStreak >= _options.SamplesToRing;

        if (!ringing) return null;

        State = CallState.Ringing;
        _ringingSince = sample.At;

        // An answered call can be observed for the first time already in progress — after a
        // restart, or if the app was only just added to the watch list.
        return _captureStreak >= _options.SamplesToAnswer ? EnterCall(sample) : null;
    }

    private CallEvent? FromRinging(DetectionSample sample)
    {
        if (_renderStreak >= 1 && _captureStreak >= _options.SamplesToAnswer)
            return EnterCall(sample);

        // Declined, missed, or cancelled: it never became a conversation.
        var windowGone = !sample.CallWindowPresent;
        var silentLongEnough = _quietSince is { } since && sample.At - since >= _options.RingingTimeout;

        if (windowGone && silentLongEnough)
        {
            var at = sample.At;
            Reset();
            return new CallEvent(CallEventKind.Abandoned, at, _app, null, TimeSpan.Zero);
        }

        // A ringing phone nobody answers must not ring forever in our state.
        if (_ringingSince is { } start && sample.At - start >= _options.MaxRingingDuration)
        {
            var at = sample.At;
            Reset();
            return new CallEvent(CallEventKind.Abandoned, at, _app, null, TimeSpan.Zero);
        }

        return null;
    }

    private CallEvent? FromInCall(DetectionSample sample)
    {
        // The window vanishing is conclusive: the call UI is gone, so the call is over. No need
        // to wait out the audio hysteresis on top of it.
        if (_options.TrustWindowDisappearance && !sample.CallWindowPresent && _sawCallWindow)
            return EndCall(sample);

        if (_quietSince is { } since && sample.At - since >= _options.SilenceBeforeEnd)
            return EndCall(sample);

        return null;
    }

    private bool _sawCallWindow;

    private CallEvent EnterCall(DetectionSample sample)
    {
        State = CallState.InCall;
        _callStartedAt = sample.At;
        _sawCallWindow = sample.CallWindowPresent;

        return new CallEvent(CallEventKind.Started, sample.At, _app, _observedTitle, TimeSpan.Zero);
    }

    private CallEvent EndCall(DetectionSample sample)
    {
        // The duration ends where the audio stopped, not where the timeout expired, so the
        // trailing silence used to confirm the end is not counted as conversation.
        var endedAt = _quietSince ?? sample.At;
        var duration = _callStartedAt is { } started ? endedAt - started : TimeSpan.Zero;
        var title = _observedTitle;
        var app = _app;

        Reset();
        return new CallEvent(CallEventKind.Ended, endedAt, app, title, duration);
    }

    private void Reset()
    {
        State = CallState.Idle;
        _renderStreak = 0;
        _captureStreak = 0;
        _quietSince = null;
        _callStartedAt = null;
        _ringingSince = null;
        _observedTitle = null;
        _sawCallWindow = false;
        _app = CallApp.Unknown;
    }
}

public sealed record CallDetectorOptions
{
    /// <summary>Consecutive rendering samples before treating it as a ring.</summary>
    public int SamplesToRing { get; init; } = 2;

    /// <summary>
    /// Consecutive capturing samples before treating the call as answered. Three at one hertz
    /// is enough to ride out the flapping that mute and discontinuous transmission cause, and
    /// short enough that almost nothing of the conversation is missed.
    /// </summary>
    public int SamplesToAnswer { get; init; } = 3;

    /// <summary>Sustained silence before an in-progress call is declared over.</summary>
    public TimeSpan SilenceBeforeEnd { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Silence before an unanswered ring is written off.</summary>
    public TimeSpan RingingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Nobody rings for this long. Guards against a stuck render session.</summary>
    public TimeSpan MaxRingingDuration { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Whether the call window disappearing ends the call immediately. True for apps whose call
    /// window is reliably detected; false falls back to the audio timeout alone.
    /// </summary>
    public bool TrustWindowDisappearance { get; init; } = true;
}
