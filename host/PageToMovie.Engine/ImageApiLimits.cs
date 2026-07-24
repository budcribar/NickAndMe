namespace PageToMovie.Engine;

/// <summary>
/// Provider-specific caps for multi-reference image edit / portrait seeds.
/// Keep UI and API aligned so we never offer more seeds than the active backend accepts.
/// </summary>
public static class ImageApiLimits
{
    public const string ProviderGrok = "grok";
    public const string ProviderGemini = "gemini";

    /// <summary>xAI Grok Imagine image edits: up to 3 reference images per request.</summary>
    public const int GrokMaxReferenceImages = 3;

    /// <summary>
    /// Gemini 3 image models: up to 14 reference images.
    /// Older Flash image paths are lower — still use 14 as soft max for selection ranking;
    /// actual client may clamp further if needed.
    /// </summary>
    public const int GeminiMaxReferenceImages = 14;

    public const int DefaultMaxReferenceImages = GrokMaxReferenceImages;

    /// <summary>
    /// Resolve provider id from model catalog first, then explicit config / name heuristics.
    /// </summary>
    public static string ResolveProvider(string? imageProvider, string? imageModel)
    {
        // Master catalog (model id → provider family)
        var entry = PageToMovie.Core.Models.SupportedModelCatalog.Find(
            imageModel,
            PageToMovie.Core.Models.ModelCapability.Image);
        if (entry is not null)
            return entry.ProviderId;

        var p = (imageProvider ?? "").Trim().ToLowerInvariant();
        if (p is "grok" or "xai" or "x.ai")
            return ProviderGrok;
        if (p is "gemini" or "google" or "nano-banana" or "nanobanana")
            return ProviderGemini;

        var m = (imageModel ?? "").Trim().ToLowerInvariant();
        if (m.Contains("gemini", StringComparison.Ordinal) ||
            m.Contains("imagen", StringComparison.Ordinal) ||
            m.Contains("nano-banana", StringComparison.Ordinal) ||
            m.Contains("nanobanana", StringComparison.Ordinal))
            return ProviderGemini;

        if (m.Contains("grok", StringComparison.Ordinal) ||
            m.Contains("imagine", StringComparison.Ordinal))
            return ProviderGrok;

        return ProviderGrok; // host default today
    }

    /// <summary>Hard max reference images for multi-image edit on this provider.</summary>
    public static int MaxReferenceImages(string? imageProvider, string? imageModel)
    {
        return ResolveProvider(imageProvider, imageModel) switch
        {
            ProviderGemini => GeminiMaxReferenceImages,
            _ => GrokMaxReferenceImages,
        };
    }

    /// <summary>
    /// Clamp a requested max-refs to the active provider limit.
    /// </summary>
    public static int ClampMaxRefs(int requested, string? imageProvider, string? imageModel)
    {
        var cap = MaxReferenceImages(imageProvider, imageModel);
        if (requested <= 0)
            return cap;
        return Math.Clamp(requested, 1, cap);
    }
}
