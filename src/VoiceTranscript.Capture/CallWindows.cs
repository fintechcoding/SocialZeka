using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Capture;

/// <summary>How much a title can be trusted to be the counterpart's name.</summary>
public enum TitleConfidence
{
    /// <summary>Nothing usable was seen.</summary>
    None = 0,

    /// <summary>
    /// A single window carried something that is not the application's own name.
    ///
    /// It is probably the person, but it may equally be whichever chat happened to be open —
    /// Telegram puts the active conversation in its main window title whether a call is happening
    /// or not. Good enough to pre-fill a box the user confirms; not good enough to file a call
    /// under without asking.
    /// </summary>
    Possible = 1,

    /// <summary>
    /// The application had its own named window open <i>and</i> a separate one carrying something
    /// else, and that separate one was in the foreground.
    ///
    /// That shape is what a call panel looks like: the messenger stays where it was and a new
    /// window comes to the front. Still a heuristic, but a materially better one.
    /// </summary>
    Likely = 2,
}

/// <summary>One top-level window seen during a single poll.</summary>
/// <param name="Title">Cleaned title, or null when the window had none.</param>
/// <param name="App">Which watched application owns it.</param>
/// <param name="ClassName">Win32 window class. Recorded for the diagnostic dump only.</param>
/// <param name="Width">Client width in pixels. Diagnostic only.</param>
/// <param name="Height">Client height in pixels. Diagnostic only.</param>
/// <param name="IsForeground">Whether this was the foreground window at the moment of the poll.</param>
public sealed record WindowSighting(
    string? Title,
    CallApp App,
    string ClassName,
    int Width,
    int Height,
    bool IsForeground,

    /// <summary>
    /// The window's own identity.
    ///
    /// Carried because a title on its own cannot tell a new window from a renamed one, and the
    /// difference decides whether a name is trusted enough to file a call under. Telegram retitles
    /// its main window every time the user clicks a different chat, which by title alone is
    /// indistinguishable from a call panel opening — so an idle click on a conversation was being
    /// read as "a call started with this person".
    ///
    /// A diagnostic value only: it is never displayed and never stored.
    /// </summary>
    nint Handle = 0);

/// <summary>
/// What one poll of the desktop found.
/// </summary>
/// <param name="AppWindowPresent">
/// A watched application has at least one visible top-level window.
///
/// This means "the messenger is open", <b>not</b> "a call is happening". The distinction is the
/// whole point of this type: the previous version of this code collapsed the two, and the flag it
/// produced was then used to decide when a call started and when it ended. Closing the chat window
/// to the tray therefore looked exactly like hanging up.
/// </param>
/// <param name="Title">The counterpart's name, when one could be identified. Null otherwise.</param>
/// <param name="App">Which application the title belongs to.</param>
/// <param name="Confidence">How far <paramref name="Title"/> can be trusted.</param>
/// <param name="CallWindowPresent">
/// The window identified as the call panel is still open.
///
/// Distinct from <paramref name="AppWindowPresent"/>, and the distinction is the point. The
/// previous version of this code had one flag meaning "the application has a window", and used its
/// disappearance to end calls — so minimising the messenger to the tray cut a recording in half.
/// This one refers to a specific window: the one that appeared when the call started, carrying the
/// other person's name. When <i>that</i> closes, the call really is over.
///
/// It is worth having because audio alone is not enough. A client can hold its audio session open
/// for a while after the call ends, and waiting for the streams to fall silent then leaves the
/// recorder running past the conversation — observed on Telegram, where the panel closes and sound
/// keeps flowing.
/// </param>
public sealed record WindowObservation(
    bool AppWindowPresent,
    string? Title,
    CallApp App,
    TitleConfidence Confidence,
    bool CallWindowPresent = false);

