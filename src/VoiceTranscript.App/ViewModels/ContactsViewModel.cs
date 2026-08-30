using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>One line of transcript, ready to display and to click.</summary>
public sealed partial class TranscriptLine(
    string speaker,
    string text,
    int startMs,
    int endMs,
    bool isMe,
    bool lowConfidence,
    bool suspectedEcho) : ObservableObject
{
    public string Speaker { get; } = speaker;
    public string Text { get; } = text;
    public int StartMs { get; } = startMs;
    public int EndMs { get; } = endMs;
    public bool IsMe { get; } = isMe;
    public bool LowConfidence { get; } = lowConfidence;
    public bool SuspectedEcho { get; } = suspectedEcho;

    /// <summary>
    /// True while this line is the one being heard.
    ///
    /// The transcript following the audio is what turns a wall of text into something a person
    /// can actually check a quote against — without it they have to hunt for the line by eye
    /// while the voice moves on.
    /// </summary>
    [ObservableProperty] private bool _isCurrent;

    public string Timestamp => $"{StartMs / 60000:00}:{StartMs / 1000 % 60:00}";

    /// <summary>Shown when the transcriber was unsure, so the user knows to listen rather than trust.</summary>
    public string? Warning => LowConfidence ? "ses net değil" : SuspectedEcho ? "yankı" : null;

    public bool HasWarning => Warning is not null;

    public bool Covers(int positionMs) => positionMs >= StartMs && positionMs < EndMs;
}

/// <summary>A ledger entry with its evidence attached.</summary>
public sealed record FlagView(Flag Flag)
{
    public string Summary => Flag.Summary;
    public string Quote => Flag.Quote.Trim();
    public string Timestamp => $"{Flag.QuoteStartMs / 60000:00}:{Flag.QuoteStartMs / 1000 % 60:00}";
    public string? CounterQuote => Flag.CounterQuote?.Trim();
    public bool HasCounter => Flag.CounterQuote is not null;

    /// <summary>
    /// Keyword matches say so wherever they appear, so a heuristic is never mistaken for
    /// something concluded about a person.
    /// </summary>
    public string? Caveat => Flag.IsHeuristic
        ? "anahtar kelime eşleşmesi — kesin bir tespit değildir"
        : Flag.LowConfidence
            ? "ses net değil, bu kayıt şüpheli olabilir"
            : null;

    public bool HasCaveat => Caveat is not null;

    public string Icon => Flag.Kind switch
    {
        FlagKind.OverdueCommitment => "⏰",
        FlagKind.MovedDeadline => "📅",
        FlagKind.ChangedAmount => "₺",
        FlagKind.Contradiction => "⚠",
        FlagKind.EvadedQuestion => "?",
        FlagKind.PressureTactic => "!",
        FlagKind.ScamPattern => "⚑",
        _ => "•",
    };

    public string Kind => Flag.Kind switch
    {
        FlagKind.OverdueCommitment => "Vadesi geçti",
        FlagKind.MovedDeadline => "Tarih kaydı",
        FlagKind.ChangedAmount => "Rakam değişti",
        FlagKind.Contradiction => "Çelişki",
        FlagKind.EvadedQuestion => "Cevapsız soru",
        FlagKind.PressureTactic => "Baskı",
        FlagKind.ScamPattern => "Dolandırıcılık kalıbı",
        _ => "Not",
    };
}

public sealed record ContactRow(Contact Contact, int OpenFlags)
{
    public string Name => Contact.Name;
    public string Detail => Contact.LastCallAt is { } last
        ? $"{Contact.CallCount} görüşme · {last.ToLocalTime():d MMM}"
        : $"{Contact.CallCount} görüşme";

    public bool HasFlags => OpenFlags > 0;
    public string FlagCount => OpenFlags.ToString();

    /// <summary>Initials for the avatar. Turkish casing, so "işçi" becomes "İ" not "I".</summary>
    public string Initials
    {
        get
        {
            var parts = Contact.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";

            var first = Core.Text.TurkishText.ToUpperTr(parts[0][..1]);
            return parts.Length == 1 ? first : first + Core.Text.TurkishText.ToUpperTr(parts[^1][..1]);
        }
    }
}

