using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FilmStudio.Core.Options;
using FilmStudio.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

public class CreditsGeneratorTests
{
    [Fact]
    public void FormatCreditsText_includes_story_software_nick_repo_and_fair_use()
    {
        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = Path.GetTempPath() });
        var store = new ProjectStore(opts);
        var service = new CreditsGeneratorService(store, opts, NullLogger<CreditsGeneratorService>.Instance);

        var formatted = service.FormatCreditsText("The Tell-Tale Heart", "Edgar Allan Poe");

        Assert.Contains("THE TELL-TALE HEART", formatted);
        Assert.Contains("Written by Edgar Allan Poe", formatted);
        Assert.Contains("Filmmaking Software: FilmStudio", formatted);
        Assert.Contains("Software Author: Bud Cribar", formatted);
        Assert.Contains("https://github.com/budcribar/FilmStudio", formatted);
        Assert.Contains("Fair Use", formatted);
    }

    [Fact]
    public void ExtractStoryTitleAndAuthor_parses_fountain_headers()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fs-credits-test-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tmp, "projects", "TestProject", "source");
        Directory.CreateDirectory(sourceDir);

        var fountain = """
            Title: The Tell-Tale Heart
            Author: Edgar Allan Poe
            Credit: Written by

            FADE IN:
            """;
        File.WriteAllText(Path.Combine(sourceDir, "screenplay.fountain"), fountain);

        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = tmp });
        var store = new ProjectStore(opts);
        var service = new CreditsGeneratorService(store, opts, NullLogger<CreditsGeneratorService>.Instance);

        var (title, author) = service.ExtractStoryTitleAndAuthor("TestProject");

        Assert.Equal("The Tell-Tale Heart", title);
        Assert.Equal("Edgar Allan Poe", author);

        try { Directory.Delete(tmp, true); } catch { }
    }

    [Fact]
    public void AreAllScenesComplete_returns_true_when_all_blueprint_scenes_have_videos()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fs-credits-scenes-" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(tmp, "projects", "TestProject");
        var videoDir = Path.Combine(projDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var bp = """
            {
              "scenes": [
                { "scene_number": 1 },
                { "scene_number": 2 }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(projDir, "blueprint.clips.grok.json"), bp);
        File.WriteAllBytes(Path.Combine(videoDir, "scene_01.mp4"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoDir, "scene_02.mp4"), new byte[2048]);

        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = tmp });
        var store = new ProjectStore(opts);
        var service = new CreditsGeneratorService(store, opts, NullLogger<CreditsGeneratorService>.Instance);

        Assert.True(service.AreAllScenesComplete("TestProject"));

        try { Directory.Delete(tmp, true); } catch { }
    }
    
    //[Fact]
    public async Task EnsureCreditsClipAsync_generates_credits_mp4()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fs-credits-gen-" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(tmp, "projects", "TestProject");
        var videoDir = Path.Combine(projDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = tmp });
        var store = new ProjectStore(opts);
        var service = new CreditsGeneratorService(store, opts, NullLogger<CreditsGeneratorService>.Instance);

        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Resources", "ffmpeg.exe");
        if (File.Exists(ffmpegPath))
        {
            var path = await service.EnsureCreditsClipAsync("TestProject", ffmpegPath);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path!).Length > 1024);
        }

        try { Directory.Delete(tmp, true); } catch { }
    }
}
