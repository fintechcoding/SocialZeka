using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Keeping what was asked and what came back.
///
/// The defect these are written against was quiet and expensive: both Sor surfaces answered a
/// question, paid for the request, and dropped the answer into an in-memory list that died with
/// the window. Asking the same thing the next day was a second bill for the same paragraph, and
/// the quotes underneath — the only reason the paragraph is allowed on screen at all — went with
/// it.
///
/// So these pin four properties. An answer survives with its evidence intact and still playable.
/// Reopening a surface costs nothing. A question asked of the whole archive belongs to no call and
/// is not lost when one is deleted. And a re-transcription says the answer is old rather than
/// throwing away something that was paid for.
/// </summary>
public sealed class AskHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-sor-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly Repository _repo;
    private readonly HttpClient _http = new();

    public AskHistoryTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var database = new Database(_paths.DatabaseFile);
        database.Migrate();

        _repo = new Repository(database);
    }

    public void Dispose()
    {
        _http.Dispose();
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

    /// <summary>An LLM that fails the test if it is reached at all.</summary>
    private sealed class ForbiddenLlm : ILlmClient
    {
        public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Kayıtlı cevapları göstermek için modele istek atıldı — bu ekran bedava açılmalı.");

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>An LLM that answers with whatever it was handed.</summary>
    private sealed class ScriptedLlm(string reply) : ILlmClient
    {
        public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse(reply, "stop", 10, 10));
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private long Seed(string contactName, string engine, params (bool IsMe, string Text)[] lines)
    {
        var contact = _repo.UpsertContact(contactName, CallApp.Telegram);

        var callId = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.Telegram,
            StartedAt = DateTimeOffset.Now.AddDays(-1),
            Duration = TimeSpan.FromMinutes(3),
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(callId, contact);
        Transcribe(callId, engine, lines);

        return callId;
    }

    private void Transcribe(long callId, string engine, params (bool IsMe, string Text)[] lines)
    {
        var segments = lines.Select((l, i) => new Segment
        {
            CallId = callId,
            IsMe = l.IsMe,
            StartMs = i * 5000,
            EndMs = i * 5000 + 4000,
            Text = l.Text,
        }).ToList();

        _repo.ReplaceSegments(callId, segments);
        _repo.SaveTranscriptVersion(callId, engine, 0.95, segments);
    }

    private static Excerpt Quote(long callId, int number, int startMs, string text) =>
        new(number, callId, "Ahmet", DateTimeOffset.Parse("2026-03-04T09:30:00+03:00"), startMs, IsMe: false, text);

    private long Store(long? callId, long? contactId, string question, params Excerpt[] quotes) =>
        _repo.SaveAskExchange(
            callId, contactId, question, "Fiyat on sekiz bin lira.",
            StoredExcerpts.Write(quotes), insufficient: false, "test-model");

    private CallWindowViewModel Window(long callId)
    {
        var settings = new AppSettings();
        return new CallWindowViewModel(_repo, () => settings, _http, callId);
    }

    private AskViewModel Page(ILlmClient llm)
    {
        var settings = new AppSettings();

        return new AskViewModel(
            _http, _repo, () => settings, _ => new ArchiveQuestions(llm, _repo));
    }

    // ---- the evidence survives the restart ----------------------------------

    /// <summary>
    /// Goes red when a stored answer comes back without the quotes it was built from, or with
    /// quotes missing the call and the millisecond they were spoken at.
    ///
    /// That is not a cosmetic loss. The answer would still render, still read as confident, and
    /// no longer be checkable against the recording — which is the whole difference between this
    /// product and a chatbot with an opinion about somebody's conversations.
    /// </summary>
    [Fact]
    public void AnAnsweredQuestionComesBackWithEveryQuoteItCited()
    {
        var call = Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin lira olur."));

        Store(call, contactId: null, "fiyat ne oldu",
            Quote(call, 1, 5000, "Fiyat on sekiz bin lira olur."),
            Quote(call, 2, 41000, "Geçen sefer on dört buçuk demiştin."));

        var stored = Assert.Single(_repo.AskExchangesOf(call));
        var quotes = StoredExcerpts.Read(stored.Citations);

        Assert.Equal("fiyat ne oldu", stored.Question);
        Assert.Equal("test-model", stored.ModelUsed);
        Assert.Equal(2, quotes.Count);

        Assert.Equal(1, quotes[0].Number);
        Assert.Equal(call, quotes[0].CallId);
        Assert.Equal(5000, quotes[0].StartMs);
        Assert.Equal("Ahmet", quotes[0].ContactName);
        Assert.False(quotes[0].IsMe);
        Assert.Equal("Fiyat on sekiz bin lira olur.", quotes[0].Text);

        // The moment is what makes a restored citation still playable.
        Assert.Equal(41000, quotes[1].StartMs);
    }

    /// <summary>
    /// Goes red when a stored answer's quotes stop reaching the panel — the citations would be in
    /// the database and not on screen, which is the same failure one layer up.
    /// </summary>
    [Fact]
    public void TheCallWindowPutsTheStoredQuotesBackOnScreen()
    {
        var call = Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin lira olur."));

        Store(call, contactId: null, "fiyat ne oldu", Quote(call, 1, 5000, "Fiyat on sekiz bin lira olur."));

        using var window = Window(call);

        var answer = window.Conversation.Single(m => !m.FromUser);

        Assert.True(answer.HasCitations);
        Assert.Equal(5000, answer.Citations[0].StartMs);
        Assert.Equal(call, answer.Citations[0].CallId);
    }

    // ---- reopening a surface costs nothing ----------------------------------

    /// <summary>
    /// Goes red the moment opening a conversation's Sor tab asks a model anything.
    ///
    /// A model request cannot happen without a row in processing_run — that is the guarantee the
    /// usage screen is built on — so a run recorded here means somebody made reading the history
    /// cost money, which is the defect this whole change exists to end.
    /// </summary>
    [Fact]
    public void ReopeningTheCallWindowShowsWhatWasAskedWithoutSpendingAnything()
    {
        var call = Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin lira olur."));

        Store(call, contactId: null, "fiyat ne oldu", Quote(call, 1, 5000, "Fiyat on sekiz bin lira olur."));

        using var window = Window(call);

        Assert.Equal(2, window.Conversation.Count);
        Assert.Equal("fiyat ne oldu", window.Conversation[0].Text);
        Assert.True(window.Conversation[0].FromUser);
        Assert.False(window.Conversation[1].FromUser);

        // Signed and dated, like every other surface that shows a model's reading.
        Assert.NotNull(window.Conversation[1].Stamp);
        Assert.Contains("test-model", window.Conversation[1].Stamp!);

        Assert.Equal(0, _repo.Usage(ProcessingStage.Ask).Runs);
    }

    /// <summary>
    /// Goes red when opening the shell's Sor page reaches for a model — the client it is handed
    /// here throws on the first request, so any re-ask on open fails the test outright.
    /// </summary>
    [Fact]
    public void ReopeningTheAskPageShowsWhatWasAskedWithoutReachingAModel()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);

        Store(callId: null, contact, "geçen ay fiyat ne oldu", Quote(0, 1, 5000, "On sekiz bin."));

        var page = Page(new ForbiddenLlm());

        var exchange = Assert.Single(page.Exchanges);

        Assert.Equal("geçen ay fiyat ne oldu", exchange.Question);
        Assert.True(exchange.HasCitations);
        Assert.Contains("test-model", exchange.Stamp);

        // The scope the question was asked under travels with it, so the answer is not read as
        // being about everybody.
        Assert.NotNull(exchange.Scope);
        Assert.Contains("Ahmet", exchange.Scope!);

        Assert.True(page.HasHistory);
        Assert.False(page.ShowSuggestions);
    }

    // ---- a question that belongs to no conversation --------------------------

    /// <summary>
    /// Goes red if call_id ever becomes NOT NULL, or if deleting a conversation takes archive-wide
    /// questions with it.
    ///
    /// The shell's Sor ranges over everything and its answers belong to no single call. Filed
    /// under one anyway they would be destroyed by an unrelated deletion; refused a home they
    /// could not be stored at all, which is the defect this change is fixing.
    /// </summary>
    [Fact]
    public void AnArchiveWideAnswerBelongsToNoCallAndOutlivesEveryOne()
    {
        var call = Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin lira olur."));

        Store(callId: null, contactId: null, "arşivde fiyat ne oldu", Quote(call, 1, 5000, "On sekiz bin."));
        Store(call, contactId: null, "bu görüşmede fiyat ne oldu", Quote(call, 1, 5000, "On sekiz bin."));

        Assert.Single(_repo.ArchiveAskExchanges());
        Assert.Null(_repo.ArchiveAskExchanges()[0].CallId);

        // The call window's own question is not listed on the archive-wide screen: an answer about
        // one conversation under a page that ranges over all of them is a different claim.
        Assert.DoesNotContain(_repo.ArchiveAskExchanges(), e => e.Question.StartsWith("bu görüşmede"));

        _repo.DeleteCall(call);

        var survivor = Assert.Single(_repo.ArchiveAskExchanges());
        Assert.Equal("arşivde fiyat ne oldu", survivor.Question);
    }

    /// <summary>
    /// Goes red when deleting a conversation leaves its questions behind pointing at nothing, or
    /// takes another conversation's questions with it.
    /// </summary>
    [Fact]
    public void DeletingAConversationTakesItsOwnQuestionsAndNobodyElses()
    {
        var mine = Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin."));
        var other = Seed("Berk", "nova-3", (false, "Pazar maça gidelim."));

        Store(mine, contactId: null, "fiyat", Quote(mine, 1, 0, "Fiyat on sekiz bin."));
        Store(other, contactId: null, "maç", Quote(other, 1, 0, "Pazar maça gidelim."));

        _repo.DeleteCall(mine);

        Assert.Empty(_repo.AskExchangesOf(mine));

        var kept = Assert.Single(_repo.AskExchangesOf(other));
        Assert.Equal("maç", kept.Question);
    }

    // ---- the user's own material, removable ---------------------------------

    /// <summary>
    /// Goes red when [Kaldır] removes more than the exchange it was pressed on, or removes
    /// nothing. This is the user's own material — a question they asked and an answer they no
    /// longer want kept — and losing the wrong one is losing something they paid for.
    /// </summary>
    [Fact]
    public void RemovingOneExchangeLeavesTheRestStanding()
    {
        var call = Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin."));

        Store(call, contactId: null, "ilk soru", Quote(call, 1, 0, "Fiyat on sekiz bin."));
        Store(call, contactId: null, "ikinci soru", Quote(call, 1, 0, "Fiyat on sekiz bin."));
        Store(call, contactId: null, "üçüncü soru", Quote(call, 1, 0, "Fiyat on sekiz bin."));

        using var window = Window(call);

        // Question, answer, question, answer, question, answer — in the order they were asked.
        Assert.Equal(6, window.Conversation.Count);
        Assert.Equal("ikinci soru", window.Conversation[2].Text);

        window.RemoveExchangeCommand.Execute(window.Conversation[3]);

        // Both halves of that one exchange are gone; the other two are untouched.
        Assert.Equal(4, window.Conversation.Count);
        Assert.DoesNotContain(window.Conversation, m => m.Text == "ikinci soru");
        Assert.Contains(window.Conversation, m => m.Text == "ilk soru");
        Assert.Contains(window.Conversation, m => m.Text == "üçüncü soru");

        Assert.Equal(2, _repo.AskExchangesOf(call).Count);
    }

    /// <summary>Goes red when the shell page's [Kaldır] does not reach the archive.</summary>
    [Fact]
    public void TheAskPageRemovesOneExchangeAndKeepsTheRest()
    {
        Store(callId: null, contactId: null, "ilk soru", Quote(0, 1, 0, "On sekiz bin."));
        Store(callId: null, contactId: null, "ikinci soru", Quote(0, 1, 0, "On sekiz bin."));

        var page = Page(new ForbiddenLlm());
        Assert.Equal(2, page.Exchanges.Count);

        page.RemoveCommand.Execute(page.Exchanges.First(e => e.Question == "ilk soru"));

        var kept = Assert.Single(page.Exchanges);
        Assert.Equal("ikinci soru", kept.Question);
        Assert.Single(_repo.ArchiveAskExchanges());
    }

    // ---- staleness ----------------------------------------------------------

    /// <summary>
    /// Goes red when transcribing a conversation again silently deletes the answers written from
    /// the old text, or leaves them standing with nothing saying so.
    ///
    /// Both are wrong in the same way the reading and the assessment were (complaint 7): the
    /// answer was paid for, so it is not thrown away, and its quotes came out of a text the screen
    /// no longer shows, so it is not passed off as an answer about the one that is there.
    /// </summary>
    [Fact]
    public void ARetranscriptionMarksAStoredAnswerStaleRatherThanDeletingIt()
    {
        var call = Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin lira olur."));

        Store(call, contactId: null, "fiyat ne oldu", Quote(call, 1, 5000, "Fiyat on sekiz bin lira olur."));

        using (var fresh = Window(call))
        {
            Assert.False(fresh.Conversation.Single(m => !m.FromUser).IsStale);
        }

        Transcribe(call, "large-v3", (false, "Fiyat on dokuz bin lira olur."));

        using var stale = Window(call);

        var answer = stale.Conversation.Single(m => !m.FromUser);

        Assert.True(answer.IsStale);

        // Still there, and still saying what it said. A warning, not a deletion.
        Assert.Equal("Fiyat on sekiz bin lira.", answer.Text);
        Assert.True(answer.HasCitations);
        Assert.Single(_repo.AskExchangesOf(call));
    }

    /// <summary>
    /// Goes red when an answer written from an unknown transcript is called stale.
    ///
    /// A row from before the pointer existed knows nothing about which text it read, and a wrong
    /// "bayat" on it teaches the reader to ignore the warning where it is real.
    /// </summary>
    [Fact]
    public void AnAnswerWhoseTranscriptIsUnknownIsNotCalledStale()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);

        // A call that was never transcribed: the exchange is stored with no version pointer.
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            App = CallApp.Telegram,
            StartedAt = DateTimeOffset.Now.AddDays(-1),
            State = ProcessingState.Analysed,
        });

        Store(call, contact, "fiyat ne oldu", Quote(call, 1, 0, "On sekiz bin."));

        Assert.Null(_repo.AskExchangesOf(call)[0].TranscriptVersionId);

        using var window = Window(call);
        Assert.False(window.Conversation.Single(m => !m.FromUser).IsStale);
    }

    // ---- what gets written down, and what does not --------------------------

    /// <summary>
    /// Goes red when asking through the shell page stops writing the answer down — the original
    /// defect, at the surface it was reported on.
    /// </summary>
    [Fact]
    public async Task AskingThroughThePageWritesTheAnswerDownWithItsQuotes()
    {
        Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin lira olur ancak."));

        var page = Page(new ScriptedLlm("""{"cevap":"On sekiz bin.","dayanaklar":[1],"yetersiz":false}"""));
        page.Question = "fiyat ne oldu";

        await page.AskCommand.ExecuteAsync(null);

        var stored = Assert.Single(_repo.ArchiveAskExchanges());

        Assert.Equal("fiyat ne oldu", stored.Question);
        Assert.Equal("On sekiz bin.", stored.Answer);
        Assert.NotEmpty(StoredExcerpts.Read(stored.Citations));

        // And it is on screen from the archive rather than from a field the next question clears.
        var shown = Assert.Single(page.Exchanges);
        Assert.Equal("On sekiz bin.", shown.Answer);
        Assert.True(shown.HasCitations);
    }

    /// <summary>
    /// Goes red when an answer the model could not ground gets written down.
    ///
    /// Such an answer is refused on screen — it has nothing to point at — and storing it would put
    /// it back tomorrow with a signature under it, as though it had been shown all along.
    /// </summary>
    [Fact]
    public async Task AnAnswerTheModelGroundedInNothingIsNotKept()
    {
        Seed("Ahmet", "nova-3", (false, "Fiyat on sekiz bin lira olur ancak."));

        var page = Page(new ScriptedLlm("""{"cevap":"On sekiz bin.","dayanaklar":[],"yetersiz":false}"""));
        page.Question = "fiyat ne oldu";

        await page.AskCommand.ExecuteAsync(null);

        Assert.NotNull(page.Problem);
        Assert.Empty(_repo.ArchiveAskExchanges());
        Assert.Empty(page.Exchanges);
    }

    /// <summary>
    /// Goes red when a question that found no conversation is written down.
    ///
    /// No model was asked, so nothing was paid for and there is nothing to save re-spending; a
    /// stored panel of rows saying "nothing was found" is worse than an empty one.
    /// </summary>
    [Fact]
    public async Task AQuestionThatFoundNoConversationIsNotWrittenDown()
    {
        var llm = new ScriptedLlm("{}");
        var page = Page(llm);
        page.Question = "zeplin sigortası";

        await page.AskCommand.ExecuteAsync(null);

        Assert.Equal(0, llm.Calls);
        Assert.Empty(_repo.ArchiveAskExchanges());
    }
}
