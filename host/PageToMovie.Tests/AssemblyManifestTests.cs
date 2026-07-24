using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class AssemblyManifestTests
{
    [Fact]
    public void PartitionSceneClipsForAssembly_excludes_auto_fail()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs-asm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var proj = Path.Combine(root, "projects", "P");
            var video = Path.Combine(proj, "assets", "video");
            Directory.CreateDirectory(video);
            Directory.CreateDirectory(Path.Combine(root, "prompts"));
            File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"P"}""");
            File.WriteAllText(Path.Combine(proj, "pipeline_state.json"), """
                {
                  "clip_auto_review": {
                    "S01C01": { "suggestion": "pass", "category": "ok" },
                    "S01C02": { "suggestion": "fail", "category": "wrong_style", "note": "sketch" }
                  },
                  "clip_review": {}
                }
                """);
            File.WriteAllBytes(Path.Combine(video, "scene_01_clip_01.mp4"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(video, "scene_01_clip_02.mp4"), new byte[2048]);

            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root });
            var store = new ProjectStore(opts);
            var learning = new ReviewEventStore(store, NullLogger<ReviewEventStore>.Instance);
            var logs = new EditLogService(store, learning, NullLogger<EditLogService>.Instance);
            var telemetry = new ProjectTelemetryService(store, NullLogger<ProjectTelemetryService>.Instance);
            var remux = new FfmpegRemuxService(
                store, logs, telemetry, opts, NullLogger<FfmpegRemuxService>.Instance);

            var (inc, exc) = remux.PartitionSceneClipsForAssembly("P", 1);
            Assert.Single(inc);
            Assert.Equal(1, inc[0].Clip);
            Assert.Equal("eligible", inc[0].Reason);
            Assert.Single(exc);
            Assert.Equal(2, exc[0].Clip);
            Assert.Contains("wrong_style", exc[0].Reason);

            var doc = FfmpegRemuxService.BuildSceneSourcesDocument(1, inc, exc, true, 6);
            var clips = Assert.IsType<List<Dictionary<string, object?>>>(doc["clips"]);
            Assert.Single(clips);
            var excluded = Assert.IsType<List<Dictionary<string, object?>>>(doc["excluded"]);
            Assert.Single(excluded);
            Assert.True(clips.Count < inc.Count + exc.Count);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* */ }
        }
    }

    [Fact]
    public void BuildSceneSourcesDocument_includes_and_excludes()
    {
        var included = new List<FfmpegRemuxService.AssemblyClipEntry>
        {
            new()
            {
                Name = "scene_01_clip_01.mp4",
                Scene = 1,
                Clip = 1,
                Reason = "eligible",
                Bytes = 1000,
                MtimeUtc = "2026-01-01T00:00:00.0000000Z",
            },
            new()
            {
                Name = "scene_01_clip_03.mp4",
                Scene = 1,
                Clip = 3,
                Reason = "eligible",
                Bytes = 2000,
            },
        };
        var excluded = new List<FfmpegRemuxService.AssemblyClipEntry>
        {
            new()
            {
                Name = "scene_01_clip_02.mp4",
                Scene = 1,
                Clip = 2,
                Reason = "auto-review fail (wrong_style) — not override-passed",
            },
        };

        var doc = FfmpegRemuxService.BuildSceneSourcesDocument(
            sceneNum: 1,
            included,
            excluded,
            assemblyGate: true,
            totalDurationSeconds: 12.5);

        Assert.Equal(1, doc["scene"]);
        Assert.Equal(2, doc["count"]);
        Assert.Equal(true, doc["assemblyGate"]);
        Assert.Equal(true, doc["strict"]);
        Assert.Equal(12.5, doc["totalDurationSeconds"]);

        var clips = Assert.IsType<List<Dictionary<string, object?>>>(doc["clips"]);
        Assert.Equal(2, clips.Count);
        Assert.Equal("scene_01_clip_01.mp4", clips[0]["name"]);
        Assert.Equal(1, clips[0]["scene"]);
        Assert.Equal(1, clips[0]["clip"]);

        var inc = Assert.IsType<List<Dictionary<string, object?>>>(doc["included"]);
        Assert.Equal(2, inc.Count);
        Assert.Equal("eligible", inc[0]["reason"]);

        var exc = Assert.IsType<List<Dictionary<string, object?>>>(doc["excluded"]);
        Assert.Single(exc);
        Assert.Equal("scene_01_clip_02.mp4", exc[0]["name"]);
        Assert.Equal(2, exc[0]["clip"]);
        Assert.Contains("wrong_style", exc[0]["reason"]?.ToString());

        // clips list is shorter than included+excluded (blocked not in concat)
        Assert.Equal(inc.Count, clips.Count);
        Assert.True(clips.Count < inc.Count + exc.Count);
    }

    [Fact]
    public void BuildSceneSourcesDocument_serializes_to_json()
    {
        var doc = FfmpegRemuxService.BuildSceneSourcesDocument(
            2,
            new[]
            {
                new FfmpegRemuxService.AssemblyClipEntry
                {
                    Name = "scene_02_clip_01.mp4",
                    Scene = 2,
                    Clip = 1,
                    Reason = "eligible",
                },
            },
            new[]
            {
                new FfmpegRemuxService.AssemblyClipEntry
                {
                    Name = "scene_02_clip_02.mp4",
                    Scene = 2,
                    Clip = 2,
                    Reason = "human review = fail",
                },
            },
            assemblyGate: true,
            totalDurationSeconds: 6);

        var json = JsonSerializer.Serialize(doc);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal(2, root.GetProperty("scene").GetInt32());
        Assert.True(root.GetProperty("assemblyGate").GetBoolean());
        Assert.Equal(1, root.GetProperty("clips").GetArrayLength());
        Assert.Equal(1, root.GetProperty("excluded").GetArrayLength());
        Assert.Equal(
            "human review = fail",
            root.GetProperty("excluded")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void BuildSceneSourcesDocument_empty_excluded_when_all_eligible()
    {
        var doc = FfmpegRemuxService.BuildSceneSourcesDocument(
            1,
            new[]
            {
                new FfmpegRemuxService.AssemblyClipEntry
                {
                    Name = "scene_01_clip_01.mp4",
                    Scene = 1,
                    Clip = 1,
                    Reason = "eligible",
                },
            },
            Array.Empty<FfmpegRemuxService.AssemblyClipEntry>(),
            assemblyGate: true);

        var exc = Assert.IsType<List<Dictionary<string, object?>>>(doc["excluded"]);
        Assert.Empty(exc);
        Assert.Equal(1, doc["count"]);
    }
}
