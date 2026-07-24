using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class ReviewIndexServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private readonly ReviewIndexService _index;
    private readonly EditLogService _logs;

    public ReviewIndexServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-review-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects"));
        Directory.CreateDirectory(Path.Combine(_root, "prompts"));

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root });
        _store = new ProjectStore(opts);
        var learning = new ReviewEventStore(_store, NullLogger<ReviewEventStore>.Instance);
        _logs = new EditLogService(_store, learning, NullLogger<EditLogService>.Instance);
        _index = new ReviewIndexService(_store, _logs, NullLogger<ReviewIndexService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private string CreateProjectWithClips(string id, params (int Scene, int Clip)[] clips)
    {
        var dir = Path.Combine(_root, "projects", id);
        Directory.CreateDirectory(Path.Combine(dir, "assets", "video"));
        Directory.CreateDirectory(Path.Combine(dir, "assets", "review"));
        File.WriteAllText(Path.Combine(dir, "project.json"),
            """{"id":"Demo","name":"Demo"}""");
        File.WriteAllText(Path.Combine(dir, "pipeline_state.json"), "{}");
        foreach (var (scene, clip) in clips)
        {
            var path = Path.Combine(dir, "assets", "video",
                $"scene_{scene:D2}_clip_{clip:D2}.mp4");
            File.WriteAllBytes(path, new byte[2048]);
        }
        return dir;
    }

    [Fact]
    public void Rebuild_lists_on_disk_clips()
    {
        CreateProjectWithClips("P1", (1, 1), (1, 2), (2, 1));
        var doc = _index.Rebuild("P1");
        Assert.Equal("P1", doc.ProjectId);
        Assert.Equal(3, doc.Clips.Count);
        Assert.Equal("S01C01", doc.Clips[0].Key);
        Assert.True(doc.Clips[0].VideoExists);
        Assert.False(doc.Clips[0].HasDraft);
        Assert.True(File.Exists(_index.IndexPath("P1")));
    }

    [Fact]
    public void Rebuild_scene_filter()
    {
        CreateProjectWithClips("P2", (1, 1), (2, 1), (2, 2));
        var doc = _index.Rebuild("P2", sceneFilter: 2);
        Assert.Equal(2, doc.Clips.Count);
        Assert.All(doc.Clips, c => Assert.Equal(2, c.Scene));
    }

    [Fact]
    public void PersistDurableFrames_and_upsert_with_draft()
    {
        var dir = CreateProjectWithClips("P3", (1, 1));
        var tmpFrames = Path.Combine(dir, "tmp_frames");
        Directory.CreateDirectory(tmpFrames);
        var f1 = Path.Combine(tmpFrames, "a.jpg");
        var f2 = Path.Combine(tmpFrames, "b.jpg");
        File.WriteAllBytes(f1, new byte[64]);
        File.WriteAllBytes(f2, new byte[64]);

        var rel = _index.PersistDurableFrames("P3", 1, 1, new[] { f1, f2 }, maxFrames: 4);
        Assert.Equal(2, rel.Count);
        Assert.All(rel, p => Assert.StartsWith("assets/review/frames/", p));
        Assert.True(File.Exists(Path.Combine(dir, rel[0].Replace('/', Path.DirectorySeparatorChar))));

        var draft = new ClipAutoReviewDraft
        {
            ProjectId = "P3",
            Scene = 1,
            Clip = 1,
            Suggestion = "fail",
            Category = "wrong_style",
            Note = "illustrated faces",
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(
            Path.Combine(dir, "assets", "review", "S01C01.auto_review.json"),
            System.Text.Json.JsonSerializer.Serialize(draft));

        var doc = _index.UpsertClip("P3", 1, 1, rel, draft);
        var row = Assert.Single(doc.Clips);
        Assert.Equal("fail", row.AutoSuggestion);
        Assert.Equal("wrong_style", row.AutoCategory);
        Assert.True(row.HasDraft);
        Assert.Equal(2, row.FramePaths.Count);
        Assert.False(row.AssemblyEligible); // auto-fail without human override
    }

    [Fact]
    public void HasDraft_and_only_missing_list()
    {
        var dir = CreateProjectWithClips("P4", (1, 1), (1, 2));
        File.WriteAllText(
            Path.Combine(dir, "assets", "review", "S01C01.auto_review.json"),
            """{"suggestion":"pass","category":"ok"}""");
        Assert.True(_index.HasDraft("P4", 1, 1));
        Assert.False(_index.HasDraft("P4", 1, 2));

        var coords = _index.ListOnDiskClipCoords("P4")
            .Where(c => !_index.HasDraft("P4", c.Scene, c.Clip))
            .ToList();
        Assert.Single(coords);
        Assert.Equal((1, 2), coords[0]);
    }

    [Fact]
    public void Load_round_trips_camelCase_index()
    {
        CreateProjectWithClips("P5", (1, 1));
        _index.Rebuild("P5");
        var loaded = _index.Load("P5");
        Assert.NotNull(loaded);
        Assert.Equal("P5", loaded!.ProjectId);
        Assert.Single(loaded.Clips);
        Assert.Equal("S01C01", loaded.Clips[0].Key);
    }
}
