using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VoiceTranscript.Core.Export;

/// <summary>Why an archive could not be opened. Said apart, because the answers differ.</summary>
public enum ArchiveFault
{
    None = 0,

    /// <summary>Not one of ours — a different file, or a renamed one.</summary>
    NotAnArchive,

    /// <summary>Written by a newer version than this one knows how to read.</summary>
    TooNew,

    /// <summary>The password is wrong, or the file has been altered since it was written.</summary>
    WrongPasswordOrDamaged,
}

/// <summary>
/// A password-protected container for everything an archive holds.
///
/// Deliberately not an encrypted ZIP. The encryption ZIP has had for thirty years is ZipCrypto,
/// which is broken to the point of being decorative — a known-plaintext attack recovers the key
/// from a few bytes anybody can guess — and the AES extension is supported inconsistently enough
/// that "it opened in my tool" is not a guarantee anybody can rely on. What is being written here
/// is the audio of private conversations, so the container is a plain one this application
/// understands and nothing else does: AES-256-GCM over a ZIP, with the key derived from the
/// password. It cannot be opened by 7-Zip. That is the trade, and it is the right way round.
///
/// The layout, in order:
///
///   magic "VTARCH01"    8 bytes   so a wrong file is refused as a wrong file rather than as a
///                                 wrong password, which sends somebody hunting for the password
///                                 they typed correctly
///   version             1 byte    a future format is refused by name, not misread
///   iterations          4 bytes   written down rather than assumed, so raising it later does not
///                                 lock anybody out of the archives they already have
///   salt                16 bytes  per archive, so two archives under one password share no key
///   nonce               12 bytes  per archive, never reused: GCM with a repeated nonce and the
///                                 same key leaks the plaintext of both
///   tag                 16 bytes  checked before a single byte is handed back
///   ciphertext          rest
///
/// The tag is what makes a wrong password say so. Without it, decryption with the wrong key
/// produces bytes rather than an error — bytes that would then be unzipped, parsed, and imported.
/// </summary>
public static class EncryptedArchive
{
    private static readonly byte[] Magic = "VTARCH01"u8.ToArray();

    private const byte Version = 1;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>
    /// PBKDF2 rounds. Slow on purpose: the only defence a short password has is the time it costs
    /// to try the next one. Six hundred thousand is the current OWASP figure for SHA-256 and takes
    /// a fraction of a second here, against an archive somebody may keep for years.
    /// </summary>
    public const int Iterations = 600_000;

    /// <summary>Bytes at the front that say what this is, without needing the password.</summary>
    public static int HeaderLength => Magic.Length + 1 + 4 + SaltBytes + NonceBytes + TagBytes;

    /// <summary>Whether a file begins the way one of ours does. Cheap, and needs no password.</summary>
    public static bool LooksLikeOne(Stream input)
    {
        if (!input.CanSeek) return false;

        var start = input.Position;

        try
        {
            Span<byte> head = stackalloc byte[8];
            if (input.Read(head) != head.Length) return false;

            return head.SequenceEqual(Magic);
        }
        finally
        {
            input.Position = start;
        }
    }

    /// <summary>Encrypts <paramref name="plain"/> under <paramref name="password"/>.</summary>
    public static void Write(Stream output, ReadOnlySpan<byte> plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);

        var key = DeriveKey(password, salt, Iterations);

        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Header(salt, nonce));
        }

        CryptographicOperations.ZeroMemory(key);

        var iterations = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(iterations, Iterations);

        output.Write(Magic);
        output.WriteByte(Version);
        output.Write(iterations);
        output.Write(salt);
        output.Write(nonce);
        output.Write(tag);
        output.Write(cipher);
    }

    /// <summary>
    /// Decrypts, or says why not.
    /// </summary>
    /// <remarks>
    /// Nothing is returned unless the tag verifies, so a caller can never be handed plausible
    /// rubbish to unzip. The three faults are kept apart because the answer to each is different:
    /// pick another file, update the application, or try another password.
    /// </remarks>
    public static ArchiveFault TryRead(ReadOnlySpan<byte> archive, string password, out byte[] plain)
    {
        plain = [];

        if (archive.Length < HeaderLength) return ArchiveFault.NotAnArchive;
        if (!archive[..Magic.Length].SequenceEqual(Magic)) return ArchiveFault.NotAnArchive;

        var at = Magic.Length;

        if (archive[at++] != Version) return ArchiveFault.TooNew;

        var iterations = BinaryPrimitives.ReadInt32LittleEndian(archive.Slice(at, 4));
        at += 4;

        // A written-down cost that is absurd is refused rather than obeyed: an archive claiming a
        // billion rounds would otherwise hang the application for an afternoon on open.
        if (iterations is < 1_000 or > 10_000_000) return ArchiveFault.NotAnArchive;

        var salt = archive.Slice(at, SaltBytes).ToArray();
        at += SaltBytes;

        var nonce = archive.Slice(at, NonceBytes).ToArray();
        at += NonceBytes;

        var tag = archive.Slice(at, TagBytes).ToArray();
        at += TagBytes;

        var cipher = archive[at..];
        var key = DeriveKey(password, salt, iterations);
        var opened = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, opened, Header(salt, nonce));
        }
        catch (CryptographicException)
        {
            // The one thing this must not do is guess which of the two it was. A wrong password
            // and a tampered file are indistinguishable by design, and claiming otherwise would
            // be telling somebody their password is right when nothing has checked it.
            CryptographicOperations.ZeroMemory(opened);
            return ArchiveFault.WrongPasswordOrDamaged;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        plain = opened;
        return ArchiveFault.None;
    }

    /// <summary>What each fault means, for somebody who is not going to read the enum.</summary>
    public static string Explain(ArchiveFault fault) => fault switch
    {
        ArchiveFault.NotAnArchive =>
            "Bu dosya bir VoiceTranscript arşivi değil.",
        ArchiveFault.TooNew =>
            "Bu arşiv daha yeni bir sürümle yazılmış. Uygulamayı güncelleyip tekrar dene.",
        ArchiveFault.WrongPasswordOrDamaged =>
            "Parola yanlış ya da dosya bozulmuş. Hangisi olduğu ayırt edilemez.",
        _ => "",
    };

    /// <summary>
    /// The header, bound into the encryption as associated data.
    ///
    /// Without this the salt and nonce are unauthenticated: somebody could edit the iteration
    /// count in a stored archive and the tag would still verify. Binding them means any change to
    /// the header is a change to the ciphertext's own integrity check.
    /// </summary>
    private static byte[] Header(byte[] salt, byte[] nonce)
    {
        var header = new byte[Magic.Length + 1 + salt.Length + nonce.Length];

        Magic.CopyTo(header, 0);
        header[Magic.Length] = Version;
        salt.CopyTo(header, Magic.Length + 1);
        nonce.CopyTo(header, Magic.Length + 1 + salt.Length);

        return header;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
}
