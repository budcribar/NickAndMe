using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Routes <see cref="IVideoClient"/> calls to the right concrete provider client based on
/// the requested <c>model</c>'s provider in <see cref="SupportedModelCatalog"/>. Submit / poll /
/// download are three separate calls in this interface, and Grok's and Gemini's request ids
/// have different shapes (Grok: short opaque id; Gemini: an operation resource path) — rather
/// than keep a requestId→provider lookup table (extra state to go stale across restarts), this
/// tags the id itself with a small provider prefix on submit and strips it again on poll, so
/// routing stays correct with no server-side memory of in-flight jobs.
/// </summary>
public sealed class MultiProviderVideoClient : IVideoClient
{
    private const string GrokPrefix = "grok:";
    private const string GeminiPrefix = "gemini:";

    private readonly GrokVideoClient _grok;
    private readonly GeminiVideoClient _gemini;

    public MultiProviderVideoClient(GrokVideoClient grok, GeminiVideoClient gemini)
    {
        _grok = grok;
        _gemini = gemini;
    }

    /// <summary>True when at least one provider has an API key configured.</summary>
    public bool IsConfigured => _grok.IsConfigured || _gemini.IsConfigured;

    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null)
    {
        var provider = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).Provider;
        if (provider == ModelProviderFamily.Google)
        {
            var id = await _gemini.SubmitGenerationAsync(
                prompt, durationSeconds, resolution, model, ct,
                referenceImagePaths, startFrameImagePath, continueFromVideoPath).ConfigureAwait(false);
            return GeminiPrefix + id;
        }

        var grokId = await _grok.SubmitGenerationAsync(
            prompt, durationSeconds, resolution, model, ct,
            referenceImagePaths, startFrameImagePath, continueFromVideoPath).ConfigureAwait(false);
        return GrokPrefix + grokId;
    }

    public Task<string> PollForVideoUrlAsync(string requestId, Action<string>? onProgress, CancellationToken ct)
    {
        var (client, id) = Resolve(requestId);
        return client.PollForVideoUrlAsync(id, onProgress, ct);
    }

    public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        // Route by URL host — never fall back to the other provider on download failure
        // (wrong auth/host obscures the real error). Gemini downloads need the Google API key;
        // Grok media URLs are typically signed GETs without swapping auth stacks.
        var client = ResolveDownloadClient(url);
        return client.DownloadToFileAsync(url, destPath, ct);
    }

    /// <summary>
    /// Pick download client from media URL host. Unknown hosts: Grok if configured, else Gemini.
    /// No cross-provider retry.
    /// </summary>
    public IVideoClient ResolveDownloadClient(string url)
    {
        var inferred = InferProviderFromDownloadUrl(url);
        if (inferred == ModelProviderFamily.Google)
            return _gemini;
        if (inferred == ModelProviderFamily.Xai)
            return _grok;

        // CDN / opaque URL: prefer whichever client is configured (single attempt).
        if (_grok.IsConfigured)
            return _grok;
        if (_gemini.IsConfigured)
            return _gemini;
        return _grok;
    }

    /// <summary>
    /// Map absolute media URL host → provider. Null when the host is not recognized
    /// (public so tests can cover routing without a full HTTP stack).
    /// </summary>
    public static ModelProviderFamily? InferProviderFromDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;
        if (host.Contains("googleapis", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("googleusercontent", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("google.com", StringComparison.OrdinalIgnoreCase))
            return ModelProviderFamily.Google;

        if (host.Equals("api.x.ai", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".x.ai", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("x.ai", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("xai", StringComparison.OrdinalIgnoreCase))
            return ModelProviderFamily.Xai;

        return null;
    }

    private (IVideoClient Client, string Id) Resolve(string requestId)
    {
        var (provider, id) = ParseTaggedRequestId(requestId);
        return provider == ModelProviderFamily.Google ? (_gemini, id) : (_grok, id);
    }

    /// <summary>
    /// Splits a dispatcher-tagged request id back into (provider, original id). Untagged ids
    /// (e.g. held by a caller from before this dispatcher existed) are treated as Grok's, since
    /// Grok ids never contained a colon prefix. Public so tests can exercise the tagging
    /// round-trip without constructing the full client graph.
    /// </summary>
    public static (ModelProviderFamily Provider, string Id) ParseTaggedRequestId(string requestId)
    {
        if (requestId.StartsWith(GeminiPrefix, StringComparison.Ordinal))
            return (ModelProviderFamily.Google, requestId[GeminiPrefix.Length..]);
        if (requestId.StartsWith(GrokPrefix, StringComparison.Ordinal))
            return (ModelProviderFamily.Xai, requestId[GrokPrefix.Length..]);
        return (ModelProviderFamily.Xai, requestId);
    }
}
