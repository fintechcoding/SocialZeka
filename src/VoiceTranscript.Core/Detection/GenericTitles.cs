using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Detection;

/// <summary>
/// Window titles that name the activity rather than the person.
///
/// WhatsApp's call window on a second screen is titled "Voice call"; Telegram's can read
/// "Telegram" while a chat is open; Windows adds "Incoming call" toasts. Bound to a contact once,
/// such a title then files every later call under that contact — which is exactly the
/// "her konuşmayı Uliana sanıyor" complaint. A title in this list is never bound, never resolved,
/// and never offered as a suggested name.
/// </summary>
public static class GenericTitles
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "whatsapp", "telegram", "signal", "whatsapp desktop", "telegram desktop",
        "voice call", "video call", "call", "incoming call", "outgoing call", "calling", "ringing",
        "sesli arama", "görüntülü arama", "arama", "gelen arama", "giden arama", "aranıyor", "çalıyor",
        "sesli görüşme", "görüntülü görüşme", "görüşme",
        "whatsapp voice call", "whatsapp video call", "whatsapp call",
        "telegram call", "telegram voice call", "telegram video call",
        "voice chat", "video chat", "sesli sohbet", "görüntülü sohbet",
    };

    public static bool IsGeneric(string? title)
    {
        var pattern = TurkishText.StripFormatting(title);
        if (pattern.Length == 0) return true;

        // "Voice call - WhatsApp" and "WhatsApp: Voice call" are the same nothing.
        var parts = pattern.Split(['-', '–', '—', ':', '|', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length > 0 && parts.All(p => Known.Contains(p));
    }
}
