namespace PageToMovie.Core.Auth;

public static class AuthHeaders
{
    public const string UserId = "X-User-Id";
    public const string ApiKey = "X-Api-Key";
}

public static class AppRoles
{
    public const string User = "user";
    public const string Admin = "admin";
}

public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class LoginResponse
{
    public bool Ok { get; set; }
    public string? Token { get; set; }
    public string? UserId { get; set; }
    public List<string> Roles { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class MeResponse
{
    public bool Ok { get; set; }
    public string? UserId { get; set; }
    public List<string> Roles { get; set; } = new();
    public bool IsAdmin { get; set; }
    public bool HasApiKey { get; set; }
}
