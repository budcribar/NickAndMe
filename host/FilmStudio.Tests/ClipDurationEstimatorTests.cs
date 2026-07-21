using System.Text.Json;
using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

public class ClipDurationEstimatorTests
{
    [Fact]
    public void Short_dialogue_is_tighter_than_8s_default()
    {
        var d = ClipDurationEstimator.Estimate(
            dialogue: "Merry Christmas, Uncle!",
            visualOrAction: "Fred grins.",
            actionClass: "dialogue");
        Assert.InRange(d, ClipDurationEstimator.MinSeconds, 6);
    }

    [Fact]
    public void Long_dialogue_gets_more_time_but_stays_capped()
    {
        var line =
            "If I could work my will, every idiot who goes about with Merry Christmas on his lips " +
            "should be boiled with his own pudding and buried with a stake of holly through his heart.";
        var d = ClipDurationEstimator.Estimate(line, "Scrooge scowls.", "dialogue");
        Assert.True(d >= 6, $"expected longer clip, got {d}");
        Assert.True(d <= ClipDurationEstimator.MaxSeconds);
    }

    [Fact]
    public void Action_only_is_not_padded_to_ten()
    {
        var d = ClipDurationEstimator.Estimate(
            dialogue: "",
            visualOrAction: "Buster runs across the grass.",
            actionClass: "action");
        Assert.InRange(d, ClipDurationEstimator.ActionOnlyMinSeconds, 8);
    }