public sealed partial class ContactsViewModel : ObservableObject, IDisposable
{
    private readonly Repository repository;

    public ContactsViewModel(Repository repository)
    {
        this.repository = repository;

        // The transcript follows the audio. Without this somebody checking a quote has to hunt
        // for the right line by eye while the voice moves on past it.
        Playback.PositionChanged += (_, ms) => Highlight(ms);
    }

    /// <summary>Marks the line currently being heard, and clears the previous one.</summary>
    private void Highlight(int positionMs)
    {
        foreach (var line in Transcript)
        {
            var current = line.Covers(positionMs);
            if (line.IsCurrent != current) line.IsCurrent = current;
        }
    }

    /// <summary>
    /// The player under the transcript, with the waveform.
    ///
    /// Shared rather than a private AudioPlayer, because the drawing, the playhead and the
    /// highlighted line all have to agree about one position. Two of them would drift.
    /// </summary>
    public PlaybackViewModel Playback { get; } = new();

    public ObservableCollection<ContactRow> Contacts { get; } = [];
    public ObservableCollection<RecentCall> Calls { get; } = [];
    public ObservableCollection<TranscriptLine> Transcript { get; } = [];
    public ObservableCollection<FlagView> Flags { get; } = [];
    public ObservableCollection<Commitment> OpenCommitments { get; } = [];

    [ObservableProperty] private ContactRow? _selectedContact;
    [ObservableProperty] private RecentCall? _selectedCall;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _transcriptMessage;
    [ObservableProperty] private string? _playbackMessage;
    [ObservableProperty] private string _contactFilter = "";

    /// <summary>
    /// Narrows this contact's calls to the ones containing a word.
    ///
    /// Somebody with two hundred conversations with the same person cannot find the one where a
    /// price was agreed by scrolling dates. The search screen answers "where was this said"
    /// across the whole archive; this answers "which of *these* was it in", which is the
    /// question being asked while already looking at a person.
    /// </summary>
    [ObservableProperty] private string _callFilter = "";

    /// <summary>Narrows this contact's calls to a stretch of time.</summary>
    [ObservableProperty] private SearchPeriod _callPeriod = SearchPeriod.Anytime;

    /// <summary>Every call with the selected contact, before filtering.</summary>
    private readonly List<RecentCall> _allCalls = [];

    public IReadOnlyList<SearchPeriod> Periods { get; } = Enum.GetValues<SearchPeriod>();

    /// <summary>How many calls the filters are hiding, for a line that says so.</summary>
    public int HiddenCallCount => Math.Max(0, _allCalls.Count - Calls.Count);

    public bool IsFilteringCalls =>
        !string.IsNullOrWhiteSpace(CallFilter) || CallPeriod != SearchPeriod.Anytime;

    partial void OnCallFilterChanged(string value) => ApplyCallFilters();

    partial void OnCallPeriodChanged(SearchPeriod value) => ApplyCallFilters();

    /// <summary>Clears both filters at once, from the line that reports them.</summary>
    [RelayCommand]
    private void ClearCallFilters()
    {
        CallFilter = "";
        CallPeriod = SearchPeriod.Anytime;
    }

    /// <summary>
    /// Rebuilds the visible call list from the full one.
    ///
    /// The text filter goes through the search index rather than matching the row's own words,
    /// because the row shows a date and a status — none of what was actually said. Matching what
    /// is on screen would mean the box could only find dates, which is the one thing the list
    /// already sorts by.
    ///
    /// The selection is kept when it survives the filter. Retyping a search term one character
    /// at a time would otherwise throw away the transcript being read at every keystroke.
    /// </summary>
    private void ApplyCallFilters()
    {
        var kept = SelectedCall;

        IReadOnlySet<long>? mentioning = null;
        if (!string.IsNullOrWhiteSpace(CallFilter) && SelectedContact is { } contact)
            mentioning = repository.CallsMentioning(contact.Contact.Id, CallFilter);

        var since = CallPeriod.Since();
        var until = CallPeriod.Until();

        Calls.Clear();

        foreach (var call in _allCalls)
        {
            if (since is not null && call.Call.StartedAt < since) continue;
            if (until is not null && call.Call.StartedAt >= until) continue;
            if (mentioning is not null && !mentioning.Contains(call.Call.Id)) continue;

            Calls.Add(call);
        }

        OnPropertyChanged(nameof(HiddenCallCount));
        OnPropertyChanged(nameof(IsFilteringCalls));
        OnPropertyChanged(nameof(HasVisibleCalls));

        SelectedCall = kept is not null && Calls.Contains(kept) ? kept : Calls.FirstOrDefault();
    }

