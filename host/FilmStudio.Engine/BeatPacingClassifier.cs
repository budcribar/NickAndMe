using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// AI Classifier that dynamically calculates dramatic clip durations (2s–12s)
/// for scene beats based on narrative tension, emotional weight, and pacing rhythm.
/// Replaces static word/character count tables with cinematic rhythm analysis.
/// </summary>
public sealed class BeatPacingClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<BeatPacingClassifier> _log;

    public BeatPacingClassifier(
        IChatClient chat,
        IOptions<FilmStudioOptions> opts,
        ILogger<BeatPacingClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyBeatPacingWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film editor and director determining duration pacing for screenplay beats.

        Your task: Given a list of scene beats (dialogue, spoken lines, visual action descriptions), analyze the dramatic tension and emotional pacing to assign an optimal duration in seconds (between 2 and 12 seconds) for each beat.

        RULES (HARD):
        1. Range: Every duration MUST be an integer between 2 and 12 seconds.
        2. Pacing Guidelines:
           - Suspense / terror / tense waiting / silent observation: Assign longer duration (7s–12s) to allow visual tension to build.
           - Climax / sudden violent action / panic: Assign medium-short duration (4s–6s) for impact.
           - Rapid dialogue / brief interjection / fast movement: Assign short duration (2s–4s).
           - Monologue / steady dialogue: Base duration on spoken length (~2.5 words per second, min 3s, max 10s).
        3. Do NOT omit any beat IDs provided in the prompt.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "pacing": [
            {
              "beat_id": "b1",
              "duration_seconds": 6,
              "reason": "tense observation"
            },
            ...
          ]
        }
        """;

    public async Task<Dictionary<string, int>?> ClassifyScenePacingAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled || beats.Count == 0) return null;

        onProgress?.Invoke($"AI Beat Pacing: Analyzing dramatic rhythm for {beats.Count} beats…");

        try
        {
            var userPrompt = BuildUserPrompt(scene, beats);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.BeatPacingClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                effectiveModel,
                temperature: 0.2,
                ct: ct,
                mode: ChatCallModes.BeatPacingClassify).ConfigureAwait(false);

            return ParsePacingResponse(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI beat pacing classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine();
        sb.AppendLine("BEATS TO PACE:");

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

    private Dictionary<string, int>? ParsePacingResponse(string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("pacing", out var paceArray) ||
                paceArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in paceArray.EnumerateArray())
            {
                if (item.TryGetProperty("beat_id", out var bid) &&
                    item.TryGetProperty("duration_seconds", out var dur))
                {
                    var id = bid.GetString() ?? "";
                    var d = Math.Clamp(dur.GetInt32(), 2, 12);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = d;
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI beat pacing response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
