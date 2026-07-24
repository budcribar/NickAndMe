using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// AI Classifier acting as a Period Visual Continuity Guard.
/// Generates context-aware, era-specific anachronism negative prompts
/// (e.g., "no modern wristwatches, no electric lamps, no plastic, no zippers").
/// </summary>
public sealed class NegativePromptClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<NegativePromptClassifier> _log;

    public NegativePromptClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<NegativePromptClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyNegativePromptWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film historian and Period Visual Continuity Guard preventing anachronisms in video generation.

        Your task: Given a scene's setting, period style, and location, generate a comma-separated list of 5–15 era-specific negative prompt tokens preventing period violations and visual glitches.

        RULES (HARD):
        1. Identify the Era:
           - If 19th Century / Gothic: "no modern wristwatches, no electric light bulbs, no plastic, no zippers, no modern cars, no printed logos, no denim, no asphalt, no modern hair gel"
           - If Medieval / Fantasy: "no modern clothes, no eyeglasses, no metal buttons, no paved roads, no power lines, no modern buildings"
           - If Sci-Fi / Future: "no primitive wooden furniture, no candles, no horses, no vintage cars"
        2. Keep tokens concise, negative, comma-separated.
        3. Do NOT include positive prompt descriptions.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "negative_tokens": "no modern wristwatches, no electric lamps, no plastic, no sneakers, no zippers, no printed text"
        }
        """;

    public async Task<string?> ClassifySceneNegativeAsync(
        Dictionary<string, object?> scene,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled) return null;

        onProgress?.Invoke($"AI Period Guard: Generating anachronism negatives for Scene {scene.GetValueOrDefault("scene_number")}…");

        try
        {
            var userPrompt = BuildUserPrompt(scene);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.NegativePromptClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                effectiveModel,
                // 0, not 0.2 — see BeatPacingClassifier for why (cacheable categorical labeling).
                temperature: 0,
                ct: ct,
                mode: ChatCallModes.NegativePromptClassify).ConfigureAwait(false);

            return ParseNegativeResponse(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI negative prompt classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        if (scene.TryGetValue("render_style_lock", out var rsl) && !string.IsNullOrWhiteSpace(rsl?.ToString()))
            sb.AppendLine($"RENDER STYLE / PERIOD LOCK: {rsl}");

        return sb.ToString();
    }

    private string? ParseNegativeResponse(string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("negative_tokens", out var nt))
            {
                var tokens = nt.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(tokens))
                    return tokens;
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI negative prompt response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
