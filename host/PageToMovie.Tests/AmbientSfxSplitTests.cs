using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class AmbientSfxSplitTests
{
    [Theory]
    [InlineData("Rain drums the roof. Soft wind.", "rain", true, "", false)]
    [InlineData("The door SLAMS shut.", "", false, "slam", true)]
    [InlineData("A clock ticks. Someone knocks.", "tick", true, "knock", true)]
    [InlineData("Empty hallway. No sound.", "", false, "", false)]
    public void InferAmbientAndSfx_splits_keywords(
        string text, string ambientHas, bool expectAmbient, string sfxHas, bool expectSfx)
    {
        var (ambient, sfx) = FountainStage1Importer.InferAmbientAndSfx(text);
        if (expectAmbient)
            Assert.Contains(ambientHas, ambient, StringComparison.OrdinalIgnoreCase);
        else
            Assert.True(string.IsNullOrWhiteSpace(ambient));

        if (expectSfx)
            Assert.Contains(sfxHas, sfx, StringComparison.OrdinalIgnoreCase);
        else
            Assert.True(string.IsNullOrWhiteSpace(sfx));
    }

    [Fact]
    public void Infer_door_slam_is_sfx_not_ambient()
    {
        var (ambient, sfx) = FountainStage1Importer.InferAmbientAndSfx("The heavy door slams.");
        Assert.True(string.IsNullOrWhiteSpace(ambient));
        Assert.Contains("slam", sfx, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Infer_rain_is_ambient()
    {
        var (ambient, sfx) = FountainStage1Importer.InferAmbientAndSfx("Rain. Wind against the shutters.");
        Assert.Contains("rain", ambient, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wind", ambient, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(sfx));
    }

    [Fact]
    public void NormalizeBeatAudioKeys_ignores_combined_ambient_or_sfx()
    {
        // V4 greenfield: combined field is stripped, not migrated
        var beat = new Dictionary<string, object?>
        {
            ["visual_event"] = "Quiet room.",
            ["ambient_or_sfx"] = "distant church bell",
        };
        Stage1Normalizer.NormalizeBeatAudioKeys(beat);
        Assert.False(beat.ContainsKey("ambient_or_sfx"));
        Assert.True(string.IsNullOrWhiteSpace(beat["sfx"]?.ToString()));
        Assert.True(string.IsNullOrWhiteSpace(beat["ambient"]?.ToString()));
        var audio = Assert.IsType<Dictionary<string, object?>>(beat["audio"]);
        Assert.False(audio.ContainsKey("ambient_or_sfx"));
    }

    [Fact]
    public void NormalizeBeatAudioKeys_preserves_separate_ambient_and_sfx()
    {
        var beat = new Dictionary<string, object?>
        {
            ["audio"] = new Dictionary<string, object?>
            {
                ["ambient"] = "soft rain",
                ["sfx"] = "knock",
                ["delivery"] = "none",
            },
        };
        Stage1Normalizer.NormalizeBeatAudioKeys(beat);
        Assert.Equal("soft rain", beat["ambient"]?.ToString());
        Assert.Equal("knock", beat["sfx"]?.ToString());
    }

    [Fact]
    public void NormalizeBeatAudioKeys_infers_from_visual_when_empty()
    {
        var beat = new Dictionary<string, object?>
        {
            ["visual_event"] = "Glass shatters. Rain continues outside.",
        };
        Stage1Normalizer.NormalizeBeatAudioKeys(beat);
        Assert.Contains("shatter", beat["sfx"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rain", beat["ambient"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrioritizeWardrobeItems_orders_signature_before_accessories()
    {
        var ordered = Stage2PlannerService.PrioritizeWardrobeItems(new[]
        {
            "brown leather satchel",
            "black wool nightshirt",
            "wire-rim glasses",
            "soft house slippers",
        });
        Assert.Equal(4, ordered.Count);
        // glasses + nightshirt rank 0, slippers rank 1, satchel rank 2
        Assert.True(
            ordered.ToList().FindIndex(s => s.Contains("glasses", StringComparison.OrdinalIgnoreCase)) <
            ordered.ToList().FindIndex(s => s.Contains("satchel", StringComparison.OrdinalIgnoreCase)));
        Assert.True(
            ordered.ToList().FindIndex(s => s.Contains("nightshirt", StringComparison.OrdinalIgnoreCase)) <
            ordered.ToList().FindIndex(s => s.Contains("satchel", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(ordered, s => s.Contains("satchel", StringComparison.OrdinalIgnoreCase));
    }
}
