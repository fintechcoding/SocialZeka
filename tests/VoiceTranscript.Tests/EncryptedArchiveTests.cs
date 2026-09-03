using System.Text;
using VoiceTranscript.Core.Export;

namespace VoiceTranscript.Tests;

/// <summary>
/// The container a password-protected backup travels in.
///
/// What is inside one is a database of private conversations and, optionally, their audio. So the
/// tests that matter are not "does it round-trip" — they are the ones about what happens when it
/// does not: a wrong password, an altered file, one cut short by a copy that failed, a file that is
/// not ours at all. Each has to be refused, and refused distinguishably, because the answer to each
/// is a different thing for somebody to go and do.
/// </summary>
public class EncryptedArchiveTests
{
    private static byte[] Sealed(byte[] plain, string password)
    {
        using var output = new MemoryStream();
        EncryptedArchive.Write(output, new MemoryStream(plain), password);
        return output.ToArray();
    }

    private static byte[] Sealed(string text, string password) =>
        Sealed(Encoding.UTF8.GetBytes(text), password);

    private static (ArchiveFault Fault, byte[] Plain) Opened(byte[] archive, string password)
    {
        using var output = new MemoryStream();
        var fault = EncryptedArchive.TryRead(new MemoryStream(archive), output, password);

        return (fault, output.ToArray());
    }

    [Fact]
    public void WhatGoesInComesBackOut()
    {
        var (fault, plain) = Opened(Sealed("görüşme kaydı", "kuvvetli-parola"), "kuvvetli-parola");

        Assert.Equal(ArchiveFault.None, fault);
        Assert.Equal("görüşme kaydı", Encoding.UTF8.GetString(plain));
    }

    /// <summary>
    /// A backup with audio is gigabytes, so the payload is framed. Several frames and a partial
    /// one is the case where an off-by-one in the framing would show.
    /// </summary>
    [Fact]
    public void SomethingLargerThanOneFrameSurvivesWhole()
    {
        var big = new byte[EncryptedArchive.FrameBytes * 2 + 12_345];
        Random.Shared.NextBytes(big);

        var (fault, plain) = Opened(Sealed(big, "parola"), "parola");

        Assert.Equal(ArchiveFault.None, fault);
        Assert.Equal(big, plain);
    }

    [Fact]
    public void AndSoDoesSomethingExactlyOneFrameLong()
    {
        var exact = new byte[EncryptedArchive.FrameBytes];
        Random.Shared.NextBytes(exact);

        var (fault, plain) = Opened(Sealed(exact, "parola"), "parola");

        Assert.Equal(ArchiveFault.None, fault);
        Assert.Equal(exact, plain);
    }

    /// <summary>
    /// The assertion the whole design rests on. Without an authentication tag, decrypting with the
    /// wrong key yields bytes rather than an error — bytes that would then be unzipped and restored
    /// over somebody's live database.
    /// </summary>
    [Fact]
    public void AWrongPasswordYieldsNothingAtAll()
    {
        var (fault, plain) = Opened(Sealed("gizli", "dogru-parola"), "yanlis-parola");

        Assert.Equal(ArchiveFault.WrongPasswordOrDamaged, fault);
        Assert.Empty(plain);
    }

    [Fact]
    public void AnAlteredArchiveIsRefused()
    {
        var archive = Sealed("gizli", "parola");
        archive[^1] ^= 0x01;

        Assert.Equal(ArchiveFault.WrongPasswordOrDamaged, Opened(archive, "parola").Fault);
    }

    /// <summary>
    /// The header is bound into every frame, so editing the cost written in it — the obvious way to
    /// make somebody's backup take an afternoon to open — breaks the tags instead.
    /// </summary>
    [Fact]
    public void EditingTheHeaderBreaksIt()
    {
        var archive = Sealed("gizli", "parola");
        archive[9] ^= 0x01;   // inside the iteration count

        Assert.NotEqual(ArchiveFault.None, Opened(archive, "parola").Fault);
    }

    /// <summary>
    /// A file cut short by a copy that failed. Without "this is the last frame" bound into the tag,
    /// a truncated backup would verify perfectly and restore as a complete, shorter one — half
    /// somebody's archive, silently.
    /// </summary>
    [Fact]
    public void AFileCutShortIsNotMistakenForACompleteOne()
    {
        var big = new byte[EncryptedArchive.FrameBytes * 2];
        Random.Shared.NextBytes(big);

        var archive = Sealed(big, "parola");
        var cut = archive[..(archive.Length / 2)];

        Assert.Equal(ArchiveFault.Truncated, Opened(cut, "parola").Fault);
    }

    /// <summary>
    /// A different file is refused as a different file. Reporting it as a wrong password would send
    /// somebody hunting for a password they had typed correctly.
    /// </summary>
    [Fact]
    public void SomethingElseEntirelyIsNotMistakenForABadPassword()
    {
        var notOurs = Encoding.UTF8.GetBytes("PK sıradan bir zip dosyası, epeyce uzun olsun diye");

        Assert.Equal(ArchiveFault.NotAnArchive, Opened(notOurs, "parola").Fault);
    }

    [Fact]
    public void AndSoIsAFileTooShortToBeOne()
    {
        Assert.Equal(ArchiveFault.NotAnArchive, Opened([1, 2, 3], "parola").Fault);
    }

    /// <summary>
    /// Two archives, one password, no shared key: the salt and the base nonce are fresh every time.
    /// A repeated nonce under one key in GCM leaks the plaintext of both.
    /// </summary>
    [Fact]
    public void TwoArchivesUnderOnePasswordShareNothing()
    {
        var first = Sealed("aynı metin", "aynı parola");
        var second = Sealed("aynı metin", "aynı parola");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first[EncryptedArchive.HeaderLength..], second[EncryptedArchive.HeaderLength..]);
    }

    [Fact]
    public void AnArchiveIsRecognisedWithoutThePassword()
    {
        using var ours = new MemoryStream(Sealed("x", "parola"));
        using var other = new MemoryStream(Encoding.UTF8.GetBytes("PK baska"));

        Assert.True(EncryptedArchive.LooksLikeOne(ours));
        Assert.False(EncryptedArchive.LooksLikeOne(other));

        // And looking does not consume it.
        Assert.Equal(0, ours.Position);
    }

    /// <summary>An empty backup is still a backup, and must not read as a truncated one.</summary>
    [Fact]
    public void AnEmptyArchiveIsStillAnArchive()
    {
        var (fault, plain) = Opened(Sealed("", "parola"), "parola");

        Assert.Equal(ArchiveFault.None, fault);
        Assert.Empty(plain);
    }

    /// <summary>
    /// Slow on purpose. The only defence a short password has is what it costs to try the next one,
    /// and this number is the whole of that defence.
    /// </summary>
    [Fact]
    public void ThePasswordIsExpensiveToGuess()
    {
        Assert.True(EncryptedArchive.Iterations >= 600_000);
    }

    [Fact]
    public void EveryFaultHasSomethingToSayAboutItself()
    {
        foreach (var fault in Enum.GetValues<ArchiveFault>().Where(f => f != ArchiveFault.None))
            Assert.False(string.IsNullOrWhiteSpace(EncryptedArchive.Explain(fault)), fault.ToString());
    }
}
