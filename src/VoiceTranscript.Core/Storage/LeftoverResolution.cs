using System.Globalization;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Storage;

/// <summary>
/// Why one of the three answers is not on offer for a particular row.
///
/// A token rather than a sentence: the words are the screen's and belong in both dictionaries,
/// and the reason has to be SAID rather than expressed by a missing button. A row with two
/// buttons where its neighbour has three, and nothing explaining the difference, teaches the user
/// that the product is unreliable — which is worse than the honest sentence.
/// </summary>
public enum LeftoverRefusal
{
    /// <summary>The answer is offered.</summary>
    None,

    /// <summary>There is nothing here to write the other machine's value into.</summary>
    NothingHere,

    /// <summary>More than one thing here carries these words, and picking one would be a guess.</summary>
    NotUnique,

    /// <summary>The stored value is not in a shape this build can turn back into columns.</summary>
    Unreadable,

    /// <summary>Keeping both would be the same act as taking theirs.</summary>
    OneOrTheOther,

    /// <summary>This is a thing that cannot be two.</summary>
    SingleValued,
}

/// <summary>Which of the three answers a row can actually carry out.</summary>
/// <param name="Theirs">Why "take theirs" is not offered, or <see cref="LeftoverRefusal.None"/>.</param>
/// <param name="Both">Why "keep both" is not offered, or <see cref="LeftoverRefusal.None"/>.</param>
public readonly record struct LeftoverChoices(LeftoverRefusal Theirs, LeftoverRefusal Both)
{
    public bool CanTakeTheirs => Theirs == LeftoverRefusal.None;
    public bool CanKeepBoth => Both == LeftoverRefusal.None;
}

/// <summary>
/// Carrying out the answer the user gave on a leftover row.
///
/// <see cref="Repository.ResolveLeftover"/> only closes the row, and says why: recording a ruling
/// and acting on it are separate so that a row can never end up marked answered because something
/// failed halfway through. This is the other half — the half that knows what each of the three
/// answers MEANS, which is a different thing for each kind of decision:
///
///   * A note is text, so "ikisi de tut" is two paragraphs, mine first, theirs after.
///   * A tag is present or absent. Keeping both is the same act as taking theirs, so the third
///     button is not drawn — a button whose two neighbours already cover it is a button that
///     teaches people to distrust the other two.
///   * A promise, a suggestion, a board card, an ear verdict and a birthday cannot be two. A
///     promise both kept and abandoned is not a state; a card in two lanes is not a board.
///     Each of those says so on the row instead of offering a third button that would have to
///     pick one silently.
///
/// AND WHAT CANNOT BE WRITTEN IS NOT OFFERED. The leftover row records enough to SHOW a decision,
/// which is not always enough to REPLAY one: a promise ruling is stored as the sentence the user
/// reads, and the row it belonged to is identified by words that several promises in one
/// conversation can share. So every answer is checked against the archive before its button is
/// drawn, and where it cannot be carried out the row says which of the reasons above applies.
/// </summary>
public static class LeftoverResolution
{
    /// <summary>
    /// What this row can actually do, asked before the buttons are drawn.
    ///
    /// Reaches the database, because the honest answer depends on what is here right now — a
    /// promise deleted since the import has nothing to write into, and saying so is the point.
    /// </summary>
    public static LeftoverChoices Offer(Repository repository, ImportLeftover row) => new(
        Theirs: WhyNotTheirs(repository, row),
        Both: WhyNotBoth(row));

    private static LeftoverRefusal WhyNotTheirs(Repository repository, ImportLeftover row) => row.Kind switch
    {
        // Addressed by the conversation or the person alone, so there is always somewhere to put
        // it — including a place this machine left empty.
        LeftoverKinds.Note or LeftoverKinds.Tag => Addressed(row.CallId),
        LeftoverKinds.Person => Addressed(row.ContactId),

        LeftoverKinds.Board => row.CallId is null ? LeftoverRefusal.NothingHere
            : LeftoverValue.ReadBoard(row.Theirs) is null ? LeftoverRefusal.Unreadable
            : LeftoverRefusal.None,

        // The three that need a row of their own here: a ruling is written ONTO something, and
        // the leftover does not carry enough to create one from nothing.
        LeftoverKinds.Verdict => Single(Verdicts(repository, row).Count),
        LeftoverKinds.Promise => LeftoverValue.ReadPromise(row.Theirs) is not { } ruling
            ? LeftoverRefusal.Unreadable
            : Writable(ruling.Status) ? Single(Promises(repository, row).Count) : LeftoverRefusal.Unreadable,
        LeftoverKinds.Suggestion => LeftoverValue.ReadSuggestion(row.Theirs) is null
            ? LeftoverRefusal.Unreadable
            : Single(Suggestions(repository, row).Count),

        _ => LeftoverRefusal.Unreadable,
    };

