using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

    /// <summary>
    /// How far below the anchor this panel sits — the strip's real height when it is on screen.
    ///
    /// It used to be the constant 34, which is a guess at the height of another window: right
    /// until the strip's font, padding or scaling changed, and then the two overlapped with no
    /// symptom anybody could connect to a number in this file.
    /// </summary>
    public double AnchorOffset { get; set; } = 34;

    /// <summary>Where the pair was left, or null while it has never been moved.</summary>
    public Point? Anchor { get; set; }

    /// <summary>Raised after the user has dragged this panel somewhere else.</summary>
    public event EventHandler? Moved;

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

        // Moving either window moves the pair, so they never come apart and end up on opposite
        // sides of the screen. The anchor is the STRIP's corner, hence the offset.
        Services.WindowDrag.Attach(this, Body, () =>
        {
            Anchor = new Point(Left, Top - AnchorOffset);
            Moved?.Invoke(this, EventArgs.Empty);
        });
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
        IReadOnlyList<CommitmentLine> given,
        IReadOnlyList<CommitmentLine> mine)
    {
        NameText.Text = name;
        InitialText.Text = Initial(name);

        // Said out loud, because this is a guess. A name with a number beside it is an offer; a
        // name on its own reads as a fact, and this one is not one.
        ConfidenceText.Text = $"%{confidence * 100:0}";

        LastCallText.Text = lastCall is { } when
            ? $"{Localisation.T("calleroverlay.son-gorusme")} {when.ToLocalTime():d MMMM}"
            : "";
        LastCallText.Visibility = lastCall is null ? Visibility.Collapsed : Visibility.Visible;

        NotesText.Text = notes ?? "";
        NotesSection.Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible;

        GivenList.ItemsSource = given;
        GivenTitle.Visibility = GivenList.Visibility = given.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        CommitmentsList.ItemsSource = mine;
        MineTitle.Visibility = CommitmentsList.Visibility = mine.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        CommitmentsSection.Visibility = given.Count + mine.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

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
    public void Reposition()
    {
        var area = SystemParameters.WorkArea;

        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            var centre = owner.Left + owner.Width / 2;

            if (!double.IsNaN(centre) && centre > area.Right)
                area = new Rect(area.Left + area.Width, area.Top, area.Width, area.Height);
        }

        var at = Services.OverlayPlacement.Resolve(
            ActualWidth, ActualHeight + AnchorOffset, area, Anchor?.X, Anchor?.Y);

        Left = at.X;
        Top = at.Y + AnchorOffset;
    }

    /// <summary>
    /// The letter in the circle.
    ///
    /// Upper-cased in Turkish, because the alternative turns "irfan" into "Irfan" — a different
    /// letter, on a badge whose whole job is being recognised at a glance.
    /// </summary>
    private static string Initial(string name)
    {
        var trimmed = name.Trim();

        return trimmed.Length == 0
            ? "?"
            : trimmed[..1].ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
    }

    /// <summary>
    /// One commitment, in the pieces the card draws.
    ///
    /// Whose promise it is used to be a prefix on the sentence — "o: faturayı gönderecek" — which
    /// reads as part of what was said. It is a chip now, and it is the first thing worth knowing
    /// about a promise.
    /// </summary>
    public sealed record CommitmentLine(bool ByMe, string Who, string Text, string? When)
    {
        private static readonly Brush Mine =
            new SolidColorBrush(Color.FromArgb(0x77, 0x2E, 0x6F, 0xF2));

        private static readonly Brush Theirs =
            new SolidColorBrush(Color.FromArgb(0x66, 0xF2, 0x8C, 0x2E));

        /// <summary>Mine and theirs are told apart by colour before either is read.</summary>
        public Brush Tint => ByMe ? Mine : Theirs;
    }

    /// <summary>
    /// One commitment as something short enough to read during a call, or null when it says
    /// nothing.
    ///
    /// The quote is left behind deliberately — it is what makes the entry checkable afterwards,
    /// and afterwards is not now.
    ///
    /// Null for an entry with no obligation text. Those exist: a fault fixed earlier left rows
    /// recording that a promise was made without recording what was promised, and on this card
    /// they came out as a bullet reading "o:" with nothing after it. An entry that cannot say
    /// what was promised is not evidence, and three of them stacked up is the panel telling
    /// somebody it knows something it does not.
    /// </summary>
    public static CommitmentLine? Line(Commitment commitment)
    {
        if (string.IsNullOrWhiteSpace(commitment.EffectiveObligation)) return null;

        // The user's own date and wording, when they gave one: the strip says what they decided,
        // not what the machine first heard.
        var when = commitment.EffectiveDeadline is { } due
            ? due.ToDateTime(TimeOnly.MinValue).ToString("d MMM")
            : commitment.DeadlineRaw is { Length: > 0 } raw ? raw : null;

        return new CommitmentLine(
            commitment.ByMe,
            commitment.ByMe ? "sen" : "o",
            commitment.EffectiveObligation.Trim(),
            when is null ? null : $"  ·  {when}");
    }
}
