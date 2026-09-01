using System.Text.RegularExpressions;

namespace VoiceTranscript.Core.Asr;

/// <summary>
/// Turns whatever a failed job left behind into one sentence a person can act on.
///
/// The worker is Python, so when it fails it fails with a traceback — twenty lines of file paths
/// and carets ending in the one line that matters. Putting that straight on screen was not a
/// cosmetic mistake: the first screen of the application became a wall of stack frames from
/// somebody else's library, and the actual fault ("a library is missing, and it can be
/// installed") was on the last line, below the fold, indistinguishable from the noise.
///
/// So the traceback is kept — it is the only thing that makes a real bug diagnosable — and it is
/// kept somewhere the user goes to look for it rather than somewhere it lands on them.
///
/// Recognised faults get a sentence in Turkish that says what to do. Everything else falls back
/// to the exception line, which is at least the sentence the failure was actually about.
/// </summary>
public static class FailureText
{
    /// <summary>Longest single-sentence summary shown before it is cut.</summary>
    private const int MaxLength = 200;

    private static readonly Regex ExceptionLine = new(
        @"^(?<type>[A-Za-z_][A-Za-z0-9_.]*(?:Error|Exception|Exit))\s*:\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The "message" field of a JSON error body, wherever it sits in the text.</summary>
    private static readonly Regex JsonMessage = new(
        "\"message\"\\s*:\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Known faults, and what somebody can actually do about each.
    ///
    /// Ordered, and the first match wins: an out-of-memory failure also mentions CUDA, and
    /// telling the user to install a library they already have would send them the wrong way.
    /// </summary>
    private static readonly (string Marker, string Sentence)[] Known =
    [
        ("out of memory",
            "Ekran kartının belleği yetmedi. Kurulum ekranından daha küçük bir model seçilebilir " +
            "ya da bu görüşme işlemcide çözümlenebilir."),

        ("cublas",
            "Ekran kartının hesaplama kütüphanesi (cuBLAS) yüklenemedi. Kurulum ve testler " +
            "ekranındaki \"Ekran kartı\" satırından kurulabilir — CUDA Toolkit gerekmez."),

        ("cudnn",
            "Eski bir cuDNN bağımlılığı aranıyor. Kurulum ve testler ekranından paketler " +
            "yeniden kurulmalı."),

        ("no kernel image",
            "Kurulu CUDA sürümü bu ekran kartını desteklemiyor. Ekran kartı sürücüsü " +
            "güncellenmeli."),

        ("device_invalidated",
            "Bilgisayar uyku moduna girdiği için ekran kartı bağlantısı koptu. Tekrar denemek yeterli."),

        ("automatic stream routing",
            "Ses yakalama başlatılamadı. Bu, giderilmiş bir hatanın eski kayıtlarda kalan izi — " +
            "yeni görüşmeler etkilenmez."),

        ("incompletesnapshoterror",
            "Model dosyaları eksik indirilmiş. Kurulum ekranından yeniden indirilmeli."),

        ("connection", "İnternet bağlantısı kurulamadı."),
        ("timed out", "İşlem zaman aşımına uğradı."),
    ];

    /// <summary>
    /// Addresses on this machine, where "internet" is the wrong diagnosis.
    ///
    /// A real failure read "İnternet bağlantısı kurulamadı" for a llama server at 127.0.0.1 —
    /// sending the user to check their Wi-Fi when the fix was starting a program. The generic
    /// connection sentence must never fire for a local address.
    /// </summary>
    private static bool MentionsLocalAddress(string lowered) =>
        lowered.Contains("127.0.0.1", StringComparison.Ordinal)
        || lowered.Contains("localhost", StringComparison.Ordinal)
        || lowered.Contains("[::1]", StringComparison.Ordinal);

    /// <summary>The one sentence to show. Never null, never a traceback.</summary>
    public static string Summarise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Sebep kaydedilmedi.";

        var lowered = raw.ToLowerInvariant();

        if (MentionsLocalAddress(lowered)
            && (lowered.Contains("connection", StringComparison.Ordinal)
                || lowered.Contains("ulaşılamadı", StringComparison.Ordinal)
                || lowered.Contains("refused", StringComparison.Ordinal)))
        {
            return "Yerel yapay zekâ sunucusu çalışmıyor. Sunucuyu başlatmak ya da Ayarlar'dan "
                 + "başka bir sağlayıcı seçmek gerekiyor — internetle ilgisi yok.";
        }

        foreach (var (marker, sentence) in Known)
            if (lowered.Contains(marker, StringComparison.Ordinal))
                return sentence;

        // An HTTP error body carried whole. The server already wrote the one sentence that
        // matters — its "message" field — and everything around it is JSON punctuation. Cutting
        // the raw body at the first line produced the memorable «OpenAi 400 döndürdü: {», which
        // is a hat with no head under it.
        var body = JsonMessage.Match(raw);
        if (body.Success)
        {
            var message = Regex.Unescape(body.Groups["value"].Value).Trim();
            var brace = raw.IndexOf('{');
            var prefix = brace > 0 ? raw[..brace].Trim() : "";

            // "OpenAi 400 döndürdü:" says who answered; keep it when it is a short heading
            // rather than a traceback.
            return prefix.Length is > 0 and <= 80 && !prefix.Contains('\n')
                ? Trim($"{prefix} {message}")
                : Trim(message);
        }

        // The last exception line: a traceback that re-raises ends with the fault that actually
        // stopped the job, and the earlier ones are the frames it passed through.
        var matches = ExceptionLine.Matches(raw);
        if (matches.Count > 0)
        {
            var last = matches[^1];
            return Trim($"{last.Groups["message"].Value.Trim()}");
        }

        // No traceback at all — a message written by this application rather than by Python.
        var lines = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0) return "Sebep kaydedilmedi.";

        // A first line ending in a colon is a heading, not the reason.
        //
        // "Yapılandırılmış servislerin hiçbiri yazıya dökemedi:" is true and useless on its own —
        // the sentence naming the actual fault is the one after it. Taking only the first line
        // showed the user a colon and nothing behind it, on the row whose whole job is to say
        // what went wrong.
        if (lines[0].EndsWith(':') && lines.Count > 1)
            return Trim($"{lines[0]} {lines[1]}");

        return Trim(lines[0]);
    }

