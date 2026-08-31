using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

public partial class OverviewPage
{
    public OverviewPage() => InitializeComponent();

    private OverviewViewModel? ViewModel => DataContext as OverviewViewModel;

    /// <summary>Opens the conversation a due reminder points at.</summary>
    private void DueCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DueCard due }) return;

        Open(due.CallId);
    }

    /// <summary>
    /// Opens the conversation a row on the first screen refers to.
    ///
    /// The rows already drew themselves as pressable and did nothing when pressed. That is worse
    /// than a plain list: it teaches somebody that the overview is a display rather than a way in,
    /// and once learned they stop trying. This makes the shortest question — "what happened
    /// today" — one click from its answer.
    /// </summary>
    private void RecentCall_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentCall row }) return;

        Open(row.Call.Id);
    }

    private void Open(long callId)
    {
        var window = new CallWindow(new CallWindowViewModel(
            App.Repository, () => App.Settings, App.HttpClient, callId))
        {
            Owner = Window.GetWindow(this),
        };

        // Shown rather than shown modally, for the same reason the contact page opens it that
        // way: reading a conversation while looking something else up is the ordinary way to use
        // this, and a modal window forbids it.
        window.Show();
    }

    // ---- the panel: drag in, drag around, take off --------------------------
    //
    // A drag begins only after the pointer has moved a real distance with the button down, so a
    // click stays a click — and a completed drag swallows the MouseUp, so the row's open-on-click
    // cannot fire on the same gesture. Everything a drag can do also exists in the right-click
    // menus; a feature that exists only as a gesture is invisible and unusable from the keyboard.

    private const string DragFormat = "voicetranscript/call-id";

    private Point _dragOrigin;

    private void Drag_Prime(object sender, MouseButtonEventArgs e)
        => _dragOrigin = e.GetPosition(this);

    private void RecentCall_DragMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentCall row } element)
            MaybeStartDrag(element, e, row.Call.Id);
    }

    private void PanelCard_DragMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PanelCard card } element)
            MaybeStartDrag(element, e, card.CallId);
    }

    private void MaybeStartDrag(FrameworkElement source, MouseEventArgs e, long callId)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(this) - _dragOrigin;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(source, new DataObject(DragFormat, callId), DragDropEffects.Move);
    }

    private void BoardPanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void BoardPanel_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } model) return;
        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not long callId) return;

        var known = model.Board.Any(c => c.CallId == callId);

        if (!known)
        {
            model.AddToBoard(callId);
        }

        // Where in the pile it landed: before the card whose upper half the pointer is over,
        // after the one whose lower half, and at the end when it fell on empty panel.
        model.MoveCardTo(callId, DropIndex(e));

        e.Handled = true;
    }

    /// <summary>The panel index the drop point corresponds to.</summary>
    private int DropIndex(DragEventArgs e)
    {
        if (ViewModel is not { } model) return int.MaxValue;

        for (var i = 0; i < model.Board.Count; i++)
        {
            if (FindCard(model.Board[i]) is not { } element) continue;

            var y = e.GetPosition(element).Y;

            if (y < element.ActualHeight / 2) return i;
            if (y < element.ActualHeight) return i + 1;
        }

        return model.Board.Count;
    }

    private FrameworkElement? FindCard(PanelCard card)
    {
        // The template's root Border carries the card as its DataContext; the visual tree is the
        // only place that pairing exists, so it is walked rather than modelled.
        foreach (var child in Descendants(BoardPanel))
        {
            if (child is System.Windows.Controls.Border { DataContext: PanelCard c } border
                && ReferenceEquals(c, card))
            {
                return border;
            }
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;

            foreach (var below in Descendants(child)) yield return below;
        }
    }

    // ---- click handlers: the non-drag equivalents ---------------------------

    private void PanelCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PanelCard card }) Open(card.CallId);
    }

    private static PanelCard? CardOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as PanelCard;

    private void PanelOpen_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) Open(card.CallId);
    }

    private void PanelUp_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) ViewModel?.MoveCardUp(card.CallId);
    }

    private void PanelDown_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) ViewModel?.MoveCardDown(card.CallId);
    }

    private void PanelRemove_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) ViewModel?.RemoveFromBoard(card.CallId);
    }

    private void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentCall row) Open(row.Call.Id);
    }

    private void RecentToBoard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentCall row)
            ViewModel?.AddToBoard(row.Call.Id);
    }
}
