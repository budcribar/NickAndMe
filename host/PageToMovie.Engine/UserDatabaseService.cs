using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// SQLite user database service for PageToMovie (pagetomovie.db).
/// Manages user authentication, account settings, WAL mode concurrency pragmas,
/// and AES-256 encryption at rest for per-user provider API keys (xAI, Gemini, Anthropic).
/// </summary>
public class UserDatabaseService
{
    private readonly string _dbPath;
    private readonly IDataProtector? _protector;
    private readonly ILogger<UserDatabaseService> _logger;
    private readonly object _initLock = new();
    private bool _initialized;

    public UserDatabaseService(
        IOptions<PageToMovieOptions> options,
        IDataProtectionProvider? dataProtection = null,
        ILogger<UserDatabaseService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UserDatabaseService>.Instance;
        _protector = dataProtection?.CreateProtector("PageToMovie.UserApiKeys");

        var workspace = options?.Value?.WorkspaceRoot;
        var dataDir = Directory.Exists("/data")
            ? "/data"
            : Directory.Exists("/app/data")
                ? "/app/data"
                : !string.IsNullOrWhiteSpace(workspace)
                    ? Path.Combine(workspace, "data")
                    : Path.Combine(Path.GetTempPath(), "PageToMovie", "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "pagetomovie.db");

        EnsureDatabaseInitialized();
    }

    private string ConnectionString => $"Data Source={_dbPath};Cache=Shared;";

    /// <summary>
    /// Ensures SQLite database and users table exist with WAL mode pragmas enabled.
    /// </summary>
    public void EnsureDatabaseInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            try
            {
                using var conn = new SqliteConnection(ConnectionString);
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        PRAGMA journal_mode = WAL;
                        PRAGMA busy_timeout = 5000;
                        PRAGMA synchronous = NORMAL;
                    ";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS users (
                            user_id TEXT PRIMARY KEY,
                            username TEXT NOT NULL UNIQUE,
                            password_hash TEXT NOT NULL,
                            encrypted_xai_api_key TEXT,
                            encrypted_gemini_api_key TEXT,
                            encrypted_anthropic_api_key TEXT,
                            role TEXT NOT NULL DEFAULT 'User',
                            created_at TEXT NOT NULL,
                            last_login_at TEXT
                        );
                    ";
                    cmd.ExecuteNonQuery();
                }

                // Migrate older DBs that only had the xAI column.
                EnsureColumn(conn, "users", "encrypted_gemini_api_key", "TEXT");
                EnsureColumn(conn, "users", "encrypted_anthropic_api_key", "TEXT");

