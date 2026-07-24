using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class NegativePromptClassifierTests
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
    public async Task ClassifySceneNegativeAsync_ParsesNegativeTokensCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "negative_tokens": "no modern wristwatches, no electric light bulbs, no plastic, no zippers, no printed text"
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyNegativePromptWithChat = true });
        var classifier = new NegativePromptClassifier(mockChat, opts, NullLogger<NegativePromptClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. BARE ROOM - NIGHT",
            ["render_style_lock"] = "19th century gothic live-action"
        };

        var tokens = await classifier.ClassifySceneNegativeAsync(scene);

        Assert.NotNull(tokens);
        Assert.Contains("no modern wristwatches", tokens);
        Assert.Contains("no electric light bulbs", tokens);
        Assert.Contains("no zippers", tokens);
    }

    [Fact]
    public async Task ClassifySceneNegativeAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyNegativePromptWithChat = false });
        var classifier = new NegativePromptClassifier(mockChat, opts, NullLogger<NegativePromptClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };

        var tokens = await classifier.ClassifySceneNegativeAsync(scene);

        Assert.Null(tokens);
    }
}
