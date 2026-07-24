using System.Security.Claims;

namespace PageToMovie.Api.Auth;

/// <summary>
/// Accepts Authorization: Bearer JWT and populates HttpContext.User (admin or future users).
/// </summary>
public sealed class JwtHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public JwtHeaderMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, IAdminAuthService auth)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = header["Bearer ".Length..].Trim();
                var principal = auth.ValidateToken(token);
                if (principal is not null)
                    ctx.User = principal;
            }
        }

        await _next(ctx);
    }
}
