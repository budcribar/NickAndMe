using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Auth;
using PageToMovie.Core.Models;
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
        Assert.Contains(settings.Providers, p => p.ProviderId == "grok" && p.HasPersonalKey);

        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public async Task UpdateUserSettingsAsync_saves_multiple_provider_keys_independently()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-user-db-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var service = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);
        var userId = "user_multi_1";

        await service.UpdateUserSettingsAsync(userId, new UpdateUserSettingsRequest
        {
            XaiApiKey = "xai-key-aaaa1111bbbb",
            GeminiApiKey = "AIza-gemini-key-2222",
        });

        Assert.Equal("xai-key-aaaa1111bbbb", await service.GetDecryptedProviderApiKeyAsync(userId, "grok"));
        Assert.Equal("AIza-gemini-key-2222", await service.GetDecryptedProviderApiKeyAsync(userId, "gemini"));
        Assert.Null(await service.GetDecryptedProviderApiKeyAsync(userId, "anthropic"));

        // Null fields leave existing keys alone.
        await service.UpdateUserSettingsAsync(userId, new UpdateUserSettingsRequest
        {
            AnthropicApiKey = "sk-ant-claude-3333",
        });
        Assert.Equal("xai-key-aaaa1111bbbb", await service.GetDecryptedProviderApiKeyAsync(userId, "grok"));
        Assert.Equal("sk-ant-claude-3333", await service.GetDecryptedProviderApiKeyAsync(userId, "anthropic"));

        // Empty string clears that provider only.
        await service.UpdateUserSettingsAsync(userId, new UpdateUserSettingsRequest
        {
            GeminiApiKey = "",
        });
        Assert.Null(await service.GetDecryptedProviderApiKeyAsync(userId, "gemini"));
        Assert.Equal("xai-key-aaaa1111bbbb", await service.GetDecryptedProviderApiKeyAsync(userId, "grok"));

        var settings = await service.GetUserSettingsDtoAsync(userId);
        Assert.True(settings.HasXaiApiKey);
        Assert.False(settings.HasGeminiApiKey);
        Assert.True(settings.HasAnthropicApiKey);
        Assert.Equal(3, settings.Providers.Count);
        Assert.Contains(settings.Providers, p => p.ProviderId == "anthropic" && !p.SupportsVideo);

        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public async Task DbUserApiKeyProvider_resolves_keys_per_provider()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ptm-key-prov-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = tmp });
        var userDb = new UserDatabaseService(opts, null, NullLogger<UserDatabaseService>.Instance);

        var testUserId = "user_beta_456";
        await userDb.UpdateUserSettingsAsync(testUserId, new UpdateUserSettingsRequest
        {
            XaiApiKey = "xai-beta-api-key-1234567890",
            GeminiApiKey = "gemini-beta-key-zzzz",
        });

        var provider = new DbUserApiKeyProvider(userDb, opts);
        Assert.Equal("xai-beta-api-key-1234567890", provider.GetKey(testUserId));
        Assert.Equal("xai-beta-api-key-1234567890", provider.GetKey(testUserId, "grok"));
        Assert.Equal("gemini-beta-key-zzzz", provider.GetKey(testUserId, "gemini"));
        Assert.True(provider.HasKey(testUserId, "gemini"));
        // Anthropic has no personal key for this user; HasKey may still be true if server env is set.
        Assert.Null(await userDb.GetDecryptedProviderApiKeyAsync(testUserId, "anthropic"));

        try { Directory.Delete(tmp, true); } catch { }
    }
}
