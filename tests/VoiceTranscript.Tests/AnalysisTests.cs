using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

public class QuoteVerifierTests
{
    private static Segment Seg(bool isMe, int startMs, string text, bool lowConfidence = false) => new()
    {
        CallId = 1,
        IsMe = isMe,
        StartMs = startMs,
        EndMs = startMs + 3000,
        Text = text,
        LowConfidence = lowConfidence,
    };

    private static readonly List<Segment> Transcript =
    [
        Seg(true, 0, "Merhaba, geçen hafta konuştuğumuz sipariş için arıyorum."),
        Seg(false, 5_000, "Tabii, hatırlıyorum."),
        Seg(true, 11_500, "On iki bin diye konuşmuştuk, doğru mu?"),
        Seg(false, 17_500, "Maliyetler arttı, on sekiz bin olur ancak."),
        Seg(false, 24_000, "Evrakları cuma günü yollarım, söz."),
    ];

    [Fact]
    public void FindsAnExactQuoteAndItsTimestamp()
    {
        var found = QuoteVerifier.Locate("Evrakları cuma günü yollarım", Transcript);

        Assert.NotNull(found);
        Assert.Equal(24_000, found.StartMs);
        Assert.False(found.IsMe);
    }

    /// <summary>
    /// Whisper does not spell Turkish diacritics consistently, and neither does a model quoting
    /// it back. Presentation differences must not discard a sound finding.
    /// </summary>
    [Theory]
    [InlineData("Evraklari cuma gunu yollarim")]
    [InlineData("evrakları cuma günü yollarım")]
    [InlineData("EVRAKLARI CUMA GUNU YOLLARIM")]
    [InlineData("Evrakları cuma günü yollarım.")]
    [InlineData("  Evrakları   cuma günü yollarım  ")]
    public void ToleratesSpellingCasingAndPunctuation(string quote)
        => Assert.NotNull(QuoteVerifier.Locate(quote, Transcript));

    /// <summary>
    /// The guard that matters. A quote nobody said must be refused, because the product presents
    /// these as evidence about a real person.
    /// </summary>
    [Theory]
    [InlineData("Parayı yarın hesabına yatıracağım")]
    [InlineData("On sekiz bin kabul ediyorum")]
    [InlineData("Bu işten vazgeçtim")]
    public void RejectsAQuoteThatWasNeverSaid(string invented)
        => Assert.Null(QuoteVerifier.Locate(invented, Transcript));

    [Fact]
    public void RejectsBlankQuotes()
    {
        Assert.Null(QuoteVerifier.Locate(null, Transcript));
        Assert.Null(QuoteVerifier.Locate("   ", Transcript));
    }

    [Fact]
    public void FindsAQuoteThatRunsAcrossConsecutiveSegmentsFromOneSpeaker()
    {
        var found = QuoteVerifier.Locate("on sekiz bin olur ancak Evrakları cuma günü yollarım", Transcript);

        Assert.NotNull(found);
        Assert.Equal(17_500, found.StartMs);
        Assert.False(found.IsMe);
    }

    /// <summary>
    /// Stitching across speakers would build a sentence neither person said and attribute it to
    /// one of them — the exact fabrication this class exists to stop.
    /// </summary>
    [Fact]
    public void RefusesToStitchWordsAcrossDifferentSpeakers()
        => Assert.Null(QuoteVerifier.Locate("doğru mu Maliyetler arttı", Transcript));

    [Fact]
    public void CarriesLowConfidenceThroughSoUncertainAudioCanBeExcluded()
    {
        List<Segment> muddy = [Seg(false, 0, "on sekiz bin", lowConfidence: true)];

        var found = QuoteVerifier.Locate("on sekiz bin", muddy);

        Assert.NotNull(found);
        Assert.True(found.LowConfidence);
    }

    [Fact]
    public void FilterSeparatesVerifiedItemsFromInventedOnes()
    {
        string[] quotes =
        [
            "Evrakları cuma günü yollarım",
            "Paranın tamamını bugün yatırdım",
            "on sekiz bin olur ancak",
        ];

        var (kept, rejected) = QuoteVerifier.Filter(quotes, q => q, Transcript);

        Assert.Equal(2, kept.Count);
        Assert.Single(rejected);
        Assert.Contains("Paranın tamamını bugün yatırdım", rejected);
    }

