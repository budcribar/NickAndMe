using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Fakes;

/// <summary>Returns minimal valid-looking JSON for Stage 1 style prompts.</summary>
public sealed class FakeGrokChatClient : IGrokChatClient
{
    private readonly ILogger<FakeGrokChatClient> _log;

    public FakeGrokChatClient(ILogger<FakeGrokChatClient> log) => _log = log;

    public bool IsConfigured => true;

    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake chat complete model={Model} userLen={Len}", model, userPrompt.Length);

        // Book → Fountain conversion (prompt-driven path)
        if (systemPrompt.Contains("Fountain", StringComparison.OrdinalIgnoreCase) ||
            userPrompt.Contains("--- PAGE", StringComparison.OrdinalIgnoreCase) ||
            userPrompt.Contains("BEGIN BOOK", StringComparison.OrdinalIgnoreCase))
        {
            const string fountain = """
                Title: Fake Book Adaptation
                Credit: Written by
                Author: Test
                Source: Adapted from book
                Draft date: 1/1/2026

                INT. LIVING ROOM - EVENING

                = page 1

                [[page 1]]

                NARRATOR
                Once upon a time, a small dog waited for bedtime.

                MOMMA
                Time for bed!

                INT. BEDROOM - NIGHT

                = page 2

                [[page 2]]

                NARRATOR
                He curled up and slept.
                """;
            return Task.FromResult(fountain);
        }

        // Minimal Stage1-shaped stub
        const string json = """
            {
              "global_production_variables": {
                "character_seed_tokens": {},
                "location_seed_tokens": {}
              },
              "scenes": []
            }
            """;
        return Task.FromResult(json);
    }
}
