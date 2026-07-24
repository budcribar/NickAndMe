using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class JobStoreTests
{
    [Fact]
    public void Create_and_Get_round_trip()
    {
        var store = new JobStore();
        var rec = store.Create(new JobRecord
        {
            Kind = "scene",
            ProjectId = "Buster",
            Status = "running",
            Scene = 2,
        });
        Assert.False(string.IsNullOrWhiteSpace(rec.JobId));
        var got = store.Get(rec.JobId);
        Assert.NotNull(got);
        Assert.Equal("scene", got!.Kind);
        Assert.Equal(2, got.Scene);
    }

    [Fact]
    public void GetPrimary_prefers_running()
    {
        var store = new JobStore();
        store.Create(new JobRecord { Kind = "remux", Status = "done", ProjectId = "A" });
        var run = store.Create(new JobRecord { Kind = "scene", Status = "running", ProjectId = "B" });
        var primary = store.GetPrimary();
        Assert.NotNull(primary);
        Assert.Equal(run.JobId, primary!.JobId);
    }

    [Fact]
    public void List_filters_by_project()
    {
        var store = new JobStore();
        store.Create(new JobRecord { ProjectId = "A", Status = "done" });
        store.Create(new JobRecord { ProjectId = "B", Status = "done" });
        store.Create(new JobRecord { ProjectId = "A", Status = "running" });
        var list = store.List(projectId: "A");
        Assert.Equal(2, list.Count);
        Assert.All(list, j => Assert.Equal("A", j.ProjectId));
    }

    [Fact]
    public void CountQueuedForUser_includes_running()
    {
        var store = new JobStore();
        store.Create(new JobRecord { UserId = "u1", Status = "running" });
        store.Create(new JobRecord { UserId = "u1", Status = "queued" });
        store.Create(new JobRecord { UserId = "u2", Status = "running" });
        Assert.Equal(2, store.CountQueuedForUser("u1"));
        Assert.Equal(1, store.CountQueuedForUser("u2"));
        Assert.Equal(2, store.CountRunning()); // u1 running + u2 running
    }
}