    [Fact]
    public void FilterReportsWhereEachVerifiedQuoteWasFound()
    {
        var located = new Dictionary<string, int>();

        QuoteVerifier.Filter(
            new[] { "Evrakları cuma günü yollarım" },
            q => q,
            Transcript,
            (quote, found) => located[quote] = found.StartMs);

        Assert.Equal(24_000, located["Evrakları cuma günü yollarım"]);
    }

    [Fact]
    public void HandlesAnEmptyTranscript()
        => Assert.Null(QuoteVerifier.Locate("herhangi bir şey", []));
}

public class DeterministicChecksTests
{
    private static Commitment Promise(
        long callId, string obligation, DateOnly? deadline, bool conditional = false, string? quote = null) => new()
    {
        CallId = callId,
        ContactId = 7,
        Quote = quote ?? $"{obligation} sözü",
        QuoteStartMs = 1000,
        Obligation = obligation,
        DeadlineDate = deadline,
        IsConditional = conditional,
        Status = CommitmentStatus.Open,
    };

    private static Claim Price(long callId, decimal amount, bool lowConfidence = false, bool byMe = false) => new()
    {
        CallId = callId,
        ContactId = 7,
        ByMe = byMe,
        Quote = $"{amount} lira",
        QuoteStartMs = 2000,
        Entity = "Sipariş",
        Attribute = "Fiyat",
        Value = $"{amount}",
        NumericValue = amount,
        LowConfidence = lowConfidence,
    };

    [Fact]
    public void FlagsACommitmentWhoseDeadlineHasPassed()
    {
        var flags = DeterministicChecks
            .OverdueCommitments([Promise(1, "evrak gönderimi", new DateOnly(2026, 8, 1))], new DateOnly(2026, 8, 18))
            .ToList();

        var flag = Assert.Single(flags);
        Assert.Equal(FlagKind.OverdueCommitment, flag.Kind);
        Assert.Contains("17 gün", flag.Summary);
    }

    [Fact]
    public void DoesNotFlagACommitmentThatIsStillInTime()
        => Assert.Empty(DeterministicChecks.OverdueCommitments(
            [Promise(1, "evrak", new DateOnly(2026, 9, 1))], new DateOnly(2026, 8, 18)));

    /// <summary>
    /// "Parayı yollarsan cuma günü gönderirim" is not broken by Friday arriving. Treating it as
    /// broken is the sort of false accusation that makes the ledger worthless.
    /// </summary>
    [Fact]
    public void DoesNotFlagAConditionalPromiseAsOverdue()
        => Assert.Empty(DeterministicChecks.OverdueCommitments(
            [Promise(1, "evrak", new DateOnly(2026, 8, 1), conditional: true)], new DateOnly(2026, 8, 18)));

    /// <summary>
    /// Goes red — with a crash rather than a failed assertion — when the overdue check judges a
    /// promise by one date and then counts the days from another.
    ///
    /// This is a real defect's shape. The gate reads <c>EffectiveDeadline</c>, so a promise the
    /// conversation never dated becomes overdue as soon as the user's own postponement passes;
    /// the line counting the days read the spoken column and found nothing there. Twelve of the
    /// thirteen promises in the real archive carry no spoken date and the Sözler page offers
    /// Ertele on every one of them, so this fired on ordinary use and every later analysis of
    /// that person's conversations died.
    /// </summary>
    [Fact]
    public void APostponedPromiseWithNoSpokenDateIsCountedFromTheUsersDate()
    {
        var promise = Promise(1, "evrak gönderimi", deadline: null) with
        {
            UserDeadlineDate = new DateOnly(2026, 8, 1),
        };

        var flag = Assert.Single(DeterministicChecks.OverdueCommitments([promise], new DateOnly(2026, 8, 18)));

        Assert.Equal(FlagKind.OverdueCommitment, flag.Kind);
        Assert.Contains("17 gün", flag.Summary);
    }

    /// <summary>
    /// Goes red when the user's postponement is honoured by the gate but ignored by the count,
    /// which would report a promise as later than the user's own date says it is.
    /// </summary>
    [Fact]
    public void TheUsersPostponementIsTheDateTheDaysAreCountedFrom()
    {
        var promise = Promise(1, "evrak gönderimi", new DateOnly(2026, 8, 1)) with
        {
            UserDeadlineDate = new DateOnly(2026, 8, 15),
        };

        var flag = Assert.Single(DeterministicChecks.OverdueCommitments([promise], new DateOnly(2026, 8, 18)));

        Assert.Contains("3 gün", flag.Summary);
        Assert.DoesNotContain("17 gün", flag.Summary);
    }

