using System.Windows;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class PromisesPage
{
    public PromisesPage()
    {
        InitializeComponent();

        // The page owns the dialogs, as the to-do page does: a view model does not open windows.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PromisesViewModel previous)
            {
                previous.RemindRequested -= OnRemind;
                previous.EditRequested -= OnEdit;
            }

            if (e.NewValue is PromisesViewModel next)
            {
                next.RemindRequested += OnRemind;
                next.EditRequested += OnEdit;
            }
        };
    }

    /// <summary>
    /// ✎ goes through the one edit dialog the call and contact windows use: wording and date, the
    /// user's own.
    ///
    /// The dialog hands back what it wrote together with the way back, and this page offers it in
    /// the same notice card as every other verb. It used to throw the undo away and merely
    /// re-read the list, which left the user's own correction as the single ruling on the page
    /// that could not be taken back — the one place where a mis-click cost work.
    /// </summary>
    private void OnEdit(object? sender, PromiseCard card)
    {
        if (App.Repository is not { } repository) return;

        if (EditPromiseWindow.Open(Window.GetWindow(this), repository, card.Commitment) is { } undo
            && DataContext is PromisesViewModel model)
        {
            model.Offer(undo);
        }
    }

    /// <summary>
    /// "Hatırlat" goes through the one reminder dialog every other surface uses, pre-filled with
    /// the promise as the reason; a reminder is set on the conversation the promise was made in.
    /// </summary>
    private void OnRemind(object? sender, PromiseCard card)
    {
        if (App.Repository is not { } repository) return;

        var subject = $"{card.ContactName} · {card.CallStartedAt.ToLocalTime():d MMMM}";

        if (RemindWindow.Open(Window.GetWindow(this), repository, card.Commitment.CallId, subject, card.Obligation)
            && DataContext is PromisesViewModel model)
        {
            model.Refresh();
        }
    }
}
