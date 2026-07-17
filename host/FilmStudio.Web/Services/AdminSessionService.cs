namespace FilmStudio.Web.Services;

/// <summary>Circuit-scoped identity: default user id + optional admin JWT (Phase B/D).</summary>
public sealed class AdminSessionService
{
    public string? Token { get; private set; }
    /// <summary>Effective user id for X-User-Id (defaults to local).</summary>
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
    }

    public void SetSession(string token, string? userId, IEnumerable<string>? roles, DateTimeOffset? expiresAt)
    {
        Token = token;
        UserId = string.IsNullOrWhiteSpace(userId) ? "local" : userId.Trim();
        Roles = roles?.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>();
        ExpiresAt = expiresAt;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Token = null;
        // Keep working as local operator after admin logout
        UserId = "local";
        Roles = Array.Empty<string>();
        ExpiresAt = null;
        Changed?.Invoke();
    }
}
