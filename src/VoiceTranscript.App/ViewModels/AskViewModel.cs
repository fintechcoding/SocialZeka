using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One excerpt as the screen shows it: the words, who said them, and where to hear them.</summary>
public sealed record CitationView(Excerpt Excerpt)
{
    public string Text => Excerpt.Text;

    public string Who => SpeakerText.For(Excerpt.IsMe, Excerpt.ContactName);

    public bool IsMe => Excerpt.IsMe;

    public string When => Excerpt.CallStartedAt.ToLocalTime().ToString("d MMMM yyyy HH:mm");

    public string At => $"{Excerpt.StartMs / 60000:00}:{Excerpt.StartMs / 1000 % 60:00}";

    public long CallId => Excerpt.CallId;
}

/// <summary>
/// One question that was asked of the archive and the answer that came back, as the page shows it.
///
/// The answer is signed and dated on its own line — the ≈ ground, the same treatment the contact
/// card's reading panel and the call window's assessment get. It is the model's reading of the
/// quotes beneath it, and a paragraph on this screen with nothing saying where it came from would
/// read as something the archive knows.
///
/// Nothing here is judged stale. An archive-wide answer is built out of quotes from many
/// conversations, each with its own transcript history; there is no single text for it to have
/// been written against, and marking it stale because one call among forty was transcribed again
/// would put a warning on nearly every stored answer and teach the reader to ignore all of them.
/// What re-transcription can actually break is one quote's position in the audio, and the player
/// already handles a moment that no longer starts a line.
/// </summary>
public sealed class AskExchangeView
{
    public AskExchangeView(Repository.StoredAskExchange stored, string? contactName)
    {
        Id = stored.Id;
        Question = stored.Question;
        Answer = stored.Answer;
        Insufficient = stored.Insufficient;

        Citations = [.. StoredExcerpts.Read(stored.Citations).Select(e => new CitationView(e))];

        Stamp = string.Format(
            Localisation.T("askpage.modelin-gorusu-imza"),
            stored.ModelUsed ?? "model",
            stored.AskedAt.ToLocalTime().ToString("d MMMM yyyy"));

        // What the question was narrowed to when it was asked. Absent when it ranged over
        // everything, because "Kapsam: her şey" is a label that says nothing.
        List<string> parts = [];

        if (contactName is not null) parts.Add(contactName);

        if (stored.Since is not null || stored.Until is not null)
        {
            parts.Add(string.Format(
                Localisation.T("askpage.tarih-araligi"),
                stored.Since?.ToLocalTime().ToString("d MMMM yyyy") ?? "…",
                stored.Until?.ToLocalTime().ToString("d MMMM yyyy") ?? "…"));
        }

        Scope = parts.Count == 0
            ? null
            : string.Format(Localisation.T("askpage.kapsam"), string.Join(" · ", parts));
    }

    public long Id { get; }
    public string Question { get; }
    public string Answer { get; }
    public bool Insufficient { get; }
    public string Stamp { get; }
    public string? Scope { get; }

    public IReadOnlyList<CitationView> Citations { get; }

    public bool HasCitations => Citations.Count > 0;
}

/// <summary>
/// The screen where a question is asked of the whole archive.
///
/// Separate from Search on purpose, and both are kept. Search answers "find me the word"; this
/// answers "what happened about this" — and they want different things on screen. A search
/// result list is the right answer to the first question and a poor answer to the second, where
/// what is wanted is a sentence with the evidence under it.
///
/// The rule the whole screen is built around: **the answer is never shown without the excerpts
/// it was built from.** A model asked about somebody's conversations will produce a confident,
/// fluent, invented account whenever the excerpts do not contain the answer, and prose alone
/// gives the reader no way to tell the two apart. So the citations are not a footnote here, they
/// are the point, and an answer that cites nothing is withheld.
/// </summary>
public sealed partial class AskViewModel : ObservableObject
{
    private readonly System.Net.Http.HttpClient _http;
    private readonly Repository _repository;
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppSettings, ArchiveQuestions>? _questions;

