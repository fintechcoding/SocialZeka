using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.ViewModels;

// The screen asks two different questions, and its history shows what merging them cost: a call
// whose ANALYSIS failed sat between calls whose TRANSCRIPTION failed, wearing the same red, and
// the user's verdict was "kafa karıştırıyor ve çok hata çıktı". Did the audio become text, and
// did the text become a ledger — separate questions, separate tabs, separate filters.

/// <summary>
/// Which rows the transcription tab is showing. Three DISJOINT states, deliberately: a
/// separate "Başarısızlar" filter overlapped "Bekleyenler" (a failure IS pending work), so the
/// same row appeared under two buttons and the user called the arrangement absurd — correctly.
/// A failed row is a pending row with a red reason on it, and the reason is already visible.
/// </summary>
public enum TranscriptFilter
{
    /// <summary>Waiting, running, failed, or still without text — everything not done.</summary>
    Unfinished,

    /// <summary>The text exists.</summary>
    Done,

    /// <summary>
    /// Every call the pipeline gave up on, whichever stage gave up and whether or not anything
    /// can still be done about it.
    ///
    /// It is its own filter because "islenemedi" is one idea to the person reading it and three
    /// to the code: a transcription that failed, a transcription that failed after producing
    /// text, and a recording that captured nothing at all. Sorted into the two tabs by which
    /// stage was at fault, the first screen's "4 gorusme islenemedi - Goster" landed on a list
    /// that could not contain a single one of them.
    /// </summary>
    Failed,

    All,
}

/// <summary>Which rows the analysis tab is showing. Same three disjoint states.</summary>
public enum AnalyseFilter
{
    /// <summary>Text exists, ledger does not — analysis failures included, worn in red.</summary>
    Unanalysed,

    /// <summary>Ledger built.</summary>
    Done,

    All,
}

/// <summary>
/// A request to redo some recordings, and how.
///
/// Carries the route rather than leaving it to the settings, because a recording is queued again
/// precisely when its usual route failed: repeating it is the one thing already known not to work.
/// </summary>
/// <param name="AsrModelId">Transcription engine from the catalogue, or null for the configured one.</param>
/// <param name="LlmModel">Analysis model, or null for the configured one.</param>
/// <param name="AnalyseOnly">Keep the existing text and rebuild only the ledger.</param>
public sealed record ReprocessRequest(
    IReadOnlyList<long> Ids, string? AsrModelId, string? LlmModel, bool AnalyseOnly);

/// <summary>One recording, as this screen shows it.</summary>
public sealed record ProcessingRow(Call Call, string ContactName, int SegmentCount)
{
    public long Id => Call.Id;

    public string When => Call.StartedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    public string Length => $"{(int)Call.Duration.TotalMinutes:00}:{Call.Duration.Seconds:00}";

    public string AppName => Call.App.ToString();

    /// <summary>Whether a transcript exists, regardless of what the state field says.</summary>
    public bool HasTranscript => SegmentCount > 0;

    public string TranscriptState => Call.State switch
    {
        ProcessingState.Recorded or ProcessingState.Queued => "Sırada",
        ProcessingState.Transcribing => "Yazıya dökülüyor",
        ProcessingState.Skipped => CallStateText.Skipped(Call.FailureReason),
        ProcessingState.Failed when SegmentCount > 0 => $"{SegmentCount} satır (sonra hata verdi)",
        ProcessingState.Failed => "İşlenemedi",
        _ => SegmentCount > 0 ? $"{SegmentCount} satır" : "Metin yok",
    };

    public string AnalysisState => Call.State switch
    {
        ProcessingState.Analysed => "Hazır",
        ProcessingState.Analysing => "Çözümleniyor",
        ProcessingState.Transcribed => "Çözümlenmedi",
        ProcessingState.Failed => "İşlenemedi",
        ProcessingState.Skipped => CallStateText.Skipped(Call.FailureReason),
        _ => "—",
    };

    public bool IsFailed => Call.State == ProcessingState.Failed;

    /// <summary>The audio never became text. This row belongs to the transcription tab's reds.</summary>
    public bool TranscriptFailed => IsFailed && !HasTranscript;

    /// <summary>The text exists and the ledger step failed — a different fault, a different tab.</summary>
    public bool AnalysisFailed => IsFailed && HasTranscript;

