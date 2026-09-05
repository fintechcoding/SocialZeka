using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The habit counters: what is counted, whose lines, how sure, and against which denominator.
///
/// The product rule these pin is that Aynam is a mirror and nothing else. Only the user's own
/// lines are read; a count is a count with the minutes and the words behind it, never a figure
/// per call; a word the engine was unsure of is listed as "belirsiz" and not counted; and a
/// moment the user listened to and rejected leaves the count. And the one thing the counters
/// notice about numbers — that an IBAN, a phone number or an amount was read out — is kept as
/// a kind and a millisecond, never as the number.
/// </summary>
public sealed class SpeechHabitsTests
{
    private static readonly HabitLexicon Lexicon = HabitLexicon.From(HabitLexicon.EmbeddedSeed());

    private static Segment Line(
        bool me, int start, int end, string text,
        bool low = false, bool echo = false, IReadOnlyList<SpokenWord>? words = null) => new()
    {
        CallId = 1, IsMe = me, StartMs = start, EndMs = end, Text = text,
        LowConfidence = low, SuspectedEcho = echo, Words = words ?? [],
    };

    /// <summary>Evenly spaced word timings for a line, each with the same confidence.</summary>
    private static IReadOnlyList<SpokenWord> Timed(int start, string text, double? probability)
    {
        var pieces = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. pieces.Select((p, i) => new SpokenWord(start + i * 500, start + i * 500 + 400, p, probability))];
    }

    private static HabitReport Count(params Segment[] lines) => SpeechHabits.Count(lines, Lexicon, 0.6, []);

    /// <summary>The rule itself. Goes red when the other party's words reach a counter.</summary>
    [Fact]
    public void OnlyTheUsersLinesAreCounted()
    {
        var report = Count(
            Line(false, 0, 5000, "yani siktir git lan"),
            Line(true, 5000, 8000, "yani tamam"));

        var moment = Assert.Single(report.Moments);
        Assert.Equal(HabitKind.Filler, moment.Kind);
        Assert.Equal(1, report.MyLines);
        Assert.Equal(2, report.MyWords);
        Assert.Equal(3000, report.MySpokenMs);
    }

    /// <summary>
    /// A line the capture layer marked as the far end heard through the microphone is not the
    /// user's, whatever stream it came from. Goes red when it is counted or adds to the minutes.
    /// </summary>
    [Fact]
    public void ASuspectedEchoLineIsNotCounted()
    {
        var report = Count(
            Line(true, 0, 5000, "siktir lan", echo: true),
            Line(true, 5000, 6000, "tamam"));

        Assert.Empty(report.Moments);
        Assert.Equal(1, report.EchoLinesExcluded);
        Assert.Equal(1, report.MyLines);
        Assert.Equal(1000, report.MySpokenMs);
    }

    /// <summary>The word gate: below the engine's threshold is "belirsiz", listed and not counted.</summary>
    [Fact]
    public void AWordBelowTheThresholdIsUncertain()
    {
        var report = Count(
            Line(true, 0, 2000, "yani tamam", words: Timed(0, "yani tamam", 0.4)),
            Line(true, 2000, 4000, "yani gittik", words: Timed(2000, "yani gittik", 0.9)));

        Assert.Equal(HabitBucket.Uncertain, report.Moments[0].Bucket);
        Assert.Equal(HabitBucket.Certain, report.Moments[1].Bucket);

        var count = report.CountOf(HabitKind.Filler);
        Assert.Equal((1, 1, 0), (count.Certain, count.Uncertain, count.Dismissed));
        Assert.True(report.HasWordConfidence);
    }

    /// <summary>
    /// No threshold means the engine's scale has not been measured against listened verdicts,
    /// and an unmeasured threshold must not silently become 0.6. Goes red when a confidence
    /// moves a bucket while the threshold is null.
    /// </summary>
    [Fact]
    public void WithoutAThresholdTheWordConfidenceDoesNotJudge()
    {
        var report = SpeechHabits.Count(
            [Line(true, 0, 2000, "yani tamam", words: Timed(0, "yani tamam", 0.1))],
            Lexicon, wordThreshold: null, verdicts: []);

        Assert.Equal(HabitBucket.Certain, Assert.Single(report.Moments).Bucket);
        Assert.Null(report.WordThreshold);
    }

    /// <summary>The line gate: an engine with no word confidence still has its low-confidence mark on the line, and that alone makes the moment uncertain.</summary>
    [Fact]
    public void ALowConfidenceLineIsUncertainWithoutAnyWordConfidence()
    {
        var report = Count(Line(true, 0, 2000, "yani tamam", low: true));

        Assert.Equal(HabitBucket.Uncertain, Assert.Single(report.Moments).Bucket);
        Assert.False(report.HasWordConfidence);
    }

    /// <summary>
    /// "6,1 küfür / görüşme" was the first design and the wrong number. Goes red when the
    /// rate is not per minute of the USER's speech and per hundred of the USER's words — and
    /// when the other party's ten minutes leak into the denominator.
    /// </summary>
    [Fact]
    public void TheDenominatorsAreTheUsersOwnMinutesAndWords()
    {
        var mine = string.Join(' ', Enumerable.Repeat("tamam", 47)) + " lan lan lan";

        var report = Count(
            Line(false, 0, 600_000, "on dakika boyunca konuştu"),
            Line(true, 600_000, 630_000, mine),
            Line(true, 630_000, 660_000, "sessiz"));

        Assert.Equal(51, report.MyWords);
        Assert.Equal(60_000, report.MySpokenMs);
        Assert.Equal(3, report.CountOf(HabitKind.Profanity).Certain);

        Assert.Equal(3.0, report.PerMinute(HabitKind.Profanity));
        Assert.Equal(300.0 / 51, report.PerHundredWords(HabitKind.Profanity)!.Value, 6);
    }

    /// <summary>Every transcript from before word timings has none. Goes red when that crashes the count or invents a speech rate.</summary>
    [Fact]
    public void ALineWithoutWordTimingsPlacesTheMomentOnTheLineAndGivesNoRate()
    {
        var report = Count(Line(true, 4000, 9000, "hadi lan gidelim"));

        var moment = Assert.Single(report.Moments);
        Assert.Equal((4000, 9000), (moment.StartMs, moment.EndMs));
        Assert.Null(report.WordsPerMinute);
        Assert.Equal(0, report.TimedMs);
    }

    /// <summary>With timings the moment is the word, so the player lands on it and the verdict is keyed to it.</summary>
    [Fact]
    public void WordTimingsPlaceTheMomentOnTheWord()
    {
        var report = Count(Line(true, 4000, 9000, "hadi lan gidelim", words: Timed(4000, "hadi lan gidelim", 0.9)));

        var moment = Assert.Single(report.Moments);
        Assert.Equal((4500, 4900), (moment.StartMs, moment.EndMs));
        Assert.Equal("lan", moment.QuoteFolded);

        // Three words in five seconds: the rate is over the timed lines only.
        Assert.Equal(36.0, report.WordsPerMinute!.Value, 6);
    }

    /// <summary>
    /// The user's ear wins. Goes red when a moment they ruled misheard or "not that" is still
    /// counted, when a moment they confirmed stays uncertain, or when a verdict a few hundred
    /// milliseconds off — a recount from another transcript — fails to find its moment.
    /// </summary>
    [Fact]
    public void AVerdictRulesTheBucket()
    {
        var lines = new[]
        {
            Line(true, 0, 2000, "lan", words: Timed(0, "lan", 0.9)),
            Line(true, 10_000, 12_000, "lan", words: Timed(10_000, "lan", 0.9)),
            Line(true, 20_000, 22_000, "lan", words: Timed(20_000, "lan", 0.2)),
            Line(true, 30_000, 32_000, "lan", words: Timed(30_000, "lan", 0.9)),
        };

        Verdict Heard(int ms, VerdictValue value) => new()
        {
            CallId = 1, Kind = VerdictKind.Profanity, QuoteFolded = "lan", StartMs = ms, Value = value,
        };

        var report = SpeechHabits.Count(lines, Lexicon, 0.6,
        [
            Heard(800, VerdictValue.Misheard),
            Heard(10_000, VerdictValue.NotThat),
            Heard(20_000, VerdictValue.Correct),
        ]);

        Assert.Equal(
            [HabitBucket.Dismissed, HabitBucket.Dismissed, HabitBucket.Certain, HabitBucket.Certain],
            report.Moments.Select(m => m.Bucket));

        var count = report.CountOf(HabitKind.Profanity);
        Assert.Equal((2, 0, 2), (count.Certain, count.Uncertain, count.Dismissed));
    }

    /// <summary>A verdict on other words, or too far away, is somebody else's verdict.</summary>
    [Fact]
    public void AVerdictOnAnotherMomentIsNotApplied()
    {
        var report = SpeechHabits.Count(
            [Line(true, 0, 2000, "lan", words: Timed(0, "lan", 0.9))],
            Lexicon, 0.6,
            [
                new Verdict { CallId = 1, Kind = VerdictKind.Profanity, QuoteFolded = "lan", StartMs = 5000, Value = VerdictValue.Misheard },
                new Verdict { CallId = 1, Kind = VerdictKind.Profanity, QuoteFolded = "ulan", StartMs = 0, Value = VerdictValue.Misheard },
                new Verdict { CallId = 1, Kind = VerdictKind.Filler, QuoteFolded = "lan", StartMs = 0, Value = VerdictValue.Misheard },
            ]);

        Assert.Equal(HabitBucket.Certain, Assert.Single(report.Moments).Bucket);
    }

    /// <summary>
    /// The one rule with a number on it: an IBAN read out is stored as the kind and the
    /// millisecond. Goes red when the digits reach the report in any form.
    /// </summary>
    [Fact]
    public void AnIbanShapedRunIsDetectedByKindOnly()
    {
        var report = Count(Line(true, 5000, 12_000, "IBAN TR12 3456 7890 1234 5678 9012 34 yazayım"));

        var disclosure = Assert.Single(report.Disclosures);
        Assert.Equal(DisclosureKind.Iban, disclosure.Kind);
        Assert.Equal(5000, disclosure.StartMs);

        var json = new HabitSnapshot(report, TalkStats.Empty).ToJson();
        Assert.DoesNotContain("3456", json);
        Assert.DoesNotContain("TR12", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1234", json);
    }

    /// <summary>The other shapes: a phone number spaced the way the engines space it, an amount of six digits or more, an explicit date — and not a time of day.</summary>
    [Fact]
    public void PhoneAmountAndDateAreDetectedByKind()
    {
        var report = Count(
            Line(true, 0, 3000, "numaram 0532 123 45 67 tamam"),
            Line(true, 3000, 6000, "kira 150.000 lira olacak"),
            Line(true, 6000, 9000, "15 mart günü gelirim"),
            Line(true, 9000, 12_000, "saat 12 30 gibi, 3 kişi"));

        Assert.Equal(
            [DisclosureKind.Phone, DisclosureKind.Amount, DisclosureKind.Date],
            report.Disclosures.Select(d => d.Kind));

        var json = new HabitSnapshot(report, TalkStats.Empty).ToJson();
        Assert.DoesNotContain("0532", json);
        Assert.DoesNotContain("150", json);
    }

    /// <summary>The other party's IBAN is theirs to give; the mirror does not notice it. Goes red when a disclosure is counted on their line.</summary>
    [Fact]
    public void NothingAboutTheOtherPartyIsInTheReport()
    {
        var report = Count(Line(false, 0, 5000, "TR12 3456 7890 1234 5678 9012 34 lan siktir 0532 123 45 67"));

        Assert.Empty(report.Disclosures);
        Assert.Empty(report.Moments);
        Assert.Equal(0, report.MyWords);
    }

    /// <summary>The cache row holds this; what goes in must come out.</summary>
    [Fact]
    public void TheSnapshotRoundTripsThroughJson()
    {
        var report = Count(
            Line(true, 0, 2000, "yani lan", words: Timed(0, "yani lan", 0.9)),
            Line(true, 2000, 4000, "15 mart", low: true));

        var talk = TalkStats.Compute([Line(true, 0, 2000, "yani lan"), Line(false, 1500, 3000, "ne?")]);

        var json = new HabitSnapshot(report, talk).ToJson();
        var back = HabitSnapshot.FromJson(json);

        Assert.NotNull(back);
        Assert.Equal(report.Moments, back.Habits.Moments);
        Assert.Equal(report.Disclosures, back.Habits.Disclosures);
        Assert.Equal(report.Counts, back.Habits.Counts);
        Assert.Equal(report.MyWords, back.Habits.MyWords);
        Assert.Equal(report.WordsPerMinute, back.Habits.WordsPerMinute);
        Assert.Equal(talk, back.Talk);

        Assert.Null(HabitSnapshot.FromJson("{bozuk"));
    }

    /// <summary>Every kind has a row even at zero, so no screen has to special-case an absent one.</summary>
    [Fact]
    public void EveryCountedKindHasARowEvenAtZero()
    {
        var report = Count(Line(true, 0, 1000, "tamam"));

        Assert.Equal(HabitKind.Counted, report.Counts.Select(c => c.Kind));
        Assert.All(report.Counts, c => Assert.Equal(0, c.Listed));

        // A kind nobody counted is zero over the denominator, not an exception.
        Assert.Equal(0.0, report.PerMinute(HabitKind.Profanity));
        Assert.Equal(0.0, report.PerHundredWords("yok"));
    }

    /// <summary>The verdict key is the folded token, so "Lan" and "lan" are one moment.</summary>
    [Fact]
    public void TheQuoteKeyIsFolded()
    {
        var report = Count(Line(true, 0, 1000, "Şey, LAN!"));

        Assert.Equal(["sey", "lan"], report.Moments.Select(m => m.QuoteFolded));
        Assert.Equal(TurkishText.NormalizeForSearch("Şey"), report.Moments[0].QuoteFolded);
    }
}
