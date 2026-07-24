using PageToMovie.Core.Models;

namespace PageToMovie.Engine.Abstractions;

/// <summary>Live counters + timing ring for admin dashboard (Phase C).</summary>
public interface IServerMetricsService
{
    void NoteCapacityReject();
    void NoteLockConflict();
    void NoteRateLimit();
    void NoteApiSlotAcquired(string userId);
    void NoteApiSlotReleased(string userId);
    void NoteFfmpegSlotAcquired();
    void NoteFfmpegSlotReleased();
    void NoteJobQueued(string kind, string? userId);
    void NoteJobStarted(string kind, string? userId, DateTimeOffset queuedAt);
    void NoteJobFinished(string kind, string? userId, bool success, DateTimeOffset queuedAt, DateTimeOffset startedAt);

    ServerMetricsSnapshot GetSnapshot(
        IJobStore jobs,
        ILockService locks,
        CapacityOptionsSnapshot capacity,
        ProcessMetricsSnapshot process);

    event Action? SnapshotUpdated;
}

public sealed class CapacityOptionsSnapshot
{
    public int MaxVideoInFlight { get; set; }
    public int MaxVideoInFlightPerUser { get; set; }
    public int MaxFfmpegInFlight { get; set; }
    public int MaxQueuePerUser { get; set; }
}

public sealed class ProcessMetricsSnapshot
{
    public long UptimeSec { get; set; }
    public double WorkingSetMb { get; set; }
    public double GcHeapMb { get; set; }
    public int ThreadCount { get; set; }
    public string Environment { get; set; } = "";
    public bool UseFakes { get; set; }
}

public sealed class ServerMetricsSnapshot
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public ProcessMetricsSnapshot Process { get; set; } = new();
    public CapacityOptionsSnapshot Capacity { get; set; } = new();
    public int ApiInFlight { get; set; }
    public int FfmpegInFlight { get; set; }
    public int CapacityRejects { get; set; }
    public int LockConflicts { get; set; }
    public int RateLimits { get; set; }
    public List<UserQueueDepth> QueueByUser { get; set; } = new();
    public List<JobSnapshot> Jobs { get; set; } = new();
    public List<LockRecord> Locks { get; set; } = new();
    public Dictionary<string, TimingStats> TimingsByKind { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UserQueueDepth
{
    public string UserId { get; set; } = "";
    public int Count { get; set; }
    public long? OldestWaitMs { get; set; }
}

public sealed class TimingStats
{
    public int CompletedInWindow { get; set; }
    public int FailuresInWindow { get; set; }
    public double FailRate { get; set; }
    public long QueueWaitP50Ms { get; set; }
    public long QueueWaitP95Ms { get; set; }
    public long RunP50Ms { get; set; }
    public long RunP95Ms { get; set; }
    public long TotalP50Ms { get; set; }
    public long TotalP95Ms { get; set; }
    public int InFlight { get; set; }
    public long? OldestInFlightAgeMs { get; set; }
}
