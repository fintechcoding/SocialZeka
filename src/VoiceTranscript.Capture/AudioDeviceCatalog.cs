using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace VoiceTranscript.Capture;

/// <summary>One audio endpoint the user can choose.</summary>
public sealed record AudioDeviceInfo
{
    /// <summary>Stable endpoint identifier. Survives renames and reboots; the name does not.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>True when Windows would pick this for a call.</summary>
    public bool IsCommunicationsDefault { get; init; }

    /// <summary>True when Windows would pick this for music and video.</summary>
    public bool IsMultimediaDefault { get; init; }

    /// <summary>
    /// True when this looks like the hands-free half of a Bluetooth headset.
    ///
    /// A Bluetooth headset presents itself twice. One endpoint pair is hands-free — microphone
    /// and earpiece together, 8 or 16 kHz, and the moment anything opens that microphone the
    /// headset drops out of stereo. The other is stereo output only, at full quality. Which one
    /// a call ends up on is decided by whichever application opened the microphone first, and
    /// choosing the wrong one here is how somebody ends up recording a conversation that sounds
    /// like a telephone from 1985 — or nothing at all.
    /// </summary>
    public bool IsHandsFree { get; init; }

    /// <summary>Mix format the endpoint is currently running at, for the description line.</summary>
    public int SampleRate { get; init; }
    public int Channels { get; init; }

    /// <summary>What to show under the name, so a choice can be made without guessing.</summary>
    public string Description
    {
        get
        {
            var parts = new List<string>();

            if (IsCommunicationsDefault) parts.Add("aramalar için varsayılan");
            else if (IsMultimediaDefault) parts.Add("müzik ve video için varsayılan");

            if (SampleRate > 0)
                parts.Add(Channels == 1 ? $"{SampleRate / 1000} kHz mono" : $"{SampleRate / 1000} kHz");

            if (IsHandsFree) parts.Add("Bluetooth eller serbest — düşük kalite");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Lists the audio endpoints, so the user can say which ones a call actually runs on.
///
/// Automatic selection is right most of the time and wrong in exactly the cases people care
/// about. The one that keeps coming up: AirPods for listening, the laptop microphone for
/// talking. Windows records that as two unrelated defaults, and a recorder that assumes one
/// device does both ends up capturing an hour of silence from the far end — with no error,
/// because a loopback client on an idle endpoint is indistinguishable from a quiet conversation.
///
/// So the choice is offered, defaulted to automatic, and every entry says enough about itself to
/// be chosen correctly: which one Windows uses for calls, what rate it runs at, and whether it is
/// the hands-free half of a Bluetooth headset.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class AudioDeviceCatalog
{
    /// <summary>Active endpoints of one direction, defaults first.</summary>
    public static IReadOnlyList<AudioDeviceInfo> List(bool forCapture)
    {
        var flow = forCapture ? DataFlow.Capture : DataFlow.Render;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            var communications = TryDefault(enumerator, flow, Role.Communications);
            var multimedia = TryDefault(enumerator, flow, Role.Multimedia);

            var devices = new List<AudioDeviceInfo>();

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(Describe(device, communications, multimedia));
                }
            }

            return
            [
                .. devices
                    .OrderByDescending(d => d.IsCommunicationsDefault)
                    .ThenByDescending(d => d.IsMultimediaDefault)
                    .ThenBy(d => d.IsHandsFree)
                    .ThenBy(d => d.Name, StringComparer.CurrentCulture)
            ];
        }
        catch (Exception)
        {
            // A machine with no audio stack at all, or a driver mid-reinstall. An empty list
            // leaves the setting on automatic, which is the same behaviour as before.
            return [];
        }
    }

    /// <summary>Resolves a saved identifier, or null when it is not plugged in any more.</summary>
    public static MMDevice? Find(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        try
        {
            var device = enumerator.GetDevice(deviceId);

            // Present is not the same as usable, and the difference cost conversations.
            //
            // GetDevice throws only for an id Windows has never heard of. A headset that was
            // unplugged this morning, or an endpoint the user disabled, is still returned — with
            // a state of Unplugged or Disabled and no ability to capture anything. The caller
            // read a non-null answer as "the chosen device is there", skipped the fallback it
            // already has, and opened a client on a device that was not connected: no packets,
            // no warning, no recording. Meanwhile the list this was chosen from only ever showed
            // Active endpoints, so the id could stop matching anything selectable at any moment.
            if (device.State == DeviceState.Active) return device;

            device.Dispose();
            return null;
        }
        catch (Exception)
        {
            // An id from a machine this profile was copied from, or a driver mid-reinstall. The
            // caller falls back to the default rather than refusing to record, because a missing
            // headset must not cost a conversation.
            return null;
        }
    }

    private static string? TryDefault(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        if (!enumerator.TryGetDefaultAudioEndpoint(flow, role, out var device)) return null;

        using (device)
        {
            return device.ID;
        }
    }

    private static AudioDeviceInfo Describe(MMDevice device, string? communications, string? multimedia)
    {
        var name = SafeName(device);

        return new AudioDeviceInfo
        {
            Id = device.ID,
            Name = name,
            IsCommunicationsDefault = device.ID == communications,
            IsMultimediaDefault = device.ID == multimedia,
            IsHandsFree = LooksHandsFree(name),
            SampleRate = SafeRate(device),
            Channels = SafeChannels(device),
        };
    }

    /// <summary>
    /// Recognises the hands-free half of a Bluetooth headset by what Windows calls it.
    ///
    /// By its name rather than by a device property, because there is no reliable property: the
    /// distinction lives in the Bluetooth profile, and what surfaces to WASAPI is two ordinary
    /// endpoints whose only difference is the words Windows puts in the name. Every language
    /// Windows ships uses one of these terms for it.
    /// </summary>
    public static bool LooksHandsFree(string name) =>
        name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Hands Free", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Eller Serbest", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Headset", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Kulaklık Seti", StringComparison.OrdinalIgnoreCase);

    private static string SafeName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception)
        {
            return "Bilinmeyen cihaz";
        }
    }

    private static int SafeRate(MMDevice device)
    {
        try
        {
            return device.AudioClient.MixFormat.SampleRate;
        }
        catch (Exception)
        {
            // Some virtual devices refuse format negotiation. Not knowing the rate is fine;
            // failing to list the device is not.
            return 0;
        }
    }

    private static int SafeChannels(MMDevice device)
    {
        try
        {
            return device.AudioClient.MixFormat.Channels;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
