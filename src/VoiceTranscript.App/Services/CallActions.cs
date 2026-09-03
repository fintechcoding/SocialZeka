using System.IO;
using System.Windows;
using VoiceTranscript.App.Views;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.App.Services;

/// <summary>
/// The things that can be done to one call, from wherever the call is shown.
///
/// The first screen's row menu offered three actions and the contacts page's offered six, with
/// delete on neither — a failed recording could only be removed from the labelling dialog, once.
/// Everything a row can do lives here, so every list that shows a call offers the same verbs,
/// worded the same way, with the same confirmations.
/// </summary>
public static class CallActions
{
    /// <summary>
    /// Raised after a call was deleted, moved or re-queued. Every list that shows calls re-reads
    /// on it, so no page has to know which other pages exist.
    /// </summary>
    public static event EventHandler? Changed;

    private static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    /// <summary>
    /// Announces that many calls changed at once — an import, a bulk delete.
    ///
    /// The event already exists for exactly this; what was missing was a way for something
    /// outside this class to raise it. Without it an import finished, wrote several hundred rows,
    /// and every screen went on showing what it had read a minute earlier.
    /// </summary>
    public static void NotifyChanged() => RaiseChanged();

    /// <summary>Whether the recording's audio is still on disk.</summary>
    public static bool HasAudio(Call call) =>
        (call.MicPath is { } mic && File.Exists(mic)) || (call.FarPath is { } far && File.Exists(far));

    private static string Describe(Call call, string contactName) =>
        $"{contactName} · {call.StartedAt.ToLocalTime():d MMMM HH:mm} · {(int)call.Duration.TotalMinutes:00}:{call.Duration.Seconds:00}";

    /// <summary>
    /// Deletes the recording, its transcript and its ledger entries, after asking once.
    /// Returns true when the row is gone.
    /// </summary>
    public static async Task<bool> DeleteAsync(Window? owner, Call call, string contactName)
    {
        // Not while the worker is reading it: the files would go from under a transcription and
        // the row would come back as a failure a minute later. Stop it first, then delete.
        if (await IsInFlightAsync(owner, call, "silinemez")) return false;

        var confirmed = await Dialogs.ConfirmAsync(
            owner,
            "Görüşmeyi sil",
            $"{Describe(call, contactName)}\n\n" +
            "Ses kaydı, döküm, özet ve bu görüşmeden çıkan defter kayıtları kalıcı olarak silinecek. " +
            "Kişi ve diğer görüşmeleri kalır. Geri alınamaz.",
            okText: "Sil");

        if (!confirmed) return false;

        var result = App.Repository.DeleteCall(call.Id);
        RaiseChanged();

        if (result.FilesLeftBehind.Count > 0)
        {
            await Dialogs.InfoAsync(
                owner,
                "Silme tamamlanmadı",
                "Kayıt silindi ama bazı ses dosyaları kaldırılamadı — büyük ihtimalle hâlâ çalınıyor:\n\n" +
                string.Join("\n", result.FilesLeftBehind.Select(Path.GetFileName)));
        }

        return true;
    }

    /// <summary>True, after saying so, when the call is being transcribed or analysed right now.</summary>
    private static async Task<bool> IsInFlightAsync(Window? owner, Call call, string verb)
    {
        var current = App.Repository.GetCall(call.Id)?.State;
        if (current is not (ProcessingState.Transcribing or ProcessingState.Analysing)) return false;

        await Dialogs.InfoAsync(owner, "Şu an işleniyor",
            $"Bu görüşme şu an işleniyor ve {verb}. Durum › İşlemler'den durdurabilir, sonra yeniden deneyebilirsin.");
        return true;
    }

    /// <summary>Files the call under another person, optionally unbinding the window title. Returns true when moved.</summary>
    public static bool Move(Window? owner, Call call, string currentContactName)
    {
        var dialog = new MoveCallWindow(
            App.Repository,
            currentContactName,
            call.ObservedTitle,
            call.App,
            call.StartedAt,
            call.Duration,
            App.Repository.CountLedgerEntriesForCall(call.Id))
        {
            Owner = owner,
        };

        if (dialog.ShowDialog() != true || dialog.ChosenContactId is not { } target) return false;

        App.Repository.AssignContact(call.Id, target);

        if (dialog.ForgetTitle && !string.IsNullOrWhiteSpace(call.ObservedTitle))
            App.Repository.ForgetTitleBinding(call.ObservedTitle, call.App);

        RaiseChanged();
        return true;
    }

    /// <summary>Queues the call for another transcription or analysis, with the engine chosen in the dialog.</summary>
    public static bool Reprocess(Window? owner, Call call, string contactName, ReprocessKind kind)
    {
        if (App.Orchestrator is null) return false;

        // Already queued or being worked on: a second request would only make the queue lie
        // about its length. Said inline; nothing to confirm.
        var current = App.Repository.GetCall(call.Id)?.State;

        if (current is ProcessingState.Queued or ProcessingState.Transcribing or ProcessingState.Analysing)
        {
            _ = Dialogs.InfoAsync(owner, "Zaten sırada",
                current == ProcessingState.Queued
                    ? "Bu görüşme zaten işlenmek üzere sırada."
                    : "Bu görüşme şu an işleniyor. Bitince yeniden işleyebilirsin; durdurmak için Durum › İşlemler.");
            return false;
        }

        // The reasons a re-run cannot work, said here instead of as a new failed row a minute
        // later: no audio left (swept, or never written), no text to analyse, a group call the
        // settings say not to transcribe.
        if (kind == ReprocessKind.Transcribe && !HasAudio(call))
        {
            _ = Dialogs.InfoAsync(owner, "Ses yok",
                "Bu kaydın ses dosyası yok — saklama süresi dolduğu için silinmiş ya da hiç yazılmamış olabilir. Yeniden yazıya dökülemez; metin duruyorsa yeniden çözümlenebilir.");
            return false;
        }

        if (kind == ReprocessKind.Analyse && App.Repository.CountSegments(call.Id) == 0)
        {
            _ = Dialogs.InfoAsync(owner, "Metin yok",
                "Bu kaydın metni yok; önce yazıya dökülmesi gerekiyor.");
            return false;
        }

        if (kind == ReprocessKind.Transcribe && call.Kind == CallKind.Group && !App.Settings.TranscribeGroupCalls)
        {
            _ = Dialogs.InfoAsync(owner, "Grup araması",
                "Grup aramaları ayarlarda açılmadıkça yazıya dökülmez: karşı taraf tek akışta karışık gelir, kim ne dedi ayrılamaz.");
            return false;
        }

        var dialog = new ReprocessWindow(App.Repository, App.Settings, contactName, count: 1, kind)
        {
            Owner = owner,
        };

        if (dialog.ShowDialog() != true) return false;

        var choice = dialog.Choice;

        App.Repository.SetCallState(call.Id, ProcessingState.Queued);
        App.Orchestrator.EnqueueWith(
            call.Id, choice.AsrModelId, choice.AnalyseOnly, choice.LlmModel, choice.LlmRouteKind, choice.LlmRouteUrl);

        RaiseChanged();
        return true;
    }

    /// <summary>Shows the recording in Explorer, or says why it cannot.</summary>
    public static async Task ShowInFolderAsync(Window? owner, Call call)
    {
        var path = call.MicPath ?? call.FarPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            await Dialogs.InfoAsync(owner, "Ses dosyası",
                "Bu görüşmenin ses dosyası bulunamadı. Kayıt tamamlanmamış ya da silinmiş olabilir.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(owner, "Ses dosyası", $"Klasör açılamadı: {ex.Message}");
        }
    }
}
