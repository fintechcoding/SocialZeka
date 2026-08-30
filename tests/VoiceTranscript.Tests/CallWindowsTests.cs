using VoiceTranscript.Capture;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Reading a contact's name off a call window.
///
/// Enumerating real windows cannot be tested here — there is no Telegram call running on a build
/// machine — but the two decisions that actually determine whether a name is right are pure
/// string work, and both have a failure mode that is silent and permanent:
///
///   Mistaking the application's own window for a person files every call under a contact called
///   "Telegram", and once that contact exists the mistake compounds with every call after it.
///
///   Failing to strip an invisible character means the same person arrives under two names that
///   look identical on screen. Their history splits in half and the ledger stops noticing that a
///   price changed between the two halves — while both halves look complete.
/// </summary>
public class CallWindowsTests
{
    [Theory]
    [InlineData("Telegram")]
    [InlineData("Telegram Desktop")]
    [InlineData("Telegram (3)")]
    [InlineData("Telegram (12)")]
    public void TheApplicationsOwnWindowIsNotAPerson(string title)
    {
        Assert.True(CallWindows.IsShellTitle(CallApp.Telegram, title));
    }

    [Theory]
    [InlineData("Ahmet Yılmaz")]
    [InlineData("Işıl")]
    [InlineData("Telegramcı Mehmet")]
    public void ANameIsNotMistakenForTheApplication(string title)
    {
        Assert.False(CallWindows.IsShellTitle(CallApp.Telegram, title));
    }

    [Fact]
    public void WhatsAppIsHeldToTheSameRuleRatherThanAssumedNameless()
    {
        // WhatsApp's main window is titled "WhatsApp". Whether its *call* window is has never
        // been verified here, so the rule is written by shape: anything that is not the
        // application naming itself is treated as a name. Coding in the assumption that WhatsApp
        // never gives one would guarantee we never found out that it does.
        Assert.True(CallWindows.IsShellTitle(CallApp.WhatsApp, "WhatsApp"));
        Assert.False(CallWindows.IsShellTitle(CallApp.WhatsApp, "Uliana"));
    }

    [Fact]
    public void AnUnknownApplicationsTitlesAreNeverTreatedAsNames()
    {
        // Without a known shell title there is nothing to rule out, so every window of that
        // process would be filed as a person.
        Assert.False(CallWindows.IsShellTitle(CallApp.Unknown, "anything"));
    }

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
