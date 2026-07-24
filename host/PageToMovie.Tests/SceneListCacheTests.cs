using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class SceneListCacheTests
{
    [Fact]
    public async Task GetOrBuild_returns_cached_until_ttl()
    {
        var cache = new SceneListCache(TimeSpan.FromSeconds(30));
        var builds = 0;
        Task<IReadOnlyList<SceneSummary>> Build(CancellationToken _)
        {
            builds++;
            return Task.FromResult<IReadOnlyList<SceneSummary>>(new List<SceneSummary>
            {
                new() { SceneNumber = 1, ClipCount = 3, ClipsOnDisk = 2 },
            });
        }

        var a = await cache.GetOrBuildAsync("Buster", probeDurations: false, Build);
        var b = await cache.GetOrBuildAsync("Buster", probeDurations: false, Build);
        Assert.Equal(1, builds);
        Assert.Equal(1, a[0].SceneNumber);
        Assert.Equal(1, b[0].SceneNumber);
        // Callers get clones — mutate does not poison cache
        ((List<SceneSummary>)a)[0].ClipsOnDisk = 99;
        var c = await cache.GetOrBuildAsync("Buster", probeDurations: false, Build);
        Assert.Equal(2, c[0].ClipsOnDisk);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task Light_and_full_are_separate_entries()
    {
        var cache = new SceneListCache(TimeSpan.FromMinutes(1));
        var lightBuilds = 0;
        var fullBuilds = 0;
        await cache.GetOrBuildAsync("P", false, _ =>
        {
            lightBuilds++;
            return Task.FromResult<IReadOnlyList<SceneSummary>>(
                new[] { new SceneSummary { SceneNumber = 1 } });
        });
        await cache.GetOrBuildAsync("P", true, _ =>
        {
            fullBuilds++;
            return Task.FromResult<IReadOnlyList<SceneSummary>>(
                new[] { new SceneSummary { SceneNumber = 2 } });
        });
        await cache.GetOrBuildAsync("P", false, _ =>
        {
            lightBuilds++;
            return Task.FromResult<IReadOnlyList<SceneSummary>>(Array.Empty<SceneSummary>());
        });
        Assert.Equal(1, lightBuilds);
        Assert.Equal(1, fullBuilds);
    }

    [Fact]
    public async Task Invalidate_clears_both_keys()
    {
        var cache = new SceneListCache(TimeSpan.FromMinutes(1));
        var builds = 0;
        Task<IReadOnlyList<SceneSummary>> Build(CancellationToken _)
        {
            builds++;
            return Task.FromResult<IReadOnlyList<SceneSummary>>(
                new[] { new SceneSummary { SceneNumber = builds } });
        }

        await cache.GetOrBuildAsync("P", false, Build);
        await cache.GetOrBuildAsync("P", true, Build);
        Assert.Equal(2, builds);
        cache.Invalidate("P");
        await cache.GetOrBuildAsync("P", false, Build);
        Assert.Equal(3, builds);
    }

    [Fact]
    public async Task Single_flight_only_one_build()
    {
        var cache = new SceneListCache(TimeSpan.FromMinutes(1));
        var builds = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IReadOnlyList<SceneSummary>> Build(CancellationToken _)
        {
            Interlocked.Increment(ref builds);
            await gate.Task.ConfigureAwait(false);
            return new[] { new SceneSummary { SceneNumber = 1 } };
        }

        var t1 = cache.GetOrBuildAsync("P", false, Build);
        await Task.Delay(50);
        var t2 = cache.GetOrBuildAsync("P", false, Build);
        await Task.Delay(50);
        // First builder is inside; second should be waiting on single-flight
        Assert.Equal(1, builds);
        gate.SetResult();
        await Task.WhenAll(t1, t2);
        Assert.Equal(1, builds);
    }
}
