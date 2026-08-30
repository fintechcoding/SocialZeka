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
}