    /// <summary>
    /// The other half of the rule, and the reason the count above is the only place that changed:
    /// a moved deadline is a fact about what was SAID, so the user's own postponement must never
    /// surface as a slipped promise held against the other person.
    /// </summary>
    [Fact]
    public void APostponementIsNeverHeldAgainstTheOtherPersonAsASlippedDeadline()
    {
        List<Commitment> history =
        [
            Promise(1, "evrak gönderimi", new DateOnly(2026, 8, 1), quote: "birinci söz"),
            Promise(2, "evrak gönderimi", new DateOnly(2026, 8, 1)) with
            {
                UserDeadlineDate = new DateOnly(2026, 9, 30),
            },
        ];

        Assert.Empty(DeterministicChecks.MovedDeadlines(history));
    }

    [Fact]
    public void DetectsADeadlineThatKeepsMovingAndTotalsTheSlip()
    {
        List<Commitment> history =
        [
            Promise(1, "evrak gönderimi", new DateOnly(2026, 8, 1), quote: "birinci söz"),
            Promise(2, "evrak gönderimi", new DateOnly(2026, 8, 8)),
            Promise(3, "evrak gönderimi", new DateOnly(2026, 8, 20), quote: "son söz"),
        ];

        var flag = Assert.Single(DeterministicChecks.MovedDeadlines(history));

        Assert.Equal(FlagKind.MovedDeadline, flag.Kind);
        Assert.Contains("2 kez", flag.Summary);
        Assert.Contains("19 gün", flag.Summary);
        Assert.Equal("son söz", flag.Quote);
        Assert.Equal("birinci söz", flag.CounterQuote);
    }

    [Fact]
    public void ADeadlineBroughtForwardIsNotAMovedDeadline()
    {
        List<Commitment> history =
        [
            Promise(1, "evrak", new DateOnly(2026, 8, 20)),
            Promise(2, "evrak", new DateOnly(2026, 8, 10)),
        ];

        Assert.Empty(DeterministicChecks.MovedDeadlines(history));
    }

    [Fact]
    public void DetectsAPriceThatChangedAcrossCalls()
    {
        var flag = Assert.Single(DeterministicChecks.ChangedAmounts([Price(1, 12000), Price(2, 18000)]));

        Assert.Equal(FlagKind.ChangedAmount, flag.Kind);
        Assert.Contains("arttı", flag.Summary);
        Assert.Contains("%50", flag.Summary);
        Assert.Equal(1, flag.CounterCallId);
    }

    [Fact]
    public void IgnoresRoundingNoise()
        => Assert.Empty(DeterministicChecks.ChangedAmounts([Price(1, 12000m), Price(2, 12000.50m)]));

    /// <summary>
    /// A misheard amount would otherwise become a fabricated price conflict attributed to a real
    /// person. Uncertain audio is excluded from automatic detection entirely.
    /// </summary>
    [Fact]
    public void ExcludesAmountsFromAudioTheTranscriberWasUnsureAbout()
        => Assert.Empty(DeterministicChecks.ChangedAmounts([Price(1, 12000), Price(2, 1800, lowConfidence: true)]));

    [Fact]
    public void OnlyTracksWhatTheOtherPartySaid()
        => Assert.Empty(DeterministicChecks.ChangedAmounts([Price(1, 12000, byMe: true), Price(2, 18000, byMe: true)]));

    [Fact]
    public void ContradictionCandidatesPairUpDisagreeingStatements()
    {
        var pairs = DeterministicChecks.ContradictionCandidates([Price(1, 12000), Price(2, 18000)]).ToList();

        var (earlier, later) = Assert.Single(pairs);
        Assert.Equal(12000m, earlier.NumericValue);
        Assert.Equal(18000m, later.NumericValue);
    }

    /// <summary>Someone correcting themselves mid-sentence is not a contradiction.</summary>
    [Fact]
    public void SelfCorrectionWithinTheSameCallIsNotACandidate()
    {
        List<Claim> claims =
        [
            Price(1, 12000) with { QuoteStartMs = 10_000 },
            Price(1, 18000) with { QuoteStartMs = 15_000 },
        ];

        Assert.Empty(DeterministicChecks.ContradictionCandidates(claims));
    }

    [Fact]
    public void IdenticalRestatementsAreNotCandidates()
        => Assert.Empty(DeterministicChecks.ContradictionCandidates([Price(1, 12000), Price(2, 12000)]));

