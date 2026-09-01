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

/// <summary>How far a window title can be trusted to name the person on the call.</summary>
/// <remarks>
/// Mirrors <c>VoiceTranscript.Capture.TitleConfidence</c>. Duplicated rather than referenced
/// because <c>Core</c> does not depend on <c>Capture</c> — and must not, since that dependency
/// would drag Win32 into the one layer that can be tested on a machine with no audio hardware.
/// </remarks>
public enum TitleTrust
{
    None = 0,
    Possible = 1,
    Likely = 2,
}

/// <summary>One observation of the world, taken roughly once a second.</summary>
public readonly record struct DetectionSample(
    DateTimeOffset At,

    /// <summary>A target process has an active render session — audio is going to the speakers.</summary>
    bool Rendering,

    /// <summary>A target process has an active capture session — the microphone is open.</summary>
    bool Capturing,

    /// <summary>
    /// A watched application has a visible top-level window.
    ///
    /// This means "the messenger is open", <b>not</b> "a call is happening", and nothing in this
    /// state machine may treat it as the latter. It used to: the flag was called
    /// <c>CallWindowPresent</c>, it was set by any window the application had, and it decided both
    /// when ringing started and when a call ended. A chat window left open therefore held the
    /// detector in <see cref="CallState.Ringing"/> indefinitely — which discarded hand-started
    /// recordings every three minutes when the ring timed out — and closing the messenger to the
    /// tray ended a live call mid-sentence.
    ///
    /// It is kept because it is genuinely useful for one thing: knowing whether reading a title
    /// was even possible. It is not evidence of a call.
    /// </summary>
    bool AppWindowPresent,

    /// <summary>Title of a window that appears to name the counterpart, when one was identified.</summary>
    string? WindowTitle = null,

    CallApp App = CallApp.Unknown,

    /// <summary>How far <see cref="WindowTitle"/> can be trusted.</summary>
    TitleTrust TitleTrust = TitleTrust.None,

    /// <summary>
    /// The window identified as the call panel is still open.
    ///
    /// Not the same as <see cref="AppWindowPresent"/>, and the difference is what makes this
    /// usable. This refers to one specific window — the one that appeared when the call started,
    /// carrying the other person's name — so its closing means the call ended, where the
    /// application merely having no visible window means somebody minimised it.
    /// </summary>
    bool CallWindowPresent = false);

public enum CallEventKind
{
    Started,
    Ended,
    Abandoned,
}

