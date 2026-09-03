using System.Windows;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Where the two call overlays sit.
///
/// Separate from the windows because the interesting cases are the ones nobody sees while
/// developing: a position saved on a second monitor that is no longer attached, a strip dragged
/// half off the right edge, a laptop whose work area shrank when the taskbar moved. Each of them
/// ends with the only signal that the microphone is open being somewhere nobody can see it,
/// which is worse than not having the signal at all.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>
    /// The top-left corner for a window of this size.
    ///
    /// Never moved: centred on the top edge, which is where a status strip belongs and where it
    /// was before it could be moved. Moved: where it was left, pushed back inside the work area
    /// if it no longer fits — enough of it has to be reachable to drag it somewhere better.
    /// </summary>
    public static Point Resolve(double width, double height, Rect area, double? left, double? top)
    {
        if (left is not { } x || top is not { } y)
            return new Point(area.Left + ((area.Width - width) / 2), area.Top);

        return new Point(
            Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)));
    }
}
