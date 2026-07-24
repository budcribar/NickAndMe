using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// AI Classifier acting as a Costume & Wardrobe Department Supervisor.
/// Dynamically tracks and determines character attire per scene based on setting,
/// time of day, location, and narrative beats, replacing static string-list lookups.
/// </summary>
public sealed class WardrobeContinuityClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<WardrobeContinuityClassifier> _log;

    public WardrobeContinuityClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<WardrobeContinuityClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyWardrobeContinuityWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film Costume Department Supervisor managing wardrobe continuity across scenes.

        Your task: Given a scene's setting (location, time of day) and character list, determine the exact, contextually appropriate attire for each character appearing in the scene.

        RULES (HARD):
        1. Contextual Attire:
           - Night / Bedchamber scenes: Characters sleeping or in bed wear nightwear (e.g. "loose white cotton nightshirt, barefoot", "flannel nightgown").
           - Parlor / Daytime / Professional scenes: Characters wear day clothes (e.g. "dark woolen waistcoat, plain dark shirtsleeves, tailored trousers").
           - Outdoor / Travel: Characters wear outerwear (e.g. "heavy wool trench coat, leather boots, gloves").
        2. Keep descriptions concise (5–15 words per character).
        3. Do NOT omit any character keys provided in the prompt.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "wardrobe": [
            {
              "character_key": "Character_The_Narrator",
              "attire": "plain dark waistcoat, white shirtsleeves, rolled cuffs"
            },
            ...
          ]
        }
        """;

    public async Task<Dictionary<string, string>?> ClassifySceneWardrobeAsync(
        Dictionary<string, object?> scene,
        List<string> cast,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled || cast.Count == 0) return null;

        onProgress?.Invoke($"AI Costume Supervisor: Determining attire for {cast.Count} character(s) in Scene {scene.GetValueOrDefault("scene_number")}…");

        try
        {
            var userPrompt = BuildUserPrompt(scene, cast);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.WardrobeContinuityClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                effectiveModel,
                // 0, not 0.2 — see BeatPacingClassifier for why (cacheable categorical labeling).
                temperature: 0,
                ct: ct,
                mode: ChatCallModes.WardrobeContinuityClassify).ConfigureAwait(false);

            return ParseWardrobeResponse(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI wardrobe continuity classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene, List<string> cast)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine($"CHARACTERS ON SCREEN: {string.Join(", ", cast)}");

        if (scene.TryGetValue("story_beats", out var beatsObj) && beatsObj is List<object?> rawBeats)
        {
            sb.AppendLine("SAMPLE BEATS:");
            var beats = rawBeats.OfType<Dictionary<string, object?>>().Take(3);
            foreach (var b in beats)
            {
                var ve = b.GetValueOrDefault("visual_event");
                var dlg = b.GetValueOrDefault("dialogue");
                if (!string.IsNullOrWhiteSpace(ve?.ToString()))
                    sb.AppendLine($"  - {ve}");
                else if (!string.IsNullOrWhiteSpace(dlg?.ToString()))
                    sb.AppendLine($"  - Spoken: \"{dlg}\"");
            }
        }

        return sb.ToString();
    }

    private Dictionary<string, string>? ParseWardrobeResponse(string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("wardrobe", out var wArray) ||
                wArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in wArray.EnumerateArray())
            {
                if (item.TryGetProperty("character_key", out var ck) &&
                    item.TryGetProperty("attire", out var att))
                {
                    var key = ck.GetString() ?? "";
                    var attire = att.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(attire))
                    {
                        result[key] = attire;
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI wardrobe response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
