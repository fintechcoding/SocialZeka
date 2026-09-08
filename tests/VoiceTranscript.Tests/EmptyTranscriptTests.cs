using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// Reading an empty transcription before calling it a failure.
///
/// Four one-minute outgoing calls in this archive came back with no words on either side and were
/// reported as "İşleme başarısız" — unanswered calls, listed beside real failures and deleted by
/// hand. These pin the reading: short and silent is set aside with its audio, long and silent is
/// still the engine's problem, and an existing transcript is never overwritten by nothing.
/// </summary>
public sealed class EmptyTranscriptTests
{
    [Fact]
    public void AShortOutgoingCallWithNothingSaidIsProbablyUnanswered()
    {
        var verdict = EmptyTranscript.Judge(CallDirection.Outgoing, TimeSpan.FromSeconds(67), hadTranscript: false);

        Assert.Equal(ProcessingState.Skipped, verdict.State);
        Assert.Contains("cevapsız", verdict.Reason);
        Assert.Contains("01:07", verdict.Reason);
        Assert.Contains("cevapsız", verdict.Notice);
    }

    [Theory]
    [InlineData(CallDirection.Incoming)]
    [InlineData(CallDirection.Unknown)]
    public void AShortCallOfUnknownOrIncomingDirectionIsSetAsideWithoutTheGuess(CallDirection direction)
    {
        var verdict = EmptyTranscript.Judge(direction, TimeSpan.FromSeconds(40), hadTranscript: false);

        Assert.Equal(ProcessingState.Skipped, verdict.State);
        Assert.StartsWith("Konuşma bulunamadı", verdict.Reason);
        Assert.DoesNotContain("cevapsız", verdict.Reason);
    }

    [Fact]
    public void ALongRecordingWithNoWordsAtAllIsTheEnginesFault()
    {
        var verdict = EmptyTranscript.Judge(CallDirection.Outgoing, TimeSpan.FromMinutes(10), hadTranscript: false);

        Assert.Equal(ProcessingState.Failed, verdict.State);
        Assert.Contains("motor", verdict.Reason);
        Assert.DoesNotContain("cevapsız", verdict.Reason);
    }

    [Fact]
    public void TheLineIsGenerousEnoughForARingOutAndNoLonger()
    {
        Assert.Equal(ProcessingState.Skipped,
            EmptyTranscript.Judge(CallDirection.Outgoing, EmptyTranscript.LongestQuietCall, false).State);

        Assert.Equal(ProcessingState.Failed,
            EmptyTranscript.Judge(CallDirection.Outgoing, EmptyTranscript.LongestQuietCall + TimeSpan.FromSeconds(1), false).State);
    }

    [Fact]
    public void AnExistingTranscriptIsNeverReplacedByNothing()
    {
        var verdict = EmptyTranscript.Judge(CallDirection.Outgoing, TimeSpan.FromSeconds(30), hadTranscript: true);

        Assert.Equal(ProcessingState.Failed, verdict.State);
        Assert.Contains("korundu", verdict.Reason);
    }

    /// <summary>
    /// The one rule in the application that deletes a recording. Every condition is required,
    /// and each row here is a way somebody could lose audio if it were not.
    /// </summary>
    [Theory]
    // enabled, measured, hasTranscript, expected
    [InlineData(true, true, false, true)]     // the only yes
    [InlineData(false, true, false, false)]   // the user turned it off
    [InlineData(true, false, false, false)]   // the audio did not say so
    [InlineData(true, true, true, false)]     // there are words from this call already
    [InlineData(false, false, false, false)]
    public void ARecordingIsThrownAwayOnlyWhenAllThreeConditionsHold(
        bool enabled, bool measured, bool hasTranscript, bool expected)
    {
        Assert.Equal(expected, EmptyTranscript.ShouldDiscardAsUnanswered(enabled, measured, hasTranscript));
    }

    /// <summary>
    /// A re-transcription of a call that already has words must never delete its audio, however
    /// convincing the ring at the front of it looks.
    /// </summary>
    [Fact]
    public void ACallThatAlreadyHasWordsIsNeverDiscarded()
    {
        Assert.False(EmptyTranscript.ShouldDiscardAsUnanswered(
            enabled: true, measuredUnanswered: true, hasTranscript: true));
    }

    /// <summary>
    /// A call the audio itself says rang unanswered: set aside, and the row says the recording
    /// went and that nothing was ever uploaded.
    /// </summary>
    [Fact]
    public void AMeasuredRingOutIsSetAsideAndSaysItsAudioIsGone()
    {
        var verdict = EmptyTranscript.Unanswered(TimeSpan.FromSeconds(63), audioKept: false);

        Assert.Equal(ProcessingState.Skipped, verdict.State);
        Assert.StartsWith("Cevapsız arama", verdict.Reason);
        Assert.Contains("01:03", verdict.Reason);
        Assert.Contains("silindi", verdict.Reason);
        Assert.Contains("gönderilmedi", verdict.Reason);
    }

    /// <summary>
    /// A file a player still holds open survives the deletion, and then the row must not claim
    /// it was deleted — a sentence that contradicts the disk is worse than no sentence.
    /// </summary>
    [Fact]
    public void ARingOutWhoseAudioSurvivedSaysThatInstead()
    {
        var verdict = EmptyTranscript.Unanswered(TimeSpan.FromSeconds(63), audioKept: true);

        Assert.Equal(ProcessingState.Skipped, verdict.State);
        Assert.Contains("duruyor", verdict.Reason);
        Assert.DoesNotContain("silindi", verdict.Reason);
    }

    /// <summary>The row's word is decided by the reason's opening, so the opening is pinned.</summary>
    [Fact]
    public void EverySetAsideReasonOpensWithTheWordsTheRowIsToldApartBy()
    {
        foreach (var direction in new[] { CallDirection.Outgoing, CallDirection.Incoming, CallDirection.Unknown })
        {
            var verdict = EmptyTranscript.Judge(direction, TimeSpan.FromSeconds(50), hadTranscript: false);
            Assert.StartsWith("Konuşma bulunamadı", verdict.Reason);
        }
    }
}