    public bool IsWorking => Call.State
        is ProcessingState.Transcribing or ProcessingState.Analysing;

    public bool IsWaiting => Call.State is ProcessingState.Recorded or ProcessingState.Queued;

    /// <summary>
    /// Why it failed, in words rather than in a stack trace. Only for real failures — guidance
    /// has its own, calmer row below.
    /// </summary>
    public string? Failure => IsFailed && !IsGuidanceFailure
        ? Core.Asr.FailureText.Summarise(Call.FailureReason)
        : null;

    /// <summary>
    /// A "failure" that is actually the application pointing at a setting — "yapılandırılmış
    /// bir servis yok, Ayarlar bölümünden…". A real user read this in red under "Hatanın
    /// tamamı" and concluded the product was broken; it gets an info look and a button to the
    /// section it names instead.
    /// </summary>
    public bool IsGuidanceFailure => IsFailed && Core.Asr.FailureText.IsGuidance(Call.FailureReason);

    public string? GuidanceText => IsGuidanceFailure
        ? Core.Asr.FailureText.Summarise(Call.FailureReason)
        : null;

    /// <summary>
    /// Status written onto a call that did NOT fail — "çözümleme yapılmadı, servis yok" on a
    /// Transcribed row. Information, and dressed as such: it used to wear the failure red.
    /// </summary>
    public string? StateNote => !IsFailed && !string.IsNullOrWhiteSpace(Call.FailureReason)
        ? Core.Asr.FailureText.Summarise(Call.FailureReason)
        : null;

    /// <summary>The expander earns its place only when there is more than the summary line.</summary>
    public string? RawFailure =>
        IsFailed && !IsGuidanceFailure && Core.Asr.FailureText.HasDetail(Call.FailureReason)
            ? Call.FailureReason
            : null;

    /// <summary>True when the audio is still on disk, so retrying is possible at all.</summary>
    public bool HasAudio => !string.IsNullOrWhiteSpace(Call.MicPath) || !string.IsNullOrWhiteSpace(Call.FarPath);

    /// <summary>
    /// Whether transcription can still do anything about this row.
    ///
    /// A capture that never started leaves a row behind with a reason and no audio — "The audio
    /// device has been disconnected or the audio hardware has been reconfigured", duration 00:00,
    /// nothing on disk. It is a failed recording, not a pending transcription, and there is no
    /// second attempt that could change it: the retry button is already disabled for these, and
    /// requeueing already skips them ("7 görüşme yeniden kuyruğa alındı. 2 tanesi atlandı").
    ///
    /// Only the waiting list had not been told. So two rows from two nights ago sat at the top of
    /// "Bekleyenler" for good, and the red counter beside it read 2 and could never reach zero —
    /// a backlog figure that no amount of work would clear, which is the same fault the pending
    /// count itself was written to fix. They are still in "Hepsi", and still deletable there.
    /// </summary>
    /// <summary>
    /// Whether the machine still has this row to do.
    ///
    /// Waiting means waiting. A row that failed is not queued behind anything and nothing is
    /// going to pick it up: it needs a person to choose another engine or to let it go, and it
    /// has its own filter now that says exactly that. Counted as pending, one recording that came
    /// back with "konuşma bulunamadı" sat at the top of "Bekleyenler" permanently with a number
    /// beside it that no amount of work could clear — the same fault the pending count was
    /// written to fix, arriving from a third direction.
    /// </summary>
    public bool NeedsTranscription =>
        IsWaiting
        || IsWorking
        || (HasAudio && !HasTranscript
            && Call.State is not (ProcessingState.Skipped or ProcessingState.Failed));
}

