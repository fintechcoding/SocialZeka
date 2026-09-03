using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// "Yaptım" has to leave a trace.
///
/// The suggested actions are one of the few things in this product that produce work rather than
/// record it, and the only thing anyone could do with one was make it vanish: ticking it took it
/// off the first screen, off the to-do list, and put it nowhere — not even under "Bitenler",
/// which is the one place somebody looks to check whether they really did it. A list that can
/// only lose items teaches people not to tick anything, and then the feature is decoration.
/// </summary>
public class SuggestionsOnTheTodoPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-todo-{Guid.NewGuid():N}");
    private readonly Repository _repository;
    private readonly TodoViewModel _model;
    private readonly long _callId;

    public SuggestionsOnTheTodoPageTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var database = new Database(paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);

        var contact = _repository.UpsertContact("Samet", CallApp.WhatsApp);

        _callId = _repository.InsertCall(new Core.Domain.Call
        {
            ContactId = contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddHours(-2),
            Duration = TimeSpan.FromMinutes(4),
            State = ProcessingState.Analysed,
        });

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

    private long Suggest(string action) =>
        _repository.InsertAction(new ActionItem
        {
            CallId = _callId,
            Action = action,
            Quote = "bunu konuşmuştuk",
        });

    private IEnumerable<TodoEntry> Everything() =>
        _model.Overdue.Concat(_model.Today).Concat(_model.Upcoming).Concat(_model.Undated);

    [Fact]
    public void ASuggestionTickedOffTurnsUpUnderTheFinishedOnes()
    {
        var id = Suggest("Siteyi bugün aç");

        _model.ShowDone = true;
        _model.ToggleCommand.Execute(Everything().Single(e => e.Id == id));

        Assert.DoesNotContain(Everything(), e => e.Id == id);
        Assert.Contains(_model.Done, e => e.Kind == TodoEntryKind.Action && e.Id == id);
    }

    /// <summary>
    /// And it can be brought back. A suggestion ticked by mistake used to be unreachable: it left
    /// the list, so there was no row left to untick.
    /// </summary>
    [Fact]
    public void TickingAFinishedSuggestionAgainReopensIt()
    {
        var id = Suggest("Proxyyi dene");

        _model.ShowDone = true;
        _model.ToggleCommand.Execute(Everything().Single(e => e.Id == id));
        _model.ToggleCommand.Execute(_model.Done.Single(e => e.Id == id));

        Assert.Contains(Everything(), e => e.Id == id);
        Assert.Equal(ActionStatus.Open, _repository.ActionsOf(_callId).Single().Status);
    }

    /// <summary>The finished section is read only when it is open; nothing else changes.</summary>
    [Fact]
    public void FinishedSuggestionsStayOutOfSightUntilAskedFor()
    {
        var id = Suggest("Fotoğraf kağıdı al");
        _repository.SetActionStatus(id, ActionStatus.Done);

        _model.Refresh();
        Assert.Empty(_model.Done);

        _model.ShowDone = true;
        Assert.Single(_model.Done);
    }

    /// <summary>A hidden suggestion is hidden. It is not finished, and it is not waiting.</summary>
    [Fact]
    public void AHiddenSuggestionIsNeitherWaitingNorFinished()
    {
        var id = Suggest("Gizlenecek");
        _repository.SetActionStatus(id, ActionStatus.Hidden);

        _model.ShowDone = true;

        Assert.DoesNotContain(Everything(), e => e.Id == id);
        Assert.DoesNotContain(_model.Done, e => e.Id == id);
    }

    [Fact]
    public void TheSuggestionsCanBeLookedAtOnTheirOwn()
    {
        Suggest("Faturayı bulup gönder");
        _repository.AddTodo("Kendi yazdığım", null);

        _model.Source = TodoSource.Suggestions;
        Assert.All(Everything(), e => Assert.Equal(TodoEntryKind.Action, e.Kind));
        Assert.Single(Everything());

        _model.Source = TodoSource.Mine;
        Assert.All(Everything(), e => Assert.NotEqual(TodoEntryKind.Action, e.Kind));
        Assert.Single(Everything());

        _model.Source = TodoSource.All;
        Assert.Equal(2, Everything().Count());
    }
}
