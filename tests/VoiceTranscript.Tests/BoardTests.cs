using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The board: conversations set aside to come back to.
///
/// The one part of this archive a person arranges themselves. Everything else on screen was
/// produced by a machine from the audio, which is why the rules here are about not letting it
/// drift into being something else — a card is always a conversation, and the lanes are fixed.
/// </summary>
public sealed class BoardTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-board-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public BoardTests()
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

    private long Call() => _repo.InsertCall(new Call
    {
        App = CallApp.WhatsApp,
        StartedAt = DateTimeOffset.Parse("2026-08-31T10:00:00+03:00"),
        State = ProcessingState.Analysed,
    });

    [Fact]
    public void AConversationCanBePutOnTheBoardAndComesBack()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);

        var card = Assert.Single(_repo.BoardCards());

        Assert.Equal(call, card.CallId);
        Assert.Equal(BoardLane.Mine, card.Lane);
    }

    /// <summary>
    /// One card per conversation. Two would mean the same thing sitting in two lanes with no way
    /// to say which is true.
    /// </summary>
    [Fact]
    public void PuttingTheSameConversationOnTwiceMovesItRatherThanDuplicating()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.ToLookAt);
        _repo.PutOnBoard(call, BoardLane.Theirs);

        var card = Assert.Single(_repo.BoardCards());

        Assert.Equal(BoardLane.Theirs, card.Lane);
    }

    /// <summary>
    /// New cards go to the end of their lane. Anywhere else and adding one silently reorders work
    /// somebody had already arranged.
    /// </summary>
    [Fact]
    public void NewCardsGoToTheEndOfTheirLane()
    {
        var first = Call();
        var second = Call();
        var third = Call();

        _repo.PutOnBoard(first, BoardLane.Mine);
        _repo.PutOnBoard(second, BoardLane.Mine);
        _repo.PutOnBoard(third, BoardLane.Mine);

        var order = _repo.BoardCards()
            .Where(c => c.Lane == BoardLane.Mine)
            .OrderBy(c => c.Position)
            .Select(c => c.CallId)
            .ToList();

        Assert.Equal([first, second, third], order);
    }

    /// <summary>An unknown lane lands in the default rather than creating a column nobody can see.</summary>
    [Fact]
    public void AnUnknownLaneFallsBackToTheDefault()
    {
        var call = Call();

        _repo.PutOnBoard(call, "uydurma-serit");

        Assert.Equal(BoardLane.ToLookAt, Assert.Single(_repo.BoardCards()).Lane);
    }

    /// <summary>Taking a card off the board leaves the conversation alone. Nothing here deletes audio.</summary>
    [Fact]
    public void RemovingACardKeepsTheConversation()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.RemoveFromBoard(call);

        Assert.Empty(_repo.BoardCards());
        Assert.NotNull(_repo.GetCall(call));
    }

    /// <summary>A card about a deleted conversation is a card about nothing.</summary>
    [Fact]
    public void DeletingTheConversationTakesItsCardWithIt()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.DeleteCall(call);

        Assert.Empty(_repo.BoardCards());
    }

    // ---- reminders -----------------------------------------------------------

    [Fact]
    public void AReminderSetForTodayIsDue()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.RemindOn(call, DateOnly.FromDateTime(DateTime.Now));

        Assert.Single(_repo.DueCards());
    }

    [Fact]
    public void AReminderInTheFutureIsNotDueYet()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.RemindOn(call, DateOnly.FromDateTime(DateTime.Now).AddDays(3));

        Assert.Empty(_repo.DueCards());
    }

    /// <summary>
    /// A reminder that has passed still shows. Silently dropping it would be the application
    /// deciding something the user asked to be reminded of no longer matters.
    /// </summary>
    [Fact]
    public void AReminderThatHasPassedIsStillDue()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.RemindOn(call, DateOnly.FromDateTime(DateTime.Now).AddDays(-5));

        Assert.Single(_repo.DueCards());
    }

    /// <summary>Finished work does not chase you. A card in "Kapandı" stops reminding.</summary>
    [Fact]
    public void AClosedCardStopsReminding()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.RemindOn(call, DateOnly.FromDateTime(DateTime.Now));

        Assert.Single(_repo.DueCards());

        _repo.PutOnBoard(call, BoardLane.Done);

        Assert.Empty(_repo.DueCards());
    }

    [Fact]
    public void AReminderCanBeCleared()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.Mine);
        _repo.RemindOn(call, DateOnly.FromDateTime(DateTime.Now));
        _repo.RemindOn(call, null);

        Assert.Empty(_repo.DueCards());
    }

    /// <summary>
    /// Moving a card between lanes must not lose its reminder — the reminder is why it is on the
    /// board, and the lane is only where it currently sits.
    /// </summary>
    [Fact]
    public void MovingACardKeepsItsReminder()
    {
        var call = Call();

        _repo.PutOnBoard(call, BoardLane.ToLookAt);
        _repo.RemindOn(call, DateOnly.FromDateTime(DateTime.Now).AddDays(2));
        _repo.PutOnBoard(call, BoardLane.Mine);

        Assert.NotNull(Assert.Single(_repo.BoardCards()).RemindOn);
    }

    // ---- the strip on the first screen ---------------------------------------

    [Fact]
    public void CountsAreReportedPerLane()
    {
        _repo.PutOnBoard(Call(), BoardLane.Mine);
        _repo.PutOnBoard(Call(), BoardLane.Mine);
        _repo.PutOnBoard(Call(), BoardLane.Theirs);

        var counts = _repo.BoardCounts();

        Assert.Equal(2, counts[BoardLane.Mine]);
        Assert.Equal(1, counts[BoardLane.Theirs]);
        Assert.False(counts.ContainsKey(BoardLane.Done));
    }

    /// <summary>Every lane has a name and a sentence for when it is empty. All four are empty on a new board.</summary>
    [Fact]
    public void EveryLaneExplainsItselfWhenEmpty()
    {
        Assert.Equal(4, BoardLane.All.Count);

        Assert.All(BoardLane.All, lane =>
        {
            Assert.False(string.IsNullOrWhiteSpace(BoardLane.NameOf(lane)));
            Assert.False(string.IsNullOrWhiteSpace(BoardLane.EmptyText(lane)));
            Assert.EndsWith(".", BoardLane.EmptyText(lane));
        });
    }
}
