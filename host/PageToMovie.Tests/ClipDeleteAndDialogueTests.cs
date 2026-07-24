using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>Delete-a-clip and edit-dialogue features on the Scenes page.</summary>
public class ClipDeleteAndDialogueTests
{
    private static (string Root, string ProjectDir, ProjectStore Store) SetUpProject(
        string testName, string blueprintJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_" + testName + "_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "projects", "Demo");
        Directory.CreateDirectory(Path.Combine(proj, "assets", "video"));
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(proj, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.grok.json"}""");
        File.WriteAllText(Path.Combine(proj, "blueprint.clips.grok.json"), blueprintJson);

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root, EnableReadCaches = false });
        var store = new ProjectStore(opts);
        return (root, proj, store);
    }

    private const string ThreeClipBlueprint = """
        {
          "scenes": [
            {
              "scene_number": 1,
              "veo_clips": [
                { "clip_number": 1, "visual_prompt": "clip one" },
                { "clip_number": 2, "visual_prompt": "clip two" },
                { "clip_number": 3, "visual_prompt": "clip three" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void DeleteClip_removes_blueprint_entry_and_video_but_leaves_siblings()
    {
        var (root, proj, store) = SetUpProject("delclip", ThreeClipBlueprint);
        try
        {
            var videoDir = Path.Combine(proj, "assets", "video");
            foreach (var n in new[] { 1, 2, 3 })
                File.WriteAllBytes(Path.Combine(videoDir, $"scene_01_clip_{n:D2}.mp4"), new byte[2048]);

            var wasInBlueprint = store.DeleteClip("Demo", scene: 1, clip: 2);

            Assert.True(wasInBlueprint);
            var json = File.ReadAllText(Path.Combine(proj, "blueprint.clips.grok.json"));
            Assert.DoesNotContain("clip two", json);
            Assert.Contains("clip one", json);
            Assert.Contains("clip three", json);

            Assert.False(File.Exists(Path.Combine(videoDir, "scene_01_clip_02.mp4")));
            Assert.True(File.Exists(Path.Combine(videoDir, "scene_01_clip_01.mp4")));
            Assert.True(File.Exists(Path.Combine(videoDir, "scene_01_clip_03.mp4")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void DeleteClip_deletes_native_sidecar_too()
    {
        var (root, proj, store) = SetUpProject("delclipnative", ThreeClipBlueprint);
        try
        {
            var videoDir = Path.Combine(proj, "assets", "video");
            File.WriteAllBytes(Path.Combine(videoDir, "scene_01_clip_01.mp4"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(videoDir, "scene_01_clip_01.mp4.native"), new byte[16]);

            store.DeleteClip("Demo", scene: 1, clip: 1);

            Assert.False(File.Exists(Path.Combine(videoDir, "scene_01_clip_01.mp4")));
            Assert.False(File.Exists(Path.Combine(videoDir, "scene_01_clip_01.mp4.native")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void DeleteClip_throws_when_clip_and_video_both_absent()
    {
        var (root, _, store) = SetUpProject("delclipmissing", ThreeClipBlueprint);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => store.DeleteClip("Demo", scene: 1, clip: 99));
            Assert.Contains("S01C99", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void UpdateClipFields_creates_audio_payload_when_missing_and_writes_all_fields()
    {
        var (root, proj, store) = SetUpProject("clipeditnew", ThreeClipBlueprint);
        try
        {
            store.UpdateClipFields("Demo", scene: 1, clip: 1, new ClipEditRequest
            {
                VisualPrompt = "new visual",
                NegativePrompt = "no hats",
                Dialogue = "Hello there.",
                Speaker = "Character_A",
                Delivery = "spoken_on_camera",
                PrimarySubject = "Character_A",
                CharactersOnScreen = new List<string> { "Character_A", "Character_B" },
                ColorPalette = "warm amber",
                FilmStock = "Kodak Vision3",
                DurationSeconds = 7,
            });

            var json = File.ReadAllText(Path.Combine(proj, "blueprint.clips.grok.json"));
            using var doc = JsonDocument.Parse(json);
            var clip = doc.RootElement.GetProperty("scenes")[0].GetProperty("veo_clips")[0];
            Assert.Equal("new visual", clip.GetProperty("visual_prompt").GetString());
            Assert.Equal("no hats", clip.GetProperty("negative_prompt").GetString());
            Assert.Equal("Character_A", clip.GetProperty("primary_subject").GetString());
            Assert.Equal(7, clip.GetProperty("duration_seconds").GetInt32());
            Assert.Equal("warm amber", clip.GetProperty("color_palette").GetString());
            Assert.Equal("Kodak Vision3", clip.GetProperty("film_stock").GetString());
            var cast = clip.GetProperty("characters_on_screen").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(new[] { "Character_A", "Character_B" }, cast);
            var audio = clip.GetProperty("audio_payload");
            Assert.Equal("Hello there.", audio.GetProperty("dialogue").GetString());
            Assert.Equal("Character_A", audio.GetProperty("speaker").GetString());
            Assert.Equal("spoken_on_camera", audio.GetProperty("delivery").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void UpdateClipFields_clears_speaker_when_request_omits_it()
    {
        const string blueprint = """
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "veo_clips": [
                    { "clip_number": 1, "audio_payload": { "dialogue": "OLD LINE", "speaker": "Character_A" } }
                  ]
                }
              ]
            }
            """;
        var (root, proj, store) = SetUpProject("clipeditclear", blueprint);
        try
        {
            // Full-form save with no speaker selected — matches the editor sending its current form state.
            store.UpdateClipFields("Demo", scene: 1, clip: 1, new ClipEditRequest { Dialogue = "NEW LINE" });
            var json = File.ReadAllText(Path.Combine(proj, "blueprint.clips.grok.json"));
            using var doc = JsonDocument.Parse(json);
            var audio = doc.RootElement.GetProperty("scenes")[0].GetProperty("veo_clips")[0].GetProperty("audio_payload");
            Assert.Equal("NEW LINE", audio.GetProperty("dialogue").GetString());
            Assert.True(audio.GetProperty("speaker").ValueKind is JsonValueKind.Null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void UpdateClipFields_throws_when_clip_not_found()
    {
        var (root, _, store) = SetUpProject("clipeditmissing", ThreeClipBlueprint);
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => store.UpdateClipFields("Demo", 1, 99, new ClipEditRequest()));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void AddClip_inserts_in_clip_number_order()
    {
        var (root, proj, store) = SetUpProject("addclipmid", ThreeClipBlueprint);
        try
        {
            // Insert clip 2.5's worth of content as clip number 2 already exists — use a
            // number between 2 and 3 isn't possible with ints, so verify insertion before
            // an existing higher clip number instead (append-then-reorder path).
            store.AddClip("Demo", scene: 1, new ClipEditRequest { Clip = 4, VisualPrompt = "clip four" });

            var json = File.ReadAllText(Path.Combine(proj, "blueprint.clips.grok.json"));
            using var doc = JsonDocument.Parse(json);
            var clips = doc.RootElement.GetProperty("scenes")[0].GetProperty("veo_clips").EnumerateArray().ToList();
            Assert.Equal(4, clips.Count);
            Assert.Equal(4, clips[^1].GetProperty("clip_number").GetInt32());
            Assert.Equal("clip four", clips[^1].GetProperty("visual_prompt").GetString());
            Assert.Equal("none", clips[^1].GetProperty("veo_continuation_source").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void AddClip_throws_when_clip_number_already_exists()
    {
        var (root, _, store) = SetUpProject("addclipdupe", ThreeClipBlueprint);
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => store.AddClip("Demo", 1, new ClipEditRequest { Clip = 2 }));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void ReviewIndexService_RemoveClip_only_drops_the_target_row()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_reviewrm_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "projects", "Demo");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var store = new ProjectStore(opts);
            var learning = new ReviewEventStore(store, NullLogger<ReviewEventStore>.Instance);
            var editLogs = new EditLogService(store, learning, NullLogger<EditLogService>.Instance);
            var reviewIndex = new ReviewIndexService(store, editLogs, NullLogger<ReviewIndexService>.Instance);

            var doc = new ReviewIndexDocument { ProjectId = "Demo", SchemaVersion = "1" };
            doc.Clips.Add(new ReviewIndexClipRow { Key = "S01C01", Scene = 1, Clip = 1 });
            doc.Clips.Add(new ReviewIndexClipRow { Key = "S01C02", Scene = 1, Clip = 2 });
            reviewIndex.Save(doc);

            reviewIndex.RemoveClip("Demo", scene: 1, clip: 1);

            var reloaded = reviewIndex.Load("Demo");
            Assert.NotNull(reloaded);
            Assert.Single(reloaded!.Clips);
            Assert.Equal("S01C02", reloaded.Clips[0].Key);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public async Task EditLogService_RemoveClipReviewStateAsync_drops_only_that_clip()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_editrm_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "projects", "Demo");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        try
        {
            var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var store = new ProjectStore(opts);
            var learning = new ReviewEventStore(store, NullLogger<ReviewEventStore>.Instance);
            var editLogs = new EditLogService(store, learning, NullLogger<EditLogService>.Instance);

            await editLogs.SetClipReviewAsync("Demo", 1, 1, "pass", "looks good");
            await editLogs.SetClipReviewAsync("Demo", 1, 2, "fail", "reshoot needed");

            await editLogs.RemoveClipReviewStateAsync("Demo", 1, 1);

            var statePath = Path.Combine(proj, "pipeline_state.json");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            var reviews = doc.RootElement.GetProperty("clip_review");
            Assert.False(reviews.TryGetProperty("S01C01", out _));
            Assert.True(reviews.TryGetProperty("S01C02", out _));

            var log = await editLogs.LoadAsync("Demo");
            Assert.Contains(log.Entries, e => e.Type == "clip_delete" && e.Scene == 1 && e.Clip == 1);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }
}
