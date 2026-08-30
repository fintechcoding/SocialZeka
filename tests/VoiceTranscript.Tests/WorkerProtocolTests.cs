using VoiceTranscript.Core.Asr;

namespace VoiceTranscript.Tests;

/// <summary>
/// Parsed against output captured from a real worker run rather than hand-written JSON, so the
/// field names and shapes are the ones the worker actually emits.
/// </summary>
public class WorkerProtocolTests
{
    private const string RealProgressLine =
        """{"type": "progress", "id": "smoke-1", "stage": "mic", "percent": 15.5}""";

    private const string RealResultLine =
        """
        {"type": "result", "id": "smoke-1", "segments": [{"speaker": "me", "start": 0.0, "end": 5.02, "text": "Hello, good morning. I am calling about the order we discussed last week.", "avg_logprob": -0.09945354726113065, "no_speech_prob": 0.011259915307164192, "low_confidence": false, "overlaps_other_speaker": false, "suspected_echo": false, "words": [{"start": 0.0, "end": 0.38, "text": " Hello,", "p": 0.8477396965026855}, {"start": 0.8, "end": 1.08, "text": " good", "p": 0.5330720543861389}]}, {"speaker": "them", "start": 5.74, "end": 10.76, "text": "Good morning. Yes, of course, I remember the conversation.", "avg_logprob": -0.2009691269624801, "no_speech_prob": 0.025567688047885895, "low_confidence": false, "overlaps_other_speaker": false, "suspected_echo": false, "words": [{"start": 5.74, "end": 6.22, "text": " Good", "p": 0.6413108110427856}, {"start": 6.22, "end": 6.62, "text": " morning.", "p": 0.9051697254180908}]}], "duration": 54.87, "stats": {"mic_segments": 5, "far_segments": 4, "overlap_segments": 0, "suspected_echo_segments": 0, "low_confidence_segments": 0, "likely_no_headphones": false}, "engine": "faster-whisper", "model_ref": "base", "language": "en", "resegment_max_gap": 1.5, "elapsed_s": 11.56}
        """;

    private const string RealHelloLine =
        """
        {"type": "hello", "python": "3.12.0", "engines": [{"name": "faster-whisper", "available": true, "version": "1.2.1", "detail": "cuda devices: 0; missing DLLs: cublas64_12.dll"}, {"name": "whisper.cpp", "available": false, "version": null, "detail": "not installed"}], "cuda": {"available": false, "device_count": 0, "ctranslate2_version": "4.7.1", "missing_dlls": ["cublas64_12.dll"], "hint": "pip install nvidia-cublas-cu12"}}
        """;

    [Fact]
    public void ParsesProgress()
    {
        var progress = Assert.IsType<WorkerProgress>(WorkerProtocol.ParseLine(RealProgressLine));

        Assert.Equal("smoke-1", progress.Id);
        Assert.Equal("mic", progress.Stage);
        Assert.Equal(15.5, progress.Percent);
    }

    [Fact]
    public void ParsesResultIncludingSegmentsAndWords()
    {
        var result = Assert.IsType<WorkerResult>(WorkerProtocol.ParseLine(RealResultLine));

        Assert.Equal("smoke-1", result.Id);
        Assert.Equal("faster-whisper", result.Engine);
        Assert.Equal("base", result.ModelRef);
        Assert.Equal(54.87, result.Duration);
        Assert.Equal(2, result.Segments.Count);

        var first = result.Segments[0];
        Assert.True(first.IsMe);
        Assert.Equal(0.0, first.Start);
        Assert.StartsWith("Hello, good morning.", first.Text);
        Assert.Equal(2, first.Words.Count);
        Assert.Equal(" Hello,", first.Words[0].Text);
        Assert.NotNull(first.Words[0].Probability);

        Assert.False(result.Segments[1].IsMe);
    }

