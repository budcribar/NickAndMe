using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Routes <see cref="IVisionClient"/> calls to the right concrete provider client based on
/// the requested <c>model</c>'s provider in <see cref="SupportedModelCatalog"/> — callers
/// (clip auto-review, book-page OCR, cast classify) keep calling one <see cref="IVisionClient"/>
/// and never need to know which backend actually served the request.
///
/// <see cref="TranscribePageAsync"/> and <see cref="ClassifyCharactersOnImageAsync"/> are only
/// implemented on <see cref="GrokVisionClient"/> today — routing one of those to Anthropic or
/// Gemini surfaces the <see cref="NotSupportedException"/> those clients already throw for them,
/// rather than silently running it on the wrong provider. <see cref="CompleteWithImagesAsync"/>
/// (clip / frame review) is real on all three providers.
/// </summary>
public sealed class MultiProviderVisionClient : IVisionClient
{
    private readonly GrokVisionClient _grok;
    private readonly AnthropicChatClient _anthropic;
    private readonly GeminiChatClient _gemini;

    public MultiProviderVisionClient(
        GrokVisionClient grok,
        AnthropicChatClient anthropic,
        GeminiChatClient gemini)
    {
        _grok = grok;
        _anthropic = anthropic;
        _gemini = gemini;
    }

    /// <summary>True when at least one provider has an API key configured.</summary>
    public bool IsConfigured => _grok.IsConfigured || _anthropic.IsConfigured || _gemini.IsConfigured;

    public Task<string> TranscribePageAsync(
        string imagePath, int page, string model = "grok-4.5", CancellationToken ct = default) =>
        Resolve(model).TranscribePageAsync(imagePath, page, model, ct);

    public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
        string model = "grok-4.5", CancellationToken ct = default) =>
        Resolve(model).ClassifyCharactersOnImageAsync(imagePath, page, cast, model, ct);

    public Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "grok-4.5",
        string detail = "low",
        CancellationToken ct = default) =>
        Resolve(model).CompleteWithImagesAsync(prompt, imagePaths, model, detail, ct);

    private IVisionClient Resolve(string? model)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Vision).Provider;
        return provider switch
        {
            ModelProviderFamily.Anthropic => _anthropic,
            ModelProviderFamily.Google => _gemini,
            _ => _grok,
        };
    }
}
