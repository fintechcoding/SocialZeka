using VoiceTranscript.Core.Analysis;

namespace VoiceTranscript.Tests;

/// <summary>
/// Placing one channel's level and pitch against the rest of that same channel.
///
/// The rule these tests exist to hold is the one that makes the whole measurement honest: a
/// number is compared only with numbers from the same channel of the same call. Microphone gain
/// is a property of the hardware and the far channel's level is whatever the other application's
/// automatic gain decided; between two calls they are two different rulers. Everything below is
/// therefore relative — a median, and distances from it — and a peak is never more than a place
/// to listen.
/// </summary>
public class ProsodySeriesTests
{
    private const double Bin = 0.5;

    /// <summary>
    /// A channel of ordinary speech: a level and a pitch that wobble the way a voice does, around
    /// a known middle. Deliberately not identical bins — real speech is never flat, and a test
    /// built from flat data would exercise a path the product never takes.
    /// </summary>
    private static List<ProsodyBin> Flat(int count, double dbfs = -30, double? f0 = 120, double voiced = 0.8) =>
    [
        .. Enumerable.Range(0, count).Select(i => new ProsodyBin(
            i * Bin,
            dbfs + Wobble(i),
            f0 is { } hz ? hz + Wobble(i) : null,
            voiced)),
    ];

    /// <summary>A small deterministic wobble: ±1.5, never the same two bins running.</summary>
    private static double Wobble(int index) => (index % 7 - 3) * 0.5;

    private static ProsodyChannel Channel(IEnumerable<ProsodyBin> bins) => new(-70, 20, [.. bins]);

    /// <summary>
    /// Goes red when a level or a pitch is reported as an absolute figure to be compared with
    /// another call's. Everything the screen shows is a distance from this channel's own median,
    /// and the median is the median of the bins that carry speech.
    /// </summary>
    [Fact]
    public void EverythingIsMeasuredAgainstThisChannelsOwnMedian()
    {
        var bins = Flat(40);
        bins[20] = bins[20] with { Dbfs = -12 };

        var reading = ProsodySeries.Build(Channel(bins), Bin);

        // The middle of an ordinary conversation, whatever the absolute figures happen to be.
        Assert.Equal(-30, reading.LevelMedianDbfs!.Value, 0.5);
        Assert.Equal(120, reading.PitchMedianHz!.Value, 0.5);

        // A bin sitting on the median is zero distance from it — the level itself is never shown.
        Assert.Equal(0, reading.Points[3].LevelZ!.Value, 1);

        // The loud one is above it, and that it is above is the only thing said about it.
        Assert.True(reading.Points[20].LevelZ > 0);
    }

    /// <summary>
    /// The reason the distance is measured in MAD units rather than standard deviations.
    ///
    /// Goes red if a plain standard deviation creeps back in: three shouts inflate it enough that
    /// none of them clears the threshold any more — the outliers drag the very yardstick meant to
    /// find them, and a call with shouting in it reports a calm conversation.
    /// </summary>
    [Fact]
    public void ShoutingDoesNotHideItselfByWideningTheYardstick()
    {
        var bins = Flat(60);
        foreach (var at in new[] { 10, 11, 12, 13, 14, 30, 31, 32, 33, 34, 50, 51, 52, 53, 54 })
            bins[at] = bins[at] with { Dbfs = -12 };

        var reading = ProsodySeries.Build(Channel(bins), Bin);

        var peaks = reading.Peaks.Where(p => p.Measure == ProsodyMeasure.Level).ToList();

        Assert.Equal(3, peaks.Count);
        Assert.All(peaks, p => Assert.True(p.Z > ProsodySeries.PeakZ));
        Assert.Equal(5000, peaks[0].StartMs);
    }

    /// <summary>
    /// Goes red when a moment's worth of loudness is called a peak. Four half-second bins is two
    /// seconds — a raised voice, not a syllable that happened to land near the microphone.
    /// </summary>
    [Fact]
    public void AShortSpikeIsNotAPeak()
    {
        var bins = Flat(60);
        bins[20] = bins[20] with { Dbfs = -10 };
        bins[21] = bins[21] with { Dbfs = -10 };
        bins[22] = bins[22] with { Dbfs = -10 };

        var reading = ProsodySeries.Build(Channel(bins), Bin);

        Assert.Empty(reading.Peaks);
    }

    /// <summary>
    /// Goes red when a stretch where both people are talking, or where the far side bled into the
    /// microphone, is counted. A level measured across another voice is that other voice, and
    /// counting it would put somebody else's shouting on this person's curve.
    /// </summary>
    [Fact]
    public void OverlappingStretchesTakeNoPart()
    {
        var bins = Flat(60);
        foreach (var at in new[] { 20, 21, 22, 23, 24, 25 })
            bins[at] = bins[at] with { Dbfs = -8 };

        var reading = ProsodySeries.Build(Channel(bins), Bin, [(9_500, 13_500)]);

        Assert.Empty(reading.Peaks);
        Assert.Equal(8, reading.ExcludedBins);
        Assert.All(reading.Points.Where(p => p.Excluded), p => Assert.Null(p.LevelZ));

        // And the excluded bins are still reported — measured, not counted.
        Assert.Equal(60, reading.Points.Count);
    }

