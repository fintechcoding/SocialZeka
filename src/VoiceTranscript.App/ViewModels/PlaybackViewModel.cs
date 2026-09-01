using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.App.ViewModels;

/// <summary>Which of the three recordings of a call is being listened to.</summary>
public enum PlaybackChannel
{
    /// <summary>Both sides, as the conversation actually sounded. The default, and the point.</summary>
    Both,

    /// <summary>The microphone alone — only the user's voice.</summary>
    Me,

    /// <summary>The speaker alone — only the other party's voice.</summary>
    Them,
}

/// <summary>
/// The player under a transcript.
///
/// This is the mechanism the whole product rests on. Every line in the ledger is a claim about
/// what somebody said, and the only honest way to publish such a claim is to make it trivial to
/// check: click the moment, hear the words. An application that asks to be believed instead is
/// not one anybody should run on their conversations.
///
/// The drawing shows both streams mirrored around a centre line — the user above, the other
/// party below. No tool that records a call as one mixed stream can draw that, because it does
/// not know whose sound is whose at any given moment. Here it falls out of the recording design
/// for nothing.
/// </summary>
public sealed partial class PlaybackViewModel : ObservableObject, IDisposable
{
    /// <summary>Logical width the waveform is drawn at. Scaled to fit by the view.</summary>
    private const double CanvasWidth = 1000;

    /// <summary>Half-height. Each stream gets this much above or below the centre line.</summary>
    private const double CanvasHalfHeight = 34;

    /// <summary>Detail in the drawing. More than this is invisible and costs a longer read.</summary>
    private const int Buckets = 500;

    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _ticker;

    private string? _micPath;
    private string? _farPath;
    private string? _bothPath;
    private bool _disposed;

    public PlaybackViewModel()
    {
        // Twenty times a second: fast enough that the playhead looks continuous and the
        // highlighted transcript line changes exactly when the voice does.
        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        _ticker.Tick += (_, _) => Tick();
    }

    /// <summary>Raised as playback moves, so the transcript can follow along.</summary>
    public event EventHandler<int>? PositionChanged;

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _duration;

    /// <summary>
    /// Which recording is playing. All three cover the same stretch of time, so changing this
    /// keeps the moment and changes only the voice.
    ///
    /// Starts on <see cref="PlaybackChannel.Both"/>, because that is what somebody means when
    /// they say they want to hear a call. Playing one side alone is a person talking into a void
    /// with the replies removed — useful for picking a sentence out of two people talking over
    /// each other, useless for finding out how the conversation went.
    /// </summary>
    [ObservableProperty] private PlaybackChannel _channel = PlaybackChannel.Both;

    /// <summary>Kept for the transcript, which colours a line by who said it.</summary>
    public bool ListeningToMe => Channel == PlaybackChannel.Me;

    public string ChannelName => Channel switch
    {
        PlaybackChannel.Me => "Sen",
        PlaybackChannel.Them => "Karşı taraf",
        _ => "Tüm görüşme",
    };

    /// <summary>Whether the two sides could be put back together. False leaves only one voice.</summary>
    [ObservableProperty] private bool _hasMixed;

    partial void OnChannelChanged(PlaybackChannel value)
    {
        OnPropertyChanged(nameof(ListeningToMe));
        OnPropertyChanged(nameof(ChannelName));
    }

    /// <summary>The file behind the current channel, falling back to whatever exists.</summary>
    private string? CurrentPath => Channel switch
    {
        PlaybackChannel.Me => _micPath ?? _bothPath ?? _farPath,
        PlaybackChannel.Them => _farPath ?? _bothPath ?? _micPath,
        _ => _bothPath ?? _micPath ?? _farPath,
    };

    [ObservableProperty] private PointCollection? _micShape;
    [ObservableProperty] private PointCollection? _farShape;

