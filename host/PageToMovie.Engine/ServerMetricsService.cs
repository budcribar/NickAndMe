using System.Collections.Concurrent;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

public sealed class ServerMetricsService : IServerMetricsService
{
    private long _capacityRejects;
    private long _lockConflicts;
    private long _rateLimits;
    private int _apiInFlight;
    private int _ffmpegInFlight;
    private readonly ConcurrentDictionary<string, int> _apiInFlightByUser =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _timingGate = new();
    private readonly List<TimingSample> _samples = new();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int MaxSamples = 500;

    public event Action? SnapshotUpdated;

    public void NoteCapacityReject()
    {
        Interlocked.Increment(ref _capacityRejects);
        SnapshotUpdated?.Invoke();
    }

    public void NoteLockConflict()
    {
        Interlocked.Increment(ref _lockConflicts);
        SnapshotUpdated?.Invoke();
    }

    public void NoteRateLimit() => Interlocked.Increment(ref _rateLimits);

    public void NoteApiSlotAcquired(string userId)
    {
        Interlocked.Increment(ref _apiInFlight);
        _apiInFlightByUser.AddOrUpdate(userId, 1, (_, n) => n + 1);
    }

    public void NoteApiSlotReleased(string userId)
    {
        // Never drive the counter negative (unmatched releases from cancel races)
        DecrementFloor(ref _apiInFlight);
        _apiInFlightByUser.AddOrUpdate(userId, 0, (_, n) => Math.Max(0, n - 1));
    }

    public void NoteFfmpegSlotAcquired() => Interlocked.Increment(ref _ffmpegInFlight);
    public void NoteFfmpegSlotReleased() => DecrementFloor(ref _ffmpegInFlight);

    private static void DecrementFloor(ref int location)
    {
        while (true)
        {
            var cur = Volatile.Read(ref location);
            if (cur <= 0) return;
            if (Interlocked.CompareExchange(ref location, cur - 1, cur) == cur)
                return;
        }
    }

    public void NoteJobQueued(string kind, string? userId) => SnapshotUpdated?.Invoke();

    public void NoteJobStarted(string kind, string? userId, DateTimeOffset queuedAt) =>
        SnapshotUpdated?.Invoke();

    public void NoteJobFinished(
        string kind,
        string? userId,
        bool success,
        DateTimeOffset queuedAt,
        DateTimeOffset startedAt)
    {
        var now = DateTimeOffset.UtcNow;
        var sample = new TimingSample
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? "unknown" : kind,
            UserId = userId,
            Success = success,
            QueuedAt = queuedAt,
            StartedAt = startedAt,
            FinishedAt = now,
            QueueWaitMs = Math.Max(0, (long)(startedAt - queuedAt).TotalMilliseconds),
            RunMs = Math.Max(0, (long)(now - startedAt).TotalMilliseconds),
            TotalMs = Math.Max(0, (long)(now - queuedAt).TotalMilliseconds),
        };
        lock (_timingGate)
        {
            _samples.Add(sample);
            PruneLocked(now);
        }
        SnapshotUpdated?.Invoke();
    }

    public ServerMetricsSnapshot GetSnapshot(
        IJobStore jobs,
        ILockService locks,
        CapacityOptionsSnapshot capacity,
        ProcessMetricsSnapshot process)
    {
        var now = DateTimeOffset.UtcNow;
        var active = jobs.List(take: 100)
            .Where(j =>
                string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase))
            .Select(j => j.ToSnapshot())
            .ToList();

        var byUser = active
            .GroupBy(j => j.UserId ?? "local", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var oldest = g
                    .Select(j => j.QueuedAt ?? j.StartedAt)
                    .Where(t => t is not null)
                    .Select(t => (long)(now - t!.Value).TotalMilliseconds)
                    .DefaultIfEmpty(0)
                    .Max();
                return new UserQueueDepth
                {
                    UserId = g.Key,
                    Count = g.Count(),
                    OldestWaitMs = oldest,
                };
            })
            .OrderByDescending(u => u.Count)
            .Take(20)
            .ToList();

        Dictionary<string, TimingStats> timings;
        lock (_timingGate)
        {
            PruneLocked(now);
            timings = BuildTimingsLocked(active, now);
        }

        return new ServerMetricsSnapshot
        {
            GeneratedAt = now,
            Process = process,
            Capacity = capacity,
            ApiInFlight = Math.Max(0, _apiInFlight),
            FfmpegInFlight = Math.Max(0, _ffmpegInFlight),
            CapacityRejects = (int)Interlocked.Read(ref _capacityRejects),
            LockConflicts = (int)Interlocked.Read(ref _lockConflicts),
            RateLimits = (int)Interlocked.Read(ref _rateLimits),
            QueueByUser = byUser,
            Jobs = active,
            Locks = locks.ListActive().ToList(),
            TimingsByKind = timings,
        };
    }

    private void PruneLocked(DateTimeOffset now)
    {
        var cutoff = now - Window;
        _samples.RemoveAll(s => s.FinishedAt < cutoff);
        if (_samples.Count > MaxSamples)
            _samples.RemoveRange(0, _samples.Count - MaxSamples);
    }

    private Dictionary<string, TimingStats> BuildTimingsLocked(
        IReadOnlyList<JobSnapshot> active,
        DateTimeOffset now)
    {
        var result = new Dictionary<string, TimingStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _samples.GroupBy(s => s.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            var fails = list.Count(s => !s.Success);
            result[group.Key] = new TimingStats
            {
                CompletedInWindow = list.Count,
                FailuresInWindow = fails,
                FailRate = list.Count == 0 ? 0 : (double)fails / list.Count,
                QueueWaitP50Ms = Percentile(list.Select(s => s.QueueWaitMs), 0.50),
                QueueWaitP95Ms = Percentile(list.Select(s => s.QueueWaitMs), 0.95),
                RunP50Ms = Percentile(list.Select(s => s.RunMs), 0.50),
                RunP95Ms = Percentile(list.Select(s => s.RunMs), 0.95),
                TotalP50Ms = Percentile(list.Select(s => s.TotalMs), 0.50),
                TotalP95Ms = Percentile(list.Select(s => s.TotalMs), 0.95),
            };
        }

        foreach (var group in active.GroupBy(j => j.Kind ?? "unknown", StringComparer.OrdinalIgnoreCase))
        {
            if (!result.TryGetValue(group.Key, out var stats))
            {
                stats = new TimingStats();
                result[group.Key] = stats;
            }
            stats.InFlight = group.Count();
            stats.OldestInFlightAgeMs = group
                .Select(j => j.StartedAt ?? j.QueuedAt)
                .Where(t => t is not null)
                .Select(t => (long)(now - t!.Value).TotalMilliseconds)
                .DefaultIfEmpty(0)
                .Max();
        }

        return result;
    }

    private static long Percentile(IEnumerable<long> values, double p)
    {
        var arr = values.OrderBy(v => v).ToArray();
        if (arr.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * arr.Length) - 1;
        idx = Math.Clamp(idx, 0, arr.Length - 1);
        return arr[idx];
    }

    private sealed class TimingSample
    {
        public string Kind { get; set; } = "";
        public string? UserId { get; set; }
        public bool Success { get; set; }
        public DateTimeOffset QueuedAt { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public long QueueWaitMs { get; set; }
        public long RunMs { get; set; }
        public long TotalMs { get; set; }
    }
}
