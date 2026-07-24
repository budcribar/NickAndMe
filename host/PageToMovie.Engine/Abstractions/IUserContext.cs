namespace PageToMovie.Engine.Abstractions;

/// <summary>Current caller identity (HTTP request or job scope).</summary>
public interface IUserContext
{
    string UserId { get; }
    bool IsAdmin { get; }
    IReadOnlyList<string> Roles { get; }
    /// <summary>Optional per-request API key override (X-Api-Key header).</summary>
    string? RequestApiKey { get; }
}

/// <summary>Resolve xAI API key for a user (or default process key).</summary>
public interface IUserApiKeyProvider
{
    /// <summary>Returns API key for user, or null if none configured.</summary>
    string? GetKey(string? userId);

    /// <summary>Whether a non-empty key is available for this user (including process default).</summary>
    bool HasKey(string? userId);
}

/// <summary>Ambient API key for Grok HTTP calls (flows with AsyncLocal across job Task.Run).</summary>
public static class ApiKeyScope
{
    private static readonly AsyncLocal<string?> CurrentKey = new();

    public static string? Current => CurrentKey.Value;

    public static IDisposable Push(string? apiKey)
    {
        var prev = CurrentKey.Value;
        CurrentKey.Value = apiKey;
        return new Pop(prev);
    }

    private sealed class Pop : IDisposable
    {
        private readonly string? _prev;
        private bool _done;
        public Pop(string? prev) => _prev = prev;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            CurrentKey.Value = _prev;
        }
    }
}
