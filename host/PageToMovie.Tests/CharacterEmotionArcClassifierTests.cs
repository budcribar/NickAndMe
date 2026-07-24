using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class CharacterEmotionArcClassifierTests
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
    public async Task ClassifySceneEmotionAsync_ParsesEmotionDirectivesCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "emotions": [
                {
                  "beat_id": "b1",
                  "intensity": 8,
                  "micro_expression": "Feverishly intense wide-eyed stare, tight unnatural smile",
                  "acting_prompt": "Acting intensity 8/10: Feverishly intense wide-eyed stare with tight unnatural smile"
                },
                {
                  "beat_id": "b2",
                  "intensity": 9,
                  "micro_expression": "Terror-stricken wide eyes, trembling lips",
                  "acting_prompt": "Acting intensity 9/10: Terror-stricken wide eyes and trembling lips"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyCharacterEmotionArcWithChat = true });
        var classifier = new CharacterEmotionArcClassifier(mockChat, opts, NullLogger<CharacterEmotionArcClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. BARE ROOM - NIGHT"
        };

        var beats = new List<Dictionary<string, object?>>
        {
            new() { ["beat_id"] = "b1", ["dialogue"] = "True!—nervous—very, very dreadfully nervous" },
            new() { ["beat_id"] = "b2", ["visual_event"] = "A sudden scream in the dark" }
        };

        var emotions = await classifier.ClassifySceneEmotionAsync(scene, beats);

        Assert.NotNull(emotions);
        Assert.Equal(8, emotions!["b1"].Intensity);
        Assert.Contains("wide-eyed stare", emotions["b1"].MicroExpression);
        Assert.Equal(9, emotions["b2"].Intensity);
        Assert.Contains("trembling lips", emotions["b2"].MicroExpression);
    }

    [Fact]
    public async Task ClassifySceneEmotionAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyCharacterEmotionArcWithChat = false });
        var classifier = new CharacterEmotionArcClassifier(mockChat, opts, NullLogger<CharacterEmotionArcClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };
        var beats = new List<Dictionary<string, object?>> { new() { ["beat_id"] = "b1" } };

        var emotions = await classifier.ClassifySceneEmotionAsync(scene, beats);

        Assert.Null(emotions);
    }
}
