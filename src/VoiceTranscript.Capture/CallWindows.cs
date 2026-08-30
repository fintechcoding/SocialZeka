using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Capture;

/// <summary>
/// Reads the contact's name off a call window, where the application puts it there.
///
/// This is the difference between an archive that files itself and one that asks a question after
/// every conversation.
///
///   <b>Telegram</b> opens the call as its own top-level window and sets the title to the
///   counterpart's name — <c>window()->setTitle(_user->name())</c> in <c>calls_panel.cpp</c>. The
///   name is simply there for the asking, at no cost and with no guessing.
///
///   <b>WhatsApp</b> titles its main window "WhatsApp" and nothing else. Whether the *call*
///   window does the same is not something this project has been able to verify — checking it
///   means opening a real call on the machine WhatsApp is signed in on, and the development
///   machine is deliberately kept out of that. So the rule is written by shape rather than by
///   assumption: any top-level window of a watched application whose title is not that
///   application's own name is treated as carrying the contact's name. If WhatsApp turns out to
///   title its call window with the person, this picks it up with no change; if it does not,
///   nothing is lost and the labelling dialog asks once and remembers.
///
/// Titles are normalised before use. Telegram writes names with bidirectional control marks
/// around them, and a name carrying an invisible U+200E does not match the same name typed by
/// hand — which would quietly create a second contact for the same person and split their
/// history in two.
///
/// No window is ever activated, closed, moved or sent anything. This reads titles from windows
/// that already exist and does nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CallWindows
{
    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint handle, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    /// <summary>GW_OWNER — a window owned by another is a dialog, not a call panel.</summary>
    private const uint GwOwner = 4;

    /// <summary>
    /// Titles Telegram's main window uses, which are never a contact.
    ///
    /// The unread count is appended in brackets, so the comparison is on the leading text.
    /// </summary>
    private static readonly string[] TelegramShellTitles = ["Telegram", "Telegram Desktop"];

    /// <summary>Titles WhatsApp's own windows use, which are never a contact.</summary>
    private static readonly string[] WhatsAppShellTitles = ["WhatsApp", "WhatsApp Desktop"];

    /// <summary>What was found on one poll.</summary>
    public sealed record Observation(bool CallWindowPresent, string? Title, CallApp App);

    /// <summary>
    /// Looks for a call window belonging to any watched process.
    ///
    /// Deliberately cheap: <c>EnumWindows</c> over the top-level windows of one desktop is a few
    /// hundred handles and runs once a second beside the audio-session poll that was going to
    /// enumerate endpoints anyway.
    /// </summary>
    public static Observation Look(IReadOnlyDictionary<int, CallApp> targetPids)
    {
        if (targetPids.Count == 0) return new Observation(false, null, CallApp.Unknown);

        var found = new Observation(false, null, CallApp.Unknown);

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;

            // An owned window is a dialog or a tooltip hanging off the main window. The call
            // panel is a top-level window in its own right.
            if (GetWindow(handle, GwOwner) != nint.Zero) return true;

            GetWindowThreadProcessId(handle, out var pid);
            if (!targetPids.TryGetValue((int)pid, out var app)) return true;

            var title = TitleOf(handle);
            if (title is null) return true;

            // A window of a watched application whose title is not that application's own name
            // is carrying something else, and on a call that something else is the person. Held
            // to one rule for both applications rather than special-cased per app: Telegram is
            // known to do this, WhatsApp has not been checked, and coding the assumption in
            // would guarantee we never find out.
            if (!IsShellTitle(app, title))
            {
                found = new Observation(true, title, app);
                return false;
            }

            // The application's own window, which says a call may be up but not with whom.
            if (!found.CallWindowPresent) found = new Observation(true, null, app);

            return true;
        }, nint.Zero);

        return found;
    }

    /// <summary>
    /// Whether a window title is the application naming itself rather than a person.
    ///
    /// Public so it can be tested. Enumerating real windows cannot be — there is no Telegram call
    /// running on a build machine — but this is where the decision that matters is made, and
    /// getting it wrong files every call under a contact named after the application.
    /// </summary>
    public static bool IsShellTitle(CallApp app, string title)
    {
        var shells = app switch
        {
            CallApp.Telegram => TelegramShellTitles,
            CallApp.WhatsApp => WhatsAppShellTitles,

            // An application we do not know the shell titles of. Treating its titles as names
            // would file calls under whatever the window happens to say.
            _ => [],
        };

        foreach (var shell in shells)
        {
            if (title.Equals(shell, StringComparison.Ordinal)) return true;

            // "Telegram (3)" — the unread count. Matched on the shape rather than the digits.
            if (title.StartsWith(shell + " (", StringComparison.Ordinal) &&
                title.EndsWith(')')) return true;
        }

        return false;
    }

    private static string? TitleOf(nint handle)
    {
        var length = GetWindowTextLengthW(handle);
        if (length <= 0) return null;

        var buffer = new StringBuilder(length + 1);
        if (GetWindowTextW(handle, buffer, buffer.Capacity) == 0) return null;

        return Clean(buffer.ToString());
    }

    /// <summary>
    /// Strips what is invisible and normalises what is not.
    ///
    /// Telegram wraps names in bidirectional control marks so that Arabic and Hebrew names lay
    /// out correctly beside Latin text. Those characters are invisible and they are part of the
    /// string: a name carrying one does not compare equal to the same name typed by hand, so the
    /// archive would end up with two contacts for one person and neither would hold the full
    /// history. Compatibility normalisation does the rest — Telegram allows decorative Unicode in
    /// display names, and "𝐀𝐡𝐦𝐞𝐭" and "Ahmet" have to be one person.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var builder = new StringBuilder(raw.Length);

        foreach (var c in raw)
        {
            // Bidi controls and the zero-width joiners, plus the byte-order mark that some
            // applications prepend.
            if (c is '‎' or '‏' or '​' or '‌' or '‍' or '﻿') continue;
            if (c is >= '‪' and <= '‮') continue;
            if (c is >= '⁦' and <= '⁩') continue;

            builder.Append(c);
        }

        var cleaned = builder.ToString().Normalize(NormalizationForm.FormKC).Trim();

        return cleaned.Length == 0 ? null : cleaned;
    }
}
