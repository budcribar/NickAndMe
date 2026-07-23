using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

public sealed record DepthOfFieldDirective(
    string Aperture,
    string FocalPlane,
    string RackFocus);

/// <summary>
/// AI Classifier acting as a Focus Puller & Optical Cinematographer.
/// Assigns optical aperture settings (f/1.4 to f/8), primary focal planes, and dynamic
/// rack-focus transitions per shot to guide viewer attention.
/// </summary>
public sealed class DepthOfFieldClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<DepthOfFieldClassifier> _log;

    public DepthOfFieldClassifier(
        IChatClient chat,
        IOptions<FilmStudioOptions> opts,
        ILogger<DepthOfFieldClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyDepthOfFieldWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert Focus Puller and Optical Cinematographer directing camera focus and depth of field.

        Your task: Given a list of scene beats and camera directives, assign optical focus specifications per beat ID:

        DIRECTIVES TO ASSIGN PER BEAT:
        1. aperture: f-stop spec (e.g. "f/1.4 shallow depth of field, creamy background bokeh", "f/2.8 moderate depth of field", "f/8 deep focus, sharp environment").
        2. focal_plane: Primary subject focus target (e.g. "Foreground: lantern latch", "Midground: Narrator's eyes", "Background: closed bedroom door").
        3. rack_focus: Focus transition instruction, if any (e.g. "Rack focus from foreground lantern latch at t=0s to Old Man's eyes in background at t=2s", "Static focus on narrator").

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "dof": [
            {
              "beat_id": "b1",
              "aperture": "f/1.4 shallow depth of field, creamy soft bokeh",
              "focal_plane": "Midground: Narrator's eyes",
              "rack_focus": "Static focus on narrator's eyes"
            },
            ...
          ]
        }
        """;

    public async Task<Dictionary<string, DepthOfFieldDirective>?> ClassifySceneDepthOfFieldAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled || beats.Count == 0) return null;

        onProgress?.Invoke($"AI Focus Puller: Directing optical aperture & rack focus for {beats.Count} beats…");

        try
        {
            var userPrompt = BuildUserPrompt(scene, beats);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.DepthOfFieldClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                effectiveModel,
                temperature: 0.2,
                ct: ct,
                mode: ChatCallModes.DepthOfFieldClassify).ConfigureAwait(false);

            return ParseDofResponse(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI depth of field classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine();
        sb.AppendLine("BEATS TO DIRECT OPTICALLY:");

        foreach (var b in beats)
        {
            var id = b.GetValueOrDefault("beat_id") ?? "b";
            var action = b.GetValueOrDefault("visual_event") ?? "";
            var psub = b.GetValueOrDefault("primary_subject") ?? "";
            var dlg = b.GetValueOrDefault("dialogue") ?? "";

            sb.AppendLine($"Beat '{id}' (subject: {psub}):");
            if (!string.IsNullOrWhiteSpace(dlg?.ToString()))
                sb.AppendLine($"  Spoken: \"{dlg}\"");
            if (!string.IsNullOrWhiteSpace(action?.ToString()))
                sb.AppendLine($"  Action prose: {action}");
        }

        return sb.ToString();
    }

    private Dictionary<string, DepthOfFieldDirective>? ParseDofResponse(string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("dof", out var dofArray) ||
                dofArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, DepthOfFieldDirective>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in dofArray.EnumerateArray())
            {
                if (item.TryGetProperty("beat_id", out var bid))
                {
                    var id = bid.GetString() ?? "";
                    var ap = item.TryGetProperty("aperture", out var a) ? a.GetString() ?? "" : "";
                    var fp = item.TryGetProperty("focal_plane", out var f) ? f.GetString() ?? "" : "";
                    var rf = item.TryGetProperty("rack_focus", out var r) ? r.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = new DepthOfFieldDirective(ap, fp, rf);
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI depth of field response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