    public bool HasVisibleCalls => Calls.Count > 0;

    /// <summary>
    /// Share of the talking that was mine, 0-1.
    ///
    /// This measurement falls out of the recording design for nothing, and no tool that records a
    /// call as one mixed stream can produce it honestly — it would have to guess at who was
    /// speaking, and guess wrong exactly where two people talk over each other. Here it is a
    /// fact: the two streams were captured separately, so the arithmetic is just addition.
    /// </summary>
    [ObservableProperty] private double _talkRatio = 0.5;

    [ObservableProperty] private string? _talkSummary;

    /// <summary>How many times each side started speaking while the other still was.</summary>
    [ObservableProperty] private string? _interruptionSummary;

    public bool HasTalkStats => TalkSummary is not null;

    partial void OnTalkSummaryChanged(string? value) => OnPropertyChanged(nameof(HasTalkStats));

    public bool HasContacts => Contacts.Count > 0;
    public bool HasSelection => SelectedContact is not null;
    public bool HasCall => SelectedCall is not null;
    public bool HasTranscript => Transcript.Count > 0;
    public bool HasFlags => Flags.Count > 0;

    [RelayCommand]
    public void Refresh()
    {
        var previous = SelectedContact?.Contact.Id;

        Contacts.Clear();

        var filter = ContactFilter.Trim();
        var contacts = string.IsNullOrEmpty(filter)
            ? repository.ListContacts()
            : repository.FindContacts(filter);

        foreach (var contact in contacts)
            Contacts.Add(new ContactRow(contact, repository.GetFlags(contact.Id).Count));

        OnPropertyChanged(nameof(HasContacts));

        if (previous is { } id)
            SelectedContact = Contacts.FirstOrDefault(c => c.Contact.Id == id);
    }

    partial void OnContactFilterChanged(string value) => Refresh();

    public void Select(long contactId, long? callId = null)
    {
        SelectedContact = Contacts.FirstOrDefault(c => c.Contact.Id == contactId);

        if (callId is { } id)
            SelectedCall = Calls.FirstOrDefault(c => c.Call.Id == id);
    }

    /// <summary>Who a call belongs to, or null when nobody has said yet.</summary>
    public long? ContactIdOf(long callId) => repository.GetCall(callId)?.ContactId;

    /// <summary>
    /// Moves playback to a moment in the open call.
    ///
    /// Deferred until the waveform has finished loading. Seeking into a player that has not read
    /// its file yet is silently ignored, which looks like a citation that does not work — and a
    /// citation that does not work undermines the only thing making the answer above it
    /// trustworthy.
    /// </summary>
    public void SeekTo(int startMs)
    {
        if (Playback.IsLoaded)
        {
            Playback.PlayFrom(startMs, isMe: false);
            return;
        }

        void WhenLoaded(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PlaybackViewModel.IsLoaded) || !Playback.IsLoaded) return;

