using System.Net.Http;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Keeps one person's reading level with their history, after a conversation with them.
///
/// A class of its own for the same reason as <see cref="HabitCounter"/> and
/// <see cref="ProsodyMeasurer"/>: the orchestrator is not constructible in a test — it opens
/// capture devices — so a rule written inside it is a rule nothing can check. The decision of
/// when a reading is worth paying for lives here, with its test beside it.
///
/// Unlike the habit count this one costs money: it is a request to whichever model the user
/// configured, over a packet of that person's ledger and transcript. So the two cheap refusals
/// come first and neither of them sends anything:
///
///   * the setting is off, or the call has nobody attached to it;
///   * the stored reading was made from exactly today's history, so there is nothing new to read.
///
/// A third refusal lives inside <see cref="ContactReadingAnalysis"/> and is made before a request
/// too: fewer than three conversations or fewer than twenty anchors and it answers "yetersiz"
/// rather than reading a person out of two sentences.
/// </summary>
public static class ContactReadingRefresher
{
    /// <summary>
    /// Refreshes this contact's reading if the archive has moved since the stored one.
    ///
    /// Returns true when a reading was written. A failure is a missing reading, never a failed
    /// conversation: the transcript and the ledger are already stored by the time this runs.
    /// </summary>
    public static async Task<bool> RefreshIfStaleAsync(
        Repository repository,
        HttpClient http,
        long callId,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.ContactReadingEnabled || settings.ContactReadingMeasuredNegative) return false;
        if (!settings.LlmReachableInPrinciple) return false;

        var call = repository.GetCall(callId);
        if (call?.ContactId is not { } contactId) return false;

        if (!IsStale(repository, contactId)) return false;

        var client = LlmClientFactory.Create(
            http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

        var report = await new ContactReadingAnalysis(client, repository).RunAsync(
            contactId,
            settings.ResolvedConsistencyModel,
            settings.PreferredName,
            settings.Provider.SendsDataOffMachine,
            cancellationToken);

        // "Too little on record" is an answer, not a failure, and it is not worth storing: the
        // panel already says so on its own, and a stored empty reading would look like one that
        // had been made and come back blank.
        return report.Ok && !report.Insufficient;
    }

    /// <summary>
    /// Whether the stored reading still describes today's history.
    ///
    /// The same fingerprint the card's own "bayatladı" line uses — call ids and transcript
    /// version ids — so the automatic refresh and the manual one agree about what "old" means.
    /// No stored reading at all counts as stale: the first conversation that reaches three is
    /// the one that earns the first reading.
    /// </summary>
    public static bool IsStale(Repository repository, long contactId)
    {
        var versions = repository.TranscriptVersionsOf(contactId);

        // Group calls are excluded on both sides, the way the card excludes them: the far channel
        // of a group call carries several voices, so nothing on it belongs to one person.
        var group = repository
            .ListCalls(contactId, limit: int.MaxValue)
            .Where(c => c.Kind == CallKind.Group)
            .Select(c => c.Id)
            .ToHashSet();

        var current = ContactReadingAnalysis.InputHash(
            repository.ContactSeries(contactId)
                .Where(p => !group.Contains(p.CallId))
                .Select(p => (p.CallId, versions.GetValueOrDefault(p.CallId))));

        var stored = repository.LatestContactReading(contactId);

        return stored is null || !ContactReadingAnalysis.StillCurrent(stored.InputHash, current);
    }
}
