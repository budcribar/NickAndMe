using PageToMovie.Core.Models;

namespace PageToMovie.Engine.Abstractions;

/// <summary>Exclusive soft locks for scenes / WIP / stage / characters (Phase C).</summary>
public interface ILockService
{
    /// <summary>
    /// Try to acquire <paramref name="resource"/>. Returns false if held by another user
    /// (or same user when not re-entrant). Expired locks are stolen automatically.
    /// </summary>
    bool TryAcquire(
        string resource,
        string userId,
        TimeSpan ttl,
        string? reason = null,
        string? jobId = null,
        bool force = false);

    bool Renew(string resource, string userId, TimeSpan ttl);

    /// <summary>Release if owned by <paramref name="userId"/>, or always when <paramref name="force"/>.</summary>
    bool Release(string resource, string userId, bool force = false);

    void ReleaseAllForJob(string jobId);

    LockRecord? Get(string resource);

    IReadOnlyList<LockRecord> ListActive();
}
