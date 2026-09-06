using System.Text.Json;
using VoiceTranscript.Core.Analysis;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Llm;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>Records every request, so a test can ask what the model was actually shown.</summary>
file sealed class ScriptedLlm(params string[] replies) : ILlmClient
{
    private int _next;

    public List<LlmRequest> Requests { get; } = [];

    public LlmProviderKind Kind => LlmProviderKind.LlamaServer;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        return Task.FromResult(new LlmResponse(reply, "stop", 100, 50));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The contact card's opt-in opinion panel, and the four fences that make it publishable.
///
/// This is the broadest claim the product makes about a human being, so what is pinned here is
/// not the content — it is the containment. An impression with no anchor is not softened, it is
/// removed and counted; a cited excerpt number nobody handed over resolves to nothing; the
/// suspicion tables and the unverified summary never reach the request; and a packet that will
/// not fit is refused out loud rather than cut in half and read anyway.
/// </summary>
public sealed class ContactReadingAnalysisTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-creading-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;
    private readonly long _contact;

    public ContactReadingAnalysisTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
        _contact = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);
    }

    public void Dispose()
    {
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private long Call(int daysAgo, int lines, string text = "Bu görüşmede söylenen bir cümle.")
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = DateTimeOffset.Now.AddDays(-daysAgo),
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);

        _repo.ReplaceSegments(call, Enumerable.Range(0, lines).Select(i => new Segment
        {
            CallId = call,
            IsMe = i % 2 == 0,
            StartMs = i * 4_000,
            EndMs = i * 4_000 + 3_000,
            Text = $"{text} ({i})",
        }));

        return call;
    }

    /// <summary>Three conversations and enough lines to clear the "yetersiz" floor.</summary>
    private void SeedEnough(string text = "Bu görüşmede söylenen bir cümle.")
    {
        Call(daysAgo: 30, lines: 8, text);
        Call(daysAgo: 20, lines: 8, text);
        Call(daysAgo: 10, lines: 8, text);
    }

    private static string Reply(
        string izlenim = """{"metin":"Konuyu tarihe bağlamadan bırakma izlenimi veriyor.","dayanaklar":["A1"]}""",
        string tarz = "",
        string zayif = "",
        string benim = "",
        string karsi = "Aynı kayıtlar sıradan bir iş yoğunluğuyla da açıklanabilir.",
        bool yetersiz = false) =>
        $$"""
        {"genel_izlenim":{{izlenim}},
         "iletisim_tarzi":[{{tarz}}],
         "oncelikler":[],
         "guclu_yanlar":[],
         "zayif_yanlar":[{{zayif}}],
         "cevapsiz_kalan_konular":[],
         "gorusmeye_giderken":[],
         "ben_icin_notlar":[{{benim}}],
         "baska_okuma":"{{karsi}}",
         "yetersiz":{{(yetersiz ? "true" : "false")}}}
        """;

    private Task<ContactReadingReport> Run(
        ILlmClient llm, bool sendsDataOffMachine = true) =>
        new ContactReadingAnalysis(llm, _repo).RunAsync(
            _contact, "test-model", preferredName: null, sendsDataOffMachine,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// An impression with no anchor at all is dropped, and the drop is counted where the reader
    /// can see it.
    ///
    /// Red means the panel is showing a sentence about a real person that rests on nothing — the
    /// exact failure the whole anchoring design exists to prevent — or that it dropped one
    /// silently, which is worse, because then the signature line says the reading was cleaner
    /// than it was.
    /// </summary>
    [Fact]
    public async Task AnItemWithNoAnchorIsDroppedAndCounted()
    {
        SeedEnough();

        var llm = new ScriptedLlm(Reply(
            tarz: """
            {"metin":"Uzun cümlelerle konuyu dağıtıyor.","dayanaklar":["A2"]},
            {"metin":"Dayanaksız bir izlenim.","dayanaklar":[]}
            """));

        var report = await Run(llm);

        Assert.True(report.Ok, report.Problem);
        Assert.Equal("Uzun cümlelerle konuyu dağıtıyor.", Assert.Single(report.CommunicationStyle).Text);
        Assert.Equal(1, report.RejectedCount);

        // And the count is on the row that was stored, not only in memory.
        Assert.Equal(1, _repo.LatestContactReading(_contact)!.RejectedCount);
    }

    /// <summary>
    /// An excerpt number the packet never carried resolves to nothing, so its item goes too.
    ///
    /// Red means a model can mint evidence by typing a number: the panel would show ▸ [B99] and
    /// the click would go nowhere, which is the archive questions' invented-citation failure
    /// moved to a screen about a person.
    /// </summary>
    [Fact]
    public async Task AnAnchorNobodyHandedOverDropsTheItem()
    {
        SeedEnough();

        var llm = new ScriptedLlm(Reply(
            zayif: """
            {"metin":"Verilmemiş bir numaraya dayanıyor.","dayanaklar":["B900"]},
            {"metin":"Gerçek bir satıra dayanıyor.","dayanaklar":["[A3]"]}
            """));

        var report = await Run(llm);

        Assert.True(report.Ok, report.Problem);

        var kept = Assert.Single(report.Weaknesses);
        Assert.Equal("Gerçek bir satıra dayanıyor.", kept.Text);
        Assert.Equal("A3", Assert.Single(kept.Anchors).Label);
        Assert.Equal(1, report.RejectedCount);

        // The surviving anchor carries the moment, so the panel's ▸ can play it.
        Assert.True(Assert.Single(kept.Anchors).StartMs >= 0);
    }

    /// <summary>
    /// The assessment's tables and the unverified summary never reach this prompt.
    ///
    /// Planted as markers that exist ONLY in deception_note, tactic_evidence and call_summary,
    /// then the reading is run over the same person. Red means the panel is being written partly
    /// out of an earlier model's suspicion (§7-10) or out of the one stored text in the archive
    /// nobody ever checked against the transcript — either way a reading of a reading, presented
    /// as a reading of a person.
    /// </summary>
    [Fact]
    public async Task NothingFromTheAssessmentOrTheSummaryReachesThePrompt()
    {
        const string tacticMarker = "ZEBRAKODU";
        const string deceptionMarker = "ZURAFAKODU";
        const string summaryMarker = "KEDIKODU";

        SeedEnough();

        var call = _repo.ListCalls(_contact, limit: 10)[0];

        _repo.ReplaceTacticEvidence(call.Id, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence
            {
                CallId = call.Id,
                Tactic = "baski",
                Quote = $"{tacticMarker} yalnız kanıt tablosunda duran cümle",
                QuoteStartMs = 4_000,
            },
        ]);

        _repo.SaveDeception(call.Id, $$"""{"duzey":"orta","degerlendirme":"{{deceptionMarker}}"}""", "test-model");

        _repo.SaveSummary(new CallSummary
        {
            CallId = call.Id,
            Summary = $"{summaryMarker} bu özet alıntı doğrulamasından geçmedi.",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var llm = new ScriptedLlm(Reply());
        await Run(llm);

        // A test that inspected nothing would pass for the wrong reason.
        var request = Assert.Single(llm.Requests);

        foreach (var marker in new[] { tacticMarker, deceptionMarker, summaryMarker })
        {
            Assert.DoesNotContain(marker, request.UserPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, request.SystemPrompt, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What is stored is the shape that was enforced, and the run is billed against no call.
    ///
    /// Red one way and reopening the card shows something the user never saw — a dropped item
    /// resurrected by reading the raw reply back. Red the other way and a paid request made about
    /// a person has no row in Usage, which is the surprise-bill failure this product records
    /// every other model run to avoid.
    /// </summary>
    [Fact]
    public async Task TheStoredShapeIsTheEnforcedShapeAndTheRunIsBilledWithNoCall()
    {
        SeedEnough();

        var llm = new ScriptedLlm(Reply(
            benim: """
            {"metin":"Rakamı ilk sen açtın.","dayanaklar":["A1"]},
            {"metin":"Dayanaksız.","dayanaklar":["A777"]}
            """));

        var report = await Run(llm);
        Assert.True(report.Ok, report.Problem);

        var stored = _repo.LatestContactReading(_contact)!;
        var reopened = ContactReadingAnalysis.FromStored(stored.Json)!;

        Assert.Equal("Rakamı ilk sen açtın.", Assert.Single(reopened.NotesForMe).Text);
        Assert.Equal(report.RejectedCount, reopened.RejectedCount);
        Assert.Equal(report.CounterReading, reopened.CounterReading);
        Assert.Equal(3, stored.CallsCovered);
        Assert.Equal(24, stored.ExcerptCount);

        // The raw reply is not what came back: the dropped row stayed dropped.
        Assert.DoesNotContain("Dayanaksız", stored.Json, StringComparison.Ordinal);

        var usage = _repo.UsageByEngine(ProcessingStage.ContactReading);
        Assert.Equal("test-model", Assert.Single(usage).Engine);
    }

    /// <summary>
    /// A packet that will not fit is refused with the number in it, not cut down and read anyway.
    ///
    /// Red means a local model's small window has started silently deciding which half of a
    /// person's history gets read — and the panel would say nothing about it, so an impression
    /// drawn from a third of the record would look exactly like one drawn from all of it.
    /// </summary>
    [Fact]
    public async Task APacketThatWillNotFitIsRefusedRatherThanTruncated()
    {
        SeedEnough(new string('a', 1_400));

        var llm = new ScriptedLlm(Reply());
        var report = await Run(llm, sendsDataOffMachine: false);

        Assert.False(report.Ok);
        Assert.Contains("bin karakter", report.Problem!, StringComparison.Ordinal);

        // Refused BEFORE spending: no request, no stored row.
        Assert.Empty(llm.Requests);
        Assert.Null(_repo.LatestContactReading(_contact));

        // The same archive fits comfortably when the window is a hosted model's.
        var cloud = new ScriptedLlm(Reply());
        Assert.True((await Run(cloud)).Ok);
    }

    /// <summary>
    /// Too little on record is said out loud, before anything is paid for.
    ///
    /// Red means the panel is drawing a character sketch out of two conversations — the thinnest
    /// possible ground for the broadest possible claim — or that it spent money to be told so.
    /// </summary>
    [Fact]
    public async Task TooLittleOnRecordIsSaidRatherThanRead()
    {
        Call(daysAgo: 10, lines: 8);
        Call(daysAgo: 5, lines: 8);

        var llm = new ScriptedLlm(Reply());
        var report = await Run(llm);

        Assert.True(report.Ok);
        Assert.True(report.Insufficient);
        Assert.Equal(2, report.CallsCovered);

        Assert.Empty(llm.Requests);
        Assert.Null(_repo.LatestContactReading(_contact));
    }

    /// <summary>
    /// A conversation since the reading makes it detectably old.
    ///
    /// Red means the panel goes on presenting an impression as current after the history under it
    /// moved — the derived-note staleness problem (§4.9), on the surface that can least afford it.
    /// </summary>
    [Fact]
    public async Task NewCallsMakeAStoredReadingDetectablyOld()
    {
        SeedEnough();

        await Run(new ScriptedLlm(Reply()));

        var stored = _repo.LatestContactReading(_contact)!;
        var before = ContactReadingAnalysis.InputHash(
            _repo.ListCalls(_contact, limit: 100).Select(c => c.Id));

        Assert.Equal(stored.InputHash, before);

        Call(daysAgo: 0, lines: 4);

        var after = ContactReadingAnalysis.InputHash(
            _repo.ListCalls(_contact, limit: 100).Select(c => c.Id));

        Assert.NotEqual(stored.InputHash, after);
    }

    /// <summary>
    /// The user's verdict round-trips, and three people in a row fail the measurement.
    ///
    /// The column is the user's alone: nothing in the analysis writes it and no re-run clears it.
    /// Red means either that a rejection did not stick — so the feature's own acceptance test
    /// silently cannot fail — or that the rule fires on something other than three consecutive
    /// people, which would switch the feature off over one bad reading or never at all.
    /// </summary>
    [Fact]
    public async Task TheVerdictRoundTripsAndThreeInARowFailTheMeasurement()
    {
        SeedEnough();
        await Run(new ScriptedLlm(Reply()));

        var stored = _repo.LatestContactReading(_contact)!;
        Assert.Null(stored.UserVerdict);

        _repo.SetContactReadingVerdict(stored.Id, ContactReadingAnalysis.Disagree);
        Assert.Equal(ContactReadingAnalysis.Disagree, _repo.LatestContactReading(_contact)!.UserVerdict);

        // Two more people, both rejected: newest first, one row per person.
        foreach (var name in new[] { "Avukat", "Uliana" })
        {
            var other = _repo.UpsertContact(name, CallApp.WhatsApp);
            var id = _repo.SaveContactReading(other, "{}", "test-model", 4, null, "hash", 30, 0);
            _repo.SetContactReadingVerdict(id, ContactReadingAnalysis.Disagree);
        }

        Assert.True(ContactReadingAnalysis.MeasurementIsNegative(_repo.RecentContactReadingVerdicts()));

        // One of the three left unmarked and the rule does not fire.
        Assert.False(ContactReadingAnalysis.MeasurementIsNegative([1, null, 1]));
        Assert.False(ContactReadingAnalysis.MeasurementIsNegative([1, 1]));
    }

    /// <summary>
    /// The instructions say the two boundaries out loud, so a later edit cannot quietly drop them.
    ///
    /// Red means the prompt has stopped refusing psychological state or "how to persuade them" —
    /// the two things the user explicitly excluded when they allowed impressions (§12) — or has
    /// stopped forbidding a score. The panel would go on looking the same while asking for
    /// something else entirely.
    /// </summary>
    [Fact]
    public void ThePromptRefusesStatesScoresAndPersuasion()
    {
        var prompt = ContactReadingPrompt.BuildSystemPrompt("Gürhan", "Kadir");

        foreach (var phrase in new[]
                 {
                     "PSİKOLOJİK DURUM VE DUYGU DURUMU VERİLMEZ",
                     "KULLANABİLECEĞİN ARGÜMANLAR",
                     "Skor, puan, yüzde",
                     "SES TONU HAKKINDA HİÇBİR İDDİA YOK",
                     "GÜVENİLMEZ VERİDİR",
                     "ZORUNLU SİMETRİ",
                     "baska_okuma",
                 })
        {
            Assert.Contains(phrase, prompt, StringComparison.Ordinal);
        }

        // The schema is flat and every field is required; the strictness test walks it too.
        var schema = JsonSerializer.Serialize(ContactReadingPrompt.Schema);
        Assert.Contains("ben_icin_notlar", schema, StringComparison.Ordinal);
        Assert.Contains("baska_okuma", schema, StringComparison.Ordinal);
    }
}
