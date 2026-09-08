using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Core.Asr;

/// <summary>
/// What became of a transcription that came back with no lines: the state the call goes to, the
/// sentence its row carries, and the one-line notice shown when it is set aside.
/// </summary>
/// <param name="State"><see cref="ProcessingState.Skipped"/> for a recording taken at its word,
/// <see cref="ProcessingState.Failed"/> for one that is more likely the engine's fault.</param>
/// <param name="Reason">The row's own sentence: what was found, what it most likely was, what
/// can be done about it.</param>
/// <param name="Notice">The toast for a call set aside. Unused for a failure, which is announced
/// by the ordinary failure path with its own wording.</param>
public sealed record EmptyTranscriptVerdict(ProcessingState State, string Reason, string Notice);

/// <summary>
/// What to make of a transcription with no words in it.
///
/// Four recordings in this archive came back like this, and every one of them looked the same:
/// an outgoing call of about a minute — the length WhatsApp rings before giving up — with the
/// far channel carrying the ring-back tone and the microphone carrying the room. The voice
/// listener counted twenty seconds of "speech" on the far side, because it measures level and a
/// ring is loud; two different engines, on separate days, found no words in it, because there
/// were none. Every one was reported as "İşleme başarısız", listed with the real failures, and
/// deleted by hand.
///
/// So an empty answer is read before it is called a failure. A short call with nothing said on
/// either side is most likely an unanswered one, and is set aside with its audio and a sentence
/// saying so. A long recording with no words at all is a different thing: two people do not sit
/// on a line for five minutes in silence, and an engine that returns nothing for that much audio
/// is the fault worth reporting. The line between the two is generous — an unanswered call rings
/// for a minute, not two — so a real conversation is never mistaken for a missed one.
/// </summary>
public static class EmptyTranscript
{
    /// <summary>
    /// The longest recording an empty answer is taken at its word for.
    ///
    /// Twice what a ring-out lasts. Above it, silence on both sides for the whole length is not
    /// a kind of call anyone makes, and the empty answer says more about the engine than about
    /// the conversation.
    /// </summary>
    public static readonly TimeSpan LongestQuietCall = TimeSpan.FromMinutes(2);

    public static EmptyTranscriptVerdict Judge(CallDirection direction, TimeSpan duration, bool hadTranscript)
    {
        // Never over an existing transcript: the old text stays and the row says why the new
        // attempt was refused.
        if (hadTranscript)
        {
            return new(
                ProcessingState.Failed,
                "Yazıya dökme boş sonuç döndürdü. Var olan döküm korundu — modeli ya da servisi "
                + "değiştirip yeniden deneyebilirsin.",
                "İşleme başarısız: yazıya dökme boş sonuç döndürdü, var olan döküm korundu.");
        }

        var length = $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";

        if (duration > LongestQuietCall)
        {
            return new(
                ProcessingState.Failed,
                $"Yazıya dökme boş sonuç döndürdü: {length} süren kayıtta hiç söz bulunamadı. Bu "
                + "uzunlukta bir sessizlik motoru işaret ediyor; başka bir motorla yeniden yazıya "
                + "dökmeyi dene. Ses kaydı duruyor.",
                "İşleme başarısız: uzun kayıtta hiç söz bulunamadı.");
        }

        if (direction == CallDirection.Outgoing)
        {
            return new(
                ProcessingState.Skipped,
                $"Konuşma bulunamadı: cevapsız arama olabilir. Giden arama {length} sürdü, iki tarafta "
                + "da söz yok. Ses kaydı duruyor; dinleyip gerekirse yeniden yazıya dökebilirsin.",
                "Görüşmede konuşma bulunamadı — cevapsız arama olabilir. Kayıt duruyor.");
        }

        return new(
            ProcessingState.Skipped,
            $"Konuşma bulunamadı: {length} süren kayıtta iki tarafta da söz yok. Ses kaydı duruyor; "
            + "dinleyip gerekirse yeniden yazıya dökebilirsin.",
            "Görüşmede konuşma bulunamadı. Kayıt duruyor, yeniden yazıya dökülebilir.");
    }
}
