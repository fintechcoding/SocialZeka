using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceTranscript.Core.Llm;

/// <summary>
/// Turns a refusal from an analysis provider into one sentence a person can act on.
///
/// The same job <see cref="Asr.FailureText"/> does for the transcription worker, for the other
/// half of the pipeline — and it was missing, so a busy service put this on screen:
///
///     OpenAi 503 döndürdü: {"error":{"message":"The model is currently overloaded with other
///     requests. You can retry your request...","type":"server_error","param":null,"code":null}}
///
/// Three things wrong with that, and only the first is cosmetic. It is somebody else's JSON in
/// the middle of a Turkish application. It buries the one word that matters — *overloaded* — in
/// punctuation. And it reads as a fault in this application, so the person who sees it goes
/// looking for a setting to change, when the correct response is to wait: the queue keeps the
/// call and tries again on its own.
///
/// Recognised refusals get a sentence naming what happened and what to do about it. Everything
/// else falls back to the provider's own message, extracted from the JSON — which is at least the
/// sentence the failure was about, without the envelope around it.
///
/// Deliberately does not name the model or the address. Those are already in the log line beside
/// this one, and repeating them makes the sentence longer than the thing it is explaining.
/// </summary>
public static class LlmFailureText
{
    /// <summary>Longest provider message kept before it is cut.</summary>
    private const int MaxLength = 180;

    /// <summary>
    /// One sentence for a failed request, from its status and whatever body came with it.
    /// </summary>
    public static string Describe(LlmProviderKind kind, int status, string? body)
    {
        var detail = MessageFrom(body);
        var lowered = (detail ?? "").ToLowerInvariant();

        // Busy is the common one and the only one where the right answer is to do nothing. Said
        // first because a 503 that also mentions a key would still, in practice, be a busy server.
        if (status is 429 or 503 || lowered.Contains("overload") || lowered.Contains("rate limit")
            || lowered.Contains("try again") || lowered.Contains("capacity"))
        {
            return "Çözümleme servisi şu an yoğun. Görüşme kuyrukta kalıyor, biraz sonra "
                   + "kendiliğinden yeniden denenecek — bir şey yapman gerekmiyor.";
        }

        if (status is 401 or 403)
        {
            return "Çözümleme servisi anahtarı kabul etmedi. Ayarlar → Çözümleme bölümünden "
                   + "API anahtarını denetle.";
        }

        // Money and quota, which look like an authentication problem and are not: the key is fine
        // and changing it would waste an evening.
        if (status == 402 || lowered.Contains("quota") || lowered.Contains("insufficient_quota")
            || lowered.Contains("billing") || lowered.Contains("credit"))
        {
            return "Çözümleme servisinin bakiyesi ya da kotası bitmiş görünüyor. Anahtar doğru; "
                   + "sağlayıcının hesabına bakman gerekiyor.";
        }

        // A model name that no longer exists is refused with a message that usually does not
        // mention the model, so this presents as "analysis stopped working" and stays unexplained.
        if (status == 404 || lowered.Contains("does not exist") || lowered.Contains("model_not_found")
            || lowered.Contains("unknown model"))
        {
            return "Çözümleme servisi bu model adını tanımıyor. Ayarlar → Çözümleme bölümünden "
                   + "model adını denetle; \"Modellere gözat\" ile listeden seçebilirsin.";
        }

        // The transcript outgrew the model's window. Actionable, and the action is not obvious.
        if (lowered.Contains("context length") || lowered.Contains("too many tokens")
            || lowered.Contains("maximum context"))
        {
            return "Görüşme metni bu modelin alabileceğinden uzun. Daha geniş bağlamlı bir model "
                   + "seç ya da görüşmeyi bölerek yeniden çözümle.";
        }

        if (status >= 500)
        {
            return "Çözümleme servisi hata verdi. Kendi tarafındaki bir arıza; görüşme duruyor, "
                   + "yeniden denenebilir.";
        }

        // Unrecognised. The provider's own sentence, without the JSON around it.
        return detail is { Length: > 0 }
            ? $"Çözümleme yapılamadı ({status}): {Truncate(detail)}"
            : $"Çözümleme yapılamadı ({status}).";
    }

    /// <summary>
    /// The human sentence inside an error body, wherever the provider chose to put it.
    ///
    /// Every one of these services answers with JSON and none of them agrees on the shape:
    /// OpenAI nests it under "error", Anthropic sometimes does and sometimes does not, and a
    /// proxy in front of either may return plain text. Parsed rather than pattern-matched so a
    /// body that is not JSON at all falls through to being used as-is.
    /// </summary>
    private static string? MessageFrom(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            var node = JsonNode.Parse(body);

            foreach (var candidate in new[] { node?["error"]?["message"], node?["message"], node?["detail"] })
                if (candidate?.GetValue<string>() is { Length: > 0 } found) return found.Trim();
        }
        catch (JsonException)
        {
            // Not JSON. A gateway's HTML page or a bare string; the text itself is the best we have.
        }

        return body.Trim();
    }

    private static string Truncate(string text) =>
        text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd() + "…";
}
