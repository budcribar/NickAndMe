using FilmStudio.Core.Models;
using Xunit;

namespace FilmStudio.Tests;

/// <summary>Phase F: multi-job primary selection (no HTTP host).</summary>
public class JobsApiContractTests
{
    [Fact]
    public void PickPrimary_prefers_running()
    {
        var jobs = new List<JobSnapshot>
        {
            new() { JobId = "a", Status = "done", FinishedAt = DateTimeOffset.UtcNow },
            new() { JobId = "b", Status = "running", StartedAt = DateTimeOffset.UtcNow },
            new() { JobId = "c", Status = "queued", QueuedAt = DateTimeOffset.UtcNow },
        };
        var p = JobListHelpers.PickPrimary(jobs);
        Assert.NotNull(p);
        Assert.Equal("b", p!.JobId);
    }

    [Fact]
    public void PickPrimary_falls_back_to_newest_finished()
    {
        var older = DateTimeOffset.UtcNow.AddMinutes(-10);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        var jobs = new List<JobSnapshot>
        {
            new() { JobId = "old", Status = "done", FinishedAt = older },
            new() { JobId = "new", Status = "done", FinishedAt = newer },
        };
        var p = JobListHelpers.PickPrimary(jobs);
        Assert.Equal("new", p!.JobId);
    }

    [Fact]
    public void PickPrimary_empty_is_null()
    {
        Assert.Null(JobListHelpers.PickPrimary(null));
        Assert.Null(JobListHelpers.PickPrimary(Array.Empty<JobSnapshot>()));
    }
}
