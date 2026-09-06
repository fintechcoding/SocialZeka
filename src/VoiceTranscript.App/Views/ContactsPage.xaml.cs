using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Export;

namespace VoiceTranscript.App.Views;

public partial class ContactsPage
{
    public ContactsPage()
    {
        InitializeComponent();

        // The "Kişi kartı" tab hosts the same control the contact window does, and it cannot open
        // a window or move the shell for itself. Its view model asks; this page answers.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ContactsViewModel previous)
            {
                previous.CardOpenRequested -= OnCardOpen;
                previous.CardPromisesRequested -= OnCardPromises;
            }

            if (e.NewValue is ContactsViewModel next)
            {
                next.CardOpenRequested += OnCardOpen;
                next.CardPromisesRequested += OnCardPromises;
            }
        };
    }

    /// <summary>
    /// A ▸ on the card: the conversation it came from, at the moment it was said.
    ///
    /// Opened as its own window rather than seeked in this pane's player. The card lists findings
    /// from every conversation with this person, so the row being clicked usually belongs to a
    /// different call from the one selected — and audio from the wrong conversation offered as
    /// proof is worse than no audio at all.
    /// </summary>
    private void OnCardOpen(object? sender, (long CallId, int StartMs, bool IsMe) target)
        => CallWindow.Show(Window.GetWindow(this), target.CallId, target.StartMs, target.IsMe);

    /// <summary>"Sözler sayfasında aç": this page is inside the shell, so it just changes page.</summary>
    private void OnCardPromises(object? sender, EventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell)
            shell.Page = ShellPage.Promises;
    }

    private ContactsViewModel? ViewModel => DataContext as ContactsViewModel;

    /// <summary>
    /// Selects the row before its context menu opens.
    ///
    /// WPF shows a ListBoxItem's context menu without selecting it first, so a right-click on one
    /// row while another is selected opens a menu that acts on the other one. For "move this
    /// conversation to somebody else" that is not a cosmetic problem: it silently moves the wrong
    /// call, and the user has no reason to suspect it.
    /// </summary>
    private void CallRow_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem item) item.IsSelected = true;
    }

    /// <summary>Opens the selected conversation in its own window.</summary>
    private void CallRow_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenCall_Click(sender, e);

    /// <summary>The user's verdict on one suggestion, applied where they read it.</summary>
    private void CallActionDone_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.ActionRow row) return;
        ViewModel?.SetCallActionStatus(row, Core.Domain.ActionStatus.Done);
    }

    private void CallActionHide_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.ActionRow row) return;
        ViewModel?.SetCallActionStatus(row, Core.Domain.ActionStatus.Hidden);
    }

    private void OpenCall_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedCall is not { } row) return;

        CallWindow.Show(Window.GetWindow(this), row.Call.Id);
    }

    /// <summary>Same for the contact list: the menu must act on the row that was clicked.</summary>
    private void ContactRow_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem item) item.IsSelected = true;
    }

    /// <summary>
    /// Opens a person in their own window.
    ///
    /// The page is right for browsing across everybody; this is for working on one person — their
    /// whole history, a search through everything they have said, and notes about them. It is a
    /// window rather than a fourth tab because two people can then be open at once, which the
    /// page cannot do: it holds one selected contact and one player.
    /// </summary>
    private void ContactRow_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ViewModel?.SelectedContact is not { } contact) return;

        ContactWindow.Show(
            Window.GetWindow(this),
            new ViewModels.ContactWindowViewModel(
                App.Repository, contact.Contact.Id, App.Paths.Photos, App.ModelAccess));
    }

    /// <summary>
    /// F2 renames the selected contact.
    ///
    /// Because that is what F2 does everywhere else in Windows, and somebody who has just noticed a
    /// misspelled name will press it before they go looking for a menu. Renaming is not a rare
    /// operation here — contacts are frequently created from a window title that was never really
    /// a name — so the fastest route to it is worth having.
    /// </summary>
    private void ContactList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.F2) return;
        if (ViewModel?.SelectedContact is null) return;

        RenameContact_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>
    /// Opens the folder holding this call's audio, with the file selected.
    ///
    /// Useful precisely when something has gone wrong: a call that failed to transcribe still has
    /// its recording, and being able to reach it is the difference between "the audio is safe" and
    /// having to take the application's word for it.
    /// </summary>
    private async void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedCall is not { } row) return;

        await Services.CallActions.ShowInFolderAsync(Window.GetWindow(this), row.Call);
    }

    /// <summary>
    /// Deletes the clicked call. Reached from the row's own button as well as the menu; a button
    /// click does not select the row, so the row under the button is selected first.
    /// </summary>
    private async void DeleteCall_Click(object sender, RoutedEventArgs e)
    {
        SelectRowUnder(sender);

        if (ViewModel is not { SelectedCall: { } row } model) return;

        var name = model.SelectedContact?.Contact.Name ?? "Bilinmeyen kişi";

        if (await Services.CallActions.DeleteAsync(Window.GetWindow(this), row.Call, name))
        {
            model.Refresh();
            model.PlaybackMessage = "Görüşme silindi.";

            if (Window.GetWindow(this)?.DataContext is ViewModels.ShellViewModel shell) shell.Overview.Refresh();
        }
    }

    private static void SelectRowUnder(object sender)
    {
        var node = sender as DependencyObject;

        while (node is not null and not ListBoxItem)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);

        if (node is ListBoxItem item) item.IsSelected = true;
    }

    /// <summary>
    /// Corrects a contact's name.
    ///
    /// Needed as its own action rather than as "delete and retype": the archive keys contacts on
    /// the name, so retyping makes a second person and leaves the history split between them —
    /// which is the same failure this whole area of the product exists to repair.
    /// </summary>
    private void RenameContact_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SelectedContact: { } contact } model) return;

        var dialog = new RenameContactWindow(contact.Contact.Name)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true) return;

        model.RenameSelectedContact(dialog.NewName);
    }

    /// <summary>
    /// Folds another contact into this one.
    ///
    /// One person becomes two rows for ordinary reasons — a window title that was not a name, a
    /// different spelling, or the same person reached on two applications, since contacts are keyed
    /// on (name, app). Leaving them split is not cosmetic: every comparison this product makes is
    /// per contact, so a divided history makes both halves look complete while the comparison
    /// across them never happens.
    /// </summary>
    private void MergeContact_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SelectedContact: { } target } model) return;

        var dialog = new MergeContactWindow(App.Repository, target.Contact)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true || dialog.ChosenContactId is not { } source) return;

        model.MergeInto(source);
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

        if (Services.CallActions.Move(Window.GetWindow(this), row.Call, model.SelectedContact?.Contact.Name ?? "bilinmeyen kişi"))
        {
            model.PlaybackMessage = "Görüşme taşındı.";
            model.Refresh();

            if (Window.GetWindow(this)?.DataContext is ViewModels.ShellViewModel shell) shell.Overview.Refresh();
        }
    }

    /// <summary>
    /// Puts this conversation on the important-conversations panel of the first screen.
    ///
    /// The panel replaced the four-lane board page — the user's word for what they wanted was a
    /// pile, not a workflow — so the menu no longer asks which lane; there is one pile now.
    /// </summary>
    private void AddToBoard_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedCall is not { } row) return;

        App.Repository.PutOnBoard(row.Call.Id, Core.Domain.BoardLane.ToLookAt);

        if (Window.GetWindow(this)?.DataContext is ViewModels.ShellViewModel shell)
            shell.Overview.Refresh();

        ViewModel.PlaybackMessage = "Önemli görüşmelere eklendi.";
    }

    /// <summary>
    /// Redoes this conversation, by a route the user picks.
    ///
    /// Asking is the point. A conversation is being redone because something about it went wrong,
    /// and repeating the configured route is the one approach already known to have failed here.
    /// The dialog also offers re-analysing without re-transcribing, which is the common case once
    /// a model is connected after the fact: the text is already there, and paying for the audio a
    /// second time is the difference between a minute and an afternoon.
    /// </summary>
    private void Retranscribe_Click(object sender, RoutedEventArgs e)
        => Reprocess(ReprocessKind.Transcribe);

    private void Reanalyse_Click(object sender, RoutedEventArgs e)
        => Reprocess(ReprocessKind.Analyse);

    private void Reprocess(ReprocessKind kind)
    {
        if (ViewModel is not { SelectedCall: { } row } model) return;

        if (Services.CallActions.Reprocess(Window.GetWindow(this), row.Call, model.SelectedContact?.Name ?? "Görüşme", kind))
        {
            model.PlaybackMessage = kind == ReprocessKind.Analyse
                ? "Görüşme yeniden çözümlenmek üzere sıraya alındı."
                : "Görüşme yeniden işlenmek üzere sıraya alındı.";
        }
    }

    /// <summary>
    /// Writes this contact into the Obsidian vault now, rather than waiting for the next call.
    ///
    /// Useful after editing a name or dismissing a flag: the contact page is regenerated from
    /// the database each time, so exporting on demand is how the file catches up.
    /// </summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedContact is not { } contact) return;

        var vault = App.Settings.ObsidianVaultPath;

        if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault))
        {
            await Services.Dialogs.InfoAsync(Window.GetWindow(this), "Dışa aktarma",
                "Obsidian kasası ayarlanmamış. Ayarlar → Dışa aktarma bölümünden bir klasör seç.");
            return;
        }

        try
        {
            var path = new ObsidianExporter(App.Repository, new ObsidianOptions { VaultPath = vault })
                .ExportContact(contact.Contact.Id);

            await Services.Dialogs.InfoAsync(Window.GetWindow(this), "Dışa aktarma", $"Yazıldı:\n{path}");
        }
        catch (Exception ex)
        {
            await Services.Dialogs.InfoAsync(Window.GetWindow(this), "Dışa aktarma",
                $"Dışa aktarılamadı: {ex.Message}");
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
    private async void DeleteContact_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { SelectedContact: { } contact } model) return;

        var confirmed = await Services.Dialogs.ConfirmAsync(
            Window.GetWindow(this), "Kişiyi sil",
            $"{contact.Name} ile ilgili her şey kalıcı olarak silinecek:\n\n" +
            "• ses kayıtları\n• görüşme metinleri\n• arama dizini\n• çıkarılmış olgular ve defter\n\n" +
            "Bu işlem geri alınamaz. Devam edilsin mi?",
            okText: "Sil", cancelText: "Vazgeç");

        if (!confirmed) return;

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

            await Services.Dialogs.InfoAsync(Window.GetWindow(this), "Kişiyi sil", message);
        }
        catch (Exception ex)
        {
            await Services.Dialogs.InfoAsync(Window.GetWindow(this), "Kişiyi sil",
                $"Silinemedi: {ex.Message}");
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

    /// <summary>A tag pill is a question: "which other conversations did I mark with this?"</summary>
    private void TagPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string tag) return;

        MainWindow.SearchTagFromAnywhere(tag);
        e.Handled = true;
    }
}
