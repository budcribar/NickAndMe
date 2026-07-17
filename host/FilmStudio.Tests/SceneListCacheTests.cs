using FilmStudio.Core.Models;
using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

public class SceneListCacheTests
{
    [Fact]
    public void GetOrBuild_returns_cached_until_ttl()
    {
        var cache = new SceneListCache(TimeSpan.FromSeconds(30));
        var builds = 0;
        IReadOnlyList<SceneSummary> Build()
        {
            builds++;
            return new List<SceneSummary>
            {
                new() { SceneNumber = 1, ClipCount = 3, ClipsOnDisk = 2 },
            };
        }

        var a = cache.GetOrBuild("Buster", probeDurations: false, Build);
        var b = cache.GetOrBuild("Buster", probeDurations: false, Build);
        Assert.Equal(1, builds);
        Assert.Equal(1, a[0].SceneNumber);
        Assert.Equal(1, b[0].SceneNumber);
        // Callers get clones — mutate does not poison cache
        ((List<SceneSummary>)a)[0].ClipsOnDisk = 99;
        var c = cache.GetOrBuild("Buster", probeDurations: false, Build);
        Assert.Equal(2, c[0].ClipsOnDisk);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Light_and_full_are_separate_entries()
    {
        var cache = new SceneListCache(TimeSpan.FromMinutes(1));
        var lightBuilds = 0;
        var fullBuilds = 0;
        cache.GetOrBuild("P", false, () =>
        {
            lightBuilds++;
            return new[] { new SceneSummary { SceneNumber = 1 } };
        });
        cache.GetOrBuild("P", true, () =>
        {
            fullBuilds++;
            return new[] { new SceneSummary { SceneNumber = 2 } };
        });
        cache.GetOrBuild("P", false, () =>
        {
            lightBuilds++;
            return Array.Empty<SceneSummary>();
        });
        Assert.Equal(1, lightBuilds);
        Assert.Equal(1, fullBuilds);
    }

    [Fact]
    public void Invalidate_clears_both_keys()
    {
        var cache = new SceneListCache(TimeSpan.FromMinutes(1));
        var builds = 0;
        IReadOnlyList<SceneSummary> Build()
        {
            builds++;
            return new[] { new SceneSummary { SceneNumber = builds } };
        }

        cache.GetOrBuild("P", false, Build);
        cache.GetOrBuild("P", true, Build);
        Assert.Equal(2, builds);
        cache.Invalidate("P");
        cache.GetOrBuild("P", false, Build);
        Assert.Equal(3, builds);
    }

    [Fact]
    public async Task Single_flight_only_one_build()
    {
        var cache = new SceneListCache(TimeSpan.FromMinutes(1));
        var builds = 0;
        var gate = new TaskCompletionSource();

        IReadOnlyList<SceneSummary> Build()
        {
            Interlocked.Increment(ref builds);
            gate.Task.GetAwaiter().GetResult();
            return new[] { new SceneSummary { SceneNumber = 1 } };
        }

        var t1 = Task.Run(() => cache.GetOrBuild("P", false, Build));
        await Task.Delay(50);
        var t2 = Task.Run(() => cache.GetOrBuild("P", false, Build));
        await Task.Delay(50);
        // First builder is inside; second should be waiting on single-flight
        Assert.Equal(1, builds);
        gate.SetResult();
        await Task.WhenAll(t1, t2);
        Assert.Equal(1, builds);
    }
}