    /// <summary>Playhead offset in the same logical units the shapes are drawn in.</summary>
    public double PlayheadX => Duration > TimeSpan.Zero
        ? CanvasWidth * Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0, 1)
        : 0;

    public string PositionText => Format(Position);

    public string DurationText => Format(Duration);

    public static double Width => CanvasWidth;

    public static double Height => CanvasHalfHeight * 2;

    partial void OnPositionChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(PlayheadX));
        OnPropertyChanged(nameof(PositionText));
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(PlayheadX));
        OnPropertyChanged(nameof(DurationText));
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>
    /// Reads both recordings and builds the drawing.
    ///
    /// Off the UI thread, because an hour of audio is over a hundred megabytes to scan and doing
    /// that on the dispatcher would freeze the window every time somebody clicked a call.
    /// </summary>
    public async Task LoadAsync(string? micPath, string? farPath, TimeSpan duration)
    {
        Stop();

        _micPath = micPath;
        _farPath = farPath;
        _bothPath = null;

        Duration = duration;
        Position = TimeSpan.Zero;
        IsLoaded = false;
        HasMixed = false;
        Channel = PlaybackChannel.Both;

        if (micPath is null && farPath is null) return;

        // The mixed copy is built here, alongside the waveform, for the same reason: an hour of
        // audio is over a hundred megabytes to walk and doing it on the dispatcher would freeze
        // the window every time somebody clicked a call. It is written once and cached beside
        // the originals, so this costs nothing from the second play onwards — including for the
        // recordings that were already on disk before the feature existed.
        //
        // A compressed recording is decoded here too, on the same worker thread: an hour of
        // Opus takes seconds to expand, and it happens once — every later read, including the
        // player's, finds the PCM copy in the cache.
        var (mic, far, both) = await Task.Run(() =>
        {
            var micPcm = AudioMaterialiser.EnsurePcm(micPath);
            var farPcm = AudioMaterialiser.EnsurePcm(farPath);

            return (
                micPcm is not null ? WaveformPeaks.Read(micPcm, Buckets) : new float[Buckets],
                farPcm is not null ? WaveformPeaks.Read(farPcm, Buckets) : new float[Buckets],
                ConversationMix.Ensure(micPcm, farPcm));
        });

        _bothPath = both;
        HasMixed = both is not null;

        MicShape = BuildShape(mic, upwards: true);
        FarShape = BuildShape(far, upwards: false);

        // A duration the caller did not know can be recovered from the recording itself, which
        // matters for a call the recorder never got to close cleanly.
        if (Duration <= TimeSpan.Zero)
            Duration = LengthOf(AudioMaterialiser.EnsurePcm(micPath)) ?? LengthOf(AudioMaterialiser.EnsurePcm(farPath)) ?? TimeSpan.Zero;

        IsLoaded = true;
    }

    /// <summary>
    /// Turns peaks into a closed outline, mirrored about the centre line.
    ///
    /// A filled polygon rather than a column of bars: at five hundred buckets across a window
    /// the bars would be under a pixel wide and alias into a grey smear, whereas an outline
    /// stays legible at any width the window happens to be.
    /// </summary>
    private static PointCollection BuildShape(float[] peaks, bool upwards)
    {
        var points = new PointCollection();
        if (peaks.Length == 0) return points;

        var step = CanvasWidth / peaks.Length;
        var sign = upwards ? -1 : 1;
        var centre = CanvasHalfHeight;

        for (var i = 0; i < peaks.Length; i++)
        {
            // A visible floor, so a quiet passage still reads as "there is audio here" rather
            // than as a gap in the recording.
            var height = Math.Max(0.6, peaks[i] * (CanvasHalfHeight - 2));
            points.Add(new Point(i * step, centre + sign * height));
        }

        // Back along the centre line to close the shape.
        for (var i = peaks.Length - 1; i >= 0; i--) points.Add(new Point(i * step, centre));

        points.Freeze();
        return points;
    }

    private static TimeSpan? LengthOf(string? path)
    {
        if (path is null || !File.Exists(path)) return null;

        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(path);
            return reader.TotalTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- transport ----------------------------------------------------------

    /// <summary>Plays from a given moment, choosing the stream that carries that speaker.</summary>
    public void PlayFrom(int startMs, bool isMe)
    {
        // Clicking a transcript line switches to that speaker's own recording on purpose: the
        // reason to click a line is to check those exact words, and the single voice is the
        // clearest way to hear them. Pressing play afterwards returns to the whole conversation.
        Channel = isMe ? PlaybackChannel.Me : PlaybackChannel.Them;

        var path = CurrentPath;
        if (path is null || !File.Exists(path)) return;

        _player.PlayFrom(path, TimeSpan.FromMilliseconds(startMs));

        IsPlaying = true;
        _ticker.Start();
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            _ticker.Stop();
            return;
        }

        var path = CurrentPath;
        if (path is null || !File.Exists(path)) return;

        _player.PlayFrom(path, Position);
        IsPlaying = true;
        _ticker.Start();
    }

    /// <summary>Jumps to a fraction of the recording. Used by clicking the waveform.</summary>
    public void SeekTo(double fraction)
    {
        if (Duration <= TimeSpan.Zero) return;

        var target = TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        Position = target;

        var path = CurrentPath;
        if (path is null || !File.Exists(path)) return;

        _player.PlayFrom(path, target);
        IsPlaying = true;
        _ticker.Start();
    }

    /// <summary>
    /// Moves to a fraction without disturbing playback. Used while a drag is in progress.
    ///
    /// Dragging cannot go through <see cref="SeekTo"/>: that reopens the file and restarts the
    /// device, and a drag produces one of those per mouse move. The result is an audible stutter
    /// and, on a long recording, a window that stops redrawing — so the position is moved on its
    /// own during the drag and the audio is repositioned once, when the button comes back up.
    /// </summary>
    public void ScrubTo(double fraction)
    {
        if (Duration <= TimeSpan.Zero) return;

        IsScrubbing = true;
        Position = TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));

        // The transcript follows the drag. Watching the lines go past is how somebody finds the
        // passage they are looking for, so it has to happen while the finger is still down.
        PositionChanged?.Invoke(this, (int)Position.TotalMilliseconds);
    }

    /// <summary>
    /// Ends a drag and moves the audio to where it was left.
    ///
    /// Whether it plays afterwards is whatever it was doing before: somebody scrubbing a paused
    /// recording is looking for a place, not asking to be talked at.
    /// </summary>
    public void EndScrub()
    {
        if (!IsScrubbing) return;

        IsScrubbing = false;

        var path = CurrentPath;
        if (path is null || !File.Exists(path)) return;

        if (IsPlaying)
        {
            _player.PlayFrom(path, Position);
            _ticker.Start();
        }
        else
        {
            // Positioned but held. Pressing play afterwards resumes from here, because
            // TogglePlay starts at Position.
            _player.Stop();
        }
    }

    /// <summary>True while the user is dragging, so the ticker does not fight them for the playhead.</summary>
    [ObservableProperty] private bool _isScrubbing;

    /// <summary>Ten seconds back. The length of a sentence somebody just missed.</summary>
    [RelayCommand]
    private void SkipBack() => Nudge(TimeSpan.FromSeconds(-10));

    [RelayCommand]
    private void SkipForward() => Nudge(TimeSpan.FromSeconds(10));

    private void Nudge(TimeSpan by)
    {
        if (Duration <= TimeSpan.Zero) return;

        var fraction = (Position + by).TotalSeconds / Duration.TotalSeconds;

        // Skipping while stopped moves the mark and leaves it stopped. Starting playback because
        // somebody wanted to look ten seconds earlier is the player deciding what they meant.
        if (IsPlaying)
        {
            SeekTo(fraction);
            return;
        }

        ScrubTo(fraction);
        EndScrub();
    }

    /// <summary>
    /// Cycles whole conversation → you → the other party, keeping the moment.
    ///
    /// A cycle rather than three buttons: this sits under a waveform in a panel that is already
    /// dense, and the choice is not one anybody makes often. The whole conversation comes first
    /// because it is the one people want, and the single sides follow for the case the separate
    /// recordings exist for — pulling one voice out of a passage where both talked at once.
    /// </summary>
    [RelayCommand]
    private void SwitchSpeaker()
    {
        var wasPlaying = IsPlaying;

        Channel = Channel switch
        {
            PlaybackChannel.Both => PlaybackChannel.Me,
            PlaybackChannel.Me => PlaybackChannel.Them,
            _ => HasMixed ? PlaybackChannel.Both : PlaybackChannel.Me,
        };

        if (wasPlaying) SeekTo(Position.TotalSeconds / Math.Max(1, Duration.TotalSeconds));
    }

    /// <summary>The single file holding the whole conversation, for export. Null if it is missing.</summary>
    public string? MixedPath => _bothPath;

    public void Stop()
    {
        _ticker.Stop();
        _player.Stop();

        IsPlaying = false;
        Position = TimeSpan.Zero;
    }

    private void Tick()
    {
        // A drag owns the playhead while it lasts. Without this the ticker writes the device's
        // position back twenty times a second and the handle springs out from under the cursor.
        if (IsScrubbing) return;

        Position = _player.Position;
        PositionChanged?.Invoke(this, (int)Position.TotalMilliseconds);

        // The player does not report having finished, so the end is detected here. Without this
        // the button would stay on "pause" over a recording that stopped minutes ago.
        if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(120))
        {
            _ticker.Stop();
            IsPlaying = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ticker.Stop();
        _player.Dispose();
    }
}
