using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Reading the conversation in the order it happened.
///
/// The numbers behind the threshold are from one real call: of the 56 turns buried inside another
/// line, 18 were back-channel and 38 were real answers — the longest of them seven words. Both
/// halves of that split are tested here, because getting either one wrong is a regression nobody
/// would see until they read a transcript and it made no sense.
/// </summary>
public class ChatFlowTests
{
    private static SpokenWord[] Words(params (int Start, int End, string Text)[] spec) =>
        [.. spec.Select(w => new SpokenWord(w.Start, w.End, w.Text))];

    private static Segment Line(bool mine, string text, SpokenWord[] words) => new()
    {
        CallId = 1,
        IsMe = mine,
        StartMs = words.Length > 0 ? words[0].StartMs : 0,
        EndMs = words.Length > 0 ? words[^1].EndMs : 0,
        Text = text,
        Words = words,
    };

    private static Segment Short(bool mine, string text, int start, int end) => new()
    {
        CallId = 1,
        IsMe = mine,
        StartMs = start,
        EndMs = end,
        Text = text,
        Words = [new SpokenWord(start, end, text)],
    };

    [Fact]
    public void ALineWithNothingBuriedInItIsUntouched()
    {
        var mine = Line(true, "bir iki üç", Words((0, 500, " bir"), (500, 1000, " iki"), (1000, 1500, " üç")));
        var theirs = Short(false, "tamam abi", 2000, 3000);

        var read = ChatFlow.InReadingOrder([mine, theirs]);

        Assert.Equal(2, read.Count);
        Assert.Equal("bir iki üç", read[0].Text);
    }

    [Fact]
    public void ARealAnswerCutsTheLineThatWouldBuryIt()
    {
        // Twelve seconds of me, with seven words of them at second five.
        var mine = Line(true, "bir iki üç dört", Words(
            (0, 1000, " bir"), (1000, 2000, " iki"), (6000, 7000, " üç"), (7000, 12000, " dört")));

        var theirs = Short(false, "tabii o için de o anlaşılmıyor ama", 5000, 5800);

        var read = ChatFlow.InReadingOrder([mine, theirs]);

        Assert.Equal(3, read.Count);
        Assert.Equal("bir iki", read[0].Text);
        Assert.Equal("tabii o için de o anlaşılmıyor ama", read[1].Text);
        Assert.Equal("üç dört", read[2].Text);
    }

    [Fact]
    public void BackChannelDoesNotCutASentenceInHalf()
    {
        // "Ha," — one word, a third of a second. Not a turn.
        var mine = Line(true, "bir iki üç dört", Words(
            (0, 1000, " bir"), (1000, 2000, " iki"), (6000, 7000, " üç"), (7000, 12000, " dört")));

        var theirs = Short(false, "Ha,", 5000, 5320);

        var read = ChatFlow.InReadingOrder([mine, theirs]);

        Assert.Equal(2, read.Count);
        Assert.Equal("bir iki üç dört", read.Single(s => s.IsMe).Text);
    }

    [Fact]
    public void AOneWordTurnThatHeldTheFloorLongEnoughStillCuts()
    {
        // "Yaaa" — a single word, but 900 ms of it. Either test may qualify a turn.
        var mine = Line(true, "bir iki üç dört", Words(
            (0, 1000, " bir"), (1000, 2000, " iki"), (6000, 7000, " üç"), (7000, 12000, " dört")));

        var theirs = Short(false, "Yaaa", 5000, 5900);

        var read = ChatFlow.InReadingOrder([mine, theirs]);

        Assert.Equal(3, read.Count);
    }

    [Fact]
    public void ALineWithoutWordTimestampsIsLeftAlone()
    {
        // Everything transcribed before word timestamps were kept. There is nowhere to cut that
        // is not invented.
        var mine = new Segment { CallId = 1, IsMe = true, StartMs = 0, EndMs = 12000, Text = "eski satır" };
        var theirs = Short(false, "tamam abi ya", 5000, 6000);

        var read = ChatFlow.InReadingOrder([mine, theirs]);

        Assert.Equal(2, read.Count);
        Assert.Equal("eski satır", read.Single(s => s.IsMe).Text);
    }

    [Fact]
    public void TwoAnswersInsideOneLineCutItTwice()
    {
        var mine = Line(true, "bir iki üç dört beş altı", Words(
            (0, 1000, " bir"), (1000, 2000, " iki"),
            (6000, 7000, " üç"), (7000, 8000, " dört"),
            (12000, 13000, " beş"), (13000, 14000, " altı")));

        var first = Short(false, "evet abi tamam", 5000, 5900);
        var second = Short(false, "he he olur", 10000, 11000);

        var read = ChatFlow.InReadingOrder([mine, first, second]);

        Assert.Equal(5, read.Count);
        Assert.Equal("bir iki", read[0].Text);
        Assert.Equal("üç dört", read[2].Text);
        Assert.Equal("beş altı", read[4].Text);
    }

    [Fact]
    public void ThePiecesKeepTheirOwnSpanAndTheParentsFlags()
    {
        var mine = Line(true, "bir iki üç", Words((0, 1000, " bir"), (6000, 7000, " iki"), (7000, 9000, " üç")))
            with { LowConfidence = true };

        var theirs = Short(false, "evet abi tamam", 5000, 5900);

        var read = ChatFlow.InReadingOrder([mine, theirs]);
        var pieces = read.Where(s => s.IsMe).ToList();

        Assert.Equal(2, pieces.Count);
        Assert.Equal(0, pieces[0].StartMs);
        Assert.Equal(1000, pieces[0].EndMs);
        Assert.Equal(6000, pieces[1].StartMs);
        Assert.Equal(9000, pieces[1].EndMs);
        Assert.All(pieces, p => Assert.True(p.LowConfidence));
        Assert.All(pieces, p => Assert.True(p.OverlapsOtherSpeaker));
    }

    [Fact]
    public void TheResultStaysInTimeOrder()
    {
        var mine = Line(true, "bir iki üç", Words((0, 1000, " bir"), (6000, 7000, " iki"), (7000, 9000, " üç")));
        var theirs = Short(false, "evet abi tamam", 5000, 5900);

        var read = ChatFlow.InReadingOrder([mine, theirs]);

        Assert.Equal([.. read.Select(s => s.StartMs).Order()], [.. read.Select(s => s.StartMs)]);
    }
}
