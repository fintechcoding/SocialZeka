namespace VoiceTranscript.Core.Text;

/// <summary>
/// One clock, for every moment and every length this product shows a person.
///
/// A quote's timestamp is not decoration: it is the address at which the user goes to hear the
/// sentence for themselves, and it is the whole reason a quote counts as evidence rather than as
/// a claim. So it has to agree with the player's own readout, and it has to be the same on the
/// ledger, on Sözler, on Aynam and in an exported clip — because a user who finds two different
/// times for one sentence has no way to tell which screen is wrong.
///
/// Before this there were a dozen hand-rolled versions and two shapes of them. One computed the
/// TOTAL minutes ("90:00"), which is unambiguous but does not match the scrubber and stops being
/// readable past an hour. The other formatted a TimeSpan as "mm\:ss", which silently DROPS the
/// hour: a sentence spoken at 1:05:00 was labelled 05:00, an hour away from where it actually
/// is. On the calls this product exists for — the long ones — that is a false timestamp printed
/// under a verbatim quote, which is the one lie it must never tell.
///
/// So: minutes and seconds under an hour, hours and minutes and seconds at or past it. Under an
/// hour, where almost every row lives, this is character-for-character what the old total-minute
/// spelling produced, and it is what the player has always shown.
/// </summary>
public static class Timestamps
{
    /// <summary>The moment a quote was said, inside its recording: "07:31", or "1:05:00".</summary>
    public static string Clip(int milliseconds) => Clip(TimeSpan.FromMilliseconds(milliseconds));

    /// <inheritdoc cref="Clip(int)"/>
    public static string Clip(long milliseconds) => Clip(TimeSpan.FromMilliseconds(milliseconds));

    /// <inheritdoc cref="Clip(int)"/>
    public static string Clip(TimeSpan at)
    {
        // Negative would render as "00:-1". A position before the start of a recording is a
        // fault upstream, and the honest thing on screen is the beginning, not punctuation.
        if (at < TimeSpan.Zero) at = TimeSpan.Zero;

        return at.TotalHours >= 1
            ? $"{(int)at.TotalHours}:{at.Minutes:00}:{at.Seconds:00}"
            : $"{at.Minutes:00}:{at.Seconds:00}";
    }

    /// <summary>The same moment, from a whole-second count — what the transcript stamps carry.</summary>
    public static string ClipFromSeconds(int seconds) => Clip(TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// How long something ran: "4:12", or "1:04:12".
    ///
    /// A length, not a position, so the leading zero goes: "4:12" is how a call log has always
    /// written four minutes, and padding it to "04:12" would make a duration look like an
    /// address into a recording.
    /// </summary>
    public static string Length(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
