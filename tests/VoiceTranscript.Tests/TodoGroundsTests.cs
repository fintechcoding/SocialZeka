using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// What the to-do page stands on.
///
/// Three sources share this list, and PLAN-SOSYALZEKA §3.1 says what each of them is allowed to
/// claim. A suggestion is the machine's, and the machine may only speak with a verbatim quote and
/// the millisecond it can be played from — that is why <c>action_item.quote</c> and
/// <c>action_item.quote_start_ms</c> are NOT NULL. A note is the user's own writing and has no
/// moment in any conversation; it must not borrow one. A reminder is neither: it is a column on a
/// board card, whose key is the call.
///
/// The page read none of that. It showed "Faturayı gönder · öneri Uliana" with no way to see what
/// was said, it could not attach a note to a person although the column has always been there,
/// and a reminder on a card with no title drew a row with nothing to read in it.
/// </summary>
public sealed class TodoGroundsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-todo-ground-{Guid.NewGuid():N}");
    private readonly Repository _repository;
    private readonly TodoViewModel _model;
    private readonly long _callId;
    private readonly long _contactId;

    public TodoGroundsTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var database = new Database(paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);

        _contactId = _repository.UpsertContact("Uliana", CallApp.WhatsApp);

        _callId = _repository.InsertCall(new Call
        {
            ContactId = _contactId,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddHours(-3),
            Duration = TimeSpan.FromMinutes(12),
            State = ProcessingState.Analysed,
        });

        _repository.AssignContact(_callId, _contactId);

        _model = new TodoViewModel(_repository);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The database file can still be held briefly.
        }

        GC.SuppressFinalize(this);
    }

    private IEnumerable<TodoEntry> Everything() =>
        _model.Overdue.Concat(_model.Today).Concat(_model.Upcoming).Concat(_model.Undated);

    // ---- A. the evidence ground -------------------------------------------

    /// <summary>
    /// Goes red when the to-do page stops reading the two columns that anchor a suggestion.
    ///
    /// Without them the row is the machine's assertion with nothing under it: the user is asked
    /// to act on "Faturayı gönder" and can only check it by opening the conversation and hunting
    /// for the sentence. Every claim in this product carries a verbatim quote and a playable
    /// millisecond, and this is the screen where that rule was entirely absent.
    /// </summary>
    [Fact]
    public void ASuggestionCarriesTheSentenceItRestsOnAndTheMomentItWasSaid()
    {
        _repository.InsertAction(new ActionItem
        {
            CallId = _callId,
            ContactId = _contactId,
            Action = "Faturayı gönder",
            Quote = "faturayı bu hafta yollarım sana",
            QuoteStartMs = 432_000,
            QuoteIsMe = true,
        });

        _model.Refresh();

        var row = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Action);

        Assert.True(row.HasQuote);
        Assert.Equal("faturayı bu hafta yollarım sana", row.Quote);
        Assert.Equal(432_000, row.QuoteStartMs);
        Assert.True(row.QuoteIsMe);

        // 432 000 ms is 7 minutes 12 seconds — the shape the rest of the product writes moments in.
        Assert.Equal("07:12", row.QuoteTimestamp);
    }

    /// <summary>
    /// Goes red when the moment on a suggestion row stops being playable.
    ///
    /// A quote nobody can hear is a transcription, and this product exists because the recording
    /// is the thing that settles an argument. ▸ has to reach the conversation at the second the
    /// sentence was said, on the side that said it.
    /// </summary>
    [Fact]
    public void TheMomentOnASuggestionCanBePlayed()
    {
        _repository.InsertAction(new ActionItem
        {
            CallId = _callId,
            ContactId = _contactId,
            Action = "Teslim tarihini yazılı teyit et",
            Quote = "cuma günü kesin hazır olur",
            QuoteStartMs = 65_000,
        });

        _model.Refresh();

        TodoEntry? asked = null;
        _model.PlayQuoteRequested += (_, entry) => asked = entry;

        var row = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Action);
        _model.PlayQuoteCommand.Execute(row);

        Assert.NotNull(asked);
        Assert.Equal(_callId, asked.CallId);
        Assert.Equal(65_000, asked.QuoteStartMs);
    }

    /// <summary>
    /// Goes red when a line the user typed starts claiming evidence it does not have.
    ///
    /// The three grounds may sit on one screen and never inside one card region without a visible
    /// boundary (§3.1). On this page the quoted bar IS that boundary: it is there on the
    /// machine's rows and absent on the user's. A note that grew a quote — borrowed from its
    /// call, invented, or copied from a neighbouring suggestion — would erase the distinction the
    /// whole page depends on.
    /// </summary>
    [Fact]
    public void ANoteTheUserWroteHasNoQuoteAndDoesNotClaimOne()
    {
        _repository.AddTodo("Kendi yazdığım bir şey", null, _contactId, _callId);

        _model.Refresh();

        var row = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Manual);

        Assert.Null(row.Quote);
        Assert.Null(row.QuoteStartMs);
        Assert.False(row.HasQuote);
        Assert.Equal("", row.QuoteTimestamp);

        // And ▸ does nothing, because there is nothing to play.
        var asked = 0;
        _model.PlayQuoteRequested += (_, _) => asked++;
        _model.PlayQuoteCommand.Execute(row);

        Assert.Equal(0, asked);
    }

    // ---- B. a to-do can be attached to a person ---------------------------

    /// <summary>
    /// Goes red when <c>Add()</c> stops passing the chosen person through.
    ///
    /// <c>todo.contact_id</c> and the join that fetches the name were both written long ago; what
    /// was missing was any way for the interface to fill the column, so every note the user typed
    /// reached the list with the person's half of the row blank.
    /// </summary>
    [Fact]
    public void ANoteSavedWithAPersonKeepsThemAndShowsTheirName()
    {
        _model.Refresh();

        _model.NewText = "Uliana'ya evrakları sor";
        _model.NewContact = _model.ContactChoices.Single(c => c.Id == _contactId);
        _model.AddCommand.Execute(null);

        var stored = Assert.Single(_repository.ListTodos());
        Assert.Equal(_contactId, stored.ContactId);

        var row = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Manual);
        Assert.Equal("Uliana", row.ContactName);
    }

    /// <summary>
    /// Goes red when the person stops being optional, or stops being cleared after an add.
    ///
    /// A note to self is a whole to-do; requiring a name would be asking for a fact to satisfy a
    /// schema. And a picker that stayed on the last person would quietly attach the next three
    /// notes to them, which is worse than no name at all.
    /// </summary>
    [Fact]
    public void ANoteWithNobodyOnItIsStillAValidNote()
    {
        _model.Refresh();

        _model.NewText = "Kendime not";
        _model.NewContact = _model.ContactChoices.Single(c => c.Id == _contactId);
        _model.AddCommand.Execute(null);

        Assert.Null(_model.NewContact?.Id);

        _model.NewText = "İkinci not";
        _model.AddCommand.Execute(null);

        var second = Assert.Single(_repository.ListTodos(), t => t.Text == "İkinci not");
        Assert.Null(second.ContactId);
    }

    // ---- C. the reminder is a column, not a row ---------------------------

    /// <summary>
    /// Goes red when a reminder on a card with no title becomes a blank row again.
    ///
    /// A board card's title is optional — the reminder is set on the conversation, not on a
    /// sentence — and the empty string went straight onto the page. What the user saw was a tick
    /// box, a date and nothing else: a line that cannot be acted on because it does not say what
    /// it is, in the one list whose whole job is to say what is left to do.
    /// </summary>
    [Fact]
    public void AReminderWithNoTitleIsNeverABlankRow()
    {
        _repository.PutOnBoard(_callId, BoardLane.Mine, title: null,
            remindOn: DateOnly.FromDateTime(DateTime.Today));

        _model.Refresh();

        var row = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Reminder);

        Assert.False(string.IsNullOrWhiteSpace(row.Text));
        Assert.Equal(Localisation.T("todopage.gorusme-hatirlatmasi"), row.Text);
    }

    /// <summary>
    /// Goes red when acting on a reminder can reach a note or a suggestion wearing the same number.
    ///
    /// The three sources number themselves independently and cannot be made to share a space: a
    /// reminder is not a row at all, it is <c>board_card.remind_on</c>, whose key is the CALL. So
    /// note 1, suggestion 1 and the reminder on conversation 1 are all on this page at once, all
    /// carrying the number 1. If a verb ever dispatches on the number instead of the source, one
    /// tick clears somebody else's work and nothing on screen says so.
    /// </summary>
    [Fact]
    public void TickingAReminderCannotTickANoteOrASuggestionWithTheSameNumber()
    {
        var todoId = _repository.AddTodo("Aynı numaralı not", DateOnly.FromDateTime(DateTime.Today));

        var actionId = _repository.InsertAction(new ActionItem
        {
            CallId = _callId,
            ContactId = _contactId,
            Action = "Aynı numaralı öneri",
            Quote = "onu da halledelim",
            QuoteStartMs = 1_000,
            DeadlineDate = DateOnly.FromDateTime(DateTime.Today),
        });

        _repository.PutOnBoard(_callId, BoardLane.Mine, title: "Aynı numaralı hatırlatma",
            remindOn: DateOnly.FromDateTime(DateTime.Today));

        // The premise: one number, three meanings, all three on screen together.
        Assert.Equal(_callId, todoId);
        Assert.Equal(_callId, actionId);

        _model.Refresh();

        var reminder = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Reminder);
        Assert.Equal(_callId, reminder.Id);

        _model.ToggleCommand.Execute(reminder);
        _model.Refresh();

        Assert.DoesNotContain(Everything(), e => e.Kind == TodoEntryKind.Reminder);

        Assert.Null(Assert.Single(_repository.ListTodos(includeDone: true), t => t.Id == todoId).DoneAt);
        Assert.Equal(ActionStatus.Open, Assert.Single(_repository.ActionsOf(_callId), a => a.Id == actionId).Status);
    }

    /// <summary>
    /// Goes red when the number can be read without naming which space it belongs to.
    ///
    /// This is the guard the test above rests on. <c>TodoEntry.Id</c> means three different
    /// things depending on the row, so every write path asks for it by the name of its own table
    /// — and asking the wrong one throws here rather than quietly updating a stranger's row.
    /// </summary>
    [Fact]
    public void ARowsNumberCannotBeReadAsAnotherSourcesNumber()
    {
        _repository.AddTodo("Not", null);

        _repository.InsertAction(new ActionItem
        {
            CallId = _callId,
            Action = "Öneri",
            Quote = "bir şey demiştik",
        });

        _repository.PutOnBoard(_callId, BoardLane.Mine, title: "Hatırlatma",
            remindOn: DateOnly.FromDateTime(DateTime.Today));

        _model.Refresh();

        var note = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Manual);
        var suggestion = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Action);
        var reminder = Assert.Single(Everything(), e => e.Kind == TodoEntryKind.Reminder);

        Assert.Equal(note.Id, note.TodoId);
        Assert.Equal(suggestion.Id, suggestion.ActionId);
        Assert.Equal(reminder.Id, reminder.ReminderCallId);

        Assert.Throws<InvalidOperationException>(() => reminder.TodoId);
        Assert.Throws<InvalidOperationException>(() => reminder.ActionId);
        Assert.Throws<InvalidOperationException>(() => note.ReminderCallId);
        Assert.Throws<InvalidOperationException>(() => suggestion.TodoId);
    }

    // ---- D. the filters wrap ----------------------------------------------

    /// <summary>
    /// Goes red when the filter chips go back into a single row.
    ///
    /// There are four of them and a narrow window pushed the last one off the edge, where a
    /// filter is not merely awkward to reach but invisible: the page then looks as though it has
    /// three filters and one of them is stuck on. PLAN-SOSYALZEKA §4.5 asks for a
    /// <c>WrapPanel</c> wherever chips are; this is the page that did not have one.
    ///
    /// A markup scan rather than a layout pass, the way the shell's wiring is checked: the rule
    /// is about which panel is used, and that is a fact about the file.
    /// </summary>
    [Fact]
    public void TheFilterChipsWrapRatherThanRunningOffTheEdge()
    {
        var markup = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "VoiceTranscript.App", "Views", "TodoPage.xaml"));

        var start = markup.IndexOf("ShowAllSourcesCommand", StringComparison.Ordinal);
        Assert.True(start > 0, "Süzgeç çipleri bulunamadı.");

        // The panel that opens immediately before the first chip is the one holding all four.
        var button = markup.LastIndexOf("<ui:Button", start, StringComparison.Ordinal);
        Assert.True(button > 0, "İlk çipin düğmesi bulunamadı.");

        var panel = markup.LastIndexOf('<', button - 1);

        Assert.StartsWith("<WrapPanel", markup[panel..], StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }
}
