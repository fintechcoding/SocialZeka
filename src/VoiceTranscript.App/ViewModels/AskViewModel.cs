using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One excerpt as the screen shows it: the words, who said them, and where to hear them.</summary>
public sealed record CitationView(Excerpt Excerpt)
{
    public string Text => Excerpt.Text;

    public string Who => Excerpt.IsMe ? "Ben" : Excerpt.ContactName ?? "Karşı taraf";

    public bool IsMe => Excerpt.IsMe;

    public string When => Excerpt.CallStartedAt.ToLocalTime().ToString("d MMMM yyyy HH:mm");

    public string At => $"{Excerpt.StartMs / 60000:00}:{Excerpt.StartMs / 1000 % 60:00}";

    public long CallId => Excerpt.CallId;
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

    private CancellationTokenSource? _work;

    public AskViewModel(
        System.Net.Http.HttpClient http,
        Repository repository,
        Func<AppSettings> settings)
    {
        _http = http;
        _repository = repository;
        _settings = settings;

        LoadContacts();
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
        new(LlmClientFactory.Create(
                _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey),
            _repository);

    /// <summary>Raised to open a contact at a moment, when a citation is clicked.</summary>
    public event EventHandler<(long CallId, int StartMs)>? OpenRequested;

    public ObservableCollection<ContactChoice> Contacts { get; } = [];
    public ObservableCollection<CitationView> Citations { get; } = [];

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

        try
        {
            var settings = _settings();

            var result = await QuestionsFor(settings).AskAsync(
                Question,
                settings.ResolvedModelName,
                Contact?.Id,
                Period.Since(),
                Period.Until(),
                _work.Token);

            Answer = result.Text;
            Problem = result.Problem;
            IsInsufficient = result.Insufficient;

            // Shown even when the answer was withheld. The retrieval half worked, and the lines
            // it found are frequently the answer — throwing them away because the summariser
            // failed would hide the very thing that was asked for.
            foreach (var citation in result.Citations)
                Citations.Add(new CitationView(citation));

            OnPropertyChanged(nameof(HasCitations));
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
            IsThinking = false;
        }
    }
}
