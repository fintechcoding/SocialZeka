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

    /// <summary>It opened, but it stops before the end the writer marked. Half a backup.</summary>
    Truncated,
}

/// <summary>
/// A password-protected container for a backup.
///
/// Deliberately not an encrypted ZIP. The encryption ZIP has had for thirty years is ZipCrypto,
/// broken to the point of being decorative, and its AES extension is supported inconsistently
/// enough that "it opened in my tool" guarantees nothing. What is being written here is a
/// database of private conversations and, optionally, their audio, so the container is a plain one
/// this application understands and nothing else does: AES-256-GCM, with the key derived from the
/// password. It cannot be opened by 7-Zip. That is the trade, and it is the right way round.
///
/// **Framed, not one block.** A backup with audio is gigabytes, and a single AES-GCM operation
/// needs all of it in memory at once — so the payload is encrypted in one-megabyte frames that
/// stream through a small buffer. Each frame carries its own authentication tag, so a file that
/// has been altered anywhere fails on the frame that was altered rather than after the whole
/// thing has been written out.
///
/// Three details that are the difference between this and something that merely looks encrypted:
///
///   **Each frame's nonce is derived from a counter**, never repeated. Two frames encrypted under
///   one key with one nonce leak each other's contents.
///
///   **Each frame is bound to its own position and to the header**, so frames cannot be swapped,
///   duplicated or dropped without the tag failing.
///
///   **The last frame says it is the last.** Without that, truncating the file would produce a
///   shorter backup that verified perfectly — a restore that silently loses the second half.
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
    /// How much plaintext goes in one frame. A megabyte is small enough to hold several copies of
    /// in memory without thinking about it, and large enough that the per-frame overhead is
    /// nothing against a backup measured in gigabytes.
    /// </summary>
    public const int FrameBytes = 1024 * 1024;

    /// <summary>
    /// PBKDF2 rounds. Slow on purpose: the only defence a short password has is the time it costs
    /// to try the next one. Six hundred thousand is the current OWASP figure for SHA-256 and takes
    /// a fraction of a second here, against an archive somebody may keep for years.
    /// </summary>
    public const int Iterations = 600_000;

    /// <summary>Bytes at the front that say what this is, before any password is needed.</summary>
    public static int HeaderLength => Magic.Length + 1 + 4 + SaltBytes + NonceBytes;

    /// <summary>Whether a file begins the way one of ours does. Cheap, and needs no password.</summary>
    public static bool LooksLikeOne(Stream input)
    {
        if (!input.CanSeek) return false;

        var start = input.Position;

        try
        {
            Span<byte> head = stackalloc byte[8];

            return input.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                   && head.SequenceEqual(Magic);
        }
        finally
        {
            input.Position = start;
        }
    }

    /// <summary>Whether the file at this path is one of ours. False for anything unreadable.</summary>
    public static bool LooksLikeOne(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return LooksLikeOne(file);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Encrypts everything remaining in <paramref name="plain"/> under a password.</summary>
    public static void Write(Stream output, Stream plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var header = Header(salt, nonce);

        output.Write(header);

        var key = DeriveKey(password, salt, Iterations);

        try
        {
            using var aes = new AesGcm(key, TagBytes);

            var current = new byte[FrameBytes];
            var lookahead = new byte[FrameBytes];
            var cipher = new byte[FrameBytes];
            var tag = new byte[TagBytes];
            var length = new byte[4];

            var pending = plain.ReadAtLeast(current, FrameBytes, throwOnEndOfStream: false);

            // Always at least one frame, even for nothing. An empty archive still carries a final
            // frame, or an empty file and a truncated one would be the same thing.
            for (long index = 0; ; index++)
            {
                // Read ahead, because a frame has to know whether it is the last one before it is
                // sealed: "last" is bound into the tag, and that is what makes a file cut short
                // fail to verify instead of passing as a shorter backup.
                var following = pending == FrameBytes
                    ? plain.ReadAtLeast(lookahead, FrameBytes, throwOnEndOfStream: false)
                    : 0;

                var last = following == 0;

                aes.Encrypt(
                    NonceFor(nonce, index),
                    current.AsSpan(0, pending),
                    cipher.AsSpan(0, pending),
                    tag,
                    FrameHeader(header, index, last));

                BinaryPrimitives.WriteInt32LittleEndian(length, pending);

                output.Write(length);
                output.Write(cipher.AsSpan(0, pending));
                output.Write(tag);

                if (last) break;

                (current, lookahead) = (lookahead, current);
                pending = following;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypts into <paramref name="output"/>, or says why not and writes nothing worth keeping.
    /// </summary>
    /// <remarks>
    /// Every frame is verified before it is written, so a caller is never handed plausible rubbish
    /// to unzip. The faults are kept apart because the answer to each is different: pick another
    /// file, update the application, try another password, or find a copy that is not cut short.
    /// </remarks>
    public static ArchiveFault TryRead(Stream input, Stream output, string password)
    {
        var header = new byte[HeaderLength];

        if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            return ArchiveFault.NotAnArchive;

        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return ArchiveFault.NotAnArchive;

        var at = Magic.Length;

        if (header[at++] != Version) return ArchiveFault.TooNew;

        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(at, 4));
        at += 4;

        // A written-down cost that is absurd is refused rather than obeyed: an archive claiming a
        // billion rounds would otherwise hang the application for an afternoon on open.
        if (iterations is < 1_000 or > 10_000_000) return ArchiveFault.NotAnArchive;

        var salt = header.AsSpan(at, SaltBytes).ToArray();
        at += SaltBytes;

        var nonce = header.AsSpan(at, NonceBytes).ToArray();

        var key = DeriveKey(password, salt, iterations);

        try
        {
            using var aes = new AesGcm(key, TagBytes);

            var length = new byte[4];
            var cipher = new byte[FrameBytes];
            var plain = new byte[FrameBytes];
            var tag = new byte[TagBytes];

            for (long index = 0; ; index++)
            {
                if (input.ReadAtLeast(length, 4, throwOnEndOfStream: false) != 4)
                    return ArchiveFault.Truncated;

                var size = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (size is < 0 or > FrameBytes) return ArchiveFault.WrongPasswordOrDamaged;

                if (input.ReadAtLeast(cipher.AsSpan(0, size), size, throwOnEndOfStream: false) != size)
                    return ArchiveFault.Truncated;

                if (input.ReadAtLeast(tag, TagBytes, throwOnEndOfStream: false) != TagBytes)
                    return ArchiveFault.Truncated;

                // Tried as a middle frame, then as the last one. Which it is has to be proven by
                // the tag rather than assumed from the file ending here — a file that ends early
                // would otherwise pass as a complete, shorter backup.
                var last = false;

                try
                {
                    aes.Decrypt(NonceFor(nonce, index), cipher.AsSpan(0, size), tag,
                                plain.AsSpan(0, size), FrameHeader(header, index, last: false));
                }
                catch (CryptographicException)
                {
                    try
                    {
                        aes.Decrypt(NonceFor(nonce, index), cipher.AsSpan(0, size), tag,
                                    plain.AsSpan(0, size), FrameHeader(header, index, last: true));
                        last = true;
                    }
                    catch (CryptographicException)
                    {
                        // A wrong password and a tampered file are indistinguishable by design,
                        // and claiming otherwise would tell somebody their password is right when
                        // nothing has checked it.
                        CryptographicOperations.ZeroMemory(plain);
                        return ArchiveFault.WrongPasswordOrDamaged;
                    }
                }

                output.Write(plain, 0, size);

                if (last) return ArchiveFault.None;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>What each fault means, for somebody who is not going to read the enum.</summary>
    public static string Explain(ArchiveFault fault) => fault switch
    {
        ArchiveFault.NotAnArchive =>
            "Bu dosya bir VoiceTranscript yedeği değil.",
        ArchiveFault.TooNew =>
            "Bu yedek daha yeni bir sürümle yazılmış. Uygulamayı güncelleyip tekrar dene.",
        ArchiveFault.WrongPasswordOrDamaged =>
            "Parola yanlış ya da dosya bozulmuş. Hangisi olduğu ayırt edilemez.",
        ArchiveFault.Truncated =>
            "Dosya yarım — kopyalanırken kesilmiş olabilir. Tamamı olan bir kopya gerekiyor.",
        _ => "",
    };

    /// <summary>
    /// The header, bound into every frame as associated data.
    ///
    /// Without it the salt and nonce are unauthenticated, and somebody could edit the iteration
    /// count in a stored archive while the tags still verified.
    /// </summary>
    private static byte[] Header(byte[] salt, byte[] nonce)
    {
        var header = new byte[HeaderLength];

        Magic.CopyTo(header, 0);
        header[Magic.Length] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length + 1, 4), Iterations);
        salt.CopyTo(header, Magic.Length + 5);
        nonce.CopyTo(header, Magic.Length + 5 + SaltBytes);

        return header;
    }

    /// <summary>Binds a frame to the archive it belongs to, its place in it, and whether it ends it.</summary>
    private static byte[] FrameHeader(byte[] header, long index, bool last)
    {
        var bound = new byte[header.Length + 8 + 1];

        header.CopyTo(bound, 0);
        BinaryPrimitives.WriteInt64LittleEndian(bound.AsSpan(header.Length, 8), index);
        bound[^1] = last ? (byte)1 : (byte)0;

        return bound;
    }

    /// <summary>A nonce per frame, from one random base and a counter. Never repeated.</summary>
    private static byte[] NonceFor(byte[] baseNonce, long index)
    {
        var nonce = (byte[])baseNonce.Clone();

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(counter, index);

        for (var i = 0; i < counter.Length; i++) nonce[nonce.Length - 1 - i] ^= counter[i];

        return nonce;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
}
