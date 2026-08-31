using System.Windows;
using System.Windows.Input;
using VoiceTranscript.App.ViewModels;

namespace VoiceTranscript.App.Controls;

/// <summary>
/// Turns any element into a timeline that can be dragged.
///
/// Clicking a waveform to jump was already possible; holding and dragging was not, and that is
/// the difference between a player somebody uses to check a quote and one they abandon. Finding
/// a sentence in a forty-minute recording is a search, not a guess: you drag, you listen to where
/// you landed, you adjust. A control that only accepts single clicks makes every adjustment a new
/// act of aim.
///
/// Attached rather than a control because the two places that need it draw completely different
/// things — a mirrored waveform on the contact page, a slim bar in the call window — and the only
/// thing they share is the arithmetic turning an X coordinate into a moment.
///
/// <code>
/// &lt;Grid controls:Scrubbable.Player="{Binding Playback}"&gt; … &lt;/Grid&gt;
/// </code>
/// </summary>
public static class Scrubbable
{
    public static readonly DependencyProperty PlayerProperty =
        DependencyProperty.RegisterAttached(
            "Player", typeof(PlaybackViewModel), typeof(Scrubbable),
            new PropertyMetadata(null, OnPlayerChanged));

    public static void SetPlayer(DependencyObject target, PlaybackViewModel? value) =>
        target.SetValue(PlayerProperty, value);

    public static PlaybackViewModel? GetPlayer(DependencyObject target) =>
        (PlaybackViewModel?)target.GetValue(PlayerProperty);

    /// <summary>
    /// Whether the handlers are already attached.
    ///
    /// The player arrives by binding, so this fires again whenever the data context is replaced —
    /// which on the contact page is every time somebody clicks a different call. Without the
    /// guard the element would accumulate a further set of handlers each time and a single drag
    /// would eventually seek once per call ever selected.
    /// </summary>
    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(Scrubbable), new PropertyMetadata(false));

    private static void OnPlayerChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)element.GetValue(HookedProperty)) return;

        element.SetValue(HookedProperty, true);

        element.MouseLeftButtonDown += OnPressed;
        element.MouseMove += OnMoved;
        element.MouseLeftButtonUp += OnReleased;

        // Capture can be taken away — another window comes forward, a touch gesture wins, the
        // element is unloaded mid-drag. Without this the view model would be left believing a
        // drag is still in progress, and the playhead would stop following the audio for good.
        element.LostMouseCapture += (s, _) =>
        {
            if (s is DependencyObject o) GetPlayer(o)?.EndScrub();
        };
    }

    private static void OnPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (GetPlayer(element) is not { } player) return;

        element.CaptureMouse();
        player.ScrubTo(FractionAt(element, e));

        e.Handled = true;
    }

    private static void OnMoved(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        // Only while this element holds the mouse. A pointer merely passing over a waveform is
        // not asking for anything.
        if (!element.IsMouseCaptured) return;
        if (GetPlayer(element) is not { } player) return;

        player.ScrubTo(FractionAt(element, e));
    }

    private static void OnReleased(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        element.ReleaseMouseCapture();
        GetPlayer(element)?.EndScrub();

        e.Handled = true;
    }

    /// <summary>
    /// Where along the element the pointer is, as a fraction.
    ///
    /// A fraction rather than pixels because the drawing is stretched to whatever width the
    /// window happens to be. Working in pixels would put the playhead somewhere else after a
    /// resize, and a player that lands near the moment rather than on it is one nobody trusts
    /// enough to check a quote with.
    /// </summary>
    private static double FractionAt(FrameworkElement element, MouseEventArgs e)
    {
        var width = element.ActualWidth;

        return width <= 0 ? 0 : e.GetPosition(element).X / width;
    }
}
