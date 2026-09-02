using System.Text;
using VoiceTranscript.Core.Export;

namespace VoiceTranscript.Tests;

/// <summary>
/// The container an exported archive travels in.
///
/// What is inside one of these is the audio of private conversations, so the tests that matter are
/// not "does it round-trip" — they are the ones about what happens when it does not: a wrong
/// password, an altered file, a file that is not ours at all. Each has to be refused, and refused
/// distinguishably, because the answer to each is different.
/// </summary>
public class EncryptedArchiveTests
{
    private static byte[] Sealed(string text, string password)
    {
        using var memory = new MemoryStream();
        EncryptedArchive.Write(memory, Encoding.UTF8.GetBytes(text), password);
        return memory.ToArray();
    }

    [Fact]
    public void WhatGoesInComesBackOut()
    {
        var archive = Sealed("görüşme kaydı", "kuvvetli-parola");

        Assert.Equal(ArchiveFault.None, EncryptedArchive.TryRead(archive, "kuvvetli-parola", out var back));
        Assert.Equal("görüşme kaydı", Encoding.UTF8.GetString(back));
    }

    /// <summary>
    /// The assertion the whole design rests on. Without an authentication tag, decrypting with the
    /// wrong key yields bytes rather than an error — bytes that would then be unzipped, parsed and
    /// imported as if they were somebody's conversation.
    /// </summary>
    [Fact]
    public void AWrongPasswordYieldsNothingAtAll()
    {
        var archive = Sealed("gizli", "dogru-parola");

        Assert.Equal(
            ArchiveFault.WrongPasswordOrDamaged,
            EncryptedArchive.TryRead(archive, "yanlis-parola", out var back));

        Assert.Empty(back);
    }

    [Fact]
    public void AnAlteredArchiveIsRefused()
    {
        var archive = Sealed("gizli", "parola");

        // One bit, in the ciphertext.
        archive[^1] ^= 0x01;

        Assert.Equal(
            ArchiveFault.WrongPasswordOrDamaged,
            EncryptedArchive.TryRead(archive, "parola", out _));
    }

    /// <summary>
    /// The header is bound into the encryption, so editing the cost written in it — the obvious
    /// way to make somebody's archive take an afternoon to open — breaks the tag instead.
    /// </summary>
    [Fact]
    public void EditingTheHeaderBreaksIt()
    {
        var archive = Sealed("gizli", "parola");

        archive[9] ^= 0x01;   // inside the iteration count

        Assert.NotEqual(ArchiveFault.None, EncryptedArchive.TryRead(archive, "parola", out _));
    }

    /// <summary>
    /// A different file is refused as a different file. Reporting it as a wrong password would
    /// send somebody hunting for a password they had typed correctly.
    /// </summary>
    [Fact]
    public void SomethingElseEntirelyIsNotMistakenForABadPassword()
    {
        var notOurs = Encoding.UTF8.GetBytes("PK sıradan bir zip dosyası, epeyce uzun");

        Assert.Equal(ArchiveFault.NotAnArchive, EncryptedArchive.TryRead(notOurs, "parola", out _));
    }

    [Fact]
    public void AndSoIsAFileTooShortToBeOne()
    {
        Assert.Equal(ArchiveFault.NotAnArchive, EncryptedArchive.TryRead([1, 2, 3], "parola", out _));
    }

    /// <summary>
    /// Two archives, one password, no shared key: the salt and the nonce are fresh every time.
    /// A repeated nonce under one key in GCM leaks the plaintext of both.
    /// </summary>
    [Fact]
    public void TwoArchivesUnderOnePasswordShareNothing()
    {
        var first = Sealed("aynı metin", "aynı parola");
        var second = Sealed("aynı metin", "aynı parola");

        Assert.NotEqual(first, second);

        var header = EncryptedArchive.HeaderLength;
        Assert.NotEqual(first[header..], second[header..]);
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

    [Fact]
    public void AnEmptyArchiveIsStillAnArchive()
    {
        var archive = Sealed("", "parola");

        Assert.Equal(ArchiveFault.None, EncryptedArchive.TryRead(archive, "parola", out var back));
        Assert.Empty(back);
    }

    /// <summary>
    /// Slow on purpose. The only defence a short password has is what it costs to try the next
    /// one, and this number is the whole of that defence.
    /// </summary>
    [Fact]
    public void ThePasswordIsExpensiveToGuess()
    {
        Assert.True(EncryptedArchive.Iterations >= 600_000);
    }
}
