using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

public class JobCancelScopeTests
{
    [Theory]
    [InlineData("alice", "alice", false, true)]
    [InlineData("alice", "bob", false, false)]
    [InlineData("alice", "bob", true, true)]  // admin cancel-all
    [InlineData("alice", null, false, false)] // unscoped bulk refused
    [InlineData("alice", "", false, false)]
    [InlineData(null, "alice", false, false)]
    [InlineData(null, null, true, true)]
    public void IsInBulkCancelScope_matches_expected(
        string? jobUser, string? requestUser, bool cancelAll, bool expected)
    {
        Assert.Equal(
            expected,
            FilmJobService.IsInBulkCancelScope(jobUser, requestUser, cancelAll));
    }

    [Fact]
    public void JobStore_TryCancel_marks_running_job_cancelled()
    {
        var store = new JobStore();
        var rec = store.Create(new FilmStudio.Core.Models.JobRecord
        {
            UserId = "alice",
            Status = "running",
            Kind = "scene",
        });
        Assert.True(store.TryCancel(rec.JobId));
        Assert.Equal("cancelled", store.Get(rec.JobId)!.Status);
    }

    [Fact]
    public void JobStore_List_filters_by_user()
    {
        var store = new JobStore();
        store.Create(new FilmStudio.Core.Models.JobRecord { UserId = "alice", Status = "running" });
        store.Create(new FilmStudio.Core.Models.JobRecord { UserId = "bob", Status = "running" });
        var alice = store.List(userId: "alice", take: 50);
        Assert.All(alice, j => Assert.Equal("alice", j.UserId));
        Assert.Single(alice);
    }
}
