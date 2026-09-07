using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VoiceTranscript.Core.Domain;
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

/// <summary>
/// The columns a person-card leftover can be about.
///
/// <c>import_leftover.field</c> holds a database column name for <see cref="LeftoverKinds.Person"/>
/// — the merge reads the shared columns of <c>contact_profile</c> at run time, so that nothing has
/// to be remembered when a schema step adds one. The three this build knows how to write back are
/// named here, once, because both the screen (which word goes on the row) and the resolution
/// (which writer to call) have to agree about them.
/// </summary>
public static class LeftoverFields
{
    /// <summary>The stored file name of the person's photo.</summary>
    public const string Photo = "photo_file";

    public const string BirthDate = "birth_date";

    /// <summary>What the user wrote about the person, on <c>contact.notes</c>.</summary>
    public const string PersonNote = "notes";
}

/// <summary>
/// The three kinds of decision whose stored value is a DESCRIPTION rather than the thing itself,
/// written and read back in one place.
///
/// A note is its own text and a tag is its own word, so the leftover row holds them verbatim. A
/// promise ruling is four columns, a suggestion ruling is two, and a board card is three; there
/// is one column to put them in, so the merge writes them as one short line. That line is the one
/// the user reads on the leftover row and the one a resolution has to turn back into columns
/// before it can be applied — which makes the format a contract with two ends, and a contract
/// with two ends written in two files drifts.
///
/// So both ends live here. <see cref="DecisionMerge"/> composes with these and the screen takes
/// them apart with these, and a round-trip test holds them to each other. The wording is
/// unchanged from what the merge already wrote: the text goes into the fingerprint, so a new
/// spelling would ask every question a second time on archives that have already answered.
/// </summary>
public static class LeftoverValue
{
    /// <summary>Between the parts. A middle dot with spaces, as the rest of the product writes lists.</summary>
    private const string Between = " · ";

    /// <summary>What a field holds when the other machine left it empty.</summary>
    private const string Nothing = "-";

    // ---- a promise ruling ---------------------------------------------------

    /// <summary>
    /// A ruling reduced to what it says, with the stamps left out.
    ///
    /// Two machines that agree the promise was kept will not agree on the millisecond the button
    /// was pressed, and comparing the stamps would make every agreement look like a conflict and
    /// fill the user's list with questions that have one answer.
    /// </summary>
    public static string Promise(long status, long dismissed, string? deadline, string? obligation) =>
        $"durum={status}"
        + $"{Between}susturuldu={dismissed}"
        + $"{Between}tarih={deadline ?? Nothing}"
        + $"{Between}söz={obligation ?? Nothing}";

    /// <summary>The four parts of a promise ruling, as they were before <see cref="Promise"/>.</summary>
    /// <param name="Status">One of <see cref="CommitmentStatus"/>.</param>
    /// <param name="Dismissed">Whether the user silenced it.</param>
    /// <param name="Deadline">The date the user typed, or null.</param>
    /// <param name="Obligation">The wording the user corrected to, or null.</param>
    public readonly record struct PromiseRuling(
        CommitmentStatus Status, bool Dismissed, DateOnly? Deadline, string? Obligation);

    // The obligation is last and takes everything after "söz=", so a sentence containing the
    // separator cannot split a ruling into the wrong number of parts. The date before it is
    // non-greedy for the same reason from the other side.
    private static readonly Regex PromisePattern = new(
        @"^durum=(-?\d+) · susturuldu=(-?\d+) · tarih=(.*?) · söz=(.*)$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Reads one back, or null when the text is not a ruling this build wrote.</summary>
    public static PromiseRuling? ReadPromise(string? text)
    {
        if (text is null) return null;

        var match = PromisePattern.Match(text);
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var status)) return null;
        if (!Enum.IsDefined((CommitmentStatus)status)) return null;

        return new PromiseRuling(
            (CommitmentStatus)status,
            match.Groups[2].Value != "0",
            Day(match.Groups[3].Value),
            Words(match.Groups[4].Value));
    }

    // ---- a suggestion ruling ------------------------------------------------

    /// <summary>What was decided about a suggested next move, and where it was sent.</summary>
    public static string Suggestion(long status, string? routedNote) =>
        $"durum={status}" + (routedNote is null ? "" : $"{Between}{routedNote}");

    /// <param name="Status">One of <see cref="ActionStatus"/>.</param>
    /// <param name="RoutedNote">Where the user sent it, or null.</param>
    public readonly record struct SuggestionRuling(ActionStatus Status, string? RoutedNote);

    private static readonly Regex SuggestionPattern = new(
        @"^durum=(-?\d+)(?: · (.*))?$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Reads one back, or null when the text is not a ruling this build wrote.</summary>
    public static SuggestionRuling? ReadSuggestion(string? text)
    {
        if (text is null) return null;

        var match = SuggestionPattern.Match(text);
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var status)) return null;
        if (!Enum.IsDefined((ActionStatus)status)) return null;

        return new SuggestionRuling(
            (ActionStatus)status,
            match.Groups[2].Success ? Words(match.Groups[2].Value) : null);
    }

    // ---- a board card -------------------------------------------------------

    /// <summary>Where the conversation was filed, what it was called there, and when it comes back.</summary>
    public static string Board(string lane, string? title, string? remindOn) =>
        lane + (title is null ? "" : Between + title) + (remindOn is null ? "" : Between + remindOn);

    /// <param name="Lane">One of <see cref="BoardLane"/>.</param>
    public readonly record struct BoardCardValue(string Lane, string? Title, DateOnly? RemindOn);

    /// <summary>
    /// Reads one back, or null when the text is not a card this build wrote.
    ///
    /// The lane is first and is one of four known words, so it is read by name rather than by
    /// position — a text whose first part is not a lane is not a card and is refused rather than
    /// guessed at. The reminder is last and is a sortable date, which is what tells it apart from
    /// a title: the two middle possibilities are otherwise indistinguishable, and a title read as
    /// a date would put a reminder on a day nobody chose.
    /// </summary>
    public static BoardCardValue? ReadBoard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var parts = text.Split(Between);
        if (!BoardLane.IsKnown(parts[0])) return null;

        var rest = parts.Skip(1).ToList();
        DateOnly? remindOn = null;

        if (rest.Count > 0 && Day(rest[^1]) is { } day)
        {
            remindOn = day;
            rest.RemoveAt(rest.Count - 1);
        }

        // Rejoined rather than taken as one part: a title the user wrote with a middle dot in it
        // is still one title, and splitting it would rename their card.
        return new BoardCardValue(
            parts[0], rest.Count == 0 ? null : Words(string.Join(Between, rest)), remindOn);
    }

    // ---- the two shapes every part is written in ----------------------------

    private static string? Words(string value) =>
        value is Nothing || string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A stored day, or null — for "-", for a blank, and for anything not a day.</summary>
    private static DateOnly? Day(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day)
            ? day
            : null;
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