                _initialized = true;
                _logger.LogInformation("SQLite database initialized at {DbPath} (WAL mode enabled)", _dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SQLite database at {DbPath}", _dbPath);
                throw;
            }
        }
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string typeSql)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql}";
        alter.ExecuteNonQuery();
    }

    public async Task<UserEntity?> GetUserByIdAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, password_hash,
                   encrypted_xai_api_key, encrypted_gemini_api_key, encrypted_anthropic_api_key,
                   role, created_at, last_login_at
            FROM users WHERE user_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", userId.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ReadUserFromReader(reader);

        return null;
    }

    public async Task<UserEntity?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT user_id, username, password_hash,
                   encrypted_xai_api_key, encrypted_gemini_api_key, encrypted_anthropic_api_key,
                   role, created_at, last_login_at
            FROM users WHERE LOWER(username) = LOWER(@name) LIMIT 1";
        cmd.Parameters.AddWithValue("@name", username.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ReadUserFromReader(reader);

        return null;
    }

    /// <summary>Saves or updates a user's encrypted xAI API key in SQLite.</summary>
    public Task SaveXaiApiKeyAsync(string userId, string? apiKey, CancellationToken ct = default) =>
        SaveProviderApiKeyAsync(userId, "grok", apiKey, ct);

    /// <summary>
    /// Saves a personal provider key. Empty/whitespace clears the stored key.
    /// Provider: grok/xai, gemini/google, anthropic/claude.
    /// </summary>
    public async Task SaveProviderApiKeyAsync(string userId, string providerId, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        var column = ProviderColumn(providerId);
        if (column is null) return;

        var encrypted = string.IsNullOrWhiteSpace(apiKey) ? null : EncryptApiKey(apiKey.Trim());

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE users SET {column} = @key WHERE user_id = @id";
        cmd.Parameters.AddWithValue("@key", (object?)encrypted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", userId.Trim());

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
        {
            var user = new UserEntity
            {
                UserId = userId.Trim(),
                Username = userId.Trim(),
                PasswordHash = HashPassword("dev-placeholder"),
                Role = "User",
                CreatedAt = DateTime.UtcNow,
            };
            SetEncryptedOnEntity(user, providerId, encrypted);
            await InsertUserAsync(user, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies non-null fields from the request. Empty string clears that personal key;
    /// null leaves the existing key unchanged.
    /// </summary>
    public async Task UpdateUserSettingsAsync(string userId, UpdateUserSettingsRequest req, CancellationToken ct = default)
    {
        if (req.XaiApiKey is not null)
            await SaveProviderApiKeyAsync(userId, "grok", req.XaiApiKey, ct).ConfigureAwait(false);
        if (req.GeminiApiKey is not null)
            await SaveProviderApiKeyAsync(userId, "gemini", req.GeminiApiKey, ct).ConfigureAwait(false);
        if (req.AnthropicApiKey is not null)
            await SaveProviderApiKeyAsync(userId, "anthropic", req.AnthropicApiKey, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetDecryptedXaiApiKeyAsync(string userId, CancellationToken ct = default) =>
        await GetDecryptedProviderApiKeyAsync(userId, "grok", ct).ConfigureAwait(false);

    public async Task<string?> GetDecryptedProviderApiKeyAsync(string userId, string providerId, CancellationToken ct = default)
    {
        var user = await GetUserByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return null;

        var encrypted = GetEncryptedFromEntity(user, providerId);
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        return DecryptApiKey(encrypted);
    }

    public async Task<UserSettingsDto> GetUserSettingsDtoAsync(string userId, CancellationToken ct = default)
    {
        var user = await GetUserByIdAsync(userId, ct).ConfigureAwait(false);
        var username = user?.Username ?? userId;

        var xaiPersonal = DecryptOptional(user?.EncryptedXaiApiKey);
        var geminiPersonal = DecryptOptional(user?.EncryptedGeminiApiKey);
        var anthropicPersonal = DecryptOptional(user?.EncryptedAnthropicApiKey);

        var xaiServer = EnvPresent(SupportedModelCatalog.XaiApiKeyEnv);
        var geminiServer = EnvPresent(SupportedModelCatalog.GoogleApiKeyEnv);
        var anthropicServer = EnvPresent(SupportedModelCatalog.AnthropicApiKeyEnv);

        var providers = new List<ProviderKeyStatusDto>
        {
            BuildProviderStatus(
                providerId: "grok",
                displayName: "xAI / Grok",
                family: "Xai",
                personal: xaiPersonal,
                hasServer: xaiServer,
                supportsVideo: true,
                supportsImage: true,
                supportsChat: true,
                supportsVision: true,
                notes: "Full pipeline: video (with clip continue + cast plates), portraits, planning, OCR, and frame review."),
            BuildProviderStatus(
                providerId: "gemini",
                displayName: "Google Gemini",
                family: "Google",
                personal: geminiPersonal,
                hasServer: geminiServer,
                supportsVideo: true,
                supportsImage: true,
                supportsChat: true,
                supportsVision: true,
                notes: "Video via Veo (text/image-to-video only — no clip continue or multi-ref cast plates). OCR/cast classify stay on Grok."),
            BuildProviderStatus(
                providerId: "anthropic",
                displayName: "Anthropic Claude",
                family: "Anthropic",
                personal: anthropicPersonal,
                hasServer: anthropicServer,
                supportsVideo: false,
                supportsImage: false,
                supportsChat: true,
                supportsVision: true,
                notes: "Chat + clip/frame review only. No video or image generation — use Grok or Gemini for those."),
        };

        return new UserSettingsDto
        {
            UserId = user?.UserId ?? userId,
            Username = username,
            HasXaiApiKey = !string.IsNullOrWhiteSpace(xaiPersonal),
            MaskedXaiApiKey = MaskKey(xaiPersonal),
            HasGeminiApiKey = !string.IsNullOrWhiteSpace(geminiPersonal),
            MaskedGeminiApiKey = MaskKey(geminiPersonal),
            HasAnthropicApiKey = !string.IsNullOrWhiteSpace(anthropicPersonal),
            MaskedAnthropicApiKey = MaskKey(anthropicPersonal),
            Providers = providers,
        };
    }

    public async Task InsertUserAsync(UserEntity user, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO users (
                user_id, username, password_hash,
                encrypted_xai_api_key, encrypted_gemini_api_key, encrypted_anthropic_api_key,
                role, created_at, last_login_at)
            VALUES (@id, @name, @hash, @xai, @gemini, @anthropic, @role, @created, @login)
            ON CONFLICT(user_id) DO UPDATE SET
                username = excluded.username,
                encrypted_xai_api_key = COALESCE(excluded.encrypted_xai_api_key, users.encrypted_xai_api_key),
                encrypted_gemini_api_key = COALESCE(excluded.encrypted_gemini_api_key, users.encrypted_gemini_api_key),
                encrypted_anthropic_api_key = COALESCE(excluded.encrypted_anthropic_api_key, users.encrypted_anthropic_api_key);
        ";
        cmd.Parameters.AddWithValue("@id", user.UserId);
        cmd.Parameters.AddWithValue("@name", user.Username);
        cmd.Parameters.AddWithValue("@hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("@xai", (object?)user.EncryptedXaiApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gemini", (object?)user.EncryptedGeminiApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@anthropic", (object?)user.EncryptedAnthropicApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", user.Role);
        cmd.Parameters.AddWithValue("@created", user.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@login", (object?)user.LastLoginAt?.ToString("o") ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private string? DecryptOptional(string? encrypted) =>
        string.IsNullOrWhiteSpace(encrypted) ? null : DecryptApiKey(encrypted);

    private static bool EnvPresent(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key.Length > 8)
            return key.Substring(0, 4) + "..." + key.Substring(key.Length - 4);
        return "****";
    }

    private static ProviderKeyStatusDto BuildProviderStatus(
        string providerId,
        string displayName,
        string family,
        string? personal,
        bool hasServer,
        bool supportsVideo,
        bool supportsImage,
        bool supportsChat,
        bool supportsVision,
        string? notes)
    {
        var hasPersonal = !string.IsNullOrWhiteSpace(personal);
        var caps = new List<string>();
        if (supportsVideo) caps.Add("Video");
        if (supportsImage) caps.Add("Image");
        if (supportsChat) caps.Add("Chat");
        if (supportsVision) caps.Add("Vision");
        if (caps.Count == 0) caps.Add("—");

        return new ProviderKeyStatusDto
        {
            ProviderId = providerId,
            DisplayName = displayName,
            Family = family,
            HasPersonalKey = hasPersonal,
            MaskedPersonalKey = MaskKey(personal),
            HasServerKey = hasServer,
            ActiveSource = hasPersonal ? "personal" : hasServer ? "server" : "none",
            CapabilitiesSummary = string.Join(", ", caps),
            SupportsVideo = supportsVideo,
            SupportsImage = supportsImage,
            SupportsChat = supportsChat,
            SupportsVision = supportsVision,
            Notes = notes,
        };
    }

    private static string? ProviderColumn(string providerId) =>
        NormalizeProvider(providerId) switch
        {
            "grok" => "encrypted_xai_api_key",
            "gemini" => "encrypted_gemini_api_key",
            "anthropic" => "encrypted_anthropic_api_key",
            _ => null,
        };

    private static string NormalizeProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return "";
        var p = providerId.Trim().ToLowerInvariant();
        return p switch
        {
            "xai" or "grok" => "grok",
            "google" or "gemini" => "gemini",
            "claude" or "anthropic" => "anthropic",
            _ => p,
        };
    }

    private static string? GetEncryptedFromEntity(UserEntity user, string providerId) =>
        NormalizeProvider(providerId) switch
        {
            "grok" => user.EncryptedXaiApiKey,
            "gemini" => user.EncryptedGeminiApiKey,
            "anthropic" => user.EncryptedAnthropicApiKey,
            _ => null,
        };

    private static void SetEncryptedOnEntity(UserEntity user, string providerId, string? encrypted)
    {
        switch (NormalizeProvider(providerId))
        {
            case "grok": user.EncryptedXaiApiKey = encrypted; break;
            case "gemini": user.EncryptedGeminiApiKey = encrypted; break;
            case "anthropic": user.EncryptedAnthropicApiKey = encrypted; break;
        }
    }

    private string EncryptApiKey(string plainText)
    {
        if (_protector != null)
            return _protector.Protect(plainText);

        return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
    }

    private string DecryptApiKey(string cipherText)
    {
        if (cipherText.StartsWith("plain:"))
        {
            var raw = cipherText.Substring(6);
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }

        if (_protector != null)
        {
            try
            {
                return _protector.Unprotect(cipherText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt API key with DataProtector");
            }
        }

        return cipherText;
    }

    public static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password + "PageToMovieSalt"));
        return Convert.ToBase64String(bytes);
    }

    private static UserEntity ReadUserFromReader(SqliteDataReader reader)
    {
        // Columns: 0 id, 1 name, 2 hash, 3 xai, 4 gemini, 5 anthropic, 6 role, 7 created, 8 login
        return new UserEntity
        {
            UserId = reader.GetString(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2),
            EncryptedXaiApiKey = reader.IsDBNull(3) ? null : reader.GetString(3),
            EncryptedGeminiApiKey = reader.IsDBNull(4) ? null : reader.GetString(4),
            EncryptedAnthropicApiKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            Role = reader.GetString(6),
            CreatedAt = DateTime.TryParse(reader.GetString(7), out var dt) ? dt : DateTime.UtcNow,
            LastLoginAt = reader.IsDBNull(8) ? null : (DateTime.TryParse(reader.GetString(8), out var ldt) ? ldt : null),
        };
    }
}
