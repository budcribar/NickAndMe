using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Tracks per-user list-rate credits (1 credit = $0.01 USD).
/// Balance is debited when video/image costs are recorded; grants on signup and via admin.
/// Balance may go negative so jobs are never blocked mid-run — admin can top up.
/// </summary>
public sealed class CreditService
{
    private readonly UserDatabaseService _db;
    private readonly AuthOptions _auth;
    private readonly ILogger<CreditService> _log;

    public CreditService(
        UserDatabaseService db,
        IOptions<PageToMovieOptions> opts,
        ILogger<CreditService>? log = null)
    {
        _db = db;
        _auth = opts.Value.Auth ?? new AuthOptions();
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CreditService>.Instance;
    }

    public double SignupGrantUsd => Math.Max(0, _auth.DefaultSignupCreditsUsd);

    /// <summary>Grant the configured signup amount (no-op if already has any grant or amount is 0).</summary>
    public async Task GrantSignupCreditsAsync(string userId, CancellationToken ct = default)
    {
        var amount = SignupGrantUsd;
        if (amount <= 0 || string.IsNullOrWhiteSpace(userId))
            return;

        try
        {
            var user = await _db.ResolveUserAsync(userId, ct).ConfigureAwait(false);
            if (user is null)
            {
                _log.LogWarning("Signup credit grant skipped — user {UserId} not found", userId);
                return;
            }

            // Only auto-grant once (lifetime granted still 0).
            if (user.CreditsLifetimeGrantedUsd > 0.0001)
                return;

            await _db.ApplyCreditDeltaAsync(
                user.UserId,
                amountUsd: amount,
                kind: "grant",
                note: "Signup grant",
                metaKind: "signup",
                projectId: null,
                ct: ct).ConfigureAwait(false);

            _log.LogInformation(
                "Granted {Usd:F2} USD ({Credits} credits) signup grant to {UserId}",
                amount, CreditUnits.ToCredits(amount), user.UserId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Signup credit grant failed for {UserId}", userId);
        }
    }

    /// <summary>Admin grant (positive) or claw-back (negative).</summary>
    public Task<UserCreditSummaryDto?> GrantAsync(
        string userId,
        double amountUsd,
        string? note = null,
        CancellationToken ct = default) =>
        _db.ApplyCreditDeltaAsync(
            userId,
            amountUsd,
            kind: amountUsd >= 0 ? "grant" : "adjust",
            note: note ?? (amountUsd >= 0 ? "Admin grant" : "Admin adjust"),
            metaKind: "admin",
            projectId: null,
            ct: ct);

    /// <summary>
    /// Debit list-rate spend for a user. No-op when userId empty or amount ≤ 0.
    /// Never throws — cost tracking must not break generation.
    /// </summary>
    public async Task TryDebitUsageAsync(
        string? userId,
        double amountUsd,
        string? projectId,
        string metaKind,
        string? note = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || amountUsd <= 0)
            return;

        try
        {
            await _db.ApplyCreditDeltaAsync(
                userId,
                amountUsd: -Math.Abs(amountUsd),
                kind: "debit",
                note: note,
                metaKind: metaKind,
                projectId: projectId,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Credit debit failed for {UserId} ${Usd:F4} ({Meta})",
                userId, amountUsd, metaKind);
        }
    }

    public Task<AdminCreditsOverviewDto> GetAdminOverviewAsync(int recentLedger = 40, CancellationToken ct = default) =>
        _db.GetAdminCreditsOverviewAsync(recentLedger, ct);

    public Task<UserCreditSummaryDto?> GetUserSummaryAsync(string userId, CancellationToken ct = default) =>
        _db.GetUserCreditSummaryAsync(userId, ct);
}
