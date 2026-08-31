using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.App.ViewModels;

/// <summary>Which recordings the screen is showing.</summary>
public enum ProcessingFilter
{
    /// <summary>Anything that is not finished: waiting, running, or failed.</summary>
    Unfinished,

    /// <summary>Only the ones that failed.</summary>
    Failed,

    /// <summary>Only the ones with no transcript at all.</summary>
    WithoutTranscript,

    /// <summary>Finished, one way or another: analysed, transcribed, or deliberately skipped.</summary>
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
        ProcessingState.Skipped => "Atlandı",
        ProcessingState.Failed when SegmentCount > 0 => $"{SegmentCount} satır (sonra hata verdi)",
        ProcessingState.Failed => "Başarısız",
        _ => SegmentCount > 0 ? $"{SegmentCount} satır" : "Metin yok",
    };

    public string AnalysisState => Call.State switch
    {
        ProcessingState.Analysed => "Hazır",
        ProcessingState.Analysing => "Çözümleniyor",
        ProcessingState.Transcribed => "Çözümlenmedi",
        ProcessingState.Failed => "Yapılamadı",
        ProcessingState.Skipped => "Atlandı",
        _ => "—",
    };

    public bool IsFailed => Call.State == ProcessingState.Failed;

    public bool IsWorking => Call.State
        is ProcessingState.Transcribing or ProcessingState.Analysing;

    public bool IsWaiting => Call.State is ProcessingState.Recorded or ProcessingState.Queued;

    /// <summary>
    /// Why it failed, in words rather than in a stack trace.
    ///
    /// The raw reason is kept in the database and shown as the tooltip, because the translated
    /// version has to be short enough to fit in a list and the original is what actually says
    /// which library is missing.
    /// </summary>
    public string? Failure => Call.State == ProcessingState.Failed
        ? Core.Asr.FailureText.Summarise(Call.FailureReason)
        : null;

    public string? RawFailure => Call.FailureReason;

    /// <summary>True when the audio is still on disk, so retrying is possible at all.</summary>
    public bool HasAudio => !string.IsNullOrWhiteSpace(Call.MicPath) || !string.IsNullOrWhiteSpace(Call.FarPath);
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
    public ObservableCollection<ProcessingRow> Rows { get; } = [];

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
            if (settings?.Invoke() is not { } current) return null;

            // Answered the way the orchestrator answers it, minus the hardware probe: it resolves
            // the local model unless the mode forbids it. Saying "local" here while the recording
            // is being uploaded would be worse than saying nothing.
            if (current.AsrMode == TranscriptionMode.CloudOnly)
                return current.UsableSttEndpoints.FirstOrDefault()?.ResolvedName;

            return current.AsrModel.DisplayName;
        }
    }

    [ObservableProperty] private ProcessingFilter _filter = ProcessingFilter.Unfinished;

    [ObservableProperty] private int _waitingCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _withoutTranscriptCount;
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
    public void ReportProgress(long callId, string stage, double? percent)
    {
        ActiveCallId = callId;
        ActiveStage = stage;
        HasActivePercent = percent is not null;
        ActivePercent = percent ?? 0;

        OnPropertyChanged(nameof(IsWorkingOnSomething));
        OnPropertyChanged(nameof(ActiveLine));
    }

    /// <summary>Clears the live line once nothing is being worked on.</summary>
    public void ClearProgress()
    {
        ActiveCallId = null;
        ActiveStage = null;
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

            var who = Rows.FirstOrDefault(r => r.Id == id)?.ContactName;
            var stage = ActiveStage ?? "İşleniyor";

            var line = who is null ? stage : $"{who} · {stage}";

            if (HasActivePercent) line = $"{line} — %{ActivePercent * 100:0}";

            // Which engine, said here rather than only in a toast that has already gone.
            return ActiveEngine is { Length: > 0 } engine ? $"{line} · {engine}" : line;
        }
    }

    /// <summary>Raised when something needs the shell — reprocessing goes through the orchestrator.</summary>
    public event EventHandler<ReprocessRequest>? ReprocessRequested;

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyMessage => Filter switch
    {
        ProcessingFilter.Failed => "Başarısız görüşme yok.",
        ProcessingFilter.WithoutTranscript => "Metni olmayan görüşme yok.",
        ProcessingFilter.Done => "Henüz biten iş yok.",
        ProcessingFilter.Unfinished => "Bekleyen iş yok — her şey işlenmiş.",
        _ => "Henüz kayıt yok.",
    };

    partial void OnFilterChanged(ProcessingFilter value) => Refresh();

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
        FailedCount = rows.Count(r => r.IsFailed);
        WithoutTranscriptCount = rows.Count(r => !r.HasTranscript && r.Call.State != ProcessingState.Skipped);
        ReadyCount = rows.Count(r => r.Call.State == ProcessingState.Analysed);

        var shown = Filter switch
        {
            ProcessingFilter.Failed => rows.Where(r => r.IsFailed),
            ProcessingFilter.Done => rows.Where(r =>
                r.Call.State is ProcessingState.Analysed or ProcessingState.Transcribed
                    or ProcessingState.Skipped),
            ProcessingFilter.WithoutTranscript =>
                rows.Where(r => !r.HasTranscript && r.Call.State != ProcessingState.Skipped),
            ProcessingFilter.Unfinished =>
                rows.Where(r => r.IsFailed || r.IsWaiting || r.IsWorking || !r.HasTranscript),
            _ => rows,
        };

        Rows.Clear();
        foreach (var row in shown.OrderByDescending(r => r.Call.StartedAt)) Rows.Add(row);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand]
    private void ShowFailed() => Filter = ProcessingFilter.Failed;

    [RelayCommand]
    private void ShowWaiting() => Filter = ProcessingFilter.Unfinished;

    [RelayCommand]
    private void ShowWithoutTranscript() => Filter = ProcessingFilter.WithoutTranscript;

    [RelayCommand]
    private void ShowAll() => Filter = ProcessingFilter.All;

    [RelayCommand]
    private void ShowDone() => Filter = ProcessingFilter.Done;

    /// <summary>
    /// Everything currently listed, for the screen to offer as one batch.
    ///
    /// The common case by a distance: a run of failures usually has one cause — a missing package,
    /// a service that was down, a key that had expired — so once it is fixed the useful action is
    /// "all of them", not eight separate clicks.
    /// </summary>
    public IReadOnlyList<ProcessingRow> AllRows => [.. Rows];

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
