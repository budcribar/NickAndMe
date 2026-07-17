using FilmStudio.Core.Models;

namespace FilmStudio.Engine.Abstractions;

/// <summary>Multi-job registry (Phase A+).</summary>
public interface IJobStore
{
    JobRecord Create(JobRecord seed);
    JobRecord? Get(string jobId);
    IReadOnlyList<JobRecord> List(string? userId = null, string? projectId = null, int take = 50);
    /// <summary>Running job if any; else most recently finished/queued (optionally filtered by user).</summary>
    JobRecord? GetPrimary(string? userId = null);
    void Update(string jobId, Action<JobRecord> mutate);
    bool TryCancel(string jobId);
    int CountRunning();
    int CountQueuedForUser(string userId);
}
