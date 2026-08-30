using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Analysis;

public sealed record ScamPattern(string Name, string Explanation, string[] Phrases, int MinimumMatches = 2);

/// <summary>
/// Keyword matching against the scripts used in common Turkish telephone fraud.
///
/// This is a heuristic and is labelled as one everywhere it surfaces. It is not the model
/// judging anyone: it is a curated list of phrases that appear in well-documented scams, and a
/// hit means "this sounded like a known script", never "this person is a criminal".
///
/// It earns its place because these calls follow a script almost word for word, so precision is
/// high, and because the cost of missing one is enormous while the cost of a false positive is
/// a notice the user dismisses in a second. Requiring several phrases from the same pattern
/// keeps ordinary conversation from tripping it: a real bank call mentions accounts constantly,
/// and only a fraudulent one pairs that with secrecy and urgency.
/// </summary>
public static class ScamPatterns
{
    public static IReadOnlyList<ScamPattern> All { get; } =
    [
        new(
            "Sahte banka araması",
            "Hesap güvenliği bahanesiyle işlem yaptırmaya yönlendiren aramalarda kullanılan ifadeler.",
            [
                "hesabınız güvende değil",
                "hesabınızdan şüpheli işlem",
                "kartınız ele geçirilmiş",
                "paranızı güvenli hesaba aktar",
                "havuz hesabına aktarın",
                "bankamızın güvenlik birimi",
                "işlemi iptal etmek için",
            ]),

        new(
            "Kamu görevlisi taklidi",
            "Savcılık, polis veya MASAK adına arama yapıldığı iddiası. Bu kurumlar telefonda para veya bilgi istemez.",
            [
                "savcılık",
                "masak",
                "hakkınızda soruşturma",
                "gizlilik kararı var",
                "kimseye söylemeyin",
                "ifadenizi almamız gerekiyor",
                "üzerinize hesap açılmış",
                "terör örgütü",
            ]),

        new(
            "Kimlik ve doğrulama kodu isteme",
            "Tek kullanımlık şifre, kart bilgisi veya kimlik numarası isteyen aramalar.",
            [
                "size gelen kodu",
                "sms ile gelen şifreyi",
                "kart numaranızın son",
                "cvv",
                "tc kimlik numaranızı",
                "internet bankacılığı şifreniz",
            ],
            MinimumMatches: 1),

        new(
            "Yatırım ve kripto baskısı",
            "Yüksek getiri vaadi ve acele ettirme birlikte geldiğinde tipik yatırım dolandırıcılığı deseni.",
            [
                "garantili getiri",
                "kaçırmayın",
                "sadece bugün",
                "kontenjan doluyor",
                "risksiz kazanç",
                "hemen yatırım yapmanız",
                "kripto",
                "borsada katlıyoruz",
            ],
            MinimumMatches: 3),

        new(
            "Sahte ödül ve borç bildirimi",
            "Kazanılmış ödül veya birikmiş borç bahanesiyle ödeme istenmesi.",
            [
                "kazandınız",
                "ödülünüzü almak için",
                "kargo ücreti yatırın",
                "borcunuz bulunmakta",
                "icra işlemi başlatılacak",
                "hattınız kapatılacak",
            ]),
    ];

    /// <summary>
    /// Scans a transcript for known scam scripts.
    ///
    /// Only the other party's speech is examined: the user quoting a scam back at someone, or
    /// warning a relative about one, must not flag their own call.
    /// </summary>
    public static IEnumerable<Flag> Scan(long callId, long? contactId, IReadOnlyList<Segment> segments)
    {
        var theirSegments = segments.Where(s => !s.IsMe).ToList();
        if (theirSegments.Count == 0) yield break;

        var normalised = theirSegments
            .Select(s => (segment: s, text: TurkishText.NormalizeForSearch(s.Text)))
            .ToList();

        foreach (var pattern in All)
        {
            List<(Segment segment, string phrase)> hits = [];

            foreach (var phrase in pattern.Phrases)
            {
                var needle = TurkishText.NormalizeForSearch(phrase);
                if (needle.Length == 0) continue;

                var match = normalised.FirstOrDefault(n => n.text.Contains(needle, StringComparison.Ordinal));
                if (match.segment is not null) hits.Add((match.segment, phrase));
            }

            if (hits.Count < pattern.MinimumMatches) continue;

            var first = hits[0].segment;

            yield return new Flag
            {
                CallId = callId,
                ContactId = contactId,
                Kind = FlagKind.ScamPattern,
                Summary = $"{pattern.Name}: {hits.Count} eşleşen ifade. {pattern.Explanation}",
                Quote = first.Text.Trim(),
                QuoteStartMs = first.StartMs,
                // Flagged as a keyword rule so it is never mistaken for a judgement by the model.
                IsHeuristic = true,
                LowConfidence = first.LowConfidence,
                CreatedAt = DateTimeOffset.UtcNow,
            };
        }
    }
}
