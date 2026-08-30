using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Highlighting the match, and narrowing by when.
///
/// Both are small, and both are the kind of thing that quietly stops working: a highlight that
/// lands on the wrong characters looks like a rendering glitch rather than a bug, and a date
/// filter off by a boundary just returns slightly fewer results than it should.
/// </summary>
public class SearchPresentationTests
{
    private static SearchResult Result(string text, string query) => new(
        new SearchHit(
            CallId: 1,
            SegmentId: 1,
            ContactId: 1,
            ContactName: "Ahmet",
            CallStartedAt: DateTimeOffset.Now,
            IsMe: false,
            StartMs: 0,
            Text: text),
        query);

    [Fact]
    public void TheMatchIsSplitOutOfTheLine()
    {
        var (before, match, after) = Result("cuma günü evrakları yollarım", "evrak").Split();

        Assert.Equal("cuma günü ", before);
        Assert.Equal("evrak", match);
        Assert.Equal("ları yollarım", after);
    }

    [Fact]
    public void TurkishCasingIsHonouredInTheHighlight()
    {
        // The whole reason the index is built over a folded shadow column: the default Unicode
        // rules get İ and ı wrong, so a search for "ışık" would find the row and then highlight
        // nothing, which reads as a broken feature rather than a casing rule.
        var (before, match, after) = Result("Dışarıda IŞIK yoktu", "ışık").Split();

        Assert.Equal("Dışarıda ", before);
        Assert.Equal("IŞIK", match);
        Assert.Equal(" yoktu", after);
    }

    [Fact]
    public void ALineWithNoVisibleMatchIsShownWholeRatherThanBlank()
    {
        // The index matches on word prefixes, so a row can come back without the exact term
        // appearing in it. Showing nothing would lose the result entirely.
        var result = Result("ödemeyi cumaya yetiştiririm", "ödeme");
        var (before, match, after) = result.Split();

        // "ödeme" is a prefix of "ödemeyi", so it does highlight here.
        Assert.Equal("ödeme", match);

        var unmatched = Result("başka bir cümle", "bulunmayan");
        Assert.Equal("başka bir cümle", unmatched.Before);
        Assert.Equal("", unmatched.Match);
        Assert.False(unmatched.HasMatch);
    }

    [Fact]
    public void AnEmptyQueryLeavesTheLineAlone()
    {
        var result = Result("herhangi bir metin", "");

        Assert.Equal("herhangi bir metin", result.Before);
        Assert.False(result.HasMatch);
    }

    [Fact]
    public void AQueryOfOnlyPunctuationDoesNotThrow()
    {
        // Normalisation can reduce a query to nothing. Slicing by a zero-length needle would
        // otherwise highlight an empty span at position zero on every row.
        var result = Result("bir cümle", "...");

        Assert.Equal("bir cümle", result.Before);
        Assert.False(result.HasMatch);
    }

    [Theory]
    [InlineData(SearchPeriod.Anytime, false)]
    [InlineData(SearchPeriod.LastWeek, true)]
    [InlineData(SearchPeriod.LastMonth, true)]
    [InlineData(SearchPeriod.LastYear, true)]
    public void EveryPeriodExceptAnytimeHasACutOff(SearchPeriod period, bool bounded)
    {
        Assert.Equal(bounded, period.Since() is not null);
    }

    [Fact]
    public void ThePeriodsWidenInOrder()
    {
        var week = SearchPeriod.LastWeek.Since()!.Value;
        var month = SearchPeriod.LastMonth.Since()!.Value;
        var year = SearchPeriod.LastYear.Since()!.Value;

        Assert.True(year < month, "yıl aydan geniş olmalı");
        Assert.True(month < week, "ay haftadan geniş olmalı");
        Assert.True(week < DateTimeOffset.Now, "hafta geçmişe bakmalı");
    }

    [Fact]
    public void EveryPeriodIsNamedInTurkish()
    {
        foreach (var period in Enum.GetValues<SearchPeriod>())
        {
            var label = period.Label();

            Assert.False(string.IsNullOrWhiteSpace(label), period.ToString());
            Assert.NotEqual(period.ToString(), label);
        }
    }
}
