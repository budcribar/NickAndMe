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
        var dataDir = ResolveDataDirectory(workspace);
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "pagetomovie.db");

        EnsureDatabaseInitialized();
    }

    /// <summary>
    /// Pick a durable data dir. Order:
    /// <list type="number">
    /// <item>Env <c>PageToMovie_USER_DB_DIR</c> / <c>PAGETOMOVIE_USER_DB_DIR</c></item>
    /// <item>Isolated <see cref="PageToMovieOptions.WorkspaceRoot"/> under the process temp path (unit tests)</item>
    /// <item>Container volume <c>/data</c> or <c>/app/data</c> (Railway)</item>
    /// <item>WorkspaceRoot/data, else temp PageToMovie/data</item>
    /// </list>
    /// </summary>
    internal static string ResolveDataDirectory(string? workspace)
    {
        var envDir = Environment.GetEnvironmentVariable("PageToMovie_USER_DB_DIR")
                     ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_USER_DB_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
            return envDir.Trim();

        // Unit tests pass a unique temp WorkspaceRoot — never share C:\data / /data with them.
        if (IsIsolatedTestWorkspace(workspace))
            return Path.Combine(workspace!.Trim(), "data");

        if (Directory.Exists("/data"))
            return "/data";
        if (Directory.Exists("/app/data"))
            return "/app/data";

        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(workspace.Trim(), "data");

        return Path.Combine(Path.GetTempPath(), "PageToMovie", "data");
    }

    private static bool IsIsolatedTestWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return false;
        try
        {
            var full = Path.GetFullPath(workspace.Trim());
            var temp = Path.GetFullPath(Path.GetTempPath());
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
                            last_login_at TEXT,
                            credits_balance_usd REAL NOT NULL DEFAULT 0,
                            credits_lifetime_granted_usd REAL NOT NULL DEFAULT 0,
                            credits_lifetime_used_usd REAL NOT NULL DEFAULT 0
                        );
                    ";
                    cmd.ExecuteNonQuery();
                }

                // Migrate older DBs that only had the xAI column.
                EnsureColumn(conn, "users", "encrypted_gemini_api_key", "TEXT");
                EnsureColumn(conn, "users", "encrypted_anthropic_api_key", "TEXT");

                // User billing credits (list-rate USD; 1 credit = $0.01).
                EnsureColumn(conn, "users", "credits_balance_usd", "REAL NOT NULL DEFAULT 0");
                EnsureColumn(conn, "users", "credits_lifetime_granted_usd", "REAL NOT NULL DEFAULT 0");
                EnsureColumn(conn, "users", "credits_lifetime_used_usd", "REAL NOT NULL DEFAULT 0");

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS credit_ledger (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            user_id TEXT NOT NULL,
                            ts TEXT NOT NULL,
                            kind TEXT NOT NULL,
                            amount_usd REAL NOT NULL,
                            balance_after_usd REAL NOT NULL,
                            project_id TEXT,
                            note TEXT,
                            meta_kind TEXT
                        );
                        CREATE INDEX IF NOT EXISTS idx_credit_ledger_user ON credit_ledger(user_id);
                        CREATE INDEX IF NOT EXISTS idx_credit_ledger_ts ON credit_ledger(ts);
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

        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql}";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Race-safe: another process may have added the column between PRAGMA and ALTER.
        }
    }

    public async Task<UserEntity?> GetUserByIdAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = UserSelectSql + " WHERE user_id = @id LIMIT 1";
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
        cmd.CommandText = UserSelectSql + " WHERE LOWER(username) = LOWER(@name) LIMIT 1";
        cmd.Parameters.AddWithValue("@name", username.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ReadUserFromReader(reader);

        return null;
    }

    /// <summary>Resolve by user_id, then username (case-insensitive).</summary>
    public async Task<UserEntity?> ResolveUserAsync(string userIdOrName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userIdOrName)) return null;
        var byId = await GetUserByIdAsync(userIdOrName, ct).ConfigureAwait(false);
        if (byId is not null) return byId;
        return await GetUserByUsernameAsync(userIdOrName, ct).ConfigureAwait(false);
    }

    private const string UserSelectSql = @"
            SELECT user_id, username, password_hash,
                   encrypted_xai_api_key, encrypted_gemini_api_key, encrypted_anthropic_api_key,
                   role, created_at, last_login_at,
                   COALESCE(credits_balance_usd, 0),
                   COALESCE(credits_lifetime_granted_usd, 0),
                   COALESCE(credits_lifetime_used_usd, 0)
            FROM users";

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
        try
        {
            return DecryptApiKey(encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetDecryptedProviderApiKeyAsync failed for {UserId}/{Provider}", userId, providerId);
            return null;
        }
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
                role, created_at, last_login_at,
                credits_balance_usd, credits_lifetime_granted_usd, credits_lifetime_used_usd)
            VALUES (@id, @name, @hash, @xai, @gemini, @anthropic, @role, @created, @login,
                    @bal, @granted, @used)
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
        cmd.Parameters.AddWithValue("@bal", user.CreditsBalanceUsd);
        cmd.Parameters.AddWithValue("@granted", user.CreditsLifetimeGrantedUsd);
        cmd.Parameters.AddWithValue("@used", user.CreditsLifetimeUsedUsd);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── Credits ──────────────────────────────────────────────────────────────

    public async Task<List<UserCreditSummaryDto>> ListUserCreditSummariesAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = UserSelectSql + " ORDER BY LOWER(username)";

        var list = new List<UserCreditSummaryDto>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ToCreditSummary(ReadUserFromReader(reader)));
        return list;
    }

    public async Task<UserCreditSummaryDto?> GetUserCreditSummaryAsync(string userId, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(userId, ct).ConfigureAwait(false);
        return user is null ? null : ToCreditSummary(user);
    }

    public async Task<AdminCreditsOverviewDto> GetAdminCreditsOverviewAsync(
        int recentLedger = 40,
        CancellationToken ct = default)
    {
        var users = await ListUserCreditSummariesAsync(ct).ConfigureAwait(false);
        var ledger = await GetRecentCreditLedgerAsync(Math.Clamp(recentLedger, 1, 200), ct).ConfigureAwait(false);

        return new AdminCreditsOverviewDto
        {
            UserCount = users.Count,
            TotalBalanceUsd = users.Sum(u => u.CreditsBalanceUsd),
            TotalGrantedUsd = users.Sum(u => u.CreditsLifetimeGrantedUsd),
            TotalUsedUsd = users.Sum(u => u.CreditsLifetimeUsedUsd),
            Users = users,
            RecentLedger = ledger,
            UsdPerCredit = CreditUnits.UsdPerCredit,
        };
    }

    public async Task<List<CreditLedgerEntryDto>> GetRecentCreditLedgerAsync(
        int take = 40,
        CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, user_id, ts, kind, amount_usd, balance_after_usd, project_id, note, meta_kind
            FROM credit_ledger
            ORDER BY id DESC
            LIMIT @take";
        cmd.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 500));

        var list = new List<CreditLedgerEntryDto>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadLedgerEntry(reader));
        return list;
    }

    /// <summary>
    /// Atomically apply a credit delta. Positive = grant, negative = debit/claw-back.
    /// Updates balance + lifetime counters and appends a ledger row.
    /// </summary>
    public async Task<UserCreditSummaryDto?> ApplyCreditDeltaAsync(
        string userId,
        double amountUsd,
        string kind,
        string? note,
        string? metaKind,
        string? projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        // Round to 4 decimal places (cents of a cent) for stable math.
        amountUsd = Math.Round(amountUsd, 4, MidpointRounding.AwayFromZero);
        if (Math.Abs(amountUsd) < 0.00005)
            return await GetUserCreditSummaryAsync(userId, ct).ConfigureAwait(false);

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        UserEntity? user;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = UserSelectSql + " WHERE user_id = @id OR LOWER(username) = LOWER(@id) LIMIT 1";
            find.Parameters.AddWithValue("@id", userId.Trim());
            using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }
            user = ReadUserFromReader(reader);
        }

        var newBalance = Math.Round(user.CreditsBalanceUsd + amountUsd, 4, MidpointRounding.AwayFromZero);
        var newGranted = user.CreditsLifetimeGrantedUsd;
        var newUsed = user.CreditsLifetimeUsedUsd;

        if (amountUsd > 0)
            newGranted = Math.Round(newGranted + amountUsd, 4, MidpointRounding.AwayFromZero);
        else
            newUsed = Math.Round(newUsed + Math.Abs(amountUsd), 4, MidpointRounding.AwayFromZero);

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = @"
                UPDATE users SET
                    credits_balance_usd = @bal,
                    credits_lifetime_granted_usd = @granted,
                    credits_lifetime_used_usd = @used
                WHERE user_id = @id";
            upd.Parameters.AddWithValue("@bal", newBalance);
            upd.Parameters.AddWithValue("@granted", newGranted);
            upd.Parameters.AddWithValue("@used", newUsed);
            upd.Parameters.AddWithValue("@id", user.UserId);
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var ts = DateTimeOffset.UtcNow;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = @"
                INSERT INTO credit_ledger
                    (user_id, ts, kind, amount_usd, balance_after_usd, project_id, note, meta_kind)
                VALUES (@uid, @ts, @kind, @amt, @bal, @proj, @note, @meta)";
            ins.Parameters.AddWithValue("@uid", user.UserId);
            ins.Parameters.AddWithValue("@ts", ts.ToString("o"));
            ins.Parameters.AddWithValue("@kind", string.IsNullOrWhiteSpace(kind) ? "adjust" : kind.Trim());
            ins.Parameters.AddWithValue("@amt", amountUsd);
            ins.Parameters.AddWithValue("@bal", newBalance);
            ins.Parameters.AddWithValue("@proj", (object?)projectId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
            ins.Parameters.AddWithValue("@meta", (object?)metaKind ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        user.CreditsBalanceUsd = newBalance;
        user.CreditsLifetimeGrantedUsd = newGranted;
        user.CreditsLifetimeUsedUsd = newUsed;
        return ToCreditSummary(user);
    }

    private static UserCreditSummaryDto ToCreditSummary(UserEntity u) => new()
    {
        UserId = u.UserId,
        Username = u.Username,
        Role = u.Role,
        CreatedAt = u.CreatedAt,
        LastLoginAt = u.LastLoginAt,
        HasXaiApiKey = !string.IsNullOrWhiteSpace(u.EncryptedXaiApiKey),
        CreditsBalanceUsd = u.CreditsBalanceUsd,
        CreditsLifetimeGrantedUsd = u.CreditsLifetimeGrantedUsd,
        CreditsLifetimeUsedUsd = u.CreditsLifetimeUsedUsd,
    };

    private static CreditLedgerEntryDto ReadLedgerEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        UserId = reader.GetString(1),
        Ts = DateTimeOffset.TryParse(reader.GetString(2), out var ts) ? ts : DateTimeOffset.UtcNow,
        Kind = reader.GetString(3),
        AmountUsd = reader.GetDouble(4),
        BalanceAfterUsd = reader.GetDouble(5),
        ProjectId = reader.IsDBNull(6) ? null : reader.GetString(6),
        Note = reader.IsDBNull(7) ? null : reader.GetString(7),
        MetaKind = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private string? DecryptOptional(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try
        {
            return DecryptApiKey(encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DecryptOptional failed — treating personal key as missing");
            return null;
        }
    }

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

        if (_protector is null)
        {
            // No protector: only accept plain: payloads (dev). Never return opaque ciphertext as a key.
            throw new InvalidOperationException(
                "Cannot decrypt personal API key (DataProtection not configured). Re-save the key in Configuration.");
        }

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (Exception ex)
        {
            // Common on Railway after redeploy without a Volume on /data: DP keys rotate and
            // stored ciphertexts become unreadable. Returning ciphertext as the API key caused
            // "Key Active" in UI with 401s on xAI. Treat as missing instead.
            _logger.LogWarning(ex,
                "Failed to decrypt API key with DataProtector — re-save the key in Configuration " +
                "(and mount a Railway Volume at /data so keys survive restarts)");
            throw new InvalidOperationException(
                "Personal API key could not be decrypted (encryption keys changed after redeploy). " +
                "Open Configuration, re-save your xAI / Grok key. Mount a Railway Volume at /data " +
                "so the key and data-protection store persist.", ex);
        }
    }

    public static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password + "PageToMovieSalt"));
        return Convert.ToBase64String(bytes);
    }

    private static UserEntity ReadUserFromReader(SqliteDataReader reader)
    {
        // Columns: 0 id, 1 name, 2 hash, 3 xai, 4 gemini, 5 anthropic, 6 role, 7 created, 8 login,
        //          9 balance, 10 granted, 11 used
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
            CreditsBalanceUsd = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetDouble(9) : 0,
            CreditsLifetimeGrantedUsd = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetDouble(10) : 0,
            CreditsLifetimeUsedUsd = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetDouble(11) : 0,
        };
    }
}
