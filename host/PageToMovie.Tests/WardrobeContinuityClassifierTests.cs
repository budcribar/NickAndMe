using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class WardrobeContinuityClassifierTests
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
    public async Task ClassifySceneWardrobeAsync_ParsesAttireCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "wardrobe": [
                {
                  "character_key": "Character_The_Narrator",
                  "attire": "plain dark waistcoat, white shirtsleeves, rolled cuffs"
                },
                {
                  "character_key": "Character_The_Old_Man",
                  "attire": "loose white cotton nightshirt"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyWardrobeContinuityWithChat = true });
        var classifier = new WardrobeContinuityClassifier(mockChat, opts, NullLogger<WardrobeContinuityClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 2,
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - DAY"
        };

        var cast = new List<string> { "Character_The_Narrator", "Character_The_Old_Man" };

        var wardrobe = await classifier.ClassifySceneWardrobeAsync(scene, cast);

        Assert.NotNull(wardrobe);
        Assert.Equal("plain dark waistcoat, white shirtsleeves, rolled cuffs", wardrobe!["Character_The_Narrator"]);
        Assert.Equal("loose white cotton nightshirt", wardrobe["Character_The_Old_Man"]);
    }

    [Fact]
    public async Task ClassifySceneWardrobeAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyWardrobeContinuityWithChat = false });
        var classifier = new WardrobeContinuityClassifier(mockChat, opts, NullLogger<WardrobeContinuityClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };
        var cast = new List<string> { "Character_The_Narrator" };

        var wardrobe = await classifier.ClassifySceneWardrobeAsync(scene, cast);

        Assert.Null(wardrobe);
    }
}
