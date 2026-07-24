using System.Collections.Concurrent;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>In-memory lock table (Phase C). File-backed persistence can replace later.</summary>
public sealed class InMemoryLockService : ILockService
{
    private readonly ConcurrentDictionary<string, LockRecord> _locks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool TryAcquire(
        string resource,
        string userId,
        TimeSpan ttl,
        string? reason = null,
        string? jobId = null,
        bool force = false)
    {
        if (string.IsNullOrWhiteSpace(resource))
            throw new ArgumentException("resource required", nameof(resource));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId required", nameof(userId));
        if (ttl <= TimeSpan.Zero)
            ttl = TimeSpan.FromMinutes(30);

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_locks.TryGetValue(resource, out var existing))
            {
                if (existing.ExpiresAt <= now)
                {
                    _locks.TryRemove(resource, out _);
                }
                else if (force)
                {
                    _locks.TryRemove(resource, out _);
                }
                else if (string.Equals(existing.UserId, userId, StringComparison.OrdinalIgnoreCase))
                {
                    // Re-entrant renew for same user
                    existing.ExpiresAt = now.Add(ttl);
                    existing.Reason = reason ?? existing.Reason;
                    if (!string.IsNullOrWhiteSpace(jobId))
                        existing.JobId = jobId;
                    return true;
                }
                else
                {
                    return false;
                }
            }

            _locks[resource] = new LockRecord
            {
                Resource = resource,
                UserId = userId.Trim(),
                Reason = reason,
                JobId = jobId,
                AcquiredAt = now,
                ExpiresAt = now.Add(ttl),
            };
            return true;
        }
    }

    public bool Renew(string resource, string userId, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(userId))
            return false;
        if (ttl <= TimeSpan.Zero)
            ttl = TimeSpan.FromMinutes(30);
        lock (_gate)
        {
            if (!_locks.TryGetValue(resource, out var existing))
                return false;
            if (!string.Equals(existing.UserId, userId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _locks.TryRemove(resource, out _);
                return false;
            }
            existing.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
            return true;
        }
    }

    public bool Release(string resource, string userId, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(resource))
            return false;
        lock (_gate)
        {
            if (!_locks.TryGetValue(resource, out var existing))
                return false;
            if (!force && !string.Equals(existing.UserId, userId, StringComparison.OrdinalIgnoreCase))
                return false;
            return _locks.TryRemove(resource, out _);
        }
    }

    public void ReleaseAllForJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return;
        lock (_gate)
        {
            foreach (var kv in _locks.ToArray())
            {
                if (string.Equals(kv.Value.JobId, jobId, StringComparison.OrdinalIgnoreCase))
                    _locks.TryRemove(kv.Key, out _);
            }
        }
    }

    public LockRecord? Get(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return null;
        lock (_gate)
        {
            if (!_locks.TryGetValue(resource, out var existing))
                return null;
            if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _locks.TryRemove(resource, out _);
                return null;
            }
            return Clone(existing);
        }
    }

    public IReadOnlyList<LockRecord> ListActive()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var kv in _locks.ToArray())
            {
                if (kv.Value.ExpiresAt <= now)
                    _locks.TryRemove(kv.Key, out _);
            }
            return _locks.Values.Select(Clone).OrderBy(l => l.Resource).ToList();
        }
    }

    private static LockRecord Clone(LockRecord s) => new()
    {
        Resource = s.Resource,
        UserId = s.UserId,
        Reason = s.Reason,
        JobId = s.JobId,
        AcquiredAt = s.AcquiredAt,
        ExpiresAt = s.ExpiresAt,
    };
}
