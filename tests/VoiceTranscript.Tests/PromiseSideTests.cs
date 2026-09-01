using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Promises finally have owners on every surface.
///
/// The user's words: "insanlar verdikleri sözleri unutabiliyorlar" — and the product's answer
/// used to be hiding their promises entirely (the ledger skipped ByMe rows on purpose) and
/// counting both sides into one anonymous "N sözün tarihi geçti". These tests pin the split:
/// own promises visible, badged, first when late, on the calendar, and named in the flags.
/// </summary>
public sealed class PromiseSideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-side-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;

    public PromiseSideTests()
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

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private (long callId, long contactId) Seed(string name = "Uliana")
    {
        var contact = _repo.UpsertContact(name, CallApp.WhatsApp);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.UtcNow,
            State = ProcessingState.Analysed,
        });
        _repo.AssignContact(call, contact);

        return (call, contact);
    }

    private long Promise(
        long callId, long contactId, bool byMe, string obligation,
        DateOnly? deadline, bool conditional = false)
        => _repo.InsertCommitment(new Commitment
        {
            CallId = callId,
            ContactId = contactId,
            ByMe = byMe,
            Quote = $"{obligation} sözü",
            QuoteStartMs = 1000,
            Obligation = obligation,
            DeadlineDate = deadline,
            IsConditional = conditional,
            Status = CommitmentStatus.Open,
        });

    // ---- repository: the calendar's query ---------------------------------------------------

    [Fact]
    public void OwnCommitmentsBetweenReturnsOnlyMineInsideTheWindow()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);

        Promise(call, contact, byMe: true, "evrak göndermek", today.AddDays(3));
        Promise(call, contact, byMe: false, "ödemeyi yapmak", today.AddDays(3));      // theirs
        Promise(call, contact, byMe: true, "koşullu iş", today.AddDays(3), true);     // conditional
        Promise(call, contact, byMe: true, "uzak iş", today.AddDays(90));             // outside

        var rows = _repo.OwnCommitmentsBetween(today, today.AddDays(41));

        var row = Assert.Single(rows);
        Assert.Equal("evrak göndermek", row.Obligation);
        Assert.Equal("Uliana", row.ContactName);
        Assert.Equal(today.AddDays(3), row.Day);
        Assert.Equal(call, row.CallId);
    }

    // ---- ledger: both sides, own chip, own-late first ---------------------------------------

    [Fact]
    public void TheLedgerListsBothSidesAndCountsMinePromisesSeparately()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);

        Promise(call, contact, byMe: true, "benim açık sözüm", today.AddDays(5));
        Promise(call, contact, byMe: false, "onun açık sözü", today.AddDays(5));

        var vm = new LedgerViewModel(_repo);
        vm.Refresh();

        Assert.Equal(1, vm.MyPromiseCount);
        Assert.Equal(1, vm.PromiseCount);
        Assert.Contains(vm.Entries, e => e.Kind == LedgerFilter.MyPromises && e.ByMe);
        Assert.Contains(vm.Entries, e => e.Kind == LedgerFilter.Promises && !e.ByMe);
    }

    [Fact]
    public void MyOverdorPromiseComesBeforeTheirsEvenWhenTheirsIsLater()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);

        Promise(call, contact, byMe: false, "onun geciken sözü", today.AddDays(-10));
        Promise(call, contact, byMe: true, "benim geciken sözüm", today.AddDays(-2));

        var vm = new LedgerViewModel(_repo);
        vm.Refresh();

        // Both graduate to Overdue; mine leads despite being less late — it is the one
        // wrong the user can fix this minute.
        Assert.Equal(2, vm.OverdueCount);
        Assert.True(vm.Entries[0].ByMe);
        Assert.Equal("benim geciken sözüm", vm.Entries[0].Headline);
    }

    // ---- overview: the split attention cards + the calendar dot -----------------------------

    [Fact]
    public void TheOverviewSplitsOverduePromisesByOwner()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);

        Promise(call, contact, byMe: true, "benim gecikenim", today.AddDays(-1));
        Promise(call, contact, byMe: false, "onun gecikeni", today.AddDays(-1));

        var vm = new OverviewViewModel(_repo, () => new AppSettings(), _paths);
        vm.Refresh();

        Assert.Contains(vm.Attention, a => a.Title.Contains("SENİN"));
        Assert.Contains(vm.Attention, a => a.Title.Contains("sözün tarihi geçti") && !a.Title.Contains("SENİN"));

        var mine = vm.Overdue.Single(o => o.ByMe);
        Assert.StartsWith("Sen → Uliana:", mine.Line);
    }

    [Fact]
    public void TheCalendarMarksMyPromiseDeadlineOnItsDay()
    {
        var (call, contact) = Seed();
        var deadline = DateOnly.FromDateTime(DateTime.Today).AddDays(3);

        Promise(call, contact, byMe: true, "evrak göndermek", deadline);

        var vm = new OverviewViewModel(_repo, () => new AppSettings(), _paths);
        vm.Refresh();

        var day = vm.CalendarDays.Single(d => d.Date == deadline);

        Assert.True(day.HasPromises);
        Assert.Contains("🤝", day.Tooltip);
        Assert.Contains("Sen: evrak göndermek — Uliana", day.Tooltip);
        Assert.Equal(call, day.Promises[0].CallId);
    }

    // ---- the flags and the prompt know both sides too ---------------------------------------

    [Fact]
    public void TheOverdueFlagNamesWhoMadeThePromise()
    {
        var today = new DateOnly(2026, 8, 18);

        Commitment At(bool byMe) => new()
        {
            CallId = 1, ContactId = 7, ByMe = byMe, Quote = "söz", QuoteStartMs = 0,
            Obligation = "evrak", DeadlineDate = new DateOnly(2026, 8, 1),
            Status = CommitmentStatus.Open,
        };

        Assert.Contains("(sen)",
            DeterministicChecks.OverdueCommitments([At(true)], today).Single().Summary);
        Assert.Contains("(karşı taraf)",
            DeterministicChecks.OverdueCommitments([At(false)], today).Single().Summary);
    }

    [Fact]
    public void TheConsistencyPromptAsksAboutOnesOwnEarlierPromisesToo()
    {
        // Folded to one line first: the raw string literal wraps mid-phrase.
        var flat = ConsistencyPrompt.SystemPrompt.ReplaceLineEndings(" ");

        Assert.Contains("konuşanın KENDİ önceki", flat);
        Assert.Contains("İnsanlar kendi verdikleri sözleri de unutur", flat);
    }

    [Fact]
    public void AFindingDraftsAReminderInItsOwnWords()
    {
        var evaded = new ConsistencyRow(new Flag
        {
            CallId = 1, Kind = FlagKind.EvadedQuestion,
            Summary = "s", Quote = "Parayı ne zaman göndereceksin?",
        }, _repo);

        Assert.StartsWith("Şu soruyu tekrar sor:", evaded.ReminderDraft);
        Assert.Contains("Parayı ne zaman göndereceksin?", evaded.ReminderDraft);

        var contradiction = new ConsistencyRow(new Flag
        {
            CallId = 1, Kind = FlagKind.Contradiction, Summary = "s", Quote = "Kira yirmi bin",
        }, _repo);

        Assert.StartsWith("Yazılı teyit iste:", contradiction.ReminderDraft);
    }
}
