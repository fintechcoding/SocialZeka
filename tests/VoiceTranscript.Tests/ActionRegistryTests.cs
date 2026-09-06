using System.Windows.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.Tests;

/// <summary>
/// One list for the rail, the keys, the palette and the Ctrl+? sheet.
///
/// The three used to be three hand-written copies, and the sheet said "Ctrl+1…8" while the
/// markup bound nine keys in another order. Now a page without an action here has no key and no
/// palette entry — which is what these tests refuse.
/// </summary>
public sealed class ActionRegistryTests
{
    /// <summary>Goes red when a page is added to the shell without a way to reach it from the keyboard.</summary>
    [Fact]
    public void EveryPageHasExactlyOneActionWithAKey()
    {
        foreach (var page in Enum.GetValues<ShellPage>())
        {
            var actions = ActionRegistry.All.Where(a => a.Page == page).ToList();

            Assert.True(actions.Count == 1, $"{page}: {actions.Count} eylem");
            Assert.NotNull(actions[0].Key);
        }
    }

    /// <summary>Goes red when two actions claim the same key — the second silently loses.</summary>
    [Fact]
    public void KeysAreUnique()
    {
        var keys = ActionRegistry.All
            .Where(a => a.Key is not null)
            .Select(a => (a.Key, a.Modifiers))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    /// <summary>The sheet prints digits as digits, and the one without a modifier without "Ctrl+".</summary>
    [Fact]
    public void TheShortcutTextReadsTheWayTheSheetShowsIt()
    {
        Assert.Equal("Ctrl+5", ActionRegistry.ForPage(ShellPage.Promises)!.ShortcutText);
        Assert.Equal("Ctrl+F", ActionRegistry.ForPage(ShellPage.Search)!.ShortcutText);
        Assert.Equal("F5", ActionRegistry.All.Single(a => a.Key == Key.F5).ShortcutText);
        Assert.Equal("Ctrl+?", ActionRegistry.All.Single(a => a.Key == Key.OemQuestion).ShortcutText);
    }

    /// <summary>
    /// The digits follow the rail: what the calls are, what they left behind, the coaching band,
    /// then the lookups. Goes red when a page is inserted into the rail without the digits
    /// moving with it — which is the state the whole registry exists to prevent.
    /// </summary>
    [Fact]
    public void TheDigitsFollowTheRailOrder()
    {
        var expected = new[]
        {
            (ShellPage.Overview, Key.D1), (ShellPage.Calls, Key.D2), (ShellPage.Contacts, Key.D3),
            (ShellPage.Ledger, Key.D4), (ShellPage.Promises, Key.D5), (ShellPage.Calendar, Key.D6),
            (ShellPage.Todo, Key.D7), (ShellPage.Mirror, Key.D8),
            (ShellPage.Ask, Key.D9), (ShellPage.Health, Key.D0),
        };

        foreach (var (page, key) in expected)
            Assert.Equal(key, ActionRegistry.ForPage(page)!.Key);
    }
}
