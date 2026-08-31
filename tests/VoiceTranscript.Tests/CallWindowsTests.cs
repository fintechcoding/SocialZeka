using VoiceTranscript.Capture;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Reading a contact's name off a call window.
///
/// Enumerating real windows cannot be tested here — the development machine is a virtual one with
/// no audio hardware and no signed-in messenger, so there is no Telegram call to observe. That is
/// exactly why the decision was pulled out into <see cref="CallWindows.Choose"/>, which is pure:
/// everything touching Win32 does nothing but gather facts, and every judgement that can file a
/// conversation under the wrong person is made here, where it can be pinned down.
///
/// Two failure modes drive these tests and both are silent and permanent:
///
///   Mistaking something that is not a person for one files every call under a contact named after
///   it — and because the labelling dialog offers to remember the pairing, one wrong guess becomes
///   a stored binding that keeps doing it, without ever asking again.
///
///   Failing to strip an invisible character means the same person arrives under two names that
///   look identical on screen. Their history splits in half and the ledger stops noticing that a
///   price changed between the two halves — while both halves look complete.
/// </summary>
public class CallWindowsTests
{
    private static WindowSighting Window(
        string? title, CallApp app = CallApp.Telegram, bool foreground = false)
        => new(title, app, "Qt5152QWindowIcon", 900, 600, foreground);

    // ---- telling the application apart from a person ------------------------

    [Theory]
    [InlineData("Telegram")]
    [InlineData("Telegram Desktop")]
    [InlineData("Telegram (3)")]
    [InlineData("Telegram (12)")]
    public void TheApplicationsOwnWindowIsNotAPerson(string title)
    {
        Assert.True(CallWindows.IsShellTitle(CallApp.Telegram, title));
    }

    /// <summary>
    /// The unread badge comes in two shapes and only the trailing one was recognised.
    ///
    /// The leading form is the one both clients commonly use, and a title this code did not
    /// recognise was treated as a person's name. So the archive grew a contact called
    /// "(3) WhatsApp" — and a separate one for every different unread count, each holding a slice
    /// of one person's history, none of them comparable with the others.
    /// </summary>
    [Theory]
    [InlineData(CallApp.Telegram, "(3) Telegram")]
    [InlineData(CallApp.Telegram, "(12) Telegram Desktop")]
    [InlineData(CallApp.WhatsApp, "(1) WhatsApp")]
    [InlineData(CallApp.WhatsApp, "(99) WhatsApp")]
    [InlineData(CallApp.Signal, "(4) Signal")]
    public void TheUnreadBadgeIsNotPartOfAName(CallApp app, string title)
    {
        Assert.True(CallWindows.IsShellTitle(app, title));
    }

    [Theory]
    [InlineData("Ahmet Yılmaz")]
    [InlineData("Işıl")]
    [InlineData("Telegramcı Mehmet")]
    [InlineData("(3) Ahmet")]
    public void ANameIsNotMistakenForTheApplication(string title)
    {
        Assert.False(CallWindows.IsShellTitle(CallApp.Telegram, title));
    }

    /// <summary>Only a parenthesised run of digits is a badge. People have brackets in their names.</summary>
    [Theory]
    [InlineData("Ahmet (iş)")]
    [InlineData("(abi) Mehmet")]
    [InlineData("Ayşe (2. hat)")]
    public void BracketsThatAreNotACounterAreLeftAlone(string title)
    {
        Assert.Equal(title, CallWindows.StripUnreadBadge(title));
        Assert.False(CallWindows.IsShellTitle(CallApp.Telegram, title));
    }

    [Fact]
    public void EachApplicationKnowsItsOwnNames()
    {
        Assert.True(CallWindows.IsShellTitle(CallApp.WhatsApp, "WhatsApp"));
        Assert.True(CallWindows.IsShellTitle(CallApp.WhatsApp, "WhatsApp Business"));
        Assert.True(CallWindows.IsShellTitle(CallApp.Signal, "Signal"));
        Assert.True(CallWindows.IsShellTitle(CallApp.Signal, "Signal Beta"));

        // A name is a name whichever application it arrives from.
        Assert.False(CallWindows.IsShellTitle(CallApp.WhatsApp, "Uliana"));
        Assert.False(CallWindows.IsShellTitle(CallApp.Signal, "Serdal"));
    }

    [Fact]
    public void AnUnknownApplicationsTitlesAreNeverTreatedAsNames()
    {
        // Without a known shell title there is nothing to rule out, so every window of that
        // process would be filed as a person.
        Assert.False(CallWindows.IsShellTitle(CallApp.Unknown, "anything"));
    }

    // ---- picking the call window out of the ones that are open --------------

    /// <summary>
    /// The rule the whole feature rests on: a call opens a NEW window titled with the person.
    ///
    /// Both WhatsApp and Telegram do this. Nothing about a single frozen snapshot separates that
    /// window from a main window showing whichever conversation is on screen — both are "a window
    /// whose title is not the application's own name". Appearance is the difference, and it is
    /// only visible by comparing consecutive polls.
    /// </summary>
    [Fact]
    public void AWindowThatJustAppearedIsTheCall()
    {
        WindowSighting[] before = [Window("Telegram"), Window("Uliana")];
        WindowSighting[] now = [Window("Telegram"), Window("Uliana"), Window("Serdal", foreground: true)];

        var observation = CallWindows.Choose(now, CallApp.Telegram, before);

        Assert.Equal("Serdal", observation.Title);
        Assert.Equal(TitleConfidence.Likely, observation.Confidence);
    }