            Playback.PropertyChanged -= WhenLoaded;
            Playback.PlayFrom(startMs, isMe: false);
        }

        Playback.PropertyChanged += WhenLoaded;
    }

    partial void OnSelectedContactChanged(ContactRow? value)
    {
        Calls.Clear();
        _allCalls.Clear();
        Flags.Clear();
        OpenCommitments.Clear();
        SelectedCall = null;

        OnPropertyChanged(nameof(HasSelection));

        if (value is null) return;

        _allCalls.Clear();

        foreach (var call in repository.ListCalls(value.Contact.Id))
            _allCalls.Add(new RecentCall(call, value.Contact.Name));

        // The filters are deliberately not reset when the contact changes: somebody narrowing to
        // "this week" is usually working through several people with the same question.
        ApplyCallFilters();

        foreach (var flag in repository.GetFlags(value.Contact.Id))
            Flags.Add(new FlagView(flag));

        foreach (var commitment in repository.GetOpenCommitments(value.Contact.Id))
            OpenCommitments.Add(commitment);

        OnPropertyChanged(nameof(HasFlags));

    }

    partial void OnSelectedCallChanged(RecentCall? value)
    {
        Transcript.Clear();
        Summary = null;
        TranscriptMessage = null;

        OnPropertyChanged(nameof(HasCall));

        if (value is null) return;

        var contactName = SelectedContact?.Name ?? "Karşı taraf";

        foreach (var segment in repository.GetSegments(value.Call.Id))
        {
            Transcript.Add(new TranscriptLine(
                segment.IsMe ? "Ben" : contactName,
                segment.Text.Trim(),
                segment.StartMs,
                segment.EndMs,
                segment.IsMe,
                segment.LowConfidence,
                segment.SuspectedEcho));
        }

        Summary = repository.GetSummary(value.Call.Id)?.Summary;
        OnPropertyChanged(nameof(HasTranscript));

        ComputeTalkStats(repository.GetSegments(value.Call.Id));

        // The waveform is read off the UI thread; an hour of audio is over a hundred megabytes.
        _ = Playback.LoadAsync(value.Call.MicPath, value.Call.FarPath, value.Call.Duration);

        // States that are normal must not read like errors.
        if (Transcript.Count == 0)
        {
            TranscriptMessage = value.Call.Kind == CallKind.Group
                ? "Grup araması. Karşı taraftaki herkes tek ses akışında karıştığı için kimin ne " +
                  "söylediği kesin bilinemez; yazıya dökülmedi, ses kaydı duruyor."
                : value.Call.State switch
                {
                    ProcessingState.Recorded or ProcessingState.Queued =>
                        "Sırada bekliyor. Görüşme bittikten sonra işleniyor.",
                    ProcessingState.Transcribing => "Şu anda yazıya dökülüyor…",
                    ProcessingState.Analysing => "Çözümleniyor…",
                    // Summarised rather than printed. A Python traceback is twenty lines of
                    // somebody else's file paths ending in the one line that matters, and
                    // putting it here buried the transcript under it.
                    ProcessingState.Failed =>
                        $"İşlenemedi: {Core.Asr.FailureText.Summarise(value.Call.FailureReason)}",
                    ProcessingState.Skipped => "Bu kayıt atlandı.",
                    _ => "Bu görüşmenin metni yok.",
                };
        }
    }

    /// <summary>
    /// Works out who did the talking.
    ///
    /// Speaking time is summed per stream, and an interruption is counted when one side starts
    /// while the other is still going. Both are stated as counts with the seconds behind them
    /// rather than as a verdict: "sen %62 konuştun" is a fact somebody can check, whereas
    /// "karşı taraf seni sürekli böldü" is a judgement this application has no business making.
    /// </summary>
    private void ComputeTalkStats(IReadOnlyList<Core.Domain.Segment> segments)
    {
        if (segments.Count == 0)
        {
            TalkSummary = null;
            InterruptionSummary = null;
            TalkRatio = 0.5;
            return;
        }

        var mine = TimeSpan.Zero;
        var theirs = TimeSpan.Zero;

        foreach (var segment in segments)
        {
            var length = TimeSpan.FromMilliseconds(Math.Max(0, segment.EndMs - segment.StartMs));
            if (segment.IsMe) mine += length; else theirs += length;
        }

        var total = mine + theirs;
        if (total <= TimeSpan.Zero)
        {
            TalkSummary = null;
            return;
        }

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

            // Started before the previous speaker finished, and by a different speaker.
            if (current.IsMe == previous.IsMe || current.StartMs >= previous.EndMs) continue;

            if (current.IsMe) myCuts++; else theirCuts++;
        }

        InterruptionSummary = myCuts + theirCuts == 0
            ? "Kimse kimsenin sözünü kesmedi."
            : $"Söz kesme: sen {myCuts}, karşı taraf {theirCuts}.";
    }

    /// <summary>Raised when an action needs something only the shell can do.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>Set by the view once the user has confirmed, so deletion is never one click.</summary>
    [ObservableProperty] private bool _isDeleting;

    /// <summary>
    /// Removes a person from the archive entirely.
    ///
    /// Audio, transcript, search index, extracted facts and exported files, in one operation.
    /// This is the promise the whole product rests on: a recording of somebody talking is theirs
    /// as much as it is yours, and "delete" that leaves the audio on disk or the words in a
    /// search index is not deletion. It is deliberately irreversible and the view asks first.
    /// </summary>
    public Core.Storage.DeletionResult? DeleteSelectedContact()
    {
        if (SelectedContact is not { } row) return null;

        var result = repository.DeleteContactCompletely(row.Contact.Id);

        SelectedContact = null;
        Refresh();

        return result;
    }

    /// <summary>
    /// Puts one recording back through transcription and analysis.
    ///
    /// The audio is intact, so a failure is nearly always transient — a busy device, a rate
    /// limit, a model still downloading. Asking somebody to re-record a conversation because of
    /// that would be absurd, and impossible anyway.
    /// </summary>
    public async Task ReprocessSelectedCallAsync(Services.CallOrchestrator orchestrator)
    {
        if (SelectedCall is not { } call) return;

        TranscriptMessage = "Yeniden işleniyor…";

        try
        {
            await orchestrator.ReprocessAsync(call.Call.Id);
            Notice?.Invoke(this, "Görüşme yeniden işlendi.");
        }
        catch (Exception e)
        {
            Notice?.Invoke(this, $"Yeniden işlenemedi: {e.Message}");
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>
    /// Plays the recording from the moment a line was spoken.
    ///
    /// The reason word-level timestamps are kept at all: every claim the application makes can
    /// be checked by listening rather than taken on trust.
    /// </summary>
    [RelayCommand]
    private void PlayFrom(int startMs)
    {
        if (SelectedCall is not { } call) return;

        var line = Transcript.FirstOrDefault(l => l.StartMs == startMs);
        var isMe = line?.IsMe ?? false;
        var path = isMe ? call.Call.MicPath : call.Call.FarPath;

        if (path is null || !File.Exists(path))
        {
            PlaybackMessage = "Ses dosyası bulunamadı.";
            return;
        }

        try
        {
            Playback.PlayFrom(startMs, isMe);
            PlaybackMessage = null;
        }
        catch (Exception e)
        {
            PlaybackMessage = $"Ses çalınamadı: {e.Message}";
        }
    }

    [RelayCommand]
    private void PlayFlag(FlagView flag) => PlayFrom(flag.Flag.QuoteStartMs);

    [RelayCommand]
    private void StopPlayback() => Playback.Stop();

    /// <summary>
    /// Dismisses a ledger entry for good.
    ///
    /// Without it false positives accumulate until the ledger is noise and the user stops
    /// reading it — at which point the real findings are lost too.
    /// </summary>
    [RelayCommand]
    private void DismissFlag(FlagView flag)
    {
        repository.DismissFlag(flag.Flag.Id);
        Flags.Remove(flag);
        OnPropertyChanged(nameof(HasFlags));
    }

    /// <summary>
    /// Removes the selected contact, everything of theirs, and the recordings.
    ///
    /// The file deletion lives in the repository rather than here. It used to be duplicated at
    /// each call site, and the copy behind the toolbar button had been written without it — so
    /// pressing delete removed the transcript and left the audio of somebody talking sitting on
    /// disk, while telling the user it was gone. One caller cannot forget what only one method
    /// does.
    /// </summary>
    [RelayCommand]
    private void DeleteContact() => DeleteSelectedContact();

    public void Dispose() => Playback.Dispose();
}
