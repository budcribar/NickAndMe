using System;

namespace PageToMovie.Core.Models;

/// <summary>
/// User account entity stored in SQLite database (pagetomovie.db).
/// Represents user identity, authentication credentials, and encrypted per-user API keys.
/// </summary>
public class UserEntity
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? EncryptedXaiApiKey { get; set; }
    public string Role { get; set; } = "User";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// User settings DTO returned to client Blazor UI (masks API key for privacy).
/// </summary>
public class UserSettingsDto
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public bool HasXaiApiKey { get; set; }
    public string? MaskedXaiApiKey { get; set; }
}

/// <summary>
/// Request payload for updating user settings.
/// </summary>
public class UpdateUserSettingsRequest
{
    public string? XaiApiKey { get; set; }
}