    [Fact]
    public void EvasionIsReportedOnlyWhenThereIsEnoughToBeAPattern()
    {
        Assert.Null(DeterministicChecks.EvasionRate(1, 7,
            [("soru bir", 1000, true), ("soru iki", 2000, false)]));

        var flag = DeterministicChecks.EvasionRate(1, 7,
        [
            ("Sözleşme ne zaman?", 1000, true),
            ("Fiyat neden değişti?", 2000, true),
            ("Evraklar nerede?", 3000, true),
            ("Anlaştık mı?", 4000, false),
        ]);

        Assert.NotNull(flag);
        Assert.Equal(FlagKind.EvadedQuestion, flag.Kind);
        Assert.Contains("3", flag.Summary);
    }

    [Fact]
    public void NoEvasionMeansNoFlag()
        => Assert.Null(DeterministicChecks.EvasionRate(1, 7,
            [("a", 1, false), ("b", 2, false), ("c", 3, false)]));
}

public class ScamPatternsTests
{
    private static Segment Them(int startMs, string text) => new()
    {
        CallId = 1, IsMe = false, StartMs = startMs, EndMs = startMs + 2000, Text = text,
    };

    [Fact]
    public void DetectsTheFakeBankScript()
    {
        List<Segment> call =
        [
            Them(0, "Merhaba, bankamızın güvenlik birimi arıyor."),
            Them(3000, "Hesabınızdan şüpheli işlem tespit ettik."),
            Them(6000, "Paranızı güvenli hesaba aktarmamız gerekiyor."),
        ];

        var flag = Assert.Single(ScamPatterns.Scan(1, 7, call), f => f.Summary.Contains("banka"));

        Assert.Equal(FlagKind.ScamPattern, flag.Kind);
        Assert.True(flag.IsHeuristic, "a keyword rule must never be presented as a model judgement");
    }

    [Fact]
    public void DetectsTheProsecutorImpersonationScript()
    {
        List<Segment> call =
        [
            Them(0, "Savcılık makamından arıyorum."),
            Them(3000, "Hakkınızda soruşturma var, gizlilik kararı var."),
            Them(6000, "Kimseye söylemeyin."),
        ];

        Assert.Contains(ScamPatterns.Scan(1, 7, call), f => f.Kind == FlagKind.ScamPattern);
    }

    /// <summary>A single verification-code request is enough on its own.</summary>
    [Fact]
    public void AsksForAOneTimeCodeIsFlaggedImmediately()
    {
        List<Segment> call = [Them(0, "Size gelen kodu bana okur musunuz?")];

        Assert.Contains(ScamPatterns.Scan(1, 7, call), f => f.Kind == FlagKind.ScamPattern);
    }

    /// <summary>
    /// A genuine bank call talks about accounts constantly. Only pairing that with secrecy and
    /// urgency looks like the script, which is why several phrases are required.
    /// </summary>
    [Fact]
    public void AnOrdinaryBankConversationIsNotFlagged()
    {
        List<Segment> call =
        [
            Them(0, "Hesabınıza ait ekstreyi gönderdik."),
            Them(3000, "Kredi başvurunuz onaylandı, şubeye bekliyoruz."),
        ];

        Assert.Empty(ScamPatterns.Scan(1, 7, call));
    }

    /// <summary>
    /// The user warning a relative about a scam quotes the script themselves. Their own words
    /// must never flag their own call.
    /// </summary>
    [Fact]
    public void TheUserQuotingAScamDoesNotFlagTheirOwnCall()
    {
        List<Segment> call =
        [
            new() { CallId = 1, IsMe = true, StartMs = 0, EndMs = 5000,
                    Text = "Sakın kanma, hesabınız güvende değil diyip paranızı güvenli hesaba aktarın diyorlar." },
        ];

        Assert.Empty(ScamPatterns.Scan(1, 7, call));
    }

    [Fact]
    public void MatchesRegardlessOfTurkishSpelling()
    {
        List<Segment> call =
        [
            Them(0, "HESABINIZ GUVENDE DEGIL"),
            Them(2000, "PARANIZI GUVENLI HESABA AKTARIN"),
        ];

        Assert.Contains(ScamPatterns.Scan(1, 7, call), f => f.Kind == FlagKind.ScamPattern);
    }

    [Fact]
    public void AnEmptyTranscriptProducesNothing()
        => Assert.Empty(ScamPatterns.Scan(1, 7, []));
}
