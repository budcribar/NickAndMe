using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace PageToMovie.Api.Auth;

public interface IAdminAuthService
{
    LoginResponse Login(string username, string password);
    LoginResponse Signup(string username, string password);
    ClaimsPrincipal? ValidateToken(string token);
}

public sealed class AdminAuthService : IAdminAuthService
{
    private readonly AuthOptions _auth;
    private readonly IHostEnvironment _env;
    private readonly UserDatabaseService _userDb;
    private readonly PasswordHasher<object> _hasher = new();
    private readonly object _hashTarget = new();

    public AdminAuthService(IOptions<PageToMovieOptions> opts, IHostEnvironment env, UserDatabaseService userDb)
    {
        _auth = opts.Value.Auth ?? new AuthOptions();
        _env = env;
        _userDb = userDb;
    }

    public LoginResponse Signup(string username, string password)
    {
        username = (username ?? "").Trim();
        password = (password ?? "").Trim();

        if (username.Length < 3)
            return Fail("Username must be at least 3 characters long");
        if (password.Length < 4)
            return Fail("Password must be at least 4 characters long");

        var existing = _userDb.GetUserByUsernameAsync(username).GetAwaiter().GetResult();
        if (existing is not null)
            return Fail("Username is already taken");

        var user = new UserEntity
        {
            UserId = username.ToLowerInvariant(),
            Username = username,
            PasswordHash = UserDatabaseService.HashPassword(password),
            Role = AppRoles.User,
            CreatedAt = DateTime.UtcNow
        };

        _userDb.InsertUserAsync(user).GetAwaiter().GetResult();

        var hours = Math.Clamp(_auth.JwtHours, 1, 168);
        var expires = DateTimeOffset.UtcNow.AddHours(hours);
        var token = IssueJwt(user.Username, new[] { AppRoles.User }, expires);

        return new LoginResponse
        {
            Ok = true,
            Token = token,
            UserId = user.Username,
            Roles = new List<string> { AppRoles.User },
            ExpiresAt = expires,
        };
    }

    public LoginResponse Login(string username, string password)
    {
        username = (username ?? "").Trim();
        password ??= "";

        if (string.IsNullOrWhiteSpace(username))
            return Fail("Username is required");

        // 1. Check SQLite database for user
        var dbUser = _userDb.GetUserByUsernameAsync(username).GetAwaiter().GetResult();
        if (dbUser is not null)
        {
            var hash = UserDatabaseService.HashPassword(password);
            if (dbUser.PasswordHash == hash)
            {
                var userRoles = new List<string> { AppRoles.User };
                if (string.Equals(dbUser.Role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(username, _auth.AdminUsername, StringComparison.OrdinalIgnoreCase))
                {
                    userRoles.Add(AppRoles.Admin);
                }

                var userHours = Math.Clamp(_auth.JwtHours, 1, 168);
                var userExpires = DateTimeOffset.UtcNow.AddHours(userHours);
                var userToken = IssueJwt(dbUser.Username, userRoles, userExpires);

                return new LoginResponse
                {
                    Ok = true,
                    Token = userToken,
                    UserId = dbUser.Username,
                    Roles = userRoles,
                    ExpiresAt = userExpires,
                };
            }
            return Fail("Invalid username or password");
        }

        // 2. Fallback check for configured admin user (e.g. dev defaults admin/admin)
        if (string.Equals(username, _auth.AdminUsername, StringComparison.OrdinalIgnoreCase))
        {
            var ok = VerifyPassword(password);
            if (!ok)
                return Fail("Invalid username or password");

            var hours = Math.Clamp(_auth.JwtHours, 1, 168);
            var expires = DateTimeOffset.UtcNow.AddHours(hours);
            var token = IssueJwt(_auth.AdminUsername, new[] { AppRoles.User, AppRoles.Admin }, expires);

            return new LoginResponse
            {
                Ok = true,
                Token = token,
                UserId = _auth.AdminUsername,
                Roles = new List<string> { AppRoles.User, AppRoles.Admin },
                ExpiresAt = expires,
            };
        }

        return Fail("Invalid username or password");
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, TokenValidationParameters(), out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }

    private bool VerifyPassword(string password)
    {
        if (_auth.AllowDevBypass && _env.IsDevelopment())
            return true;

        var envPw = Environment.GetEnvironmentVariable("PageToMovie_ADMIN_PASSWORD");
        if (!string.IsNullOrEmpty(envPw) && password == envPw)
            return true;

        if (!string.IsNullOrWhiteSpace(_auth.AdminPasswordHash))
        {
            var r = _hasher.VerifyHashedPassword(_hashTarget, _auth.AdminPasswordHash, password);
            return r is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }

        if (!string.IsNullOrEmpty(_auth.AdminPassword))
            return password == _auth.AdminPassword;

        // No password configured: allow only in Development with empty password
        return _env.IsDevelopment() && password.Length == 0;
    }

    private string IssueJwt(string userId, IEnumerable<string> roles, DateTimeOffset expires)
    {
        var key = ResolveSigningKey();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
            new("sub", userId),
        };
        foreach (var r in roles.Distinct(StringComparer.OrdinalIgnoreCase))
            claims.Add(new Claim(ClaimTypes.Role, r));

        var token = new JwtSecurityToken(
            issuer: "PageToMovie.Api",
            audience: "PageToMovie",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private TokenValidationParameters TokenValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = "PageToMovie.Api",
        ValidateAudience = true,
        ValidAudience = "PageToMovie",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ResolveSigningKey())),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2),
    };

    private string ResolveSigningKey()
    {
        var env = Environment.GetEnvironmentVariable("PageToMovie_JWT_KEY")
                  ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_JWT_KEY")
                  ?? Environment.GetEnvironmentVariable("PageToMovie__Auth__JwtSigningKey")
                  ?? Environment.GetEnvironmentVariable("FILMSTUDIO_JWT_KEY");

        var key = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (_auth.JwtSigningKey ?? "");
        if (AuthOptions.IsInsecureDefaultJwtSigningKey(key) && !_env.IsDevelopment())
        {
            key = System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*", 64);
            _auth.JwtSigningKey = key;
        }
        if (key.Length < 32)
            key = (key + "PageToMovie-Pad-Key-To-32-Chars!!!!").PadRight(32)[..64];
        return key;
    }

    private static LoginResponse Fail(string error) => new() { Ok = false, Error = error };
}
