using System.Text.Json;
using FilmStudio.Core;
using Xunit;

namespace FilmStudio.Tests;

public class CharacterLookDistinctnessTests
{
    [Fact]
    public void FindNearDuplicates_flags_copied_description_across_cast()
    {
        var seeds = Seeds(
            ("Character_A", "Lean pale man with dark messy hair and bright eyes.", "lean pale messy hair"),
            ("Character_B", "Broad stocky officer in a period police coat.", "broad stocky police coat"));

        var hits = CharacterLookDistinctness.FindNearDuplicates(
            seeds,
            "Character_C",
            description: "Lean pale man with dark messy hair and bright eyes.",
            visualLock: "something else entirely for the third person");

        Assert.Contains(hits, h =>
            h.OtherCharKey == "Character_A" &&
            h.Field == "description" &&
            h.Score >= CharacterLookDistinctness.NearDuplicateThreshold);
        Assert.DoesNotContain(hits, h => h.OtherCharKey == "Character_B");
    }

    [Fact]
    public void FindNearDuplicates_flags_near_identical_visual_lock()
    {
        var shared = "Constants: broad stocky adult male officer in mid-19th-century police coat and hat; period police uniform; heavier wider build is the distinguisher. Not lean; not elderly.";
        var seeds = Seeds(
            ("Character_Hayes", "Broad-built adult male police officer.", shared),
            ("Character_Clemm", "Younger slim officer with a notebook.", "young slim notebook"));

        var hits = CharacterLookDistinctness.FindNearDuplicates(
            seeds,
            "Character_Reynolds",
            description: "Senior calm officer, average build, greying temples.",
            visualLock: shared);

        Assert.Contains(hits, h => h.OtherCharKey == "Character_Hayes" && h.Field == "visual_lock");
        Assert.DoesNotContain(hits, h => h.OtherCharKey == "Character_Clemm");
    }

    [Fact]
    public void FindNearDuplicates_ignores_self_and_unrelated_looks()
    {
        var seeds = Seeds(
            ("Character_Hero", "Small brown dog with floppy ears.", "brown floppy dog"),
            ("Character_Mom", "Adult woman in a green coat.", "green coat woman"));

        var hits = CharacterLookDistinctness.FindNearDuplicates(
            seeds,
            "Character_Hero",
            description: "Small brown dog with floppy ears.",
            visualLock: "brown floppy dog");

        Assert.Empty(hits);
    }

    [Fact]
    public void FindNearDuplicates_allows_shared_period_vocabulary_when_distinct()
    {
        // Both officers share "police coat and hat" but different identity lines
        var seeds = Seeds(
            ("Character_Hayes",
                "Broad-built adult male police officer with a solid face and heavy frame; mid-19th-century police coat and hat.",
                "Constants: broad stocky adult male officer; heavier wider build; mid-19th-century police coat and hat."),
            ("Character_Clemm",
                "Younger slim police officer carrying a small notebook; mid-19th-century police coat and hat.",
                "Constants: youngest officer, slimmer build, notebook prop; mid-19th-century police coat and hat."));

        var hits = CharacterLookDistinctness.FindNearDuplicates(
            seeds,
            "Character_Reynolds",
            description:
                "Human male, senior middle age, calm composed face with a steady gaze, greying temples, upright controlled bearing; mid-19th-century police coat and hat, dark uniform cloth, practical boots.",
            visualLock:
                "Constants: senior-aged male officer, calm composed expression, steady gaze, greying temples, upright controlled posture; mid-19th-century police coat and hat. Average build, not broad or heavyset; not elderly.");

        Assert.Empty(hits);
    }

    [Fact]
    public void FormatWarning_names_other_cast_member()
    {
        var hits = new[]
        {
            new CharacterLookDistinctness.SimilarLookHit("Character_Officer_Hayes", "description", 0.95),
        };
        var msg = CharacterLookDistinctness.FormatWarning(hits, k => "Officer Hayes");
        Assert.NotNull(msg);
        Assert.Contains("Officer Hayes", msg);
        Assert.Contains("distinct", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Similarity_exact_normalized_is_one()
    {
        var a = CharacterLookDistinctness.Normalize("  Broad  Stocky  Officer! ");
        var b = CharacterLookDistinctness.Normalize("broad stocky officer");
        Assert.Equal(1.0, CharacterLookDistinctness.Similarity(a, b));
    }

    private static Dictionary<string, JsonElement> Seeds(params (string Key, string Desc, string Vis)[] rows)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, desc, vis) in rows)
        {
            using var doc = JsonDocument.Parse(
                JsonSerializer.Serialize(new { description = desc, visual_lock = vis }));
            dict[key] = doc.RootElement.Clone();
        }
        return dict;
    }
}
