using System.Windows;
using System.Windows.Controls;

using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Draws the conversation against the clock: theirs on the left, mine on the right, each line at
/// the moment it was said.
///
/// The placement rule and the reasoning behind it live in <see cref="TimelineLayout"/>, which is
/// pure and tested. This class does only what a panel has to do — measure the children, ask where
/// they go, and put them there.
/// </summary>
public sealed class TimelinePanel : Panel
{
    /// <summary>Space between the two columns.</summary>
    private const double ColumnGap = 24;

    /// <summary>
    /// How many pixels one second is worth.
    ///
    /// Set by the window from the call's length and the height of the viewport, so a two-minute
    /// call and a nineteen-minute one are both readable. Zero means "not set yet", and the panel
    /// falls back to a middling density rather than collapsing to nothing.
    /// </summary>
    public static readonly DependencyProperty PixelsPerSecondProperty =
        DependencyProperty.Register(
            nameof(PixelsPerSecond), typeof(double), typeof(TimelinePanel),
            new FrameworkPropertyMetadata(
                30.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        var column = Math.Max(80, (width - ColumnGap) / 2);

        var items = new List<TimelineLayout.Item>(InternalChildren.Count);

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(column, double.PositiveInfinity));
            items.Add(ItemFor(child));
        }

        var tops = TimelineLayout.Tops(items, Density());

        return new Size(width, TimelineLayout.Height(items, tops));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var column = Math.Max(80, (finalSize.Width - ColumnGap) / 2);

        var items = new List<TimelineLayout.Item>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren) items.Add(ItemFor(child));

        var tops = TimelineLayout.Tops(items, Density());

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var mine = items[i].IsMe;
            var left = mine ? finalSize.Width - column : 0;

            child.Arrange(new Rect(left, tops[i], column, child.DesiredSize.Height));
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, TimelineLayout.Height(items, tops)));
    }

    private double Density() => PixelsPerSecond > 0 ? PixelsPerSecond : 30;

    /// <summary>
    /// What the layout needs to know about one bubble.
    ///
    /// A child whose data is not a turn — anything the template system leaves behind — is placed
    /// at zero on the left rather than throwing. A drawing with one thing in the wrong place is
    /// recoverable; a window that will not open is not.
    /// </summary>
    private static TimelineLayout.Item ItemFor(UIElement child)
    {
        var turn = (child as FrameworkElement)?.DataContext as ChatTurn;

        return new TimelineLayout.Item(
            turn?.StartMs ?? 0, turn?.IsMe ?? false, child.DesiredSize.Height);
    }
}
