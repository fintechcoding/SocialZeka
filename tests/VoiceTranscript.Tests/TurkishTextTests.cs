using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

public class TurkishTextTests
{
    /// <summary>
    /// The failure the whole normalisation layer exists to prevent. With FTS5 default folding
    /// these three spellings do not find each other, and the search returns nothing at all
    /// rather than reporting an error.
    /// </summary>
    [Theory]
    [InlineData("IŞIK")]
    [InlineData("ışık")]
    [InlineData("Işık")]
    [InlineData("isik")]
    [InlineData("ISIK")]
    public void AllSpellingsOfIsik_FoldToTheSameKey(string spelling)
        => Assert.Equal("isik", TurkishText.NormalizeForSearch(spelling));

    [Theory]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("istanbul", "istanbul")]
    [InlineData("Çağrı", "cagri")]
    [InlineData("ÇAĞRI", "cagri")]
    [InlineData("Gökhan", "gokhan")]
    [InlineData("ŞÜKRÜ", "sukru")]
    [InlineData("Ödeme", "odeme")]
    public void TurkishLetters_FoldOntoAsciiBase(string input, string expected)
        => Assert.Equal(expected, TurkishText.NormalizeForSearch(input));

    [Fact]
    public void DecomposedInput_MatchesPrecomposedInput()
    {
        var precomposed = "Gökhan";
        var decomposed = "Gökhan"; // o + COMBINING DIAERESIS

        Assert.NotEqual(precomposed, decomposed);
        Assert.Equal(
            TurkishText.NormalizeForSearch(precomposed),
            TurkishText.NormalizeForSearch(decomposed));
    }

    /// <summary>
    /// Telegram window titles arrive with a leading LEFT-TO-RIGHT MARK and users routinely set
    /// styled-Unicode display names. Both must be cleaned before the value becomes a contact key.
    /// </summary>
    [Fact]
    public void StripFormatting_RemovesBidiMarks()
    {
        var raw = "‎Ahmet Yılmaz‏";
        Assert.Equal("Ahmet Yılmaz", TurkishText.StripFormatting(raw));
    }

    [Fact]
    public void StripFormatting_FoldsStyledUnicodeOntoPlainLetters()
    {
        // MATHEMATICAL BOLD SCRIPT letters, as seen in real Telegram display names.
        var styled = "\U0001D4E2\U0001D4EE\U0001D4FB"; // script S, e, r
        var cleaned = TurkishText.StripFormatting(styled);

        Assert.Equal("Ser", cleaned);
    }

    [Fact]
    public void StripFormatting_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, TurkishText.StripFormatting(null));
        Assert.Equal(string.Empty, TurkishText.StripFormatting(""));
    }

    [Fact]
    public void ToLowerTr_UsesTurkishRules_NotInvariant()
    {
        Assert.Equal("ışık", TurkishText.ToLowerTr("IŞIK"));
        Assert.Equal("istanbul", TurkishText.ToLowerTr("İSTANBUL"));

        // Guard against a future refactor quietly swapping in invariant casing.
        Assert.NotEqual("IŞIK".ToLowerInvariant(), TurkishText.ToLowerTr("IŞIK"));
    }

    [Fact]
    public void ToUpperTr_UsesTurkishRules_NotInvariant()
    {
        Assert.Equal("IŞIK", TurkishText.ToUpperTr("ışık"));
        Assert.Equal("İSTANBUL", TurkishText.ToUpperTr("istanbul"));
    }

    /// <summary>
    /// Turkish is agglutinative, so an exact-token search is close to useless: the word the user
    /// remembers almost never appears in the exact form they type.
    /// </summary>
    [Fact]
    public void ToMatchQuery_EmitsPrefixTermsSoSuffixedFormsAreFound()
    {
        Assert.Equal("kitap*", TurkishText.ToMatchQuery("kitap"));
        Assert.Equal("odeme* gecikti*", TurkishText.ToMatchQuery("Ödeme gecikti").Replace(" AND ", " "));
    }

    [Fact]
    public void ToMatchQuery_JoinsTermsWithAnd()
        => Assert.Equal("fatura* AND tutari*", TurkishText.ToMatchQuery("fatura tutarı"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ToMatchQuery_ReturnsEmptyForBlankInput(string? input)
        => Assert.Equal(string.Empty, TurkishText.ToMatchQuery(input));

    /// <summary>
    /// A transcript line is untrusted text. Quotes and FTS5 operators inside it must never be
    /// able to produce a malformed MATCH expression.
    /// </summary>
    [Theory]
    [InlineData("\"quoted\"", "quoted*")]
    [InlineData("a OR b", "a* AND or* AND b*")]
    [InlineData("foo* AND bar", "foo* AND and* AND bar*")]
    [InlineData("50% -zam", "50* AND zam*")]
    public void ToMatchQuery_StripsOperatorsAndPunctuation(string input, string expected)
        => Assert.Equal(expected, TurkishText.ToMatchQuery(input));
}
