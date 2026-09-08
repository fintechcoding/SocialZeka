using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Core.Llm;

/// <summary>
/// How much of a conversation one request may carry, for the reads that take a call whole.
///
/// Three analyses hand the entire transcript to the model in one request — the reading, the
/// opt-in assessment and the conversation summary — and none of them can be chunked: a risk
/// point, a rewritten promise or "what was this call about" is a relation between the two ends
/// of the call, and half a conversation read as the whole is a different conversation. So the
/// only honest options are to fit, to read a window and say so, or to refuse and say why. This
/// is where the arithmetic for "fits" lives, once, so the three agree with each other and with
/// the consistency check that wrote the rule down first.
///
/// The size that matters is tokens, and the model's window is known when the model is one of
/// ours: <see cref="LocalLlmModel.ContextTokens"/> is the figure the VRAM arithmetic in the
/// catalogue already assumes. So the cap is derived from it — the window, less the answer, less
/// the instructions — and the flat character limits the consistency check chose are kept only
/// for a model the catalogue has never heard of (an Ollama tag, a file the user typed), where
/// nothing better is known.
///
/// A 45-minute call on a 16k-token model used to reach the server, overflow it, and come back
/// as a provider error about tokens — and the summary path swallowed the same error into a null,
/// so the user saw a broken button and no explanation. Now the number is computed before
/// anything is paid for.
/// </summary>
public sealed record PromptBudget(bool SendsDataOffMachine, int? ContextTokens)
{
    /// <summary>
    /// The flat caps, in characters, for a model whose window is not known. Exactly the
    /// consistency check's two numbers: cloud models hold multi-hour calls whole, and a local
    /// server of unknown size gets the figure that fits an 8k window at the estimate below.
    /// </summary>
    public const int CloudCharacterLimit = 400_000;

    public const int LocalCharacterLimit = 24_000;

    /// <summary>
    /// Characters per token, for Turkish transcript text handed to the model with speaker
    /// labels and timestamps.
    ///
    /// Measured on this machine's archive with the default model's tokenizer, bare Turkish runs
    /// at about 4.1 characters per token (69 minutes ≈ 65,853 characters ≈ 16k tokens). The
    /// prompt is not bare text: every line carries "[mm:ss] SEN: ", and digits and brackets
    /// tokenize at roughly two characters each, which drags a labelled line to about 3.5. Three
    /// is deliberately under that. Over-estimating the tokens costs a refusal a few minutes
    /// early on a borderline call; under-estimating sends a request the server cannot hold,
    /// which is the fault this exists to remove. The chunker uses four for the extraction, where
    /// a miss only costs recall — here a miss costs the whole request.
    /// </summary>
    public const int CharactersPerToken = 3;

    /// <summary>
    /// The budget for a request to <paramref name="model"/> through a provider that does or
    /// does not send text off the machine. A cloud model's window is not looked up: the
    /// catalogue only knows local files, and the flat cloud limit is generous by design.
    /// </summary>
    public static PromptBudget For(string model, bool sendsDataOffMachine) =>
        new(sendsDataOffMachine, sendsDataOffMachine ? null : LocalLlmCatalog.ContextTokensOf(model));

    /// <summary>Tokens a piece of text is assumed to cost. Rounded up, never down.</summary>
    public static int EstimateTokens(int characters) =>
        (Math.Max(0, characters) + CharactersPerToken - 1) / CharactersPerToken;

    public static int EstimateTokens(string text) => EstimateTokens(text.Length);

    /// <summary>
    /// The most characters the conversation itself may occupy in one request.
    ///
    /// With a known window: what is left of it once the answer has been reserved and the
    /// instructions and schema have taken their tokens, converted back to characters. Without
    /// one: the flat limit, compared against the conversation alone, exactly as the consistency
    /// check does.
    /// </summary>
    /// <param name="overheadCharacters">The system prompt and the schema, in characters.</param>
    /// <param name="answerTokens">The MaxTokens the request will ask for.</param>
    public int CharacterLimit(int overheadCharacters, int answerTokens)
    {
        if (ContextTokens is int context && context > 0)
        {
            var left = context - answerTokens - EstimateTokens(overheadCharacters);
            return Math.Max(0, left) * CharactersPerToken;
        }

        return SendsDataOffMachine ? CloudCharacterLimit : LocalCharacterLimit;
    }

    /// <summary>
    /// Null when the conversation fits; otherwise the sentence that refuses it — with the size,
    /// the limit, and what would change the answer — so a refusal is never mistaken for a
    /// broken button.
    /// </summary>
    public string? Refuse(string conversationPrompt, int overheadCharacters, int answerTokens)
    {
        var limit = CharacterLimit(overheadCharacters, answerTokens);

        return conversationPrompt.Length <= limit ? null : Refusal(conversationPrompt.Length, limit);
    }

    /// <summary>
    /// The refusal for a conversation of <paramref name="characters"/> against
    /// <paramref name="limit"/>. Thousands, as the consistency check words it: the exact count
    /// means nothing to the reader, and "47 bin karakter, en çok 39 bin" says why.
    /// </summary>
    public string Refusal(int characters, int limit) =>
        string.Format(
            Localisation.T(SendsDataOffMachine
                ? "promptbudget.bulut-sigmiyor"
                : "promptbudget.yerel-model-sigmiyor"),
            characters / 1000,
            limit / 1000);
}