    /// <summary>
    /// Pitch is heard logarithmically. Goes red if hertz reach the screen: twenty hertz above a
    /// low voice and twenty above a high one are not the same event, and a linear figure calls
    /// the second one nothing.
    /// </summary>
    [Fact]
    public void PitchIsMeasuredInSemitones()
    {
        Assert.Equal(12, ProsodySeries.Semitones(240, 120), 6);
        Assert.Equal(-12, ProsodySeries.Semitones(60, 120), 6);

        var bins = Flat(40);
        bins[10] = bins[10] with { F0Hz = 240 };

        var reading = ProsodySeries.Build(Channel(bins), Bin);

        Assert.Equal(12, reading.Points[10].PitchSemitones!.Value, 0.1);
        Assert.Equal(0, reading.Points[3].PitchSemitones!.Value, 0.1);
    }

    /// <summary>
    /// Goes red when a handful of bins is enough to declare a median and start finding peaks in
    /// it. Fifteen seconds of speech is the least that makes "usual for this channel" mean
    /// anything; below it the screen says nothing rather than something shaky.
    /// </summary>
    [Fact]
    public void TooLittleSpeechYieldsNoPeaksAndSaysSo()
    {
        var bins = Flat(10);
        foreach (var at in new[] { 4, 5, 6, 7 })
            bins[at] = bins[at] with { Dbfs = -5 };

        var reading = ProsodySeries.Build(Channel(bins), Bin);

        Assert.Empty(reading.Peaks);
        Assert.False(reading.IsUsable);
        Assert.Equal(10, reading.MeasuredBins);
    }

    /// <summary>Silence is not a quiet moment in a conversation; it is the absence of one.</summary>
    [Fact]
    public void SilentBinsStayOutOfTheMedian()
    {
        var speech = Flat(40);
        var silence = Enumerable.Range(40, 40).Select(i => new ProsodyBin(i * Bin, -70, null, 0));

        var reading = ProsodySeries.Build(Channel([.. speech, .. silence]), Bin);

        Assert.Equal(-30, reading.LevelMedianDbfs);
        Assert.Equal(40, reading.MeasuredBins);
        Assert.All(reading.Points.Skip(40), p => Assert.Null(p.LevelZ));
    }

    /// <summary>An empty channel is an empty reading, not an exception.</summary>
    [Fact]
    public void NothingMeasuredIsNotAFailure()
    {
        Assert.Same(ProsodyReading.Empty, ProsodySeries.Build(ProsodyChannel.Empty, Bin));
        Assert.False(ProsodyReading.Empty.IsUsable);
    }

    /// <summary>
    /// The stored measurement round-trips, and a row this build cannot read is a missing
    /// measurement rather than a crash on opening a conversation.
    /// </summary>
    [Fact]
    public void TheSnapshotSurvivesTheColumnAndBadJsonIsNull()
    {
        var snapshot = new ProsodySnapshot(Bin, Channel(Flat(4)), null);

        var back = ProsodySnapshot.FromJson(snapshot.ToJson());

        Assert.NotNull(back);
        Assert.Equal(Bin, back!.BinSeconds);
        Assert.Equal(4, back.Mic!.Bins.Count);
        Assert.Null(back.Far);

        Assert.Null(ProsodySnapshot.FromJson("bu json değil"));
        Assert.Null(ProsodySnapshot.FromJson(null));
    }

    /// <summary>
    /// The key says which recording a measurement was made from. Goes red when a trimmed or
    /// re-encoded file keeps the old key — the curve would then be of audio that no longer exists.
    /// </summary>
    [Fact]
    public void TheAudioKeyFollowsTheFileNotTheTranscript()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vt-prosody-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var mic = Path.Combine(directory, "call-1-mic.wav");
            File.WriteAllBytes(mic, new byte[1000]);

            var before = ProsodySeries.AudioKey(mic, null);

            File.WriteAllBytes(mic, new byte[600]);
            Assert.NotEqual(before, ProsodySeries.AudioKey(mic, null));

            // A channel that was never recorded is part of the key too: one file becoming two is
            // a different recording.
            Assert.NotEqual(ProsodySeries.AudioKey(mic, null), ProsodySeries.AudioKey(mic, mic));

            // A missing file never matches a present one, so the measurement is redone rather
            // than shown against audio that is gone.
            Assert.NotEqual(before, ProsodySeries.AudioKey(Path.Combine(directory, "yok.wav"), null));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
