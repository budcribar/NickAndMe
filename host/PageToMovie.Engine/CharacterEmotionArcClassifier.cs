using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

public sealed record EmotionDirective(
    int Intensity,
    string MicroExpression,
    string ActingPrompt);

/// <summary>
/// AI Classifier acting as an Acting Coach & Performance Director.
/// Calculates emotional intensity (1–10 scale) and facial micro-expressions
/// per beat for each character on screen, driving acting performances in video generation.
/// </summary>
public sealed class CharacterEmotionArcClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<CharacterEmotionArcClassifier> _log;

    public CharacterEmotionArcClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<CharacterEmotionArcClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyCharacterEmotionArcWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film Acting Coach and Performance Director directing character micro-acting.

        Your task: Given a list of scene beats, determine the emotional intensity (1 to 10 scale) and facial micro-expressions per beat ID.

        DIRECTIVES TO ASSIGN PER BEAT:
        1. intensity: Integer scale 1 (calm/neutral) to 10 (extreme panic/rage/terror).
        2. micro_expression: Specific facial muscle movement (e.g. "feverishly intense wide-eyed stare, tight unnatural smile, jaw muscle twitch").
        3. acting_prompt: Concise 10–20 word performance instruction (e.g. "Acting intensity 8/10: Feverishly intense wide-eyed stare with tight unnatural smile").

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "emotions": [
            {
              "beat_id": "b1",
              "intensity": 8,
              "micro_expression": "Feverishly intense wide-eyed stare, tight unnatural smile, jaw muscle twitch",
              "acting_prompt": "Acting intensity 8/10: Feverishly intense wide-eyed stare with tight unnatural smile"
            },
            ...
          ]
        }
        """;

    public async Task<Dictionary<string, EmotionDirective>?> ClassifySceneEmotionAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled || beats.Count == 0) return null;

        onProgress?.Invoke($"AI Acting Coach: Directing emotional intensity & micro-acting for {beats.Count} beats…");

        try
        {
            var userPrompt = BuildUserPrompt(scene, beats);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.CharacterEmotionArcClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                effectiveModel,
                // 0, not 0.2 — see BeatPacingClassifier for why (cacheable categorical labeling).
                temperature: 0,
                ct: ct,
                mode: ChatCallModes.CharacterEmotionArcClassify).ConfigureAwait(false);

            return ParseEmotionResponse(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI character emotion arc classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine();
        sb.AppendLine("BEATS TO DIRECT:");

        foreach (var b in beats)
        {
            var id = b.GetValueOrDefault("beat_id") ?? "b";
            var action = b.GetValueOrDefault("visual_event") ?? "";
            var spk = b.GetValueOrDefault("speaker") ?? "";
            var dlg = b.GetValueOrDefault("dialogue") ?? "";
            var ac = b.GetValueOrDefault("action_class") ?? "";

            sb.AppendLine($"Beat '{id}' (class: {ac}):");
            if (!string.IsNullOrWhiteSpace(spk?.ToString()) || !string.IsNullOrWhiteSpace(dlg?.ToString()))
                sb.AppendLine($"  Spoken ({spk}): \"{dlg}\"");
            if (!string.IsNullOrWhiteSpace(action?.ToString()))
                sb.AppendLine($"  Action prose: {action}");
        }

        return sb.ToString();
    }

    private Dictionary<string, EmotionDirective>? ParseEmotionResponse(string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("emotions", out var emoArray) ||
                emoArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, EmotionDirective>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in emoArray.EnumerateArray())
            {
                if (item.TryGetProperty("beat_id", out var bid))
                {
                    var id = bid.GetString() ?? "";
                    var intensity = item.TryGetProperty("intensity", out var val) ? Math.Clamp(val.GetInt32(), 1, 10) : 5;
                    var micro = item.TryGetProperty("micro_expression", out var me) ? me.GetString() ?? "" : "";
                    var prompt = item.TryGetProperty("acting_prompt", out var ap) ? ap.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = new EmotionDirective(intensity, micro, prompt);
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI character emotion response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