    private static LeftoverRefusal WhyNotBoth(ImportLeftover row) => row.Kind switch
    {
        // Two paragraphs, one after the other. The only shape where "both" is a real third answer
        // rather than a disguised one of the other two.
        LeftoverKinds.Note => Text(row),
        LeftoverKinds.Person when row.Field == LeftoverFields.PersonNote => Text(row),

        LeftoverKinds.Tag => LeftoverRefusal.OneOrTheOther,
        _ => LeftoverRefusal.SingleValued,
    };

    /// <summary>Keeping both needs two things to keep. With one side blank it is "take theirs".</summary>
    private static LeftoverRefusal Text(ImportLeftover row) =>
        string.IsNullOrWhiteSpace(row.Mine) ? LeftoverRefusal.OneOrTheOther : LeftoverRefusal.None;

    private static LeftoverRefusal Addressed(long? id) =>
        id is null ? LeftoverRefusal.NothingHere : LeftoverRefusal.None;

    private static LeftoverRefusal Single(int matches) => matches switch
    {
        0 => LeftoverRefusal.NothingHere,
        1 => LeftoverRefusal.None,
        _ => LeftoverRefusal.NotUnique,
    };

    /// <summary>
    /// Whether a promise status is one this application has a way of writing.
    ///
    /// <see cref="CommitmentStatus.Renegotiated"/> is defined and nothing in the product sets it,
    /// so there is no writer for it and inventing one here would be this screen deciding what a
    /// renegotiated promise is. Refused by name rather than by a silent failure.
    /// </summary>
    private static bool Writable(CommitmentStatus status) =>
        status is CommitmentStatus.Open or CommitmentStatus.Fulfilled or CommitmentStatus.Abandoned;

    // ---- carrying it out ----------------------------------------------------

    /// <summary>
    /// Applies one answer and closes the row, in that order.
    ///
    /// The order is the whole reason the two halves are separate. Writing first means a failure
    /// leaves the question open and askable again; closing first would mean a row marked answered
    /// with nothing behind it, which is the silent loss this package exists to end.
    ///
    /// <see cref="LeftoverResolutions.Mine"/> writes nothing at all — what is here is already what
    /// the user chose — and only closes the row.
    /// </summary>
    /// <returns>False when the answer is not one this row can carry out; nothing was written.</returns>
    public static bool Apply(Repository repository, ImportLeftover row, string resolution)
    {
        if (!LeftoverResolutions.IsKnown(resolution)) return false;

        var choices = Offer(repository, row);

        switch (resolution)
        {
            case LeftoverResolutions.Theirs when !choices.CanTakeTheirs:
            case LeftoverResolutions.Both when !choices.CanKeepBoth:
                return false;

            case LeftoverResolutions.Theirs:
                if (!Write(repository, row, row.Theirs)) return false;
                break;

            case LeftoverResolutions.Both:
                // A blank line between them, because two paragraphs run together read as one
                // paragraph somebody wrote badly rather than as two people writing.
                if (!Write(repository, row, $"{row.Mine}\n\n{row.Theirs}")) return false;
                break;
        }

        return repository.ResolveLeftover(row.Id, resolution);
    }

    /// <summary>
    /// Puts back what was here before <see cref="Apply"/> and asks the question again.
    ///
    /// The local value is on the row — that is what <c>mine</c> is — so undoing is the same
    /// writer pointed at the other side, and a row whose local side was empty is emptied again.
    /// The question reopens with it: an answer taken back is not an answer.
    /// </summary>
    public static bool Undo(Repository repository, ImportLeftover row)
    {
        if (!Write(repository, row, row.Mine)) return false;

        repository.ReopenLeftover(row.Id);
        return true;
    }

