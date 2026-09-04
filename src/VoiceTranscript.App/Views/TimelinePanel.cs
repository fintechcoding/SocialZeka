using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using VoiceTranscript.App.Services;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Draws the conversation against the clock: theirs on the left, mine on the right, each line at
/// the moment it was said.
///
/// The placement rule and the reasoning behind it live in <see cref="TimelineLayout"/>, which is
/// pure and tested. What this class adds is the part that makes the drawing legible as time rather
/// than as two columns of text: a spine down the middle with the minutes marked on it, a tick
/// where each line begins, and a bar showing how long it ran.
///
/// <b>The bar is the point.</b> A list can show who spoke and in what order. Only this view can
/// show that one of those turns took eleven seconds and the one under it took two — and holding
/// the floor is most of what a conversation's shape is made of. It is drawn from the real
/// duration, so where a short line needs three tall lines of text the bar is visibly shorter than
/// its bubble, which is the honest picture: the words needed the room, the speaking did not take
/// that long.
/// </summary>
public sealed class TimelinePanel : Panel
{
    /// <summary>Space between the two columns, where the spine and the minute labels live.</summary>
    private const double ColumnGap = 56;

    /// <summary>Width of the bar that shows how long a line ran.</summary>
    private const double BarWidth = 3;

    /// <summary>Gap between a bubble and its own duration bar.</summary>
    private const double BarInset = 7;

    /// <summary>What was placed where, kept from arrange so the drawing can mark it.</summary>
    private readonly List<Placed> _placed = [];

    private readonly record struct Placed(double Top, double Height, double BarHeight, bool IsMe);

    /// <summary>
    /// How many pixels one second is worth, worked out during measure and kept for the arrange
    /// and the drawing so all three agree.
    ///
    /// It used to be a property the window set from the call's duration. That could not work: the
    /// right density depends on how much was said, and the amount of text is only known here,
    /// after the children have been measured. See <see cref="TimelineLayout.PixelsPerSecond"/>.
    /// </summary>
    private double _density = 30;