    [Fact]
    public void Allocate_does_not_inflate_dialogue_clips_to_fill_scene_budget()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["dialogue"] = "Hi.",
                ["visual_event"] = "Wave.",
                ["action_class"] = "dialogue",
            },
            new()
            {
                ["dialogue"] = "Bye.",
                ["visual_event"] = "Exit.",
                ["action_class"] = "dialogue",
            },
        };
        var durs = ClipDurationEstimator.AllocateForBeats(beats, sceneTargetSeconds: 40);
        Assert.All(durs, d => Assert.True(d <= 6, $"dialogue clip padded to {d}"));
    }

    [Fact]
    public void EstimateForClip_reads_audio_payload()
    {
        var clip = JsonDocument.Parse("""
            {
              "duration_seconds": 10,
              "visual_prompt": "Momma points to the door.",
              "audio_payload": {
                "speaker": "Character_Momma",
                "dialogue": "A doggy goes outside.",
                "delivery": "on_camera"
              }
            }
            """).RootElement;
        var d = ClipDurationEstimator.EstimateForClip(clip);
        Assert.True(d < 10, "should not keep inflated plan for short dialogue");
        Assert.InRange(d, ClipDurationEstimator.MinSeconds, ClipDurationEstimator.MaxSeconds);
    }

    [Fact]
    public void Short_dialogue_is_not_split()
    {
        var line = "Well enough. Well enough.";
        var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(line);
        Assert.Single(parts);
        Assert.Equal(line, parts[0]);
    }

    [Fact]
    public void Long_monologue_splits_into_multiple_model_safe_chunks()
    {
        // Poe-scale first confession speech (~80+ words) must not stay one 10s clip
        var line =
            "True!—nervous—very, very dreadfully nervous I had been and am; but why will you say that I am mad? " +
            "The disease had sharpened my senses—not destroyed—not dulled them. Above all was the sense of hearing acute. " +
            "I heard all things in the heaven and in the earth. I heard many things in hell. How, then, am I mad? " +
            "Hearken! and observe how healthily—how calmly I can tell you the whole story.";

        Assert.True(ClipDurationEstimator.DialogueExceedsModelMax(line));
        var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(line);
        Assert.True(parts.Count >= 2, $"expected split, got {parts.Count} part(s)");
        Assert.Equal(
            ClipDurationEstimator.CountWords(line),
            parts.Sum(ClipDurationEstimator.CountWords));

        foreach (var p in parts)
        {
            var uncapped = ClipDurationEstimator.EstimateUncapped(p, "", "dialogue", "spoken_on_camera");
            var budget = ClipDurationEstimator.MaxSeconds - ClipDurationEstimator.DialogueModelPaddingSeconds;
            Assert.True(uncapped <= budget + 0.5,
                $"chunk still too long ({uncapped:F1}s > budget {budget:F1}s): {p[..Math.Min(60, p.Length)]}…");
            var planned = ClipDurationEstimator.Estimate(p, "speaks", "dialogue");
            Assert.InRange(planned, ClipDurationEstimator.MinSeconds, ClipDurationEstimator.MaxSeconds);
        }
    }

    [Fact]
    public void ExpandLongDialogueBeats_splits_and_renumbers()
    {
        var monologue =
            "It is impossible to say how first the idea entered my brain; but once conceived, it haunted me day and night. " +
            "Object there was none. Passion there was none. I loved the old man. He had never wronged me. " +
            "He had never given me insult. For his gold I had no desire. I think it was his eye! yes, it was this!";

        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = "b1",
                ["action_class"] = "action",
                ["dialogue"] = "",
                ["delivery"] = "none",
                ["visual_event"] = "Chair faces us.",
            },
            new()
            {
                ["beat_id"] = "b2",
                ["action_class"] = "dialogue",
                ["dialogue"] = monologue,
                ["delivery"] = "spoken_on_camera",
                ["speaker"] = "Character_Narrator",
                ["visual_event"] = "NARRATOR speaks.",
                ["audio"] = new Dictionary<string, object?>
                {
                    ["delivery"] = "spoken_on_camera",
                    ["speaker"] = "Character_Narrator",
                    ["dialogue"] = monologue,
                },
            },
        };

        var expanded = ClipDurationEstimator.ExpandLongDialogueBeats(beats);
        Assert.True(expanded.Count > 2, $"expected monologue expansion, count={expanded.Count}");
        Assert.Equal("b1", expanded[0]["beat_id"]?.ToString());
        Assert.Equal("", expanded[0]["dialogue"]?.ToString() ?? "");

        var speech = expanded.Skip(1).ToList();
        Assert.All(speech, b =>
        {
            Assert.Equal("Character_Narrator", b["speaker"]?.ToString());
            Assert.False(string.IsNullOrWhiteSpace(b["dialogue"]?.ToString()));
            Assert.False(ClipDurationEstimator.DialogueExceedsModelMax(b["dialogue"]?.ToString()));
        });
        // Sequential ids after expand
        for (var i = 0; i < expanded.Count; i++)
            Assert.Equal($"b{i + 1}", expanded[i]["beat_id"]?.ToString());
    }

    [Fact]
    public void Run_on_sentence_without_punctuation_still_packs_by_words()
    {
        var words = string.Join(" ", Enumerable.Repeat("madness", 60));
        var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(words);
        Assert.True(parts.Count >= 2);
        foreach (var p in parts)
            Assert.False(ClipDurationEstimator.DialogueExceedsModelMax(p));
    }
}

public class ClipSilenceTrimmerTests
{
    [Fact]
    public void ComputeCutPoint_trims_trailing_silence()
    {
        // Speech until 5.0s, then silence to 8.0s
        var log = """
            [silencedetect @ 0x] silence_start: 5.0
            """;
        var cut = ClipSilenceTrimmer.ComputeCutPoint(log, totalDuration: 8.0, keepTailSeconds: 0.35);
        Assert.NotNull(cut);
        Assert.InRange(cut!.Value, 5.2, 5.6);
    }

    [Fact]
    public void ComputeCutPoint_skips_when_no_trailing_silence()
    {
        var log = """
            [silencedetect @ 0x] silence_start: 1.0
            [silencedetect @ 0x] silence_end: 1.5
            """;
        // speech resumes and continues to end — no open trailing silence
        var cut = ClipSilenceTrimmer.ComputeCutPoint(log, totalDuration: 6.0, keepTailSeconds: 0.35);
        Assert.Null(cut);
    }
}
