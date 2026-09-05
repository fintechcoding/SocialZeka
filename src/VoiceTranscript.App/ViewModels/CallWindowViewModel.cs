using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One turn in the conversation, laid out as a message.</summary>
public sealed partial class ChatTurn(
    string speaker, string text, int startMs, int endMs, bool isMe, bool lowConfidence,
    bool overlapsOther = false, bool suspectedEcho = false) : ObservableObject
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
    /// Somebody was already speaking when this line started.
    ///
    /// Worth showing because it changes how the line reads. "Tamam" said into a pause is agreement;
    /// the same word over the top of someone else is an interruption, and a ledger entry that
    /// quotes it without saying which is quoting half a fact. Both sides of an overlap are kept —
    /// separate capture is what makes that possible at all.
    /// </summary>
    public bool OverlapsOther { get; } = overlapsOther;

    /// <summary>
    /// The same words on both channels at the same moment: one voice reaching the microphone
    /// through the speakers, not two people agreeing verbatim. Marked, never deleted — a genuine
    /// simultaneous "aynen" is indistinguishable from bleed, and deleting would erase real speech.
    /// </summary>
    public bool SuspectedEcho { get; } = suspectedEcho;

    /// <summary>The one-word note the bubble carries, or nothing. Echo first: it questions whether
    /// the line was said at all, which outranks how it was said.</summary>
    public string? Note => SuspectedEcho ? "yankı" : OverlapsOther ? "üst üste" : null;

    public bool HasNote => Note is not null;

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

    /// <summary>
    /// Whether the conversation is drawn against the clock instead of as a list of turns.
    ///
    /// Set by the window from the remembered preference and written back when it changes. Held
    /// here rather than read from settings directly so the view model still builds in a test.
    /// </summary>
    [ObservableProperty] private bool _timelineView;

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

    // ---- who did the talking --------------------------------------------------
    //
    // The same strip the contacts page wears, here at the user's request: facts with the
    // seconds behind them ("sen %62 konuştun"), never a judgement about who interrupts.

    [ObservableProperty] private double _talkRatio = 0.5;
    [ObservableProperty] private string? _talkSummary;
    [ObservableProperty] private string? _interruptionSummary;

    public bool HasTalkStats => TalkSummary is not null;

    private void ComputeTalkStats(IReadOnlyList<Segment> segments)
    {
        TalkSummary = null;
        InterruptionSummary = null;
        TalkRatio = 0.5;

        if (segments.Count > 0)
        {
            var mine = TimeSpan.Zero;
            var theirs = TimeSpan.Zero;

            foreach (var segment in segments)
            {
                var length = TimeSpan.FromMilliseconds(Math.Max(0, segment.EndMs - segment.StartMs));
                if (segment.IsMe) mine += length; else theirs += length;
            }

            var total = mine + theirs;
            if (total > TimeSpan.Zero)
            {
                TalkRatio = mine.TotalSeconds / total.TotalSeconds;

                TalkSummary =
                    $"Sen {mine.TotalMinutes:0.#} dk (%{TalkRatio * 100:0}), " +
                    $"karşı taraf {theirs.TotalMinutes:0.#} dk (%{(1 - TalkRatio) * 100:0})";

                var ordered = segments.OrderBy(s => s.StartMs).ToList();
                var myCuts = 0;
                var theirCuts = 0;

                for (var i = 1; i < ordered.Count; i++)
                {
                    var previous = ordered[i - 1];
                    var current = ordered[i];

                    if (current.IsMe == previous.IsMe || current.StartMs >= previous.EndMs) continue;

                    if (current.IsMe) myCuts++; else theirCuts++;
                }

                InterruptionSummary = myCuts + theirCuts == 0
                    ? "Kimse kimsenin sözünü kesmedi."
                    : $"Söz kesme: sen {myCuts}, karşı taraf {theirCuts}.";
            }
        }

        OnPropertyChanged(nameof(HasTalkStats));
    }

    /// <summary>
    /// Whether analysis has actually run — which is NOT the same as the ledger having rows.
    /// The "çözümlenmemiş" card used to key on ledger emptiness, so an ordinary conversation
    /// that analysed clean kept being described as never analysed, straight after the user
    /// watched it analyse.
    /// </summary>
    [ObservableProperty] private bool _isAnalysed;

    /// <summary>Analysed, and honestly empty — the state the quiet explanation belongs to.</summary>
    public bool ShowEmptyLedger => IsAnalysed && !HasLedger;

    // The window-level attention strip. Fed by the verified evidence layers, plus — only when
    // the user switched it on — an elevated deception level, always labelled a model view.
    // The free reading never reaches it.
    public bool HasAttention => Flags.Count > 0 || ConsistencyFindings.Count > 0
        || (DeceptionEnabled && Deception is { IsElevated: true });

    public string AttentionLine
    {
        get
        {
            var parts = new List<string>();
            if (Flags.Count > 0) parts.Add($"{Flags.Count} işaret");
            if (ConsistencyFindings.Count > 0) parts.Add($"{ConsistencyFindings.Count} denetim bulgusu");

            if (DeceptionEnabled && Deception is { IsElevated: true } elevated)
                parts.Add($"model görüşü: şüphe düzeyi {(elevated.Level == "yuksek" ? "yüksek" : "orta")}");

            var head = string.Join(" · ", parts);
            return string.IsNullOrWhiteSpace(ConsistencyWarning)
                ? $"{head} — her biri gerekçesiyle Tutarlılık sekmesinde; hükmü sen ver."
                : $"{head} — {ConsistencyWarning}";
        }
    }

    private void RefreshAttention()
    {
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(AttentionLine));
    }

    /// <summary>Re-reads everything about this call. Used after a stored transcript is put back.</summary>
    public void Reload() => Load();

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

        UpdateConsistencyCostLine(segments.Sum(s => s.Text.Length));
        ComputeTalkStats(segments);

        Turns.Clear();

        // Read in the order it happened, not in the order the lines start. A twelve-second turn
        // with a real answer inside it is cut open here so the answer is not filed underneath the
        // sentence it replies to. Nothing stored changes: see Services.ChatFlow.
        foreach (var segment in Services.ChatFlow.InReadingOrder(segments))
        {
            Turns.Add(new ChatTurn(
                SpeakerText.For(segment.IsMe, contact),
                segment.Text,
                segment.StartMs,
                segment.EndMs,
                segment.IsMe,
                segment.LowConfidence,
                segment.OverlapsOtherSpeaker,
                segment.SuspectedEcho));
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
                if (f.Source != Flag.Sources.Consistency)
                    Flags.Add(f);
        }

        LoadActions();
        LoadReading();
        LoadDeception();

        // The consistency section's own rows — split from the ledger's flags because the two
        // come from different runs, clear separately, and answer different clicks. Reloaded
        // here so reopening the window brings a past run's findings and note back.
        ConsistencyFindings.Clear();
        foreach (var f in _repository.FlagsOf(CallId).Where(f => f.Source == Flag.Sources.Consistency))
            ConsistencyFindings.Add(new ConsistencyRow(f, _repository));

        if (_repository.GetConsistencyNote(CallId) is { } stored)
        {
            ConsistencyWarning = stored.Note;
            ConsistencyStamp =
                $"{stored.ModelUsed ?? "model"} · {stored.CreatedAt.ToLocalTime():d MMMM yyyy}";
        }
        else
        {
            ConsistencyWarning = null;
            ConsistencyStamp = ConsistencyFindings.Count > 0 ? "önceki koşum" : null;
        }

        // The observations are not stored — only the findings and the note are — so a reopened
        // window used to show the accusatory half of a consistency run with its balancing half
        // silently missing, and nothing said so. The reader saw findings against a person and
        // no observations in their favour, and had no way to know the run had produced any.
        ConsistencyObservations.Clear();

        if (ConsistencyFindings.Count > 0)
        {
            ConsistencyMessage =
                "Önceki koşumun bulguları. Gözlemler saklanmaz; dengeleyici gözlemleri görmek " +
                "için denetimi yeniden çalıştır.";
        }

        OnPropertyChanged(nameof(HasConsistencyRun));
        RefreshAttention();

        IsAnalysed = call.State == ProcessingState.Analysed || HasSummary || HasLedger;
        OnPropertyChanged(nameof(ShowEmptyLedger));

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

        var run = _repository.LastRun(CallId, ProcessingStage.Transcribe);

        // Where the text on screen came from, asked of the text on screen.
        //
        // This used to ask the last run, and the last run is not the same question. Restoring an
        // older transcript left the strip naming a different engine than the one whose words were
        // being read — line counts from the visible lines, engine and speed from something else,
        // mixed inside one sentence. In a product whose argument is that every quote can be
        // traced, a provenance line that names the wrong engine is worse than none.
        var version = _repository.CurrentTranscriptVersion(CallId);

        var engine = version?.Engine ?? run?.Engine;
        var coverage = version?.SpeechCoverage ?? run?.SpeechCoverage;

        // How much of the conversation is here at all, before anything about how sure it was.
        //
        // Uncertain lines and missing ones are different faults and used to read the same. A
        // marked line is one the reader can judge; a dropped one leaves a gap indistinguishable
        // from a pause in the call. This archive holds transcripts where a hosted engine returned
        // words for two thirds of the speech and the text gave no sign of it — which is the
        // failure that took four days to name.
        if (coverage is { } share && share < 0.95)
            parts.Add($"konuşmanın %{share * 100:0}'i yazıya döküldü");

        if (engine is not null)
        {
            parts.Add(Core.Asr.AsrCatalog.DisplayFor(engine));

            // Only when the last run is the run that produced these words. A speed measured while
            // transcribing something else is another engine's figure wearing this one's name.
            if (run?.SpeedFactor is { } speed && run.Engine == engine)
                parts.Add($"gerçek zamanın {speed:0.#} katı");
        }

        QualityLine = string.Join(" · ", parts);

        // Two ways a transcript stops being worth quoting from, and the second is the quieter one.
        // A third of the lines marked uncertain is the old threshold; a fifth of the speech never
        // transcribed at all is the new one.
        QualityIsPoor = lowConfidence * 3 >= lines || coverage < Core.Domain.CallRun.PoorCoverage;
    }

    /// <summary>
    /// Marks the line the player is inside.
    ///
    /// The point is reading along: somebody checking a quote is looking at the text and listening
    /// at the same time, and without this they have to find their place again after every seek.
    /// </summary>
    /// <summary>
    /// The line the player is inside, whenever it changes.
    ///
    /// Raised rather than bound because following it is a view concern — it means scrolling a
    /// container, and only the view knows where the line has ended up on screen. Raised only on a
    /// change, not on every position tick: the player reports several times a second and a line
    /// lasts seconds, so re-scrolling to the same bubble would be a permanent gentle twitch.
    /// </summary>
    public event EventHandler<ChatTurn?>? CurrentTurnChanged;

    private ChatTurn? _currentTurn;

    private void Highlight(int positionMs) => Highlight(Turns, positionMs, ref _currentTurn, CurrentTurnChanged, this);

    /// <summary>
    /// Marks every line being spoken at <paramref name="positionMs"/> and returns the one the
    /// view should travel with.
    ///
    /// <b>Every line, not one of them.</b> Marking whichever line had started most recently is
    /// the same thing only while people take turns, and they do not: in this archive 144 of one
    /// call's 217 lines overlap another. A nine-second turn with a half-second "iyi" inside it
    /// moved the mark onto the interjection and left it there — the long line kept playing with
    /// nothing marked, and the view had scrolled away from it.
    ///
    /// Marking both is also the honest answer to what the reader is looking at. Two outlined
    /// bubbles say "these were said over each other", which lines stacked top to bottom cannot.
    ///
    /// The one returned is the EARLIEST line still in progress. Following the newest would scroll
    /// to a half-second interjection and straight back, twice a second.
    ///
    /// Static and over a plain list so the decision can be checked without a window, a
    /// repository or an audio device.
    /// </summary>
    public static ChatTurn? Spoken(IReadOnlyList<ChatTurn> turns, int positionMs)
    {
        ChatTurn? speaking = null;   // earliest line still in progress
        ChatTurn? lastEnded = null;  // the line that finished most recently, for the gaps

        foreach (var turn in turns)
        {
            if (turn.StartMs > positionMs)
            {
                turn.IsCurrent = false;
                continue;
            }

            var live = positionMs < turn.EndMs;
            turn.IsCurrent = live;

            if (live) speaking ??= turn;
            else if (lastEnded is null || turn.EndMs > lastEnded.EndMs) lastEnded = turn;
        }

        // Between two lines nothing is being spoken. The one that finished most recently keeps
        // the mark rather than the transcript going blank every time somebody draws breath — and
        // most recently FINISHED, not most recently started: after a long turn that ran over a
        // short interjection, the words still in the reader's ear are the long one's.
        var anchor = speaking ?? lastEnded;

        if (anchor is not null) anchor.IsCurrent = true;

        return anchor;
    }

    /// <summary>
    /// Where the scroller should sit for the line being spoken, or <paramref name="current"/> when
    /// it is already comfortably readable.
    ///
    /// A third of the way down rather than at an edge: what was just said stays visible, which is
    /// most of what makes reading along work, and there is room for the lines about to arrive.
    /// A bubble taller than the viewport goes to the top — a third of a negative gap would scroll
    /// past the start of the very line being followed.
    ///
    /// Pure, and separate from the window, because this is the part that can be wrong in a way
    /// nobody notices until they are watching a conversation play.
    /// </summary>
    /// <summary>How long the transcript stops following after the reader scrolls it by hand.</summary>
    public const long ResumeFollowingAfterMs = 10_000;

    /// <summary>
    /// Whether the transcript should travel with the playhead right now.
    ///
    /// Following used to stop for good at the first turn of the wheel, and only pressing play
    /// again brought it back. That reads as broken: the recording is still playing, the highlight
    /// is moving somewhere below the fold, and nothing on screen says why the text stopped
    /// keeping up or how to make it start. Reported as "auto scroll still does not work", which
    /// is what it looked like from the outside.
    ///
    /// So the pause is a pause. Scrolling back to re-read a minute ago still wins immediately —
    /// that is the thing this window is for — and when the reader stops, the transcript rejoins
    /// the audio on its own. Ten seconds is long enough to read the two or three lines somebody
    /// scrolls back for, and short enough that nobody has to wonder what to press.
    /// </summary>
    public static bool ShouldFollow(long nowMs, long lastManualScrollMs) =>
        lastManualScrollMs <= 0 || nowMs - lastManualScrollMs >= ResumeFollowingAfterMs;

    public static double FollowOffset(
        double top, double height, double current, double viewport, double extent)
    {
        var furthest = Math.Max(0, extent - viewport);

        // Already in view with room to breathe: leave it. Re-scrolling on every line would twitch
        // the transcript under the reader even when nothing needed to move.
        var margin = Math.Min(viewport / 6, 80);

        if (top >= current + margin && top + height <= current + viewport - margin) return current;

        var visible = Math.Min(height, viewport);
        return Math.Clamp(top - ((viewport - visible) / 3), 0, furthest);
    }

    private static void Highlight(
        IReadOnlyList<ChatTurn> turns,
        int positionMs,
        ref ChatTurn? currentTurn,
        EventHandler<ChatTurn?>? changed,
        object sender)
    {
        var anchor = Spoken(turns, positionMs);

        if (ReferenceEquals(anchor, currentTurn)) return;

        currentTurn = anchor;
        changed?.Invoke(sender, anchor);
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

    // ---- the consistency check ---------------------------------------------
    //
    // A deliberate, separately billed read of this one conversation for what can be SHOWN:
    // contradictions, evaded questions, timelines that do not add up. Not a lie detector —
    // every finding below carries a verified quote, and everything unverifiable was dropped
    // before it got here.

    public ObservableCollection<ConsistencyRow> ConsistencyFindings { get; } = [];

    /// <summary>What held together — the balancing list.</summary>
    public ObservableCollection<string> ConsistencyObservations { get; } = [];

    /// <summary>The model's justified warning to the user, or null when the evidence earned none.</summary>
    [ObservableProperty] private string? _consistencyWarning;

    /// <summary>Which model read this, and when — a finding is that model's reading, not a verdict.</summary>
    [ObservableProperty] private string? _consistencyStamp;

    [ObservableProperty] private string? _consistencyMessage;
    [ObservableProperty] private string? _consistencyProblem;
    [ObservableProperty] private bool _isCheckingConsistency;

    public bool HasConsistencyRun => ConsistencyFindings.Count > 0 || ConsistencyStamp is not null;

    /// <summary>
    /// What pressing the button would roughly cost, said BEFORE the spend: the check sends the
    /// whole transcript in one request by design, which makes it the most expensive single
    /// click in the application — and a price you only learn from the bill is a trap.
    /// </summary>
    [ObservableProperty] private string? _consistencyCostLine;

    private void UpdateConsistencyCostLine(int transcriptChars)
    {
        if (transcriptChars == 0)
        {
            ConsistencyCostLine = null;
            return;
        }

        // Same ~4 chars/token rule the chunker budgets with. An estimate, and labelled as one.
        var tokens = transcriptChars / 4;

        ConsistencyCostLine = tokens >= 1000
            ? $"Tahmini girdi: ~{tokens / 1000} bin belirteç"
            : $"Tahmini girdi: ~{tokens} belirteç";

        // Balance, only where an endpoint publishes one. Fetched in the background; the line
        // simply grows a tail when the answer lands.
        var settings = _settings();

        if (settings.LlmProvider == LlmProviderKind.OpenRouter
            && !string.IsNullOrWhiteSpace(settings.LlmApiKey))
        {
            _ = AppendBalanceAsync(ConsistencyCostLine);
        }
    }

    private async Task AppendBalanceAsync(string prefix)
    {
        try
        {
            var balance = await Core.Llm.LlmBalance.OpenRouterAsync(
                _http, _settings().LlmApiKey, CancellationToken.None);

            if (balance is not null && ConsistencyCostLine == prefix)
                ConsistencyCostLine = $"{prefix} · {balance}";
        }
        catch (Exception)
        {
            // The line is a courtesy; a failed balance probe must never surface as an error.
        }
    }

    [RelayCommand]
    private async Task CheckConsistencyAsync(CancellationToken cancellationToken)
    {
        if (IsCheckingConsistency) return;

        var settings = _settings();

        if (!settings.LlmReachableInPrinciple)
        {
            ConsistencyProblem =
                "Bağlı bir yapay zekâ servisi yok. Ayarlar → Çözümleme bölümünden bir sağlayıcı seç.";
            return;
        }

        IsCheckingConsistency = true;
        ConsistencyProblem = null;
        ConsistencyMessage = null;

        try
        {
            var client = LlmClientFactory.Create(
                _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

            var model = settings.ResolvedConsistencyModel;

            var report = await new ConsistencyAnalysis(client, _repository).RunAsync(
                CallId,
                model,
                useLedgerContext: settings.ConsistencyUsesLedgerContext,
                otherPartyOnly: settings.ConsistencyOtherPartyOnly,
                sendsDataOffMachine: settings.Provider.SendsDataOffMachine,
                cancellationToken);

            if (!report.Ok)
            {
                ConsistencyProblem = report.Problem;
                return;
            }

            ConsistencyFindings.Clear();
            foreach (var flag in report.Findings)
                ConsistencyFindings.Add(new ConsistencyRow(flag, _repository));

            ConsistencyObservations.Clear();
            foreach (var observation in report.Observations) ConsistencyObservations.Add(observation);

            ConsistencyWarning = report.Warning;
            ConsistencyStamp = $"{model} · {DateTime.Now:d MMMM yyyy}";

            ConsistencyMessage = report.Findings.Count == 0
                ? report.Insufficient
                    ? "Döküm anlamlı bir denetim için çok kısa."
                    : "Bulgu yok — kısa ve sıradan konuşmalarda bu olağandır."
                : report.RejectedCount > 0
                    ? $"{report.RejectedCount} bulgu, alıntısı dökümde bulunamadığı için elendi."
                    : null;

            OnPropertyChanged(nameof(HasConsistencyRun));
            RefreshAttention();
        }
        catch (Exception e)
        {
            ConsistencyProblem = $"Denetim tamamlanamadı: {e.Message}";
        }
        finally
        {
            IsCheckingConsistency = false;
        }
    }

    // ---- suggested actions --------------------------------------------------
    //
    // The user's proposed next moves, machine-suggested and user-routed. Open rows only:
    // done/hidden/routed suggestions are the user's history with the list, not display.

    public ObservableCollection<ActionRow> Actions { get; } = [];

    [ObservableProperty] private bool _isExtractingActions;
    [ObservableProperty] private string? _actionsMessage;

    public bool HasActions => Actions.Count > 0;

    private void LoadActions()
    {
        Actions.Clear();
        foreach (var action in _repository.ActionsOf(CallId, includeClosed: false))
            Actions.Add(new ActionRow(action));

        OnPropertyChanged(nameof(HasActions));
    }

    /// <summary>The user's verdict on one suggestion, applied and reflected immediately.</summary>
    public void SetActionStatus(ActionRow row, ActionStatus status, string? note = null)
    {
        _repository.SetActionStatus(row.Item.Id, status, note);
        Actions.Remove(row);
        OnPropertyChanged(nameof(HasActions));

        // The verdict is shared: the same suggestion sits on the home screen and the to-do page,
        // and both went on showing it as open until something else happened to refresh them.
        Services.CallActions.NotifyChanged();
    }

    [RelayCommand]
    private async Task ExtractActionsAsync(CancellationToken cancellationToken)
    {
        if (IsExtractingActions) return;

        var settings = _settings();

        if (!settings.LlmReachableInPrinciple)
        {
            ActionsMessage = "Bağlı bir yapay zekâ servisi yok. Ayarlar → Çözümleme bölümünden bir sağlayıcı seç.";
            return;
        }

        IsExtractingActions = true;
        ActionsMessage = null;

        try
        {
            var client = LlmClientFactory.Create(
                _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

            var report = await new ActionExtraction(client, _repository).RunAsync(
                CallId, settings.ResolvedModelName, cancellationToken);

            if (!report.Ok)
            {
                ActionsMessage = report.Problem;
                return;
            }

            LoadActions();

            ActionsMessage = report.Actions.Count == 0
                ? "Aksiyon çıkmadı — sıradan bir konuşmada bu olağandır."
                : report.RejectedCount > 0
                    ? $"{report.RejectedCount} öneri, alıntısı dökümde bulunamadığı için elendi."
                    : null;
        }
        catch (Exception e)
        {
            ActionsMessage = $"Çıkarılamadı: {e.Message}";
        }
        finally
        {
            IsExtractingActions = false;
        }
    }

    // ---- the model's reading ------------------------------------------------
    //
    // The one deliberately subjective surface, at the user's explicit request. Lives here
    // and nowhere else: never fed to other prompts, never written into evidence tables.

    [ObservableProperty] private ReadingReport? _reading;
    [ObservableProperty] private string? _readingStamp;
    [ObservableProperty] private string? _readingProblem;
    [ObservableProperty] private bool _isReadingRunning;

    public bool HasReading => Reading is { Ok: true };

    public bool CommentaryEnabled => _settings().CommentaryEnabled;

    private void LoadReading()
    {
        if (_repository.GetReading(CallId) is { } stored
            && ReadingAnalysis.FromStored(stored.Json) is { } report)
        {
            Reading = report;
            ReadingStamp = $"{stored.ModelUsed ?? "model"} · {stored.CreatedAt.ToLocalTime():d MMMM yyyy}";
        }
        else
        {
            Reading = null;
            ReadingStamp = null;
        }

        OnPropertyChanged(nameof(HasReading));
        OnPropertyChanged(nameof(CommentaryEnabled));
    }

    [RelayCommand]
    private async Task RunReadingAsync(CancellationToken cancellationToken)
    {
        if (IsReadingRunning) return;

        var settings = _settings();

        if (!settings.LlmReachableInPrinciple)
        {
            ReadingProblem = "Bağlı bir yapay zekâ servisi yok. Ayarlar → Çözümleme bölümünden bir sağlayıcı seç.";
            return;
        }

        IsReadingRunning = true;
        ReadingProblem = null;

        try
        {
            var client = LlmClientFactory.Create(
                _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

            var model = settings.ResolvedConsistencyModel;
            var report = await new ReadingAnalysis(client, _repository).RunAsync(
                CallId, model, settings.PreferredName, cancellationToken);

            if (!report.Ok)
            {
                ReadingProblem = report.Problem;
                return;
            }

            Reading = report;
            ReadingStamp = $"{model} · {DateTime.Now:d MMMM yyyy}";
            OnPropertyChanged(nameof(HasReading));
        }
        catch (Exception e)
        {
            ReadingProblem = $"Okuma tamamlanamadı: {e.Message}";
        }
        finally
        {
            IsReadingRunning = false;
        }
    }

    // ---- the opt-in deception assessment ------------------------------------
    //
    // The user's informed choice: an explicit opinion, delivered as an opinion. Its rows
    // feed the attention strip only at elevated levels, and always labelled as a view.

    [ObservableProperty] private DeceptionReport? _deception;
    [ObservableProperty] private string? _deceptionStamp;
    [ObservableProperty] private string? _deceptionProblem;
    [ObservableProperty] private bool _isDeceptionRunning;

    public bool HasDeception => Deception is { Ok: true };

    public bool DeceptionEnabled => _settings().DeceptionEnabled;

    public string DeceptionLevelLine => Deception?.Level switch
    {
        "yok" => "Şüphe düzeyi: belirti yok",
        "dusuk" => "Şüphe düzeyi: düşük",
        "orta" => "Şüphe düzeyi: orta",
        "yuksek" => "Şüphe düzeyi: yüksek",
        _ => "",
    };

    private void LoadDeception()
    {
        if (_repository.GetDeception(CallId) is { } stored
            && DeceptionAnalysis.FromStored(stored.Json) is { } report)
        {
            Deception = report;
            DeceptionStamp = $"{stored.ModelUsed ?? "model"} · {stored.CreatedAt.ToLocalTime():d MMMM yyyy}";
        }
        else
        {
            Deception = null;
            DeceptionStamp = null;
        }

        OnPropertyChanged(nameof(HasDeception));
        OnPropertyChanged(nameof(DeceptionLevelLine));
        OnPropertyChanged(nameof(DeceptionEnabled));
    }

    [RelayCommand]
    private async Task RunDeceptionAsync(CancellationToken cancellationToken)
    {
        if (IsDeceptionRunning) return;

        var settings = _settings();

        if (!settings.LlmReachableInPrinciple)
        {
            DeceptionProblem = "Bağlı bir yapay zekâ servisi yok. Ayarlar → Çözümleme bölümünden bir sağlayıcı seç.";
            return;
        }

        IsDeceptionRunning = true;
        DeceptionProblem = null;

        try
        {
            var client = LlmClientFactory.Create(
                _http, settings.LlmProvider, settings.ResolvedBaseUrl, settings.LlmApiKey);

            var model = settings.ResolvedConsistencyModel;
            var report = await new DeceptionAnalysis(client, _repository).RunAsync(
                CallId, model, cancellationToken);

            if (!report.Ok)
            {
                DeceptionProblem = report.Problem;
                return;
            }

            Deception = report;
            DeceptionStamp = $"{model} · {DateTime.Now:d MMMM yyyy}";
            OnPropertyChanged(nameof(HasDeception));
            OnPropertyChanged(nameof(DeceptionLevelLine));
            RefreshAttention();
        }
        catch (Exception e)
        {
            DeceptionProblem = $"Değerlendirme tamamlanamadı: {e.Message}";
        }
        finally
        {
            IsDeceptionRunning = false;
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

/// <summary>One suggested action, dressed for the screen.</summary>
public sealed record ActionRow(ActionItem Item)
{
    public string Action => Item.Action;
    public string? Reason => Item.Reason;

    public Wpf.Ui.Controls.SymbolRegular KindIcon => Item.Kind switch
    {
        "yazili_teyit" => Wpf.Ui.Controls.SymbolRegular.DocumentCheckmark24,
        "gonderme" => Wpf.Ui.Controls.SymbolRegular.Send24,
        "soru" => Wpf.Ui.Controls.SymbolRegular.QuestionCircle24,
        "takip" => Wpf.Ui.Controls.SymbolRegular.Clock24,
        "hazirlik" => Wpf.Ui.Controls.SymbolRegular.ClipboardTaskListLtr24,
        _ => Wpf.Ui.Controls.SymbolRegular.ArrowRight24,
    };

    public string KindLabel => Item.Kind switch
    {
        "yazili_teyit" => "yazılı teyit",
        "gonderme" => "gönderme",
        "soru" => "soru",
        "takip" => "takip",
        "hazirlik" => "hazırlık",
        _ => "adım",
    };

    public bool HasDeadline => Item.DeadlineDate is not null || Item.DeadlineRaw is not null;

    public string DeadlineText => Item.DeadlineDate is { } day
        ? day.ToDateTime(TimeOnly.MinValue).ToString("d MMMM")
        : Item.DeadlineRaw ?? "";

    /// <summary>The anchoring words, playable via the existing excerpt path.</summary>
    public Excerpt Quote => new(0, Item.CallId, null, default, Item.QuoteStartMs, Item.QuoteIsMe, Item.Quote);
}

/// <summary>
/// One consistency finding, dressed for the screen: a Turkish kind label, the model's
/// confidence, and both quotes as playable excerpts. The counter side may live in an EARLIER
/// conversation — then it opens that conversation instead of playing here.
/// </summary>
public sealed record ConsistencyRow(Flag Flag, Repository Repository)
{
    public string KindLabel => Flag.Kind switch
    {
        FlagKind.Contradiction => "Çelişki",
        FlagKind.TimelineMismatch => "Zaman uyumsuzluğu",
        FlagKind.EvadedQuestion => "Cevapsız soru",
        FlagKind.VagueShift => "Belirsizleşme",
        FlagKind.PressureTactic => "Baskı işareti",
        _ => "Gözlem",
    };

    public Wpf.Ui.Controls.SymbolRegular KindIcon => Flag.Kind switch
    {
        FlagKind.Contradiction => Wpf.Ui.Controls.SymbolRegular.ArrowsBidirectional24,
        FlagKind.TimelineMismatch => Wpf.Ui.Controls.SymbolRegular.Clock24,
        FlagKind.EvadedQuestion => Wpf.Ui.Controls.SymbolRegular.QuestionCircle24,
        FlagKind.VagueShift => Wpf.Ui.Controls.SymbolRegular.WeatherFog24,
        FlagKind.PressureTactic => Wpf.Ui.Controls.SymbolRegular.Warning24,
        _ => Wpf.Ui.Controls.SymbolRegular.Info24,
    };

    public string Summary => Flag.Summary;

    public string ConfidenceLabel => Flag.Confidence switch
    {
        "yuksek" => "güven: yüksek",
        "orta" => "güven: orta",
        _ => "güven: düşük",
    };

    /// <summary>"Ses net değil" rides along when the transcriber itself was unsure.</summary>
    public bool AudioUnclear => Flag.LowConfidence;

    /// <summary>The main quote as a playable excerpt for PlayExcerptCommand.</summary>
    public Excerpt Quote => new(0, Flag.CallId, null, default, Flag.QuoteStartMs, IsMe: false, Flag.Quote);

    public bool HasCounter => Flag.CounterQuote is not null;

    /// <summary>True when the other end of the finding is in THIS conversation — playable here.</summary>
    public bool CounterIsHere => Flag.CounterCallId == Flag.CallId && Flag.CounterQuote is not null;

    /// <summary>True when it points at an earlier conversation — a click opens that one.</summary>
    public bool CounterIsElsewhere => HasCounter && !CounterIsHere;

    public Excerpt? CounterQuote => Flag is { CounterQuote: { } q, CounterQuoteStartMs: { } ms }
        ? new Excerpt(0, Flag.CounterCallId ?? Flag.CallId, null, default, ms, IsMe: false, q)
        : null;

    public string CounterHeading => CounterIsElsewhere
        ? Repository.GetCall(Flag.CounterCallId ?? 0)?.StartedAt.ToLocalTime()
              .ToString("d MMMM yyyy") is { } day
            ? $"Önceki görüşme · {day}:"
            : "Önceki görüşme:"
        : "Karşı ifade:";

    /// <summary>
    /// The reason the reminder dialog opens with when this finding is turned into a follow-up.
    /// The warning note's advice ("teyit et, tekrar sor") made executable: two clicks from the
    /// finding to a dated reminder, wording matched to what kind of finding it was.
    /// </summary>
    public string ReminderDraft
    {
        get
        {
            var quote = Flag.Quote.Length <= 80 ? Flag.Quote : Flag.Quote[..77] + "…";

            return Flag.Kind switch
            {
                FlagKind.EvadedQuestion => $"Şu soruyu tekrar sor: \"{quote}\"",
                FlagKind.Contradiction or FlagKind.TimelineMismatch =>
                    $"Yazılı teyit iste: \"{quote}\"",
                FlagKind.VagueShift => $"Netleştir: \"{quote}\"",
                _ => $"Üzerine git: \"{quote}\"",
            };
        }
    }
}
