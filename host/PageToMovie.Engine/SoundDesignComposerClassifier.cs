using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

public sealed record SoundDesignDirective(
    string AmbientLayer,
    string FoleyLayer,
    string ScoreLayer);

/// <summary>
/// AI Classifier acting as a Film Sound Designer & Audio Director.
/// Composes 3-track cinematic audio blueprints (ambient background, physical foley, score mood)
/// per beat for audio synthesis and native ffmpeg multi-channel remuxing.
/// </summary>
public sealed class SoundDesignComposerClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<SoundDesignComposerClassifier> _log;

    public SoundDesignComposerClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<SoundDesignComposerClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifySoundDesignComposerWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film Sound Designer and Audio Supervisor creating multi-track cinematic sound designs.

        Your task: Given a list of scene beats, compose 3 distinct audio layers per beat ID:

        LAYERS TO BUILD PER BEAT:
        1. ambient_layer: Environmental acoustics & background ambience (e.g. "Heavy wind howling outside with room reverb 0.4").
        2. foley_layer: Specific physical contact and movement sound effects (e.g. "Creaking wooden floorboards under deliberate slow footsteps").
        3. score_layer: Musical mood, tonal texture, or rhythmic pulse (e.g. "Low sub-bass cello drone rising to an 80 BPM heartbeat pulse").

        RULES:
        - Keep each layer description concise (5–15 words).
        - Ensure layers reflect the scene's emotional tension and setting.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "sound_design": [
            {
              "beat_id": "b1",
              "ambient_layer": "Quiet night room room-tone with distant howling wind",
              "foley_layer": "Subtle rustle of clothes, quiet rhythmic breathing",
              "score_layer": "Tense low-frequency dark ambient drone"
            },
            ...
          ]
        }
        """;

    public async Task<Dictionary<string, SoundDesignDirective>?> ClassifySceneSoundDesignAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled || beats.Count == 0) return null;

        onProgress?.Invoke($"AI Sound Director: Composing 3-layer sound design for {beats.Count} beats…");

        try
        {
            var userPrompt = BuildUserPrompt(scene, beats);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.SoundDesignComposerClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                effectiveModel,
                // 0, not 0.2 — see BeatPacingClassifier for why (cacheable categorical labeling).
                temperature: 0,
                ct: ct,
                mode: ChatCallModes.SoundDesignComposerClassify).ConfigureAwait(false);

            return ParseSoundResponse(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI sound design classification for scene {Scene}", scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine();
        sb.AppendLine("BEATS TO COMPOSE SOUND FOR:");

        foreach (var b in beats)
        {
            var id = b.GetValueOrDefault("beat_id") ?? "b";
            var action = b.GetValueOrDefault("visual_event") ?? "";
            var spk = b.GetValueOrDefault("speaker") ?? "";
            var dlg = b.GetValueOrDefault("dialogue") ?? "";
            var amb = b.GetValueOrDefault("ambient") ?? "";
            var sfx = b.GetValueOrDefault("sfx") ?? "";

            sb.AppendLine($"Beat '{id}':");
            if (!string.IsNullOrWhiteSpace(spk?.ToString()) || !string.IsNullOrWhiteSpace(dlg?.ToString()))
                sb.AppendLine($"  Spoken ({spk}): \"{dlg}\"");
            if (!string.IsNullOrWhiteSpace(action?.ToString()))
                sb.AppendLine($"  Action prose: {action}");
            if (!string.IsNullOrWhiteSpace(amb?.ToString()))
                sb.AppendLine($"  Base ambient: {amb}");
            if (!string.IsNullOrWhiteSpace(sfx?.ToString()))
                sb.AppendLine($"  Base SFX: {sfx}");
        }

        return sb.ToString();
    }

    private Dictionary<string, SoundDesignDirective>? ParseSoundResponse(string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("sound_design", out var sdArray) ||
                sdArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, SoundDesignDirective>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in sdArray.EnumerateArray())
            {
                if (item.TryGetProperty("beat_id", out var bid))
                {
                    var id = bid.GetString() ?? "";
                    var amb = item.TryGetProperty("ambient_layer", out var a) ? a.GetString() ?? "" : "";
                    var fol = item.TryGetProperty("foley_layer", out var f) ? f.GetString() ?? "" : "";
                    var scr = item.TryGetProperty("score_layer", out var s) ? s.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = new SoundDesignDirective(amb, fol, scr);
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI sound design response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
