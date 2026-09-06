using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

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
    private readonly AppPaths _paths;
    private readonly Repository _repository;
    private readonly TodoViewModel _model;
    private readonly long _callId;
    private readonly long _contactId;

    public SuggestionsOnTheTodoPageTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repository = new Repository(database);

        _contactId = _repository.UpsertContact("Samet", CallApp.WhatsApp);

        _callId = _repository.InsertCall(new Core.Domain.Call
        {
            ContactId = _contactId,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddHours(-2),
            Duration = TimeSpan.FromMinutes(4),
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
        WhileTheShellIsListening(() => _model.ToggleCommand.Execute(Everything().Single(e => e.Id == id)));

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
        WhileTheShellIsListening(() => _model.ToggleCommand.Execute(Everything().Single(e => e.Id == id)));
        WhileTheShellIsListening(() => _model.ToggleCommand.Execute(_model.Done.Single(e => e.Id == id)));

        Assert.Contains(Everything(), e => e.Id == id);
        Assert.Equal(ActionStatus.Open, _repository.ActionsOf(_callId).Single().Status);
    }

    /// <summary>The finished section is shown only when it is open; nothing else changes.</summary>
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

    /// <summary>
    /// The count is known while the section is closed: the checkbox says "Bitenler (1)", which
    /// is how somebody sees that ticking left a trace without opening the section.
    /// </summary>
    [Fact]
    public void TheFinishedCountIsKnownWhileTheSectionIsClosed()
    {
        var id = Suggest("Fotoğraf kağıdı al");
        _repository.SetActionStatus(id, ActionStatus.Done);

        _model.Refresh();

        Assert.Empty(_model.Done);
        Assert.Equal(1, _model.DoneCount);
        Assert.Contains("(1)", _model.ShowDoneText);
    }

    /// <summary>The section starts the way it was left, from the saved setting.</summary>
    [Fact]
    public void TheFinishedSectionStartsOpenWhenItWasLeftOpen()
    {
        var id = Suggest("Siteyi aç");
        _repository.SetActionStatus(id, ActionStatus.Done);

        var model = new TodoViewModel(_repository, showDone: true);
        model.Refresh();

        Assert.True(model.ShowDone);
        Assert.Single(model.Done);
    }

    /// <summary>The refusal is named as what it is. "Gizlendi" said the row was merely out of sight.</summary>
    [Fact]
    public void RefusingASuggestionSaysRefused()
    {
        var id = Suggest("Reddedilecek");

        _model.Refresh();
        _model.DismissCommand.Execute(Everything().Single(e => e.Id == id));

        Assert.Equal(string.Format(Localisation.T("todopage.reddedildi-n"), "Reddedilecek"), _model.Notice);
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

    // ---- complaint 2's own measure: "Yaptım" somewhere, and the list is current here ----------
    //
    // The complaint was not that ticking failed to write. It wrote. It was that the Yapılacaklar
    // list went on showing the suggestion as open until something unrelated happened to refresh
    // it — the same suggestion, in two places, disagreeing about whether it was done.
    //
    // The fix is four lines: three announcements (the call window, the contacts page and the home
    // screen each tell CallActions after writing a verdict) and one listener (the shell re-reads
    // the to-do list on that announcement). Any one of them deleted and the complaint is back,
    // in one surface, silently. These tests are what makes that noisy.

    /// <summary>Runs the ruling with the to-do list wired the way the shell wires it.</summary>
    private void WhileTheShellIsListening(Action ruling)
    {
        void Refresh(object? sender, EventArgs e) => _model.Refresh();

        VoiceTranscript.App.Services.CallActions.Changed += Refresh;

        try
        {
            ruling();
        }
        finally
        {
            VoiceTranscript.App.Services.CallActions.Changed -= Refresh;
        }
    }

    /// <summary>
    /// Ticked on the contacts page, and the to-do list is current at the same moment.
    ///
    /// Red means <c>ContactsViewModel.SetCallActionStatus</c> has stopped announcing. The row
    /// would still be written — the assertion on the repository would pass on its own — and the
    /// page the user is not looking at would go on offering the job they have just done.
    /// </summary>
    [Fact]
    public void TickingASuggestionOnTheContactsPageUpdatesTheTodoList()
    {
        var id = Suggest("Sözleşmeyi gönder");

        _model.Refresh();
        Assert.Contains(Everything(), e => e.Id == id);

        using var contacts = new ContactsViewModel(_repository);
        contacts.Refresh();
        contacts.Select(_contactId, _callId);

        var row = contacts.CallActions.Single(r => r.Item.Id == id);

        WhileTheShellIsListening(() => contacts.SetCallActionStatus(row, ActionStatus.Done));

        Assert.DoesNotContain(Everything(), e => e.Id == id);
        Assert.Equal(ActionStatus.Done, _repository.ActionsOf(_callId).Single().Status);
    }

    /// <summary>
    /// Ticked on the home screen, same measure.
    ///
    /// Red means <c>OverviewViewModel.SetDayActionStatus</c> has stopped announcing — the surface
    /// the complaint was actually made about, because the home screen is where a suggestion is
    /// met first and the to-do page is where it is looked for afterwards.
    /// </summary>
    [Fact]
    public void TickingASuggestionOnTheHomeScreenUpdatesTheTodoList()
    {
        var id = Suggest("Faturayı bulup gönder");

        _model.Refresh();
        Assert.Contains(Everything(), e => e.Id == id);

        var overview = new OverviewViewModel(_repository, () => new AppSettings(), _paths);
        overview.Refresh();

        var row = overview.DayActions.Single(d => d.Item.Id == id);

        WhileTheShellIsListening(() => overview.SetDayActionStatus(row, ActionStatus.Done));

        Assert.DoesNotContain(Everything(), e => e.Id == id);
    }

    /// <summary>
    /// Ticked in the conversation window, same measure.
    ///
    /// Red means <c>CallWindowViewModel.SetActionStatus</c> has stopped announcing. This is the
    /// surface where a suggestion is most often ruled on, because it is where the sentence it
    /// came from is on screen.
    /// </summary>
    [Fact]
    public void TickingASuggestionInTheCallWindowUpdatesTheTodoList()
    {
        var id = Suggest("Proxyyi dene");

        _model.Refresh();
        Assert.Contains(Everything(), e => e.Id == id);

        using var http = new HttpClient();
        var window = new CallWindowViewModel(_repository, () => new AppSettings(), http, _callId);

        var row = window.Actions.Single(a => a.Item.Id == id);

        WhileTheShellIsListening(() => window.SetActionStatus(row, ActionStatus.Done));

        Assert.DoesNotContain(Everything(), e => e.Id == id);
    }

    /// <summary>
    /// And the shell really is the listener the three tests above stand in for.
    ///
    /// The other half of the fix cannot be reached from here: <see cref="ShellViewModel"/> takes
    /// a <c>CallOrchestrator</c>, which opens capture devices and a Python worker. So the wiring
    /// is read out of the source instead — the subscription that turns an announcement into a
    /// refresh, and the place that knows how to re-read the to-do list.
    ///
    /// Red means the announcements are being made and nothing is listening, which looks exactly
    /// like the complaint that started this: a verdict written, and a list that does not move.
    ///
    /// A source scan, not a behavioural test. It once read the line inside <c>RefreshAll</c>,
    /// back when that method re-read all ten pages in a row; the to-do line had been missing from
    /// it precisely because the page was not the one on screen. RefreshAll now re-reads the
    /// visible page and marks the rest, so the mapping lives in <c>Reload</c> and the scan reads
    /// it there. That the marked pages really are re-read on arrival is
    /// <see cref="ShellRefreshTests"/>'s job, not this one's.
    /// </summary>
    [Fact]
    public void TheShellRefreshesTheTodoListWhenASuggestionIsRuledOn()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "VoiceTranscript.App", "ViewModels", "ShellViewModel.cs"));

        var subscription = source
            .Split('\n')
            .SingleOrDefault(line => line.Contains("CallActions.Changed", StringComparison.Ordinal));

        Assert.NotNull(subscription);
        Assert.Contains("RefreshAll", subscription, StringComparison.Ordinal);

        var start = source.IndexOf("private void Reload(ShellPage page)", StringComparison.Ordinal);
        var end = source.IndexOf("private void Touch(ShellPage page)", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, "Reload gövdesi bulunamadı.");
        Assert.Contains("Todo.Refresh()", source[start..end], StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
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
