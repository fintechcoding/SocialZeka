using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Services;

/// <summary>Which ruling was just made on a ledger row.</summary>
public enum LedgerVerb
{
    Dismiss,
    DismissMany,
    Restore,
    Fulfil,
    Reopen,
    Abandon,
    Postpone,
    Reword,
    Edit,
}

/// <summary>
/// What one verb did, and how to take it back.
///
/// Every ruling on the ledger is a tombstone or a stamp, never a deletion, so every one of them
/// has an inverse. The verb returns it together with a sentence for the screen, and any surface
/// — the ledger page, a call window, a contact window — can show "Geri al" without knowing which
/// table the row lived in.
/// </summary>
/// <param name="Verb">What was done.</param>
/// <param name="Sentence">One localised line for the notice card: "Reddedildi: …".</param>
/// <param name="Undo">The inverse. Writes the repository and announces the change like the verb did.</param>
public sealed record PendingUndo(LedgerVerb Verb, string Sentence, Action Undo);

/// <summary>
/// The things that can be done to one ledger row, from wherever the row is shown.
///
/// The ledger page, the call window's Defter tab and the contact windows each showed the same
/// promises and findings, and each offered a different subset of verbs worded a different way —
/// one could dismiss, one could not, none could take a ruling back. Every verb lives here, calls
/// the repository once, and raises <see cref="Changed"/>, so every screen showing the row learns
/// of the ruling the way it learns of a deleted call (<see cref="CallActions"/>).
///
/// The repository is a parameter rather than <c>App.Repository</c> so a view model can be driven
/// by a test with its own database.
/// </summary>
public static class LedgerActions
{
    /// <summary>
    /// Raised after a ruling was made or taken back. Every list that shows the ledger re-reads
    /// on it, so no page has to know which other pages exist.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>Announces a change made outside this class.</summary>
    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);

    // ---- dismiss / restore -------------------------------------------------------------------

    /// <summary>Turns a promise down. The words stay in the transcript; the row becomes a tombstone.</summary>
    public static PendingUndo Dismiss(Repository repository, Commitment commitment)
    {
        repository.DismissCommitment(commitment.Id);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Dismiss,
            string.Format(Localisation.T("ledgeractions.reddedildi-n"), Label(commitment)),
            () =>
            {
                repository.RestoreCommitment(commitment.Id);
                NotifyChanged();
            });
    }

    /// <summary>Turns a finding down. Dismissed findings are not found again on the next run.</summary>
    public static PendingUndo Dismiss(Repository repository, Flag flag)
    {
        repository.DismissFlag(flag.Id);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Dismiss,
            string.Format(Localisation.T("ledgeractions.reddedildi-n"), Label(flag)),
            () =>
            {
                repository.RestoreFlag(flag.Id);
                NotifyChanged();
            });
    }

    /// <summary>Several at once — the ledger's select mode. One ruling, one undo.</summary>
    public static PendingUndo DismissMany(
        Repository repository, IReadOnlyCollection<long> commitmentIds, IReadOnlyCollection<long> flagIds)
    {
        var commitments = commitmentIds.Distinct().ToList();
        var flags = flagIds.Distinct().ToList();

        var count = repository.DismissCommitments(commitments) + repository.DismissFlags(flags);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.DismissMany,
            string.Format(Localisation.T("ledgeractions.n-kayit-reddedildi"), count),
            () =>
            {
                foreach (var id in commitments) repository.RestoreCommitment(id);
                foreach (var id in flags) repository.RestoreFlag(id);
                NotifyChanged();
            });
    }

    /// <summary>Brings a dismissed promise back to the ledger.</summary>
    public static PendingUndo Restore(Repository repository, Commitment commitment)
    {
        repository.RestoreCommitment(commitment.Id);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Restore,
            string.Format(Localisation.T("ledgeractions.geri-getirildi-n"), Label(commitment)),
            () =>
            {
                repository.DismissCommitment(commitment.Id);
                NotifyChanged();
            });
    }

    /// <summary>Brings a dismissed finding back to the ledger.</summary>
    public static PendingUndo Restore(Repository repository, Flag flag)
    {
        repository.RestoreFlag(flag.Id);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Restore,
            string.Format(Localisation.T("ledgeractions.geri-getirildi-n"), Label(flag)),
            () =>
            {
                repository.DismissFlag(flag.Id);
                NotifyChanged();
            });
    }

    // ---- kept / not kept ---------------------------------------------------------------------

    /// <summary>
    /// The user says the promise was kept. Only the user: the machine never concludes that from
    /// audio, so this is the one place "tutuldu" is written.
    /// </summary>
    public static PendingUndo Fulfil(Repository repository, Commitment commitment, long? byCallId = null)
    {
        repository.FulfilCommitment(commitment.Id, byCallId);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Fulfil,
            string.Format(Localisation.T("ledgeractions.tutuldu-n"), Label(commitment)),
            () => RevertStatus(repository, commitment));
    }

    /// <summary>Undoes "tutuldu" or "tutulmadı": the promise is open again.</summary>
    public static PendingUndo Reopen(Repository repository, Commitment commitment)
    {
        repository.ReopenCommitment(commitment.Id);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Reopen,
            string.Format(Localisation.T("ledgeractions.yeniden-acildi-n"), Label(commitment)),
            () => RevertStatus(repository, commitment));
    }

    /// <summary>The user says the promise was not kept. A silence is never read as this.</summary>
    public static PendingUndo Abandon(Repository repository, Commitment commitment)
    {
        repository.AbandonCommitment(commitment.Id);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Abandon,
            string.Format(Localisation.T("ledgeractions.tutulmadi-n"), Label(commitment)),
            () => RevertStatus(repository, commitment));
    }

    /// <summary>Puts the row back in the state the snapshot shows — the inverse of any status verb.</summary>
    private static void RevertStatus(Repository repository, Commitment before)
    {
        switch (before.Status)
        {
            case CommitmentStatus.Fulfilled:
                repository.FulfilCommitment(before.Id, before.FulfilledByCallId, before.FulfilledAt);
                break;

            case CommitmentStatus.Abandoned:
                repository.AbandonCommitment(before.Id);
                break;

            default:
                repository.ReopenCommitment(before.Id);
                break;
        }

        NotifyChanged();
    }

    // ---- the user's own columns --------------------------------------------------------------

    /// <summary>
    /// The user's own deadline — "Ertele". The spoken date stays where it was; this one wins
    /// wherever a date is shown or counted. Null takes the user's date away again.
    /// </summary>
    public static PendingUndo SetUserDeadline(Repository repository, Commitment commitment, DateOnly? deadline)
    {
        var before = commitment.UserDeadlineDate;

        repository.SetUserDeadline(commitment.Id, deadline);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Postpone,
            deadline is { } day
                ? string.Format(Localisation.T("ledgeractions.ertelendi-n"), Label(commitment), $"{day:d MMMM yyyy}")
                : string.Format(Localisation.T("ledgeractions.vade-kaldirildi-n"), Label(commitment)),
            () =>
            {
                repository.SetUserDeadline(commitment.Id, before);
                NotifyChanged();
            });
    }

    /// <summary>The user's rewording of the obligation. The quote is never touched. Null clears it.</summary>
    public static PendingUndo SetUserObligation(Repository repository, Commitment commitment, string? obligation)
    {
        var before = commitment.UserObligation;

        repository.SetUserObligation(commitment.Id, obligation);
        NotifyChanged();

        return new PendingUndo(
            LedgerVerb.Reword,
            string.IsNullOrWhiteSpace(obligation)
                ? string.Format(Localisation.T("ledgeractions.duzeltme-kaldirildi-n"), Label(commitment))
                : string.Format(Localisation.T("ledgeractions.duzenlendi-n"), Shorten(obligation.Trim())),
            () =>
            {
                repository.SetUserObligation(commitment.Id, before);
                NotifyChanged();
            });
    }

    /// <summary>
    /// Both of the user's columns at once — what the edit dialog saves. One ruling, one undo,
    /// rather than two notices for one click of Kaydet.
    /// </summary>
    public static PendingUndo Edit(Repository repository, Commitment commitment, string? obligation, DateOnly? deadline)
    {
        var wordingBefore = commitment.UserObligation;
        var deadlineBefore = commitment.UserDeadlineDate;

        repository.SetUserObligation(commitment.Id, obligation);
        repository.SetUserDeadline(commitment.Id, deadline);
        NotifyChanged();

        var cleared = string.IsNullOrWhiteSpace(obligation) && deadline is null;

        return new PendingUndo(
            LedgerVerb.Edit,
            cleared
                ? string.Format(Localisation.T("ledgeractions.duzeltme-kaldirildi-n"), Label(commitment))
                : string.Format(Localisation.T("ledgeractions.duzenlendi-n"),
                    Shorten(string.IsNullOrWhiteSpace(obligation) ? commitment.Obligation : obligation.Trim())),
            () =>
            {
                repository.SetUserObligation(commitment.Id, wordingBefore);
                repository.SetUserDeadline(commitment.Id, deadlineBefore);
                NotifyChanged();
            });
    }

    // ---- wording -----------------------------------------------------------------------------

    private static string Label(Commitment commitment) => Shorten(commitment.EffectiveObligation);

    private static string Label(Flag flag) => Shorten(flag.Summary);

    private static string Shorten(string text)
    {
        var flat = text.Trim();
        return flat.Length <= 46 ? flat : flat[..45].TrimEnd() + "…";
    }
}
