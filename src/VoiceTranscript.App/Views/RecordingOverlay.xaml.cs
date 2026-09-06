using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VoiceTranscript.Core.Text;

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

        // One timer, not two. The meter is read from the tick that was already running: a second
        // DispatcherTimer on the same one-second cadence would be a second wake-up of the UI
        // thread throughout every conversation, for a figure that only moves once a second.
        _ticker.Tick += (_, _) =>
        {
            Elapsed.Text = Format(DateTimeOffset.Now - _startedAt);
            ShowMeter();
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle);

            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        };

        // Positioned once the real size is known. SizeToContent means the width is not settled
        // until the content has been measured, and centring against zero puts it off-screen.
        SizeChanged += (_, _) => Reposition();

        Services.WindowDrag.Attach(this, Body, () =>
        {
            Anchor = new Point(Left, Top);
            Moved?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Raised when the user presses Durdur on the strip itself.</summary>
    public event EventHandler? StopRequested;

    /// <summary>Raised after the user has dragged the strip somewhere else.</summary>
    public event EventHandler? Moved;

    /// <summary>Where it was left last time, or null while it has never been moved.</summary>
    public Point? Anchor { get; set; }

    // Dragged by its body: it has no title bar, because it is a strip and not a window. The
    // buttons on it swallow their own clicks, so Durdur and ✕ still work; only the space between
    // them starts a drag.

    /// <summary>Starts the strip's clock and shows it.</summary>
    public void Begin(DateTimeOffset startedAt, string? headline = null)
    {
        _startedAt = startedAt;

        Label.Text = headline ?? Localisation.T("recordingoverlay.kaydediliyor");
        Elapsed.Text = Format(DateTimeOffset.Now - startedAt);

        // Decided per call rather than once at construction: the setting can be turned on between
        // two conversations, and the strip is built lazily and then reused for the rest of the
        // session. Hidden outright rather than disabled — a greyed button on a strip this small is
        // three seconds of somebody wondering what they did wrong.
        IntentButton.Visibility = App.Settings.IntentCardEnabled && CallInProgress is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Drawn once here as well as on every tick, so the strip does not appear a second wide
        // and then grow when the first tick lands.
        ShowMeter();

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
    /// Puts the strip where its owner left it, or on the top edge of the screen holding the main
    /// window while it has never been moved.
    ///
    /// The main window's screen rather than the primary one: on a two-monitor desk the call is
    /// where the person is looking, and a warning on the other monitor is not a warning.
    /// </summary>
    public void Reposition()
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

        var at = Services.OverlayPlacement.Resolve(
            ActualWidth, ActualHeight, area, Anchor?.X, Anchor?.Y);

        Left = at.X;
        Top = at.Y;
    }

    /// <summary>Width of the share bar's track, matching the Border in the markup.</summary>
    private const double ShareBarWidth = 46;

    /// <summary>
    /// Puts the current reading on the strip, or takes the meter off it.
    ///
    /// Read from the orchestrator once a second rather than pushed from the capture thread. A
    /// push would mean the audio path raising an event a hundred times a second into the
    /// dispatcher, which is exactly the work the rule about the recording path forbids; pulling
    /// costs the UI thread one array walk and the capture thread nothing.
    ///
    /// Three states and no fourth: a share, a line saying why there is no share, or nothing at
    /// all while there is not yet anything to say. Nothing here warns, and nothing here is a
    /// number in decibels.
    /// </summary>
    private void ShowMeter()
    {
        var meter = App.Settings.LiveTalkMeterEnabled && CallInProgress is not null
            ? App.Orchestrator?.LiveMeter
            : null;

        if (meter is null)
        {
            Meter.Visibility = Visibility.Collapsed;
            MeterNote.Visibility = Visibility.Collapsed;
            return;
        }

        var reading = meter.Read();

        Meter.Visibility = reading.State == Services.TalkMeterState.Measured
            ? Visibility.Visible
            : Visibility.Collapsed;

        MeterNote.Visibility = reading.State == Services.TalkMeterState.Bleeding
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (reading.State != Services.TalkMeterState.Measured) return;

        // Rounded to whole points. A share written to a decimal place claims a precision this
        // does not have: the gate is a level threshold and the window is a minute wide.
        ShareText.Text = string.Format(
            Localisation.T("recordingoverlay.sen-yuzde-n"),
            (int)Math.Round(reading.Share * 100));

        ShareFill.Width = Math.Clamp(reading.Share, 0, 1) * ShareBarWidth;

        TrendMark.Text = reading.Trend switch
        {
            Services.TalkMeterTrend.Above => "▲",
            Services.TalkMeterTrend.Below => "▼",
            Services.TalkMeterTrend.Level => "—",

            // No arrow at all rather than a flat one, for the first half-minute and whenever the
            // user has not spoken enough to have a median. A flat arrow is a claim.
            _ => " ",
        };
    }

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The conversation being recorded right now, asked of the orchestrator rather than passed in.
    ///
    /// Asked rather than passed because the row exists from the instant capture starts, and the
    /// strip is created and shown from a different place that has no reason to know about it. It
    /// is null between calls, which is exactly when there is nothing to write a note against.
    /// </summary>
    private static long? CallInProgress => App.Orchestrator?.CurrentCallId;

    /// <summary>
    /// Opens the intent note for the call in progress.
    ///
    /// Owned by the main window rather than by the strip. The strip is deliberately never
    /// activated (WS_EX_NOACTIVATE, above), and a modal dialog owned by a window that refuses
    /// focus is a dialog whose text box cannot be typed into.
    /// </summary>
    private void Intent_Click(object sender, RoutedEventArgs e)
    {
        if (App.Repository is not { } repository || CallInProgress is not { } callId) return;

        NiyetWindow.Open(
            Application.Current?.MainWindow,
            repository,
            callId,
            Localisation.T("niyetwindow.suren-gorusme"));
    }

    /// <summary>
    /// Hides the strip without stopping the recording.
    ///
    /// Offered because the alternative is that somebody who finds it in the way stops the
    /// recording to be rid of it, or turns the strip off permanently in settings. Both cost more
    /// than letting them dismiss it for one call — it comes back on the next one.
    /// </summary>
    private void HideForCall_Click(object sender, RoutedEventArgs e)
    {
        End();

        // The caller card goes with it. They are one thing to look at and one thing to be rid
        // of; dismissing the strip and leaving a panel floating over the call would be a close
        // button that did not close what the user was pointing at.
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the user closes the strip, so whatever is stacked under it can go too.</summary>
    public event EventHandler? Dismissed;
}
