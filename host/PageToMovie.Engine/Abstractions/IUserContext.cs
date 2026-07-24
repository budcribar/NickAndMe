namespace PageToMovie.Engine.Abstractions;

/// <summary>Current caller identity (HTTP request or job scope).</summary>
public interface IUserContext
{
    string UserId { get; }
    bool IsAdmin { get; }
    IReadOnlyList<string> Roles { get; }
    /// <summary>Optional per-request API key override (X-Api-Key header) — treated as xAI/Grok.</summary>
    string? RequestApiKey { get; }
}

/// <summary>Resolve provider API keys for a user (personal DB key, then process env).</summary>
public interface IUserApiKeyProvider
{
    /// <summary>xAI / Grok key (back-compat). Prefer <see cref="GetKey(string?, string)"/>.</summary>
    string? GetKey(string? userId);

    /// <summary>
    /// Key for a provider id (<c>grok</c>/<c>xai</c>, <c>gemini</c>/<c>google</c>, <c>anthropic</c>/<c>claude</c>).
    /// </summary>
    string? GetKey(string? userId, string providerId);

    /// <summary>Whether a non-empty xAI key is available (including process default).</summary>
    bool HasKey(string? userId);

    bool HasKey(string? userId, string providerId);
}

/// <summary>
/// Ambient multi-provider API keys for HTTP clients (flows with AsyncLocal across job Task.Run).
/// <see cref="Current"/> remains the xAI/Grok key for existing Grok clients.
/// </summary>
public static class ApiKeyScope
{
    private static readonly AsyncLocal<ProviderKeys?> CurrentKeys = new();

    /// <summary>xAI / Grok ambient key (same as <see cref="Get"/>("grok")).</summary>
    public static string? Current => CurrentKeys.Value?.Xai;

    public static string? CurrentGemini => CurrentKeys.Value?.Gemini;

    public static string? CurrentAnthropic => CurrentKeys.Value?.Anthropic;

    public static string? Get(string providerId)
    {
        var k = CurrentKeys.Value;
        if (k is null) return null;
        return NormalizeProvider(providerId) switch
        {
            "grok" => k.Xai,
            "gemini" => k.Gemini,
            "anthropic" => k.Anthropic,
            _ => null,
        };
    }

    /// <summary>Push xAI-only key (legacy). Other provider slots stay null.</summary>
    public static IDisposable Push(string? xaiApiKey) =>
        Push(xaiApiKey, geminiApiKey: null, anthropicApiKey: null);

    public static IDisposable Push(string? xaiApiKey, string? geminiApiKey, string? anthropicApiKey)
    {
        var prev = CurrentKeys.Value;
        CurrentKeys.Value = new ProviderKeys(xaiApiKey, geminiApiKey, anthropicApiKey);
        return new Pop(prev);
    }

    public static string NormalizeProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return "";
        var p = providerId.Trim().ToLowerInvariant();
        return p switch
        {
            "xai" or "grok" => "grok",
            "google" or "gemini" => "gemini",
            "claude" or "anthropic" => "anthropic",
            _ => p,
        };
    }

    private sealed record ProviderKeys(string? Xai, string? Gemini, string? Anthropic);

    private sealed class Pop : IDisposable
    {
        private readonly ProviderKeys? _prev;
        private bool _done;
        public Pop(ProviderKeys? prev) => _prev = prev;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            CurrentKeys.Value = _prev;
        }
    }
}