    /// <summary>
    /// Writes one value into the place this row is about. Null empties that place.
    ///
    /// Every branch goes through the repository's own writers rather than SQL of its own, so a
    /// resolution obeys the same rules as the user typing the value by hand — a cleared note is
    /// deleted rather than stored blank, a re-spelled tag is still one tag, a promise marked kept
    /// gets its stamp.
    /// </summary>
    private static bool Write(Repository repository, ImportLeftover row, string? value)
    {
        switch (row.Kind)
        {
            case LeftoverKinds.Note when row.CallId is { } note:
                repository.SaveNote(note, value);
                return true;

            case LeftoverKinds.Tag when row.CallId is { } tagged:
                // Removing rather than adding is what an undo of a tag means: the tag was not
                // here, so putting back "what was here" is taking it off again.
                if (value is null) repository.Untag(tagged, row.Theirs ?? "");
                else repository.Tag(tagged, value);
                return true;

            case LeftoverKinds.Person when row.ContactId is { } person:
                return WritePersonCard(repository, person, row.Field, value);

            case LeftoverKinds.Board when row.CallId is { } carded:
                return WriteBoardCard(repository, carded, value);

            case LeftoverKinds.Verdict when Verdicts(repository, row) is [{ } heard]:
                if (!int.TryParse(value, CultureInfo.InvariantCulture, out var verdict)) return false;

                // Written through the same key it was found by — the words and the millisecond —
                // so the existing row is replaced rather than a second opinion filed beside it.
                repository.SaveVerdict(heard with
                {
                    Value = (VerdictValue)verdict,
                    DecidedAt = DateTimeOffset.UtcNow,
                });

                return true;

            case LeftoverKinds.Promise when Promises(repository, row) is [{ } promise]:
                return WritePromiseRuling(repository, promise.Id, LeftoverValue.ReadPromise(value));

            case LeftoverKinds.Suggestion when Suggestions(repository, row) is [{ } suggestion]:
                if (LeftoverValue.ReadSuggestion(value) is not { } ruled) return false;

                repository.SetActionStatus(suggestion.Id, ruled.Status, ruled.RoutedNote);
                return true;

            default:
                return false;
        }
    }

    private static bool WritePersonCard(Repository repository, long contactId, string field, string? value)
    {
        switch (field)
        {
            case LeftoverFields.PersonNote:
                repository.SaveContactNote(contactId, value);
                return true;

            case LeftoverFields.Photo:
                // The file itself travels in the backup beside the database, so the name that
                // arrives points at a photo that is really here. It did not before this package,
                // and taking the other machine's name would have named a file on their disk.
                repository.SetContactPhoto(contactId, value);
                return true;

            case LeftoverFields.BirthDate when value is null:
                repository.SetBirthDate(contactId, null);
                return true;

            case LeftoverFields.BirthDate:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day))
                {
                    return false;
                }

                repository.SetBirthDate(contactId, day);
                return true;

            default:
                // A column a later schema step added, which this build has no writer for. Refused
                // rather than guessed at; the row stays open and still says both sides.
                return false;
        }
    }

    private static bool WriteBoardCard(Repository repository, long callId, string? value)
    {
        if (value is null)
        {
            repository.RemoveFromBoard(callId);
            return true;
        }

        if (LeftoverValue.ReadBoard(value) is not { } card) return false;

        repository.PutOnBoard(callId, card.Lane, card.Title, card.RemindOn);

        // PutOnBoard keeps a reminder it is not given, because moving a card between lanes must
        // not lose one. Here the value IS the whole card, so a side that has no reminder means
        // there is to be none.
        if (card.RemindOn is null) repository.RemindOn(callId, null);

        return true;
    }

    private static bool WritePromiseRuling(Repository repository, long commitmentId, LeftoverValue.PromiseRuling? ruling)
    {
        if (ruling is not { } r) return false;

        switch (r.Status)
        {
            case CommitmentStatus.Open: repository.ReopenCommitment(commitmentId); break;
            case CommitmentStatus.Fulfilled: repository.FulfilCommitment(commitmentId); break;
            case CommitmentStatus.Abandoned: repository.AbandonCommitment(commitmentId); break;
            default: return false;
        }

        if (r.Dismissed) repository.DismissCommitment(commitmentId);
        else repository.RestoreCommitment(commitmentId);

        // The user's own pen, carried across as the user's: their wording and their date are
        // written whether or not the other machine had any, so taking their ruling does not leave
        // this machine's correction attached to it.
        repository.SetUserDeadline(commitmentId, r.Deadline);
        repository.SetUserObligation(commitmentId, r.Obligation);

        return true;
    }

    // ---- finding what a row is about ----------------------------------------
    //
    // All three match on the folded words, because that is the only address the leftover carries.
    // A conversation where two rows share those words gives a list of two, and every caller reads
    // that as "not unique" rather than taking the first.

    private static IReadOnlyList<Verdict> Verdicts(Repository repository, ImportLeftover row) =>
        row.CallId is not { } call || row.Quote is null
            ? []
            : [.. repository.Verdicts(call, row.Field).Where(v => v.QuoteFolded == row.Quote)];

    private static IReadOnlyList<Commitment> Promises(Repository repository, ImportLeftover row) =>
        row.CallId is not { } call || row.Quote is null
            ? []
            : [.. repository.CommitmentsOnCall(call).Where(c => Same(c.Quote, row.Quote))];

    private static IReadOnlyList<ActionItem> Suggestions(Repository repository, ImportLeftover row) =>
        row.CallId is not { } call || row.Quote is null
            ? []
            : [.. repository.ActionsOf(call).Where(a => Same(a.Quote, row.Quote))];

    private static bool Same(string? here, string there) =>
        TurkishText.NormalizeForSearch(here) == TurkishText.NormalizeForSearch(there);
}