/// <param name="Direction">
/// Who called whom, read off the ring: an incoming call rings the speaker while the microphone
/// is still closed, an outgoing one opens the microphone the moment dialling starts. Unknown
/// when a call is first observed already in progress — a guess would be worse than an honest
/// blank, in a product whose one rule is not asserting what it does not know.
/// </param>
public sealed record CallEvent(
    CallEventKind Kind,
    DateTimeOffset At,
    CallApp App,
    string? WindowTitle,
    TimeSpan Duration,
    CallDirection Direction = CallDirection.Unknown);

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
    private TitleTrust _titleTrust;
    private bool _sawCallWindow;
    private int _callWindowGoneStreak;
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

        // Which application is on the call is decided by audio, never by a window.
        //
        // An open window is not evidence: both messengers can be open at once, so a sample where
        // only that flag was set could re-attribute a live call to whichever application the
        // window enumeration happened to reach. That is not hypothetical — it rewrote the
        // attribution during the silent samples at the end of a call, so the finished call was
        // reported under the wrong application, the labelling dialog announced the wrong one, and
        // the learned title binding was stored under a key that could never match again.
        //
        // An idle sample still carries no information and must not write here either, or stale
        // attribution outlives the call it belonged to.
        if (sample.App != CallApp.Unknown && (sample.Rendering || sample.Capturing))
            _app = sample.App;

        // Keep the best title seen, not merely the first.
        //
        // Keeping the first was wrong in a way that showed up as calls filed under the wrong
        // person. A title is observable before the call connects — the messenger's main window
        // carries whichever conversation is on screen — so the first value seen was often the chat
        // the user happened to have open, and once stored it could never be corrected even when
        // the call panel appeared a second later carrying the actual name. Confidence makes the
        // later, better answer win; equal confidence still keeps the earlier one, because during a
        // call a title change means participants changed, not that the first was wrong.
        if (!string.IsNullOrWhiteSpace(sample.WindowTitle)
            && (string.IsNullOrEmpty(_observedTitle) || sample.TitleTrust > _titleTrust))
        {
            _observedTitle = sample.WindowTitle;
            _titleTrust = sample.TitleTrust;
        }

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
        // Ringing is decided by audio alone.
        //
        // A window used to be able to declare it on its own, on the reasoning that a call window
        // appearing is instant and unambiguous. The flag did not mean that: it meant the
        // application had any window at all. So leaving Telegram open put the detector into
        // Ringing and kept it there, and three minutes later the ring timed out as Abandoned —
        // which deletes the recording in progress. Somebody using hand-started recording lost it
        // roughly every three minutes, silently, to a call that never existed.
        //
        // Audio is the honest signal, and it is the one this class was written around: a process
        // holding a render session is playing something, and after two consecutive samples that
        // something is a ringtone rather than a notification.
        if (_renderStreak < _options.SamplesToRing) return null;

        State = CallState.Ringing;
        _ringingSince = sample.At;

        // The direction is decided here and only here, while the ring is fresh: a speaker
        // ringing over a closed microphone is somebody calling US; a microphone already open
        // while the dial tone plays is us calling THEM.
        _ringDirection = _captureStreak > 0 ? CallDirection.Outgoing : CallDirection.Incoming;

        // An answered call can be observed for the first time already in progress — after a
        // restart, or if the app was only just added to the watch list.
        // Observed mid-call: no ring was seen, so no honest direction exists.
        return _captureStreak >= _options.SamplesToAnswer
            ? EnterCall(sample.At, sample, CallDirection.Unknown)
            : null;
    }

    private CallEvent? FromRinging(DetectionSample sample)
    {
        if (_renderStreak >= 1 && _captureStreak >= _options.SamplesToAnswer)
            return EnterCall(sample.At, sample, _ringDirection);

        // Declined, missed, or cancelled: it never became a conversation.
        //
        // Judged on silence alone now. The window condition that used to sit alongside it could
        // not be satisfied while the messenger was open, so a ring that was never answered stayed
        // Ringing until MaxRingingDuration — three minutes of the recorder believing a call was
        // about to start.
        var silentLongEnough = _quietSince is { } since && sample.At - since >= _options.RingingTimeout;

        if (silentLongEnough)
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
        // The call panel closing ends the call, and it is the better signal of the two.
        //
        // Audio is not enough on its own: a client can hold its session open after the
        // conversation is over — observed on Telegram, where the panel closes and sound keeps
        // flowing — so waiting for silence leaves the recorder running past the end, sometimes
        // indefinitely. The window that was opened for this call closing is unambiguous.
        //
        // This is not the rule that used to be here and was removed. That one watched a flag
        // meaning "the application has any window", so minimising the messenger to the tray cut a
        // recording in half. This watches the specific window identified as the call panel, which
        // only became possible once a newly appearing window could be told apart from the one
        // showing whichever conversation was open.
        //
        // Two consecutive samples, because Qt and Chromium both recreate top-level windows during
        // a call — going full screen, a layout change — and a single missing sample would end a
        // conversation that is still happening.
        if (_sawCallWindow)
        {
            _callWindowGoneStreak = sample.CallWindowPresent ? 0 : _callWindowGoneStreak + 1;

            if (_callWindowGoneStreak >= _options.SamplesBeforeWindowClosesTheCall)
                return EndCall(sample);
        }
        else if (sample.CallWindowPresent)
        {
            // Identified after the call was already under way — the panel takes a moment to appear
            // after the audio starts, and on a restart it may be seen mid-call.
            _sawCallWindow = true;
            _callWindowGoneStreak = 0;
        }

        if (_quietSince is { } since && sample.At - since >= _options.SilenceBeforeEnd)
            return EndCall(sample);

        // Nothing else can end a call, so something has to bound it.
        //
        // Both audio streams can stay nominally active after a call really ends — a client that
        // keeps its session open, a driver that never reports the change — and there was no
        // ceiling at all on this state, only on ringing. The result was a recording that ran until
        // the application was closed: an ever-growing file, a microphone left open, and no
        // finished call to show the user. A ceiling turns that from a silent loss into a long
        // recording that at least exists and can be trimmed.
        if (_callStartedAt is { } started && sample.At - started >= _options.MaxCallDuration)
            return EndCall(sample);

        return null;
    }

    private CallEvent EnterCall(DateTimeOffset at, DetectionSample sample, CallDirection direction)
    {
        State = CallState.InCall;
        _callStartedAt = at;
        _sawCallWindow = sample.CallWindowPresent;
        _callWindowGoneStreak = 0;

        return new CallEvent(CallEventKind.Started, at, _app, _observedTitle, TimeSpan.Zero, direction);
    }

    private CallDirection _ringDirection = CallDirection.Unknown;

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
        _ringDirection = CallDirection.Unknown;
        _observedTitle = null;
        _titleTrust = TitleTrust.None;
        _sawCallWindow = false;
        _callWindowGoneStreak = 0;
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

    /// <summary>
    /// Consecutive samples with the call panel gone before the call is declared over.
    ///
    /// Two, not one. Qt and Chromium both recreate top-level windows during a call — going full
    /// screen, a layout change — and a single missing sample would end a conversation that is
    /// still happening, splitting it into two recordings.
    /// </summary>
    public int SamplesBeforeWindowClosesTheCall { get; init; } = 2;

    /// <summary>Nobody rings for this long. Guards against a stuck render session.</summary>
    public TimeSpan MaxRingingDuration { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The longest a single call is allowed to run before it is closed off regardless.
    ///
    /// A ceiling exists because the only other way out of <see cref="CallState.InCall"/> is
    /// sustained silence on both streams, and a client or driver that leaves a session nominally
    /// active defeats that — leaving a recording running with no way to finish. Four hours is
    /// past any conversation this is built for and well short of filling a disk.
    ///
    /// Reaching it produces an ordinary <see cref="CallEventKind.Ended"/>, so the recording is
    /// kept, written to its row and offered for labelling like any other. Losing it would be the
    /// worse failure by far.
    /// </summary>
    public TimeSpan MaxCallDuration { get; init; } = TimeSpan.FromHours(4);
}