/// <summary>
/// What has been processed, what has not, and what went wrong.
///
/// This screen exists because the answer to "is it working" was previously unavailable. A
/// recording that failed showed a single word on the first screen and nothing else; a recording
/// stuck in the queue looked identical to one being worked on; and a run of failures caused by one
/// missing Python package looked like eight separate mysteries. The user's words, after installing
/// on a machine where the worker was not set up: <i>"çözümlenmemiş ses kayıtlarını görüp tekrar
/// transcript edebileceğimiz bir sistem yok mu?"</i>
///
/// It matters more on a machine without a usable GPU, where transcription runs several times
/// slower than real time: a long call can be worked on for an hour, and with nothing on screen
/// saying so, an application that is working looks exactly like one that has hung.
/// </summary>
public sealed partial class ProcessingViewModel(
    Repository repository, Func<AppSettings>? settings = null) : ObservableObject
{
    /// <summary>Did the audio become text — the transcription tab's rows.</summary>
    public ObservableCollection<ProcessingRow> TranscriptRows { get; } = [];

    /// <summary>Did the text become a ledger — the analysis tab's rows. Only calls with text.</summary>
    public ObservableCollection<ProcessingRow> AnalysisRows { get; } = [];

    /// <summary>
    /// Which engine will do the work, named on the live line.
    ///
    /// "Yazıya dökülüyor" answers what is happening and not what is doing it, and on this product
    /// those are different questions with different answers: the same sentence covers a local
    /// model grinding at a fifth of real time and an upload to a hosted one. It was said once, in
    /// a toast that disappears, at the moment somebody was least likely to be looking.
    /// </summary>
    private string? ActiveEngine
    {
        get
        {
            // The running job's own answer wins over anything the settings imply.
            //
            // They disagree routinely and the settings are the one that can be wrong: an engine
            // picked in the reprocess dialog belongs to that recording alone, the automatic mode
            // decides per call against the graphics card as it is right now, and the setting can
            // be changed while a job is already in flight. Reading the setting, this line told a
            // user their call was going to a hosted service while it was being transcribed on
            // their own machine.
            if (_reportedEngine is { Length: > 0 } reported) return reported;

            if (settings?.Invoke() is not { } current) return null;

            // Answered the way the orchestrator answers it, minus the hardware probe: it resolves
            // the local model unless the mode forbids it. Saying "local" here while the recording
            // is being uploaded would be worse than saying nothing.
            if (current.AsrMode == TranscriptionMode.CloudOnly)
                return current.UsableSttEndpoints.FirstOrDefault()?.ResolvedName;

            return current.AsrModel.DisplayName;
        }
    }

    /// <summary>Whether anything is waiting behind the job in flight.</summary>
    [ObservableProperty] private bool _hasQueue;

    [ObservableProperty] private TranscriptFilter _transcriptFilter = TranscriptFilter.Unfinished;
    [ObservableProperty] private AnalyseFilter _analyseFilter = AnalyseFilter.Unanalysed;

    // The four counters, aligned with the two tabs. They used to be transcription-flavoured
    // regardless of what was on screen: "başarısız" summed both kinds of failure and said which
    // tab to look in about neither, and "metni yok" counted recordings that were merely waiting
    // their turn. A counter that cannot tell you where to click is a decoration.
    [ObservableProperty] private int _waitingCount;
    [ObservableProperty] private int _transcriptFailedCount;
    [ObservableProperty] private int _unanalysedCount;
    [ObservableProperty] private int _readyCount;

    [ObservableProperty] private string? _notice;

    /// <summary>Which recording is being worked on right now, and how far along it is.</summary>
    [ObservableProperty] private long? _activeCallId;

    [ObservableProperty] private string? _activeStage;
    [ObservableProperty] private double _activePercent;
    [ObservableProperty] private bool _hasActivePercent;

    public bool IsWorkingOnSomething => ActiveCallId is not null;

    /// <summary>
    /// Records where the current job has got to.
    ///
    /// Held here rather than pushed into the row objects because a row is a record and replacing
    /// it on every progress tick would rebuild the list several times a second — which flickers,
    /// loses the selection, and costs far more than it shows.
    /// </summary>
    public void ReportProgress(long callId, string stage, double? percent, string? engine = null)
    {
        ActiveCallId = callId;
        ActiveStage = stage;

        // What the job says it is using, which is the only answer that cannot be wrong. Held
        // rather than overwritten with null, because not every progress tick carries it.
        if (engine is { Length: > 0 }) _reportedEngine = engine;
        HasActivePercent = percent is not null;
        ActivePercent = percent ?? 0;

        OnPropertyChanged(nameof(IsWorkingOnSomething));
        OnPropertyChanged(nameof(ActiveLine));
    }

    /// <summary>The engine the running job reported, or null before one has said.</summary>
    private string? _reportedEngine;

    /// <summary>Clears the live line once nothing is being worked on.</summary>
    public void ClearProgress()
    {
        ActiveCallId = null;
        ActiveStage = null;
        _reportedEngine = null;
        HasActivePercent = false;

        OnPropertyChanged(nameof(IsWorkingOnSomething));
        OnPropertyChanged(nameof(ActiveLine));
    }

    /// <summary>One line saying what is happening, for the strip above the list.</summary>
    public string ActiveLine
    {
        get
        {
            if (ActiveCallId is not { } id) return "";

            var who = TranscriptRows.Concat(AnalysisRows).FirstOrDefault(r => r.Id == id)?.ContactName;
            var stage = ActiveStage ?? "İşleniyor";

            var line = who is null ? stage : $"{who} · {stage}";

            if (HasActivePercent) line = $"{line} — %{ActivePercent * 100:0}";

            // Which engine, said here rather than only in a toast that has already gone.
            return ActiveEngine is { Length: > 0 } engine ? $"{line} · {engine}" : line;
        }
    }

    /// <summary>Raised when something needs the shell — reprocessing goes through the orchestrator.</summary>
    public event EventHandler<ReprocessRequest>? ReprocessRequested;

    public bool IsTranscriptEmpty => TranscriptRows.Count == 0;
    public bool IsAnalysisEmpty => AnalysisRows.Count == 0;

    public string TranscriptEmptyMessage => TranscriptFilter switch
    {
        TranscriptFilter.Done => "Yazıya dökülmüş görüşme yok.",
        TranscriptFilter.Unfinished => "Bekleyen iş yok — her kayıt yazıya dökülmüş.",
        TranscriptFilter.Failed => "İşlenemeyen görüşme yok.",
        _ => "Henüz kayıt yok.",
    };

    public string AnalysisEmptyMessage => AnalyseFilter switch
    {
        AnalyseFilter.Done => "Henüz çözümlenmiş görüşme yok.",
        AnalyseFilter.Unanalysed => "Çözümlenmeyi bekleyen görüşme yok.",
        _ => "Metni olan görüşme yok — çözümleme metinden çalışır.",
    };
    // Both filters re-read. Only the analysis one did, so "Bitenler" and "Hepsi" on the
    // transcription tab moved the highlight and left the list exactly as it was - and the first
    // screen's "Goster", which sets this property on its way to the page, changed nothing at all.
    partial void OnTranscriptFilterChanged(TranscriptFilter value) => Refresh();
    partial void OnAnalyseFilterChanged(AnalyseFilter value) => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        // Counted from every call rather than from the filtered view, so the tabs say how much
        // there is even while looking at one slice of it.
        var all = repository.ListCalls(limit: 2000);

        var rows = all
            .Select(call => new ProcessingRow(
                call,
                call.ContactId is { } id ? repository.GetContact(id)?.Name ?? "İsimsiz" : "İsimsiz",
                repository.CountSegments(call.Id)))
            .ToList();

        WaitingCount = rows.Count(r => r.IsWaiting || r.IsWorking);

        // "Hepsini durdur" only means anything when there is something behind the running job.
        HasQueue = rows.Count(r => r.IsWaiting) > 0;
        // Every failure, the same population the first screen counts - including the ones no
        // retry can help, because they are now reachable, and deletable, through the failures
        // filter. Counting only the retryable ones made this read 0 directly under a first screen
        // saying 4, which teaches the user that one of the two screens is lying.
        TranscriptFailedCount = rows.Count(r => r.IsFailed);
        UnanalysedCount = rows.Count(r =>
            r.HasTranscript && (r.Call.State == ProcessingState.Transcribed || r.AnalysisFailed));
        ReadyCount = rows.Count(r => r.Call.State == ProcessingState.Analysed);

        // Two tabs, two questions. A call with no text is transcription's business even when its
        // state says Failed; a call with text and no ledger is analysis's, same state field.
        var transcript = TranscriptFilter switch
        {
            ViewModels.TranscriptFilter.Done => rows.Where(r => r.HasTranscript),
            ViewModels.TranscriptFilter.Unfinished => rows.Where(r => r.NeedsTranscription),
            ViewModels.TranscriptFilter.Failed => rows.Where(r => r.IsFailed),
            _ => rows,
        };

        var withText = rows.Where(r => r.HasTranscript).ToList();

        var analysis = AnalyseFilter switch
        {
            ViewModels.AnalyseFilter.Done => withText.Where(r => r.Call.State == ProcessingState.Analysed),
            ViewModels.AnalyseFilter.Unanalysed => withText.Where(r =>
                r.Call.State == ProcessingState.Transcribed || r.AnalysisFailed),
            _ => withText,
        };

        TranscriptRows.Clear();
        foreach (var row in transcript.OrderByDescending(r => r.Call.StartedAt)) TranscriptRows.Add(row);

        AnalysisRows.Clear();
        foreach (var row in analysis.OrderByDescending(r => r.Call.StartedAt)) AnalysisRows.Add(row);

        OnPropertyChanged(nameof(IsTranscriptEmpty));
        OnPropertyChanged(nameof(IsAnalysisEmpty));
        OnPropertyChanged(nameof(TranscriptEmptyMessage));
        OnPropertyChanged(nameof(AnalysisEmptyMessage));
    }

    [RelayCommand]
    private void ShowTranscriptDone() => TranscriptFilter = TranscriptFilter.Done;

    [RelayCommand]
    private void ShowTranscriptWaiting() => TranscriptFilter = TranscriptFilter.Unfinished;

    [RelayCommand]
    private void ShowTranscriptFailed() => TranscriptFilter = TranscriptFilter.Failed;

    [RelayCommand]
    private void ShowTranscriptAll() => TranscriptFilter = TranscriptFilter.All;

    [RelayCommand]
    private void ShowAnalysisUnanalysed() => AnalyseFilter = AnalyseFilter.Unanalysed;

    [RelayCommand]
    private void ShowAnalysisDone() => AnalyseFilter = AnalyseFilter.Done;

    [RelayCommand]
    private void ShowAnalysisAll() => AnalyseFilter = AnalyseFilter.All;

    /// <summary>
    /// Everything the visible tab lists, for its batch button.
    ///
    /// The common case by a distance: a run of failures usually has one cause — a missing package,
    /// a service that was down, a key that had expired — so once it is fixed the useful action is
    /// "all of them", not eight separate clicks.
    /// </summary>
    public IReadOnlyList<ProcessingRow> ListedTranscriptRows => [.. TranscriptRows];

    public IReadOnlyList<ProcessingRow> ListedAnalysisRows => [.. AnalysisRows];

    /// <summary>
    /// Puts recordings back in the queue, optionally by a different route.
    ///
    /// The route matters because a recording is here precisely because its usual one failed.
    /// Retrying the same way was the button's only behaviour, which made it least useful exactly
    /// when it was needed.
    /// </summary>
    public void Requeue(IReadOnlyList<ProcessingRow> rows, ReprocessRequest? how = null)
    {
        var analyseOnly = how?.AnalyseOnly == true;

        // What each route needs. Re-analysing works from the text, so a recording whose audio has
        // gone can still be re-analysed — refusing it because of a missing WAV would block the one
        // route that was still open to it.
        var possible = rows
            .Where(r => analyseOnly ? r.HasTranscript : r.HasAudio)
            .ToList();

        var impossible = rows.Count - possible.Count;

        if (possible.Count == 0)
        {
            Notice = analyseOnly
                ? "Bu kayıtların metni yok; önce yazıya dökülmeleri gerekiyor."
                : impossible > 0
                    ? "Bu kayıtların ses dosyası yok; yeniden işlenemezler."
                    : "Yeniden işlenecek kayıt yok.";
            return;
        }

        foreach (var row in possible) repository.SetCallState(row.Id, ProcessingState.Queued);

        ReprocessRequested?.Invoke(this, how is null
            ? new ReprocessRequest([.. possible.Select(r => r.Id)], null, null, false)
            : how with { Ids = [.. possible.Select(r => r.Id)] });

        var what = analyseOnly ? "yeniden çözümlenecek" : "yeniden kuyruğa alındı";

        Notice = impossible == 0
            ? $"{possible.Count} görüşme {what}."
            : $"{possible.Count} görüşme {what}. {impossible} tanesi atlandı.";

        Refresh();
    }
}
