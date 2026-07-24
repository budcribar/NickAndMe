using System.Security.Claims;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;

namespace PageToMovie.Api.Auth;

/// <summary>
/// Resolves user from JWT claims, then X-User-Id, then default.
/// Scoped per request; jobs capture UserId/ApiKey at start via ApiKeyScope.
/// </summary>
public sealed class HttpUserContext : IUserContext
{
    private readonly IHttpContextAccessor _http;
    private readonly AuthOptions _auth;

    public HttpUserContext(IHttpContextAccessor http, IOptions<PageToMovieOptions> opts)
    {
        _http = http;
        _auth = opts.Value.Auth ?? new AuthOptions();
    }

    public string UserId
    {
        get
        {
            var ctx = _http.HttpContext;
            if (ctx?.User?.Identity?.IsAuthenticated == true)
            {
                var sub = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? ctx.User.FindFirstValue("sub")
                          ?? ctx.User.Identity?.Name;
                if (!string.IsNullOrWhiteSpace(sub))
                    return sub.Trim();
            }

            if (ctx?.Request.Headers.TryGetValue(AuthHeaders.UserId, out var h) == true &&
                !string.IsNullOrWhiteSpace(h))
                return h.ToString().Trim();

            return string.IsNullOrWhiteSpace(_auth.DefaultUserId) ? "local" : _auth.DefaultUserId.Trim();
        }
    }

    public IReadOnlyList<string> Roles
    {
        get
        {
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AppRoles.User };
            var ctx = _http.HttpContext;
            if (ctx?.User?.Identity?.IsAuthenticated == true)
            {
                foreach (var c in ctx.User.FindAll(ClaimTypes.Role))
                {
                    if (!string.IsNullOrWhiteSpace(c.Value))
                        roles.Add(c.Value.Trim());
                }
            }

            if (_auth.AdminUserIds.Any(id =>
                    string.Equals(id, UserId, StringComparison.OrdinalIgnoreCase)))
                roles.Add(AppRoles.Admin);

            return roles.ToList();
        }
    }

    public bool IsAdmin =>
        Roles.Any(r => string.Equals(r, AppRoles.Admin, StringComparison.OrdinalIgnoreCase));

    /// <summary>Validated JWT only — X-User-Id alone does not count as signed in.</summary>
    public bool IsAuthenticated =>
        _http.HttpContext?.User?.Identity?.IsAuthenticated == true;

    public string? RequestApiKey
    {
        get
        {
            var ctx = _http.HttpContext;
            if (ctx?.Request.Headers.TryGetValue(AuthHeaders.ApiKey, out var h) == true &&
                !string.IsNullOrWhiteSpace(h))
                return h.ToString().Trim();
            return null;
        }
    }
}

public sealed class DbUserApiKeyProvider : IUserApiKeyProvider
{
    private readonly UserDatabaseService _userDb;
    private readonly AuthOptions _auth;

    public DbUserApiKeyProvider(UserDatabaseService userDb, IOptions<PageToMovieOptions> opts)
    {
        _userDb = userDb;
        _auth = opts.Value.Auth ?? new AuthOptions();
    }

    public string? GetKey(string? userId) => GetKey(userId, "grok");

    public string? GetKey(string? userId, string providerId)
    {
        var provider = ApiKeyScope.NormalizeProvider(providerId);
        if (string.IsNullOrEmpty(provider))
            return null;

        // Request header override is xAI-only (legacy X-Api-Key).
        if (provider == "grok" && !string.IsNullOrWhiteSpace(userId))
        {
            // Fall through to DB / env below.
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                var dbKey = _userDb.GetDecryptedProviderApiKeyAsync(userId, provider).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(dbKey))
                    return dbKey.Trim();
            }
            catch { /* fallback */ }

            // Config map + USERKEY_* are xAI-only (historical).
            if (provider == "grok")
            {
                if (_auth.UserApiKeys.TryGetValue(userId, out var mapped) &&
                    !string.IsNullOrWhiteSpace(mapped))
                    return mapped.Trim();

                var envName = "USERKEY_" + userId.Trim().Replace('-', '_');
                var env = Environment.GetEnvironmentVariable(envName)
                          ?? Environment.GetEnvironmentVariable(envName.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(env))
                    return env.Trim();
            }
        }

        var processEnv = provider switch
        {
            "grok" => SupportedModelCatalog.XaiApiKeyEnv,
            "gemini" => SupportedModelCatalog.GoogleApiKeyEnv,
            "anthropic" => SupportedModelCatalog.AnthropicApiKeyEnv,
            _ => null,
        };
        if (processEnv is null) return null;
        var process = Environment.GetEnvironmentVariable(processEnv);
        return string.IsNullOrWhiteSpace(process) ? null : process.Trim();
    }

    public bool HasKey(string? userId) => !string.IsNullOrWhiteSpace(GetKey(userId));

    public bool HasKey(string? userId, string providerId) =>
        !string.IsNullOrWhiteSpace(GetKey(userId, providerId));
}
