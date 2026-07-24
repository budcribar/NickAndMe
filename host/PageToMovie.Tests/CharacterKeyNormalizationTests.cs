using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Stage2 scene/clip data can reference a character under a naming/article variant
/// (Character_The_Old_Man) that differs from cast_seeds.json's real key (Character_OldMan).
/// Commit 150db61 fixed this for character description/visual-lock text and voice lock via
/// Stage2PlannerService.NormalizeCharacterKey, but the reference-IMAGE lookup
/// (ClipVideoPromptBuilder.ResolveCharacterRefPathPublic / ProjectStore.ResolveCharacterRefPath)
/// still did a literal-key-only lookup and silently returned no image for the mismatched key —
/// meaning on-screen characters could render with zero locked reference photo despite having
/// one on disk under a slightly different key spelling.
/// </summary>
public sealed class CharacterKeyNormalizationTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "KeyNormGate";

    public CharacterKeyNormalizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-keynorm-" + Guid.NewGuid().ToString("N"));
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

    private string CharDir()
    {
        var dir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteSeeds(string json)
    {
        var source = Path.Combine(_store.GetProjectDir(ProjectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "cast_seeds.json"), json);
    }

    [Fact]
    public void ProjectStore_resolves_placeholder_key_to_real_locked_file()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_OldMan": { "description": "frail elderly man" }
              }
            }
            """);
        File.WriteAllBytes(Path.Combine(CharDir(), "character_old_man_ref.png"), new byte[128]);

        // Literal key still resolves directly.
        Assert.NotNull(_store.ResolveCharacterRefPath(ProjectId, "Character_OldMan"));

        // Stage2-style placeholder key ("The_" article + underscore-per-word) must resolve
        // to the SAME file via normalized fallback, not return null.
        var viaPlaceholder = _store.ResolveCharacterRefPath(ProjectId, "Character_The_Old_Man");
        Assert.NotNull(viaPlaceholder);
        Assert.EndsWith("character_old_man_ref.png", viaPlaceholder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectStore_normalized_fallback_never_matches_a_wardrobe_plate()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_OfficerReynolds": { "description": "x", "wardrobe_lock": "Wardrobe_PoliceOfficer" }
              },
              "wardrobe_lock_tokens": { "Wardrobe_PoliceOfficer": { "description": "y" } }
            }
            """);
        // Only a shared costume plate exists — no character portrait at all.
        File.WriteAllBytes(Path.Combine(CharDir(), "wardrobe_policeofficer_ref.png"), new byte[128]);

        Assert.Null(_store.ResolveCharacterRefPath(ProjectId, "Character_Officer_Reynolds"));
    }

    [Fact]
    public void ClipVideoPromptBuilder_resolves_placeholder_key_via_disk_scan()
    {
        var dir = Path.Combine(_root, "standalone_project");
        var charDir = Path.Combine(dir, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_narrator_ref.png"), new byte[128]);

        Assert.NotNull(ClipVideoPromptBuilder.ResolveCharacterRefPathPublic(dir, "Character_Narrator"));
        var viaPlaceholder = ClipVideoPromptBuilder.ResolveCharacterRefPathPublic(dir, "Character_The_Narrator");
        Assert.NotNull(viaPlaceholder);
        Assert.EndsWith("character_narrator_ref.png", viaPlaceholder, StringComparison.OrdinalIgnoreCase);
    }
}
