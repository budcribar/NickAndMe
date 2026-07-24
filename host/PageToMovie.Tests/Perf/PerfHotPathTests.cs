using System.Diagnostics;
using PageToMovie.Api.Services;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests.Perf;

/// <summary>
/// Lightweight performance smoke tests for hot-path components (not full LoadSim).
/// Thresholds are loose so CI stays green on varied hardware; failures mean major regressions.
/// </summary>
public class PerfHotPathTests
{
    [Fact]
    public void HttpRequestMetrics_Record_is_fast()
    {
        var m = new HttpRequestMetrics();
        const int n = 200_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
            m.Record(i % 3 == 0 ? "/api/projects/x/scenes" : i % 3 == 1 ? "/api/jobs" : "/api/admin/state");
        sw.Stop();

        var snap = m.Snapshot();
        Assert.True(snap.RequestsLast30Sec >= 0);
        // ~200k records should finish well under 2s on modern hardware
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"HttpRequestMetrics.Record too slow: {sw.ElapsedMilliseconds}ms for {n}");
    }

    [Fact]
    public async Task ProjectReadCache_projects_list_avoids_rebuild()
    {
        var cache = new ProjectReadCache();
        cache.Enabled = true;
        var builds = 0;
        Task<IReadOnlyList<ProjectInfo>> Build(CancellationToken _)
        {
            builds++;
            Thread.Sleep(5); // simulate I/O
            return Task.FromResult<IReadOnlyList<ProjectInfo>>(new[]
            {
                new ProjectInfo { Id = "P", Path = "/p" },
            });
        }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
            _ = await cache.GetOrBuildProjectsAsync(Build);
        sw.Stop();

        Assert.Equal(1, builds);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Cached project list lookups too slow: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SceneListCache_single_flight()
    {
        var cache = new SceneListCache();
        var builds = 0;
        async Task<IReadOnlyList<SceneSummary>> Build(CancellationToken ct)
        {
            Interlocked.Increment(ref builds);
            await Task.Delay(30, ct);
            return new List<SceneSummary> { new() { SceneNumber = 1 } };
        }

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrBuildAsync("proj", probeDurations: false, Build))
            .ToArray();
        await Task.WhenAll(tasks);

        // Single-flight: concurrent first wave should build once (or very few times)
        Assert.True(builds <= 2, $"Expected single-flight, builds={builds}");
    }

    [Fact]
    public void ReviewEventStore_append_throughput()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_perf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var store = new ProjectStore(opts);
            var events = new ReviewEventStore(store, NullLogger<ReviewEventStore>.Instance);

            const int n = 2_000;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
            {
                events.Append(new ReviewLearningEvent
                {
                    ProjectId = "P",
                    Type = "clip_fail",
                    Category = "continuity",
                    Note = "n" + i,
                    Scene = 1,
                    Clip = (i % 10) + 1,
                });
            }
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 8000,
                $"Append {n} events too slow: {sw.ElapsedMilliseconds}ms");

            var sw2 = Stopwatch.StartNew();
            var insights = events.BuildInsights("P");
            sw2.Stop();
            Assert.Equal(n, insights.EventCount);
            Assert.True(sw2.ElapsedMilliseconds < 2500,
                $"BuildInsights too slow: {sw2.ElapsedMilliseconds}ms");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void ClipDurationEstimator_batch_is_fast()
    {
        const int n = 50_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
        {
            _ = ClipDurationEstimator.Estimate(
                dialogue: i % 2 == 0 ? "Hello there friend how are you today" : "",
                visualOrAction: "Wide shot of the room with soft light",
                actionClass: "dialogue",
                delivery: "on_camera");
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 4000,
            $"ClipDurationEstimator {n} too slow: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void JobListHelpers_PickPrimary_is_fast()
    {
        var jobs = new List<JobSnapshot>();
        for (var i = 0; i < 5_000; i++)
        {
            jobs.Add(new JobSnapshot
            {
                JobId = "j" + i,
                Status = i == 123 ? "running" : i % 10 == 0 ? "queued" : "done",
                QueuedAt = DateTimeOffset.UtcNow.AddSeconds(-i),
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-i),
                FinishedAt = DateTimeOffset.UtcNow.AddSeconds(-i),
            });
        }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            _ = JobListHelpers.PickPrimary(jobs);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"PickPrimary too slow: {sw.ElapsedMilliseconds}ms");
        Assert.Equal("j123", JobListHelpers.PickPrimary(jobs)!.JobId);
    }

    [Fact]
    public void SupportedModelCatalog_resolve_is_fast()
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
        {
            _ = SupportedModelCatalog.ResolveOrDefault("grok-imagine-video", ModelCapability.Video);
            _ = SupportedModelCatalog.ProviderIdFor("grok-4.5", ModelCapability.Chat);
        }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Catalog resolve too slow: {sw.ElapsedMilliseconds}ms");
    }
}
