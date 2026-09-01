using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The Takvim page: the month view shows everything date-bearing and invents nothing.
///
/// Pins the two queries the page added — the other side's promise deadlines and the open action
/// suggestions with a date — and the view model's assembly of a Monday-first month where the
/// user's own entries outrank machine suggestions inside every day.
/// </summary>
public sealed class CalendarPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-takvim-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;

    public CalendarPageTests()
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

    private long Suggest(
        long callId, long contactId, string action, DateOnly? deadline,
        ActionStatus status = ActionStatus.Open)
        => _repo.InsertAction(new ActionItem
        {
            CallId = callId,
            ContactId = contactId,
            Action = action,
            Quote = $"{action} alıntısı",
            DeadlineDate = deadline,
            Status = status,
        });

    // ---- the two queries the month view added -----------------------------------------------

    [Fact]
    public void TheirCommitmentsBetweenReturnsOnlyTheirsInsideTheWindow()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);

        Promise(call, contact, byMe: false, "ödemeyi yapmak", today.AddDays(3));
        Promise(call, contact, byMe: true, "evrak göndermek", today.AddDays(3));       // mine
        Promise(call, contact, byMe: false, "koşullu iş", today.AddDays(3), true);     // conditional
        Promise(call, contact, byMe: false, "uzak iş", today.AddDays(90));             // outside

        var rows = _repo.TheirCommitmentsBetween(today, today.AddDays(41));

        var row = Assert.Single(rows);
        Assert.Equal("ödemeyi yapmak", row.Obligation);
        Assert.Equal("Uliana", row.ContactName);
        Assert.Equal(today.AddDays(3), row.Day);
        Assert.Equal(call, row.CallId);
    }

    [Fact]
    public void ActionsDueBetweenReturnsOnlyOpenDatedSuggestionsInsideTheWindow()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);

        Suggest(call, contact, "yazılı teyit iste", today.AddDays(3));
        Suggest(call, contact, "bitmiş iş", today.AddDays(3), ActionStatus.Done);      // closed
        Suggest(call, contact, "tarihsiz öneri", deadline: null);                      // undated
        Suggest(call, contact, "uzak öneri", today.AddDays(90));                       // outside

        var rows = _repo.ActionsDueBetween(today, today.AddDays(41));

        var row = Assert.Single(rows);
        Assert.Equal("yazılı teyit iste", row.Action);
        Assert.Equal("Uliana", row.ContactName);
        Assert.Equal(today.AddDays(3), row.Day);
        Assert.Equal(call, row.CallId);
    }

    // ---- the view model ---------------------------------------------------------------------

    [Fact]
    public void TheMonthPutsEveryKindOnItsDayWithSuggestionsLast()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var day = today.AddDays(3);

        _repo.PutOnBoard(call, BoardLane.ToLookAt, title: "Evrak sözü");
        _repo.RemindOn(call, day);
        Promise(call, contact, byMe: true, "evrak göndermek", day);
        Promise(call, contact, byMe: false, "ödemeyi yapmak", day);
        Suggest(call, contact, "yazılı teyit iste", day);

        var vm = new CalendarViewModel(_repo);
        vm.Refresh();

        Assert.Equal(42, vm.Days.Count);

        var cell = vm.Days.Single(d => d.Date == day);
        Assert.Equal(4, cell.Entries.Count);

        // Authority order inside the day: the user's reminder first, the machine's idea last.
        Assert.Equal(CalendarEntryKind.Reminder, cell.Entries[0].Kind);
        Assert.Equal(CalendarEntryKind.ActionSuggestion, cell.Entries[^1].Kind);

        // Three lines fit a cell; the fourth becomes the count.
        Assert.Equal(3, cell.Preview.Count);
        Assert.True(cell.HasMore);
        Assert.Equal("+1 daha", cell.MoreText);
    }

    [Fact]
    public void ArrivingOnTheCurrentMonthSelectsTodayAndListsItsAgenda()
    {
        var (call, contact) = Seed();
        var today = DateOnly.FromDateTime(DateTime.Today);

        Promise(call, contact, byMe: true, "bugünkü işim", today);

        var vm = new CalendarViewModel(_repo);
        vm.Refresh();

        Assert.True(vm.HasSelection);
        Assert.True(vm.Selected!.IsToday);
        Assert.False(vm.SelectedDayIsEmpty);

        var entry = Assert.Single(vm.Agenda);
        Assert.Equal("Sen: bugünkü işim", entry.Line);
        Assert.Equal(call, entry.CallId);
    }

    [Fact]
    public void ABareDaySaysSoInsteadOfRefusingTheClick()
    {
        var vm = new CalendarViewModel(_repo);
        vm.Refresh();

        var bare = vm.Days.First(d => !d.IsToday);
        vm.SelectDay(bare);

        Assert.True(bare.IsSelected);
        Assert.True(vm.SelectedDayIsEmpty);
        Assert.Empty(vm.Agenda);
    }
}
