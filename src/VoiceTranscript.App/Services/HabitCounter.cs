using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Counts one conversation's speaking habits and files the result.
///
/// A class of its own rather than a private method on the orchestrator because three callers
/// need exactly the same arithmetic: the transcription tail, the "count the archive" button, and
/// a test that has to be able to assert the stored figures without driving a recording. The
/// orchestrator is not constructible in a unit test — it opens capture devices and a worker — so
/// putting the rule here is what makes the rule checkable at all.
///
/// Nothing in here can be expensive: it is a pass over rows already in the database, no model,
/// no audio, no network. That is the whole reason the feature is on by default, and it is also
/// why every caller may treat a failure as nothing more than a missing count.
/// </summary>
public static class HabitCounter
{
    /// <summary>
    /// Counts a call unless the stored figures are already the right ones.
    ///
    /// "Already right" means two things at once: counted from the transcript the call shows now,
    /// and counted with the dictionary as it stands now. Either changing invalidates the row —
    /// a re-transcription because the words are different, an edited dictionary because the rule
    /// is. Both are cheap to detect and the recount is cheap to run, so neither is deferred.
    /// </summary>
    /// <param name="force">Count even when the stored row looks current. Used by nothing but tests today.</param>
    /// <returns>True when a row was written.</returns>
    public static bool CountIfStale(Repository repository, long callId, bool force = false)
    {
        var lexicon = HabitLexicon.Load(repository);

        if (!force && !IsStale(repository, callId, lexicon.LexiconVersion)) return false;

        return Count(repository, callId, lexicon);
    }

    /// <summary>
    /// Whether a call's stored counts are missing or were made against a different transcript or
    /// dictionary. The backfill's filter, and the guard in <see cref="CountIfStale"/>.
    /// </summary>
    public static bool IsStale(Repository repository, long callId, int lexiconVersion)
    {
        if (repository.GetHabits(callId) is not { } stored) return true;
        if (stored.LexiconVersion != lexiconVersion) return true;

        // Null on either side means "not recorded", which is not evidence of staleness: an
        // archive transcribed before versions were written would otherwise be recounted for ever.
        var current = repository.CurrentTranscriptVersion(callId)?.Id;

        return stored.TranscriptVersionId is { } counted && current is { } shown && counted != shown;
    }

    /// <summary>
    /// Counts unconditionally and writes the snapshot.
    ///
    /// The word-confidence threshold is null on purpose and will stay null until one is measured
    /// per engine. Null means every hit is counted as certain-or-uncertain by the report's own
    /// rule rather than being filtered by a number nobody has established — a made-up threshold
    /// would silently drop real hits and there would be no way to tell.
    /// </summary>
    public static bool Count(Repository repository, long callId, HabitLexicon lexicon)
    {
        var segments = repository.GetSegments(callId);
        if (segments.Count == 0) return false;

        var report = SpeechHabits.Count(segments, lexicon, wordThreshold: null, repository.Verdicts(callId));
        var talk = TalkStats.Compute(segments);

        repository.SaveHabits(callId, lexicon.LexiconVersion, new HabitSnapshot(report, talk).ToJson());

        return true;
    }

    /// <summary>
    /// Every call that has a transcript and no current count — what the archive sweep works
    /// through, newest first.
    ///
    /// Newest first because that is the order the numbers become useful in: somebody who presses
    /// the button wants this month's curve before last year's, and a sweep interrupted halfway
    /// has then done the half that is looked at.
    ///
    /// Recordings that were never transcribed are left off rather than visited and skipped. It is
    /// not about the seconds: a list that kept them would never empty, so the button would answer
    /// "N görüşme sayıldı" for ever on an archive that is entirely up to date.
    /// </summary>
    public static IReadOnlyList<long> NeedingCount(Repository repository, int lexiconVersion) =>
        [.. repository.ListCalls(limit: int.MaxValue)
            .Where(c => c.State is ProcessingState.Transcribed or ProcessingState.Analysing
                            or ProcessingState.Analysed or ProcessingState.Failed)
            .Where(c => IsStale(repository, c.Id, lexiconVersion))
            .Select(c => c.Id)];
}
