using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class UserDatabaseServiceTests
{
    [Fact]
    public async Task SaveXaiApiKeyAsync_encrypts_key_and_decrypts_per_user()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-user-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var service = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

        var testUserId = "user_alpha_123";
        var originalKey = "xai-test-key-998877665544332211";

        await service.SaveXaiApiKeyAsync(testUserId, originalKey);

        var decrypted = await service.GetDecryptedXaiApiKeyAsync(testUserId);
        Assert.Equal(originalKey, decrypted);

        var settings = await service.GetUserSettingsDtoAsync(testUserId);
        Assert.True(settings.HasXaiApiKey);
        Assert.NotNull(settings.MaskedXaiApiKey);
        Assert.Contains("...", settings.MaskedXaiApiKey);
        Assert.DoesNotContain("998877665544332211", settings.MaskedXaiApiKey);

        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public async Task DbUserApiKeyProvider_resolves_key_from_user_database()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-key-prov-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var userDb = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

        var testUserId = "user_beta_456";
        var userKey = "xai-beta-api-key-1234567890";
        await userDb.SaveXaiApiKeyAsync(testUserId, userKey);

        var provider = new DbUserApiKeyProvider(userDb, opts);
        var resolved = provider.GetKey(testUserId);

        Assert.Equal(userKey, resolved);

        try { Directory.Delete(tmp, true); } catch { }
    }
}
