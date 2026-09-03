using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Voice;

namespace VoiceTranscript.Tests;

/// <summary>
/// Deciding who a voice belongs to, and — more importantly — refusing to.
///
/// The measurement these thresholds come from was run over this application's own archive, and
/// what it mostly found was that the archive's labels are wrong more often than the model is. So
/// the tests that matter here are the ones about not acting: on a thin match, on a voiceprint
/// built from a single unchecked call, and on a person whose own recordings disagree.
/// </summary>
public sealed class VoiceMatcherTests
{
    /// <summary>A unit vector pointing mostly one way, so two of them can be made to differ by a known amount.</summary>
    private static float[] Voice(double angle, int size = 256)
    {
        var vector = new float[size];
        vector[0] = (float)Math.Cos(angle);
        vector[1] = (float)Math.Sin(angle);
        return vector;
    }

    private static (Voiceprint, string) Known(long id, string name, float[] vector, int calls = 3) =>
        (new Voiceprint
        {
            ContactId = id,
            Vector = vector,
            Model = "test",
            CallsUsed = calls,
            SpeechSeconds = 120,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, name);

    [Fact]
    public void NobodyKnownMeansNoAnswer()
    {
        Assert.Equal(VoiceVerdict.Unknown, VoiceMatcher.Match(Voice(0), []).Verdict);
        Assert.Equal(VoiceVerdict.Unknown, VoiceMatcher.Match([], [Known(1, "Uliana", Voice(0))]).Verdict);
    }

    [Fact]
    public void AClearMatchAgainstAWellEstablishedVoiceIsFiled()
    {
        var match = VoiceMatcher.Match(
            Voice(0.05),
            [Known(1, "Uliana", Voice(0)), Known(2, "Serdal", Voice(Math.PI / 2))]);

        Assert.Equal(VoiceVerdict.Assign, match.Verdict);
        Assert.Equal("Uliana", match.Name);
        Assert.True(match.Score > VoiceMatcher.AssignScore);
    }

    [Fact]
    public void AThinMatchIsOfferedRatherThanApplied()
    {
        // About 0.5 — past the point of being worth mentioning, short of being worth acting on.
        var match = VoiceMatcher.Match(Voice(1.05), [Known(1, "Uliana", Voice(0))]);

        Assert.Equal(VoiceVerdict.Suggest, match.Verdict);
    }

    [Fact]
    public void AVoiceLikeNobodyKnownGetsNoName()
    {
        var match = VoiceMatcher.Match(Voice(Math.PI / 2), [Known(1, "Uliana", Voice(0))]);

        Assert.Equal(VoiceVerdict.Unknown, match.Verdict);
    }

    /// <summary>
    /// A recording that scores well against two people has not identified either of them.
    ///
    /// Without this the first contact enrolled would win every ambiguous call — which is precisely
    /// how the window-title binding filed every conversation under one person.
    /// </summary>
    [Fact]
    public void ScoringHighAgainstTwoPeopleIdentifiesNeither()
    {
        var match = VoiceMatcher.Match(
            Voice(0.35),
            [Known(1, "Uliana", Voice(0)), Known(2, "Serdal", Voice(0.7))]);

        Assert.NotEqual(VoiceVerdict.Assign, match.Verdict);
    }

    /// <summary>
    /// The labels in this archive are hand-typed and the user says some are wrong. A voiceprint
    /// built from one call has never been checked against anything, so it may speak but not act.
    /// </summary>
    [Fact]
    public void AVoiceLearnedFromASingleCallMayOnlySuggest()
    {
        var match = VoiceMatcher.Match(Voice(0.05), [Known(1, "Uliana", Voice(0), calls: 1)]);

        Assert.Equal(VoiceVerdict.Suggest, match.Verdict);
    }

    // ---- enrolment ----------------------------------------------------------

    [Fact]
    public void RecordingsThatAgreeBecomeOneVoice()
    {
        var (vector, used, rejected) = VoiceMatcher.Enrol(
            [(1, Voice(0)), (2, Voice(0.1)), (3, Voice(-0.1))]);

        Assert.Equal([1L, 2L, 3L], used);
        Assert.Empty(rejected);
        Assert.Equal(1.0, Voiceprint.Similarity(vector, vector), 3);
    }

    /// <summary>
    /// The guard against the archive's own mistakes, and the reason this feature is worth having
    /// on day one.
    ///
    /// One call filed under the wrong person drags that person's average towards a stranger and
    /// poisons every later match — silently, because an average always produces a number. The odd
    /// one out is left out and reported, and reporting it is how the wrong label gets found.
    /// </summary>
    [Fact]
    public void ARecordingThatDoesNotSoundLikeTheOthersIsLeftOutAndNamed()
    {
        var (vector, used, rejected) = VoiceMatcher.Enrol(
            [(1, Voice(0)), (2, Voice(0.1)), (3, Voice(0.05)), (99, Voice(Math.PI / 2))]);

        Assert.Equal([1L, 2L, 3L], used);
        Assert.Equal([99L], rejected);

        // And the stranger did not move the result: it still matches the three that agreed.
        Assert.True(Voiceprint.Similarity(vector, Voice(0.05)) > 0.99);
    }

    /// <summary>
    /// Two recordings that disagree leave nothing to build on. Picking one would be choosing which
    /// of two labels to believe with no reason to prefer either, and a wrong voiceprint is worse
    /// than none — it files future calls under the wrong person on its own.
    /// </summary>
    [Fact]
    public void TwoRecordingsThatDisagreeProduceNoVoiceAtAll()
    {
        var (vector, used, rejected) = VoiceMatcher.Enrol([(1, Voice(0)), (2, Voice(Math.PI / 2))]);

        Assert.Empty(vector);
        Assert.Empty(used);
        Assert.Equal(2, rejected.Count);
    }

    [Fact]
    public void ASingleRecordingIsTakenAsItIsBecauseThereIsNothingToCheckItAgainst()
    {
        var (vector, used, rejected) = VoiceMatcher.Enrol([(7, Voice(0.3))]);

        Assert.Equal([7L], used);
        Assert.Empty(rejected);
        Assert.True(Voiceprint.Similarity(vector, Voice(0.3)) > 0.99);
    }

    [Fact]
    public void NothingInNothingOut()
    {
        var (vector, used, rejected) = VoiceMatcher.Enrol([]);

        Assert.Empty(vector);
        Assert.Empty(used);
        Assert.Empty(rejected);
    }
}
