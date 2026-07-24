using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class LockServiceTests
{
    [Fact]
    public void TryAcquire_exclusive_second_user_fails()
    {
        var locks = new InMemoryLockService();
        Assert.True(locks.TryAcquire(LockKeys.Scene("Buster", 1), "u1", TimeSpan.FromMinutes(5), "gen"));
        Assert.False(locks.TryAcquire(LockKeys.Scene("Buster", 1), "u2", TimeSpan.FromMinutes(5), "gen"));
        var held = locks.Get(LockKeys.Scene("Buster", 1));
        Assert.NotNull(held);
        Assert.Equal("u1", held!.UserId);
    }

    [Fact]
    public void Same_user_reentrant_renews()
    {
        var locks = new InMemoryLockService();
        Assert.True(locks.TryAcquire(LockKeys.Wip("Buster"), "u1", TimeSpan.FromMinutes(5)));
        Assert.True(locks.TryAcquire(LockKeys.Wip("Buster"), "u1", TimeSpan.FromMinutes(10), "again"));
    }

    [Fact]
    public void Release_allows_other_user()
    {
        var locks = new InMemoryLockService();
        Assert.True(locks.TryAcquire(LockKeys.Stage("Buster"), "u1", TimeSpan.FromMinutes(5)));
        Assert.True(locks.Release(LockKeys.Stage("Buster"), "u1"));
        Assert.True(locks.TryAcquire(LockKeys.Stage("Buster"), "u2", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Different_scenes_do_not_conflict()
    {
        var locks = new InMemoryLockService();
        Assert.True(locks.TryAcquire(LockKeys.Scene("Buster", 1), "u1", TimeSpan.FromMinutes(5)));
        Assert.True(locks.TryAcquire(LockKeys.Scene("Buster", 2), "u2", TimeSpan.FromMinutes(5)));
        Assert.Equal(2, locks.ListActive().Count);
    }

    [Fact]
    public void ReleaseAllForJob_clears_matching()
    {
        var locks = new InMemoryLockService();
        locks.TryAcquire(LockKeys.Scene("Buster", 3), "u1", TimeSpan.FromMinutes(5), "gen", jobId: "abc");
        locks.TryAcquire(LockKeys.Scene("Buster", 4), "u1", TimeSpan.FromMinutes(5), "gen", jobId: "other");
        locks.ReleaseAllForJob("abc");
        Assert.Null(locks.Get(LockKeys.Scene("Buster", 3)));
        Assert.NotNull(locks.Get(LockKeys.Scene("Buster", 4)));
    }
}
