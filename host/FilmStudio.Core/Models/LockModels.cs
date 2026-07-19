namespace FilmStudio.Core.Models;

/// <summary>Soft exclusive lock on a project resource (Phase C).</summary>
public sealed class LockRecord
{
    public string Resource { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? Reason { get; set; }
    public string? JobId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Standard lock resource key helpers.</summary>
public static class LockKeys
{
    public static string Scene(string projectId, int scene) =>
        $"project:{projectId}:scene:{scene:D2}";

    public static string Wip(string projectId) =>
        $"project:{projectId}:wip";

    public static string Stage(string projectId) =>
        $"project:{projectId}:stage";

    public static string Character(string projectId, string charKey)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(charKey))
            throw new ArgumentException("charKey required", nameof(charKey));
        return $"project:{projectId.Trim()}:char:{charKey.Trim()}";
    }
}

/// <summary>Thrown when a required resource lock cannot be acquired (HTTP 409).</summary>
public sealed class LockConflictException : Exception
{
    public string Resource { get; }
    public string? OwnerUserId { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public LockConflictException(string resource, string? ownerUserId, DateTimeOffset? expiresAt, string? message = null)
        : base(message ?? BuildMessage(resource, ownerUserId))
    {
        Resource = resource;
        OwnerUserId = ownerUserId;
        ExpiresAt = expiresAt;
    }

    private static string BuildMessage(string resource, string? owner) =>
        string.IsNullOrWhiteSpace(owner)
            ? $"Resource locked: {resource}"
            : $"Resource locked by {owner}: {resource}";
}

/// <summary>Thrown when capacity gates reject a new job (HTTP 409).</summary>
public sealed class CapacityRejectedException : Exception
{
    public CapacityRejectedException(string message) : base(message) { }
}
