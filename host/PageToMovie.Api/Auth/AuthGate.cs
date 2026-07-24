using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Options;

namespace PageToMovie.Api.Auth;

/// <summary>
/// Shared 401/403 checks for project mutation, API-key settings, and OCR/import.
/// </summary>
public static class AuthGate
{
    /// <summary>401 unless JWT present (or <see cref="AuthOptions.RequireLogin"/> is false).</summary>
    public static IResult? RequireLogin(IUserContext user, IOptions<PageToMovieOptions> opts)
    {
        var auth = opts.Value.Auth ?? new AuthOptions();
        if (!auth.RequireLogin)
            return null;
        if (user.IsAuthenticated)
            return null;
        return Results.Json(
            new
            {
                ok = false,
                error = "Sign in required. Open /login or /signup, then try again.",
                code = "auth_required",
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Login + personal xAI/Grok key in the user DB (not merely server env).
    /// OCR / book import must use the signed-in user's key.
    /// </summary>
    public static IResult? RequirePersonalGrokKey(
        IUserContext user,
        UserDatabaseService userDb,
        IOptions<PageToMovieOptions> opts,
        bool useFakes = false)
    {
        var login = RequireLogin(user, opts);
        if (login is not null)
            return login;

        // Fakes mode (tests/loadsim) does not need a real xAI key.
        if (useFakes || opts.Value.UseFakes)
            return null;

        string? personal = null;
        try
        {
            personal = userDb.GetDecryptedXaiApiKeyAsync(user.UserId).GetAwaiter().GetResult();
        }
        catch
        {
            personal = null;
        }

        if (!string.IsNullOrWhiteSpace(personal))
            return null;

        return Results.Json(
            new
            {
                ok = false,
                error =
                    "Save your personal xAI / Grok API key in Configuration before book OCR or import. " +
                    "Server env keys alone are not enough for this action.",
                code = "personal_key_required",
            },
            statusCode: StatusCodes.Status403Forbidden);
    }
}
