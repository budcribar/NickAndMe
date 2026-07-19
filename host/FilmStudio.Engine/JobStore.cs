using System.Collections.Concurrent;
using FilmStudio.Core.Models;
using FilmStudio.Engine.Abstractions;

namespace FilmStudio.Engine;

/// <summary>In-memory multi-job registry (Phase A).</summary>
public sealed class JobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public JobRecord Create(JobRecord seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(seed.JobId) || _jobs.ContainsKey(seed.JobId))
            {
                // Never overwrite an existing id — generate a fresh one when missing or taken
                string id;
                do
                {
                    id = Guid.NewGuid().ToString("N")[..12];
                } while (_jobs.ContainsKey(id));
                seed.JobId = id;
            }
            seed.QueuedAt = seed.QueuedAt == default ? DateTimeOffset.UtcNow : seed.QueuedAt;
            if (seed.Log is null)
                seed.Log = new List<string>();
            _jobs[seed.JobId] = Clone(seed);
            return Clone(seed);
        }
    }

    public JobRecord? Get(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var j) ? Clone(j) : null;
        }
    }

    public IReadOnlyList<JobRecord> List(string? userId = null, string? projectId = null, int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        lock (_gate)
        {
            IEnumerable<JobRecord> q = _jobs.Values;
            if (!string.IsNullOrWhiteSpace(userId))
                q = q.Where(j => string.Equals(j.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(projectId))
                q = q.Where(j => string.Equals(j.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
            return q
                .OrderByDescending(j => j.QueuedAt)
                .Take(take)
                .Select(Clone)
                .ToList();
        }
    }

    public JobRecord? GetPrimary(string? userId = null)
    {
        lock (_gate)
        {
            IEnumerable<JobRecord> q = _jobs.Values;
            if (!string.IsNullOrWhiteSpace(userId))
                q = q.Where(j => string.Equals(j.UserId, userId, StringComparison.OrdinalIgnoreCase));

            var list = q.ToList();
            // Match JobListHelpers.PickPrimary: running → queued → newest finished
            var running = list
                .Where(j => string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
                .FirstOrDefault();
            if (running is not null)
                return Clone(running);

            var queued = list
                .Where(j => string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.QueuedAt)
                .FirstOrDefault();
            if (queued is not null)
                return Clone(queued);

            return list
                .OrderByDescending(j => j.FinishedAt ?? j.StartedAt ?? j.QueuedAt)
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public void Update(string jobId, Action<JobRecord> mutate)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var j))
                return;
            mutate(j);
        }
    }

    public bool TryCancel(string jobId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var j))
                return false;
            if (string.Equals(j.Status, "done", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Status, "error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                return false;
            j.Status = "cancelled";
            j.Message = "Cancel requested";
            j.FinishedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public int CountRunning() =>
        _jobs.Values.Count(j => string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase));

    public int CountQueuedForUser(string userId) =>
        _jobs.Values.Count(j =>
            string.Equals(j.UserId, userId, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase)));

    private static JobRecord Clone(JobRecord s) => new()
    {
        JobId = s.JobId,
        Status = s.Status,
        Kind = s.Kind,
        Message = s.Message,
        ProjectId = s.ProjectId,
        UserId = s.UserId,
        CharKey = s.CharKey,
        Scene = s.Scene,
        Clip = s.Clip,
        Index = s.Index,
        Total = s.Total,
        Log = s.Log is null ? new List<string>() : s.Log.ToList(),
        Error = s.Error,
        QueuedAt = s.QueuedAt,
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
    };
}
