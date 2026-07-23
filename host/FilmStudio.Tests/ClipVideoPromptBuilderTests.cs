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
              "characters_on_screen": ["Character_Buster", "Character_Momma"],
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
    public void Build_silent_clip_tells_model_not_to_show_speaking()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_OldMan sleeps. Character_Narrator watches from the shadows.",
              "characters_on_screen": ["Character_OldMan", "Character_Narrator"],
              "veo_continuation_source": "none",
              "audio_payload": { "speaker": "", "dialogue": "", "delivery": "none" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-silent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_OldMan"] = new()
            {
                Key = "Character_OldMan",
                DisplayName = "Old Man",
                Description = "frail elderly man",
                VoiceProfile = "raspy whisper",
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

        Assert.Contains("Silent beat", built.Prompt);
        Assert.Contains("do not show any on-screen character", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the spoken line", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Build_clip_with_dialogue_still_uses_spoken_line_closing()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "Character_OldMan speaks.",
              "characters_on_screen": ["Character_OldMan"],
              "veo_continuation_source": "none",
              "audio_payload": {
                "speaker": "Character_OldMan",
                "dialogue": "Come closer.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-clip-dialogue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_OldMan"] = new()
            {
                Key = "Character_OldMan",
                DisplayName = "Old Man",
                Description = "frail elderly man",
                VoiceProfile = "raspy whisper",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);

        Assert.Contains("the spoken line and primary action finish", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silent beat", built.Prompt);

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
    public void Video_prompt_hard_cap_matches_xai_limit()
    {
        Assert.Equal(4000, ClipVideoPromptBuilder.VideoPromptHardCapChars);
        Assert.Equal(
            ClipVideoPromptBuilder.VideoPromptHardCapChars,
            ClipVideoPromptBuilder.MaxPromptChars);
    }

    [Fact]
    public void Build_includes_cast_count_for_on_screen_keys()
    {
        var json = """
            {
              "visual_prompt": "INT. ROOM - DAY. Character_Hero and Character_Villain face off.",
              "characters_on_screen": ["Character_Hero", "Character_Villain"],
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

        var huge = new string('x', 12_000) + "\n" + full;
        var s2 = ClipVideoPromptBuilder.ShortenPromptForRetry(huge, 2);
        Assert.True(s2.Length <= ClipVideoPromptBuilder.VideoPromptHardCapChars + 80);
        Assert.Contains("shortened after API length limit", s2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FitPromptToVideoBudget_strips_gen_pack_before_first_send()
    {
        var core = "CHARACTER VARIABLES\n- Character_Hero: pale man\n\nTHIS CLIP:\nHe walks.\n";
        var pack = "\n# Film Studio gen pack (active addendum)\n\n" + new string('z', 4500);
        var full = core + pack;
        Assert.True(full.Length > ClipVideoPromptBuilder.VideoPromptHardCapChars);

        var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(full);
        Assert.True(fitted.Length <= ClipVideoPromptBuilder.VideoPromptHardCapChars);
        Assert.DoesNotContain("Film Studio gen pack", fitted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_Hero", fitted);
    }

    [Fact]
    public void Build_does_not_attach_cast_only_named_in_dialogue()
    {
        // Blueprint: Narrator only. Dialogue names "the old man" — must not promote Old Man on screen.
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 4,
              "visual_prompt": "INT. CONFESSION ROOM. Character_Narrator ON CAMERA lip-syncs \"I loved the old man. I think it was his eye!\".",
              "characters_on_screen": ["Character_Narrator"],
              "primary_subject": "Character_Narrator",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "I loved the old man. I think it was his eye!",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-cast-dlg-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_narrator_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                Description = "lean pale man",
            },
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "elderly white-haired man",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Equal(1, built.CastCount);
        Assert.DoesNotContain("Character_Old_Man", built.OnScreenKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CAST COUNT: exactly 1", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character_Old_Man", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Single(built.ReferenceImagePaths);
        Assert.Contains("narrator", Path.GetFileName(built.ReferenceImagePaths[0]), StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
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

    [Theory]
    [InlineData(
        "NARRATOR (CONT'D). Character_Narrator ON CAMERA lip-syncs \"Hello.\"",
        "NARRATOR. Character_Narrator ON CAMERA lip-syncs \"Hello.\"")]
    [InlineData(
        "Character_Narrator He steadies his hands on his knees.",
        "Character_Narrator steadies his hands on his knees.")]
    [InlineData(
        "Character_Hero Character_Hero walks in.",
        "Character_Hero walks in.")]
    public void StripFountainLeakage_removes_contd_and_token_pronoun_glue(string raw, string expected)
    {
        var clean = ClipVideoPromptBuilder.StripFountainLeakage(raw);
        Assert.Equal(expected, clean);
        Assert.DoesNotContain("CONT", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character_Narrator He", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "True!-nervous-very, very dreadfully nervous I had been and am;",
        "True! Nervous — very, very dreadfully nervous I had been and am;")]
    [InlineData(
        "True!—nervous—very, very dreadfully nervous I had been and am;",
        "True! Nervous — very, very dreadfully nervous I had been and am;")]
    [InlineData(
        "healthily-how calmly I can tell you",
        "healthily — how calmly I can tell you")]
    [InlineData("Wait -- please!", "Wait — please!")]
    [InlineData("Oh God -- what have I done?", "Oh God — what have I done?")]
    [InlineData("", "")]
    [InlineData("  Hello world.  ", "Hello world.")]
    public void SanitizeSpokenDialogue_speech_safe_pauses(string raw, string expected)
    {
        Assert.Equal(expected, ClipVideoPromptBuilder.SanitizeSpokenDialogue(raw));
    }

    /// <summary>Real compounds must stay hyphenated (not become speech pauses).</summary>
    [Theory]
    [InlineData("Why is a raven like a writing-desk?", "writing-desk")]
    [InlineData("Good-bye, feet!", "Good-bye")]
    [InlineData("Come dine with us to-morrow.", "to-morrow")]
    [InlineData("What's to-day?", "to-day")]
    [InlineData("I am here to-night to warn you.", "to-night")]
    [InlineData("It's always tea-time.", "tea-time")]
    [InlineData("The stupidest tea-party I ever was at.", "tea-party")]
    [InlineData("Dead as a door-nail.", "door-nail")]
    [InlineData("A well-known fact.", "well-known")]
    [InlineData("An age-old idea.", "age-old")]
    [InlineData("Half-past one.", "Half-past")]
    [InlineData("I cut some more bread-and-butter.", "bread-and-butter")]
    [InlineData("Ah! Bed-curtains!", "Bed-curtains")]
    public void SanitizeSpokenDialogue_preserves_hyphenated_compounds(string raw, string mustContain)
    {
        var cleaned = ClipVideoPromptBuilder.SanitizeSpokenDialogue(raw);
        Assert.Contains(mustContain, cleaned, StringComparison.OrdinalIgnoreCase);
        // Must not have turned that compound into an em-dash pause
        var broken = mustContain.Replace("-", " — ", StringComparison.Ordinal);
        Assert.DoesNotContain(broken, cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("True! Nervous — very, very dreadfully nervous I had been and am;", "True!")]
    [InlineData("Hello world.", "Hello")]
    [InlineData("", "")]
    public void FirstSpokenToken_extracts_opening(string line, string expected)
    {
        Assert.Equal(expected, ClipVideoPromptBuilder.FirstSpokenToken(line));
    }

    [Fact]
    public void Build_audio_requires_opening_word()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "INT. ROOM. Character_Narrator ON CAMERA lip-syncs \"True! Nervous very.\"",
              "characters_on_screen": ["Character_Narrator"],
              "veo_continuation_source": "none",
              "audio_payload": {
                "speaker": "Character_Narrator",
                "dialogue": "True! Nervous very.",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                Description = "pale man",
            },
        };
        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.Contains("Start speaking immediately with \"True!\"", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("exactly: \"True! Nervous very.\"", built.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_audio_and_visual_use_sanitized_spoken_dialogue()
    {
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "INT. BARE ROOM - NIGHT. The Narrator speaks. Character_The_Narrator ON CAMERA lip-syncs \"True!-nervous-very, very dreadfully nervous I had been and am;\"",
              "characters_on_screen": ["Character_The_Narrator"],
              "veo_continuation_source": "extend_previous",
              "audio_payload": {
                "speaker": "Character_The_Narrator",
                "dialogue": "True!-nervous-very, very dreadfully nervous I had been and am;",
                "delivery": "spoken_on_camera"
              }
            }
            """).RootElement;
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_The_Narrator"] = new()
            {
                Key = "Character_The_Narrator",
                DisplayName = "Narrator",
                Description = "pale man",
                VoiceProfile = "tense confessor",
            },
        };
        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath(), profiles);
        Assert.DoesNotContain("True!-nervous", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("True! Nervous — very", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("lip-syncs", built.Prompt, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Build_compacts_non_focus_characters_from_primary_and_speaker()
    {
        // No motion verbs required — Old Man is non-focus via metadata only
        var clip = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. BEDCHAMBER. Character_Narrator at the door. Character_Old_Man in the bed.",
              "characters_on_screen": ["Character_Narrator", "Character_Old_Man"],
              "primary_subject": "Character_Narrator",
              "audio_payload": { "speaker": "Character_Narrator", "dialogue": "I opened it gently.", "delivery": "spoken_on_camera" }
            }
            """).RootElement;

        var tmp = Path.Combine(Path.GetTempPath(), "fs-multi-compact-" + Guid.NewGuid().ToString("N"));
        var charDir = Path.Combine(tmp, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_narrator_ref.png"), new byte[512]);
        File.WriteAllBytes(Path.Combine(charDir, "character_old_man_ref.png"), new byte[512]);

        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new()
            {
                Key = "Character_Narrator",
                DisplayName = "Narrator",
                Description = "Lean pale man in waistcoat",
                VisualLock = "Same lean pale face",
            },
            ["Character_Old_Man"] = new()
            {
                Key = "Character_Old_Man",
                DisplayName = "Old Man",
                Description = "Frail elderly man with sparse white hair and blue eye",
                VisualLock = "Always elderly, white-haired",
            },
        };

        var built = ClipVideoPromptBuilder.Build(clip, tmp, profiles, maxRefs: 5);
        Assert.Contains("Character_Narrator", built.Prompt);
        Assert.Contains("Also present (not shot focus)", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Passive background", built.Prompt, StringComparison.OrdinalIgnoreCase);
        // Narrator should keep full visual lock prose
        Assert.Contains("Visual lock:", built.Prompt, StringComparison.OrdinalIgnoreCase);

        try { Directory.Delete(tmp, true); } catch { /* ignore */ }
    }

    [Fact]
    public void ResolveFocusKeys_big_action_keeps_all_on_screen()
    {
        var keys = ClipVideoPromptBuilder.ResolveFocusKeys(
            new[] { "Character_A", "Character_B" },
            primarySubject: "Character_A",
            speaker: null,
            actionClass: "big_action");
        Assert.Equal(2, keys.Count);
        Assert.Contains("Character_A", keys);
        Assert.Contains("Character_B", keys);
    }

    [Fact]
    public void ResolveFocusKeysForClip_prefers_explicit_focus_keys()
    {
        var clip = JsonDocument.Parse("""
            {
              "characters_on_screen": ["Character_A", "Character_B", "Character_C"],
              "primary_subject": "Character_A",
              "focus_keys": ["Character_B", "Character_C"],
              "audio_payload": { "speaker": "Character_A", "dialogue": "Hello.", "delivery": "spoken_on_camera" }
            }
            """).RootElement;
        var keys = ClipVideoPromptBuilder.ResolveFocusKeysForClip(
            new[] { "Character_A", "Character_B", "Character_C" }, clip);
        Assert.Equal(2, keys.Count);
        Assert.Contains("Character_B", keys);
        Assert.Contains("Character_C", keys);
        Assert.DoesNotContain("Character_A", keys);
    }

    [Fact]
    public void Stage2_CoalesceShortMonologueBeats_merges_consecutive_short_monologues()
    {
        var b1 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b1",
            ["speaker"] = "Character_Narrator",
            ["dialogue"] = "True! Nervous I had been.",
            ["delivery"] = "spoken_on_camera",
            ["visual_event"] = "Narrator sits at table.",
        };
        var b2 = new Dictionary<string, object?>
        {
            ["beat_id"] = "b2",
            ["speaker"] = "Character_Narrator",
            ["dialogue"] = "But why will you say I am mad?",
            ["delivery"] = "spoken_on_camera",
            ["visual_event"] = "Narrator leans in.",
        };
        var beats = new List<Dictionary<string, object?>> { b1, b2 };

        var coalesced = Stage2PlannerService.CoalesceShortMonologueBeats(beats);
        Assert.Single(coalesced);
        Assert.Contains("True! Nervous I had been. But why will you say I am mad?", coalesced[0]["dialogue"]?.ToString());
    }

    [Theory]
    [InlineData(0, 1, "Medium shot")]
    [InlineData(1, 1, "Extreme close-up on eyes")]
    [InlineData(2, 1, "Three-quarter profile")]
    [InlineData(3, 1, "Close-up on hands")]
    [InlineData(2, 2, "Over-the-shoulder shot")]
    public void Stage2_GetMonologueCameraFraming_cycles_and_gates_ots(
        int step, int onScreen, string expectedFraming)
    {
        var framing = Stage2PlannerService.GetMonologueCameraFraming(step, "Narrator", onScreen);
        Assert.Contains(expectedFraming, framing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Narrator", framing);
        if (onScreen < 2)
            Assert.DoesNotContain("Over-the-shoulder", framing, StringComparison.OrdinalIgnoreCase);
    }
}