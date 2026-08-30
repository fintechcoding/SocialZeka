using VoiceTranscript.Core.Audio;

namespace VoiceTranscript.Tests;

/// <summary>
/// Exercises the recorder against the file-backed capture source, which reproduces the one
/// behaviour that breaks real recordings: a loopback stream that withholds packets whenever the
/// far end is quiet.
/// </summary>
public sealed class CallRecorderTests : IDisposable
{
    private static readonly AudioFormat Fmt = AudioFormat.WhisperPcm;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vt-rec-{Guid.NewGuid():N}");

    public CallRecorderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Writes a WAV where the given second-ranges contain speech and the rest is silent.</summary>
    private string WriteWav(string name, double totalSeconds, params (double from, double to)[] speech)
    {
        var path = Path.Combine(_dir, name);
        using var sink = new WavPcmSink(path, Fmt);

        var totalFrames = (int)(totalSeconds * Fmt.SampleRate);
        var pcm = new byte[totalFrames * Fmt.BytesPerFrame];

        foreach (var (from, to) in speech)
        {
            var start = (int)(from * Fmt.SampleRate) * Fmt.BytesPerFrame;
            var end = Math.Min(pcm.Length, (int)(to * Fmt.SampleRate) * Fmt.BytesPerFrame);

            for (var i = start; i < end; i++) pcm[i] = (byte)(i % 200 + 30);
        }

        sink.Write(pcm);
        return path;
    }

    private static (RecordingResult result, FileAudioSource source) Record(
        string dir, string micPath, string farPath, bool skipSilence = true)
    {
        var source = new FileAudioSource(micPath, farPath) { SkipSilence = skipSilence };
        using var recorder = new CallRecorder(source);

        recorder.StartAsync(dir, "call-1").GetAwaiter().GetResult();
        source.Replay();

        return (recorder.Stop(), source);
    }

    [Fact]
    public void ProducesOneFilePerSpeaker()
    {
        var mic = WriteWav("in-mic.wav", 5, (0, 2));
        var far = WriteWav("in-far.wav", 5, (2, 4));

        var (result, _) = Record(_dir, mic, far);

        Assert.NotNull(result.MicPath);
        Assert.NotNull(result.FarPath);
        Assert.True(File.Exists(result.MicPath));
        Assert.True(File.Exists(result.FarPath));
    }

    /// <summary>
    /// The regression the whole timeline layer exists for, exercised end to end. The far end
    /// speaks for two seconds of a ten-second call and the source withholds packets for the rest,
    /// exactly as a real loopback client does. Both files must still come out ten seconds long.
    /// </summary>
    [Fact]
    public void AMostlySilentFarEndStillProducesAFullLengthRecording()
    {
        var mic = WriteWav("in-mic.wav", 10, (0, 10));
        var far = WriteWav("in-far.wav", 10, (4, 6));

        var (result, _) = Record(_dir, mic, far);

        Assert.True(result.StreamsAreAligned,
            $"mic {result.MicDuration} vs far {result.FarDuration}");

        var error = (result.FarDuration - TimeSpan.FromSeconds(10)).Duration();
        Assert.True(error < TimeSpan.FromSeconds(1), $"far stream was {result.FarDuration}");
    }

    /// <summary>
    /// The failure the design prevents, demonstrated deliberately: concatenating packets instead
    /// of placing them on a timeline produces a far-end file a fraction of the real length.
    /// </summary>
    [Fact]
    public void ConcatenatingPacketsWouldLoseMostOfTheCall()
    {
        var far = WriteWav("in-far.wav", 10, (4, 6));

        using var source = new FileAudioSource(null, far) { SkipSilence = true };
        var deliveredFrames = 0L;
        source.PacketReady += (_, packet) => deliveredFrames += packet.FrameCount;
        source.Replay();

        var delivered = TimeSpan.FromSeconds((double)deliveredFrames / Fmt.SampleRate);

        // About two seconds of audio for a ten-second call: appending these in order would
        // produce a two-second file and misplace everything after the first silence.
        Assert.True(delivered < TimeSpan.FromSeconds(4), $"delivered {delivered}");
    }

    [Fact]
    public void RecordedFilesAreReadableWavAtTheWhisperFormat()
    {
        var mic = WriteWav("in-mic.wav", 3, (0, 3));
        var far = WriteWav("in-far.wav", 3, (0, 3));

        var (result, _) = Record(_dir, mic, far);

        // Round-trips through the reader the worker also uses.
        using var replay = new FileAudioSource(result.MicPath, result.FarPath) { SkipSilence = false };
        var frames = 0L;
        replay.PacketReady += (_, packet) => frames += packet.FrameCount;
        replay.Replay();

        Assert.True(frames > 0);
    }

    [Fact]
    public void StatsAreCleanForAWellBehavedRecording()
    {
        var mic = WriteWav("in-mic.wav", 4, (0, 4));
        var far = WriteWav("in-far.wav", 4, (0, 4));

        var (result, _) = Record(_dir, mic, far, skipSilence: false);

        Assert.True(result.IsClean, $"mic {result.MicStats} / far {result.FarStats}");
        Assert.Equal(0, result.FarStats.Overlaps);
    }

    [Fact]
    public void SilenceIsReportedAsFilledGapsRatherThanHiddenAway()
    {
        var mic = WriteWav("in-mic.wav", 10, (0, 10));
        var far = WriteWav("in-far.wav", 10, (4, 6));

        var (result, _) = Record(_dir, mic, far);

        Assert.True(result.FarStats.GapsFilled > 0);
        Assert.True(result.FarStats.SilenceFramesInserted > Fmt.SampleRate);
    }

