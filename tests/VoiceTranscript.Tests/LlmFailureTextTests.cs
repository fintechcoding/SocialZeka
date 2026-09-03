using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.Tests;

/// <summary>
/// Turning a failed request into a sentence the reader can act on.
///
/// The reader has to decide one thing when analysis stops: wait, or fix something. On the wire
/// "model overloaded" and "insufficient quota" are the same shape — a status and some JSON — and
/// they are opposite answers. Getting that wrong costs an evening spent replacing a key that was
/// never wrong.
///
/// These assert the decision, not the wording: what matters is which of the two the sentence
/// tells somebody to do.
/// </summary>
public class LlmFailureTextTests
{
    private const LlmProviderKind Any = LlmProviderKind.OpenAi;

    [Theory]
    [InlineData(429, "")]
    [InlineData(503, "")]
    [InlineData(500, "{\"error\":{\"message\":\"model is overloaded\"}}")]
    [InlineData(400, "{\"error\":{\"message\":\"Rate limit reached\"}}")]
    public void BeingBusyAsksForNothing(int status, string body)
    {
        var text = LlmFailureText.Describe(Any, status, body);

        Assert.Contains("yoğun", text);
        Assert.DoesNotContain("anahtar", text);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void ARejectedKeySaysWhereToLook(int status)
    {
        var text = LlmFailureText.Describe(Any, status, "");

        Assert.Contains("anahtar", text);
        Assert.Contains("Ayarlar", text);
    }

    [Theory]
    [InlineData(402, "")]
    [InlineData(429, "{\"error\":{\"message\":\"You exceeded your current quota\",\"type\":\"insufficient_quota\"}}")]
    public void RunningOutOfMoneySaysTheKeyIsFine(int status, string body)
    {
        var text = LlmFailureText.Describe(Any, status, body);

        // The whole point: a 429 that is really a billing problem must not be read as "busy",
        // because waiting for a quota to refill on its own is waiting forever.
        Assert.Contains("Anahtar doğru", text);
        Assert.DoesNotContain("yoğun", text);
    }

    [Fact]
    public void AModelThatNoLongerExistsSaysSo()
    {
        var text = LlmFailureText.Describe(
            Any, 404, "{\"error\":{\"message\":\"The model `gpt-4-old` does not exist\"}}");

        Assert.Contains("model", text);
        Assert.Contains("Ayarlar", text);
    }

    [Fact]
    public void ATranscriptTooLongSaysWhatToDoAboutIt()
    {
        var text = LlmFailureText.Describe(
            Any, 400, "{\"error\":{\"message\":\"maximum context length is 8192 tokens\"}}");

        Assert.Contains("uzun", text);
    }

    [Fact]
    public void AnUnrecognisedFailureKeepsTheProvidersOwnSentence()
    {
        var text = LlmFailureText.Describe(Any, 418, "{\"error\":{\"message\":\"Tuhaf bir şey oldu\"}}");

        Assert.Contains("418", text);
        Assert.Contains("Tuhaf bir şey oldu", text);
    }

    [Fact]
    public void AnEmptyBodyStillProducesASentence()
    {
        var text = LlmFailureText.Describe(Any, 418, null);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("418", text);
    }

    [Fact]
    public void ANoveldSizedBodyDoesNotBecomeTheMessage()
    {
        var text = LlmFailureText.Describe(Any, 418, "{\"error\":{\"message\":\"" + new string('x', 5000) + "\"}}");

        Assert.True(text.Length < 400, $"tek cümle olmalı, {text.Length} karakter geldi");
    }
}
