using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoiceTranscript.Core.Detection;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Capture;

/// <summary>
/// Samples WASAPI audio sessions to work out whether a watched application is on a call.
///
/// Audio sessions are the signal of choice because they are language-independent. The user runs
/// Windows in Turkish, so any heuristic reading window or button text is a localisation trap;
/// whether a process holds an active capture session is not. It also needs no elevation and
/// keeps working while the app is minimised to the tray.
///
/// Three details make the difference between this working and quietly seeing nothing:
///
///   Both directions are enumerated. Rendering alone is a ringtone or a played voice note.
///   Only capture together with rendering means a conversation.
///
///   Every active endpoint is enumerated, not just the default one. VoIP applications open
///   their streams on the communications endpoint, which is frequently a different device from
///   the multimedia default — a headset, typically. Watching only the default is how this ends
///   up reporting silence through an entire call.
///
///   The enumerator is rebuilt on every poll. Microsoft documents that a session enumerator does
///   not learn about sessions created after it was obtained, so a cached one goes stale exactly
///   when a call starts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioSessionWatcher : IDisposable
{
    private readonly TargetProcesses _targets;
    private bool _disposed;

    /// <summary>
    /// What the previous poll saw, so a call window can be recognised by having just appeared.
    ///
    /// The one piece of state this class keeps, and it earns its place: appearance is the only
    /// signal that separates a call panel from a main window displaying an open conversation, and
    /// appearance is invisible without a previous frame to compare against.
    /// </summary>
    private IReadOnlyList<WindowSighting>? _previousWindows;

    /// <summary>
    /// The title of the window identified as the call panel, while it is open.
    ///
    /// Held here rather than in the detector because identifying it needs the previous poll, which
    /// is state this class already keeps. The detector is given the answer — "the call window is
    /// still there" — and stays free of Win32.
    /// </summary>
    private string? _callWindowTitle;

    public AudioSessionWatcher(TargetProcesses? targets = null) => _targets = targets ?? new TargetProcesses();

    /// <summary>
    /// The watched applications' processes, shared rather than re-enumerated.
    ///
    /// Exposed so the recorder can ask which process to follow for per-application capture. A
    /// second TargetProcesses would walk every process tree on its own five-second cadence for
    /// an answer this one already has.
    /// </summary>
    public TargetProcesses Targets => _targets;

    /// <summary>
    /// Takes one observation of every watched application.
    ///
    /// The window arguments are left null in production so the windows are looked at here. They
    /// exist so tests can drive the detector with a fabricated observation on a machine with no
    /// windows and no audio hardware, which is the only way any of this is testable at all.
    /// </summary>
    public DetectionSample Sample(DateTimeOffset now, bool? appWindowPresent = null, string? windowTitle = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var targetPids = _targets.Resolve(now);
        if (targetPids.Count == 0)
            return new DetectionSample(now, false, false, appWindowPresent ?? false, windowTitle);

        // The sessions are read first so the windows can be read on their behalf.
        //
        // These two answers used to be produced independently and never compared: the sessions
        // could say Telegram while the title came off whichever WhatsApp window EnumWindows
        // happened to reach first. The labelling dialog then announced the wrong application,
        // offered the name of a conversation open in the other one, and — because a learned title
        // binding is keyed on (title, app) — stored a pairing that could never match again.
        var rendering = ActiveApp(DataFlow.Render, targetPids);
        var capturing = ActiveApp(DataFlow.Capture, targetPids);

        var app = rendering != CallApp.Unknown ? rendering : capturing;

        // Read in the same poll as the sessions, because the two have to agree about the same
        // instant: a title read a second later can belong to a different call.
        //
        // This is where automatic naming comes from. Nothing was filling it in before, so
        // ObservedTitle was null for every call ever recorded and the labelling dialog had
        // nothing to pre-fill — which made every conversation, on both applications, an
        // "İsimsiz" row waiting to be named by hand.
        WindowObservation windows;

        if (appWindowPresent is null)
        {
            // The previous poll is carried across so that a window which has just appeared can be
            // recognised as new. That is what identifies the call: both clients open a fresh
            // window for one and put the other person's name in its title, and nothing about a
            // single snapshot distinguishes that window from a main window showing whichever
            // conversation happens to be on screen.
            windows = CallWindows.Look(targetPids, app, _previousWindows, out var seen);
            _previousWindows = seen;

            // Lock on to the call panel the first time it is confidently identified, then watch
            // for it closing.
            //
            // This is what tells the recorder a call is over. Audio alone is not enough: a client
            // can keep its session open after the conversation ends — seen on Telegram, where the
            // panel closes and sound carries on — and waiting for silence then leaves the recorder
            // running well past the end, or not stopping at all.
            if (windows.Confidence == TitleConfidence.Likely && windows.Title is not null)
                _callWindowTitle = windows.Title;

            var callWindowPresent = CallWindows.IsStillOpen(seen, _callWindowTitle);

            // Released once it is gone, so the next call can lock on to its own panel. The detector
            // remembers that it had seen one, which is what makes the disappearance meaningful.
            if (_callWindowTitle is not null && !callWindowPresent) _callWindowTitle = null;

            windows = windows with { CallWindowPresent = callWindowPresent };
        }
        else
        {
            windows = new WindowObservation(
                appWindowPresent.Value,
                windowTitle,
                CallApp.Unknown,
                windowTitle is null ? TitleConfidence.None : TitleConfidence.Possible);
        }

        return new DetectionSample(
            now,
            Rendering: rendering != CallApp.Unknown,
            Capturing: capturing != CallApp.Unknown,
            AppWindowPresent: windows.AppWindowPresent,
            WindowTitle: windows.Title,

            // The audio session is the stronger signal for which application is on a call; the
            // window only fills in when no session has been attributed yet.
            App: app != CallApp.Unknown ? app : windows.App,
            TitleTrust: (TitleTrust)windows.Confidence,
            CallWindowPresent: windows.CallWindowPresent);
    }

    /// <summary>
    /// Every window the watched applications currently have, described in full.
    ///
    /// For the diagnostic screen only. Which window a call actually puts a name in cannot be
    /// established on a machine with no audio hardware and no signed-in messenger, so the way to
    /// settle it is to look on the machine where calls happen — see <see cref="CallWindows.Describe"/>.
    /// </summary>
    public string DescribeWindows(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var targetPids = _targets.Resolve(now);

        return targetPids.Count == 0
            ? "WhatsApp veya Telegram çalışmıyor."
            : CallWindows.Describe(CallWindows.Enumerate(targetPids));
    }

    /// <summary>Which watched application, if any, has an active session in this direction.</summary>
    private static CallApp ActiveApp(DataFlow flow, IReadOnlyDictionary<int, CallApp> targetPids)
    {
        // Rebuilt every call: a cached enumerator never sees sessions created after it.
        using var enumerator = new MMDeviceEnumerator();

        MMDeviceCollection endpoints;
        try
        {
            endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        }
        catch (Exception)
        {
            // No audio hardware at all. True on the development machine, and not fatal.
            return CallApp.Unknown;
        }

        foreach (var device in endpoints)
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                if (sessions is null) continue;

                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];

                    // Expired sessions linger in the enumerator after an app closes a stream, so
                    // only Active counts as evidence of anything.
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                    var pid = (int)session.GetProcessID;
                    if (targetPids.TryGetValue(pid, out var app)) return app;
                }
            }
            catch (Exception)
            {
                // A device can be removed between enumeration and query. Skip it.
            }
            finally
            {
                device.Dispose();
            }
        }

        return CallApp.Unknown;
    }

    /// <summary>
    /// The endpoint a call is actually using.
    ///
    /// Preferring the communications role matters: capturing the multimedia default while the
    /// call runs on a headset records sixty minutes of digital silence, with no error anywhere.
    /// </summary>
    public static MMDevice? DefaultEndpoint(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (enumerator.TryGetDefaultAudioEndpoint(flow, Role.Communications, out var communications))
            return communications;

        return enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var multimedia) ? multimedia : null;
    }

    public void Dispose() => _disposed = true;
}
