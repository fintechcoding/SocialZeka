namespace VoiceTranscript.Tests;

/// <summary>
/// The tests that listen to the application's change broadcast, one at a time.
///
/// <c>LedgerActions.Changed</c> and <c>CallActions.Changed</c> are static events: a ruling made
/// anywhere is announced to every screen in the process. That is right for the application —
/// no page has to know which other pages exist — and it is a trap for a parallel test run,
/// because a class that subscribes to count "how many times did my page re-read itself" also
/// counts every ruling that some other test class happened to make on its own database at the
/// same moment. The failure is intermittent, lands on a test that did nothing wrong, and reads
/// as "one click refreshed the ledger four times".
///
/// So the handful of classes that raise or listen to that broadcast share one collection and
/// the collection does not run beside anything else. It costs a second of wall clock; the
/// alternative is a suite that is green four runs out of five.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChangeBroadcastCollection
{
    public const string Name = "Değişiklik yayını";
}
