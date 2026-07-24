using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Shared wardrobe/uniform lock plumbing (wardrobe_lock_tokens in cast_seeds.json) — lets
/// several characters (e.g. three police officers) point at one costume description and one
/// shared reference plate instead of each independently re-describing/re-generating the
/// uniform. See CharacterDesignService.EnsureWardrobeReferenceAsync for the generation side.
/// </summary>
public sealed class WardrobeLockTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "WardrobeGate";

    public WardrobeLockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-wardrobe-" + Guid.NewGuid().ToString("N"));
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

    private void WriteSeeds(string json)
    {
        var source = Path.Combine(_store.GetProjectDir(ProjectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "cast_seeds.json"), json);
    }

    [Fact]
    public void GetWardrobeLock_reads_shared_group_from_cast_seeds()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_OfficerReynolds": {
                  "description": "solid competent build",
                  "wardrobe_lock": "Wardrobe_PoliceOfficer"
                },
                "Character_OfficerHale": {
                  "description": "medium build",
                  "wardrobe_lock": "Wardrobe_PoliceOfficer"
                }
              },
              "wardrobe_lock_tokens": {
                "Wardrobe_PoliceOfficer": {
                  "description": "dark navy double-breasted coat, peaked cap, round badge"
                }
              }
            }
            """);

        var reynolds = _store.GetCharacterSeed(ProjectId, "Character_OfficerReynolds");
        Assert.NotNull(reynolds);
        Assert.Equal("Wardrobe_PoliceOfficer", ProjectStore.GetWardrobeLockKey(reynolds!.Value));

        var group = _store.GetWardrobeLock(ProjectId, "Wardrobe_PoliceOfficer");
        Assert.NotNull(group);
        Assert.Equal(
            "dark navy double-breasted coat, peaked cap, round badge",
            group!.Value.GetProperty("description").GetString());

        // Both officers resolve to the SAME group token — that's the point.
        var viaReynolds = _store.ResolveCharacterWardrobeLock(ProjectId, "Character_OfficerReynolds");
        var viaHale = _store.ResolveCharacterWardrobeLock(ProjectId, "Character_OfficerHale");
        Assert.NotNull(viaReynolds);
        Assert.NotNull(viaHale);
        Assert.Equal(
            viaReynolds!.Value.GetProperty("description").GetString(),
            viaHale!.Value.GetProperty("description").GetString());
    }

    [Fact]
    public void Character_without_wardrobe_lock_resolves_to_null()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "description": "lean pale man"
                }
              }
            }
            """);

        var narrator = _store.GetCharacterSeed(ProjectId, "Character_Narrator");
        Assert.NotNull(narrator);
        Assert.Null(ProjectStore.GetWardrobeLockKey(narrator!.Value));
        Assert.Null(_store.ResolveCharacterWardrobeLock(ProjectId, "Character_Narrator"));
    }

    [Theory]
    [InlineData("Wardrobe_PoliceOfficer", "wardrobe_policeofficer_ref.png")]
    [InlineData("police officer", "wardrobe_police_officer_ref.png")]
    public void WardrobeRefFileName_is_stable_and_namespaced(string key, string expected)
    {
        Assert.Equal(expected, ProjectStore.WardrobeRefFileName(key));
    }

    [Fact]
    public void ResolveWardrobeRefPath_null_until_plate_exists_on_disk()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {},
              "wardrobe_lock_tokens": { "Wardrobe_PoliceOfficer": { "description": "x" } }
            }
            """);

        Assert.Null(_store.ResolveWardrobeRefPath(ProjectId, "Wardrobe_PoliceOfficer"));

        var charDir = Path.Combine(_store.GetProjectDir(ProjectId), "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(
            Path.Combine(charDir, ProjectStore.WardrobeRefFileName("Wardrobe_PoliceOfficer")),
            new byte[128]);

        var resolved = _store.ResolveWardrobeRefPath(ProjectId, "Wardrobe_PoliceOfficer");
        Assert.NotNull(resolved);
        Assert.EndsWith("wardrobe_policeofficer_ref.png", resolved);
    }

    [Fact]
    public void UpdateWardrobeLockText_round_trips_through_GetWardrobeLock()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {},
              "wardrobe_lock_tokens": {
                "Wardrobe_PoliceOfficer": { "description": "old text" }
              }
            }
            """);

        _store.UpdateWardrobeLockText(
            ProjectId,
            "Wardrobe_PoliceOfficer",
            description: "dark navy coat, rounded peaked cap, round badge");

        var updated = _store.GetWardrobeLock(ProjectId, "Wardrobe_PoliceOfficer");
        Assert.NotNull(updated);
        Assert.Contains("rounded peaked cap", updated!.Value.GetProperty("description").GetString());
    }

    [Fact]
    public void UpdateWardrobeLockText_creates_group_when_missing()
    {
        WriteSeeds("""
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {}
            }
            """);

        Assert.Null(_store.GetWardrobeLock(ProjectId, "Wardrobe_Soldier"));

        _store.UpdateWardrobeLockText(ProjectId, "Wardrobe_Soldier", description: "olive drab uniform");

        var created = _store.GetWardrobeLock(ProjectId, "Wardrobe_Soldier");
        Assert.NotNull(created);
        Assert.Equal("olive drab uniform", created!.Value.GetProperty("description").GetString());
    }
}