    /// <summary>
    /// Speaker attribution comes from which file the audio was in, so it must survive parsing
    /// exactly. Getting this wrong would silently swap who said what.
    /// </summary>
    [Fact]
    public void SpeakerLabelsSurviveParsing()
    {
        var result = (WorkerResult)WorkerProtocol.ParseLine(RealResultLine)!;

        Assert.Equal("me", result.Segments[0].Speaker);
        Assert.Equal("them", result.Segments[1].Speaker);
    }

    [Fact]
    public void ParsesStats()
    {
        var result = (WorkerResult)WorkerProtocol.ParseLine(RealResultLine)!;

        Assert.NotNull(result.Stats);
        Assert.Equal(5, result.Stats.MicSegments);
        Assert.Equal(4, result.Stats.FarSegments);
        Assert.False(result.Stats.LikelyNoHeadphones);
    }

    [Fact]
    public void ParsesCapabilityReport()
    {
        var hello = Assert.IsType<WorkerHello>(WorkerProtocol.ParseLine(RealHelloLine));

        Assert.Equal("3.12.0", hello.Python);
        Assert.Equal(2, hello.Engines.Count);
        Assert.True(hello.Engines[0].Available);
        Assert.False(hello.Engines[1].Available);

        Assert.NotNull(hello.Cuda);
        Assert.False(hello.Cuda.Available);
        Assert.Equal("4.7.1", hello.Cuda.Ctranslate2Version);
        Assert.Equal(["cublas64_12.dll"], hello.Cuda.MissingDlls);
    }

    [Fact]
    public void ParsesFailureAndClassifiesIt()
    {
        var line = """{"type":"error","id":"j1","code":"cuda_runtime","message":"cublas missing"}""";

        var failure = Assert.IsType<WorkerFailure>(WorkerProtocol.ParseLine(line));

        Assert.Equal("cuda_runtime", failure.Code);
        Assert.True(failure.IsCudaProblem);
        Assert.True(failure.CanRetryOnCpu);
    }

    /// <summary>
    /// Dependencies print warnings, and a stray line must never abort a running transcription.
    /// The huggingface_hub symlink warning showed up on the very first real run.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UserWarning: `huggingface_hub` cache-system uses symlinks by default")]
    [InlineData("{ not json at all")]
    [InlineData("""{"type":"something_new","id":"x"}""")]
    [InlineData("""{"no_type":true}""")]
    public void NonProtocolLinesAreIgnoredRatherThanThrowing(string line)
        => Assert.Null(WorkerProtocol.ParseLine(line));

    [Fact]
    public void RequestSerialisesToTheSnakeCaseNamesTheWorkerReads()
    {
        var request = new TranscriptionRequest
        {
            Id = "call-42",
            ModelRef = "large-v3-turbo",
            MicPath = @"D:\data\mic.wav",
            FarPath = @"D:\data\far.wav",
            Language = "tr",
        };

        var json = WorkerProtocol.SerialiseRequest(request);

        Assert.Contains("\"model_ref\":\"large-v3-turbo\"", json);
        Assert.Contains("\"mic_path\"", json);
        Assert.Contains("\"far_path\"", json);
        Assert.Contains("\"resegment_max_gap\":1.5", json);
        Assert.Contains("\"language\":\"tr\"", json);
    }

    [Fact]
    public void RequestDefaultsToTurkish()
        => Assert.Equal("tr", new TranscriptionRequest { Id = "x", ModelRef = "y" }.Language);

    /// <summary>
    /// Turkish text has to survive the round trip. The dotted and dotless i are exactly what a
    /// wrong console encoding destroys, which is why the worker forces UTF-8 on both ends.
    /// </summary>
    [Fact]
    public void TurkishCharactersSurviveParsing()
    {
        var line = """
            {"type":"result","id":"tr","duration":1.0,"segments":[{"speaker":"them","start":0.0,"end":1.0,"text":"Ödemeyi Cuma günü yapacağım, ışıklar açık kalsın.","words":[]}]}
            """;

        var result = (WorkerResult)WorkerProtocol.ParseLine(line)!;

        Assert.Equal("Ödemeyi Cuma günü yapacağım, ışıklar açık kalsın.", result.Segments[0].Text);
    }
}
