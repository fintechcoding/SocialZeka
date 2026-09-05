using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Word timings, into one column and back out.
///
/// The encoding is an implementation detail with one property that is not: a line whose timings
/// cannot be read must still be a line. Losing the moment a word was said costs a feature;
/// losing the words costs the recording.
/// </summary>
public class SegmentWordsTests
{
    [Fact]
    public void WordsSurviveTheRoundTrip()
    {
        IReadOnlyList<SpokenWord> words =
        [
            new(920, 1180, " Abi"),
            new(1180, 1680, " ne"),
            new(1680, 1920, " yapıyorsun?"),
        ];

        var back = SegmentWords.Read(SegmentWords.Write(words));

        Assert.Equal(words, back);
    }

    /// <summary>
    /// The engine's confidence travels with the word when there is one, and a word without one
    /// stays a triple. Goes red when a confidence is dropped on the way to the column (the habit
    /// counters would then have nothing to bucket by), or when a plain triple — every line
    /// written before confidences were kept — no longer reads back as it was.
    /// </summary>
    [Fact]
    public void AConfidenceRidesAlongOnlyWhenTheEngineGaveOne()
    {
        IReadOnlyList<SpokenWord> words =
        [
            new(920, 1180, " Abi", 0.9123456),
            new(1180, 1680, " ne"),
        ];

        var json = SegmentWords.Write(words);

        Assert.Equal("""[[920,1180," Abi",0.912],[1180,1680," ne"]]""", json);

        var back = SegmentWords.Read(json);
        Assert.Equal(0.912, back[0].Probability);
        Assert.Null(back[1].Probability);

        // A stored triple from before is exactly a word with no confidence.
        Assert.Equal([new SpokenWord(10, 20, "eski")], SegmentWords.Read("""[[10,20,"eski"]]"""));

        // And a fourth element that is not a number is not a confidence.
        Assert.Null(SegmentWords.Read("""[[10,20,"eski","x"]]""")[0].Probability);
    }

    [Fact]
    public void ALineWithNoWordsStoresNothing()
    {
        // Null rather than "[]", so the column says "never had any" and costs nothing on the
        // hundreds of calls transcribed before it existed.
        Assert.Null(SegmentWords.Write([]));
        Assert.Null(SegmentWords.Write(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingStoredReadsBackAsNoWords(string? stored)
    {
        Assert.Empty(SegmentWords.Read(stored));
    }

    [Theory]
    [InlineData("bu json degil")]
    [InlineData("{\"words\":[]}")]
    [InlineData("[[1,2]]")]
    [InlineData("[[\"a\",\"b\",\"c\"]]")]
    [InlineData("[null]")]
    public void UnreadableTimingsCostTheTimingsAndNotTheLine(string stored)
    {
        // Every one of these returns rather than throwing: ReplaceSegments writes this column,
        // and a parse failure while reading a transcript would take the transcript with it.
        Assert.Empty(SegmentWords.Read(stored));
    }

    [Fact]
    public void OneBadRowDoesNotDiscardTheGoodOnes()
    {
        var back = SegmentWords.Read("""[[100,200,"bir"],[300],[400,500,"iki"]]""");

        Assert.Equal(2, back.Count);
        Assert.Equal("bir", back[0].Text);
        Assert.Equal("iki", back[1].Text);
    }

    [Fact]
    public void TheEncodingStaysSmallEnoughForALongCall()
    {
        // Three thousand words is an ordinary twenty-minute call. Spelled as objects with named
        // fields the same content runs past 150 KB per call, on an archive of hundreds.
        IReadOnlyList<SpokenWord> many =
            [.. Enumerable.Range(0, 3000).Select(i => new SpokenWord(i * 300, i * 300 + 250, " kelime"))];

        var json = SegmentWords.Write(many);

        Assert.NotNull(json);
        Assert.True(json.Length < 90_000, $"{json.Length} karakter — beklenenden büyük");
        Assert.Equal(3000, SegmentWords.Read(json).Count);
    }
}
