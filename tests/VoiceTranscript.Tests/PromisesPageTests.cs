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

    private long Promise(long call, long contact, bool byMe, string obligation, DateOnly? deadline = null, bool conditional = false) =>
        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, ByMe = byMe, Quote = $"{obligation} sözü", QuoteStartMs = 4000,
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
}
