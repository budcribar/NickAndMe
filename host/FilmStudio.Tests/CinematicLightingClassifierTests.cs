using System.Text.Json;
using FilmStudio.Core.Options;
using FilmStudio.Engine;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

public sealed class CinematicLightingClassifierTests
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
    public async Task ClassifySceneLightingAsync_ParsesLightingTokenCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "lighting_token": "Chiaroscuro flickering candlelight with deep obsidian shadows and desaturated cool-gray volumetric fog"
            }
            """
        };

        var opts = Options.Create(new FilmStudioOptions { ClassifyCinematicLightingWithChat = true });
        var classifier = new CinematicLightingClassifier(mockChat, opts, NullLogger<CinematicLightingClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 2,
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - DAY",
            ["render_style_lock"] = "Period gothic live-action"
        };

        var token = await classifier.ClassifySceneLightingAsync(scene);

        Assert.NotNull(token);
        Assert.Contains("Chiaroscuro flickering candlelight", token);
        Assert.Contains("obsidian shadows", token);
    }

    [Fact]
    public async Task ClassifySceneLightingAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new FilmStudioOptions { ClassifyCinematicLightingWithChat = false });
        var classifier = new CinematicLightingClassifier(mockChat, opts, NullLogger<CinematicLightingClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };

        var token = await classifier.ClassifySceneLightingAsync(scene);

        Assert.Null(token);
    }
}
