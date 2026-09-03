namespace VoiceTranscript.Core.Text;

/// <summary>
/// The words the recogniser should expect: the ones the user typed, and only those.
///
/// <b>This class used to do considerably more, and what was removed is worth writing down,
/// because the idea that produced it is a good one and somebody will have it again.</b>
///
/// The reasoning was that a hand-written term list does not scale — the user has hundreds of
/// names, products and bits of jargon, nobody maintains a list like that, and most of it is
/// already in the application: the contacts, and the proper nouns the transcripts themselves keep
/// producing. So a miner read capitalised mid-sentence words out of the archive, they were merged
/// with the contact list, and the result went to the recogniser two ways at once: as
/// <c>hotwords</c>, and as the decoder's <c>initial_prompt</c>.
///
/// Those two are not the same feature spelled differently, and treating them as one is what
/// caused the worst fault this application has had.
///
/// <list type="bullet">
///   <item><b>hotwords</b> is a weighting. Every term's probability is nudged up in each decoding
///   window, so "Sumsub" wins against "sum sub" where the audio is ambiguous. A wrong term costs
///   almost nothing: it simply never wins.</item>
///   <item><b>initial_prompt</b> is context — text the decoder is told it has just produced, which
///   it then continues. It carries style as much as vocabulary, and a comma-separated list of
///   capitalised words is a style. Given one, the model stops transcribing and goes on writing the
///   list.</item>
/// </list>
///
/// Measured on one real recording, the same 180 seconds through the same service, the only
/// difference being this field:
///
/// <code>
/// with it:    "Yani, Uzun, Bir, Süre, Tabii, İşin, Yücün, Rast gelsin, Yapıyor, Bunu, Ama,
///              Sonuçta, Bu, Paraları, Senin, Ödem..."
/// without it: "Bu paraları senin ödemen gerekiyordu. O kendisi üstleniyor. Neden? Çünkü senin
///              sorumluluğunda."
/// </code>
///
/// The second is the transcript. The dates agree: the prompt was introduced on 2026-09-02, every
/// call recorded before it reads cleanly at 100-160 words a minute with no invented lines, and
/// almost every call after it carries them.
///
/// And it compounded rather than staying constant, because the list fed itself. The miner found
/// names by looking for capitalised words mid-sentence — and a transcript made of capitalised
/// words mid-sentence is nothing but candidates. Two days of that had collected 230 "names" whose
/// most frequent members were "Yani", "Ben", "Tamam", "Ama", "Evet": the commonest words in the
/// language. Those went back into the prompt, and round again.
///
/// So the prompt is gone, and the mining with it. What is left is the list the user typed, which
/// was always the part that could not be derived from anything and is the reason the feature
/// exists — "Sumsub" is not in the archive under the right spelling precisely because the
/// recogniser has never once got it right.
///
/// <b>Where the remaining list actually reaches.</b> Hotwords is a faster-whisper (CTranslate2)
/// parameter. The local engine uses it; ElevenLabs takes the same terms as <c>keyterms</c> and
/// Deepgram as <c>keywords</c>. stt.ex5.ai and OpenAI have no equivalent field at all, so on those
/// two the typed list is carried and has no effect. That is worth knowing before anybody spends
/// an evening wondering why a term they added changed nothing.
/// </summary>
public sealed record Vocabulary(string? Terms)
{
    public static readonly Vocabulary Empty = new((string?)null);

    /// <summary>How many terms go as hotwords. Beyond this the bias dilutes into noise.</summary>
    public const int MaxTerms = 300;

    /// <summary>The typed terms, cleaned and de-duplicated, as the recogniser wants them.</summary>
    public static Vocabulary Compose(IEnumerable<string>? manual)
    {
        if (manual is null) return Empty;

        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var terms = new List<string>();

        foreach (var raw in manual)
        {
            var term = raw.Trim().Trim(',', ';', '.');
            if (term.Length < 2 || !seen.Add(term)) continue;

            terms.Add(term);
            if (terms.Count >= MaxTerms) break;
        }

        return terms.Count == 0 ? Empty : new Vocabulary(string.Join(", ", terms));
    }
}
