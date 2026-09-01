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

        if (e.Effects == DragDropEffects.None)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }

        ShowDropLineAt(DropIndex(e));
    }

    private void BoardPanel_DragLeave(object sender, DragEventArgs e)
        => DropLine.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Draws the landing line above the card at the given index — or under the last one.
    ///
    /// Positioned with a margin rather than an adorner: the panel is one small vertical pile,
    /// and an adorner layer would be machinery for a two-pixel rectangle.
    /// </summary>
    private void ShowDropLineAt(int index)
    {
        if (ViewModel is not { } model || model.Board.Count == 0)
        {
            DropLine.Visibility = Visibility.Collapsed;
            return;
        }

        FrameworkElement? anchor;
        double y;

        // Measured against the element the line actually lives in. Against the outer Border it
        // was off by that Border's padding — a guide line drawn five pixels wrong is worse than
        // none, because it teaches the eye to distrust the guide.
        if (index < model.Board.Count)
        {
            anchor = FindCard(model.Board[index]);
            if (anchor is null) return;

            y = anchor.TranslatePoint(new Point(0, 0), BoardInner).Y - 2;
        }
        else
        {
            anchor = FindCard(model.Board[^1]);
            if (anchor is null) return;

            y = anchor.TranslatePoint(new Point(0, anchor.ActualHeight), BoardInner).Y + 1;
        }

        DropLine.Margin = new Thickness(6, Math.Max(0, y), 6, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private void BoardPanel_Drop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;

        if (ViewModel is not { } model) return;
        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not long callId) return;

        // The index is read BEFORE the pile changes: AddToBoard rebuilds the collection, and an
        // index measured against the old visuals applied to the new list appended every fresh
        // drop to the end regardless of where the hand let go.
        var index = DropIndex(e);

        var from = model.Board.ToList().FindIndex(c => c.CallId == callId);

        if (from < 0)
        {
            model.AddToBoard(callId);
        }
        else if (from < index)
        {
            // Dragging downward: the card is still IN the list the index was measured against,
            // so its own removal shifts everything after it up by one. Without this, every
            // downward drop landed one slot below the line the panel had just drawn — the exact
            // surprise the line exists to prevent.
            index--;
        }

        model.MoveCardTo(callId, index);

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

    /// <summary>Sets or clears the card's reminder; it surfaces in the Bugün tab on that day.</summary>
    private void PanelRemind_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;

        RemindWindow.Open(
            Window.GetWindow(this), App.Repository, card.CallId, $"{card.ContactName} · {card.When}");
        ViewModel?.Refresh();
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

    /// <summary>The due reminder's own verb: cleared, card stays on the pile.</summary>
    private void DueDone_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DueCard due)
            ViewModel?.RemindCard(due.CallId, 0);
    }

    /// <summary>
    /// A reminder straight from the conversation list — the entry the feature was missing.
    /// One step: onto the pile if it was not there, and dated.
    /// </summary>
    private void RecentRemind_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentCall row) return;

        // The dialog itself puts the call on the board when saved — no card is created for a
        // reminder the user then cancels.
        RemindWindow.Open(
            Window.GetWindow(this), App.Repository, row.Call.Id, $"{row.ContactName} · {row.When}");
        ViewModel?.Refresh();
    }

    /// <summary>A tag pill is a question: "which other conversations did I mark with this?"</summary>
    private void TagPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string tag) return;

        MainWindow.SearchTagFromAnywhere(tag);
        e.Handled = true;
    }

    /// <summary>Picks a day on the calendar; bare days clear the pick.</summary>
    private void CalendarDay_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CalendarDay day)
            ViewModel?.SelectCalendarDay(day);
    }

    /// <summary>A reminder on the calendar opens the conversation it hangs on.</summary>
    private void CalendarReminder_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CalendarReminder reminder)
            Open(reminder.CallId);
    }

    /// <summary>An own-promise row opens the conversation the promise was made in.</summary>
    private void CalendarPromise_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CalendarPromise promise)
            Open(promise.CallId);
    }
}
