using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Project-wide cast gate: every character needs voice + locked image (voice-only: voice only)
/// before video gen to avoid wasted API spend.
/// </summary>
public class CastReadinessTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "CastGate";

    public CastReadinessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-cast-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId));
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root });
        _store = new ProjectStore(opts);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void Empty_cast_is_not_ready()
    {
        var status = _store.ReadCastStatus(ProjectId);
        Assert.Equal(0, status.Total);
        Assert.False(status.ReadyForShots);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.NotEmpty(missing);
        Assert.Contains(missing, m => m.Contains("no cast seeds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Voice_only_with_voice_is_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "display_name_policy": "never_on_screen",
                  "voice_profile": "calm storyteller, mid pitch"
                }
              }
            }
            """);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.Equal(1, status.Total);
        Assert.Equal(1, status.Ready);
        Assert.True(status.ReadyForShots);
        Assert.Empty(status.Missing);
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void Voice_only_without_voice_is_not_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "display_name_policy": "never_on_screen",
                  "voice_profile": ""
                }
              }
            }
            """);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);
        Assert.Contains("Character_Narrator", status.Missing);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void On_screen_with_variant_but_no_lock_is_not_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Hero": {
                  "voice_profile": "warm mid pitch",
                  "description": "a hero"
                }
              }
            }
            """);

        // Unlocked draft variant only — HasPreferred true, Locked false
        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_hero_variant_01.png"), new byte[128]);

        var rows = _store.ListCharacters(ProjectId);
        var hero = Assert.Single(rows);
        Assert.True(hero.HasPreferred);
        Assert.False(hero.Locked);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);
        Assert.Contains("Character_Hero", status.Missing);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("locked image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void On_screen_with_locked_image_and_voice_is_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Hero": {
                  "voice_profile": "warm mid pitch",
                  "description": "a hero"
                },
                "Character_Narrator": {
                  "display_name_policy": "never_on_screen",
                  "voice_profile": "calm storyteller"
                }
              }
            }
            """);

        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_hero_ref.png"), new byte[128]);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.Equal(2, status.Total);
        Assert.Equal(2, status.Ready);
        Assert.True(status.ReadyForShots);
        Assert.Empty(status.Missing);
        Assert.Empty(_store.GetCastNotReadyForVideo(ProjectId));
    }

    [Fact]
    public void On_screen_locked_without_voice_is_not_ready()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Hero": {
                  "voice_profile": "",
                  "description": "a hero"
                }
              }
            }
            """);

        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_hero_ref.png"), new byte[128]);

        var status = _store.ReadCastStatus(ProjectId);
        Assert.False(status.ReadyForShots);

        var missing = _store.GetCastNotReadyForVideo(ProjectId);
        Assert.Contains(missing, m => m.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    private void WriteSeeds(string json)
    {
        var source = Path.Combine(_store.GetProjectDir(ProjectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "cast_seeds.json"), json);
    }
}
