using VoiceTranscript.App.Services;

namespace VoiceTranscript.Tests;

/// <summary>
/// The same sentence, twenty times, is not twenty pieces of information.
///
/// A backlog of recordings against a service that is down produces one identical pair per
/// recording — "…yükleniyor", "…başarısız" — about a minute apart. The processing rows already
/// carry the per-recording detail; a toast is meant to say something the user does not know.
/// </summary>
public class NoticeRepeatTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameSentenceIsSaidOncePerWindow()
    {
        var guard = new NoticeRepeatGuard();

        Assert.True(guard.ShouldSay("İşleme başarısız: 403.", Start));
        Assert.False(guard.ShouldSay("İşleme başarısız: 403.", Start.AddSeconds(30)));
        Assert.False(guard.ShouldSay("İşleme başarısız: 403.", Start.AddMinutes(4)));
    }

    /// <summary>
    /// The failing pair alternates, so "skip it if it equals the previous one" catches none of it.
    /// This is the case that makes the obvious rule useless, and the reason the guard remembers
    /// each sentence rather than just the last.
    /// </summary>
    [Fact]
    public void AlternatingRepeatsAreCollapsedToo()
    {
        var guard = new NoticeRepeatGuard();
        var said = 0;

        for (var i = 0; i < 20; i++)
        {
            var at = Start.AddSeconds(i * 60);

            if (guard.ShouldSay("Bu görüşme ex5 servisine yükleniyor.", at)) said++;
            if (guard.ShouldSay("İşleme başarısız: 403.", at.AddSeconds(2))) said++;
        }

        // Twenty minutes of it, said four times: once per sentence per five-minute window.
        Assert.Equal(8, said);
    }

    [Fact]
    public void AnythingWordedDifferentlyIsNewInformationAndStillArrives()
    {
        var guard = new NoticeRepeatGuard();

        Assert.True(guard.ShouldSay("İşleme başarısız: 403.", Start));
        Assert.True(guard.ShouldSay("İşleme başarısız: 413.", Start));
        Assert.True(guard.ShouldSay("Kayıt başlatılamadı.", Start));
    }

    /// <summary>
    /// The window ends. It collapses a burst; it does not keep a session quiet — least of all the
    /// warning that says the audio of a conversation is leaving the machine.
    /// </summary>
    [Fact]
    public void OnceTheWindowHasPassedTheSentenceIsSaidAgain()
    {
        var guard = new NoticeRepeatGuard();

        Assert.True(guard.ShouldSay("Ses ex5 servisine yükleniyor.", Start));
        Assert.False(guard.ShouldSay("Ses ex5 servisine yükleniyor.", Start.AddMinutes(4).AddSeconds(59)));
        Assert.True(guard.ShouldSay("Ses ex5 servisine yükleniyor.", Start.AddMinutes(5)));
    }

    [Fact]
    public void ManyDistinctSentencesDoNotGrowTheGuardWithoutLimit()
    {
        var guard = new NoticeRepeatGuard(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 500; i++)
            Assert.True(guard.ShouldSay($"benzersiz {i}", Start.AddSeconds(i)));

        // Still answering correctly after the pruning it had to do along the way.
        Assert.False(guard.ShouldSay("benzersiz 499", Start.AddSeconds(500)));
    }
}
