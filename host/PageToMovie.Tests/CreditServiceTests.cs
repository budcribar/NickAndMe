using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class CreditServiceTests
{
    [Fact]
    public async Task Grant_and_debit_update_balance_and_ledger()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-credits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var opts = Options.Create(new PageToMovieOptions
            {
                WorkspaceRoot = tmp,
                Auth = new AuthOptions { DefaultSignupCreditsUsd = 5.0 },
            });
            var db = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);
            var credits = new CreditService(db, opts, NullLogger<CreditService>.Instance);

            await db.InsertUserAsync(new UserEntity
            {
                UserId = "alice",
                Username = "Alice",
                PasswordHash = UserDatabaseService.HashPassword("x"),
                Role = "User",
                CreatedAt = DateTime.UtcNow,
            });

            await credits.GrantSignupCreditsAsync("alice");
            var afterSignup = await credits.GetUserSummaryAsync("alice");
            Assert.NotNull(afterSignup);
            Assert.Equal(5.0, afterSignup!.CreditsBalanceUsd, 3);
            Assert.Equal(5.0, afterSignup.CreditsLifetimeGrantedUsd, 3);
            Assert.Equal(500, afterSignup.CreditsBalance);

            // Second signup grant is a no-op.
            await credits.GrantSignupCreditsAsync("alice");
            afterSignup = await credits.GetUserSummaryAsync("alice");
            Assert.Equal(5.0, afterSignup!.CreditsBalanceUsd, 3);

            await credits.TryDebitUsageAsync("alice", 1.25, "proj1", "video", "S01C1");
            var afterDebit = await credits.GetUserSummaryAsync("alice");
            Assert.NotNull(afterDebit);
            Assert.Equal(3.75, afterDebit!.CreditsBalanceUsd, 3);
            Assert.Equal(1.25, afterDebit.CreditsLifetimeUsedUsd, 3);
            Assert.Equal(5.0, afterDebit.CreditsLifetimeGrantedUsd, 3);

            var overview = await credits.GetAdminOverviewAsync();
            Assert.Equal(1, overview.UserCount);
            Assert.Equal(3.75, overview.TotalBalanceUsd, 3);
            Assert.Equal(1.25, overview.TotalUsedUsd, 3);
            Assert.True(overview.RecentLedger.Count >= 2);

            var granted = await credits.GrantAsync("alice", 2.0, "admin top-up");
            Assert.NotNull(granted);
            Assert.Equal(5.75, granted!.CreditsBalanceUsd, 3);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void CreditUnits_convert_usd_and_credits()
    {
        Assert.Equal(100, CreditUnits.ToCredits(1.0));
        Assert.Equal(1.0, CreditUnits.FromCredits(100), 4);
        Assert.Equal(500, CreditUnits.ToCredits(5.0));
    }
}
