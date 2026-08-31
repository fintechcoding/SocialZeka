using System.Text.RegularExpressions;

namespace VoiceTranscript.Core.Llm;

/// <summary>
/// Sorts a provider's catalogue into something a person can choose from.
///
/// OpenAI answers <c>/v1/models</c> with 126 entries on an ordinary account. Most of them are not
/// choices: dated snapshots of models already in the list, text-to-speech voices, embedding
/// models, moderation endpoints, image generators, coding variants. Handing that to somebody and
/// asking them to pick one for conversation analysis is not offering a choice, it is offering a
/// haystack — and the cost of picking wrong is a provider that rejects every request for reasons
/// that never mention the model.
///
/// Two passes, and the order matters:
///
///   <b>Drop what is not a candidate.</b> A dated duplicate of a model whose base name is already
///   present adds nothing; a speech synthesiser cannot analyse a transcript. This is where the
///   126 becomes something like 25.
///
///   <b>Lift the few worth trying first.</b> A short, hand-kept list per provider, intersected
///   with what the provider actually returned — so nothing invented ever appears, and a
///   recommendation that has been retired simply stops being shown.
///
/// Nothing is hidden that could conceivably be wanted: the full list stays available underneath.
/// A filter that hides things eventually hides the one model somebody needs.
/// </summary>
public static class ModelRecommendations
{
    /// <summary>A model worth trying first, and the reason in one clause.</summary>
    public sealed record Recommendation(string Id, string Reason);

    /// <summary>
    /// A dated snapshot: "gpt-5.5-2026-04-23", "claude-haiku-4-5-20251001".
    ///
    /// Pinning a date is a legitimate thing to want, so these are only dropped when the undated
    /// name is also on offer — that entry means the same model and keeps working.
    /// </summary>
    private static readonly Regex Dated = new(@"[-@](\d{4}-\d{2}-\d{2}|\d{8})$", RegexOptions.Compiled);

    /// <summary>
    /// Things that cannot do the job being chosen for.
    ///
    /// Kept as substrings rather than exact names because the list grows constantly and an exact
    /// list would need editing every time a provider ships a variant.
    /// </summary>
    private static readonly string[] NotForAnalysis =
    [
        "tts", "whisper", "transcribe", "embedding", "moderation", "dall-e", "image",
        "codex", "realtime", "audio-preview", "search-api", "search-preview", "computer-use",
    ];

    private static readonly string[] NotForTranscription =
    [
        "tts", "embedding", "moderation", "dall-e", "image", "codex", "chat", "search",
    ];

    /// <summary>
    /// Removes the entries that are not choices for this job.
    ///
    /// Falls back to the untouched list when filtering would empty it. A provider whose names
    /// happen to trip every rule must still be usable — an empty picker is a worse failure than a
    /// cluttered one.
    /// </summary>
    public static IReadOnlyList<RemoteModel> Winnow(IReadOnlyList<RemoteModel> models, bool forTranscription)
    {
        var names = models.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unwanted = forTranscription ? NotForTranscription : NotForAnalysis;

        var kept = models
            .Where(m => !IsRedundantSnapshot(m.Id, names))
            .Where(m => !unwanted.Any(bad => m.Id.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return kept.Count > 0 ? kept : models;
    }

    private static bool IsRedundantSnapshot(string id, HashSet<string> allNames)
    {
        var match = Dated.Match(id);
        if (!match.Success) return false;

        return allNames.Contains(id[..match.Index]);
    }

    /// <summary>
    /// The few worth trying first, in order, for one provider.
    ///
    /// Reasons are about shape and cost, not about benchmark scores. This project does not have
    /// measured Turkish figures for these, and a confident claim it cannot support would be the
    /// same fault as the invented accuracy numbers it refuses to print elsewhere.
    /// </summary>
    public static IReadOnlyList<Recommendation> For(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.OpenAi =>
        [
            new("gpt-5.5", "Güçlü genel model. Şemaya uyan çıktıda güvenilir."),
            new("gpt-5.4-mini", "Küçük ve ucuz. Çıkarım işi için çoğu zaman fazlasıyla yeterli."),
            new("gpt-5-mini", "Daha da ucuz; uzun arşivleri toplu işlerken fark birikir."),
            new("gpt-4.1-mini", "Eski kuşak, en ucuz seçeneklerden."),
        ],

        LlmProviderKind.Anthropic =>
        [
            new("claude-sonnet-4-5", "Dengeli seçim: Türkçe'de güçlü, maliyeti makul."),
            new("claude-haiku-4-5", "Hızlı ve ucuz. Çıkarım için genelde yeterli."),
            new("claude-opus-4-1", "En güçlüsü, en pahalısı. Zor konuşmalar için."),
        ],

        LlmProviderKind.OpenRouter =>
        [
            new("anthropic/claude-haiku-4.5", "Ucuz ve Türkçe'de iyi."),
            new("google/gemini-2.5-flash", "Çok ucuz, hızlı."),
            new("openai/gpt-5-mini", "OpenAI'ye kendi anahtarın olmadan erişim."),
            new("qwen/qwen3-235b-a22b-instruct", "Açık ağırlıklı, şemaya uyumu iyi."),
        ],

        _ => [],
    };

    /// <summary>
    /// Splits a fetched catalogue into "try these first" and "everything else".
    ///
    /// The recommended half is the intersection with what the provider actually returned, so a
    /// name that has been retired quietly disappears instead of being offered and rejected.
    /// </summary>
    public static (IReadOnlyList<RemoteModel> Recommended, IReadOnlyList<RemoteModel> Others) Split(
        IReadOnlyList<RemoteModel> models, LlmProviderKind kind)
    {
        var wanted = For(kind);
        if (wanted.Count == 0) return ([], models);

        var byId = models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        List<RemoteModel> recommended = [];

        foreach (var pick in wanted)
        {
            if (!byId.TryGetValue(pick.Id, out var model)) continue;

            // The reason replaces the provider's own description, which for these is usually the
            // identifier repeated back.
            recommended.Add(model with { Detail = pick.Reason, IsRecommended = true });
        }

        var chosen = recommended.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (recommended, [.. models.Where(m => !chosen.Contains(m.Id))]);
    }
}
