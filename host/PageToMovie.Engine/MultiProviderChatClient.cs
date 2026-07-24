using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Routes <see cref="IChatClient.CompleteAsync"/> to the right concrete provider client
/// based on the requested <c>model</c>'s provider in <see cref="SupportedModelCatalog"/> —
/// callers (Stage 1/2 planning, cast scrub, QA) keep calling one <see cref="IChatClient"/>
/// and never need to know which backend actually served the request. Unknown / unrecognized
/// model ids fall back to Grok, matching <see cref="SupportedModelCatalog.ResolveOrDefault"/>'s
/// own forward-compatible default.
/// </summary>
public sealed class MultiProviderChatClient : IChatClient
{
    private readonly GrokChatClient _grok;
    private readonly AnthropicChatClient _anthropic;
    private readonly GeminiChatClient _gemini;

    public MultiProviderChatClient(
        GrokChatClient grok,
        AnthropicChatClient anthropic,
        GeminiChatClient gemini)
    {
        _grok = grok;
        _anthropic = anthropic;
        _gemini = gemini;
    }

    /// <summary>True when at least one provider has an API key configured.</summary>
    public bool IsConfigured => _grok.IsConfigured || _anthropic.IsConfigured || _gemini.IsConfigured;

    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null) =>
        Resolve(model).CompleteAsync(systemPrompt, userPrompt, model, temperature, ct, mode);

    private IChatClient Resolve(string? model)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Chat).Provider;
        return provider switch
        {
            ModelProviderFamily.Anthropic => _anthropic,
            ModelProviderFamily.Google => _gemini,
            _ => _grok,
        };
    }
}
