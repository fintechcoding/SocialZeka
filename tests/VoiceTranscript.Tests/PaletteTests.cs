using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The palette's matching, without the window. What matters is that a Turkish keyboard and a
/// mangled-ASCII keyboard both find the same action — "isaret" must reach "İşaretler", or the
/// palette teaches people it cannot be trusted with their own language.
/// </summary>
public sealed class PaletteTests
{
    private static int Score(string query, string title) => ActionRegistry.Score(
        TurkishText.NormalizeForSearch(query), TurkishText.NormalizeForSearch(title));

    [Fact]
    public void PrefixBeatsSubstringBeatsSubsequenceAndAMissBeatsNothing()
    {
        Assert.Equal(100, Score("def", "Defter"));
        Assert.Equal(50, Score("bakış", "Genel bakış"));
        Assert.True(Score("gnl", "Genel") is 10);
        Assert.Equal(0, Score("xyz", "Defter"));
    }

    [Fact]
    public void TurkishFoldingMakesTheDottedAndDotlessKeyboardsEqual()
    {
        // ASCII typing finds the Turkish title, and Turkish typing finds it too.
        Assert.Equal(100, Score("isaret", "İşaretler"));
        Assert.Equal(100, Score("İŞARET", "isaretler"));
        Assert.Equal(100, Score("kisiler", "Kişiler"));
    }

    [Fact]
    public void AnEmptyQueryShowsEverythingInsteadOfNothing()
    {
        foreach (var action in ActionRegistry.All)
            Assert.Equal(1, ActionRegistry.Score("", action.Folded));
    }

    [Fact]
    public void EveryRegisteredActionSurvivesItsOwnTitleAsAQuery()
    {
        // The registry's own titles are the palette's most likely queries — each must be a
        // perfect prefix match of itself after folding.
        foreach (var action in ActionRegistry.All)
            Assert.Equal(100, ActionRegistry.Score(action.Folded, action.Folded));
    }
}
