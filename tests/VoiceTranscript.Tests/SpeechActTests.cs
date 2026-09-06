using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Tests;

/// <summary>
/// The questions, kept — and the denominator that makes them honest.
///
/// The extraction has always found the questions of a conversation and always thrown them away,
/// so "does this person answer you" could only be asked about the call on screen. These tests
/// pin the two things that make the stored version worth reading: an answer status is one of
/// four words or none at all, and a conversation nobody counted is reported as unmeasured
/// rather than as a conversation in which nothing was asked.
/// </summary>
public sealed class SpeechActTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-sa-{Guid.NewGuid():N}.db");
    private readonly Repository _repo;
    private readonly long _contact;

    public SpeechActTests()
    {
        var database = new Database(_path);
        database.Migrate();
        _repo = new Repository(database);
        _contact = _repo.UpsertContact("Avukat", CallApp.WhatsApp);
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

    private long Call(DateTimeOffset at)
    {
        var call = _repo.InsertCall(new Call
        {
            ContactId = _contact,
            App = CallApp.WhatsApp,
            StartedAt = at,
            State = ProcessingState.Analysed,
        });

        _repo.AssignContact(call, _contact);
        return call;
    }

    private static SpeechAct Question(long call, bool byMe, int ms, string quote, string? status) => new()
    {
        CallId = call,
        ByMe = byMe,
        Kind = SpeechAct.Kinds.Question,
        AnswerStatus = status,
        Quote = quote,
        QuoteStartMs = ms,
    };

    /// <summary>
    /// The four words, and nothing else.
    ///
    /// Red means a word the extraction invented has been stored as if it were one of the four —
    /// a guess about whether somebody answered, dressed as a measurement.
    /// </summary>
    [Fact]
    public void AnAnswerStatusOutsideTheFourIsStoredAsNotRecorded()
    {
        var call = Call(DateTimeOffset.UtcNow);

        _repo.ReplaceSpeechActs(call,
        [
            Question(call, true, 1_000, "Sözleşme ne zaman gelir?", "cevaplandi"),
            Question(call, true, 5_000, "Peki tarih belli mi?", "KISMI"),
            Question(call, true, 9_000, "Ücret ne kadar olacak?", "belki cevapladı"),
            Question(call, true, 12_000, "Yazılı gönderir misin?", null),
        ]);

        var stored = _repo.SpeechActsOf(call);

        Assert.Equal(4, stored.Count);
        Assert.Equal(SpeechAct.Statuses.Answered, stored[0].AnswerStatus);

        // Case is folded, so a model shouting the same word is the same word.
        Assert.Equal(SpeechAct.Statuses.Partial, stored[1].AnswerStatus);

        // And a word nobody recognises is "not recorded", not the nearest of the four.
        Assert.Null(stored[2].AnswerStatus);
        Assert.Null(stored[3].AnswerStatus);

        // The question itself survives either way: it was asked, whatever happened next.
        Assert.Equal("Ücret ne kadar olacak?", stored[2].Quote);
    }

    /// <summary>
    /// A second run replaces rather than appends.
    ///
    /// Red means analysing a call twice doubles its questions, which doubles the denominator the
    /// card divides by and halves every rate computed from it.
    /// </summary>
    [Fact]
    public void RewritingACallsQuestionsReplacesThem()
    {
        var call = Call(DateTimeOffset.UtcNow);

        _repo.ReplaceSpeechActs(call, [Question(call, true, 1_000, "İlk soru?", "kacamak")]);
        _repo.ReplaceSpeechActs(call, [Question(call, true, 1_000, "İlk soru?", "cevaplandi")]);

        var stored = Assert.Single(_repo.SpeechActsOf(call));
        Assert.Equal(SpeechAct.Statuses.Answered, stored.AnswerStatus);

        // The contact travelled with the call rather than being taken from the caller.
        Assert.Equal(_contact, stored.ContactId);
    }

    /// <summary>
    /// "Measured in N of M conversations" — the sentence the card cannot be honest without.
    ///
    /// Red means a call analysed before questions were kept is being reported as a call in which
    /// nobody was asked anything, which turns silence in the archive into a fact about a person.
    /// </summary>
    [Fact]
    public void EveryCallIsReturnedAndTheUnmeasuredOnesSaySo()
    {
        var measured = Call(DateTimeOffset.UtcNow.AddDays(-2));
        var unmeasured = Call(DateTimeOffset.UtcNow.AddDays(-1));

        _repo.ReplaceSpeechActs(measured,
        [
            Question(measured, true, 1_000, "Tarihi netleştirebilir miyiz?", SpeechAct.Statuses.Answered),
            Question(measured, true, 5_000, "Peki ücret ne olacak?", SpeechAct.Statuses.Evasive),
            Question(measured, true, 9_000, "Yazılı gönderecek misin?", SpeechAct.Statuses.Deflected),
            Question(measured, true, 13_000, "Kim imzalayacak?", SpeechAct.Statuses.Partial),

            // Their own question is stored, and is not part of "how they answer you".
            Question(measured, false, 17_000, "Sen ne düşünüyorsun peki?", SpeechAct.Statuses.Answered),
        ]);

        var summary = _repo.SpeechActs(_contact);

        Assert.Equal(2, summary.CallsTotal);
        Assert.Equal(1, summary.CallsMeasured);

        var counted = summary.Calls.Single(c => c.CallId == measured);
        Assert.True(counted.Measured);
        Assert.Equal(4, counted.Asked);
        Assert.Equal(1, counted.Answered);
        Assert.Equal(1, counted.Partial);
        Assert.Equal(1, counted.Evaded);
        Assert.Equal(1, counted.Deflected);
        Assert.Equal(2, counted.Unanswered);

        var blank = summary.Calls.Single(c => c.CallId == unmeasured);
        Assert.False(blank.Measured);
        Assert.Equal(0, blank.Asked);
    }

    /// <summary>
    /// The questions follow their call to a different person.
    ///
    /// Red means moving a mislabelled conversation leaves its questions counted against the
    /// person it was taken away from — the split-history failure the ledger tables were listed
    /// together to prevent.
    /// </summary>
    [Fact]
    public void QuestionsAndTacticEvidenceFollowACallThatIsReassigned()
    {
        var call = Call(DateTimeOffset.UtcNow);
        var other = _repo.UpsertContact("Gürhan", CallApp.WhatsApp);

        _repo.ReplaceSpeechActs(call, [Question(call, true, 1_000, "Bir soru?", "kacamak")]);
        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "kacamak", Quote = "bir alıntı", QuoteStartMs = 1_000 },
        ]);

        _repo.AssignContact(call, other);

        Assert.Equal(other, Assert.Single(_repo.SpeechActsOf(call)).ContactId);
        Assert.Equal(other, Assert.Single(_repo.TacticEvidenceOf(call)).ContactId);
        Assert.Equal(0, _repo.SpeechActs(_contact).CallsTotal);
        Assert.Equal(1, _repo.SpeechActs(other).CallsMeasured);
    }

    /// <summary>
    /// The "you are moving N ledger rows" count leaves the questions out.
    ///
    /// Red means a conversation with forty questions in it announces itself as forty-odd rows to
    /// somebody about to move it, and the sentence stops saying what it exists to say.
    /// </summary>
    [Fact]
    public void TheLedgerCountIgnoresQuestionsAndCountsTacticEvidence()
    {
        var call = Call(DateTimeOffset.UtcNow);

        _repo.ReplaceSpeechActs(call,
        [
            Question(call, true, 1_000, "Birinci soru?", "cevaplandi"),
            Question(call, true, 4_000, "İkinci soru?", "kacamak"),
            Question(call, true, 8_000, "Üçüncü soru?", "kismi"),
        ]);

        Assert.Equal(0, _repo.CountLedgerEntriesForCall(call));

        _repo.ReplaceTacticEvidence(call, TacticEvidence.Sources.Deception,
        [
            new TacticEvidence { CallId = call, Tactic = "baski", Quote = "bir alıntı", QuoteStartMs = 1_000 },
        ]);

        Assert.Equal(1, _repo.CountLedgerEntriesForCall(call));
    }
}
