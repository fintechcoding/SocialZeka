using VoiceTranscript.Core.Asr;

namespace VoiceTranscript.Tests;

/// <summary>
/// Reducing a failed job to something a person can act on.
///
/// The case this was written for is on record: a missing graphics library produced a twenty-line
/// Python traceback which the first screen of the application printed in full. The fault itself —
/// one library, installable in a click — was the last line, below the fold, indistinguishable
/// from the file paths above it. The user's reading of that screen was that the product was
/// broken, which is a fair reading of what it showed them.
/// </summary>
public class FailureTextTests
{
    /// <summary>
    /// A first line ending in a colon is a heading, not the reason.
    ///
    /// The real message a user saw was
    ///
    ///     Yapılandırılmış servislerin hiçbiri yazıya dökemedi:
    ///     OpenAI: 404: Invalid URL (POST /v1/audio/transcriptions)
    ///
    /// and the row showed only the first line — true, and useless. The sentence naming the fault
    /// is the one after it, on the screen whose entire job is saying what went wrong.
    /// </summary>
    [Fact]
    public void AHeadingCarriesTheLineThatActuallyNamesTheFault()
    {
        var summary = VoiceTranscript.Core.Asr.FailureText.Summarise(
            "Yapılandırılmış servislerin hiçbiri yazıya dökemedi:\n"
            + "OpenAI: 404: Invalid URL (POST /v1/audio/transcriptions)");

        Assert.Contains("404", summary, StringComparison.Ordinal);
        Assert.Contains("Invalid URL", summary, StringComparison.Ordinal);
    }

    /// <summary>A single sentence that happens to end in a colon must not lose itself.</summary>
    [Fact]
    public void AColonWithNothingAfterItIsStillShown()
    {
        var summary = VoiceTranscript.Core.Asr.FailureText.Summarise("Bir şey ters gitti:");

        Assert.Contains("Bir şey ters gitti", summary, StringComparison.Ordinal);
    }

    private const string CublasTraceback = """
        The worker exited with code 1.
        Traceback (most recent call last):
          File "C:\Users\x\worker\vt_worker\__main__.py", line 294, in main
            return cmd_transcribe(request)
          File "C:\Users\x\python\Lib\site-packages\faster_whisper\transcribe.py", line 1400, in encode
            return self.model.encode(features, to_cpu=to_cpu)
        RuntimeError: Library cublas64_12.dll is not found or cannot be loaded
        """;

    [Fact]
    public void ATracebackBecomesOneSentenceThatSaysWhatToDo()
    {
        var summary = FailureText.Summarise(CublasTraceback);

        Assert.DoesNotContain("Traceback", summary);
        Assert.DoesNotContain("site-packages", summary);
        Assert.Contains("cuBLAS", summary);

        // The point of the sentence is the next action, not the diagnosis.
        Assert.Contains("Kurulum", summary);
    }

    [Fact]
    public void TheTracebackIsStillWorthKeeping()
    {
        // Summarising is for display. Throwing the detail away would make a genuine bug — one
        // not on the recognised list — undiagnosable.
        Assert.True(FailureText.HasDetail(CublasTraceback));
        Assert.False(FailureText.HasDetail("Ses cihazı bulunamadı."));
    }

    [Fact]
    public void RunningOutOfVideoMemoryIsNotReportedAsAMissingLibrary()
    {
        // An out-of-memory failure also mentions CUDA. Matching the library rule first would
        // send somebody off to install something they already have.
        var summary = FailureText.Summarise(
            "RuntimeError: CUDA failed with error out of memory");

        Assert.Contains("belleği yetmedi", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cuBLAS", summary);
    }

    [Fact]
    public void AnUnrecognisedFailureFallsBackToItsOwnLastLine()
    {
        var summary = FailureText.Summarise("""
            Traceback (most recent call last):
              File "x.py", line 1, in <module>
            ValueError: beklenmeyen ses biçimi
            """);

        Assert.Equal("beklenmeyen ses biçimi", summary);
    }

    [Fact]
    public void AMessageThisApplicationWroteIsLeftAlone()
    {
        // Not everything that fails is Python. A sentence already written for a person should
        // arrive intact rather than be parsed for exception syntax it does not have.
        Assert.Equal("Etkin bir mikrofon bulunamadı.",
            FailureText.Summarise("Etkin bir mikrofon bulunamadı."));
    }

    [Fact]
    public void NothingRecordedStillProducesSomethingReadable()
    {
        Assert.Equal("Sebep kaydedilmedi.", FailureText.Summarise(null));
        Assert.Equal("Sebep kaydedilmedi.", FailureText.Summarise("   "));
    }

    [Fact]
    public void AVeryLongSingleLineIsCutAtAWord()
    {
        var summary = FailureText.Summarise(string.Join(" ", Enumerable.Repeat("kelime", 200)));

        Assert.True(summary.Length <= 201, $"{summary.Length} karakter");
        Assert.EndsWith("…", summary);

        // Cut at a space: a Turkish word broken in half reads as a typo rather than a truncation.
        Assert.DoesNotContain("kelim…", summary);
    }
    /// <summary>
    /// A dead local server is not an internet problem.
    ///
    /// A real failure read "İnternet bağlantısı kurulamadı" for a llama server at
    /// 127.0.0.1 — sending the user to check their Wi-Fi when the fix was starting a
    /// program on their own machine.
    /// </summary>
    [Fact]
    public void ALocalServerBeingDownIsNotBlamedOnTheInternet()
    {
        var summary = FailureText.Summarise(
            "LlamaServer adresine ulaşılamadı (http://127.0.0.1:8080/v1): connection refused");

        Assert.DoesNotContain("İnternet", summary);
        Assert.Contains("Yerel", summary);
    }

    [Fact]
    public void ARealRemoteConnectionFailureStillSaysInternet()
    {
        var summary = FailureText.Summarise("HttpRequestException: connection failure to api.openai.com");

        Assert.Contains("İnternet", summary);
    }

    /// <summary>
    /// An HTTP error body carried whole used to be cut at its first line, producing the
    /// memorable «OpenAi 400 döndürdü: {» — a hat with no head under it. The server's own
    /// "message" field is the sentence that matters.
    /// </summary>
    [Fact]
    public void AnHttpErrorBodyShowsItsMessageNotItsOpeningBrace()
    {
        var summary = FailureText.Summarise(
            "OpenAi 400 döndürdü: {\n  \"error\": {\n    \"message\": \"Unsupported value: " +
            "'temperature' does not support 0.2 with this model.\",\n    \"type\": " +
            "\"invalid_request_error\",\n    \"param\": \"temperature\"\n  }\n}");

        Assert.Contains("Unsupported value", summary);
        Assert.Contains("OpenAi 400 döndürdü:", summary);
        Assert.DoesNotContain("invalid_request_error", summary);
        Assert.DoesNotContain("{", summary);
    }

    /// <summary>When the heading before the body is itself multi-line noise, only the message survives.</summary>
    [Fact]
    public void ALongAggregateHeadingIsDroppedInFavourOfTheMessage()
    {
        var summary = FailureText.Summarise(
            "Yapılandırılmış servislerin hiçbiri yazıya dökemedi:\n" +
            "OpenAI: 400 (https://api.openai.com/v1/audio/transcriptions): {\n" +
            "  \"error\": { \"message\": \"response_format 'verbose_json' is not compatible " +
            "with model 'gpt-4o-mini-transcribe'. Use 'json' or 'text' instead.\" } }");

        Assert.Contains("verbose_json", summary);
        Assert.DoesNotContain("{", summary);
    }
}
