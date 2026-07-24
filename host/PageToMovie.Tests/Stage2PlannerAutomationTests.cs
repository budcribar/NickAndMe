using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class Stage2PlannerAutomationTests
{
    [Fact]
    public void CoalesceSilentPreludeBeats_MergesSilentBeat1IntoBeat2()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = "b1",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE OLD MAN turns in a shaft of gray light.",
                ["dialogue"] = "",
                ["speaker"] = ""
            },
            new()
            {
                ["beat_id"] = "b2",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE NARRATOR's face goes cold.",
                ["dialogue"] = "He had the eye of a vulture.",
                ["speaker"] = "Character_The_Narrator"
            }
        };

        var coalesced = Stage2PlannerService.CoalesceSilentPreludeBeats(beats);

        Assert.Single(coalesced);
        Assert.Equal("b2", coalesced[0]["beat_id"]);
        Assert.Equal("He had the eye of a vulture.", coalesced[0]["dialogue"]);
        Assert.Contains("THE OLD MAN turns in a shaft of gray light.", coalesced[0]["visual_event"]?.ToString());
        Assert.Contains("THE NARRATOR's face goes cold.", coalesced[0]["visual_event"]?.ToString());
    }

    [Fact]
    public void CoalesceSilentPreludeBeats_LeavesSceneUnchangedIfBeat1HasDialogue()
    {
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = "b1",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE OLD MAN turns in a shaft of gray light.",
                ["dialogue"] = "Who's there?",
                ["speaker"] = "Character_The_Old_Man"
            },
            new()
            {
                ["beat_id"] = "b2",
                ["location_id"] = "Loc_Bedchamber",
                ["visual_event"] = "THE NARRATOR stands motionless.",
                ["dialogue"] = "I kept quite still.",
                ["speaker"] = "Character_The_Narrator"
            }
        };

        var coalesced = Stage2PlannerService.CoalesceSilentPreludeBeats(beats);

        Assert.Equal(2, coalesced.Count);
        Assert.Equal("b1", coalesced[0]["beat_id"]);
    }

    private sealed class MockChatClient : IChatClient
    {
        public bool IsConfigured => true;
        public string ResponseToReturn { get; set; } = "";

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null)
        {
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task ShotPlanRefiner_UpdatesVisualPromptsAndContinuationSources()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "refinements": [
                {
                  "clip_number": 1,
                  "visual_prompt": "INT. BEDCHAMBER - DAY. Wide establishing shot. Character_The_Narrator in doorway.",
                  "veo_continuation_source": "none"
                },
                {
                  "clip_number": 2,
                  "visual_prompt": "INT. BEDCHAMBER - DAY. ECU on Character_The_Old_Man pale blue eye.",
                  "veo_continuation_source": "none"
                },
                {
                  "clip_number": 3,
                  "visual_prompt": "INT. BEDCHAMBER - DAY. Medium shot on Character_The_Narrator shuddering.",
                  "veo_continuation_source": "extend_previous"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyShotPlanRefineWithChat = true });
        var refiner = new ShotPlanRefiningClassifier(mockChat, opts, NullLogger<ShotPlanRefiningClassifier>.Instance);

        var clips = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["clip_number"] = 1,
                ["duration_seconds"] = 5,
                ["visual_prompt"] = "INT. BEDCHAMBER - DAY. Same static prompt.",
                ["veo_continuation_source"] = "none"
            },
            new Dictionary<string, object?>
            {
                ["clip_number"] = 2,
                ["duration_seconds"] = 8,
                ["visual_prompt"] = "INT. BEDCHAMBER - DAY. Same static prompt.",
                ["veo_continuation_source"] = "extend_previous"
            },
            new Dictionary<string, object?>
            {
                ["clip_number"] = 3,
                ["duration_seconds"] = 6,
                ["visual_prompt"] = "INT. BEDCHAMBER - DAY. Same static prompt.",
                ["veo_continuation_source"] = "extend_previous"
            }
        };

        var plannedScene = new Dictionary<string, object?>
        {
            ["scene_number"] = 2,
            ["setting"] = "INT. BEDCHAMBER - DAY",
            ["characters_on_screen"] = new List<object?> { "Character_The_Narrator", "Character_The_Old_Man" },
            ["veo_clips"] = clips
        };

        var applied = await refiner.RefinePlannedSceneAsync(plannedScene);

        Assert.True(applied);
        var updatedClips = ((List<object?>)plannedScene["veo_clips"]!).OfType<Dictionary<string, object?>>().ToList();
        Assert.Contains("Wide establishing shot", updatedClips[0]["visual_prompt"]?.ToString());
        Assert.Contains("ECU on Character_The_Old_Man", updatedClips[1]["visual_prompt"]?.ToString());
        Assert.Equal("none", updatedClips[1]["veo_continuation_source"]);
        Assert.Equal("extend_previous", updatedClips[2]["veo_continuation_source"]);
    }

    [Fact]
    public void CoalesceSilentPreludeBeats_TellTaleHeartScene2_CoalescesClip1IntoFrame1VO()
    {
        var fountainPath = @"c:\Users\budcr\source\repos\PageToMovie\projects\TellTaleHeartV7\source\screenplay.fountain";
        if (!System.IO.File.Exists(fountainPath))
        {
            fountainPath = @"c:\Users\budcr\source\repos\gemini\PageToMovie\projects\TellTaleHeartV7\source\screenplay.fountain";
        }
        var text = System.IO.File.ReadAllText(fountainPath);
        var model = ScreenplayService.BuildModelFromFountainText(text);

        var scenes = Stage2PlannerService.GetScenes(model);
        var scene2 = scenes.FirstOrDefault(s => Stage2PlannerService.ToInt(s.GetValueOrDefault("scene_number")) == 2);
        Assert.NotNull(scene2);

        var beats = Stage2PlannerService.GetList(scene2!, "story_beats").OfType<Dictionary<string, object?>>().ToList();
        var coalesced = Stage2PlannerService.CoalesceSilentPreludeBeats(beats);

        // Before coalescing: Beat 1 was silent action, Beats 2-6 were VO dialogue (6 beats total).
        // After coalescing: Beat 1 is merged into Beat 2, yielding 5 beats total with VO dialogue on frame 1.
        Assert.Equal(5, coalesced.Count);
        Assert.Contains("He had the eye of a vulture", coalesced[0]["dialogue"]?.ToString());
        Assert.Contains("THE OLD MAN turns in a shaft of gray light", coalesced[0]["visual_event"]?.ToString());
    }
}
