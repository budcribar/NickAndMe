using System.Text.Json;
using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

public class ClipVideoPromptBuilderTests
{
    [Fact]
    public void Build_includes_character_variables_and_image_tags()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_Buster runs across the grass. Character_Momma watches.",
              "veo_continuation_source": "none",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "He's Buster the Noodle Head Dog.",
                "delivery": "voiceover_internal"
              }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-prompt-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_buster_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_momma_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Buster"] = new()
            {
                Key = "Character_Buster",
                DisplayName = "Buster",
                Description = "Small black-and-white dog",
                VisualLock = "Always black-and-white patches",
                VoiceProfile = "nonverbal",
            },
            ["Character_Momma"] = new()
            {
                Key = "Character_Momma",
                DisplayName = "Momma",
                Description = "Adult woman, warm",
                VisualLock = "Same mother figure",
                VoiceProfile = "warm mid pitch",
            },
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                VoiceOnly = true,
                VoiceProfile = "calm storyteller",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Contains("CHARACTER VARIABLES", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Buster", built.Prompt);
        Assert.Contains("Small black-and-white dog", built.Prompt);
        Assert.Contains("<IMAGE_1>", built.Prompt);
        Assert.Contains("VOICE LOCK", built.Prompt);
        Assert.Contains("He's Buster the Noodle Head Dog.", built.Prompt);
        Assert.True(built.ReferenceImagePaths.Count >= 1);
        Assert.Null(built.StartFrameImagePath);
        Assert.True(built.Prompt.Length < ClipVideoPromptBuilder.MaxPromptChars);
        Assert.True(built.Prompt.Length > 200);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_video_extend_mode_when_previous_clip_file_exists()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "Character_Buster skids and tumbles.",
              "veo_continuation_source": "extend_previous",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-cont-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var prevVideo = Path.Combine(tmp, "scene_01_clip_01.mp4");
        File.WriteAllBytes(prevVideo, new byte[2048]);

        var built = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            previousClipVisualPrompt: "Character_Buster rockets across the grass.",
            previousClipVideoPath: prevVideo,
            maxRefs: 3);

        Assert.Equal("video-extend", built.Mode);
        Assert.Contains("PREVIOUS CLIP", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rockets across the grass", built.Prompt);
        Assert.Contains("EXTENSION", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(built.ReferenceImagePaths);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Max_prompt_cap_is_far_above_legacy_4k()
    {
        Assert.True(ClipVideoPromptBuilder.MaxPromptChars >= 50_000);
    }

    [Fact]
    public void Build_includes_cast_count_for_on_screen_keys()
    {
        var json = """
            {
              "visual_prompt": "INT. ROOM - DAY. Character_Hero and Character_Villain face off.",
              "primary_subject": "Character_Hero",
              "audio_payload": { "speaker": "Character_Hero", "dialogue": "Stop.", "delivery": "spoken_on_camera" }
            }
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var built = ClipVideoPromptBuilder.Build(
            doc.RootElement,
            projectDir: Path.GetTempPath(),
            characters: new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_Hero"] = new() { Key = "Character_Hero", DisplayName = "Hero", Description = "tall hero" },
                ["Character_Villain"] = new() { Key = "Character_Villain", DisplayName = "Villain", Description = "scarred villain" },
            });
        Assert.Contains("CAST COUNT: exactly 2", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Hero", built.Prompt);
        Assert.Contains("Character_Villain", built.Prompt);
    }

    [Theory]
    [InlineData("Grok submit HTTP 400: prompt too long", true)]
    [InlineData("context_length_exceeded", true)]
    [InlineData("maximum context length exceeded", true)]
    [InlineData("HTTP 413 payload too large", true)]
    [InlineData("Grok job failed: bad face", false)]
    [InlineData("rate limit", false)]
    public void IsPromptTooLongError_detects_length_failures(string msg, bool expected)
    {
        Assert.Equal(expected, ClipVideoPromptBuilder.IsPromptTooLongError(msg));
    }

    [Fact]
    public void ShortenPromptForRetry_strips_gen_pack_then_caps()
    {
        var core = "CHARACTER VARIABLES\n- Character_Hero: pale man in wool coat\n\nTHIS CLIP:\nAction beats go here.\n";
        var pack = "\n# Film Studio gen pack (active addendum)\n\nApply these house rules when building clip video prompts:\n- rule one\n";
        var full = core + pack + "\nPROJECT HOUSE RULES (approved):\n- period drama\n";

        var s1 = ClipVideoPromptBuilder.ShortenPromptForRetry(full, 1);
        Assert.DoesNotContain("Film Studio gen pack", s1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PROJECT HOUSE RULES", s1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Hero", s1);
        Assert.True(s1.Length < full.Length);

        var huge = new string('x', 700_000) + "\n" + full;
        var s2 = ClipVideoPromptBuilder.ShortenPromptForRetry(huge, 2);
        Assert.True(s2.Length <= 600_000 + 80);
        Assert.Contains("shortened after API length limit", s2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeActionText_strips_embedded_cast_count()
    {
        var raw = "INT. ROOM. CAST COUNT: exactly 1 on-screen identity(ies) — Character_Narrator. No extra people. An OLD MAN sleeps. / 480p, 24fps";
        var clean = ClipVideoPromptBuilder.SanitizeActionText(raw, new[] { "Character_Narrator", "Character_Old_Man" });
        Assert.DoesNotContain("CAST COUNT", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Old_Man", clean);
        Assert.Contains("Character_Narrator", clean);
    }

    [Fact]
    public void InferKeysFromProse_promotes_old_man_and_officers()
    {
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new() { Key = "Character_Narrator", DisplayName = "Narrator" },
            ["Character_Old_Man"] = new() { Key = "Character_Old_Man", DisplayName = "Old Man" },
            ["Character_Officer"] = new() { Key = "Character_Officer", DisplayName = "Officer" },
            ["Character_Officer_Two"] = new() { Key = "Character_Officer_Two", DisplayName = "Officer Two" },
            ["Character_Officer_Three"] = new() { Key = "Character_Officer_Three", DisplayName = "Officer Three" },
        };
        var keys = ClipVideoPromptBuilder.InferKeysFromProse(
            "An OLD MAN sleeps. Three OFFICERS sit over the boards.", profiles);
        Assert.Contains("Character_Old_Man", keys);
        Assert.Contains("Character_Officer", keys);
        Assert.Contains("Character_Officer_Two", keys);
        Assert.Contains("Character_Officer_Three", keys);
    }

    [Fact]
    public void Build_uses_characters_on_screen_and_single_cast_count()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. CHAMBER. CAST COUNT: exactly 1 on-screen identity(ies) — Character_Narrator. No extra people. An OLD MAN sleeps behind a curtained bed. / 480p, 24fps",
              "characters_on_screen": ["Character_Narrator", "Character_Old_Man"],
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new() { Key = "Character_Narrator", DisplayName = "Narrator", Description = "pale man" },
            ["Character_Old_Man"] = new() { Key = "Character_Old_Man", DisplayName = "Old Man", Description = "elderly" },
        };
        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.Equal(2, built.CastCount);
        Assert.Equal(2, built.OnScreenKeys.Count);
        Assert.Contains("CAST COUNT: exactly 2", built.Prompt);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(built.Prompt, "CAST COUNT:").Count);
        Assert.DoesNotContain("CAST COUNT: exactly 1", built.Prompt);
        Assert.True(built.Prompt.IndexOf("CAST COUNT", StringComparison.OrdinalIgnoreCase) <
                    built.Prompt.IndexOf("THIS CLIP", StringComparison.OrdinalIgnoreCase));
    }

    // --- PR2: identity continuity (fresh / extend / cast-change reseed) ---

    [Fact]
    public void Build_fresh_attaches_refs_and_no_identity_reinforce()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_Old_Man sleeps in bed.",
              "characters_on_screen": ["Character_Old_Man"],
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-pr2-fresh-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "elderly pale man in nightshirt",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 3);
        Assert.Equal("fresh", built.Mode);
        Assert.True(built.RefsAttachedToApi);
        Assert.NotEmpty(built.ReferenceImagePaths);
        Assert.DoesNotContain("IDENTITY: Match locked plate", built.Prompt, StringComparison.Ordinal);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_video_extend_same_cast_no_api_refs_has_identity_reinforce()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "Character_Old_Man stirs under the covers.",
              "characters_on_screen": ["Character_Old_Man"],
              "veo_continuation_source": "extend_previous",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-pr2-extend-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);
        var prevVideo = Path.Combine(tmp, "scene_01_clip_01.mp4");
        File.WriteAllBytes(prevVideo, new byte[2048]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "elderly pale man in nightshirt",
            },
        };

        var built = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            characters: profiles,
            previousClipVisualPrompt: "Character_Old_Man sleeps.",
            previousClipVideoPath: prevVideo,
            maxRefs: 3);

        Assert.Equal("video-extend", built.Mode);
        Assert.False(built.RefsAttachedToApi);
        Assert.Empty(built.ReferenceImagePaths);
        Assert.Contains("IDENTITY: Match locked plate", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("Character_Old_Man", built.Prompt);
        Assert.Contains("EXTENSION", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_cast_change_reseed_is_fresh_with_refs_when_prev_video_cleared()
    {
        // FilmJobService nulls previousClipVideoPath on cast-set change (IdentityReseedOnCastChange).
        // Builder then attaches locked plates like clip 1.
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 3,
              "visual_prompt": "Three OFFICERS enter. Character_Officer speaks.",
              "characters_on_screen": ["Character_Officer", "Character_Officer_Two", "Character_Officer_Three"],
              "veo_continuation_source": "extend_previous",
              "audio_payload": { "speaker": "Character_Officer", "dialogue": "A noise?", "delivery": "spoken_on_camera" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-pr2-reseed-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_officer_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_officer_two_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_officer_three_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Officer"] = new() { Key = "Character_Officer", DisplayName = "Officer", Description = "uniformed officer" },
            ["Character_Officer_Two"] = new() { Key = "Character_Officer_Two", DisplayName = "Officer Two", Description = "second officer" },
            ["Character_Officer_Three"] = new() { Key = "Character_Officer_Three", DisplayName = "Officer Three", Description = "third officer" },
            ["Character_Old_Man"] = new() { Key = "Character_Old_Man", DisplayName = "Old Man", Description = "elderly" },
        };

        // prev video path NOT passed → same as FilmJobService reseed after cast change
        var built = ClipVideoPromptBuilder.Build(
            clip,
            tmp,
            characters: profiles,
            previousClipVisualPrompt: "Character_Old_Man sleeps. (prior cast for prose only)",
            previousClipVideoPath: null,
            maxRefs: 5);

        Assert.Equal("fresh", built.Mode);
        Assert.True(built.RefsAttachedToApi);
        Assert.True(built.ReferenceImagePaths.Count >= 1);
        Assert.Equal(3, built.CastCount);
        Assert.DoesNotContain("IDENTITY: Match locked plate", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("<IMAGE_1>", built.Prompt);
        Assert.Contains("new cast plate refs attached", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }
}