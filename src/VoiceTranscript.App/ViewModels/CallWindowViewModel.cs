using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One turn in the conversation, laid out as a message.</summary>
public sealed partial class ChatTurn(
    string speaker, string text, int startMs, bool isMe, bool lowConfidence) : ObservableObject
{
    public string Speaker { get; } = speaker;
    public string Text { get; } = text;
    public int StartMs { get; } = startMs;
    public bool IsMe { get; } = isMe;

    /// <summary>Whisper was unsure. Marked rather than hidden — an uncertain line is still evidence.</summary>
    public bool LowConfidence { get; } = lowConfidence;

    public string Time => TimeSpan.FromMilliseconds(StartMs).ToString(@"mm\:ss");

    /// <summary>Highlighted while the player is inside this turn.</summary>
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>One exchange in the question panel.</summary>
public sealed record ChatMessage(bool FromUser, string Text, IReadOnlyList<Excerpt> Citations)
{
    public bool HasCitations => Citations.Count > 0;
}

/// <summary>
/// One conversation, opened on its own.
///
/// The contact page shows a call inside a column beside everything else that person has ever said,
/// which is the right shape for browsing and the wrong one for reading. A conversation somebody
/// has opened deliberately is something they want to work through: read it, hear a passage again,
/// ask what was agreed, write down what they concluded. That wants a window and room.
///
/// Four things, in the order they are used:
///
///   <b>The conversation</b>, laid out as an exchange rather than a table, because that is what it
///   was. Clicking a line plays from it — checking a sentence against the audio is the one action
///   this product asks people to perform, and it has to cost one click.
///
///   <b>What was extracted</b>, kept separate from the words. A summary is a claim about the
///   conversation and belongs beside it, not woven into it.
///
///   <b>Questions</b>, answered with quotes from this call alone. Scoped deliberately: an answer
///   drawn from a year of calls is a different thing from an answer about the call on screen, and
///   conflating them is how somebody ends up believing something was said here that was said
///   somewhere else.
///
///   <b>Notes</b>, which are the only part a person writes. Everything else is replaced when the
///   call is analysed again; this is not.
/// </summary>
public sealed partial class CallWindowViewModel : ObservableObject, IDisposable
{
    private readonly Repository _repository;
    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _http;

    public CallWindowViewModel(
        Repository repository, Func<AppSettings> settings, HttpClient http, long callId)
    {
        _repository = repository;
        _settings = settings;
        _http = http;

        CallId = callId;

        Playback.PositionChanged += (_, ms) => Highlight(ms);

        Load();
    }

    public long CallId { get; }

    public PlaybackViewModel Playback { get; } = new();

    public ObservableCollection<ChatTurn> Turns { get; } = [];
    public ObservableCollection<ChatMessage> Conversation { get; } = [];
    public ObservableCollection<Commitment> Commitments { get; } = [];
    public ObservableCollection<Claim> Claims { get; } = [];
    public ObservableCollection<Flag> Flags { get; } = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _transcriptMessage;

    [ObservableProperty] private string _question = "";
    [ObservableProperty] private bool _isAsking;
    [ObservableProperty] private string? _askProblem;

    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _noteSaved;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasLedger => Commitments.Count > 0 || Claims.Count > 0 || Flags.Count > 0;
    public bool HasTurns => Turns.Count > 0;

    private void Load()
    {
        var call = _repository.GetCall(CallId);
        if (call is null) return;

        var contact = call.ContactId is { } id ? _repository.GetContact(id)?.Name : null;

        Title = contact ?? "İsimsiz";
        Subtitle = $"{call.StartedAt.ToLocalTime():d MMMM yyyy, HH:mm} · "
                   + $"{(int)call.Duration.TotalMinutes:00}:{call.Duration.Seconds:00} · {call.App}";

        var segments = _repository.GetSegments(CallId);

        Turns.Clear();
        foreach (var segment in segments)
        {
            Turns.Add(new ChatTurn(
                segment.IsMe ? "Ben" : contact ?? "Karşı taraf",
                segment.Text,
                segment.StartMs,
                segment.IsMe,
                segment.LowConfidence));
        }

        TranscriptMessage = segments.Count == 0
            ? call.State == ProcessingState.Failed
                ? "Bu görüşme yazıya dökülemedi. İşlem durumu ekranından yeniden denenebilir."
                : "Bu görüşme henüz yazıya dökülmedi."
            : null;

        Summary = _repository.GetSummary(CallId)?.Summary;

        Commitments.Clear();
        Claims.Clear();
        Flags.Clear();

        if (call.ContactId is { } contactId)
        {
            foreach (var c in _repository.GetOpenCommitments(contactId).Where(c => c.CallId == CallId))
                Commitments.Add(c);

            foreach (var c in _repository.GetAllClaims(contactId).Where(c => c.CallId == CallId))
                Claims.Add(c);

            foreach (var f in _repository.GetFlags(contactId).Where(f => f.CallId == CallId))
                Flags.Add(f);
        }

        Note = _repository.GetNote(CallId);

        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(HasLedger));
        OnPropertyChanged(nameof(HasTurns));

        _ = Playback.LoadAsync(call.MicPath, call.FarPath, call.Duration);
    }

    /// <summary>
    /// Marks the line the player is inside.
    ///
    /// The point is reading along: somebody checking a quote is looking at the text and listening
    /// at the same time, and without this they have to find their place again after every seek.
    /// </summary>
    private void Highlight(int positionMs)
    {
        ChatTurn? current = null;

        foreach (var turn in Turns)
        {
            if (turn.StartMs <= positionMs) current = turn;
            else break;
        }

        foreach (var turn in Turns) turn.IsCurrent = ReferenceEquals(turn, current);
    }

    /// <summary>
    /// Plays from a line.
    ///
    /// The stream matters as much as the position: the two speakers are recorded separately, so
    /// playing "my" line from the far stream produces silence, which reads as a broken recording
    /// rather than as the wrong channel.
    /// </summary>
    [RelayCommand]
    private void PlayTurn(ChatTurn turn)
    {
        var call = _repository.GetCall(CallId);
        var path = turn.IsMe ? call?.MicPath : call?.FarPath;

        if (path is null || !System.IO.File.Exists(path))
        {
            TranscriptMessage = "Ses dosyası bulunamadı.";
            return;
        }

        try
        {
            Playback.PlayFrom(turn.StartMs, turn.IsMe);
            TranscriptMessage = null;
        }
        catch (Exception e)
        {
            TranscriptMessage = $"Ses çalınamadı: {e.Message}";
        }
    }

    /// <summary>Plays from a citation the question panel produced.</summary>
    [RelayCommand]
    private void PlayExcerpt(Excerpt excerpt)
    {
        var turn = Turns.FirstOrDefault(t => t.StartMs == excerpt.StartMs)
                   ?? new ChatTurn("", "", excerpt.StartMs, excerpt.IsMe, false);

        PlayTurn(turn);
    }

    // ---- questions about this call -----------------------------------------

    /// <summary>
    /// Answers a question using only this conversation.
    ///
    /// Scoped to one call on purpose. The archive-wide version already exists and answers a
    /// different question; mixing the two would let an answer about last March appear under a call
    /// from today, with citations that look identical.
    /// </summary>
    [RelayCommand]
    private async Task AskAsync(CancellationToken cancellationToken)
    {
        var question = Question?.Trim();
        if (string.IsNullOrWhiteSpace(question) || IsAsking) return;

        var settings = _settings();

        if (!settings.LlmReachableInPrinciple)
        {
            AskProblem = "Bağlı bir yapay zekâ servisi yok. Ayarlar → Çözümleme bölümünden bir sağlayıcı seç.";
            return;
        }

        Conversation.Add(new ChatMessage(FromUser: true, question, []));
        Question = "";
        AskProblem = null;
        IsAsking = true;

        try
        {
            var call = _repository.GetCall(CallId);

            var client = new OpenAiCompatibleClient(
                _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

            // Narrowed to this call by contact and by the day it happened. ArchiveQuestions has no
            // per-call filter, and adding one there would change a screen that already works;
            // bounding the window to the call's own minute achieves the same thing here.
            var answer = await new ArchiveQuestions(client, _repository).AskAsync(
                question,
                settings.ResolvedModelName,
                call?.ContactId,
                since: call?.StartedAt.AddSeconds(-1),
                until: call?.StartedAt.Add(call.Duration).AddSeconds(1),
                cancellationToken);

            if (!answer.Ok)
            {
                AskProblem = answer.Problem;
                return;
            }

            Conversation.Add(new ChatMessage(FromUser: false, answer.Text, answer.Citations));
        }
        catch (Exception e)
        {
            AskProblem = $"Cevap alınamadı: {e.Message}";
        }
        finally
        {
            IsAsking = false;
        }
    }

    // ---- notes --------------------------------------------------------------

    /// <summary>
    /// Saves what the user wrote.
    ///
    /// Explicit rather than on every keystroke: a note is a considered thing, and a database write
    /// per character would also mean a write while somebody is still deciding what they think.
    /// </summary>
    [RelayCommand]
    private void SaveNote()
    {
        _repository.SaveNote(CallId, Note);
        NoteSaved = true;
    }

    partial void OnNoteChanged(string value) => NoteSaved = false;

    public void Dispose() => Playback.Dispose();
}
