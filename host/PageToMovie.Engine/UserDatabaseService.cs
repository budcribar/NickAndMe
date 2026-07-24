using System;
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
/// and AES-256 encryption at rest for per-user xAI / Grok API keys.
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

                // WAL mode & busy timeout for 1,000 concurrent reader scaling
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        PRAGMA journal_mode = WAL;
                        PRAGMA busy_timeout = 5000;
                        PRAGMA synchronous = NORMAL;
                    ";
                    cmd.ExecuteNonQuery();
                }

                // Users table schema
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS users (
                            user_id TEXT PRIMARY KEY,
                            username TEXT NOT NULL UNIQUE,
                            password_hash TEXT NOT NULL,
                            encrypted_xai_api_key TEXT,
                            role TEXT NOT NULL DEFAULT 'User',
                            created_at TEXT NOT NULL,
                            last_login_at TEXT
                        );
                    ";
                    cmd.ExecuteNonQuery();
                }

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

    /// <summary>
    /// Gets a user account entity by UserId.
    /// </summary>
    public async Task<UserEntity?> GetUserByIdAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, username, password_hash, encrypted_xai_api_key, role, created_at, last_login_at FROM users WHERE user_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", userId.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadUserFromReader(reader);
        }

        return null;
    }

    /// <summary>
    /// Gets a user account entity by Username.
    /// </summary>
    public async Task<UserEntity?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, username, password_hash, encrypted_xai_api_key, role, created_at, last_login_at FROM users WHERE LOWER(username) = LOWER(@name) LIMIT 1";
        cmd.Parameters.AddWithValue("@name", username.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadUserFromReader(reader);
        }

        return null;
    }

    /// <summary>
    /// Saves or updates a user's encrypted xAI API key in SQLite.
    /// </summary>
    public async Task SaveXaiApiKeyAsync(string userId, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        var encrypted = string.IsNullOrWhiteSpace(apiKey) ? null : EncryptApiKey(apiKey.Trim());

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET encrypted_xai_api_key = @key WHERE user_id = @id";
        cmd.Parameters.AddWithValue("@key", (object?)encrypted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", userId.Trim());

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
        {
            // Auto-create user record if default or missing
            var user = new UserEntity
            {
                UserId = userId.Trim(),
                Username = userId.Trim(),
                PasswordHash = HashPassword("dev-placeholder"),
                EncryptedXaiApiKey = encrypted,
                Role = "User",
                CreatedAt = DateTime.UtcNow
            };
            await InsertUserAsync(user, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets and decrypts a user's per-account xAI API key from SQLite.
    /// Returns null if no key is saved for this user.
    /// </summary>
    public async Task<string?> GetDecryptedXaiApiKeyAsync(string userId, CancellationToken ct = default)
    {
        var user = await GetUserByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null || string.IsNullOrWhiteSpace(user.EncryptedXaiApiKey))
            return null;

        return DecryptApiKey(user.EncryptedXaiApiKey);
    }

    /// <summary>
    /// Gets user settings DTO for client UI (masks API key for security).
    /// </summary>
    public async Task<UserSettingsDto> GetUserSettingsDtoAsync(string userId, CancellationToken ct = default)
    {
        var user = await GetUserByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new UserSettingsDto { UserId = userId, Username = userId, HasXaiApiKey = false };
        }

        var key = string.IsNullOrWhiteSpace(user.EncryptedXaiApiKey) ? null : DecryptApiKey(user.EncryptedXaiApiKey);
        var hasKey = !string.IsNullOrWhiteSpace(key);
        var masked = hasKey && key!.Length > 8
            ? key.Substring(0, 4) + "..." + key.Substring(key.Length - 4)
            : (hasKey ? "****" : null);

        return new UserSettingsDto
        {
            UserId = user.UserId,
            Username = user.Username,
            HasXaiApiKey = hasKey,
            MaskedXaiApiKey = masked
        };
    }

    /// <summary>
    /// Inserts a new user entity into SQLite.
    /// </summary>
    public async Task InsertUserAsync(UserEntity user, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO users (user_id, username, password_hash, encrypted_xai_api_key, role, created_at, last_login_at)
            VALUES (@id, @name, @hash, @key, @role, @created, @login)
            ON CONFLICT(user_id) DO UPDATE SET
                username = excluded.username,
                encrypted_xai_api_key = excluded.encrypted_xai_api_key;
        ";
        cmd.Parameters.AddWithValue("@id", user.UserId);
        cmd.Parameters.AddWithValue("@name", user.Username);
        cmd.Parameters.AddWithValue("@hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("@key", (object?)user.EncryptedXaiApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", user.Role);
        cmd.Parameters.AddWithValue("@created", user.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@login", (object?)user.LastLoginAt?.ToString("o") ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private string EncryptApiKey(string plainText)
    {
        if (_protector != null)
        {
            return _protector.Protect(plainText);
        }

        // Fallback simple base64 marker if DataProtection unconfigured in dev
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
        return new UserEntity
        {
            UserId = reader.GetString(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2),
            EncryptedXaiApiKey = reader.IsDBNull(3) ? null : reader.GetString(3),
            Role = reader.GetString(4),
            CreatedAt = DateTime.TryParse(reader.GetString(5), out var dt) ? dt : DateTime.UtcNow,
            LastLoginAt = reader.IsDBNull(6) ? null : (DateTime.TryParse(reader.GetString(6), out var ldt) ? ldt : null)
        };
    }
}
