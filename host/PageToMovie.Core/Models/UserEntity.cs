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
    public string? EncryptedGeminiApiKey { get; set; }
    public string? EncryptedAnthropicApiKey { get; set; }
    public string Role { get; set; } = "User";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Remaining list-rate credit balance (USD). 1 credit = $0.01.</summary>
    public double CreditsBalanceUsd { get; set; }

    /// <summary>Lifetime granted (signup + admin), USD.</summary>
    public double CreditsLifetimeGrantedUsd { get; set; }

    /// <summary>Lifetime spent on video/image list-rate, USD.</summary>
    public double CreditsLifetimeUsedUsd { get; set; }
}

/// <summary>
/// One provider row for the Configuration "Account &amp; API keys" panel.
/// </summary>
public sealed class ProviderKeyStatusDto
{
    /// <summary>Stable id: <c>grok</c>, <c>gemini</c>, <c>anthropic</c>.</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>UI label, e.g. "xAI / Grok".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Family enum name for tooling (<c>Xai</c>, <c>Google</c>, <c>Anthropic</c>).</summary>
    public string Family { get; set; } = "";

    /// <summary>Personal (per-user, encrypted) key is saved.</summary>
    public bool HasPersonalKey { get; set; }

    /// <summary>Masked personal key when present.</summary>
    public string? MaskedPersonalKey { get; set; }

    /// <summary>Server-wide env key is available (fallback when no personal key).</summary>
    public bool HasServerKey { get; set; }

    /// <summary>True if personal or server key can be used for API calls.</summary>
    public bool IsConfigured => HasPersonalKey || HasServerKey;

    /// <summary>Where the active key comes from: personal, server, or none.</summary>
    public string ActiveSource { get; set; } = "none";

    /// <summary>Human-readable capabilities, e.g. "Video, Image, Chat, Vision".</summary>
    public string CapabilitiesSummary { get; set; } = "";

    public bool SupportsVideo { get; set; }
    public bool SupportsImage { get; set; }
    public bool SupportsChat { get; set; }
    public bool SupportsVision { get; set; }

    /// <summary>Limitations users need before picking this provider (video continue, OCR, etc.).</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// User settings DTO returned to client Blazor UI (masks API keys for privacy).
/// </summary>
public class UserSettingsDto
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";

    /// <summary>Back-compat: personal xAI / Grok key.</summary>
    public bool HasXaiApiKey { get; set; }
    public string? MaskedXaiApiKey { get; set; }

    public bool HasGeminiApiKey { get; set; }
    public string? MaskedGeminiApiKey { get; set; }

    public bool HasAnthropicApiKey { get; set; }
    public string? MaskedAnthropicApiKey { get; set; }

    /// <summary>Per-provider status for the Configuration UI.</summary>
    public List<ProviderKeyStatusDto> Providers { get; set; } = new();
}

/// <summary>
/// Request payload for updating user settings. Null fields leave existing keys unchanged;
/// empty string clears that provider's personal key.
/// </summary>
public class UpdateUserSettingsRequest
{
    public string? XaiApiKey { get; set; }
    public string? GeminiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }
}
