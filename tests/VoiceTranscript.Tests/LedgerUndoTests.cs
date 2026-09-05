using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The user's rulings on the ledger: taken back, stamped, and never overwritten by a re-run.
///
/// Complaint 1: the ledger could not be cleaned. A dismissal was one way; "tutuldu" was a
/// status with no date and no way back; a postponed deadline had nowhere to go but over what
/// was said; and every re-analysis deleted the open rows and wrote them again, so whatever the
/// user had changed came back as it was. These tests pin the repository half of the fix — the
/// verbs, their stamps, and the two protections (ClearAnalysis / SweepLedger keep an edited row;
/// the pipeline does not write the same words twice beside a surviving one).
/// </summary>
public sealed class LedgerUndoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-undo-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly long _call;
    private readonly long _contact;

    public LedgerUndoTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);

        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
        _call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
            State = ProcessingState.Analysed,
        });
        _repo.AssignContact(_call, _contact);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private long Promise(string obligation, DateOnly? deadline = null, bool byMe = false, string? quote = null) =>
        _repo.InsertCommitment(new Commitment
        {
            CallId = _call,
            ContactId = _contact,
            ByMe = byMe,
            Quote = quote ?? $"{obligation} sözü",
            QuoteStartMs = 4200,
            Obligation = obligation,
            DeadlineRaw = deadline is null ? null : "cuma",
            DeadlineDate = deadline,
            Status = CommitmentStatus.Open,
        });

    private long Finding(string quote) =>
        _repo.InsertFlag(new Flag
        {
            CallId = _call,
            ContactId = _contact,
            Kind = FlagKind.PressureTactic,
            Summary = "baskı",
            Quote = quote,
            QuoteStartMs = 9000,
            CreatedAt = DateTimeOffset.UtcNow,
        });

    private Commitment Row(long id) =>
        _repo.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == id).Commitment;

    // ---- stamps ---------------------------------------------------------------------------

    /// <summary>Goes red when a new promise does not know when it was written — "bilinmiyor" is for old rows only.</summary>
    [Fact]
    public void ANewPromiseIsStampedWithItsBirth()
    {
        var id = Promise("Sözleşmeyi göndermek");

        var row = Row(id);
        Assert.NotNull(row.CreatedAt);
        Assert.InRange(row.CreatedAt!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Null(row.DecidedAt);
    }

    /// <summary>
    /// Goes red when "tutuldu" can no longer be taken back, or loses its date. A kept promise
    /// carries when it was marked so; reopening clears the mark and keeps the ruling stamp.
    /// </summary>
    [Fact]
    public void KeptCanBeReopenedAndBothAreStamped()
    {
        var id = Promise("Sözleşmeyi göndermek");
        var later = _repo.InsertCall(new Call { ContactId = _contact, App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Analysed });

        _repo.FulfilCommitment(id, byCallId: later);

        var kept = Row(id);
        Assert.Equal(CommitmentStatus.Fulfilled, kept.Status);
        Assert.Equal(later, kept.FulfilledByCallId);
        Assert.NotNull(kept.FulfilledAt);
        Assert.NotNull(kept.DecidedAt);

        _repo.ReopenCommitment(id);

        var reopened = Row(id);
        Assert.Equal(CommitmentStatus.Open, reopened.Status);
        Assert.Null(reopened.FulfilledByCallId);
        Assert.Null(reopened.FulfilledAt);
        Assert.NotNull(reopened.DecidedAt);
        Assert.Contains(_repo.AllOpenCommitments(), r => r.Commitment.Id == id);
    }

    /// <summary>Goes red when a dismissal cannot be undone: the row is a tombstone, not gone.</summary>
    [Fact]
    public void ADismissedPromiseCanBeBroughtBack()
    {
        var id = Promise("Sözleşmeyi göndermek");

        _repo.DismissCommitment(id);
        Assert.DoesNotContain(_repo.AllOpenCommitments(), r => r.Commitment.Id == id);
        Assert.Contains(_repo.DismissedCommitments(), r => r.Commitment.Id == id);
        Assert.NotNull(Row(id).DecidedAt);

        _repo.RestoreCommitment(id);
        Assert.Contains(_repo.AllOpenCommitments(), r => r.Commitment.Id == id);
        Assert.DoesNotContain(_repo.DismissedCommitments(), r => r.Commitment.Id == id);
    }

    /// <summary>Same for a finding: dismissed, listed among the dismissed, brought back.</summary>
    [Fact]
    public void ADismissedFindingCanBeBroughtBack()
    {
        var id = Finding("bugün karar vermezsen başkasına vereceğim");

        _repo.DismissFlag(id);
        Assert.DoesNotContain(_repo.GetFlags(_contact), f => f.Id == id);
        Assert.Contains(_repo.DismissedFlags(), f => f.Flag.Id == id);
        Assert.NotNull(_repo.GetFlags(_contact, includeDismissed: true).Single(f => f.Id == id).DecidedAt);

        _repo.RestoreFlag(id);
        Assert.Contains(_repo.GetFlags(_contact), f => f.Id == id);
    }

    /// <summary>Goes red when the select mode's bulk dismissal misses a row or touches one it was not given.</summary>
    [Fact]
    public void SeveralCanBeDismissedAtOnce()
    {
        var a = Promise("a"); var b = Promise("b"); var c = Promise("c");
        var f1 = Finding("bir"); var f2 = Finding("iki");

        Assert.Equal(2, _repo.DismissCommitments([a, c]));
        Assert.Equal(1, _repo.DismissFlags([f2]));

        Assert.Equal([b], _repo.AllOpenCommitments().Select(r => r.Commitment.Id).ToList());
        Assert.Equal([f1], _repo.GetFlags(_contact).Select(f => f.Id).ToList());
    }

    // ---- the user's own columns ------------------------------------------------------------

    /// <summary>
    /// Goes red when postponing a promise overwrites the spoken date, or when the postponed date
    /// does not count. The machine's date stays (it is what the words said); the user's wins
    /// wherever a date is shown or judged.
    /// </summary>
    [Fact]
    public void PostponingKeepsTheSpokenDateAndCountsTheUsersOne()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var id = Promise("Sözleşmeyi göndermek", today.AddDays(-3));

        _repo.SetUserDeadline(id, today.AddDays(4));

        var row = Row(id);
        Assert.Equal(today.AddDays(-3), row.DeadlineDate);
        Assert.Equal(today.AddDays(4), row.UserDeadlineDate);
        Assert.Equal(today.AddDays(4), row.EffectiveDeadline);
        Assert.True(row.IsEdited);
        Assert.False(row.IsOverdue(today));

        Assert.DoesNotContain(_repo.OverdueCommitments(today), r => r.Commitment.Id == id);
        Assert.Contains(_repo.TheirCommitmentsBetween(today, today.AddDays(7)), r => r.CallId == _call);
    }

    /// <summary>
    /// Goes red when a postponement is held against the other person. The moved-deadline check
    /// reads what was SAID across calls; a date the user typed is not a date anybody moved.
    /// </summary>
    [Fact]
    public void APostponementIsNotAMovedDeadline()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var id = Promise("Sözleşmeyi göndermek", today.AddDays(2));
        _repo.SetUserDeadline(id, today.AddDays(20));

        var flags = DeterministicChecks.MovedDeadlines(_repo.GetOpenCommitments(_contact));

        Assert.Empty(flags);
    }

    /// <summary>Goes red when rewording touches the quote, or when clearing both edits leaves the row marked edited.</summary>
    [Fact]
    public void RewordingChangesWhatIsShownNotWhatWasSaid()
    {
        var id = Promise("Sözleşme", quote: "cumaya sana yollarım");

        _repo.SetUserObligation(id, "Sözleşme taslağını göndermek");

        var row = Row(id);
        Assert.Equal("cumaya sana yollarım", row.Quote);
        Assert.Equal("Sözleşme", row.Obligation);
        Assert.Equal("Sözleşme taslağını göndermek", row.EffectiveObligation);
        Assert.True(row.IsEdited);

        _repo.SetUserObligation(id, null);
        Assert.False(Row(id).IsEdited);
    }

    // ---- protection from re-runs ------------------------------------------------------------

    /// <summary>
    /// The complaint's sharpest edge. Goes red when a re-analysis deletes a promise the user
    /// edited or postponed: their ruling is the one thing in the table a person wrote.
    /// </summary>
    [Fact]
    public void ClearAnalysisLeavesAnEditedPromiseAlone()
    {
        var edited = Promise("Düzenlenen");
        var postponed = Promise("Ertelenen", DateOnly.FromDateTime(DateTime.Today));
        var untouched = Promise("Dokunulmayan");

        _repo.SetUserObligation(edited, "Düzenlenen, benim sözlerimle");
        _repo.SetUserDeadline(postponed, DateOnly.FromDateTime(DateTime.Today).AddDays(3));

        _repo.ClearAnalysis(_call);

        var left = _repo.PromiseLedger(includeClosed: true).Select(r => r.Commitment.Id).ToList();
        Assert.Contains(edited, left);
        Assert.Contains(postponed, left);
        Assert.DoesNotContain(untouched, left);
    }

    /// <summary>
    /// Goes red when the identity of a surviving row is not what the pipeline checks against:
    /// (by whom, folded quote), folded the way the pipeline folds — so "Cumaya" and "cumaya"
    /// are one promise.
    /// </summary>
    [Fact]
    public void SurvivingRowsAreKnownByTheirWords()
    {
        var kept = Promise("Tutulan", quote: "Cumaya sana yollarım");
        var edited = Promise("Düzenlenen", quote: "Pazartesi ararım");
        Promise("Sıradan", quote: "Bakarız");

        _repo.FulfilCommitment(kept);
        _repo.SetUserObligation(edited, "Pazartesi arayacak");

        var keys = _repo.SurvivingCommitmentKeys(_call);

        Assert.Equal(2, keys.Count);
        Assert.Contains((false, TurkishText.NormalizeForSearch("cumaya sana yollarım")), keys);
        Assert.Contains((false, TurkishText.NormalizeForSearch("PAZARTESİ ARARIM")), keys);
    }

    /// <summary>Goes red when the sweep for hollow or duplicated rows takes an edited one with it.</summary>
    [Fact]
    public void TheSweepSparesAnEditedRow()
    {
        var hollow = _repo.InsertCommitment(new Commitment { CallId = _call, ContactId = _contact, Quote = "hmm", Obligation = " " });
        _repo.SetUserObligation(hollow, "Aslında şunu söz verdi");

        var twin = Promise("Aynı", quote: "aynı söz");
        var twinToo = Promise("Aynı", quote: "aynı söz");
        _repo.SetUserDeadline(twinToo, DateOnly.FromDateTime(DateTime.Today));

        var swept = _repo.SweepLedger();

        var left = _repo.PromiseLedger(includeClosed: true).Select(r => r.Commitment.Id).ToList();
        Assert.Contains(hollow, left);
        Assert.Contains(twin, left);
        Assert.Contains(twinToo, left);
        Assert.Equal(0, swept.Total);
    }
}