    private CancellationTokenSource? _work;

    /// <param name="questions">
    /// How to reach a model, when the caller wants to say. The page's whole promise is that
    /// opening it and reading what was answered before costs nothing, and the only way to hold
    /// that promise to account is to hand it a way of asking that fails if it is ever used.
    /// </param>
    public AskViewModel(
        System.Net.Http.HttpClient http,
        Repository repository,
        Func<AppSettings> settings,
        Func<AppSettings, ArchiveQuestions>? questions = null)
    {
        _http = http;
        _repository = repository;
        _settings = settings;
        _questions = questions;

        LoadContacts();
        LoadHistory();
    }

    /// <summary>
    /// Built per question rather than held.
    ///
    /// The provider, the address and the model can all be changed in settings while the window
    /// is open, and a client captured at construction would keep talking to the endpoint that
    /// was configured when the application started — failing with a connection error that says
    /// nothing about the setting that actually changed.
    /// </summary>
    private ArchiveQuestions QuestionsFor(AppSettings settings) =>
        _questions?.Invoke(settings)
        ?? new(LlmClientFactory.Create(
                   _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey),
               _repository);

    /// <summary>Raised to open a contact at a moment, when a citation is clicked.</summary>
    public event EventHandler<(long CallId, int StartMs)>? OpenRequested;

    public ObservableCollection<ContactChoice> Contacts { get; } = [];
    public ObservableCollection<CitationView> Citations { get; } = [];

    /// <summary>What has already been asked and answered, newest first. Read, never re-asked.</summary>
    public ObservableCollection<AskExchangeView> Exchanges { get; } = [];

    public IReadOnlyList<SearchPeriod> Periods { get; } = Enum.GetValues<SearchPeriod>();

    [ObservableProperty] private string _question = "";
    [ObservableProperty] private ContactChoice? _contact;
    [ObservableProperty] private SearchPeriod _period = SearchPeriod.Anytime;

    [ObservableProperty] private string? _answer;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private bool _isInsufficient;
    [ObservableProperty] private bool _hasAsked;

    public bool HasAnswer => !string.IsNullOrWhiteSpace(Answer);
    public bool HasCitations => Citations.Count > 0;

    public bool HasHistory => Exchanges.Count > 0;

    /// <summary>The examples are an introduction, and there is nothing to introduce once the page has answers on it.</summary>
    public bool ShowSuggestions => !HasAsked && Exchanges.Count == 0;

    /// <summary>
    /// Questions worth trying before there is a habit of asking any.
    ///
    /// An empty box with a blinking cursor is the worst possible introduction to a feature whose
    /// usefulness depends entirely on knowing what it can be asked. These are the four shapes
    /// this archive actually answers well.
    /// </summary>
    public IReadOnlyList<string> Suggestions { get; } =
    [
        "Fiyat konusunda ne konuşuldu?",
        "Bana ne söz verildi?",
        "Teslim tarihi kaça çekildi?",
        "Cevapsız kalan sorular neydi?",
    ];

    partial void OnAnswerChanged(string? value) => OnPropertyChanged(nameof(HasAnswer));

    public void LoadContacts()
    {
        var selected = Contact?.Id;

        Contacts.Clear();
        Contacts.Add(new ContactChoice(null, "Herkes"));

        foreach (var contact in _repository.ListContacts())
            Contacts.Add(new ContactChoice(contact.Id, contact.Name));

        Contact = Contacts.FirstOrDefault(c => c.Id == selected) ?? Contacts[0];
    }

