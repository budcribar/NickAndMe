using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

public sealed record CameraDirective(
    string ShotScale,
    string LensSpec,
    string CameraMovement,
    string FramingPrompt);

/// <summary>
/// AI Classifier acting as a Virtuoso Film Director / Director of Photography.
/// Assigns cinematic lens choices, camera movements (push-in, tracking, dolly),
/// and shot framing per beat ID based on narrative emotion.
/// </summary>
public sealed class CameraDirectorClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<CameraDirectorClassifier> _log;

    public CameraDirectorClassifier(
        IChatClient chat,
        IOptions<FilmStudioOptions> opts,
        ILogger<CameraDirectorClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyCameraDirectorWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are a Virtuoso Film Director and Director of Photography (DP) directing camera composition and movement.

        Your task: Given a list of scene beats, assign cinematic camera directives per beat ID based on film grammar and narrative tension.

        DIRECTIVES TO ASSIGN PER BEAT:
        1. shot_scale: "wide", "medium", "close_up", or "extreme_close_up".
        2. lens_spec: Choice of lens (e.g. "24mm wide anamorphic lens", "35mm prime lens", "85mm f/1.4 portrait lens", "100mm macro lens").
        3. camera_movement: Specific cinematic movement (e.g. "slow 10% dolly push-in", "locked tripod hold", "low-angle slow tracking shot", "steady handheld tilt").
        4. framing_prompt: A 10–25 word description of the camera shot composition (e.g. "Low-angle medium shot, 35mm lens, camera slowly pushes in as character speaks").

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "directives": [
            {
              "beat_id": "b1",
              "shot_scale": "wide",
              "lens_spec": "24mm wide anamorphic lens",
              "camera_movement": "locked tripod establishing shot",
              "framing_prompt": "Establishing wide shot, 24mm anamorphic lens, static locked camera framing subject centrally."
            },
            ...
          ]
        }
        """;

    public async Task<Dictionary<string, CameraDirective>?> ClassifySceneCameraAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled || beats.Count == 0) return null;

        onProgress?.Invoke($"AI Camera Director: Directing camera lenses & movement for {beats.Count} beats…");

        try
        {
            var userPrompt = BuildUserPrompt(scene, beats);
            var model = _opts.CameraDirectorClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                model,
                temperature: 0.2,
                ct: ct,
                mode: ChatCallModes.CameraDirectorClassify).ConfigureAwait(false);

            return ParseCameraResponse(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI camera director classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
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

    private Dictionary<string, CameraDirective>? ParseCameraResponse(string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("directives", out var dirArray) ||
                dirArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, CameraDirective>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in dirArray.EnumerateArray())
            {
                if (item.TryGetProperty("beat_id", out var bid))
                {
                    var id = bid.GetString() ?? "";
                    var scale = item.TryGetProperty("shot_scale", out var ss) ? ss.GetString() ?? "medium" : "medium";
                    var lens = item.TryGetProperty("lens_spec", out var ls) ? ls.GetString() ?? "35mm lens" : "35mm lens";
                    var move = item.TryGetProperty("camera_movement", out var cm) ? cm.GetString() ?? "locked tripod" : "locked tripod";
                    var framing = item.TryGetProperty("framing_prompt", out var fp) ? fp.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = new CameraDirective(scale, lens, move, framing);
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI camera director response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
