using System.Windows;
using System.Windows.Input;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Makes a borderless window draggable by a surface inside it.
///
/// Deliberately not <see cref="Window.DragMove"/>. That call asks Windows to run its own modal
/// move loop through WM_SYSCOMMAND, and the two windows this is for carry WS_EX_NOACTIVATE —
/// they must never take focus, because they appear in the middle of somebody's call. Handing a
/// window that refuses activation to the system move loop is exactly the kind of thing that
/// works on one machine and does nothing on another, with no error either way.
///
/// So the drag is done here, in device-independent units throughout: the grab point and the
/// current point are both measured against the window, so the difference between them is the
/// movement whatever the display scaling is. Mouse capture keeps the moves arriving after the
/// pointer has left the window, which it does immediately, because the window is travelling
/// with it.
/// </summary>
public static class WindowDrag
{
    /// <param name="window">The window to move.</param>
    /// <param name="handle">The surface that starts a drag — usually the whole body.</param>
    /// <param name="dropped">Called once, after the button is released, with the move finished.</param>
    public static void Attach(Window window, UIElement handle, Action dropped)
    {
        var dragging = false;
        var grabbed = default(Point);

        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            grabbed = e.GetPosition(window);
            dragging = handle.CaptureMouse();
        };

        handle.MouseMove += (_, e) =>
        {
            if (!dragging) return;

            var now = e.GetPosition(window);

            window.Left += now.X - grabbed.X;
            window.Top += now.Y - grabbed.Y;
        };

        handle.MouseLeftButtonUp += (_, _) =>
        {
            if (!dragging) return;

            dragging = false;
            handle.ReleaseMouseCapture();
            dropped();
        };
    }
}
