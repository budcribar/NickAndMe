using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Fakes;

public sealed class FakeGrokVisionClient : IVisionClient
{
    private readonly ILogger<FakeGrokVisionClient> _log;

    public FakeGrokVisionClient(ILogger<FakeGrokVisionClient> log) => _log = log;

    public bool IsConfigured => true;

    public Task<string> TranscribePageAsync(
        string imagePath,
        int page,
        string model = "grok-4.5",
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake vision transcribe page={Page}", page);
        return Task.FromResult("(illustration only)");
    }

    public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast,
        string model = "grok-4.5",
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake vision classify page={Page} cast={N}", page, cast.Count);
        return Task.FromResult(new CharacterPageClassification
        {
            Page = page,
            PageKind = "illustration",
            Matches = new List<CharacterPageMatch>(),
        });
    }

    public Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "grok-4.5",
        string detail = "low",
        CancellationToken ct = default)
    {
        _log.LogInformation("Fake vision multi-image n={N}", imagePaths?.Count ?? 0);
        // Portrait style gate (CharacterDesignService) — always pass for fakes so UI tests can lock.
        if (!string.IsNullOrEmpty(prompt) &&
            prompt.Contains("PORTRAIT STYLE GATE", StringComparison.OrdinalIgnoreCase))
        {
            var medium = prompt.Contains("Expected medium for this project: illustration", StringComparison.OrdinalIgnoreCase)
                ? "illustration"
                : "photoreal";
            return Task.FromResult(
                $"{{\"pass\":true,\"medium\":\"{medium}\",\"reason\":\"Fake style gate pass.\"}}");
        }

        // Minimal valid auto-review JSON for UI/job testing without spend
        return Task.FromResult("""
            {
              "suggestion": "unclear",
              "category": "other",
              "confidence": "low",
              "continuity": "unclear",
              "note": "Fake review — connect API for real analysis.",
              "suggestions": []
            }
            """);
    }
}