    /// <summary>
    /// This is the Uliana-and-Serdal fault, as a test.
    ///
    /// Telegram's main window title is the conversation currently open. Calling Serdal while
    /// Uliana's chat is on screen used to file the call under Uliana — and with the "remember this
    /// title" box ticked by default, every later call showing that title went to Uliana too, with
    /// the labelling dialog no longer appearing because the contact now looked known.
    /// </summary>
    [Fact]
    public void TheOpenChatDoesNotWinOverTheCallWindow()
    {
        WindowSighting[] before = [Window("Uliana")];
        WindowSighting[] now = [Window("Uliana"), Window("Serdal", foreground: true)];

        Assert.Equal("Serdal", CallWindows.Choose(now, CallApp.Telegram, before).Title);
    }

    /// <summary>
    /// Nothing new appeared, and two windows both carry names. There is no way to tell which is
    /// the call, so the one the user is looking at is offered — for confirmation, not as fact.
    /// </summary>
    [Fact]
    public void WithNothingNewTheForegroundWindowIsOfferedTentatively()
    {
        WindowSighting[] windows = [Window("Uliana"), Window("Serdal", foreground: true)];

        var observation = CallWindows.Choose(windows, CallApp.Telegram, windows);

        Assert.Equal("Serdal", observation.Title);
        Assert.Equal(TitleConfidence.Possible, observation.Confidence);
    }

    /// <summary>
    /// When it genuinely cannot be told, say nothing.
    ///
    /// A wrong name is worse than no name and not by a little: the labelling dialog offers to
    /// remember it, so one wrong guess becomes a permanent misfiling that stops asking. No name
    /// means one question, once.
    /// </summary>
    [Fact]
    public void AmbiguityProducesNoNameRatherThanAGuess()
    {
        WindowSighting[] windows = [Window("Uliana"), Window("Serdal")];

        var observation = CallWindows.Choose(windows, CallApp.Telegram, windows);

        Assert.Null(observation.Title);
        Assert.Equal(TitleConfidence.None, observation.Confidence);
        Assert.True(observation.AppWindowPresent);
    }

    [Fact]
    public void OnlyTheApplicationsOwnWindowsMeansNoName()
    {
        WindowSighting[] windows = [Window("Telegram"), Window("(3) Telegram")];

        var observation = CallWindows.Choose(windows, CallApp.Telegram);

        Assert.Null(observation.Title);
        Assert.True(observation.AppWindowPresent);
    }

    /// <summary>
    /// The title has to come from the application the audio says is on the call.
    ///
    /// These two answers used to be produced independently and never compared, so the sessions
    /// could say Telegram while the title came off whichever WhatsApp window was enumerated first.
    /// The dialog then announced the wrong application, offered a name from the other one, and
    /// stored a binding keyed on (title, app) that could never match again.
    /// </summary>
    [Fact]
    public void TheTitleComesFromTheApplicationTheAudioBlames()
    {
        WindowSighting[] before = [Window("WhatsApp", CallApp.WhatsApp), Window("Telegram")];
        WindowSighting[] now =
        [
            Window("Ayşe", CallApp.WhatsApp, foreground: true),
            Window("Telegram"),
            Window("Serdal"),
        ];

        Assert.Equal("Serdal", CallWindows.Choose(now, CallApp.Telegram, before).Title);
        Assert.Equal("Ayşe", CallWindows.Choose(now, CallApp.WhatsApp, before).Title);
    }

    [Fact]
    public void NoWindowsAtAllIsReportedAsSuch()
    {
        var observation = CallWindows.Choose([], CallApp.Telegram);

        Assert.False(observation.AppWindowPresent);
        Assert.Null(observation.Title);
    }

    /// <summary>
    /// Seen for the first time with a call already under way — after a restart, or when the
    /// application was only just added to the watch list. One name, offered for confirmation.
    /// </summary>
    [Fact]
    public void ACallAlreadyInProgressStillYieldsAName()
    {
        WindowSighting[] windows = [Window("Telegram"), Window("Serdal")];

        var observation = CallWindows.Choose(windows, CallApp.Telegram);

        Assert.Equal("Serdal", observation.Title);
        Assert.Equal(TitleConfidence.Possible, observation.Confidence);
    }

    // ---- normalising what is read -------------------------------------------

    [Fact]
    public void InvisibleDirectionMarksAreStripped()
    {
        // Telegram wraps names in bidi controls so mixed-script names lay out correctly. The
        // characters are invisible and they are part of the string.
        Assert.Equal("Ahmet", CallWindows.Clean("‎Ahmet‎"));
        Assert.Equal("Ahmet", CallWindows.Clean("‫Ahmet‬"));
        Assert.Equal("Ahmet", CallWindows.Clean("﻿Ahmet"));
    }

    [Fact]
    public void DecorativeUnicodeResolvesToTheSamePerson()
    {
        // Display names may use mathematical alphanumerics. "Ahmet" typed by hand has to reach
        // the same contact.
        Assert.Equal("Ahmet", CallWindows.Clean("\U0001D400\U0001D421\U0001D426\U0001D41E\U0001D42D"));
    }

    [Fact]
    public void TurkishLettersSurviveNormalisation()
    {
        // Compatibility normalisation must not decompose these into a base letter plus a mark;
        // an "Işıl" that comes back as "Isil" is a different person.
        Assert.Equal("Işıl Çağla Öğüt", CallWindows.Clean("  Işıl Çağla Öğüt  "));
    }

    [Fact]
    public void ATitleThatIsOnlyInvisibleCharactersIsNoTitleAtAll()
    {
        // Otherwise a contact gets created with an empty name that no search will ever match.
        Assert.Null(CallWindows.Clean("‎‎"));
        Assert.Null(CallWindows.Clean("   "));
        Assert.Null(CallWindows.Clean(null));
    }
}
