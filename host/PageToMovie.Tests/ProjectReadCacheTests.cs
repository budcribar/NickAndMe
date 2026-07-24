using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectReadCacheTests
{
    [Fact]
    public async Task Projects_list_cached_until_invalidate()
    {
        var cache = new ProjectReadCache();
        var builds = 0;
        Task<IReadOnlyList<ProjectInfo>> Build(CancellationToken _)
        {
            builds++;
            return Task.FromResult<IReadOnlyList<ProjectInfo>>(new[]
            {
                new ProjectInfo { Id = "A", Label = "A", Path = "/tmp/A" },
            });
        }

        var a = await cache.GetOrBuildProjectsAsync(Build);
        var b = await cache.GetOrBuildProjectsAsync(Build);
        Assert.Equal(1, builds);
        Assert.Equal("A", a[0].Id);
        Assert.Equal("A", b[0].Id);

        // Caller mutation must not poison cache
        ((List<ProjectInfo>)a)[0].Id = "mutated";
        var c = await cache.GetOrBuildProjectsAsync(Build);
        Assert.Equal("A", c[0].Id);
        Assert.Equal(1, builds);

        cache.InvalidateProjectsList();
        _ = await cache.GetOrBuildProjectsAsync(Build);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task Blueprint_document_cached_by_mtime_shared()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-read-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "blueprint.json");
            await File.WriteAllTextAsync(path, """{"scenes":[]}""");
            var cache = new ProjectReadCache();
            var a = await cache.GetOrLoadBlueprintDocumentAsync(path);
            var b = await cache.GetOrLoadBlueprintDocumentAsync(path);
            Assert.NotNull(a);
            Assert.Same(a, b); // shared instance — do not dispose
            Assert.Same(
                await cache.GetOrLoadBlueprintUtf8Async(path),
                await cache.GetOrLoadBlueprintUtf8Async(path));

            await Task.Delay(20);
            await File.WriteAllTextAsync(path, """{"scenes":[{"scene_number":1}]}""");
            var c = await cache.GetOrLoadBlueprintDocumentAsync(path);
            Assert.NotNull(c);
            Assert.NotSame(a, c);
            Assert.True(c!.RootElement.GetProperty("scenes").GetArrayLength() == 1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Dir_index_cached_until_dir_mtime_or_invalidate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-dir-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.mp4"), new string('x', 2048));
            var cache = new ProjectReadCache();
            var builds = 0;
            Task<Dictionary<string, long>> Index(string d, CancellationToken _)
            {
                builds++;
                var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(d))
                    map[Path.GetFileName(f)] = new FileInfo(f).Length;
                return Task.FromResult(map);
            }

            var a = await cache.GetOrIndexDirAsync(dir, Index);
            var b = await cache.GetOrIndexDirAsync(dir, Index);
            Assert.Equal(1, builds);
            Assert.True(a.ContainsKey("a.mp4"));
            Assert.True(b.ContainsKey("a.mp4"));

            cache.InvalidateProject("P", dir);
            _ = await cache.GetOrIndexDirAsync(dir, Index);
            Assert.Equal(2, builds);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Blueprint_path_cached_per_project()
    {
        var cache = new ProjectReadCache();
        var finds = 0;
        Task<string?> Find(CancellationToken _)
        {
            finds++;
            return Task.FromResult<string?>("/proj/blueprint.json");
        }

        Assert.Equal("/proj/blueprint.json", await cache.GetOrFindBlueprintPathAsync("Buster", Find));
        Assert.Equal("/proj/blueprint.json", await cache.GetOrFindBlueprintPathAsync("Buster", Find));
        Assert.Equal(1, finds);

        cache.InvalidateProject("Buster", "/proj");
        Assert.Equal("/proj/blueprint.json", await cache.GetOrFindBlueprintPathAsync("Buster", Find));
        Assert.Equal(2, finds);
    }

    [Fact]
    public async Task Disabled_always_rebuilds_projects()
    {
        var cache = new ProjectReadCache { Enabled = false };
        var builds = 0;
        Task<IReadOnlyList<ProjectInfo>> Build(CancellationToken _)
        {
            builds++;
            return Task.FromResult<IReadOnlyList<ProjectInfo>>(
                new[] { new ProjectInfo { Id = "A", Path = "/a" } });
        }

        _ = await cache.GetOrBuildProjectsAsync(Build);
        _ = await cache.GetOrBuildProjectsAsync(Build);
        Assert.Equal(2, builds);
    }
}
