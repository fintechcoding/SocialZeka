using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;

    public RepositoryTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
    }

    public void Dispose()
    {
        // Scoped to this test’s own database. ClearAllPools would dispose pooled handles
        // belonging to every other test class running in parallel, which is a real and
        // measured source of ObjectDisposedException in unrelated tests.
        new Database(_path).ClearPool();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private long NewCall(long? contactId = null, DateTimeOffset? at = null) => _repo.InsertCall(new Call
    {
        ContactId = contactId,
        App = CallApp.Telegram,
        StartedAt = at ?? DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMinutes(3),
        State = ProcessingState.Transcribed,
    });

    /// <summary>
    /// FTS5 is only present in some SQLitePCLRaw bundles. When it is missing the schema fails
    /// with a confusing "no such module" that points nowhere near the real cause.
    /// </summary>
    [Fact]
    public void Fts5IsCompiledIntoTheSqliteBuild() => Assert.True(Database.Fts5Available());

    /// <summary>
    /// A row inserted when recording started and never completed is the trace a crash leaves.
    /// It is the only kind of row the startup reclaim may touch: anything with audio attached
    /// was finished properly, and anything past Queued is somebody else's business.
    /// </summary>
    [Fact]
    public void OnlyWaitingCallsWithNoAudioAreStranded()
    {
        var stranded = _repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Recorded,
        });

        var finished = _repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Queued,
        });
        _repo.CompleteCall(finished, "mic.wav", "far.wav", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);

        var transcribed = NewCall();

        var ids = _repo.CallsWithoutAudio().Select(c => c.Id).ToList();

        Assert.Equal(new[] { stranded }, ids);
        Assert.DoesNotContain(finished, ids);
        Assert.DoesNotContain(transcribed, ids);
    }

    /// <summary>
    /// The compression backlog is exactly the finished calls still holding PCM. A call without
    /// a transcript keeps its original — the audio is its whole record — and a call the queue
    /// has or is about to have is left alone until it is done.
    /// </summary>
    /// <summary>
    /// "Voice call" is what WhatsApp's call window is titled on a second screen. Bound to a
    /// contact once, it filed every later call under that person. Refused on the way in, ignored
    /// on the way out, and never offered as a name.
    /// </summary>
    [Theory]
    [InlineData("Voice call")]
    [InlineData("WhatsApp")]
    [InlineData("Voice call - WhatsApp")]
    [InlineData("Sesli arama")]
    [InlineData("Telegram")]
    public void AGenericWindowTitleIsNeverBoundOrResolved(string title)
    {
        var contactId = _repo.UpsertContact("Uliana", CallApp.WhatsApp);

        Assert.False(_repo.RememberTitle(title, contactId, CallApp.WhatsApp));
        Assert.Null(_repo.ResolveTitle(title, CallApp.WhatsApp));
        Assert.True(VoiceTranscript.Core.Detection.GenericTitles.IsGeneric(title));
    }

    [Fact]
    public void ARealNameStillBindsAndResolves()
    {
        var contactId = _repo.UpsertContact("Gürhan Abi", CallApp.Telegram);

        Assert.False(VoiceTranscript.Core.Detection.GenericTitles.IsGeneric("Gürhan Abi"));
        Assert.True(_repo.RememberTitle("Gürhan Abi", contactId, CallApp.Telegram));
        Assert.Equal(contactId, _repo.ResolveTitle("Gürhan Abi", CallApp.Telegram));
    }

    [Fact]
    public void TheVocabularyKnowsEveryNameTheUserWroteDown()
    {
        var uliana = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        _repo.UpsertContact("Gürhan Abi", CallApp.Telegram);
        _repo.AddField(uliana, "Şirket", "Sumsub");

        var names = _repo.VocabularyNames();

        Assert.Contains("Uliana", names);
        Assert.Contains("Gürhan Abi", names);
        Assert.Contains("Sumsub", names);
    }

    [Fact]
    public void ATodoIsWrittenTickedAndForgottenInItsOwnTable()
    {
        var due = new DateOnly(2026, 9, 5);
        var id = _repo.AddTodo("  Cuma günü evrakları gönder ", due);

        var open = Assert.Single(_repo.ListTodos());
        Assert.Equal(id, open.Id);
        Assert.Equal("Cuma günü evrakları gönder", open.Text);
        Assert.Equal(due, open.DueDate);
        Assert.Null(open.DoneAt);

        _repo.SetTodoDone(id, true);
        Assert.Empty(_repo.ListTodos());

        var done = Assert.Single(_repo.ListTodos(includeDone: true));
        Assert.NotNull(done.DoneAt);

        _repo.SetTodoDone(id, false);
        Assert.Single(_repo.ListTodos());

        _repo.DeleteTodo(id);
        Assert.Empty(_repo.ListTodos(includeDone: true));
    }

    [Fact]
    public void OnlyFinishedTranscribedCallsWithPcmAreCompressed()
    {
        long Insert(ProcessingState state, string mic, string far, bool withTranscript)
        {
            var id = _repo.InsertCall(new Call
            {
                App = CallApp.WhatsApp, StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Recorded,
            });
            _repo.CompleteCall(id, mic, far, TimeSpan.FromMinutes(2), DateTimeOffset.UtcNow);
            if (withTranscript)
                _repo.ReplaceSegments(id, [new Segment { CallId = id, IsMe = false, StartMs = 0, EndMs = 1000, Text = "kayıt" }]);
            _repo.SetCallState(id, state);
            return id;
        }

        var done = Insert(ProcessingState.Analysed, "a-mic.wav", "a-far.wav", withTranscript: true);
        var textOnly = Insert(ProcessingState.Transcribed, "b-mic.wav", "b-far.wav", withTranscript: true);
        var queued = Insert(ProcessingState.Queued, "c-mic.wav", "c-far.wav", withTranscript: true);
        var noTranscript = Insert(ProcessingState.Analysed, "d-mic.wav", "d-far.wav", withTranscript: false);
        var already = Insert(ProcessingState.Analysed, "e-mic.ogg", "e-far.ogg", withTranscript: true);

        var ids = _repo.CallsWithUncompressedAudio().Select(c => c.Id).ToList();

        Assert.Equal(new[] { done, textOnly }, ids);
        Assert.DoesNotContain(queued, ids);
        Assert.DoesNotContain(noTranscript, ids);
        Assert.DoesNotContain(already, ids);
    }

    [Fact]
    public void SchemaAppliesAndIsIdempotent()
    {
        var database = new Database(_path);
        database.Migrate();
        database.Migrate();

        Assert.Empty(_repo.ListContacts());
    }

    // ---- contacts -----------------------------------------------------------

    [Fact]
    public void UpsertContactReturnsTheSameRowForTheSamePerson()
    {
        var first = _repo.UpsertContact("Ahmet Yılmaz", CallApp.Telegram);
        var second = _repo.UpsertContact("Ahmet Yılmaz", CallApp.Telegram);

        Assert.Equal(first, second);
        Assert.Single(_repo.ListContacts());
    }

    /// <summary>
    /// The same person typed with different Turkish spellings must not become two contacts,
    /// otherwise their history splits in half and the whole point of the ledger is lost.
    /// </summary>
    [Fact]
    public void ContactsMatchAcrossTurkishSpellingVariants()
    {
        var canonical = _repo.UpsertContact("Işık Çağrı", CallApp.WhatsApp);

        Assert.Equal(canonical, _repo.UpsertContact("IŞIK ÇAĞRI", CallApp.WhatsApp));
        Assert.Equal(canonical, _repo.UpsertContact("ışık çağrı", CallApp.WhatsApp));
        Assert.Single(_repo.ListContacts());
    }

    [Fact]
    public void SamePersonOnDifferentAppsIsStoredSeparately()
    {
        var telegram = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var whatsapp = _repo.UpsertContact("Ahmet", CallApp.WhatsApp);

        Assert.NotEqual(telegram, whatsapp);
    }

    [Fact]
    public void ContactNamesKeepTheirOriginalSpellingForDisplay()
    {
        var id = _repo.UpsertContact("Şükrü Gökhan", CallApp.Telegram);

        Assert.Equal("Şükrü Gökhan", _repo.GetContact(id)!.Name);
    }

    [Fact]
    public void FindContactsMatchesPartialAndDifferentlySpelledNames()
    {
        _repo.UpsertContact("Ahmet Yılmaz", CallApp.Telegram);

        Assert.NotEmpty(_repo.FindContacts("yilmaz"));
        Assert.NotEmpty(_repo.FindContacts("YILMAZ"));
        Assert.NotEmpty(_repo.FindContacts("ahmet"));
        Assert.Empty(_repo.FindContacts("mehmet"));
    }

    [Fact]
    public void EmptyContactNameIsRejected()
        => Assert.Throws<ArgumentException>(() => _repo.UpsertContact("   ", CallApp.Telegram));

    // ---- title bindings -----------------------------------------------------

    /// <summary>
    /// WhatsApp never puts the contact in its window title, so the application learns the
    /// mapping from the label the user gives after the first call.
    /// </summary>
    [Fact]
    public void ATitleLearnedOnceResolvesAutomaticallyAfterwards()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        _repo.RememberTitle("Ahmet Yılmaz", contact, CallApp.Telegram);

        Assert.Equal(contact, _repo.ResolveTitle("Ahmet Yılmaz", CallApp.Telegram));
        Assert.Null(_repo.ResolveTitle("Ahmet Yılmaz", CallApp.WhatsApp));
        Assert.Null(_repo.ResolveTitle("Bilinmeyen", CallApp.Telegram));
    }

    /// <summary>
    /// Telegram window titles arrive with a leading LEFT-TO-RIGHT MARK. If that is stored raw,
    /// the binding never matches again.
    /// </summary>
    [Fact]
    public void TitlesAreCleanedOfInvisibleMarksBeforeBinding()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        _repo.RememberTitle("\u200eAhmet Yılmaz", contact, CallApp.Telegram);

        Assert.Equal(contact, _repo.ResolveTitle("Ahmet Yılmaz", CallApp.Telegram));
    }

    // ---- calls --------------------------------------------------------------

    [Fact]
    public void AssigningAContactUpdatesItsCountAndLastCallTime()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var when = DateTimeOffset.UtcNow.AddHours(-2);

        _repo.AssignContact(NewCall(at: when), contact);
        _repo.AssignContact(NewCall(at: when.AddHours(1)), contact);

        var stored = _repo.GetContact(contact)!;
        Assert.Equal(2, stored.CallCount);
        Assert.NotNull(stored.LastCallAt);
    }

    [Fact]
    public void CallsAwaitingProcessingReturnsOnlyUnprocessedOnes()
    {
        var pending = _repo.InsertCall(new Call { StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Recorded });
        _repo.InsertCall(new Call { StartedAt = DateTimeOffset.UtcNow, State = ProcessingState.Analysed });

        var queue = _repo.CallsAwaitingProcessing();

        Assert.Single(queue);
        Assert.Equal(pending, queue[0].Id);
    }

    [Fact]
    public void CallRoundTripsAllItsFields()
    {
        var id = _repo.InsertCall(new Call
        {
            App = CallApp.WhatsApp,
            Direction = CallDirection.Incoming,
            Kind = CallKind.Group,
            StartedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(12),
            MicPath = @"D:\a\mic.wav",
            FarPath = @"D:\a\far.wav",
            State = ProcessingState.Skipped,
            LikelyNoHeadphones = true,
            IsPinned = true,
        });

        var stored = _repo.GetCall(id)!;

        Assert.Equal(CallApp.WhatsApp, stored.App);
        Assert.Equal(CallKind.Group, stored.Kind);
        Assert.Equal(TimeSpan.FromMinutes(12), stored.Duration);
        Assert.True(stored.LikelyNoHeadphones);
        Assert.True(stored.IsPinned);
    }

    // ---- segments and search ------------------------------------------------

    private long SeedTranscript(params (bool me, int ms, string text)[] lines)
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var call = NewCall(contact);
        _repo.AssignContact(call, contact);

        _repo.ReplaceSegments(call, lines.Select(l => new Segment
        {
            CallId = call,
            IsMe = l.me,
            StartMs = l.ms,
            EndMs = l.ms + 2000,
            Text = l.text,
        }));

        return call;
    }

    [Fact]
    public void SegmentsRoundTripInTimeOrder()
    {
        var call = SeedTranscript((true, 3000, "ikinci"), (false, 1000, "birinci"));

        var segments = _repo.GetSegments(call);

        Assert.Equal(["birinci", "ikinci"], segments.Select(s => s.Text));
        Assert.False(segments[0].IsMe);
    }

    [Fact]
    public void ReplaceSegmentsClearsThePreviousTranscript()
    {
        var call = SeedTranscript((true, 0, "eski metin"));

        _repo.ReplaceSegments(call, [new Segment { CallId = call, IsMe = true, StartMs = 0, EndMs = 1, Text = "yeni metin" }]);

        Assert.Single(_repo.GetSegments(call));
        Assert.Empty(_repo.Search("eski"));
        Assert.NotEmpty(_repo.Search("yeni"));
    }

    /// <summary>
    /// The failure this normalisation exists to prevent: with FTS5 defaults these queries return
    /// nothing at all, and it looks like the transcript was never saved.
    /// </summary>
    [Theory]
    [InlineData("ışık")]
    [InlineData("IŞIK")]
    [InlineData("Işık")]
    [InlineData("isik")]
    public void SearchFindsTurkishWordsWhateverTheSpelling(string query)
    {
        SeedTranscript((false, 0, "Odadaki ışık açık kalmış"));

        Assert.NotEmpty(_repo.Search(query));
    }

    /// <summary>Turkish is agglutinative, so the word searched is rarely the word spoken.</summary>
    [Fact]
    public void SearchReachesSuffixedFormsOfTheSameWord()
    {
        SeedTranscript((false, 0, "Ödemeyi cuma günü yapacağım"));

        Assert.NotEmpty(_repo.Search("ödeme"));
        Assert.NotEmpty(_repo.Search("odeme"));
        Assert.NotEmpty(_repo.Search("cuma"));
    }

    [Fact]
    public void SearchRequiresAllTerms()
    {
        SeedTranscript((false, 0, "fatura tutarı on sekiz bin lira"));

        Assert.NotEmpty(_repo.Search("fatura tutar"));
        Assert.Empty(_repo.Search("fatura kontrat"));
    }

    [Fact]
    public void SearchResultsCarryEnoughContextToJumpToTheAudio()
    {
        var call = SeedTranscript((false, 45_000, "on sekiz bin olur ancak"));

        var hit = Assert.Single(_repo.Search("sekiz"));

        Assert.Equal(call, hit.CallId);
        Assert.Equal(45_000, hit.StartMs);
        Assert.Equal("Ahmet", hit.ContactName);
        Assert.False(hit.IsMe);
    }

    [Fact]
    public void SearchIgnoresFts5OperatorsInUserInput()
    {
        SeedTranscript((false, 0, "sozlesme hazir"));

        Assert.Empty(_repo.Search("\"unmatched"));
        Assert.NotEmpty(_repo.Search("sozlesme"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSearchReturnsNothingRatherThanEverything(string query)
        => Assert.Empty(_repo.Search(query));

    // ---- analysis -----------------------------------------------------------

    [Fact]
    public void OpenCommitmentsComeBackOrderedByDeadline()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var call = NewCall(contact);

        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, Quote = "gelecek ay hallederim",
            Obligation = "ödeme", DeadlineDate = new DateOnly(2026, 9, 30),
        });
        _repo.InsertCommitment(new Commitment
        {
            CallId = call, ContactId = contact, Quote = "cuma günü yollarım",
            Obligation = "evrak", DeadlineDate = new DateOnly(2026, 9, 1),
        });

        var open = _repo.GetOpenCommitments(contact);

        Assert.Equal(2, open.Count);
        Assert.Equal("evrak", open[0].Obligation);
    }

    [Fact]
    public void OverdueIsComputedAgainstTheDeadline()
    {
        var commitment = new Commitment
        {
            CallId = 1, Quote = "cuma yollarım", Obligation = "evrak",
            DeadlineDate = new DateOnly(2026, 8, 1), Status = CommitmentStatus.Open,
        };

        Assert.True(commitment.IsOverdue(new DateOnly(2026, 8, 20)));
        Assert.False(commitment.IsOverdue(new DateOnly(2026, 7, 20)));
        Assert.False((commitment with { Status = CommitmentStatus.Fulfilled }).IsOverdue(new DateOnly(2026, 8, 20)));
    }

    /// <summary>
    /// Claims are stored so a plain SQL join finds a later contradicting statement. Nothing has
    /// to remember anything, and the comparison is exact rather than inferred.
    /// </summary>
    [Fact]
    public void ClaimsAboutTheSameThingAreFoundTogetherAcrossCalls()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);

        foreach (var (amount, quote) in new[] { (12000m, "on iki bin"), (18000m, "on sekiz bin") })
        {
            var call = NewCall(contact);
            _repo.InsertClaim(new Claim
            {
                CallId = call, ContactId = contact, Quote = quote,
                Entity = "Sipariş", Attribute = "Fiyat", Value = quote, NumericValue = amount,
            });
        }

        // Entity and attribute are folded on write, so the lookup spelling does not matter.
        var claims = _repo.GetClaims(contact, "siparis", "fiyat");

        Assert.Equal(2, claims.Count);
        Assert.Equal([12000m, 18000m], claims.Select(c => c.NumericValue));
    }

    [Fact]
    public void DismissedFlagsStayHidden()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var call = NewCall(contact);

        var flag = _repo.InsertFlag(new Flag
        {
            CallId = call, ContactId = contact, Kind = FlagKind.ChangedAmount,
            Summary = "Fiyat değişti", Quote = "on sekiz bin", QuoteStartMs = 1000,
        });

        Assert.Single(_repo.GetFlags(contact));

        _repo.DismissFlag(flag);

        Assert.Empty(_repo.GetFlags(contact));
        Assert.Single(_repo.GetFlags(contact, includeDismissed: true));
    }

    [Fact]
    public void FlagsKeepBothSidesOfAContradiction()
    {
        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var earlier = NewCall(contact);
        var later = NewCall(contact);

        _repo.InsertFlag(new Flag
        {
            CallId = later, ContactId = contact, Kind = FlagKind.ChangedAmount,
            Summary = "Fiyat 12.000 TL'den 18.000 TL'ye çıktı",
            Quote = "on sekiz bin olur ancak", QuoteStartMs = 45_000,
            CounterQuote = "on iki bin diye konuşmuştuk", CounterCallId = earlier, CounterQuoteStartMs = 12_000,
        });

        var flag = Assert.Single(_repo.GetFlags(contact));

        Assert.Equal(earlier, flag.CounterCallId);
        Assert.Equal(12_000, flag.CounterQuoteStartMs);
        Assert.False(flag.IsHeuristic);
    }

    [Fact]
    public void SummariesAreReplacedRatherThanDuplicatedOnReanalysis()
    {
        var call = NewCall();

        _repo.SaveSummary(new CallSummary { CallId = call, Summary = "ilk özet", ModelUsed = "qwen3.5-4b" });
        _repo.SaveSummary(new CallSummary { CallId = call, Summary = "yeni özet", ModelUsed = "qwen3.5-4b" });

        Assert.Equal("yeni özet", _repo.GetSummary(call)!.Summary);
    }

    // ---- deletion -----------------------------------------------------------

    /// <summary>
    /// Deleting a contact must leave nothing behind — including in the search index, which is a
    /// separate table and would otherwise keep returning hits for someone who no longer exists.
    /// </summary>
    [Fact]
    public void DeletingAContactRemovesEveryTraceAndReportsItsAudioFiles()
    {
        // Real files on disk, because the promise being tested is that the recordings go. A
        // path pointing at nothing would pass whatever the deletion code did.
        var audio = Path.Combine(Path.GetTempPath(), $"vt-del-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audio);

        var micFile = Path.Combine(audio, "mic.wav");
        var farFile = Path.Combine(audio, "far.wav");

        File.WriteAllBytes(micFile, new byte[64]);
        File.WriteAllBytes(farFile, new byte[64]);

        var contact = _repo.UpsertContact("Ahmet", CallApp.Telegram);
        var call = _repo.InsertCall(new Call
        {
            ContactId = contact,
            StartedAt = DateTimeOffset.UtcNow,
            MicPath = micFile,
            FarPath = farFile,
            State = ProcessingState.Analysed,
        });

        _repo.ReplaceSegments(call, [new Segment { CallId = call, IsMe = false, StartMs = 0, EndMs = 1, Text = "gizli konu" }]);
        _repo.InsertFlag(new Flag { CallId = call, ContactId = contact, Kind = FlagKind.PressureTactic, Summary = "x", Quote = "y" });
        _repo.InsertClaim(new Claim { CallId = call, ContactId = contact, Quote = "y", Entity = "a", Attribute = "b", Value = "c" });

        var result = _repo.DeleteContactCompletely(contact);

        // The count is files genuinely deleted. It used to be incremented for paths that were
        // not there at all, which reported "2 recordings removed" for a call whose audio had
        // already gone — a reassuring number that meant nothing.
        Assert.Equal(2, result.FilesRemoved);
        Assert.True(result.IsComplete);
        Assert.False(File.Exists(micFile));
        Assert.False(File.Exists(farFile));

        Directory.Delete(audio, recursive: true);
        Assert.Null(_repo.GetContact(contact));
        Assert.Null(_repo.GetCall(call));
        Assert.Empty(_repo.GetSegments(call));
        Assert.Empty(_repo.Search("gizli"));
        Assert.Empty(_repo.GetFlags(contact, includeDismissed: true));
        Assert.Empty(_repo.GetAllClaims(contact));
    }

    /// <summary>
    /// A window title that turns out to belong to two people stops being trusted.
    ///
    /// This is the defect that made every WhatsApp conversation "Uliana". The title was bound to
    /// the first contact labelled against it and then consulted before anybody was asked, so each
    /// later call was filed under that person without a prompt. Rebinding on conflict — what the
    /// code used to do — did not help: the pattern went on capturing calls, it just captured them
    /// for whoever had been named most recently.
    ///
    /// The failure is silent in the worst way. Nothing errors, the archive looks full, and two
    /// people's histories are quietly merged — which also corrupts the ledger, because a price
    /// that "changed" did so between two different conversations with two different people.
    /// </summary>
    [Fact]
    public void ATitleClaimedByTwoPeopleStopsIdentifyingAnybody()
    {
        var uliana = _repo.UpsertContact("Uliana", CallApp.WhatsApp);
        var gurhan = _repo.UpsertContact("Gurhan", CallApp.WhatsApp);

        // WhatsApp shows whichever chat was open, so the same string arrives for both calls.
        const string shared = "WhatsApp Sohbet";

        Assert.True(_repo.RememberTitle(shared, uliana, CallApp.WhatsApp));
        Assert.Equal(uliana, _repo.ResolveTitle(shared, CallApp.WhatsApp));

        // The second person behind the same title is the proof that it identifies nobody.
        Assert.False(_repo.RememberTitle(shared, gurhan, CallApp.WhatsApp));

        // And from then on it names nobody at all — asking is better than answering wrongly.
        Assert.Null(_repo.ResolveTitle(shared, CallApp.WhatsApp));
    }

    [Fact]
    public void ATitleThatKeepsNamingOnePersonGoesOnWorking()
    {
        // The Telegram case, which is the reason the feature exists: the call window really is
        // titled with the counterpart. Learning to distrust titles must not break it.
        var ahmet = _repo.UpsertContact("Ahmet", CallApp.Telegram);

        Assert.True(_repo.RememberTitle("Ahmet", ahmet, CallApp.Telegram));
        Assert.True(_repo.RememberTitle("Ahmet", ahmet, CallApp.Telegram));

        Assert.Equal(ahmet, _repo.ResolveTitle("Ahmet", CallApp.Telegram));
    }

    [Fact]
    public void AWrongBindingCanBeForgotten()
    {
        // The way out for somebody who has already been filed under the wrong person.
        var contact = _repo.UpsertContact("Berk", CallApp.Telegram);

        _repo.RememberTitle("Berk", contact, CallApp.Telegram);
        Assert.Equal(contact, _repo.ResolveTitle("Berk", CallApp.Telegram));

        _repo.ForgetTitle("Berk", CallApp.Telegram);
        Assert.Null(_repo.ResolveTitle("Berk", CallApp.Telegram));
    }
}
