using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.Tests;

/// <summary>
/// Which line is being spoken, while the recording plays.
///
/// The shape that broke this is real and ordinary: in one call from this archive, 144 of 217
/// lines overlap another. A nine-second turn with a half-second "iyi" inside it used to move the
/// mark onto the interjection and leave it there — so the long line played on with nothing
/// marked, and the view had already scrolled away from it.
///
/// The numbers below are that call, verbatim.
/// </summary>
public class SpokenLineTests
{
    /// <summary>Uliana talks for nine seconds; the user answers three times inside it.</summary>
    private static List<ChatTurn> Overlapping() =>
    [
        new("Uliana", "Yaşı, atpravila...", 55960, 65180, isMe: false, false, true, false),
        new("Sen", "Değil mi onunla uğraşıyorum?", 59380, 60140, isMe: true, false, true, false),
        new("Sen", "İyi.", 62680, 62900, isMe: true, false, true, false),
        new("Sen", "İyi.", 65820, 65990, isMe: true, false, false, false),
    ];

    [Fact]
    public void BothSpeakersAreMarkedWhileTheyTalkOverEachOther()
    {
        var turns = Overlapping();

        VoiceTranscript.App.ViewModels.CallWindowViewModel.Spoken(turns, 59500);

        Assert.True(turns[0].IsCurrent, "Uliana hâlâ konuşuyor");
        Assert.True(turns[1].IsCurrent, "kullanıcı araya girdi");
        Assert.False(turns[2].IsCurrent);
        Assert.False(turns[3].IsCurrent);
    }

    [Fact]
    public void TheViewFollowsTheLineStillInProgressRatherThanTheInterjection()
    {
        // The whole reason the anchor is the earliest one still speaking: following the newest
        // would scroll to a half-second line and straight back, twice a second.
        var turns = Overlapping();

        Assert.Same(turns[0], CallWindowViewModel.Spoken(turns, 59500));
        Assert.Same(turns[0], CallWindowViewModel.Spoken(turns, 62700));
    }

    [Fact]
    public void AFinishedInterjectionStopsBeingMarkedAndTheLongLineKeepsIt()
    {
        var turns = Overlapping();

        CallWindowViewModel.Spoken(turns, 63500); // after "İyi." ended at 62.90

        Assert.True(turns[0].IsCurrent, "Uliana konuşmaya devam ediyor");
        Assert.False(turns[2].IsCurrent, "biten araya girme işareti bırakmalı");
    }

    [Fact]
    public void ALineThatHasNotStartedIsNeverMarked()
    {
        var turns = Overlapping();

        CallWindowViewModel.Spoken(turns, 56000);

        Assert.True(turns[0].IsCurrent);
        Assert.All(turns.Skip(1), t => Assert.False(t.IsCurrent));
    }

    [Fact]
    public void InTheSilenceBetweenLinesTheLastOneKeepsTheMark()
    {
        // Otherwise the transcript blanks every time somebody draws breath, and the reader loses
        // their place for exactly as long as the pause lasts.
        var turns = Overlapping();

        var anchor = CallWindowViewModel.Spoken(turns, 65500); // Uliana ended 65.18, next starts 65.82

        Assert.Same(turns[0], anchor);
        Assert.True(turns[0].IsCurrent);
    }

    [Fact]
    public void BeforeTheFirstLineNothingIsMarked()
    {
        var turns = Overlapping();

        Assert.Null(CallWindowViewModel.Spoken(turns, 1000));
        Assert.All(turns, t => Assert.False(t.IsCurrent));
    }

    [Fact]
    public void AnEmptyTranscriptIsNotAFailure()
    {
        Assert.Null(CallWindowViewModel.Spoken([], 5000));
    }

    [Fact]
    public void OrdinaryTurnTakingStillMarksExactlyOneLine()
    {
        // The case that always worked must keep working: no overlap, one mark.
        List<ChatTurn> turns =
        [
            new("Uliana", "Alo.", 5720, 5980, isMe: false, false, false, false),
            new("Sen", "Alo, sigaram yoksa...", 6460, 12140, isMe: true, false, false, false),
            new("Uliana", "Iii.", 14170, 14540, isMe: false, false, false, false),
        ];

        CallWindowViewModel.Spoken(turns, 8000);

        Assert.Single(turns, t => t.IsCurrent);
        Assert.True(turns[1].IsCurrent);
    }
}
