using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class BeatPacingClassifierTests
{
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
    public async Task ClassifyScenePacingAsync_ParsesBeatDurationsCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "pacing": [
                { "beat_id": "b1", "duration_seconds": 10, "reason": "suspenseful pause" },
                { "beat_id": "b2", "duration_seconds": 4, "reason": "rapid spoken dialogue" },
                { "beat_id": "b3", "duration_seconds": 8, "reason": "dramatic reveal" }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyBeatPacingWithChat = true });
        var classifier = new BeatPacingClassifier(mockChat, opts, NullLogger<BeatPacingClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. BEDCHAMBER - NIGHT"
        };

        var beats = new List<Dictionary<string, object?>>
        {
            new() { ["beat_id"] = "b1", ["action_class"] = "hold", ["visual_event"] = "Silence in the room." },
            new() { ["beat_id"] = "b2", ["action_class"] = "dialogue", ["dialogue"] = "Who's there?" },
            new() { ["beat_id"] = "b3", ["action_class"] = "big_action", ["visual_event"] = "Lantern springs open." }
        };

        var pacing = await classifier.ClassifyScenePacingAsync(scene, beats);

        Assert.NotNull(pacing);
        Assert.Equal(10, pacing!["b1"]);
        Assert.Equal(4, pacing["b2"]);
        Assert.Equal(8, pacing["b3"]);
    }

    [Fact]
    public async Task ClassifyScenePacingAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyBeatPacingWithChat = false });
        var classifier = new BeatPacingClassifier(mockChat, opts, NullLogger<BeatPacingClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };
        var beats = new List<Dictionary<string, object?>> { new() { ["beat_id"] = "b1" } };

        var pacing = await classifier.ClassifyScenePacingAsync(scene, beats);

        Assert.Null(pacing);
    }
}
