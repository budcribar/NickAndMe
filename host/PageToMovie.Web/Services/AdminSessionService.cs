using System.Text.Json;
using Microsoft.JSInterop;

namespace PageToMovie.Web.Services;

/// <summary>
/// Circuit-scoped admin identity. Also mirrors JWT into sessionStorage via JS
/// so a full page reload can restore the session.
/// </summary>
public sealed class AdminSessionService
{
    private const string StorageKey = "PageToMovie.admin.session";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IJSRuntime? _js;
    private bool _hydrated;

    public AdminSessionService(IJSRuntime? js = null) => _js = js;

    public string? Token { get; private set; }
    public string UserId { get; private set; } = "local";
    public IReadOnlyList<string> Roles { get; private set; } = Array.Empty<string>();
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);
    public bool IsLoggedIn => IsAuthenticated;
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

    /// <summary>
    /// Restore from sessionStorage. Safe only after interactive render (JS available).
    /// Never throws; never hangs longer than a quick JS call.
    /// </summary>
    public async Task EnsureHydratedAsync()
    {
        if (_hydrated)
            return;

        if (_js is null)
        {
            _hydrated = true;
            return;
        }

        try
        {
            var json = await _js.InvokeAsync<string?>("sessionStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var s = JsonSerializer.Deserialize<StoredSession>(json, JsonOpts);
                if (s is not null && !string.IsNullOrWhiteSpace(s.Token))
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
        }
        catch
        {
            // JS not ready or disabled
        }
        finally
        {
            _hydrated = true;
        }
    }

    private async Task PersistAsync()
    {
        if (_js is null || string.IsNullOrWhiteSpace(Token))
            return;
        try
        {
            var json = JsonSerializer.Serialize(new StoredSession
            {
                Token = Token,
                UserId = UserId,
                Roles = Roles.ToList(),
                ExpiresAt = ExpiresAt,
            }, JsonOpts);
            await _js.InvokeVoidAsync("sessionStorage.setItem", StorageKey, json);
        }
        catch
        {
            // ignore
        }
    }

    private async Task ClearPersistAsync()
    {
        if (_js is null) return;
        try { await _js.InvokeVoidAsync("sessionStorage.removeItem", StorageKey); }
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
