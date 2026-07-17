using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace FilmStudio.Web.Services;

/// <summary>
/// Circuit-scoped admin identity. Persists JWT to sessionStorage so navigation
/// between per-page Interactive Server roots does not drop the login.
/// </summary>
public sealed class AdminSessionService
{
    private const string StorageKey = "filmstudio.admin.session";

    private readonly ProtectedSessionStorage? _store;
    private bool _hydrated;

    public AdminSessionService(ProtectedSessionStorage? store = null) => _store = store;

    public string? Token { get; private set; }
    public string UserId { get; private set; } = "local";
    public IReadOnlyList<string> Roles { get; private set; } = Array.Empty<string>();
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);
    public bool IsAdmin =>
        Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

    public event Action? Changed;

    public void SetUserId(string? userId)
    {
        UserId = string.IsNullOrWhiteSpace(userId) ? "local" : userId.Trim();
        Changed?.Invoke();
        _ = PersistAsync();
    }

    public void SetSession(string token, string? userId, IEnumerable<string>? roles, DateTimeOffset? expiresAt)
    {
        Token = token;
        UserId = string.IsNullOrWhiteSpace(userId) ? "local" : userId.Trim();
        Roles = roles?.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>();
        ExpiresAt = expiresAt;
        _hydrated = true;
        Changed?.Invoke();
        _ = PersistAsync();
    }

    public void Clear()
    {
        Token = null;
        UserId = "local";
        Roles = Array.Empty<string>();
        ExpiresAt = null;
        _hydrated = true;
        Changed?.Invoke();
        _ = ClearPersistAsync();
    }

    /// <summary>Load JWT from browser sessionStorage (call once per interactive page).</summary>
    public async Task EnsureHydratedAsync()
    {
        if (_hydrated || _store is null)
            return;

        try
        {
            var result = await _store.GetAsync<StoredSession>(StorageKey);
            if (result.Success && result.Value is { } s && !string.IsNullOrWhiteSpace(s.Token))
            {
                if (s.ExpiresAt is DateTimeOffset exp && exp < DateTimeOffset.UtcNow)
                {
                    await ClearPersistAsync();
                }
                else
                {
                    Token = s.Token;
                    UserId = string.IsNullOrWhiteSpace(s.UserId) ? "local" : s.UserId!;
                    Roles = s.Roles?.ToList() ?? new List<string>();
                    ExpiresAt = s.ExpiresAt;
                }
            }
        }
        catch
        {
            // Prerender / JS unavailable — ignore
        }
        finally
        {
            _hydrated = true;
        }
    }

    private async Task PersistAsync()
    {
        if (_store is null || string.IsNullOrWhiteSpace(Token))
            return;
        try
        {
            await _store.SetAsync(StorageKey, new StoredSession
            {
                Token = Token,
                UserId = UserId,
                Roles = Roles.ToList(),
                ExpiresAt = ExpiresAt,
            });
        }
        catch
        {
            // ignore
        }
    }

    private async Task ClearPersistAsync()
    {
        if (_store is null) return;
        try { await _store.DeleteAsync(StorageKey); }
        catch { /* ignore */ }
    }

    private sealed class StoredSession
    {
        public string? Token { get; set; }
        public string? UserId { get; set; }
        public List<string>? Roles { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
