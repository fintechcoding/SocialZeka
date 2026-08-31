using System.Runtime.InteropServices;
using VoiceTranscript.Core.Audio;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// Trimming the dead air out of a finished recording.
///
/// The one property everything here defends: the two streams share a clock, so silence is only
/// removable where it is silent in BOTH files at once, both files are cut identically, and every
/// stored timestamp moves through the same map the audio did. A quote that plays the wrong
/// moment after a trim would be this product breaking its own spine to save disk space.
/// </summary>
public sealed class SilenceTrimTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vt-trim-{Guid.NewGuid():N}");

    public SilenceTrimTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly AudioFormat Format = AudioFormat.WhisperPcm;

    /// <summary>A WAV of interleaved spans: (loud?, seconds). Loud is a 440-ish square wave.</summary>
    private string Wav(string name, params (bool Loud, double Seconds)[] spans)
    {
        var path = Path.Combine(_root, name);
        using var sink = new WavPcmSink(path, Format);

        foreach (var (loud, seconds) in spans)
        {
            var frames = (int)(seconds * Format.SampleRate);
            var samples = new short[frames];

            if (loud)
                for (var i = 0; i < frames; i++)
                    samples[i] = (short)(i / 18 % 2 == 0 ? 8000 : -8000);

            sink.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        }

        return path;
    }

    private static double Seconds(string path)
    {
        using var reader = PcmReader.Open(path);
        return reader.Format.FramesToDuration(reader.Frames).TotalSeconds;
    }

    // ---- planning -----------------------------------------------------------

    [Fact]
    public void JointSilenceLongerThanTheFloorIsPlanned()
    {
        // 2s speech · 10s nobody · 2s speech, in both files.
        var mic = Wav("mic.wav", (true, 2), (false, 10), (true, 2));
        var far = Wav("far.wav", (true, 2), (false, 10), (true, 2));

        var cut = Assert.Single(SilenceTrimmer.PlanCuts(mic, far));
        var (startMs, removedMs) = Assert.Single(SilenceTrimmer.ToMilliseconds([cut]));

        // The cut starts after the kept edge and removes the run minus both edges.
        Assert.Equal(2_000 + SilenceTrimmer.KeptEdgeMs, startMs);
        Assert.Equal(10_000 - 2 * SilenceTrimmer.KeptEdgeMs, removedMs);
    }

    /// <summary>
    /// One side listening is not silence. That quiet is what keeps the files on one clock.
    /// </summary>
    [Fact]
    public void SilenceOnOnlyOneSideIsNeverCut()
    {
        var mic = Wav("mic.wav", (true, 2), (false, 10), (true, 2));
        var far = Wav("far.wav", (true, 14)); // the other side talks straight through

        Assert.Empty(SilenceTrimmer.PlanCuts(mic, far));
    }

    [Fact]
    public void ShortPausesBelongToTheConversation()
    {
        // 2 seconds of nobody talking: thinking, not dead air.
        var mic = Wav("mic.wav", (true, 2), (false, 2), (true, 2));
        var far = Wav("far.wav", (true, 2), (false, 2), (true, 2));

        Assert.Empty(SilenceTrimmer.PlanCuts(mic, far));
    }

    /// <summary>A shorter file's missing tail is silence — absence of recording is silent.</summary>
    [Fact]
    public void AShorterFilesTailCountsAsSilent()
    {
        var mic = Wav("mic.wav", (true, 2), (false, 10));
        var far = Wav("far.wav", (true, 2));

        var cut = Assert.Single(SilenceTrimmer.PlanCuts(mic, far));

        Assert.True(cut.StartFrame >= 2 * Format.SampleRate);
    }

    // ---- applying -----------------------------------------------------------

    [Fact]
    public void BothFilesShrinkByTheSameAmountAndSpeechSurvivesExactly()
    {
        var mic = Wav("mic.wav", (true, 2), (false, 10), (true, 2));
        var far = Wav("far.wav", (false, 2), (false, 10), (true, 4));

        var cuts = SilenceTrimmer.PlanCuts(mic, far);
        var removedSeconds = cuts.Sum(c => c.Frames) / (double)Format.SampleRate;

        SilenceTrimmer.Apply(mic, mic + ".t", cuts);
        SilenceTrimmer.Apply(far, far + ".t", cuts);

        Assert.Equal(14 - removedSeconds, Seconds(mic + ".t"), precision: 2);
        Assert.Equal(16 - removedSeconds, Seconds(far + ".t"), precision: 2);

        // The speech at the end still ends with the same samples: nothing shifted inside it.
        using var reader = PcmReader.Open(far + ".t");
        var all = new short[reader.Frames];
        var got = 0;
        while (got < all.Length)
        {
            var n = reader.Read(all.AsSpan(got));
            if (n == 0) break;
            got += n;
        }

        Assert.NotEqual(0, all[^100]); // loud to the end, as written
    }

    [Fact]
    public void ARecordingWithNoDeadAirIsLeftByteForByteAlone()
    {
        var mic = Wav("mic.wav", (true, 6));
        var far = Wav("far.wav", (true, 6));

        Assert.Empty(SilenceTrimmer.PlanCuts(mic, far));
    }

    // ---- the map ------------------------------------------------------------

    [Fact]
    public void MomentsAfterACutSlideBackByExactlyWhatWasRemoved()
    {
        IReadOnlyList<(long, long)> cuts = [(3_000L, 5_000L)];

        Assert.Equal(1_000, SilenceTrimmer.MapMs(1_000, cuts));   // before: untouched
        Assert.Equal(3_000, SilenceTrimmer.MapMs(3_000, cuts));   // at the seam
        Assert.Equal(3_000, SilenceTrimmer.MapMs(6_000, cuts));   // inside: lands at the seam
        Assert.Equal(4_000, SilenceTrimmer.MapMs(9_000, cuts));   // after: minus the cut
    }

    [Fact]
    public void MultipleCutsAccumulate()
    {
        IReadOnlyList<(long, long)> cuts = [(2_000L, 1_000L), (10_000L, 4_000L)];

        Assert.Equal(15_000, SilenceTrimmer.MapMs(20_000, cuts));
    }

    // ---- the database side --------------------------------------------------

    [Fact]
    public void ApplyTrimMovesEveryTimestampTheCallOwnsAtOnce()
    {
        var dbPath = Path.Combine(_root, "calls.db");
        var database = new Database(dbPath);
        database.Migrate();
        var repo = new Repository(database);

        var contact = repo.UpsertContact("Uliana", CallApp.WhatsApp);

        var call = repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            ContactId = contact,
            StartedAt = DateTimeOffset.UtcNow,
            State = ProcessingState.Analysed,
            Duration = TimeSpan.FromSeconds(30),
        });

        repo.ReplaceSegments(call,
        [
            new Segment { CallId = call, StartMs = 1_000, EndMs = 2_000, Text = "kesimden önce", IsMe = true },
            new Segment { CallId = call, StartMs = 20_000, EndMs = 22_000, Text = "kesimden sonra", IsMe = false },
        ]);

        // 10 seconds of dead air removed starting at 5s.
        repo.ApplyTrim(call, [(5_000L, 10_000L)], TimeSpan.FromSeconds(20));

        var segments = repo.GetSegments(call);

        Assert.Equal(1_000, segments[0].StartMs);   // before the cut: unmoved
        Assert.Equal(10_000, segments[1].StartMs);  // after: slid back by ten seconds
        Assert.Equal(12_000, segments[1].EndMs);

        var after = repo.GetCall(call);

        Assert.Equal(TimeSpan.FromSeconds(20), after!.Duration);
        Assert.NotNull(after.TrimmedAt);

        new Database(dbPath).ClearPool();
    }
}
