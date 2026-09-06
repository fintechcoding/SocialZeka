using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Counts one conversation again and files the result.
///
/// Here rather than in either screen because two of them need it and the rule must not exist
/// twice: the call window's "Yeniden say" and the mirror page's verdict buttons are the same
/// act. A verdict is applied by recounting rather than by filtering the stored report, so the
/// figure on a card and the bucket on a moment always come from <see cref="SpeechHabits.Count"/>
/// and never from a screen's own idea of what a ruling means.
///
/// The dictionary is read fresh every time, so a word the user added or excluded takes effect on
/// the next recount rather than at the next re-transcription.
/// </summary>
public static class HabitRecount
{
    /// <summary>Recounts and saves, returning what was stored. The user's verdicts are read at the same moment.</summary>
    public static HabitSnapshot Run(Repository repository, long callId)
    {
        var segments = repository.GetSegments(callId);
        var lexicon = HabitLexicon.Load(repository);

        // No word threshold: none is measured yet, so only the line's own low-confidence mark
        // decides the uncertain bucket. Passing a made-up number would put the engines' word
        // probabilities on a scale nobody calibrated.
        var report = SpeechHabits.Count(segments, lexicon, wordThreshold: null, repository.Verdicts(callId));
        var snapshot = new HabitSnapshot(report, TalkStats.Compute(segments));

        repository.SaveHabits(callId, lexicon.LexiconVersion, snapshot.ToJson());

        return snapshot;
    }
}