    /// <summary>Colour of the spine, the minute rules and their labels.</summary>
    public static readonly DependencyProperty RuleBrushProperty =
        DependencyProperty.Register(
            nameof(RuleBrush), typeof(Brush), typeof(TimelinePanel),
            new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush RuleBrush
    {
        get => (Brush)GetValue(RuleBrushProperty);
        set => SetValue(RuleBrushProperty, value);
    }

    /// <summary>Colour of my duration bars.</summary>
    public static readonly DependencyProperty MineBrushProperty =
        DependencyProperty.Register(
            nameof(MineBrush), typeof(Brush), typeof(TimelinePanel),
            new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush MineBrush
    {
        get => (Brush)GetValue(MineBrushProperty);
        set => SetValue(MineBrushProperty, value);
    }

    /// <summary>Colour of their duration bars.</summary>
    public static readonly DependencyProperty TheirsBrushProperty =
        DependencyProperty.Register(
            nameof(TheirsBrush), typeof(Brush), typeof(TimelinePanel),
            new FrameworkPropertyMetadata(Brushes.Silver, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TheirsBrush
    {
        get => (Brush)GetValue(TheirsBrushProperty);
        set => SetValue(TheirsBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        var column = ColumnWidth(width);

        var items = new List<TimelineLayout.Item>(InternalChildren.Count);

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(column, double.PositiveInfinity));
            items.Add(ItemFor(child));
        }

        // The scale comes from what was said, and this is the first moment that is known.
        var span = items.Count > 0 ? items.Max(i => Math.Max(i.EndMs, i.StartMs)) : 0;
        var text = items.Sum(i => i.Height);

        _density = TimelineLayout.PixelsPerSecond(span, text);

        var tops = TimelineLayout.Tops(items, _density);

        return new Size(width, TimelineLayout.Height(items, tops));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var column = ColumnWidth(finalSize.Width);
        var density = Density();

        var items = new List<TimelineLayout.Item>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren) items.Add(ItemFor(child));

        var tops = TimelineLayout.Tops(items, density);

        _placed.Clear();

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var item = items[i];
            var height = child.DesiredSize.Height;
            var left = item.IsMe ? finalSize.Width - column : 0;

            child.Arrange(new Rect(left, tops[i], column, height));

            var spoken = Math.Max(0, item.EndMs - item.StartMs) / 1000.0 * density;

            _placed.Add(new Placed(tops[i], height, spoken, item.IsMe));
        }

        InvalidateVisual();

        return new Size(finalSize.Width, Math.Max(finalSize.Height, TimelineLayout.Height(items, tops)));
    }

    /// <summary>The spine, the minutes on it, and a bar for every line's own length.</summary>
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var density = Density();
        var middle = Math.Round(ActualWidth / 2) + 0.5;
        var column = ColumnWidth(ActualWidth);

        var faint = Faint(RuleBrush, 0.55);
        var spine = new Pen(faint, 1);
        spine.Freeze();

        context.DrawLine(spine, new Point(middle, 0), new Point(middle, ActualHeight));

        // ---- the minutes ----
        //
        // The step comes from the density rather than being fixed: at eight pixels a second a
        // minute is half a screen, at sixty it is eight screens, and one figure cannot serve both.
        var step = StepSeconds(density);
        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var second = step; second * density < ActualHeight; second += step)
        {
            var y = Math.Round(second * density) + 0.5;

            var label = new FormattedText(
                $"{second / 60:00}:{second % 60:00}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.0, faint, dpi);

            // The rule stops either side of its own label rather than running through it.
            var half = label.Width / 2 + 6;

            context.DrawLine(spine, new Point(0, y), new Point(middle - half, y));
            context.DrawLine(spine, new Point(middle + half, y), new Point(ActualWidth, y));

            context.DrawText(label, new Point(middle - label.Width / 2, y - label.Height / 2));
        }

        // ---- how long each line ran ----
        foreach (var placed in _placed)
        {
            if (placed.BarHeight <= 0) continue;

            // On the gutter side of the bubble, so the bars of both speakers face the spine and
            // can be compared against each other without the eye travelling.
            var x = placed.IsMe
                ? ActualWidth - column - BarInset - BarWidth
                : column + BarInset;

            var brush = placed.IsMe ? MineBrush : TheirsBrush;

            context.DrawRoundedRectangle(
                Faint(brush, 0.55), null,
                new Rect(x, placed.Top + 2, BarWidth, Math.Max(BarWidth, placed.BarHeight)),
                BarWidth / 2, BarWidth / 2);
        }
    }

    /// <summary>
    /// How many seconds one rule is worth at this density.
    ///
    /// Chosen so the rules land roughly <see cref="TargetSpacingPx"/> apart whatever the call's
    /// length. A fixed minute cannot do that: at eight pixels a second a minute is half a screen
    /// and at sixty it is eight screens, so the same figure gives a crowded ladder on one call and
    /// a blank page on the next. The ladder runs from five seconds up so that a short call, drawn
    /// dense, still has something to measure against.
    /// </summary>
    public static int StepSeconds(double pixelsPerSecond)
    {
        foreach (var step in (int[])[5, 10, 15, 30, 60, 120, 300, 600])
        {
            if (step * pixelsPerSecond >= TargetSpacingPx) return step;
        }

        return 900;
    }

    /// <summary>How far apart the minute rules should sit. Close enough to measure by, far enough to read past.</summary>
    private const double TargetSpacingPx = 100;

    private double ColumnWidth(double width) => Math.Max(80, (width - ColumnGap) / 2);

    private double Density() => _density > 0 ? _density : 30;

    /// <summary>The same colour, quieter. Drawn behind the text rather than competing with it.</summary>
    private static Brush Faint(Brush brush, double opacity)
    {
        var copy = brush.CloneCurrentValue();
        copy.Opacity = opacity;
        copy.Freeze();

        return copy;
    }

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
            turn?.StartMs ?? 0, turn?.IsMe ?? false, child.DesiredSize.Height, turn?.EndMs ?? 0);
    }
}
