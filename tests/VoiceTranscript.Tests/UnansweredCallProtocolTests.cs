using VoiceTranscript.Core.Asr;

namespace VoiceTranscript.Tests;

/// <summary>
/// The worker's ring-back reading, across the pipe.
///
/// The recording is deleted on the strength of this field, so a rename on either side must fail
/// loudly rather than silently read as "not a ring-out" — which is the shape a missing field
/// takes, and which would quietly turn the feature off for good.
/// </summary>
public sealed class UnansweredCallProtocolTests
{
    private const string RingOut =
        """
        {"type":"result","id":"call-97","segments":[],"audio_events":[],"duration":0.0,
         "stats":{"mic_segments":0,"far_segments":0,"overlap_segments":0,
                  "suspected_echo_segments":0,"low_confidence_segments":0,
                  "likely_no_headphones":false},
         "engine":"cloud-deepgram","language":"tr","elapsed_s":0.4,"speech_coverage":{},
         "unanswered":{"unanswered":true,
                       "far":{"bursts":10,"period_s":6.0,"jitter":0.0,"similarity":0.998},
                       "mic":{"range_db":7.0},
                       "why":"karsi kanal 10 kez ayni sesi calmis; mikrofon 7 dB oynamis"}}
        """;

    private const string OrdinaryCall =
        """
        {"type":"result","id":"call-96","segments":[],"audio_events":[],"duration":0.0,
         "stats":{"mic_segments":0,"far_segments":0,"overlap_segments":0,
                  "suspected_echo_segments":0,"low_confidence_segments":0,
                  "likely_no_headphones":false},
         "engine":"cloud-deepgram","language":"tr","elapsed_s":0.4,"speech_coverage":{}}
        """;

    [Fact]
    public void ARingOutArrivesWithTheSentenceItRestsOn()
    {
        var result = Assert.IsType<WorkerResult>(WorkerProtocol.ParseLine(RingOut));

        Assert.NotNull(result.Unanswered);
        Assert.True(result.Unanswered!.Unanswered);
        Assert.Contains("mikrofon", result.Unanswered.Why);
    }

    /// <summary>
    /// A job that never asked the question says nothing about it, and "nothing" must read as
    /// "unknown" rather than as "answered" — the caller only deletes on a positive reading.
    /// </summary>
    [Fact]
    public void AJobThatDidNotAskCarriesNoReadingAtAll()
    {
        var result = Assert.IsType<WorkerResult>(WorkerProtocol.ParseLine(OrdinaryCall));

        Assert.Null(result.Unanswered);
    }

    /// <summary>The request has to carry the flag under the name the worker reads it by.</summary>
    [Fact]
    public void TheRequestAsksForTheCheckByTheWorkersOwnName()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new TranscriptionRequest
            {
                Id = "call-1",
                ModelRef = "x",
                MicPath = "mic.wav",
                FarPath = "far.wav",
                DetectUnanswered = true,
            },
            WorkerProtocol.Json);

        Assert.Contains("\"detect_unanswered\":true", json);
    }

    [Fact]
    public void ARequestThatDidNotAskSendsFalse()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new TranscriptionRequest { Id = "call-1", ModelRef = "x" }, WorkerProtocol.Json);

        Assert.Contains("\"detect_unanswered\":false", json);
    }
}
