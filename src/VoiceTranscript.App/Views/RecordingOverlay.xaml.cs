using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VoiceTranscript.App.Views;

/// <summary>
/// The strip at the top of the screen that says a recording is running.
///
/// Two window styles here are load-bearing and neither is obvious.
///
/// <c>WS_EX_NOACTIVATE</c> stops the strip from ever taking focus. Without it, showing it the
/// moment a call connects steals the keyboard from the call window — so the first thing this
/// application would do on every call is interfere with the call.
///
/// <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt+Tab. A recorder that puts an extra entry in the
/// task switcher for the duration of every conversation is an irritation somebody will
/// eventually turn off, and turning it off means losing the one signal that says the microphone
/// is open.
/// </summary>
public partial class RecordingOverlay
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint window, int index, int value);

    private readonly DispatcherTimer _ticker;
    private DateTimeOffset _startedAt;

    public RecordingOverlay()
    {
        InitializeComponent();

        _ticker = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _ticker.Tick += (_, _) => Elapsed.Text = Format(DateTimeOffset.Now - _startedAt);

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle);

            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        };

        // Positioned once the real size is known. SizeToContent means the width is not settled
        // until the content has been measured, and centring against zero puts it off-screen.
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Raised when the user presses Durdur on the strip itself.</summary>
    public event EventHandler? StopRequested;

    /// <summary>Starts the strip's clock and shows it.</summary>
    public void Begin(DateTimeOffset startedAt, string? headline = null)
    {
        _startedAt = startedAt;

        Label.Text = headline ?? "Kaydediliyor";
        Elapsed.Text = Format(DateTimeOffset.Now - startedAt);

        _ticker.Start();

        if (!IsVisible) Show();

        Reposition();
    }

    /// <summary>Hides the strip and stops its clock. Safe to call when it is already hidden.</summary>
    public void End()
    {
        _ticker.Stop();

        if (IsVisible) Hide();
    }

    /// <summary>
    /// Centres the strip on the top edge of the screen holding the main window.
    ///
    /// The main window's screen rather than the primary one: on a two-monitor desk the call is
    /// where the person is looking, and a warning on the other monitor is not a warning.
    /// </summary>
    private void Reposition()
    {
        var area = SystemParameters.WorkArea;

        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            // Kept within the same virtual-desktop band as the main window, without taking a
            // dependency on WinForms just to enumerate screens.
            var centre = owner.Left + owner.Width / 2;

            if (!double.IsNaN(centre) && centre > area.Right)
                area = new Rect(area.Left + area.Width, area.Top, area.Width, area.Height);
        }

        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top;
    }

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Hides the strip without stopping the recording.
    ///
    /// Offered because the alternative is that somebody who finds it in the way stops the
    /// recording to be rid of it, or turns the strip off permanently in settings. Both cost more
    /// than letting them dismiss it for one call — it comes back on the next one.
    /// </summary>
    private void HideForCall_Click(object sender, RoutedEventArgs e) => End();
}