    /// <summary>
    /// Puts the answered questions back on the page.
    ///
    /// A pure read of the archive. No model is reached, nothing is re-asked, and the page is
    /// therefore free to open — which is the entire point of writing the answers down.
    ///
    /// Only the archive-wide ones: a question asked inside a call window belongs to that
    /// conversation and is answered there. Listing it here would put an answer about one
    /// conversation under a screen whose every other row ranges over all of them.
    /// </summary>
    public void LoadHistory()
    {
        Exchanges.Clear();

        var names = _repository.ListContacts().ToDictionary(c => c.Id, c => c.Name);

        foreach (var stored in _repository.ArchiveAskExchanges())
        {
            Exchanges.Add(new AskExchangeView(
                stored,
                stored.ContactId is { } id && names.TryGetValue(id, out var name) ? name : null));
        }

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(ShowSuggestions));
    }

    /// <summary>
    /// Takes one exchange off the page and out of the archive.
    ///
    /// [Kaldır], not [Reddet]: the machine did not suggest this and the user is not turning it
    /// down — they asked a question, kept the answer, and have now decided not to.
    /// </summary>
    [RelayCommand]
    private void Remove(AskExchangeView exchange)
    {
        _repository.DeleteAskExchange(exchange.Id);
        LoadHistory();
    }

    [RelayCommand]
    private void UseSuggestion(string suggestion)
    {
        Question = suggestion;
        _ = AskAsync();
    }

    [RelayCommand]
    private void Open(CitationView citation) =>
        OpenRequested?.Invoke(this, (citation.CallId, citation.Excerpt.StartMs));

    /// <summary>Stops a question that is taking too long. The local model is not always quick.</summary>
    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    [RelayCommand]
    private async Task AskAsync()
    {
        if (IsThinking || string.IsNullOrWhiteSpace(Question)) return;

        _work?.Dispose();
        _work = new CancellationTokenSource();

        IsThinking = true;
        HasAsked = true;
        Problem = null;
        Answer = null;
        IsInsufficient = false;
        Citations.Clear();
        OnPropertyChanged(nameof(HasCitations));
        OnPropertyChanged(nameof(ShowSuggestions));

        try
        {
            var settings = _settings();

            var since = Period.Since();
            var until = Period.Until();

            var result = await QuestionsFor(settings).AskAsync(
                Question,
                settings.ResolvedModelName,
                Contact?.Id,
                since,
                until,
                _work.Token);

            // An answer with quotes under it is written down and then read back out of the
            // archive, so a fresh answer and one restored tomorrow are rendered by the same code
            // and cannot disagree about its signature, its scope or its evidence.
            //
            // Everything else stays in the live area below and is not written down. Two different
            // refusals land there: a search that matched nothing, which reached no model, cost
            // nothing and is free to repeat; and an answer the model could not ground in a quote,
            // which is refused on screen and would come back tomorrow wearing a signature and a
            // date if it were kept — as though it had been shown all along.
            if (result.Ok && result.Citations.Count > 0 && !string.IsNullOrWhiteSpace(result.Text))
            {
                _repository.SaveAskExchange(
                    callId: null,
                    Contact?.Id,
                    Question.Trim(),
                    result.Text,
                    StoredExcerpts.Write(result.Citations),
                    result.Insufficient,
                    settings.ResolvedModelName,
                    since,
                    until);

                LoadHistory();
                return;
            }

            Answer = result.Text;
            Problem = result.Problem;
            IsInsufficient = result.Insufficient;

            // Shown even when the answer was withheld. The retrieval half worked, and the lines
            // it found are frequently the answer — throwing them away because the summariser
            // failed would hide the very thing that was asked for.
            foreach (var citation in result.Citations)
                Citations.Add(new CitationView(citation));
        }
        catch (OperationCanceledException)
        {
            Problem = "Soru iptal edildi.";
        }
        catch (Exception e)
        {
            Problem = $"Cevaplanamadı: {e.Message}";
        }
        finally
        {
            // Raised on every exit, so the citations panel always agrees with the list —
            // whether the question was answered, withheld, cancelled or failed.
            OnPropertyChanged(nameof(HasCitations));
            IsThinking = false;
        }
    }
}
