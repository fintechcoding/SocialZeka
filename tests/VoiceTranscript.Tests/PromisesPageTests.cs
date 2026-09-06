using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The Sözler page: both directions, the user's verbs, and the words it refuses to say.
///
/// "Tutuldu" is the user's mark and nothing else's; "açık kaldı" is said only when there was a
/// chance; a conditional promise is never overdue; a dismissed one is a tombstone under its own
/// chip. Each of these was a way the old ledger chips could mislead.
/// </summary>
public sealed class PromisesPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-sozler-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);

    public PromisesPageTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        new Database(_paths.DatabaseFile).ClearPool();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private (long call, long contact) Seed(string name = "Gürhan", int daysAgo = 10, params string[] lines)
    {
        var contact = _repo.UpsertContact(name, CallApp.WhatsApp);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact, App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddDays(-daysAgo), State = ProcessingState.Analysed,
        });
        _repo.AssignContact(call, contact);

        if (lines.Length > 0)
        {
            _repo.ReplaceSegments(call, lines.Select((text, i) => new Segment
            {
                CallId = call, IsMe = i % 2 == 0, StartMs = i * 4000, EndMs = i * 4000 + 3000, Text = text,
            }));
        }

        return (call, contact);
    }

    private long Promise(
        long call, long contact, bool byMe, string obligation,
        DateOnly? deadline = null, bool conditional = false, string? quote = null, int quoteStartMs = 4000) =>
        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, ByMe = byMe,
            Quote = quote ?? $"{obligation} sözü", QuoteStartMs = quoteStartMs,
            Obligation = obligation, DeadlineDate = deadline, IsConditional = conditional,
        });

    private PromisesViewModel Page()
    {
        var vm = new PromisesViewModel(_repo);
        vm.Refresh();
        return vm;
    }

    /// <summary>Goes red when a promise lands in the wrong column, or the counts on the chips lie.</summary>
    [Fact]
    public void EachDirectionHasItsColumnAndTheChipsCount()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: true, "Sözleşme taslağını göndermek", _today.AddDays(-3));
        Promise(call, contact, byMe: false, "Dilekçeyi iletmek", _today.AddDays(2));
        Promise(call, contact, byMe: false, "Bakmak");

        var vm = Page();

        Assert.Single(vm.Mine);
        Assert.Equal(2, vm.Theirs.Count);
        Assert.Equal(3, vm.AllCount);
        Assert.Equal(1, vm.OverdueCount);
        Assert.Equal(1, vm.ThisWeekCount);
        Assert.Equal(1, vm.UndatedCount);
        Assert.Contains("(1)", vm.MineHeader);
    }

    /// <summary>Goes red when "if X then I will Y" is shown as late — a condition is not a date missed.</summary>
    [Fact]
    public void AConditionalPromiseIsNeverOverdue()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: false, "Ödemeyi yapmak", _today.AddDays(-5), conditional: true);

        var vm = Page();

        var card = Assert.Single(vm.Theirs);
        Assert.False(card.IsOverdue);
        Assert.Equal(0, vm.OverdueCount);
        Assert.Equal(1, vm.ConditionalCount);
    }

    /// <summary>
    /// Goes red when "açık kaldı" is said without a later call — silence is not a broken promise —
    /// or not said once there was one.
    /// </summary>
    [Fact]
    public void LeftOpenNeedsAChanceToHaveKeptIt()
    {
        var (call, contact) = Seed(daysAgo: 30);
        Promise(call, contact, byMe: false, "Dilekçeyi iletmek", _today.AddDays(-20));

        var before = Assert.Single(Page().Theirs);
        Assert.True(before.IsOverdue);
        Assert.False(before.IsLeftOpen);

        Seed(daysAgo: 3);

        var after = Assert.Single(Page().Theirs);
        Assert.Equal(1, after.CallsSince);
        Assert.True(after.IsLeftOpen);
    }

    /// <summary>Goes red when a dismissed promise stays on the open list, or is lost from its own chip.</summary>
    [Fact]
    public void ADismissedPromiseLivesOnlyUnderItsChip()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: false, "Bakmak");

        var vm = Page();
        vm.DismissCommand.Execute(vm.Theirs.Single());

        Assert.Empty(vm.Theirs);
        Assert.Equal(1, vm.DismissedCount);
        Assert.NotNull(vm.Notice);

        vm.Filter = PromiseFilter.Dismissed;
        var card = Assert.Single(vm.Theirs);
        Assert.True(card.IsDismissed);
        Assert.True(card.CanRestore);
    }

    /// <summary>Goes red when "Tutuldu" cannot be taken back from the notice, or leaves the open list without a trace.</summary>
    [Fact]
    public void KeptCanBeUndoneFromTheNotice()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: true, "Sözleşme taslağını göndermek", _today.AddDays(-1));

        var vm = Page();
        vm.FulfilCommand.Execute(vm.Mine.Single());

        Assert.Empty(vm.Mine);
        Assert.Equal(1, vm.KeptCount);
        Assert.True(vm.CanUndo);
        Assert.Contains("tutuldu", vm.MineTally, StringComparison.OrdinalIgnoreCase);

        vm.UndoCommand.Execute(null);

        Assert.Single(vm.Mine);
        Assert.Equal(0, vm.KeptCount);
        Assert.False(vm.CanUndo);
    }

    /// <summary>Goes red when postponing loses the spoken date or fails to lift the overdue mark.</summary>
    [Fact]
    public void PostponingMovesTheDateAndKeepsTheSpokenOne()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: false, "Dilekçeyi iletmek", _today.AddDays(-2));

        var vm = Page();
        var card = vm.Theirs.Single();

        vm.BeginPostponeCommand.Execute(card);
        card.PostponeTo = _today.AddDays(5).ToDateTime(TimeOnly.MinValue);
        vm.ApplyPostponeCommand.Execute(card);

        var moved = vm.Theirs.Single();
        Assert.False(moved.IsOverdue);
        Assert.Equal(_today.AddDays(5), moved.Deadline);
        Assert.Equal(_today.AddDays(-2), moved.Commitment.DeadlineDate);
        Assert.True(moved.HasUserDeadline);
        Assert.Contains("senin tarihin", moved.DeadlineText);
    }

    /// <summary>Goes red when a later line about the promise is not offered as a question under the card.</summary>
    [Fact]
    public void ALaterMentionBecomesAQuestionNotAMark()
    {
        var (call, contact) = Seed("Gürhan", 8, "Alo", "Sözleşme taslağını cumaya yollarım");
        Promise(call, contact, byMe: false, "Sözleşme taslağını göndermek", _today.AddDays(-3));

        Seed("Gürhan", 1, "Merhaba", "Sözleşme taslağını dün gönderdim sana");

        var card = Assert.Single(Page().Theirs);

        Assert.True(card.HasHint);
        Assert.Contains("tutuldu mu", card.HintText);
        Assert.True(card.IsOpen);
    }

    // ---- S1: sözün etrafı -------------------------------------------------------------------

    /// <summary>
    /// Goes red when the lines around a promise come from the wrong conversation, run past the
    /// two either side, include the promise's own line, or walk off the end of the call.
    ///
    /// The whole point of the strip is that it is THIS call's transcript at THIS moment: a line
    /// borrowed from another conversation would be evidence for a promise that was never made
    /// there, which is the one thing this product must never put on screen.
    /// </summary>
    [Fact]
    public void SurroundingLinesComeFromTheRightCallAndStopAtItsEdges()
    {
        var (call, contact) = Seed("Uliana", 5,
            "Sesim çok kötü geliyor.", "Alo duyuyor musun?", "Yav bir kulaklık alacağım güzel ya.",
            "Tamam şimdi iyi.", "Neyse sonra konuşuruz.", "Hadi görüşürüz.");

        // The promise sits on the third line, at 8000 ms.
        Promise(call, contact, byMe: true, "güzel bir kulaklık almak", quoteStartMs: 8000);

        // Another conversation entirely, whose lines must never appear on this card.
        Seed("Bozkurt", 4, "Başka bir görüşme", "Buranın satırları o kartta görünemez");

        var card = Assert.Single(Page().Mine);

        Assert.Equal(4, card.Around.Count);
        Assert.All(card.Around, line => Assert.Equal(call, line.CallId));
        Assert.DoesNotContain(card.Around, line => line.StartMs == 8000);

        Assert.Equal(new[] { 0, 4000, 12000, 16000 }, card.Around.Select(l => l.StartMs).ToArray());
        Assert.Equal("Sesim çok kötü geliyor.", card.Around[0].Text);

        // Stamped by side, from which file the audio was in — nothing is read into it.
        Assert.True(card.Around[0].IsMe);
        Assert.False(card.Around[1].IsMe);

        // And a promise on the first line has nothing before it and does not reach backwards.
        Promise(call, contact, byMe: false, "ilk satırdaki söz", quoteStartMs: 0);

        var atTheEdge = Assert.Single(Page().Theirs);
        Assert.Equal(2, atTheEdge.Around.Count);
        Assert.Equal(new[] { 4000, 8000 }, atTheEdge.Around.Select(l => l.StartMs).ToArray());
    }

    /// <summary>
    /// Goes red when a promise whose conversation has no transcript throws instead of showing an
    /// empty strip. Twenty-odd calls in a real archive were transcribed by an engine that is no
    /// longer there, and a page that crashes on them is a page nobody can open.
    /// </summary>
    [Fact]
    public void ACardWithNoNeighbouringLinesDoesNotCrash()
    {
        var (call, contact) = Seed("Samet", 3);
        Promise(call, contact, byMe: false, "Dilekçeyi iletmek");

        var card = Assert.Single(Page().Theirs);

        Assert.Empty(card.Around);
        Assert.False(card.HasAround);
        Assert.False(card.IsAroundOpen);
    }

    /// <summary>
    /// Goes red when one long transcript line is allowed to make the card taller than the page.
    /// Four uncut lines on each of thirteen cards turns the ledger back into a transcript.
    /// </summary>
    [Fact]
    public void LongSurroundingLinesAreCutSoTheCardCannotBloat()
    {
        var (call, contact) = Seed("Sinan", 2, new string('a', 400), "kısa satır");
        Promise(call, contact, byMe: false, "Bakmak", quoteStartMs: 4000);

        var line = Assert.Single(Assert.Single(Page().Theirs).Around);

        Assert.Equal(PromiseLine.MaxLength, line.Text.Length);
        Assert.EndsWith("…", line.Text);
    }

    // ---- S2: tek cümle, iki söz -------------------------------------------------------------

    private const string OneSentence = "Yav bir kulaklık alacağım güzel ya. Dur Whatsapp'tan ayırayım seni bekle.";

    /// <summary>
    /// Goes red when the card groups rows that are not the same sentence, or fails to group ones
    /// that are. The key is the quadruple — one call, one side, one millisecond, one wording —
    /// and every part of it has to matter: a different moment is a different sentence, and the
    /// other person's promise is never a candidate reading of the user's own.
    /// </summary>
    [Fact]
    public void GroupingFiresOnlyOnAnExactQuadrupleMatch()
    {
        var (call, contact) = Seed("Uliana", 6, "Sesim kötü", OneSentence);

        Promise(call, contact, byMe: true, "güzel bir kulaklık almak", quote: OneSentence, quoteStartMs: 51_450);
        Promise(call, contact, byMe: true, "Whatsapp'tan ayırmak", quote: OneSentence, quoteStartMs: 51_450);

        // Same words, other side. Same words, another millisecond. Another call entirely.
        Promise(call, contact, byMe: false, "başka taraf", quote: OneSentence, quoteStartMs: 51_450);
        Promise(call, contact, byMe: true, "başka an", quote: OneSentence, quoteStartMs: 60_000);

        var (other, otherContact) = Seed("Bozkurt", 6);
        Promise(other, otherContact, byMe: true, "başka görüşme", quote: OneSentence, quoteStartMs: 51_450);

        var vm = Page();

        // One grouped card carrying two readings, plus the two singles that did not match it.
        var grouped = Assert.Single(vm.Mine, k => k.IsGrouped);
        Assert.Equal(2, grouped.Candidates.Count);
        Assert.Equal(
            new[] { "güzel bir kulaklık almak", "Whatsapp'tan ayırmak" },
            grouped.Candidates.Select(k => k.Obligation).ToArray());

        // The group is one card: the follower is not listed beside its own leader.
        Assert.Equal(3, vm.Mine.Count);
        Assert.All(vm.Theirs, k => Assert.False(k.IsGrouped));

        // Grouping is a view decision. The ledger still holds five rows and says so.
        Assert.Equal(5, vm.AllCount);
        Assert.Equal(5, vm.OpenCount);
    }

    /// <summary>
    /// Goes red when answering "bu cümlede hangisi?" fails to silence the readings the user did
    /// not pick, rewrites anything on the one they did, or cannot be taken back in one click.
    ///
    /// This is the archive's own case: one of the user's three promises was not a promise at all,
    /// and it was marked "tutuldu" seven seconds after the real one.
    /// </summary>
    [Fact]
    public void PickingSilencesTheOthersAndLeavesTheChoiceAsABadge()
    {
        var (call, contact) = Seed("Uliana", 6, "Sesim kötü", OneSentence);
        var kulaklik = Promise(call, contact, byMe: true, "güzel bir kulaklık almak", quote: OneSentence, quoteStartMs: 51_450);
        var whatsapp = Promise(call, contact, byMe: true, "Whatsapp'tan ayırmak", quote: OneSentence, quoteStartMs: 51_450);

        var vm = Page();
        var group = Assert.Single(vm.Mine);
        var chosen = group.Candidates.Single(k => k.Id == kulaklik);

        vm.PickCandidateCommand.Execute(chosen);

        // One card again, and it is the one that was picked.
        var standing = Assert.Single(vm.Mine);
        Assert.Equal(kulaklik, standing.Id);
        Assert.False(standing.IsGrouped);

        // ✎ below the card, derived from the tombstone beside it — nothing was written to the row.
        Assert.True(standing.IsChosen);
        Assert.True(standing.HasUserMark);
        Assert.Equal("güzel bir kulaklık almak", standing.Obligation);
        Assert.Null(standing.Commitment.UserObligation);

        var silenced = _repo.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == whatsapp);
        Assert.True(silenced.Commitment.DismissedByUser);

        // And one "Geri al" puts the question back exactly as it was.
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);

        var back = Assert.Single(vm.Mine);
        Assert.True(back.IsGrouped);
        Assert.False(back.IsChosen);
    }

    /// <summary>
    /// Goes red when "İkisi de kalsın" is not remembered — the card would ask the same question
    /// on every visit, which is how a surface teaches people to ignore it.
    /// </summary>
    [Fact]
    public void KeepingBothSplitsTheCardBackInTwo()
    {
        var (call, contact) = Seed("Uliana", 6, "Sesim kötü", OneSentence);
        Promise(call, contact, byMe: true, "güzel bir kulaklık almak", quote: OneSentence, quoteStartMs: 51_450);
        Promise(call, contact, byMe: true, "Whatsapp'tan ayırmak", quote: OneSentence, quoteStartMs: 51_450);

        var vm = Page();
        vm.KeepAllCandidatesCommand.Execute(Assert.Single(vm.Mine));

        Assert.Equal(2, vm.Mine.Count);
        Assert.All(vm.Mine, k => Assert.False(k.IsGrouped));
        Assert.All(vm.Mine, k => Assert.True(k.IsJudgedCorrect));

        // Nothing was turned down: both promises still stand.
        Assert.Equal(2, vm.OpenCount);
        Assert.Equal(0, vm.DismissedCount);

        // A fresh page reads the same answer back out of the archive rather than asking again.
        Assert.Equal(2, Page().Mine.Count);
    }

    // ---- S3: "ne zamana?" -------------------------------------------------------------------

    /// <summary>
    /// Goes red when the strip writes over what the words said. The spoken date is what the
    /// consistency check reads to see whether the OTHER person moved a deadline — a date the
    /// user typed into that column would be held against somebody who never said it.
    /// </summary>
    [Fact]
    public void TheStripWritesTheUsersDeadlineAndLeavesTheSpokenOne()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: false, "Dilekçeyi iletmek");

        var vm = Page();
        var card = Assert.Single(vm.Theirs);

        Assert.True(card.NeedsDeadline);
        Assert.True(card.IsUndated);

        vm.DeadlineThisWeekCommand.Execute(card);

        var dated = Assert.Single(vm.Theirs);
        Assert.NotNull(dated.Deadline);
        Assert.True(dated.HasUserDeadline);
        Assert.Equal(dated.Commitment.UserDeadlineDate, dated.Deadline);

        // The machine's column was never touched, and the strip has stopped asking.
        Assert.Null(dated.Commitment.DeadlineDate);
        Assert.False(dated.NeedsDeadline);

        // "Bu hafta" is the end of this week; "önümüzdeki hafta" is a week further out.
        Assert.True(dated.Deadline <= _today.AddDays(7));

        vm.DeadlineNextWeekCommand.Execute(dated);
        Assert.True(Assert.Single(vm.Theirs).Deadline > _today.AddDays(6));
        Assert.Null(Assert.Single(vm.Theirs).Commitment.DeadlineDate);
    }

    /// <summary>
    /// Goes red when "Tarihsiz kalsın" is forgotten. Twelve of the archive's thirteen promises
    /// have no date; if the strip cannot take "there is no date" for an answer it asks the same
    /// question on almost every card, for ever.
    /// </summary>
    [Fact]
    public void KeepingItUndatedSilencesTheStrip()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: true, "Coşkun'u halletmek");

        var vm = Page();
        vm.KeepUndatedCommand.Execute(Assert.Single(vm.Mine));

        var answered = Assert.Single(vm.Mine);
        Assert.False(answered.NeedsDeadline);
        Assert.True(answered.IsKeptUndated);
        Assert.True(answered.KeepsUndated);

        // An answer, not a date: neither column was written.
        Assert.Null(answered.Commitment.UserDeadlineDate);
        Assert.Null(answered.Commitment.DeadlineDate);
        Assert.True(answered.IsOpen);
        Assert.Equal(1, vm.UndatedCount);

        // Read back from the archive on a fresh page, and revocable.
        Assert.True(Assert.Single(Page().Mine).KeepsUndated);

        vm.AskAgainForDeadlineCommand.Execute(answered);
        Assert.True(Assert.Single(vm.Mine).NeedsDeadline);
    }

    // ---- S4: "bu söz değildi" ----------------------------------------------------------------

    /// <summary>
    /// Goes red when a row the user said is not a promise is still counted as one anywhere.
    ///
    /// The archive's finding: a conversational aside — "dur Whatsapp'tan ayırayım seni" — sat in
    /// the ledger as a promise and was marked kept. A ruling that leaves the row in even one
    /// count is a ruling the page ignored.
    /// </summary>
    [Fact]
    public void ANotAPromiseRowLeavesEveryCount()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: true, "Whatsapp'tan ayırmak");
        Promise(call, contact, byMe: true, "Sözleşme taslağını göndermek", _today.AddDays(-3));

        var vm = Page();
        Assert.Equal(2, vm.OpenCount);

        vm.JudgeNotAPromiseCommand.Execute(vm.Mine.Single(k => k.Obligation == "Whatsapp'tan ayırmak"));

        Assert.Single(vm.Mine);
        Assert.Equal(1, vm.OpenCount);
        Assert.Equal(1, vm.AllCount);
        Assert.Equal(0, vm.UndatedCount);
        Assert.Equal(1, vm.OverdueCount);
        Assert.Equal(0, vm.KeptCount);
        Assert.StartsWith("İşaretledin: 0 tutuldu · 1 vadesi geçti · 0 işaretsiz", vm.MineTally);

        // Reachable and revocable: a refusal is a tombstone, never a deletion.
        Assert.Equal(1, vm.DismissedCount);

        vm.Filter = PromiseFilter.Dismissed;
        var refused = Assert.Single(vm.Mine);
        Assert.True(refused.IsNotAPromise);
        Assert.True(refused.IsRefused);
        Assert.True(refused.CanRestore);
        Assert.Contains("söz değil", refused.HeadText);

        vm.RestoreCommand.Execute(refused);
        vm.Filter = PromiseFilter.Open;
        Assert.Equal(2, vm.OpenCount);
    }

    /// <summary>
    /// Goes red when the ear's other two answers stop being marks and start being verdicts of
    /// their own. "Yanlış duyulmuş" is a complaint about the transcript, not about the promise:
    /// the row stays exactly where it was, with the user's ruling showing beside it.
    /// </summary>
    [Fact]
    public void MishearingIsAMarkAndNotARemoval()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: false, "Dilekçeyi iletmek");

        var vm = Page();
        vm.JudgeMisheardCommand.Execute(vm.Theirs.Single());

        var marked = Assert.Single(vm.Theirs);
        Assert.True(marked.IsMisheard);
        Assert.True(marked.HasUserMark);
        Assert.True(marked.IsOpen);
        Assert.Equal(1, vm.OpenCount);
        Assert.NotNull(marked.JudgementText);
    }

    // ---- S5: the honesty line under each column -----------------------------------------------

    /// <summary>
    /// Goes red when the page shows two columns of very different sizes without saying that the
    /// difference may be its own. Today's archive is three of the user's own promises against ten
    /// of the other side's, and most of that shape is the extraction's: a promise in one's own
    /// speech is hedged, half-said and interrupted, and the model finds fewer of them.
    ///
    /// The rule is twice-and-three-clear. A ratio alone would fire on nothing-against-two, which
    /// is a coin-flip run; a gap alone would fire on forty-against-forty-three, which is not a
    /// shape at all.
    /// </summary>
    [Fact]
    public void TheHonestyLineAppearsAtThreeAgainstTenAndNotAtParity()
    {
        var (call, contact) = Seed("Uliana", 7);

        for (var i = 0; i < 3; i++) Promise(call, contact, byMe: true, $"benim sözüm {i}");
        for (var i = 0; i < 10; i++) Promise(call, contact, byMe: false, $"onun sözü {i}");

        var vm = Page();

        // The denominator, under both columns.
        Assert.Contains("1", vm.SourceLine);
        Assert.Contains("görüşme", vm.SourceLine);

        Assert.True(vm.HasAsymmetryNote);
        Assert.Contains("çıkarımdan", vm.AsymmetryNote);

        // Brought to parity, the page stops explaining a difference that is not there.
        for (var i = 0; i < 7; i++) Promise(call, contact, byMe: true, $"benim sözüm ek {i}");

        vm.Refresh();
        Assert.False(vm.HasAsymmetryNote);
        Assert.Null(vm.AsymmetryNote);
    }

    // ---- the completion audit's two findings ---------------------------------------------------

    /// <summary>
    /// Finding 5. Goes red when the "Açık" chip counts anything but open promises.
    ///
    /// It used to show <c>PromiseLedger(includeClosed: true).Count</c> — kept rows, dismissed
    /// rows and all — under a label that says "open". The total belongs on "Hepsi", which had no
    /// number at all.
    /// </summary>
    [Fact]
    public void TheOpenChipCountsOnlyOpenRowsAndTheTotalMovesToHepsi()
    {
        var (call, contact) = Seed();
        Promise(call, contact, byMe: false, "Açık kalan");
        var kept = Promise(call, contact, byMe: false, "Tutulan");
        var dropped = Promise(call, contact, byMe: false, "Reddedilen");

        _repo.FulfilCommitment(kept);
        _repo.DismissCommitment(dropped);

        var vm = Page();

        Assert.Equal(1, vm.OpenCount);
        Assert.Equal(3, vm.AllCount);
        Assert.Equal(1, vm.KeptCount);
        Assert.Equal(1, vm.DismissedCount);
    }

    /// <summary>
    /// Finding 25. Goes red when "tutuldu" forgets which conversation closed the promise.
    ///
    /// <c>fulfilled_by_call_id</c> has existed since the first schema and no path in the product
    /// ever wrote it: the page called FulfilCommitment with the call id left null, so the
    /// "tutuldu mu?" suggestion could never point back at the call it came from.
    /// </summary>
    [Fact]
    public void FulfilledByCallIdRoundTrips()
    {
        var (call, contact) = Seed("Gürhan", 8, "Alo", "Sözleşme taslağını cumaya yollarım");
        var promise = Promise(call, contact, byMe: false, "Sözleşme taslağını göndermek", _today.AddDays(-3));

        var (later, _) = Seed("Gürhan", 1, "Merhaba", "Sözleşme taslağını dün gönderdim sana");

        var vm = Page();
        var card = Assert.Single(vm.Theirs);

        Assert.Equal(later, card.Hint?.CallId);

        vm.FulfilCommand.Execute(card);

        var stamped = _repo.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == promise).Commitment;
        Assert.Equal(CommitmentStatus.Fulfilled, stamped.Status);
        Assert.Equal(later, stamped.FulfilledByCallId);

        vm.UndoCommand.Execute(null);

        var reopened = _repo.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == promise).Commitment;
        Assert.Equal(CommitmentStatus.Open, reopened.Status);
        Assert.Null(reopened.FulfilledByCallId);
    }

    /// <summary>
    /// Finding 6. Goes red when a ruling made in the edit dialog cannot be taken back on the page
    /// that opened it — ✎ was the only verb here whose undo was dropped on the floor.
    /// </summary>
    [Fact]
    public void TheEditDialogsUndoIsOfferedByThePage()
    {
        var (call, contact) = Seed();
        var promise = Promise(call, contact, byMe: false, "Dilekçeyi iletmek");

        var vm = Page();
        var card = Assert.Single(vm.Theirs);

        // What PromisesPage does with what EditPromiseWindow hands back.
        vm.Offer(VoiceTranscript.App.Services.LedgerActions.Edit(
            _repo, card.Commitment, "Dilekçeyi kaymakamlığa iletmek", _today.AddDays(4)));

        Assert.True(vm.CanUndo);
        Assert.NotNull(vm.Notice);

        var edited = Assert.Single(vm.Theirs);
        Assert.Equal("Dilekçeyi kaymakamlığa iletmek", edited.Obligation);
        Assert.True(edited.IsEdited);

        vm.UndoCommand.Execute(null);

        var back = _repo.PromiseLedger(includeClosed: true).Single(r => r.Commitment.Id == promise).Commitment;
        Assert.Null(back.UserObligation);
        Assert.Null(back.UserDeadlineDate);
        Assert.False(vm.CanUndo);
    }
}
