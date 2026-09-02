namespace VoiceTranscript.App.Services;

/// <summary>
/// Whether a sentence has just been said, so it is not said again.
///
/// A backlog of twenty recordings against a service that is down produces one identical pair per
/// recording — "…yükleniyor", "…başarısız" — about a minute apart, and the screen becomes a column
/// of the same two sentences with a toast for each. The rows on the processing page already carry
/// the per-recording detail; a toast is meant to tell somebody something they do not already know.
///
/// Two decisions worth keeping:
///
///   <b>The pair alternates.</b> "Skip it if it equals the previous one" is the obvious rule and
///   it catches none of this, because no two consecutive notices are ever equal. What has to be
///   remembered is when each distinct sentence was last said.
///
///   <b>Keyed on the exact text.</b> Two recordings failing for the same reason is one thing to
///   know once; anything phrased differently is, by definition, new information and still arrives.
///   That also means the window can be short: it exists to collapse a burst, not to keep a session
///   quiet, and the warning about audio leaving the machine must never be silenced for a whole one.
///
/// Pure and clock-injected so the burst can be tested without waiting five minutes for it.
/// </summary>
public sealed class NoticeRepeatGuard(TimeSpan? window = null)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly Dictionary<string, DateTimeOffset> _lastSaid = new(StringComparer.Ordinal);

    /// <summary>True when this sentence should be raised; false when it was just raised.</summary>
    public bool ShouldSay(string message, DateTimeOffset now)
    {
        if (_lastSaid.TryGetValue(message, out var last) && now - last < _window)
            return false;

        // Bounded. The key set is message texts, and a long session with many distinct failures
        // would otherwise grow without limit.
        if (_lastSaid.Count > 200)
        {
            foreach (var stale in _lastSaid.Where(e => now - e.Value >= _window).Select(e => e.Key).ToList())
                _lastSaid.Remove(stale);
        }

        _lastSaid[message] = now;
        return true;
    }
}
