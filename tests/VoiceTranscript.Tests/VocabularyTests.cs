using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The vocabulary the recogniser is sent has to come from the archive, not from a list nobody
/// will maintain — and it has to be the names, not every capital letter.
/// </summary>
public sealed class VocabularyTests
{
    [Fact]
    public void RecurringMidSentenceNamesAreMined()
    {
        var texts = new[]
        {
            "Dün Uliana ile Sumsub'a bakacağız dedik.",
            "Sumsub tarafı KYC için bir daha aradı.",
            "Bugün Uliana aramadı, ama Sumsub onboarding'i sordu ve KYC belgelerini istedi.",
        };

        var mined = VocabularyMiner.Mine(texts);

        Assert.Contains("Sumsub", mined);
        Assert.Contains("Uliana", mined);
        Assert.Contains("KYC", mined);
    }

    [Fact]
    public void SentenceStartsAndOneOffsAreNotNames()
    {
        var texts = new[]
        {
            "Bugün hava güzel. Yarın da güzel olacak.",
            "Bugün geldi. Ahmet bir kez geçti.",
        };

        var mined = VocabularyMiner.Mine(texts);

        // "Bugün" and "Yarın" only ever start a sentence; "Ahmet" appears once, after a stop.
        Assert.DoesNotContain("Bugün", mined);
        Assert.DoesNotContain("Yarın", mined);
        Assert.DoesNotContain("Ahmet", mined);
    }

    [Fact]
    public void TheMostFrequentComeFirstAndTheListIsCapped()
    {
        var texts = Enumerable.Range(0, 5).Select(_ => "biz Sumsub ve Uliana ile Sumsub için konuştuk").ToList();
        texts.Add("bir kere Zeta dedi, sonra yine Zeta dedi");

        var mined = VocabularyMiner.Mine(texts, max: 2);

        Assert.Equal(["Sumsub", "Uliana"], mined);
    }

    [Fact]
    public void ComposeKeepsTheTypedTermsFirstAndDropsDuplicates()
    {
        var vocabulary = Vocabulary.Compose(
            manual: ["Sumsub", "KYC"],
            names: ["Uliana", "sumsub"],
            mined: ["KYC", "Zeta"]);

        Assert.Equal("Sumsub, KYC, Uliana, Zeta", vocabulary.Terms);
        Assert.Equal("Sumsub, KYC, Uliana, Zeta.", vocabulary.Prompt);
    }

    [Fact]
    public void ThePromptIsShortEvenWhenTheTermListIsLong()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"Isim{i}").ToList();

        var vocabulary = Vocabulary.Compose(many);

        Assert.Equal(Vocabulary.MaxTerms, vocabulary.Terms!.Split(", ").Length);
        Assert.Equal(Vocabulary.PromptTerms, vocabulary.Prompt!.TrimEnd('.').Split(", ").Length);
    }

    [Fact]
    public void NothingKnownMeansNothingSent()
    {
        Assert.Same(Vocabulary.Empty, Vocabulary.Compose(null));
        Assert.Null(Vocabulary.Compose([""], [" "]).Terms);
    }
}
