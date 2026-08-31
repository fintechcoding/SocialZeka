using System.IO;
using System.Windows;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Export;

namespace VoiceTranscript.App.Views;

public partial class ContactsPage
{
    public ContactsPage() => InitializeComponent();

    private ContactsViewModel? ViewModel => DataContext as ContactsViewModel;

    /// <summary>
    /// Clicking the waveform plays from that point.
    ///
    /// Position is taken as a fraction of the strip rather than in pixels, because the drawing is
    /// scaled to whatever width the window happens to be. Working in pixels would put the
    /// playhead somewhere else on a resized window, and a player that lands near the moment
    /// rather than on it is one nobody uses to check a quote.
    /// </summary>
    private void Waveform_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ViewModel is not { } model || sender is not System.Windows.FrameworkElement strip) return;
        if (strip.ActualWidth <= 0) return;

        model.Playback.SeekTo(e.GetPosition(strip).X / strip.ActualWidth);
    }

    /// <summary>
    /// Moves the selected call to a different person.
    ///
    /// Opened from the call toolbar rather than hidden in a menu, because a call filed under the
    /// wrong person is something the user is looking straight at when they notice it. The window
    /// can create the contact as well as pick one: the person a call belongs to frequently does
    /// not exist yet, and sending somebody away to make one first is how a wrong filing gets left
    /// in place and forgotten.
    /// </summary>
    private void MoveCall_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SelectedCall: { } row } model) return;

        var call = row.Call;

        // Counted so the window can say it. A call is not one row — the promises and figures taken
        // out of it are filed against the same person and travel with it — and somebody moving a
        // call deserves to know their ledger is about to change too.
        var ledgerEntries = App.Repository.CountLedgerEntriesForCall(call.Id);

        var dialog = new MoveCallWindow(
            App.Repository,
            model.SelectedContact?.Contact.Name ?? "bilinmeyen kişi",
            call.ObservedTitle,
            call.App,
            call.StartedAt,
            call.Duration,
            ledgerEntries)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true || dialog.ChosenContactId is not { } target) return;

        model.MoveSelectedCall(target, dialog.ForgetTitle);
    }

    private async void Reprocess_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model || App.Orchestrator is null) return;

        await model.ReprocessSelectedCallAsync(App.Orchestrator);
    }

    /// <summary>
    /// Writes this contact into the Obsidian vault now, rather than waiting for the next call.
    ///
    /// Useful after editing a name or dismissing a flag: the contact page is regenerated from
    /// the database each time, so exporting on demand is how the file catches up.
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedContact is not { } contact) return;

        var vault = App.Settings.ObsidianVaultPath;

        if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault))
        {
            MessageBox.Show(
                "Obsidian kasası ayarlanmamış. Ayarlar → Dışa aktarma bölümünden bir klasör seç.",
                "Dışa aktarma", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var path = new ObsidianExporter(App.Repository, new ObsidianOptions { VaultPath = vault })
                .ExportContact(contact.Contact.Id);

            MessageBox.Show($"Yazıldı:\n{path}", "Dışa aktarma",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Dışa aktarılamadı: {ex.Message}", "Dışa aktarma",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Removes a person from the archive entirely, after asking.
    ///
    /// Asked plainly and by name, with the consequence spelled out, because this cannot be
    /// undone and the thing being destroyed is a record of somebody's conversations. It is also
    /// the promise the product rests on: a "delete" that leaves audio on disk or words in a
    /// search index would not be deletion at all.
    /// </summary>
    private void DeleteContact_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SelectedContact: { } contact } model) return;

        var answer = MessageBox.Show(
            $"{contact.Name} ile ilgili her şey kalıcı olarak silinecek:\n\n" +
            "• ses kayıtları\n• görüşme metinleri\n• arama dizini\n• çıkarılmış olgular ve defter\n\n" +
            "Bu işlem geri alınamaz. Devam edilsin mi?",
            "Kişiyi sil",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var result = model.DeleteSelectedContact();
            if (result is null) return;

            // Reported exactly, including what could not be removed. Telling somebody a
            // recording is gone when it is still on disk is worse than not offering to delete it.
            var message = result.IsComplete
                ? result.FilesRemoved == 0
                    ? "Silindi."
                    : $"Silindi. {result.FilesRemoved} ses dosyası kaldırıldı."
                : $"Kayıtlar silindi ama {result.FilesLeftBehind.Count} ses dosyası kaldırılamadı "
                  + "(dosya kullanımda olabilir):" + Environment.NewLine + Environment.NewLine
                  + string.Join(Environment.NewLine, result.FilesLeftBehind.Take(5));

            MessageBox.Show(message, "Kişiyi sil", MessageBoxButton.OK,
                result.IsComplete ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Silinemedi: {ex.Message}", "Kişiyi sil",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Saves the few seconds a ledger entry rests on as its own file.
    ///
    /// A save dialog rather than a folder chosen once in settings. These files are made to be
    /// sent to somebody, so the user is choosing where to put something they are about to share —
    /// and being asked is what makes that a decision rather than a side effect.
    /// </summary>
    private void ExportClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Control)?.Tag is not ViewModels.FlagView view) return;
        if (DataContext is not ViewModels.ContactsViewModel model) return;

        var exporter = new Services.ClipExporter(App.Repository);
        var flag = view.Flag;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Ses kesiti nereye kaydedilsin?",
            FileName = exporter.NameFor(flag.CallId, flag.QuoteStartMs),
            Filter = "Ses dosyası (*.wav)|*.wav",
            DefaultExt = ".wav",
        };

        if (dialog.ShowDialog() != true) return;

        var result = exporter.ExportFlag(flag.CallId, flag.QuoteStartMs, dialog.FileName);

        model.PlaybackMessage = result.Message;
        Services.AppLog.Write("kesit", result.Ok ? "ses kesiti yazıldı" : $"kesit alınamadı: {result.Message}");
    }
}
