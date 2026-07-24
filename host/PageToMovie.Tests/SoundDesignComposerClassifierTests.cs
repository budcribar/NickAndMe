using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class SoundDesignComposerClassifierTests
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
    public async Task ClassifySceneSoundDesignAsync_ParsesSoundDirectivesCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "sound_design": [
                {
                  "beat_id": "b1",
                  "ambient_layer": "Heavy rain against glass pane with room reverb 0.4",
                  "foley_layer": "Creaking wooden floorboards under slow footsteps",
                  "score_layer": "Low sub-bass drone rising to an 80 BPM heartbeat pulse"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifySoundDesignComposerWithChat = true });
        var classifier = new SoundDesignComposerClassifier(mockChat, opts, NullLogger<SoundDesignComposerClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. BARE ROOM - NIGHT"
        };

        var beats = new List<Dictionary<string, object?>>
        {
            new() { ["beat_id"] = "b1", ["visual_event"] = "Slow footsteps across the room" }
        };

        var sound = await classifier.ClassifySceneSoundDesignAsync(scene, beats);

        Assert.NotNull(sound);
        Assert.Equal("Heavy rain against glass pane with room reverb 0.4", sound!["b1"].AmbientLayer);
        Assert.Contains("Creaking wooden floorboards", sound["b1"].FoleyLayer);
        Assert.Contains("heartbeat pulse", sound["b1"].ScoreLayer);
    }

    [Fact]
    public async Task ClassifySceneSoundDesignAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifySoundDesignComposerWithChat = false });
        var classifier = new SoundDesignComposerClassifier(mockChat, opts, NullLogger<SoundDesignComposerClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };
        var beats = new List<Dictionary<string, object?>> { new() { ["beat_id"] = "b1" } };

        var sound = await classifier.ClassifySceneSoundDesignAsync(scene, beats);

        Assert.Null(sound);
    }
}
