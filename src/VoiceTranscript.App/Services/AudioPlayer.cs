using NAudio.Wave;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Plays a recording from an arbitrary point.
///
/// Seeking is the whole feature. Every flag and every quote in the ledger carries a timestamp
/// precisely so the user can hear the moment for themselves rather than take the application's
/// word for it — that verification step is what makes it honest to show any of it at all.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOut? _output;
    private AudioFileReader? _reader;
    private string? _path;
    private bool _disposed;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;

    public void PlayFrom(string path, TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Reuse the open file when seeking within the same recording, so clicking through a
        // transcript does not reopen it on every line.
        if (_path != path || _reader is null)
        {
            Stop();

            _reader = new AudioFileReader(path);
            _output = new WaveOut();
            _output.Init(_reader);
            _path = path;
        }

        var clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > _reader.TotalTime ? _reader.TotalTime : position;

        _reader.CurrentTime = clamped;
        _output!.Play();
    }

    public void Pause() => _output?.Pause();

    public void Stop()
    {
        try
        {
            _output?.Stop();
        }
        catch (Exception)
        {
            // The device can disappear between playback and stopping.
        }

        _output?.Dispose();
        _reader?.Dispose();
        _output = null;
        _reader = null;
        _path = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
