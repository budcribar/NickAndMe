using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// AI Shot-Plan Refiner for multi-clip scenes in Stage 2 planning.
/// Eliminates copy-pasted visual prompt stagnation across extended monologues
/// by assigning progressive camera framings and micro-action beats.
/// </summary>
public sealed class ShotPlanRefiningClassifier
{
    public const string PromptVersion = "v1_product";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<ShotPlanRefiningClassifier> _log;

    public ShotPlanRefiningClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<ShotPlanRefiningClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyShotPlanRefineWithChat && _chat.IsConfigured;

    public static string SystemPrompt() => """
        You are an expert film director and cinematographer refining shot plans for a multi-clip scene in a movie screenplay.

        Your task: Given a list of planned video clips for a scene, eliminate copy-pasted visual prompt stagnation across consecutive clips by generating progressive camera framings and micro-action beats.

        RULES (HARD):
        1. Preserve exact character identity tokens (e.g. Character_The_Narrator, Character_The_Old_Man) and location tokens (e.g. Loc_Old_Man_S_Bedchamber).
        2. Do NOT invent unscripted major plot events or add unmentioned characters.
        3. Evolve the visual framing logically across clips:
           - Clip 1: Establishing / medium shot setting up the scene.
           - Mid clips: Dynamic shot progression (e.g. Extreme Close-Up on key prop/detail, Over-The-Shoulder, or reaction shot).
           - Later clips: Wide holding shot or intense reaction shot matching monologue climax.
        4. Continuation rules:
           - When changing to a distinct new camera angle/framing (e.g. close-up on detail), set veo_continuation_source to "none".
           - When continuing or holding the previous angle, set veo_continuation_source to "extend_previous".

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "refinements": [
            {
              "clip_number": 1,
              "visual_prompt": "INT. OLD MAN'S BEDCHAMBER - DAY. Wide establishing shot. Character_The_Narrator in doorway.",
              "veo_continuation_source": "none"
            },
            ...
          ]
        }
        """;

    public async Task<bool> RefinePlannedSceneAsync(
        Dictionary<string, object?> plannedScene,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        if (!IsEnabled) return false;

        if (!plannedScene.TryGetValue("veo_clips", out var clipsObj) ||
            clipsObj is not List<object?> rawClips || rawClips.Count < 3)
        {
            return false; // Skip single/dual clip scenes (no stagnation risk)
        }

        var clips = rawClips.OfType<Dictionary<string, object?>>().ToList();
        if (clips.Count < 3) return false;

        // Check if prompts are copy-pasted/duplicated across clips
        var prompts = clips.Select(c => CoerceString(c.TryGetValue("visual_prompt", out var vp) ? vp : null)).ToList();
        var uniquePrompts = prompts.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (uniquePrompts > (clips.Count / 2))
        {
            // Scene already has sufficient visual diversity
            return false;
        }

        onProgress?.Invoke($"AI Shot Refiner: Variating camera framing across {clips.Count} clips…");

        try
        {
            var userPrompt = BuildUserPrompt(plannedScene, clips);
            var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.ShotPlanRefineClassifyModel;
            var response = await _chat.CompleteAsync(
                SystemPrompt(),
                userPrompt,
                effectiveModel,
                // 0, not 0.2 — see BeatPacingClassifier for why (cacheable categorical labeling).
                temperature: 0,
                ct: ct,
                mode: ChatCallModes.ShotPlanRefineClassify).ConfigureAwait(false);

            return ApplyRefinements(plannedScene, clips, response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to run AI shot plan refinement for scene {Scene}", plannedScene.GetValueOrDefault("scene_number"));
            return false;
        }
    }

    private static string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> clips)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");
        sb.AppendLine($"CHARACTERS ON SCREEN: {JsonSerializer.Serialize(scene.GetValueOrDefault("characters_on_screen"))}");
        sb.AppendLine();
        sb.AppendLine("PLANNED CLIPS:");

        foreach (var c in clips)
        {
            var cNum = c.GetValueOrDefault("clip_number");
            var dur = c.GetValueOrDefault("duration_seconds");
            var audio = c.TryGetValue("audio_payload", out var aObj) && aObj is Dictionary<string, object?> aDict
                ? CoerceString(aDict.GetValueOrDefault("dialogue"))
                : "";
            var prompt = c.GetValueOrDefault("visual_prompt");

            sb.AppendLine($"Clip {cNum} ({dur}s):");
            if (!string.IsNullOrWhiteSpace(audio))
                sb.AppendLine($"  Dialogue/VO: \"{audio}\"");
            sb.AppendLine($"  Current Prompt: {prompt}");
        }

        return sb.ToString();
    }

    private bool ApplyRefinements(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> clips,
        string rawJson)
    {
        try
        {
            var cleaned = Regex.Replace(rawJson, @"```json|```", "").Trim();
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("refinements", out var refArray) ||
                refArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var refDict = new Dictionary<int, (string VisualPrompt, string Continuation)>();
            foreach (var item in refArray.EnumerateArray())
            {
                if (item.TryGetProperty("clip_number", out var cn) &&
                    item.TryGetProperty("visual_prompt", out var vp))
                {
                    var num = cn.GetInt32();
                    var prompt = vp.GetString() ?? "";
                    var cont = item.TryGetProperty("veo_continuation_source", out var cs)
                        ? cs.GetString() ?? "none"
                        : "none";
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        refDict[num] = (prompt, cont);
                    }
                }
            }

            if (refDict.Count == 0) return false;

            foreach (var clip in clips)
            {
                var cNum = ToInt(clip.GetValueOrDefault("clip_number"));
                if (refDict.TryGetValue(cNum, out var refTuple))
                {
                    clip["visual_prompt"] = refTuple.VisualPrompt;
                    clip["veo_continuation_source"] = refTuple.Continuation;
                }
            }

            _log.LogInformation("AI Shot Refiner applied dynamic camera framings to {Count} clips in scene {Scene}",
                refDict.Count, scene.GetValueOrDefault("scene_number"));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI shot plan refiner response JSON: {RawJson}", rawJson);
            return false;
        }
    }

    private static string CoerceString(object? val) => val?.ToString() ?? "";
    private static int ToInt(object? val) => val switch
    {
        int i => i,
        long l => (int)l,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
        string s when int.TryParse(s, out var p) => p,
        _ => 0,
    };
}
