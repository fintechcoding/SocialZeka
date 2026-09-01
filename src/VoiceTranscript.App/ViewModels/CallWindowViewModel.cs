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
    string speaker, string text, int startMs, int endMs, bool isMe, bool lowConfidence) : ObservableObject
{
    public string Speaker { get; } = speaker;
    public string Text { get; } = text;
    public int StartMs { get; } = startMs;

    /// <summary>Where the line finishes. Needed to cut a clip that does not stop mid-word.</summary>
    public int EndMs { get; } = endMs;

    public bool IsMe { get; } = isMe;

    /// <summary>Whisper was unsure. Marked rather than hidden — an uncertain line is still evidence.</summary>
    public bool LowConfidence { get; } = lowConfidence;

    /// <summary>
    /// Hour-aware on purpose: "mm\:ss" silently drops the hour, so on the long calls this
    /// product explicitly supports, a line spoken at 1:05:00 claimed to be at 05:00 — a wrong
    /// timestamp under a verbatim quote, which is the one lie this product must never tell.
    /// </summary>
    public string Time
    {
        get
        {
            var t = TimeSpan.FromMilliseconds(StartMs);

            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }

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

    // ---- tags ---------------------------------------------------------------
    //
    // The user's own words on the conversation — "tehdit edildik", "önemli". User data, like the
    // note: reprocessing never touches them, and the vocabulary is theirs alone.

    public ObservableCollection<string> Tags { get; } = [];

    [ObservableProperty] private string _newTag = "";

    /// <summary>Earlier tags, most used first, so labelling stays one vocabulary, not typos.</summary>
    public ObservableCollection<string> TagSuggestions { get; } = [];

    [RelayCommand]
    private void AddTag()
    {
        var tag = NewTag.Trim();
        if (tag.Length == 0) return;

        _repository.Tag(CallId, tag);
        NewTag = "";

        LoadTags();
    }

    public void RemoveTag(string tag)
    {
        _repository.Untag(CallId, tag);
        LoadTags();
    }

    private void LoadTags()
    {
        Tags.Clear();
        foreach (var tag in _repository.TagsOf(CallId)) Tags.Add(tag);

        TagSuggestions.Clear();

        // Defined vocabulary first — the tags the user gave a face — then whatever else is in
        // use. Suggestions are why the manager's definitions show up before their first use.
        foreach (var def in Services.TagPalette.All)
            if (!Tags.Contains(def.Tag)) TagSuggestions.Add(def.Tag);

        foreach (var (tag, _) in _repository.AllTags().Take(8))
            if (!Tags.Contains(tag) && !TagSuggestions.Contains(tag)) TagSuggestions.Add(tag);
    }

    /// <summary>Re-reads tags and suggestions — called after the tag manager saves.</summary>
    public void ReloadTags() => LoadTags();

    /// <summary>
    /// How good the text is, and what produced it.
    ///
    /// Worth stating because a transcript is evidence, and evidence with an unstated provenance is
    /// worth less than it looks. The share the model was unsure about is the honest measure of
    /// whether these words can be leaned on — on a recording where the microphone was wrong or one
    /// side was quiet it is the difference between a transcript to read and one to redo. Which is
    /// exactly why the button beside it exists.
    /// </summary>
    [ObservableProperty] private string? _qualityLine;

    /// <summary>True when a large share of the lines were uncertain, so it is worth saying louder.</summary>
    [ObservableProperty] private bool _qualityIsPoor;

    // ---- live processing ----------------------------------------------------
    //
    // This window used to answer "Çözümle" with a dialog telling the user to close it and open
    // it again later. The orchestrator was already announcing progress several times a second
    // and completion once — the window simply was not listening. Now it is, and the dialog and
    // its instruction are gone.

    /// <summary>True while this conversation is queued or being worked on.</summary>
    [ObservableProperty] private bool _isWorking;

    /// <summary>What the pipeline says it is doing right now.</summary>
    [ObservableProperty] private string? _workStage;

    /// <summary>Progress 0..1 when the stage reports one.</summary>
    [ObservableProperty] private double _workPercent;

    /// <summary>True while no percentage is available, so the bar still moves.</summary>
    [ObservableProperty] private bool _workIsIndeterminate = true;

    /// <summary>One readable sentence when the run failed, beside a way to try again.</summary>
    [ObservableProperty] private string? _workFailure;

    /// <summary>Flips the strip on the moment the request is queued, before any event arrives.</summary>
    public void MarkQueued()
    {
        WorkFailure = null;
        WorkStage = "Sırada bekliyor";
        WorkPercent = 0;
        WorkIsIndeterminate = true;
        IsWorking = true;
    }

    /// <summary>A progress report from the pipeline. Ignores other conversations'.</summary>
    public void OnProgress(Services.CallProgress progress)
    {
        if (progress.CallId != CallId) return;

        IsWorking = true;
        WorkFailure = null;
        WorkStage = progress.Stage;
        WorkIsIndeterminate = progress.Percent is null;
        WorkPercent = progress.Percent ?? 0;
    }

    /// <summary>
    /// The pipeline finished with this conversation, one way or the other. Everything on screen
    /// is re-read from the archive, because that is what just changed.
    /// </summary>
    public void OnProcessed(Services.CallProcessed processed)
    {
        if (processed.CallId != CallId) return;

        IsWorking = false;
        WorkStage = null;

        WorkFailure = processed.Failure is null
            ? null
            : Core.Asr.FailureText.Summarise(processed.Failure);

        Load();
    }

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
                   + $"{(int)call.Duration.TotalMinutes:00}:{call.Duration.Seconds:00} · {call.App}"
                   + (call.Direction switch
                   {
                       CallDirection.Incoming => " · ↓ gelen arama",
                       CallDirection.Outgoing => " · ↑ giden arama",
                       _ => "", // observed mid-call: an honest blank beats a guess
                   });

        var segments = _repository.GetSegments(CallId);

        Turns.Clear();
        foreach (var segment in segments)
        {
            Turns.Add(new ChatTurn(
                segment.IsMe ? "Ben" : contact ?? "Karşı taraf",
                segment.Text,
                segment.StartMs,
                segment.EndMs,
                segment.IsMe,
                segment.LowConfidence));
        }

        TranscriptMessage = segments.Count == 0
            ? call.State == ProcessingState.Failed
                ? "Bu görüşme yazıya dökülemedi. İşlem durumu ekranından yeniden denenebilir."
                : "Bu görüşme henüz yazıya dökülmedi."
            : null;

        // The failure strip and this message are the same fact twice; when the strip is up, one
        // voice is enough.
        if (WorkFailure is not null) TranscriptMessage = null;

        // A window opened onto a recording that is already queued or being worked on shows the
        // live strip from the first frame — the state field knew, and the strip did not.
        if (call.State is ProcessingState.Queued or ProcessingState.Recorded)
        {
            MarkQueued();
        }
        else if (call.State is ProcessingState.Transcribing or ProcessingState.Analysing)
        {
            IsWorking = true;
            WorkStage = call.State == ProcessingState.Transcribing ? "Yazıya dökülüyor" : "Çözümleniyor";
            WorkIsIndeterminate = true;
        }

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

        LoadTags();
        LoadQuality();

        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(HasLedger));
        OnPropertyChanged(nameof(HasTurns));
        OnPropertyChanged(nameof(HasAnalysis));

        // The player reloads only when its audio actually changed. Load() also runs when a
        // re-analysis finishes, and reloading then stopped whatever the user was listening to at
        // that exact moment — playback yanked to zero by an event they did not cause. Same
        // paths + same duration (trimming changes the second) means the sound is the same sound.
        if (call.MicPath != _loadedMic || call.FarPath != _loadedFar
            || call.Duration != _loadedDuration || !Playback.IsLoaded)
        {
            _loadedMic = call.MicPath;
            _loadedFar = call.FarPath;
            _loadedDuration = call.Duration;

            _ = Playback.LoadAsync(call.MicPath, call.FarPath, call.Duration);
        }
    }

    private string? _loadedMic;
    private string? _loadedFar;
    private TimeSpan _loadedDuration;

    /// <summary>
    /// Whether any analysis has ever produced anything here — the "Çözümle" invitation shows
    /// only when this is false. It used to key on the ledger alone, so a call whose analysis
    /// produced a summary and no entries showed the invitation and the empty-state at once.
    /// </summary>
    public bool HasAnalysis => HasSummary || HasLedger;

    /// <summary>
    /// Says what produced this text and how sure it was.
    ///
    /// Both halves are facts the archive already held and never showed. Which engine ran matters
    /// because they differ enormously — on this machine a local model managed a fifth of real time
    /// and a hosted one two hundred times it — and the uncertain share matters because it is the
    /// difference between a transcript worth quoting and one worth producing again.
    /// </summary>
    private void LoadQuality()
    {
        var (lines, lowConfidence, overlapping) = _repository.TranscriptQuality(CallId);

        if (lines == 0)
        {
            QualityLine = null;
            QualityIsPoor = false;
            return;
        }

        List<string> parts = [$"{lines} satır"];

        if (lowConfidence > 0)
            parts.Add($"{lowConfidence} tanesi belirsiz (%{100.0 * lowConfidence / lines:0})");

        if (overlapping > 0) parts.Add($"{overlapping} satırda üst üste konuşma");

        if (_repository.LastRun(CallId, ProcessingStage.Transcribe) is { } run)
        {
            parts.Add(Core.Asr.AsrCatalog.DisplayFor(run.Engine));

            if (run.SpeedFactor is { } speed) parts.Add($"gerçek zamanın {speed:0.#} katı");
        }

        QualityLine = string.Join(" · ", parts);

        // A third is the point where the text stops being something to quote from. Said plainly
        // rather than scored: the reader can see the marked lines and decide.
        QualityIsPoor = lowConfidence * 3 >= lines;
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
        // The stand-in only ever reaches PlayTurn, which reads the moment and the speaker, so its
        // end is nominal — it exists for the case where the transcript has been re-run since the
        // citation was made and no line starts at exactly that millisecond any more.
        var turn = Turns.FirstOrDefault(t => t.StartMs == excerpt.StartMs)
                   ?? new ChatTurn("", "", excerpt.StartMs, excerpt.StartMs, excerpt.IsMe, false);

        PlayTurn(turn);
    }

    /// <summary>
    /// The stretch covered by a line and the <paramref name="following"/> lines after it.
    ///
    /// Counted in turns rather than in seconds because that is how somebody thinks about it: "what
    /// I asked and what they said back" is two turns whether the reply came instantly or after
    /// twenty seconds of thinking. A fixed number of seconds would cut one of those two cases in
    /// half, and it would be the case where the pause was the interesting part.
    /// </summary>
    public (int FromMs, int ToMs) ExchangeRange(ChatTurn turn, int following)
    {
        var index = Turns.IndexOf(turn);

        if (index < 0) return (turn.StartMs, turn.EndMs);

        var last = Turns[Math.Min(index + Math.Max(0, following), Turns.Count - 1)];

        // Max, not simply the last turn's end: the speakers are recorded separately and can
        // overlap, so a later turn can finish before an earlier one does.
        return (turn.StartMs, Math.Max(last.EndMs, turn.EndMs));
    }

    /// <summary>Who the call is with, for labelling an export. Null when it was never attributed.</summary>
    public string? ContactName => Title == "İsimsiz" ? null : Title;

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

            var client = LlmClientFactory.Create(
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
