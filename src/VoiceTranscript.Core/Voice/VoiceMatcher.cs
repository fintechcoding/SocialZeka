using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Voice;

/// <summary>What the voice concluded, and how far the application is willing to act on it.</summary>
public enum VoiceVerdict
{
    /// <summary>Nothing close enough. Say nothing — a wrong name is worse than no name.</summary>
    Unknown,

    /// <summary>Close enough to offer, not close enough to file. Prefills the labelling window.</summary>
    Suggest,

    /// <summary>Close enough to file on its own, and recorded as having been filed that way.</summary>
    Assign,
}

public sealed record VoiceMatch(VoiceVerdict Verdict, long ContactId, string Name, double Score, double Margin)
{
    public static readonly VoiceMatch None = new(VoiceVerdict.Unknown, 0, "", 0, 0);
}

/// <summary>
/// Deciding who a voice belongs to, and refusing to decide when the evidence is thin.
///
/// All of this is arithmetic over unit vectors, which is why it lives here rather than in the
/// Python worker: the worker turns audio into 256 numbers and knows nothing about contacts, and
/// the address book never has to cross the pipe.
///
/// <b>The thresholds below are provisional and the code should say so.</b> They come from a
/// leave-one-out measurement over this application's own archive — the honest thing to measure,
/// because the published figures for these models are about wideband English read speech and this
/// is 16 kHz Turkish conversation decoded back from a compressed archive. But that archive held
/// only four people with more than one call, and at least one of its labels is wrong: two
/// recordings filed under different names score 0.910, higher than any genuine pair. So the
/// numbers are set where they cost silence rather than mistakes, and they are meant to be
/// measured again once there are more voices to measure against.
/// </summary>
public static class VoiceMatcher
{
    /// <summary>
    /// Above this, and clear of the runner-up, the call is filed without asking.
    ///
    /// Measured: two recordings of one person average 0.75 once there is enough speech, two
    /// different people 0.18. This sits well above the midpoint on purpose — the cost of the two
    /// mistakes is not symmetric. A call left unlabelled asks a question; a call labelled wrongly
    /// corrupts two people's histories at once and does it quietly.
    /// </summary>
    public const double AssignScore = 0.55;

    /// <summary>
    /// How far ahead of the second-best candidate the winner has to be.
    ///
    /// A high score against everybody means the recording is generic, not that it is anybody in
    /// particular. Without this, the first contact enrolled would win every ambiguous call.
    /// </summary>
    public const double AssignMargin = 0.15;

    /// <summary>
    /// Above this the name is offered but not applied — it prefills the labelling window, which
    /// already treats its suggestion as "fill this in, do not file it".
    /// </summary>
    public const double SuggestScore = 0.40;

    /// <summary>
    /// How many calls a voiceprint must be built from before it may file anything on its own.
    ///
    /// One call cannot be checked against anything, so its label has never been tested — and the
    /// labels in this archive are hand-typed and demonstrably fallible. Two calls that agree with
    /// each other are the smallest amount of evidence that the label is right.
    /// </summary>
    public const int CallsBeforeAssigning = 2;

    /// <summary>
    /// How far a recording may sit from the rest of a person's calls and still help define them.
    ///
    /// This is the guard against the archive's own mistakes. A voiceprint is the average of
    /// somebody's calls, so one call filed under the wrong name drags the average towards a
    /// stranger and quietly poisons every later match. Anything below this is left out of the
    /// average and reported instead — which is also how the archive gets cleaned up, since the
    /// recording that does not fit is usually the one that is filed wrongly.
    /// </summary>
    public const double ConsistencyFloor = 0.35;

    /// <summary>
    /// Who this voice is, out of everybody known — or nothing, said plainly.
    ///
    /// Candidates carry the number of calls they were built from because that decides whether the
    /// answer may file anything: a print from a single call may only ever suggest.
    /// </summary>
    public static VoiceMatch Match(
        IReadOnlyList<float> voice,
        IReadOnlyList<(Voiceprint Print, string Name)> known)
    {
        if (voice.Count == 0 || known.Count == 0) return VoiceMatch.None;

        var scored = known
            .Select(k => (k.Print, k.Name, Score: Voiceprint.Similarity(k.Print.Vector, voice)))
            .OrderByDescending(k => k.Score)
            .ToList();

        var best = scored[0];
        var runnerUp = scored.Count > 1 ? scored[1].Score : double.NegativeInfinity;
        var margin = double.IsNegativeInfinity(runnerUp) ? best.Score : best.Score - runnerUp;

        if (best.Score >= AssignScore
            && margin >= AssignMargin
            && best.Print.CallsUsed >= CallsBeforeAssigning)
        {
            return new VoiceMatch(VoiceVerdict.Assign, best.Print.ContactId, best.Name, best.Score, margin);
        }

        if (best.Score >= SuggestScore)
            return new VoiceMatch(VoiceVerdict.Suggest, best.Print.ContactId, best.Name, best.Score, margin);

        return VoiceMatch.None;
    }

    /// <summary>
    /// One voice from several recordings of the same person, leaving out the ones that disagree.
    ///
    /// Returns the recordings that were used and the ones that were not. The second list is not a
    /// diagnostic to be discarded: a recording whose voice does not match the rest of the person
    /// it is filed under is, far more often than not, filed under the wrong person — and telling
    /// the user which ones those are is the most useful thing this whole feature does on day one.
    /// </summary>
    public static (float[] Vector, IReadOnlyList<long> Used, IReadOnlyList<long> Rejected) Enrol(
        IReadOnlyList<(long CallId, float[] Vector)> recordings)
    {
        if (recordings.Count == 0) return ([], [], []);
        if (recordings.Count == 1) return (Normalise(recordings[0].Vector), [recordings[0].CallId], []);

        // How much each recording looks like the others. With two recordings this is simply how
        // much they look like each other, and if they disagree neither can be trusted over the
        // other — so both are rejected rather than one being picked arbitrarily.
        var agreement = recordings
            .Select((r, i) => (
                r.CallId,
                r.Vector,
                Mean: recordings
                    .Where((_, j) => j != i)
                    .Average(o => Voiceprint.Similarity(r.Vector, o.Vector))))
            .ToList();

        var used = agreement.Where(a => a.Mean >= ConsistencyFloor).ToList();
        var rejected = agreement.Where(a => a.Mean < ConsistencyFloor).Select(a => a.CallId).ToList();

        if (used.Count == 0) return ([], [], rejected);

        return (Average(used.Select(u => u.Vector)), [.. used.Select(u => u.CallId)], rejected);
    }

    private static float[] Average(IEnumerable<float[]> vectors)
    {
        var all = vectors.ToList();
        var sum = new float[all[0].Length];

        foreach (var vector in all)
            for (var i = 0; i < sum.Length; i++)
                sum[i] += vector[i];

        return Normalise(sum);
    }

    /// <summary>Unit length, so every comparison is a dot product and nothing else.</summary>
    private static float[] Normalise(float[] vector)
    {
        double squared = 0;
        foreach (var value in vector) squared += value * value;

        var length = Math.Sqrt(squared);
        if (length <= 0) return vector;

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / length);

        return result;
    }
}