    [Fact]
    public async Task StartingTwiceIsRejected()
    {
        var mic = WriteWav("in-mic.wav", 1, (0, 1));
        using var source = new FileAudioSource(mic, null);
        using var recorder = new CallRecorder(source);

        await recorder.StartAsync(_dir, "call-1", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.StartAsync(_dir, "call-2", cancellationToken: TestContext.Current.CancellationToken));
        recorder.Stop();
    }

    [Fact]
    public void StoppingWithoutStartingIsRejected()
    {
        var mic = WriteWav("in-mic.wav", 1, (0, 1));
        using var source = new FileAudioSource(mic, null);
        using var recorder = new CallRecorder(source);

        Assert.Throws<InvalidOperationException>(() => recorder.Stop());
    }

    /// <summary>A call where the far end never speaks at all must still record cleanly.</summary>
    [Fact]
    public void AOneSidedCallIsRecordedWithoutError()
    {
        var mic = WriteWav("in-mic.wav", 5, (0, 5));
        var far = WriteWav("in-far.wav", 5); // entirely silent

        var (result, _) = Record(_dir, mic, far);

        Assert.True(result.StreamsAreAligned);
        Assert.True(result.MicDuration > TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void FileSourceRejectsANonWavFile()
    {
        var bogus = Path.Combine(_dir, "bogus.wav");
        File.WriteAllBytes(bogus, new byte[100]);

        using var source = new FileAudioSource(bogus, null);

        Assert.Throws<InvalidDataException>(() => source.Replay());
    }

    [Fact]
    public void FileSourceRequiresAtLeastOneStream()
        => Assert.Throws<ArgumentException>(() => new FileAudioSource(null, null));

    /// <summary>
    /// A backend that cannot open its devices must leave nothing behind.
    ///
    /// This is the shape of a real failure that reached the user: NAudio refuses a synchronous
    /// build when the stream is asked to follow the default device, so starting threw after the
    /// WAV files had already been created. Without cleanup that leaves two empty files and a
    /// recorder that believes it is running, so the next call is rejected as well.
    /// </summary>
    [Fact]
    public async Task AFailureToStartLeavesNoStaleStateBehind()
    {
        using var backend = new FailingBackend();
        using var recorder = new CallRecorder(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.StartAsync(_dir, "call-1", cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(recorder.IsRecording);

        // The recorder must be usable again rather than stuck.
        var mic = WriteWav("in-mic.wav", 1, (0, 1));
        using var working = new FileAudioSource(mic, null);
        using var second = new CallRecorder(working);

        await second.StartAsync(_dir, "call-2", cancellationToken: TestContext.Current.CancellationToken);
        working.Replay();

        Assert.True(second.Stop().Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task StartingAFileSourceTwiceDoesNotDeliverEveryPacketTwice()
    {
        var mic = WriteWav("in-mic.wav", 2, (0, 2));
        using var source = new FileAudioSource(mic, null);

        var frames = 0L;
        source.PacketReady += (_, packet) => frames += packet.FrameCount;

        await source.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await source.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        source.Replay();

        Assert.Equal(2 * Fmt.SampleRate, frames);
    }

    private sealed class FailingBackend : IAudioCaptureBackend
    {
        public string Name => "hatalı";
        public AudioFormat Format => Fmt;
        public bool IsProcessIsolated => false;

#pragma warning disable CS0067
        public event PacketHandler? PacketReady;
        public event EventHandler<string>? Interrupted;
#pragma warning restore CS0067

        public Task StartAsync(int? targetProcessId = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ses cihazı açılamadı");

        public void Stop() { }
        public void Dispose() { }
    }

    // ---- the verdict shown after a call ------------------------------------

    private static RecordingResult ResultWith(int micPeak, int farPeak) => new(
        MicPath: "mic.wav",
        FarPath: "far.wav",
        MicDuration: TimeSpan.FromMinutes(4),
        FarDuration: TimeSpan.FromMinutes(4),
        MicStats: new TimelineStats { PeakAmplitude = micPeak },
        FarStats: new TimelineStats { PeakAmplitude = farPeak });

    [Fact]
    public void BothStreamsAudible_ReportsSuccessWithoutQualification()
    {
        var result = ResultWith(9000, 7000);

        Assert.False(result.HasSilentStream);
        Assert.Equal("Her iki taraf da kaydedildi.", result.AudioSummary);
    }

    /// <summary>
    /// The failure that is invisible otherwise. Loopback pointed at the wrong endpoint records a
    /// full-length file of digital silence, and every other indicator says the call was captured.
    /// </summary>
    [Fact]
    public void ASilentFarEnd_IsNamedAsSuchAndPointsAtTheOutputDevice()
    {
        var result = ResultWith(9000, 0);

        Assert.True(result.HasSilentStream);
        Assert.True(result.MicCarriedAudio);
        Assert.False(result.FarCarriedAudio);
        Assert.Contains("karşı taraftan hiç ses gelmedi", result.AudioSummary);
    }

    [Fact]
    public void ASilentMicrophone_IsNamedAsSuchAndPointsAtTheInputDevice()
    {
        var result = ResultWith(0, 7000);

        Assert.True(result.HasSilentStream);
        Assert.Contains("mikrofonundan hiç ses gelmedi", result.AudioSummary);
    }

    [Fact]
    public void BothStreamsSilent_SendsTheUserToTheCaptureSelfTest()
    {
        var result = ResultWith(0, 0);

        Assert.True(result.HasSilentStream);
        Assert.Contains("ses yakalama", result.AudioSummary);
    }
}
