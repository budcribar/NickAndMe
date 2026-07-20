using FilmStudio.Core.Options;
using FilmStudio.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

public class ProjectArtifactIndexServiceTests : IDisposable
{
    private readonly string _root;

    public ProjectArtifactIndexServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-artidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects"));
        Directory.CreateDirectory(Path.Combine(_root, "prompts"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* */ }
    }

    [Fact]
    public async Task Rebuild_writes_md_json_and_telemetry_snapshots()
    {
        var proj = Path.Combine(_root, "projects", "Demo");
        Directory.CreateDirectory(Path.Combine(proj, "source"));
        Directory.CreateDirectory(Path.Combine(proj, "assets", "video", "prompts"));
        Directory.CreateDirectory(Path.Combine(proj, "assets", "characters"));
        Directory.CreateDirectory(Path.Combine(proj, "assets", "review"));
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(proj, "pipeline_state.json"), """{"cost_ledger":[]}""");
        File.WriteAllText(Path.Combine(proj, "source", "screenplay.fountain"), "Title: Demo\n");
        File.WriteAllText(Path.Combine(proj, "source", "book_full.txt"), "Once upon a time.");
        File.WriteAllText(Path.Combine(proj, "source", "cast_seeds.json"), "{}");
        File.WriteAllText(Path.Combine(proj, "project_rules.json"), "[]");
        File.WriteAllText(Path.Combine(proj, "blueprint.clips.grok.json"), """{"scenes":[]}""");
        File.WriteAllBytes(Path.Combine(proj, "assets", "movie_wip.mp4"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(proj, "assets", "video", "scene_01_clip_01.mp4"), new byte[2048]);
        File.WriteAllText(Path.Combine(proj, "assets", "video", "prompts", "S01C01.txt"), "PROMPT");
        File.WriteAllText(Path.Combine(proj, "assets", "video", "prompts", "S01C01.meta.json"), "{}");
        File.WriteAllText(Path.Combine(proj, "assets", "review", "index.json"), """{"clips":[]}""");

        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = _root });
        var store = new ProjectStore(opts);
        var costs = new CostReportService(store);
        var svc = new ProjectArtifactIndexService(
            store, costs, opts, NullLogger<ProjectArtifactIndexService>.Instance);

        var doc = await svc.RebuildAsync("Demo");
        Assert.Equal("Demo", doc.ProjectId);
        Assert.True(File.Exists(svc.ArtifactsMdPath("Demo")));
        Assert.True(File.Exists(svc.IndexJsonPath("Demo")));
        Assert.True(File.Exists(Path.Combine(proj, "telemetry", "cost_ledger.json")));
        Assert.True(File.Exists(Path.Combine(proj, "telemetry", "models.json")));
        Assert.True(File.Exists(Path.Combine(proj, "telemetry", "INDEX.md")));
        Assert.Contains(doc.Entries, e => e.Path == "assets/movie_wip.mp4" && e.Exists);
        Assert.True(doc.Stats.ContainsKey("clipMp4Count"));
    }
}
