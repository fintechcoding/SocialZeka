using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.App.Views;

/// <summary>
/// The panel that says who is on the other end of the call, and the two or three things about
/// them worth knowing before the call is over.
///
/// The window styles here are the same two that make <see cref="RecordingOverlay"/> usable, and
/// they are load-bearing for the same reasons.
///
/// <c>WS_EX_NOACTIVATE</c> stops it taking focus. This appears in the middle of a conversation,
/// and a panel that steals the keyboard from the call window mid-sentence is worse than no panel.
///
/// <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt+Tab, so a call does not silently add an entry to
/// the task switcher.
///
/// It sits below the recording strip rather than replacing it. They answer different questions —
/// "am I being recorded" and "who is this" — and the first one must never be crowded out by the
/// second.
/// </summary>
public partial class CallerOverlay
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    /// <summary>Roughly the height of the recording strip, so the two stack rather than overlap.</summary>
    private const double StripHeight = 34;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint window, int index, int value);

    public CallerOverlay()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle);

            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        };

        // Positioned once the real size is known: SizeToContent leaves ActualWidth at zero until
        // the content has been measured, and centring against zero puts the window off-screen.
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>
    /// Shows who this is.
    ///
    /// Takes the pieces rather than a contact id so the window needs no repository and can be
    /// built by the smoke test with nothing behind it.
    /// </summary>
    public void Begin(
        string name,
        double confidence,
        DateTimeOffset? lastCall,
        string? notes,
        IReadOnlyList<string> commitments)
    {
        NameText.Text = name;

        // Said out loud, because this is a guess. A name with a number beside it is an offer; a
        // name on its own reads as a fact, and this one is not one.
        ConfidenceText.Text = $"%{confidence * 100:0}";

        LastCallText.Text = lastCall is { } when
            ? $"{Localisation.T("calleroverlay.son-gorusme")} {when.ToLocalTime():d MMMM}"
            : "";
        LastCallText.Visibility = lastCall is null ? Visibility.Collapsed : Visibility.Visible;

        NotesText.Text = notes ?? "";
        NotesSection.Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible;

        CommitmentsList.ItemsSource = commitments;
        CommitmentsSection.Visibility = commitments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (!IsVisible) Show();

        Reposition();
    }

    /// <summary>Hides it. Safe to call when it is already hidden.</summary>
    public void End()
    {
        if (IsVisible) Hide();
    }

    /// <summary>
    /// Dismissed for this call, back for the next one.
    ///
    /// The same semantic as the recording strip's ✕: somebody who wants it out of the way now is
    /// not saying they never want it, and making them go to the settings window to get it back
    /// would mean they never do.
    /// </summary>
    private void HideForCall_Click(object sender, RoutedEventArgs e) => End();

    /// <summary>Directly under the recording strip, on the screen holding the main window.</summary>
    private void Reposition()
    {
        var area = SystemParameters.WorkArea;

        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            var centre = owner.Left + owner.Width / 2;

            if (!double.IsNaN(centre) && centre > area.Right)
                area = new Rect(area.Left + area.Width, area.Top, area.Width, area.Height);
        }

        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + StripHeight;
    }

    /// <summary>
    /// One commitment as a line short enough to read during a call.
    ///
    /// The obligation and its deadline, and who owes it. The quote is left behind deliberately —
    /// it is what makes the entry checkable afterwards, and afterwards is not now.
    /// </summary>
    public static string Line(Commitment commitment)
    {
        var who = commitment.ByMe ? "sen" : "o";
        var when = commitment.DeadlineRaw is { Length: > 0 } raw ? $" · {raw}" : "";

        return $"{who}: {commitment.Obligation}{when}";
    }
}