    /// <summary>Whether there is more to see than the sentence — i.e. whether to offer details.</summary>
    public static bool HasDetail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;

        return raw.Contains('\n') || raw.Length > MaxLength;
    }

    /// <summary>
    /// Whether this "failure" is really the application giving directions.
    ///
    /// Some of what lands in the failure column was written by this application, for a person,
    /// pointing at a setting — "yapılandırılmış bir servis yok, Ayarlar bölümünden…". Painting
    /// that red inside a "Hatanın tamamı" expander taught a real user that the product was
    /// broken, when the message was the product being helpful. Guidance gets an info look and a
    /// button to the settings it names; red stays for things that actually broke.
    /// </summary>
    public static bool IsGuidance(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;

        foreach (var marker in new[]
        {
            "yapılandırılmış bir servis yok",
            "Ayarlar bölümünden",
            "Ayarlardan bir sağlayıcı",
            "çalışan bir yapay zekâ servisi",
            "Kullanıcı durdurdu",
        })
        {
            if (raw.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string Trim(string value)
    {
        value = value.Trim();

        if (value.Length <= MaxLength) return value;

        // Cut at a word rather than mid-syllable; a Turkish word broken in half reads as a typo.
        var cut = value.LastIndexOf(' ', MaxLength - 1);

        return string.Concat(value.AsSpan(0, cut > MaxLength / 2 ? cut : MaxLength - 1).TrimEnd(), "…");
    }
}
