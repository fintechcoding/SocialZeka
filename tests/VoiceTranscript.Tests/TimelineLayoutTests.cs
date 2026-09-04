using VoiceTranscript.App.Services;
using VoiceTranscript.App.Views;

namespace VoiceTranscript.Tests;

/// <summary>
/// The conversation drawn against the clock.
///
/// The whole point is that speech which happened together is drawn together, so the one thing
/// that must never happen is two lines in the same column sitting on top of each other — that is
/// the failure a list has and a timeline is supposed to fix.
/// </summary>
public class TimelineLayoutTests
{
    private static TimelineLayout.Item At(int startMs, bool mine, double height) =>
        new(startMs, mine, height);

    [Fact]
    public void ALineSitsAtTheMomentItWasSaid()
    {
        var tops = TimelineLayout.Tops([At(0, true, 40), At(10_000, false, 40)], pixelsPerSecond: 20);

        Assert.Equal(0, tops[0]);
        Assert.Equal(200, tops[1]);
    }

    [Fact]
    public void SpeechAtTheSameMomentIsDrawnSideBySide()
    {
        // The reason this view exists: both said something at second five, and neither is pushed
        // below the other.
        var tops = TimelineLayout.Tops([At(5_000, true, 40), At(5_000, false, 40)], pixelsPerSecond: 20);

        Assert.Equal(100, tops[0]);
        Assert.Equal(100, tops[1]);
    }

    [Fact]
    public void ALineTooTallForItsMomentPushesTheNextOneDown()
    {
        // Sixty words take the same two seconds as four, and time alone would overlap them.
        var items = new[] { At(0, true, 300), At(2_000, true, 40) };

        var tops = TimelineLayout.Tops(items, pixelsPerSecond: 20);

        Assert.Equal(0, tops[0]);
        Assert.Equal(300 + TimelineLayout.GapPx, tops[1]);
    }

    [Fact]
    public void ATallLineInOneColumnDoesNotMoveTheOther()
    {
        // Columns are independent: a speaker cannot overlap themselves, and pushing the other
        // person down would be inventing a delay that did not happen.
        var items = new[] { At(0, true, 300), At(2_000, false, 40) };

        var tops = TimelineLayout.Tops(items, pixelsPerSecond: 20);

        Assert.Equal(40, tops[1]);
    }

    [Fact]
    public void NoTwoLinesInAColumnEverOverlap()
    {
        var items = new List<TimelineLayout.Item>();
        for (var i = 0; i < 40; i++) items.Add(At(i * 400, i % 3 == 0, 30 + i % 7 * 20));

        var tops = TimelineLayout.Tops(items, pixelsPerSecond: 25);

        foreach (var mine in new[] { true, false })
        {
            var column = Enumerable.Range(0, items.Count)
                .Where(i => items[i].IsMe == mine)
                .OrderBy(i => tops[i])
                .ToList();

            for (var k = 1; k < column.Count; k++)
            {
                var above = column[k - 1];
                Assert.True(tops[column[k]] >= tops[above] + items[above].Height,
                    $"{column[k]} çakışıyor: {tops[column[k]]} < {tops[above] + items[above].Height}");
            }
        }
    }

    [Fact]
    public void TheDrawingIsAsTallAsItsLowestLine()
    {
        var items = new[] { At(0, true, 40), At(10_000, false, 60) };
        var tops = TimelineLayout.Tops(items, pixelsPerSecond: 20);

        Assert.Equal(260, TimelineLayout.Height(items, tops));
    }

    [Fact]
    public void DensityFitsTheCallRatherThanBeingFixed()
    {
        // Two minutes and nineteen minutes cannot share a density and both stay readable.
        var brief = TimelineLayout.PixelsPerSecond(164_000, 700);
        var long_ = TimelineLayout.PixelsPerSecond(1_129_000, 700);

        Assert.True(brief > long_);
        Assert.InRange(brief, 8, 60);
        Assert.InRange(long_, 8, 60);
    }

    [Fact]
    public void ARecordingWithNoLengthStillGetsAUsableDensity()
    {
        Assert.True(TimelineLayout.PixelsPerSecond(0, 700) > 0);
    }

    // ---- the minute rules -------------------------------------------------
    //
    // The step has to come from the density: at eight pixels a second a minute is half a screen,
    // at sixty it is eight screens, and a fixed figure either crowds the rules together or leaves
    // nothing to measure against.

    /// <summary>A denser drawing never gets sparser marks than a looser one.</summary>
    [Fact]
    public void MarksNeverThinOutAsTheDrawingGetsDenser()
    {
        var densities = new[] { 8.0, 12.0, 20.0, 25.0, 40.0, 60.0 };

        for (var i = 1; i < densities.Length; i++)
        {
            Assert.True(TimelinePanel.StepSeconds(densities[i]) <= TimelinePanel.StepSeconds(densities[i - 1]),
                $"{densities[i]} px/sn, {densities[i - 1]} px/sn'den daha seyrek işaretleniyor");
        }
    }

    [Fact]
    public void RulesAreNeverDrawnOnTopOfEachOther()
    {
        foreach (var density in new[] { 8.0, 12.0, 25.0, 40.0, 60.0 })
        {
            var step = TimelinePanel.StepSeconds(density);

            Assert.True(step * density >= 100, $"{density} px/sn: {step} sn araligi çok dar");
        }
    }
}
