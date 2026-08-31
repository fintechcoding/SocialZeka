using System.Net.Http;

namespace VoiceTranscript.Core.Llm;

/// <summary>
/// Builds the client that speaks the chosen provider's protocol.
///
/// Worth existing as one function rather than as a <c>new</c> at each call site. Every place that
/// wants to ask a model something — the orchestrator, the question panel, the call window, the
/// status screen — used to construct <see cref="OpenAiCompatibleClient"/> directly, which was
/// correct only while every provider spoke that one protocol. The moment one of them does not,
/// each of those sites is a separate opportunity to send an Anthropic request to an OpenAI-shaped
/// endpoint and get back a rejection that mentions neither.
/// </summary>
public static class LlmClientFactory
{
    /// <summary>Creates a client for one provider.</summary>
    public static ILlmClient Create(
        HttpClient http, LlmProviderKind kind, string baseUrl, string? apiKey = null) =>
        kind switch
        {
            LlmProviderKind.Anthropic => new AnthropicClient(http, baseUrl, apiKey),

            // Everything else — llama-server, Ollama, LM Studio, OpenRouter, OpenAI and anything
            // claiming compatibility — is chat-completions, and one implementation covers them.
            _ => new OpenAiCompatibleClient(http, kind, baseUrl, apiKey),
        };
}
