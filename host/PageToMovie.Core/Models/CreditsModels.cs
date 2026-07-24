namespace PageToMovie.Core.Models;

/// <summary>
/// Credits are tracked in USD list-rate units (same as cost_ledger).
/// Display helper: 1 credit = $0.01 (one cent).
/// </summary>
public static class CreditUnits
{
    public const double UsdPerCredit = 0.01;

    public static int ToCredits(double usd) =>
        (int)Math.Round(Math.Max(0, usd) / UsdPerCredit, MidpointRounding.AwayFromZero);

    public static double FromCredits(int credits) => credits * UsdPerCredit;
}

public sealed class UserCreditSummaryDto
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Role { get; set; } = "User";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool HasXaiApiKey { get; set; }

    /// <summary>Remaining balance (USD list-rate).</summary>
    public double CreditsBalanceUsd { get; set; }

    /// <summary>Lifetime granted (USD).</summary>
    public double CreditsLifetimeGrantedUsd { get; set; }

    /// <summary>Lifetime spent (USD).</summary>
    public double CreditsLifetimeUsedUsd { get; set; }

    public int CreditsBalance => CreditUnits.ToCredits(CreditsBalanceUsd);
    public int CreditsLifetimeGranted => CreditUnits.ToCredits(CreditsLifetimeGrantedUsd);
    public int CreditsLifetimeUsed => CreditUnits.ToCredits(CreditsLifetimeUsedUsd);
}

public sealed class CreditLedgerEntryDto
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset Ts { get; set; }
    /// <summary>grant | debit | adjust | refund</summary>
    public string Kind { get; set; } = "";
    public double AmountUsd { get; set; }
    public double BalanceAfterUsd { get; set; }
    public string? ProjectId { get; set; }
    public string? Note { get; set; }
    public string? MetaKind { get; set; }
}

public sealed class AdminCreditsOverviewDto
{
    public int UserCount { get; set; }
    public double TotalBalanceUsd { get; set; }
    public double TotalGrantedUsd { get; set; }
    public double TotalUsedUsd { get; set; }
    public int TotalBalanceCredits => CreditUnits.ToCredits(TotalBalanceUsd);
    public int TotalGrantedCredits => CreditUnits.ToCredits(TotalGrantedUsd);
    public int TotalUsedCredits => CreditUnits.ToCredits(TotalUsedUsd);
    public List<UserCreditSummaryDto> Users { get; set; } = new();
    public List<CreditLedgerEntryDto> RecentLedger { get; set; } = new();
    /// <summary>1 credit = this many USD.</summary>
    public double UsdPerCredit { get; set; } = CreditUnits.UsdPerCredit;
    public string Notes { get; set; } =
        "Credits track estimated list-rate API spend (same basis as project cost ledger), not provider invoices. " +
        "1 credit = $0.01 USD. New accounts receive a signup grant; video/image gens debit the signed-in user.";
}

public sealed class AdminGrantCreditsRequest
{
    public string UserId { get; set; } = "";
    /// <summary>Positive USD to add (or negative to claw back).</summary>
    public double AmountUsd { get; set; }
    public string? Note { get; set; }
}
