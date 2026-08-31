using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Putting a conversation under the right person after it went under the wrong one.
///
/// This is not a tidy-up feature, it is a correctness one, and it exists because automatic
/// attribution cannot be made reliable. A window title is the only thing the messengers offer, and
/// a title is sometimes the person, sometimes the conversation that happened to be open, and
/// sometimes an unread counter. So calls will land under the wrong contact, and the product's
/// answer has to be that fixing it takes two clicks — not that the guess gets cleverer.
///
/// What makes this delicate is that a call is not one row. The commitments, claims and flags
/// extracted from it each carry their own contact, and moving the call alone would leave the
/// promise filed under one person and the conversation it was made in under another. Both
/// histories would then look complete while the comparisons this product exists to make — a price
/// that moved, a deadline that slipped — silently stopped working across the split.
/// </summary>
public sealed class ContactRepairTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-repair-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public ContactRepairTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private long Call(long? contactId, string startedAt = "2026-08-31T10:00:00+03:00")
        => _repo.InsertCall(new Call
        {
            ContactId = contactId,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Parse(startedAt),
            State = ProcessingState.Analysed,
        });

    private void AddCommitment(long callId, long? contactId, string obligation)
        => _repo.InsertCommitment(new Commitment
        {
            CallId = callId,
            ContactId = contactId,
            ByMe = false,
            Quote = "söz verdi",
            QuoteStartMs = 1000,
            Obligation = obligation,
            Status = CommitmentStatus.Open,
        });

    /// <summary>
    /// The reported case, end to end: a call with Serdal was filed under Uliana.
    /// </summary>
    [Fact]
    public void ACallCanBeMovedFromOneContactToAnother()
    {
        var uliana = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        var serdal = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        var call = Call(uliana);

        var takenFrom = _repo.AssignContact(call, serdal);

        Assert.Equal(uliana, takenFrom);
        Assert.Equal(serdal, _repo.GetCall(call)!.ContactId);
    }

    /// <summary>
    /// The counters on both contacts have to end up right, not just the destination's.
    ///
    /// Only the destination used to be recalculated. That did not show while the sole caller was
    /// labelling a call that had no contact yet — there was nothing to take it away from — and it
    /// appears the instant moving becomes possible.
    /// </summary>
    [Fact]
    public void BothContactsCountersAreCorrectedByAMove()
    {
        var uliana = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        var serdal = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        var first = Call(uliana, "2026-08-30T09:00:00+03:00");
        var second = Call(uliana, "2026-08-31T09:00:00+03:00");
        _repo.AssignContact(first, uliana);
        _repo.AssignContact(second, uliana);

        Assert.Equal(2, _repo.GetContact(uliana)!.CallCount);

        _repo.AssignContact(second, serdal);

        Assert.Equal(1, _repo.GetContact(uliana)!.CallCount);
        Assert.Equal(1, _repo.GetContact(serdal)!.CallCount);
    }

    /// <summary>
    /// The promise moves with the conversation it was made in.
    ///
    /// Leaving it behind is the worst outcome of a half-move: Uliana keeps a commitment she never
    /// made and will be shown as having missed it, while Serdal's page says he promised nothing.
    /// Neither page looks broken.
    /// </summary>
    [Fact]
    public void TheLedgerEntriesMoveWithTheCall()
    {
        var uliana = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        var serdal = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        var call = Call(uliana);
        AddCommitment(call, uliana, "parayı cuma günü gönderecek");

        _repo.AssignContact(call, serdal);

        Assert.Empty(_repo.GetOpenCommitments(uliana));

        var moved = Assert.Single(_repo.GetOpenCommitments(serdal));
        Assert.Equal("parayı cuma günü gönderecek", moved.Obligation);
    }

    /// <summary>Labelling a call for the first time is the same operation with nothing to take it from.</summary>
    [Fact]
    public void AssigningAnUnlabelledCallStillWorks()
    {
        var serdal = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = Call(contactId: null);

        Assert.Null(_repo.AssignContact(call, serdal));
        Assert.Equal(serdal, _repo.GetCall(call)!.ContactId);
        Assert.Equal(1, _repo.GetContact(serdal)!.CallCount);
    }

    [Fact]
    public void MovingACallToWhereItAlreadyIsChangesNothing()
    {
        var serdal = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = Call(serdal);
        _repo.AssignContact(call, serdal);

        Assert.Null(_repo.AssignContact(call, serdal));
        Assert.Equal(1, _repo.GetContact(serdal)!.CallCount);
    }

    // ---- the binding that caused it -----------------------------------------

    /// <summary>
    /// Moving the call fixes the past; forgetting the binding is what stops it recurring.
    ///
    /// A wrong pairing is not a one-off. The labelling dialog offers to remember the window title
    /// and that box is ticked by default, so a title that was never a name gets bound to whoever
    /// was chosen — and every later call showing it resolves to the same wrong contact. Worse, the
    /// contact then looks known, so the dialog stops appearing and nobody is asked again.
    /// </summary>
    [Fact]
    public void AWrongTitleBindingCanBeForgotten()
    {
        var uliana = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        _repo.RememberTitle("(3) WhatsApp", uliana, CallApp.WhatsApp);

        Assert.Equal(uliana, _repo.ResolveTitle("(3) WhatsApp", CallApp.WhatsApp));

        Assert.Equal(1, _repo.ForgetTitleBinding("(3) WhatsApp", CallApp.WhatsApp));

        Assert.Null(_repo.ResolveTitle("(3) WhatsApp", CallApp.WhatsApp));
    }

    [Fact]
    public void LearnedBindingsCanBeReviewed()
    {
        var uliana = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        _repo.RememberTitle("(3) WhatsApp", uliana, CallApp.WhatsApp);

        var binding = Assert.Single(_repo.TitleBindings());

        Assert.Equal("(3) WhatsApp", binding.Title);
        Assert.Equal("Uliana", binding.ContactName);
        Assert.Equal(CallApp.WhatsApp, binding.App);
    }

    // ---- one person who became two ------------------------------------------

    /// <summary>
    /// Everything of the absorbed contact arrives, and the empty row goes.
    ///
    /// One person becomes two here for ordinary reasons: a title that was not a name created a
    /// contact, a name was typed with different capitalisation, or the same person was reached on
    /// two applications — contacts are keyed on (name, app), so those are already two people.
    /// </summary>
    [Fact]
    public void TwoContactsForOnePersonCanBeMerged()
    {
        var wrong = _repo.UpsertContact("(3) WhatsApp", CallApp.WhatsApp);
        var right = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        var a = Call(wrong, "2026-08-29T09:00:00+03:00");
        var b = Call(wrong, "2026-08-30T09:00:00+03:00");
        _repo.AssignContact(a, wrong);
        _repo.AssignContact(b, wrong);
        AddCommitment(a, wrong, "faturayı yollayacak");

        var c = Call(right, "2026-08-31T09:00:00+03:00");
        _repo.AssignContact(c, right);

        Assert.Equal(2, _repo.MergeContacts(wrong, right));

        Assert.Null(_repo.GetContact(wrong));
        Assert.Equal(3, _repo.GetContact(right)!.CallCount);
        Assert.Single(_repo.GetOpenCommitments(right));
        Assert.All(_repo.ListCalls(right), call => Assert.Equal(right, call.ContactId));
    }

    /// <summary>
    /// Merging carries the learned bindings across, so the surviving contact keeps recognising
    /// the titles the absorbed one had learned.
    /// </summary>
    [Fact]
    public void MergingCarriesTitleBindingsToTheSurvivor()
    {
        var wrong = _repo.UpsertContact("Serdaal", CallApp.WhatsApp);
        var right = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        _repo.RememberTitle("Serdal Bey", wrong, CallApp.WhatsApp);

        _repo.MergeContacts(wrong, right);

        Assert.Equal(right, _repo.ResolveTitle("Serdal Bey", CallApp.WhatsApp));
    }

    /// <summary>
    /// Both learned the same title. A pattern can only point at one contact, and the survivor's
    /// binding is the one that stays — the alternative is a unique-constraint violation that
    /// aborts the whole merge.
    /// </summary>
    [Fact]
    public void ABindingBothContactsLearnedDoesNotBreakTheMerge()
    {
        var wrong = _repo.UpsertContact("Serdaal", CallApp.WhatsApp);
        var right = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        _repo.RememberTitle("Serdal Bey", wrong, CallApp.WhatsApp);
        _repo.RememberTitle("Serdal Bey", right, CallApp.WhatsApp);

        _repo.MergeContacts(wrong, right);

        Assert.Equal(right, _repo.ResolveTitle("Serdal Bey", CallApp.WhatsApp));
        Assert.Single(_repo.TitleBindings());
    }

    [Fact]
    public void MergingAContactIntoItselfIsRefused()
    {
        var serdal = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var call = Call(serdal);
        _repo.AssignContact(call, serdal);

        Assert.Equal(0, _repo.MergeContacts(serdal, serdal));
        Assert.NotNull(_repo.GetContact(serdal));
        Assert.Equal(1, _repo.GetContact(serdal)!.CallCount);
    }

    // ---- fixing a name ------------------------------------------------------

    /// <summary>
    /// Renaming has to be its own operation: UpsertContact matches on the normalised name, so
    /// passing a corrected spelling there creates a second person instead of fixing the first.
    /// </summary>
    [Fact]
    public void AContactCanBeRenamedWithoutCreatingASecondOne()
    {
        var contact = _repo.UpsertContact("Serdaal", CallApp.WhatsApp);

        Assert.True(_repo.RenameContact(contact, "Serdal"));

        Assert.Equal("Serdal", _repo.GetContact(contact)!.Name);
        Assert.Single(_repo.ListContacts());
    }

    /// <summary>
    /// Renaming onto a name that already exists is refused rather than silently merged. Two people
    /// sharing a name is the user's decision to make, and merging exists for when that is what
    /// they meant.
    /// </summary>
    [Fact]
    public void RenamingOntoAnExistingNameIsRefused()
    {
        var first = _repo.UpsertContact("Serdal", CallApp.WhatsApp);
        var second = _repo.UpsertContact("Uliana", CallApp.WhatsApp);

        Assert.False(_repo.RenameContact(second, "Serdal"));

        Assert.Equal("Uliana", _repo.GetContact(second)!.Name);
        Assert.Equal("Serdal", _repo.GetContact(first)!.Name);
    }

    [Fact]
    public void AnEmptyNameIsRefused()
    {
        var contact = _repo.UpsertContact("Serdal", CallApp.WhatsApp);

        Assert.False(_repo.RenameContact(contact, "   "));
        Assert.Equal("Serdal", _repo.GetContact(contact)!.Name);
    }
}
