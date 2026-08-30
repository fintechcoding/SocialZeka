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

    public AudioSessionWatcher(TargetProcesses? targets = null) => _targets = targets ?? new TargetProcesses();

    /// <summary>
    /// Takes one observation of every watched application.
    ///
    /// The window arguments are left null in production so the windows are looked at here. They
    /// exist so tests can drive the detector with a fabricated observation on a machine with no
    /// windows and no audio hardware, which is the only way any of this is testable at all.
    /// </summary>
    public DetectionSample Sample(DateTimeOffset now, bool? callWindowPresent = null, string? windowTitle = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var targetPids = _targets.Resolve(now);
        if (targetPids.Count == 0)
            return new DetectionSample(now, false, false, callWindowPresent ?? false, windowTitle);

        // Read alongside the sessions rather than separately, because the two have to agree
        // about the same instant: a title read a second later can belong to a different call.
        //
        // This is where automatic naming comes from. Nothing was filling it in before, so
        // ObservedTitle was null for every call ever recorded and the labelling dialog had
        // nothing to pre-fill — which made every conversation, on both applications, an
        // "İsimsiz" row waiting to be named by hand.
        var windows = callWindowPresent is null
            ? CallWindows.Look(targetPids)
            : new CallWindows.Observation(callWindowPresent.Value, windowTitle, CallApp.Unknown);

        var rendering = ActiveApp(DataFlow.Render, targetPids);
        var capturing = ActiveApp(DataFlow.Capture, targetPids);

        var app = rendering != CallApp.Unknown ? rendering : capturing;

        return new DetectionSample(
            now,
            Rendering: rendering != CallApp.Unknown,
            Capturing: capturing != CallApp.Unknown,
            CallWindowPresent: windows.CallWindowPresent,
            WindowTitle: windows.Title,

            // The audio session is the stronger signal for which application is on a call; the
            // window only fills in when no session has been attributed yet.
            App: app != CallApp.Unknown ? app : windows.App);
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
