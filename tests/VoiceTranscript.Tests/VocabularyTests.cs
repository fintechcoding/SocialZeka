using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The vocabulary is the list the user typed, and it reaches the recogniser as a weighting.
///
/// It used to be more than that: contact names and proper nouns mined out of the archive were
/// merged in, and the result was sent both as hotwords and as the decoder's initial prompt. That
/// combination broke transcription outright for two days, and these tests exist to hold the two
/// halves of the fix — no prompt, no mining — rather than to describe a feature.
/// </summary>
public sealed class VocabularyTests
{
    [Fact]
    public void TheTypedTermsAreCleanedAndDeduplicated()
    {
        var vocabulary = Vocabulary.Compose(["Sumsub", "KYC,", " Uliana ", "sumsub", "x", ""]);

        // "x" is one character and "sumsub" is the same word again; both go.
        Assert.Equal("Sumsub, KYC, Uliana", vocabulary.Terms);
    }

    [Fact]
    public void NothingTypedMeansNothingSent()
    {
        Assert.Same(Vocabulary.Empty, Vocabulary.Compose(null));
        Assert.Null(Vocabulary.Compose(["", " ", "x"]).Terms);
    }

    [Fact]
    public void TheListIsCapped()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"Isim{i}");

        Assert.Equal(Vocabulary.MaxTerms, Vocabulary.Compose(many).Terms!.Split(", ").Length);
    }

    /// <summary>
    /// The fault, guarded at the type level.
    ///
    /// Hotwords weights a decoding window and a wrong term simply never wins. A prompt is text the
    /// decoder is told it has already written, so it continues the *style* of it — and a
    /// comma-separated list of capitalised terms is a style. Measured on one real recording, the
    /// same 180 seconds through the same service with and without: with it, "Yani, Uzun, Bir,
    /// Süre, Tabii, İşin, Yücün, Rast gelsin, Yapıyor, Bunu, Ama, Sonuçta..."; without it, "Bu
    /// paraları senin ödemen gerekiyordu. O kendisi üstleniyor. Neden?"
    ///
    /// So <see cref="Vocabulary"/> carries one field. If a second one appears here that is handed
    /// to a decoder as context, this test is the place that should have stopped it.
    /// </summary>
    [Fact]
    public void TheVocabularyHasNoWayToBecomeDecoderContext()
    {
        var properties = typeof(Vocabulary)
            .GetProperties()
            .Select(p => p.Name)
            .Where(name => name != "EqualityContract")
            .ToArray();

        Assert.Equal(["Terms"], properties);
    }

    /// <summary>
    /// The other half: the archive can no longer put words into the recogniser's mouth.
    ///
    /// The miner read names out of transcripts by looking for capitalised words mid-sentence,
    /// which works on clean output and collects the whole language on output that capitalises
    /// mid-sentence at random. Two days of it had gathered 230 "names" led by "Yani", "Ben",
    /// "Tamam", "Ama", "Evet". Those went back into the prompt and produced more of themselves.
    /// </summary>
    [Fact]
    public void NothingIsCollectedFromTheArchive()
    {
        Assert.Null(typeof(Vocabulary).Assembly.GetType("VoiceTranscript.Core.Text.VocabularyMiner"));
    }
}
