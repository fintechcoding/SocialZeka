using System.Text;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Storage;

/// <summary>
/// One decision an import could not carry, kept until the user answers it.
///
/// Two shapes, told apart by <paramref name="Mine"/>:
///
///   * <paramref name="Mine"/> is not null — a genuine disagreement. Both machines wrote
///     something on the same thing and the two differ. The local value stands, always, and this
///     row is the incoming one waiting for a ruling.
///   * <paramref name="Mine"/> is null — a decision with nowhere to land. Most often the
///     conversation was transcribed again on one of the two machines, so the quote the ruling was
///     anchored to no longer matches anything here (§7.4 names this as the weakest joint in the
///     whole design). Nothing was displaced and nothing was overwritten; the ruling simply could
///     not be placed, and saying so is the only honest alternative to dropping it.
///
/// A row is CLOSED by a resolution, never deleted.
/// </summary>
public sealed record ImportLeftover(
    long Id,
    string Fingerprint,
    string? SourceArchiveId,
    string Kind,
    long? CallId,
    long? ContactId,
    string Field,
    string? Mine,
    string? Theirs,
    string? Quote,
    DateTimeOffset NoticedAt,
    string? Resolution,
    DateTimeOffset? ResolvedAt)
{
    public bool IsOpen => Resolution is null;

    /// <summary>True when there was nothing here to lose — the decision had nowhere to land.</summary>
    public bool HadNowhereToLand => Mine is null;
}

/// <summary>
/// What kind of thing the user decided. Stored as these tokens rather than as sentences, so the
/// screen owns the wording and a translation never has to migrate the database.
/// </summary>
public static class LeftoverKinds
{
    /// <summary>A ruling on a promise: kept, dismissed, reopened, postponed, reworded.</summary>
    public const string Promise = "soz";

    /// <summary>What the user wrote about the conversation.</summary>
    public const string Note = "not";

    /// <summary>A label the user put on the conversation.</summary>
    public const string Tag = "etiket";

    /// <summary>The ear verdict: they listened and said whether the machine heard right.</summary>
    public const string Verdict = "kulak";

    /// <summary>A ruling on a suggested next move: done, hidden, routed.</summary>
    public const string Suggestion = "oneri";

    /// <summary>Where the conversation was filed on the board.</summary>
    public const string Board = "pano";

    /// <summary>Something on a person's card: their birthday, their photo, the note about them.</summary>
    public const string Person = "kisi";
}

/// <summary>How the user answered a leftover. Written into <c>import_leftover.resolution</c>.</summary>
public static class LeftoverResolutions
{
    /// <summary>Keep what is here. The incoming value is discarded — by the user, knowingly.</summary>
    public const string Mine = "burada";

    /// <summary>Take the other machine's value.</summary>
    public const string Theirs = "oteki";

    /// <summary>Keep both, however the screen chooses to do that for this kind.</summary>
    public const string Both = "ikisi";

    public static bool IsKnown(string? value) =>
        value is Mine or Theirs or Both;
}

/// <summary>
/// The arithmetic that makes "nothing was dropped in silence" checkable rather than believed.
///
/// A merge leaves a conversation that exists on both machines completely alone — that is the
/// rule, and it is the right one for a transcript. It is the wrong one for the rulings ON that
/// conversation, which is why they are considered one at a time. Every one of them lands in
/// exactly one of three places:
///
///   <paramref name="Carried"/> — written here, because there was nothing here to displace.
///   <paramref name="AlreadySame"/> — both machines already say the same thing. Nothing to do.
///   <paramref name="Left"/> — a row in import_leftover, waiting for the user.
///
/// <paramref name="Seen"/> is how many were considered, and <see cref="Balances"/> is the
/// invariant §7.3 calls a hard one: seen equals carried plus same plus left. There is no fourth
/// destination, and in particular there is no "dropped". A merge that cannot make this sum is
/// refused rather than committed.
/// </summary>
public sealed record DecisionCounts(int Seen, int Carried, int AlreadySame, int Left)
{
    public static readonly DecisionCounts None = new(0, 0, 0, 0);

    public bool Balances => Seen == Carried + AlreadySame + Left;

    public static DecisionCounts operator +(DecisionCounts a, DecisionCounts b) => new(
        a.Seen + b.Seen,
        a.Carried + b.Carried,
        a.AlreadySame + b.AlreadySame,
        a.Left + b.Left);
}

/// <summary>
/// The identity of a disagreement, built so that meeting it twice is not two questions.
///
/// Row ids are useless here: the same promise has a different id on each machine, and a second
/// import of the same file would produce a second set of ids again. So the fingerprint is made
/// out of things both machines agree on and neither renumbers — the conversation's instant, the
/// folded words a ruling hangs on, the folded name of a person — plus the incoming value itself.
///
/// Including the incoming VALUE is what makes the row a question rather than a topic. Import the
/// same file again and the fingerprint repeats, so the UNIQUE index quietly drops it and the user
/// is not asked a second time — including when they have already answered, which is the whole
/// reason the answered rows are kept. Import a file where the other machine has since changed its
/// mind, and the value differs, so it is a new question and gets asked.
///
/// The separator is a unit separator rather than anything typeable, so a value that happens to
/// contain the delimiter cannot forge a different fingerprint.
/// </summary>
public static class LeftoverFingerprint
{
    private const char Separator = (char)0x1F;

    public static string Of(string kind, string anchor, string field, string? theirs)
    {
        var text = new StringBuilder()
            .Append(kind).Append(Separator)
            .Append(anchor).Append(Separator)
            .Append(field).Append(Separator)
            .Append(TurkishText.NormalizeForSearch(theirs))
            .ToString();

        // Hashed rather than stored whole: an anchor carries a verbatim quote, and a quote is a
        // sentence somebody said. The unique index needs an identity, not the words — so the words
        // that end up in the column are only the ones the screen has to show back (mine, theirs,
        // quote), and the key itself says nothing about anybody.
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>The anchor for something hanging off one conversation: its instant and app.</summary>
    public static string CallAnchor(DateTimeOffset startedAt, long app, string? within = null) =>
        $"{startedAt.UtcDateTime:O}{Separator}{app}"
        + (within is null ? "" : $"{Separator}{TurkishText.NormalizeForSearch(within)}");

    /// <summary>The anchor for something on a person's card: their folded name and app.</summary>
    public static string ContactAnchor(string nameNormalised, long app) =>
        $"{nameNormalised}{Separator}{app}";
}