/// <summary>
/// Reads the contact's name off a call window, where the application puts it there.
///
/// This is the difference between an archive that files itself and one that asks a question after
/// every conversation. It is also, historically, where this project filed conversations under the
/// wrong person — so the rules below are deliberately cautious, and the reasoning is written down.
///
/// <b>What this is not used for any more.</b> Window presence used to decide when a call started
/// and when it ended. It no longer does, and that is a correction rather than a simplification.
/// <see cref="Core.Detection.CallDetector"/> says in its own summary that heuristics reading
/// window text are a localisation trap and that audio sessions are the language-independent
/// signal — and then a window flag crept in and drove ringing and hang-up anyway. Because that
/// flag really meant "the messenger has a window open", three separate faults followed from it:
/// a chat window sitting open kept the detector permanently in <c>Ringing</c>, which discarded
/// hand-started recordings roughly every three minutes; closing the messenger to the tray cut a
/// recording in half mid-conversation; and a title read minutes before the call was attributed
/// to it. Windows now answer one question only: <i>who is this call with.</i>
///
/// <b>Telegram</b> opens the call as its own top-level window and sets the title to the
/// counterpart's name — <c>window()-&gt;setTitle(_user-&gt;name())</c> in <c>calls_panel.cpp</c>.
/// But its <i>main</i> window title is the conversation currently on screen, which is not
/// necessarily the person calling, and both windows fail a "is this the application's own name"
/// test identically.
///
/// <b>WhatsApp</b> titles its main window "WhatsApp". Whether the call window carries the person
/// has not been verifiable here — checking it means a real call on a machine WhatsApp is signed
/// in on, and the development machine is a virtual one with no audio hardware at all. So the
/// decision is written by shape, kept pure, and tested exhaustively; and <see cref="Describe"/>
/// exists so the real answer can be collected from the machine where calls actually happen
/// instead of guessed at here.
///
/// <b>The rule when it is ambiguous is to say nothing.</b> A wrong name is worse than no name,
/// and not by a little: the labelling dialog offers to remember the pairing, so one wrong guess
/// becomes a stored binding that silently files every later call with that title under the wrong
/// person — and because the contact then looks known, the dialog stops appearing and the user is
/// never given the chance to notice.
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint handle, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint handle, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>GW_OWNER — a window owned by another is a dialog, not a call panel.</summary>
    private const uint GwOwner = 4;

    /// <summary>
    /// Titles Telegram's own windows use, which are never a contact.
    ///
    /// Includes the third-party forks, because somebody running one of those is still running
    /// Telegram and would otherwise acquire a contact named after their client.
    /// </summary>
    private static readonly string[] TelegramShellTitles =
        ["Telegram", "Telegram Desktop", "Telegram Web", "AyuGram", "64Gram", "Kotatogram"];

    /// <summary>Titles WhatsApp's own windows use, which are never a contact.</summary>
    private static readonly string[] WhatsAppShellTitles =
        ["WhatsApp", "WhatsApp Desktop", "WhatsApp Web", "WhatsApp Business"];

    /// <summary>Titles Signal's own windows use, which are never a contact.</summary>
    private static readonly string[] SignalShellTitles =
        ["Signal", "Signal Desktop", "Signal Beta"];

    /// <summary>
    /// Looks at every top-level window belonging to a watched process.
    /// </summary>
    /// <param name="targetPids">Process ids of watched applications, mapped to which one.</param>
    /// <param name="attributedApp">
    /// The application the audio sessions say is on a call, or <see cref="CallApp.Unknown"/> when
    /// nothing has been attributed yet.
    ///
    /// Passed in rather than inferred, because the two answers used to be produced independently
    /// and never compared: the sessions could say Telegram while the title came off whichever
    /// WhatsApp window happened to be enumerated first. The label dialog then announced the wrong
    /// application, offered the wrong name, and stored the binding under a key that would never
    /// match again.
    /// </param>
    /// <param name="previous">What the previous poll saw, so a newly opened call window is visible as new.</param>
    /// <param name="current">Receives what this poll saw, to be passed back as <paramref name="previous"/> next time.</param>
    public static WindowObservation Look(
        IReadOnlyDictionary<int, CallApp> targetPids,
        CallApp attributedApp,
        IReadOnlyList<WindowSighting>? previous,
        out IReadOnlyList<WindowSighting> current)
    {
        current = targetPids.Count == 0 ? [] : Enumerate(targetPids);

        return current.Count == 0
            ? new WindowObservation(false, null, CallApp.Unknown, TitleConfidence.None)
            : Choose(current, attributedApp, previous);
    }

    /// <summary>
    /// Every visible, unowned top-level window of a watched application.
    ///
    /// Deliberately cheap: <c>EnumWindows</c> over one desktop is a few hundred handles and runs
    /// once a second beside an audio-session poll that was going to enumerate endpoints anyway.
    ///
    /// Unlike the previous version this does not stop at the first match. Stopping made the answer
    /// depend on z-order, which is to say on which window the user last clicked — so the name a
    /// conversation was filed under could change depending on what was in front at the time.
    /// </summary>
    public static IReadOnlyList<WindowSighting> Enumerate(IReadOnlyDictionary<int, CallApp> targetPids)
    {
        var sightings = new List<WindowSighting>();
        if (targetPids.Count == 0) return sightings;

        var foreground = GetForegroundWindow();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;

            // An owned window is a dialog or a tooltip hanging off the main window. A call panel
            // is a top-level window in its own right.
            if (GetWindow(handle, GwOwner) != nint.Zero) return true;

            GetWindowThreadProcessId(handle, out var pid);
            if (!targetPids.TryGetValue((int)pid, out var app)) return true;

            var width = 0;
            var height = 0;
            if (GetClientRect(handle, out var rect))
            {
                width = rect.Right - rect.Left;
                height = rect.Bottom - rect.Top;
            }

            // Zero-sized windows are message-only helpers that Qt and Chromium both create. They
            // are invisible to the user and carry nothing worth reading.
            if (width == 0 && height == 0) return true;

            sightings.Add(new WindowSighting(
                TitleOf(handle),
                app,
                ClassOf(handle),
                width,
                height,
                handle == foreground,
                handle));

            return true;
        }, nint.Zero);

        return sightings;
    }

    /// <summary>
    /// Decides which of the observed windows, if any, is naming the person on the call.
    ///
    /// Pure, and that is not incidental. A real Telegram or WhatsApp call cannot be staged on the
    /// machine this is developed on — it is a virtual machine with no audio hardware — so this
    /// function is the only part of the naming logic that can be tested at all. Everything that
    /// touches Win32 is above it and does nothing but gather facts.
    /// </summary>
    /// <param name="sightings">What <see cref="Enumerate"/> saw.</param>
    /// <param name="attributedApp">
    /// The application the audio sessions blame, or <see cref="CallApp.Unknown"/> to consider all.
    /// </param>
    /// <param name="previous">
    /// What the previous poll saw, or null on the first one.
    ///
    /// This is the signal that actually identifies the call panel, and it comes from the one place
    /// it could: watching it happen. Both WhatsApp and Telegram open a <i>new</i> window when a
    /// call starts and put the other person's name in its title. A title that was not there a
    /// second ago and is not the application naming itself is therefore the call — which is a far
    /// better rule than anything that can be inferred from a single frozen snapshot, where the
    /// call panel and a main window showing the currently open conversation look identical.
    /// </param>
    public static WindowObservation Choose(
        IReadOnlyList<WindowSighting> sightings,
        CallApp attributedApp = CallApp.Unknown,
        IReadOnlyList<WindowSighting>? previous = null)
    {
        // When the sessions have named an application, only its windows may name the person.
        var relevant = attributedApp == CallApp.Unknown
            ? sightings
            : [.. sightings.Where(s => s.App == attributedApp)];

        if (relevant.Count == 0) return new WindowObservation(false, null, CallApp.Unknown, TitleConfidence.None);

        var app = attributedApp != CallApp.Unknown ? attributedApp : relevant[0].App;

        // A candidate is a window carrying something that is not the application naming itself.
        var candidates = relevant
            .Where(s => s.Title is not null && !IsShellTitle(s.App, s.Title))
            .ToList();

        if (candidates.Count == 0) return new WindowObservation(true, null, app, TitleConfidence.None);

        // A window that was not there a second ago is the call panel.
        //
        // Both clients open a new window for a call and title it with the other person. Nothing
        // else about a single snapshot separates that window from a main window displaying
        // whichever conversation is on screen — they are both "a window whose title is not the
        // application's own name". Appearance is the difference, and it is only visible by
        // comparing consecutive polls.
        if (previous is not null)
        {
            // Compared by window, not by title.
            //
            // By title alone, a chat switch in Telegram's main window looks exactly like a call
            // panel appearing: one moment the set of titles contains "Ahmet", the next it
            // contains "Berk". That earned Likely — the confidence reserved for a call panel and
            // the only level trusted enough to file a call under — so clicking through
            // conversations while idle produced a stream of confident, wrong names.
            //
            // A window that was not there a second ago is a different claim entirely, and the
            // handle is what distinguishes it.
            var before = previous
                .Where(p => p.Handle != 0)
                .Select(p => p.Handle)
                .ToHashSet();

            var appeared = candidates
                .Where(c => c.Handle != 0 && !before.Contains(c.Handle))
                .ToList();

            // Exactly one new name. This is the case the feature exists for, and it is worth
            // trusting: it is what happens on every ordinary call.
            if (appeared.Count == 1)
                return new WindowObservation(true, appeared[0].Title, appeared[0].App, TitleConfidence.Likely);

            // Two windows appeared at once — a second call, or the messenger being restored from
            // the tray with several windows. The one in front is the one the user is looking at.
            if (appeared.Count > 1 && appeared.FirstOrDefault(c => c.IsForeground) is { } newest)
                return new WindowObservation(true, newest.Title, newest.App, TitleConfidence.Possible);
        }

        if (candidates.Count == 1)
        {
            // One window carries something else, and either this is the first poll or it was
            // already there. It is probably the person — but on Telegram the main window title is
            // the conversation currently open, so it is offered for confirmation rather than
            // trusted outright.
            //
            // This is also the path taken when the application is only noticed once a call is
            // already under way, after a restart.
            var only = candidates[0];

            return new WindowObservation(true, only.Title, only.App, TitleConfidence.Possible);
        }

        // Several windows carry names, none of them new. There is no way to tell which is the
        // call. The one in front is the better guess — somebody on a call is looking at it — and
        // if none is, nothing is claimed at all.
        //
        // Refusing to guess is deliberate. A wrong pick does not stay a single mistake: the
        // labelling dialog offers to remember the pairing, and a remembered wrong pairing files
        // every later call under the wrong person without ever asking again.
        var front = candidates.FirstOrDefault(c => c.IsForeground);

        return front is null
            ? new WindowObservation(true, null, app, TitleConfidence.None)
            : new WindowObservation(true, front.Title, front.App, TitleConfidence.Possible);
    }

    /// <summary>
    /// Whether a particular window is still among the ones open.
    ///
    /// Used to notice that the call panel has closed. Matched on the title because that is the only
    /// stable identity available across polls: a handle would be better, but Chromium and Qt both
    /// recreate top-level windows during a call — on a layout change, on going full screen — and a
    /// recreated handle would read as "the call ended" when nothing happened.
    /// </summary>
    public static bool IsStillOpen(IReadOnlyList<WindowSighting> sightings, string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;

        foreach (var sighting in sightings)
        {
            if (string.Equals(sighting.Title, title, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a window title is the application naming itself rather than a person.
    ///
    /// Public so it can be tested. Enumerating real windows cannot be — there is no Telegram call
    /// running on a build machine — but this is where the decision that matters is made, and
    /// getting it wrong files every call under a contact named after the application.
    ///
    /// <b>The unread badge comes in two shapes and only one was handled.</b> This code knew
    /// "Telegram (3)" and not "(3) Telegram", and the prefix form is the one both clients
    /// commonly use. A title it did not recognise was treated as a person's name, so the archive
    /// grew a contact called "(3) WhatsApp" — and a separate one for every different unread count,
    /// each holding a slice of one person's history.
    /// </summary>
    public static bool IsShellTitle(CallApp app, string title)
    {
        var shells = app switch
        {
            CallApp.Telegram => TelegramShellTitles,
            CallApp.WhatsApp => WhatsAppShellTitles,
            CallApp.Signal => SignalShellTitles,

            // An application we do not know the shell titles of. Treating its titles as names
            // would file calls under whatever the window happens to say.
            _ => [],
        };

        if (shells.Length == 0) return false;

        var bare = StripUnreadBadge(title).Trim();
        if (bare.Length == 0) return true;

        foreach (var shell in shells)
        {
            if (bare.Equals(shell, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Removes an unread counter from either end of a title.
    ///
    /// Both shapes occur — "(3) WhatsApp" and "Telegram (3)" — and which one a build uses is not
    /// something to depend on. Only a parenthesised run of digits is removed, so a person named
    /// "Ahmet (iş)" keeps their name intact.
    /// </summary>
    public static string StripUnreadBadge(string title)
    {
        var text = title.Trim();

        // Leading "(12) "
        if (text.StartsWith('('))
        {
            var close = text.IndexOf(')');
            if (close > 1 && AllDigits(text.AsSpan(1, close - 1)))
                text = text[(close + 1)..].TrimStart();
        }

        // Trailing " (12)"
        if (text.EndsWith(')'))
        {
            var open = text.LastIndexOf('(');
            if (open >= 0 && open < text.Length - 2 && AllDigits(text.AsSpan(open + 1, text.Length - open - 2)))
                text = text[..open].TrimEnd();
        }

        return text;
    }

    private static bool AllDigits(ReadOnlySpan<char> span)
    {
        if (span.Length == 0) return false;

        foreach (var c in span)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }

        return true;
    }

    /// <summary>
    /// A human-readable dump of every window a watched application currently has.
    ///
    /// This exists because the decision above cannot be verified where it is written. Guessing at
    /// what WhatsApp titles its call window has already cost this project a wrong contact for
    /// every unread count; the way out is to look, on the machine where calls happen, rather than
    /// to guess more carefully.
    ///
    /// <b>The output can contain a contact's name</b>, which is exactly why it is worth having and
    /// exactly why it must never be written to the ordinary log — that log is offered to the user
    /// to send to somebody else and is written on the promise that it carries no such thing. This
    /// is produced only when explicitly asked for, and whoever asks is told what it may contain.
    /// </summary>
    public static string Describe(IReadOnlyList<WindowSighting> sightings)
    {
        if (sightings.Count == 0) return "İzlenen uygulamalara ait görünür pencere yok.";

        var report = new StringBuilder();
        report.AppendLine($"{sightings.Count} pencere bulundu:");
        report.AppendLine();

        foreach (var s in sightings)
        {
            var verdict = s.Title is null
                ? "başlıksız"
                : IsShellTitle(s.App, s.Title)
                    ? "uygulamanın kendi adı"
                    : "KİŞİ ADI OLABİLİR";

            report.AppendLine($"  {s.App,-9} {s.Width,5}x{s.Height,-5} {(s.IsForeground ? "ÖN PLAN" : "       ")} "
                              + $"[{s.ClassName}]");
            report.AppendLine($"            başlık: {s.Title ?? "(yok)"}   → {verdict}");
        }

        report.AppendLine();
        report.AppendLine("Karar: " + Summarise(Choose(sightings)));

        return report.ToString();
    }

    private static string Summarise(WindowObservation observation) => observation switch
    {
        { Title: null, AppWindowPresent: false } => "izlenen uygulama açık değil",
        { Title: null } => "isim çıkarılamadı — kullanıcıya sorulacak",
        var o => $"\"{o.Title}\" ({o.App}, güven: {o.Confidence})",
    };

    private static string ClassOf(nint handle)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(handle, buffer, buffer.Capacity) == 0 ? "" : buffer.ToString();
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
