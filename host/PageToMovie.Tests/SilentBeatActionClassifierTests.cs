using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class SilentBeatActionClassifierTests
{
    [Fact]
    public void ParseLabels_reads_standard_payload()
    {
        var map = SilentBeatActionClassifier.ParseLabels("""
            {"labels":[
              {"id":"s1_b1","class":"establishing","reason":"room"},
              {"id":"s1_b2","class":"hold"}
            ]}
            """);
        Assert.Equal("establishing", map["s1_b1"]);
        Assert.Equal("hold", map["s1_b2"]);
    }

    [Fact]
    public void ParseLabels_rejects_unknown_class()
    {
        var map = SilentBeatActionClassifier.ParseLabels(
            """{"labels":[{"id":"s1_b1","class":"montage"}]}""");
        Assert.Empty(map);
    }

    [Fact]
    public void NormalizeClass_accepts_four_labels_only()
    {
        Assert.Equal("big_action", SilentBeatActionClassifier.NormalizeClass("big_action"));
        Assert.Equal("big_action", SilentBeatActionClassifier.NormalizeClass("big action"));
        Assert.Null(SilentBeatActionClassifier.NormalizeClass("dialogue"));
    }

    [Theory]
    [InlineData(
        "hold",
        "She stares at the coins. Nothing left to do. She flops down on the couch and howls.",
        "action")]
    [InlineData(
        "hold",
        "He freezes; a thin smile.",
        "hold")]
    [InlineData(
        "big_action",
        "She ransacks store after store—counters of watches, trays of trinkets—searching.",
        "action")]
    [InlineData(
        "big_action",
        "They chase through the alley and crash into the stalls.",
        "big_action")]
    public void PostProcessActionClass_multi_step_and_busy_rules(
        string ai, string visual, string expected)
    {
        Assert.Equal(expected, SilentBeatActionClassifier.PostProcessActionClass(ai, visual));
    }

    [Fact]
    public async Task Classify_post_processes_hold_into_action_for_multi_step()
    {
        var chat = new ScriptedChat(_ => """
            {"labels":[
              {"id":"s1_b1","class":"hold","reason":"emotion"},
              {"id":"s1_b2","class":"hold","reason":"smile"}
            ]}
            """);
        var clf = NewClassifier(chat, enabled: true);
        // Override visual for b1 to multi-step business
        var stage1 = MiniStage1();
        var beats = Beats(stage1);
        beats[0]["visual_event"] =
            "She stares at the coins, then flops on the couch and sobs.";
        beats[1]["visual_event"] = "He freezes; a thin smile.";
        var result = await clf.ClassifyStage1Async(stage1);
        Assert.Equal(2, result.AiCount);
        Assert.Equal("action", Beats(stage1)[0]["action_class"]?.ToString());
        Assert.Equal("hold", Beats(stage1)[1]["action_class"]?.ToString());
        Assert.Equal("v2_pp", result.PromptVersion);
    }

    [Fact]
    public async Task Classify_uses_AI_when_chat_returns_labels()
    {
        var chat = new ScriptedChat(_ => """
            {"labels":[
              {"id":"s1_b1","class":"action","reason":"business not place open"},
              {"id":"s1_b2","class":"hold","reason":"smile"}
            ]}
            """);
        var clf = NewClassifier(chat, enabled: true);
        var stage1 = MiniStage1();
        var result = await clf.ClassifyStage1Async(stage1);
        Assert.Equal(2, result.AiCount);
        Assert.Equal(0, result.FallbackCount);
        var beats = Beats(stage1);
        Assert.Equal("action", beats[0]["action_class"]?.ToString());
        Assert.Equal("hold", beats[1]["action_class"]?.ToString());
        Assert.Equal(SilentBeatActionClassifier.PromptVersion, result.PromptVersion);
    }

    [Fact]
    public async Task Classify_retries_then_falls_back_on_persistent_failure()
    {
        var calls = 0;
        var chat = new ScriptedChat(_ =>
        {
            calls++;
            throw new InvalidOperationException("simulated outage");
        });
        var clf = NewClassifier(chat, enabled: true, maxAttempts: 3);
        var stage1 = MiniStage1();
        var result = await clf.ClassifyStage1Async(stage1);
        Assert.Equal(3, calls);
        Assert.Equal(0, result.AiCount);
        Assert.Equal(2, result.FallbackCount);
        // First silent → baseline establishing
        Assert.Equal("establishing", Beats(stage1)[0]["action_class"]?.ToString());
    }

    [Fact]
    public async Task Classify_retries_once_then_succeeds()
    {
        var calls = 0;
        var chat = new ScriptedChat(_ =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("transient");
            return """{"labels":[{"id":"s1_b1","class":"hold"},{"id":"s1_b2","class":"action"}]}""";
        });
        var clf = NewClassifier(chat, enabled: true, maxAttempts: 3);
        var stage1 = MiniStage1();
        var result = await clf.ClassifyStage1Async(stage1);
        Assert.True(calls >= 2);
        Assert.Equal(2, result.AiCount);
        Assert.Equal(0, result.FallbackCount);
        Assert.Equal("hold", Beats(stage1)[0]["action_class"]?.ToString());
    }

    [Fact]
    public async Task Classify_disabled_uses_heuristic_only_without_chat()
    {
        var calls = 0;
        var chat = new ScriptedChat(_ =>
        {
            calls++;
            return "{}";
        });
        var clf = NewClassifier(chat, enabled: false);
        var stage1 = MiniStage1();
        var result = await clf.ClassifyStage1Async(stage1);
        Assert.Equal(0, calls);
        Assert.Equal(2, result.FallbackCount);
        Assert.False(result.Enabled);
    }

    [Fact]
    public async Task Classify_partial_labels_fill_rest_with_heuristic()
    {
        var chat = new ScriptedChat(_ =>
            // only first id; retries may get same — remaining stays heuristic
            """{"labels":[{"id":"s1_b1","class":"action"}]}""");
        var clf = NewClassifier(chat, enabled: true, maxAttempts: 2);
        var stage1 = MiniStage1();
        var result = await clf.ClassifyStage1Async(stage1);
        Assert.Equal("action", Beats(stage1)[0]["action_class"]?.ToString());
        Assert.True(result.FallbackCount >= 1);
        Assert.Equal(1, result.AiCount);
    }

    private static SilentBeatActionClassifier NewClassifier(
        IChatClient chat,
        bool enabled,
        int maxAttempts = 3)
    {
        var opts = Options.Create(new PageToMovieOptions
        {
            ClassifySilentBeatsWithChat = enabled,
            SilentBeatClassifyModel = "test-model",
            SilentBeatClassifyTemperature = 0,
            SilentBeatClassifyMaxAttempts = maxAttempts,
            SilentBeatClassifyBackoffBaseMs = 0,
        });
        return new SilentBeatActionClassifier(
            chat,
            opts,
            NullLogger<SilentBeatActionClassifier>.Instance);
    }

    private static Dictionary<string, object?> MiniStage1() => new()
    {
        ["scenes"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["scene_number"] = 1,
                ["setting"] = "INT. ROOM - DAY",
                ["story_beats"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["beat_id"] = "b1",
                        ["visual_event"] =
                            "She finishes her cry and powders her cheeks by the window.",
                        ["dialogue"] = "",
                        ["action_class"] = "establishing",
                    },
                    new Dictionary<string, object?>
                    {
                        ["beat_id"] = "b2",
                        ["visual_event"] = "He steadies his hands. A thin smile.",
                        ["dialogue"] = "",
                        ["action_class"] = "hold",
                    },
                },
            },
        },
    };

    private static List<Dictionary<string, object?>> Beats(Dictionary<string, object?> stage1)
    {
        var scenes = (List<object?>)stage1["scenes"]!;
        var scene = (Dictionary<string, object?>)scenes[0]!;
        return ((List<object?>)scene["story_beats"]!)
            .Cast<Dictionary<string, object?>>()
            .ToList();
    }

    private sealed class ScriptedChat : IChatClient
    {
        private readonly Func<string, string> _reply;
        public ScriptedChat(Func<string, string> reply) => _reply = reply;
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null) =>
            Task.FromResult(_reply(userPrompt));
    }
}
