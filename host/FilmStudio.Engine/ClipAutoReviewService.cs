using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Models;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// Per-clip AI review: sample previous tail + current frames, draft structured suggestions.
/// Does not apply edits or regen — user confirms via Apply → Regen.
/// </summary>
public sealed class ClipAutoReviewService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ProjectStore _projects;
    private readonly IGrokVisionClient _vision;
    private readonly FfmpegRemuxService _ffmpeg;
    private readonly EditLogService _logs;
    private readonly PromptPackService _promptPacks;
    private readonly ProjectRulesService _projectRules;
    private readonly ILogger<ClipAutoReviewService> _log;

    public ClipAutoReviewService(
        ProjectStore projects,
        IGrokVisionClient vision,
        FfmpegRemuxService ffmpeg,
        EditLogService logs,
        PromptPackService promptPacks,
        ProjectRulesService projectRules,
        ILogger<ClipAutoReviewService> log)
    {
        _projects = projects;
        _vision = vision;
        _ffmpeg = ffmpeg;
        _logs = logs;
        _promptPacks = promptPacks;
        _projectRules = projectRules;
        _log = log;
    }

    public bool IsConfigured => _vision.IsConfigured;

    public string DraftPath(string projectId, int scene, int clip) =>
        Path.Combine(
            _projects.GetProjectDir(projectId),
            "assets",
            "review",
            $"S{scene:D2}C{clip:D2}.auto_review.json");

    public ClipAutoReviewDraft? LoadDraft(string projectId, int scene, int clip)
    {
        var path = DraftPath(projectId, scene, clip);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClipAutoReviewDraft>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public void SaveDraft(ClipAutoReviewDraft draft)
    {
        var path = DraftPath(draft.ProjectId, draft.Scene, draft.Clip);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(draft, JsonOpts) + "\n");
    }

    public async Task<ClipAutoReviewDraft> ReviewAsync(
        string projectId,
        int scene,
        int clip,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_vision.IsConfigured)
            throw new InvalidOperationException("Connect service (XAI_API_KEY) for clip review.");
        if (!_ffmpeg.IsAvailable())
            throw new InvalidOperationException("ffmpeg required to sample frames for clip review.");

        var projectDir = _projects.GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var clipPath = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        if (!File.Exists(clipPath) || new FileInfo(clipPath).Length < 512)
            throw new InvalidOperationException($"Clip not on disk: S{scene:D2}C{clip:D2}");

        onProgress?.Invoke(5, 100, "Loading clip plan…");
        var plan = LoadClipPlan(projectId, scene, clip);
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);

        string? prevPath = null;
        if (clip > 1)
        {
            var cand = Path.Combine(videoDir, $"scene_{scene:D2}_clip_{clip - 1:D2}.mp4");
            if (File.Exists(cand) && new FileInfo(cand).Length >= 512)
                prevPath = cand;
        }

        var workDir = Path.Combine(projectDir, "assets", "review", $"_frames_S{scene:D2}C{clip:D2}");
        try
        {
            if (Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* */ }
            }
            Directory.CreateDirectory(workDir);

            var images = new List<(string Path, string Label)>();
            if (prevPath is not null)
            {
                onProgress?.Invoke(15, 100, "Sampling end of previous clip…");
                var prevFrames = await ExtractTailFramesAsync(prevPath, workDir, "prev", count: 3, ct);
                foreach (var f in prevFrames)
                    images.Add((f, "PREVIOUS_CLIP_TAIL"));
            }
            else
            {
                onProgress?.Invoke(15, 100, clip == 1
                    ? "First clip — no previous for continuity…"
                    : "Previous clip missing — reviewing this clip only…");
            }

            onProgress?.Invoke(35, 100, "Sampling this clip…");
            var curFrames = await ExtractSpanFramesAsync(clipPath, workDir, "cur", ct);
            foreach (var f in curFrames)
                images.Add((f, "CURRENT_CLIP"));

            if (images.Count == 0)
                throw new InvalidOperationException("Could not extract frames from clip video.");

            onProgress?.Invoke(55, 100, "AI reviewing continuity and quality…");
            var prompt = BuildReviewPrompt(scene, clip, plan, profiles, images, prevPath is not null);
            try
            {
                var pack = _promptPacks.LoadActivePackText(PromptPackService.KindAutoReview);
                if (!string.IsNullOrWhiteSpace(pack))
                    prompt += "\n\n" + pack.Trim();
                var rules = _projectRules.GetActiveRulesBlock(projectId);
                if (!string.IsNullOrWhiteSpace(rules))
                    prompt += "\n\n" + rules.Trim();
            }
            catch { /* non-fatal */ }
            var imagePaths = images.Select(i => i.Path).ToList();
            var raw = await _vision.CompleteWithImagesAsync(
                prompt,
                imagePaths,
                model: "grok-4.5",
                detail: "low",
                ct: ct);

            onProgress?.Invoke(85, 100, "Parsing suggestions…");
            var draft = ParseDraft(raw, projectId, scene, clip, plan, profiles, prevPath is not null);
            draft.GeneratedAt = DateTimeOffset.UtcNow;
            SaveDraft(draft);

            await TryLogAsync(projectId, scene, clip, draft, ct);

            onProgress?.Invoke(100, 100, "Review draft ready");
            return draft;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Write accepted suggestion values into cast seeds / blueprint clip (with before/after log).</summary>
    public void ApplySuggestions(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<ClipAutoReviewApplyItem> items)
    {
        if (items is null || items.Count == 0)
            throw new InvalidOperationException("No suggestions selected to apply.");

        var plan = LoadClipPlan(projectId, scene, clip);
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);
        var beforeParts = new List<string>();
        var afterParts = new List<string>();

        foreach (var item in items)
        {
            var layer = (item.Layer ?? "clip").Trim().ToLowerInvariant();
            var field = (item.Field ?? "").Trim().ToLowerInvariant();
            var value = item.Value ?? "";
            var before = "";

            if (layer == "character" && !string.IsNullOrWhiteSpace(item.CharKey))
            {
                profiles.TryGetValue(item.CharKey, out var p);
                before = field switch
                {
                    "description" => p?.Description ?? "",
                    "visual_lock" => p?.VisualLock ?? "",
                    "voice_profile" => p?.VoiceProfile ?? "",
                    _ => "",
                };
                switch (field)
                {
                    case "voice_profile":
                        _projects.UpdateCharacterSeedText(projectId, item.CharKey, voiceProfile: value);
                        break;
                    case "description":
                        _projects.UpdateCharacterSeedText(projectId, item.CharKey, description: value);
                        break;
                    case "visual_lock":
                        _projects.UpdateCharacterSeedText(projectId, item.CharKey, visualLock: value);
                        break;
                    default:
                        _log.LogWarning("Unknown character field {Field}", field);
                        break;
                }
                beforeParts.Add($"{item.CharKey}.{field}: {Trim(before, 400)}");
                afterParts.Add($"{item.CharKey}.{field}: {Trim(value, 400)}");
            }
            else if (layer == "clip" && field is "visual_prompt" or "prompt")
            {
                before = plan.VisualPrompt;
                _projects.UpdateClipVisualPrompt(projectId, scene, clip, value);
                beforeParts.Add($"clip.visual_prompt: {Trim(before, 600)}");
                afterParts.Add($"clip.visual_prompt: {Trim(value, 600)}");
                plan.VisualPrompt = value;
            }
        }

        var draft = LoadDraft(projectId, scene, clip);
        if (draft is not null)
        {
            draft.RawSummary = (draft.RawSummary ?? "") + "\n[applied " + DateTimeOffset.UtcNow.ToString("O") + "]";
            SaveDraft(draft);
        }

        try
        {
            _logs.AddAsync(
                projectId,
                "auto_review_apply",
                $"Applied {items.Count} suggestion(s) to S{scene:D2}C{clip:D2}",
                scene: scene,
                clip: clip,
                actionTaken: "apply_suggestions",
                before: string.Join("\n---\n", beforeParts),
                after: string.Join("\n---\n", afterParts),
                category: draft?.Category,
                suggestionCount: items.Count).GetAwaiter().GetResult();
        }
        catch { /* non-fatal */ }
    }

    private async Task TryLogAsync(
        string projectId, int scene, int clip, ClipAutoReviewDraft draft, CancellationToken ct)
    {
        try
        {
            await _logs.AddAsync(
                projectId,
                "auto_review",
                draft.Note.Length > 0 ? draft.Note : draft.Suggestion,
                scene: scene,
                clip: clip,
                actionTaken: $"suggestion={draft.Suggestion};category={draft.Category};confidence={draft.Confidence}",
                category: draft.Category,
                suggestion: draft.Suggestion,
                confidence: draft.Confidence,
                continuity: draft.Continuity,
                suggestionCount: draft.Suggestions.Count,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "auto_review log skip");
        }
    }

    private ClipPlan LoadClipPlan(string projectId, int scene, int clip)
    {
        var plan = new ClipPlan();
        try
        {
            using var doc = _projects.LoadBlueprintAsync(projectId).GetAwaiter().GetResult();
            if (doc is null) return plan;
            var root = doc.RootElement;
            if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
                return plan;
            foreach (var s in scenes.EnumerateArray())
            {
                if (!s.TryGetProperty("scene_number", out var sn) || !sn.TryGetInt32(out var n) || n != scene)
                    continue;
                if (!s.TryGetProperty("clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
                    break;
                foreach (var c in clips.EnumerateArray())
                {
                    if (!c.TryGetProperty("clip_number", out var cn) || !cn.TryGetInt32(out var cnum) || cnum != clip)
                        continue;
                    plan.VisualPrompt = c.TryGetProperty("visual_prompt", out var vp) ? vp.GetString() ?? "" : "";
                    if (c.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object)
                    {
                        plan.Dialogue = ap.TryGetProperty("dialogue", out var d) ? d.GetString() ?? "" : "";
                        plan.Speaker = ap.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "" : "";
                        plan.Delivery = ap.TryGetProperty("delivery", out var del) ? del.GetString() ?? "" : "";
                    }
                    if (c.TryGetProperty("characters_present", out var cp) && cp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var x in cp.EnumerateArray())
                        {
                            var k = x.GetString();
                            if (!string.IsNullOrWhiteSpace(k)) plan.Characters.Add(k!);
                        }
                    }
                    return plan;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "LoadClipPlan failed");
        }
        return plan;
    }

    private static string BuildReviewPrompt(
        int scene,
        int clip,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        List<(string Path, string Label)> images,
        bool hasPrev)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a film QC assistant for a short children's/book adaptation.");
        sb.AppendLine($"Review clip S{scene:D2}C{clip:D2}.");
        sb.AppendLine();
        sb.AppendLine("Images are labeled in order:");
        for (var i = 0; i < images.Count; i++)
            sb.AppendLine($"  IMAGE_{i + 1}: {images[i].Label}");
        if (hasPrev)
            sb.AppendLine("PREVIOUS_CLIP_TAIL frames are the END of the prior clip — judge continuity into CURRENT_CLIP (especially its START).");
        else
            sb.AppendLine("No previous clip tail available.");
        sb.AppendLine();
        sb.AppendLine("Planned visual_prompt:");
        sb.AppendLine(plan.VisualPrompt.Length > 0 ? plan.VisualPrompt : "(missing)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(plan.Dialogue))
        {
            sb.AppendLine($"Dialogue speaker={plan.Speaker} delivery={plan.Delivery}:");
            sb.AppendLine($"\"{plan.Dialogue}\"");
        }
        else
            sb.AppendLine("No dialogue on this clip.");
        sb.AppendLine();
        if (plan.Characters.Count > 0)
        {
            sb.AppendLine("Cast present:");
            foreach (var key in plan.Characters)
            {
                profiles.TryGetValue(key, out var p);
                var name = p?.DisplayName ?? key;
                var look = p?.Description ?? "";
                var voice = p?.VoiceProfile ?? "";
                sb.AppendLine($"- {key} ({name}) look: {Trim(look, 200)} voice: {Trim(voice, 120)}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Check: continuity from prev tail, character look, lip/speech vs dialogue, empty/dead frames, wrong action.");
        sb.AppendLine("Respond with JSON ONLY (no markdown):");
        sb.AppendLine("""
            {
              "suggestion": "pass"|"fail"|"unclear",
              "category": "continuity"|"wrong_look"|"wrong_voice"|"silent"|"framing"|"other",
              "confidence": "high"|"medium"|"low",
              "continuity": "ok"|"jump"|"unclear"|"n/a",
              "note": "one short human-readable review note",
              "suggestions": [
                {
                  "layer": "clip"|"character",
                  "field": "visual_prompt"|"voice_profile"|"description"|"visual_lock",
                  "char_key": "Character_... or null",
                  "label": "short UI label",
                  "suggested_value": "full replacement text for that field",
                  "include_by_default": true,
                  "rationale": "why"
                }
              ]
            }
            """);
        sb.AppendLine("Rules: only suggest changes that would improve a re-gen. Prefer clip visual_prompt. Character changes only if look/voice is clearly wrong. Keep Character_* keys. Empty suggestions[] if pass/no edit needed.");
        return sb.ToString();
    }

    private static ClipAutoReviewDraft ParseDraft(
        string raw,
        string projectId,
        int scene,
        int clip,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        bool hasPrev)
    {
        var draft = new ClipAutoReviewDraft
        {
            ProjectId = projectId,
            Scene = scene,
            Clip = clip,
            IncludedPreviousTail = hasPrev,
            RawSummary = Trim(raw, 2000),
        };

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                draft.Suggestion = "unclear";
                draft.Note = "Could not parse review response.";
                return draft;
            }

            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            draft.Suggestion = GetStr(root, "suggestion", "unclear").ToLowerInvariant();
            draft.Category = GetStr(root, "category", "other").ToLowerInvariant();
            draft.Confidence = GetStr(root, "confidence", "medium").ToLowerInvariant();
            draft.Continuity = GetStr(root, "continuity", hasPrev ? "unclear" : "n/a").ToLowerInvariant();
            draft.Note = GetStr(root, "note", "");

            if (root.TryGetProperty("suggestions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var layer = GetStr(item, "layer", "clip").ToLowerInvariant();
                    var field = GetStr(item, "field", "");
                    if (string.IsNullOrWhiteSpace(field) && item.TryGetProperty("suggested_value", out _))
                        field = layer == "character" ? "voice_profile" : "visual_prompt";
                    var charKey = GetStr(item, "char_key", "") is { Length: > 0 } ck ? ck : null;
                    if (charKey is null && item.TryGetProperty("charKey", out var ck2))
                        charKey = ck2.GetString();

                    var suggested = GetStr(item, "suggested_value", "");
                    if (string.IsNullOrWhiteSpace(suggested))
                        suggested = GetStr(item, "suggestedValue", "");
                    if (string.IsNullOrWhiteSpace(suggested)) continue;

                    var current = "";
                    if (layer == "clip" && field is "visual_prompt" or "prompt")
                        current = plan.VisualPrompt;
                    else if (layer == "character" && charKey is not null &&
                             profiles.TryGetValue(charKey, out var p))
                    {
                        current = field switch
                        {
                            "description" => p.Description,
                            "visual_lock" => p.VisualLock,
                            "voice_profile" => p.VoiceProfile,
                            _ => "",
                        };
                    }

                    var include = true;
                    if (item.TryGetProperty("include_by_default", out var ib) &&
                        ib.ValueKind is JsonValueKind.False)
                        include = false;
                    if (item.TryGetProperty("includeByDefault", out var ib2) &&
                        ib2.ValueKind is JsonValueKind.False)
                        include = false;

                    draft.Suggestions.Add(new ClipAutoReviewSuggestion
                    {
                        Layer = layer is "character" or "scene" ? layer : "clip",
                        Field = field,
                        CharKey = charKey,
                        Label = GetStr(item, "label", field),
                        CurrentValue = current,
                        SuggestedValue = suggested,
                        IncludeByDefault = include,
                        Rationale = GetStr(item, "rationale", ""),
                    });
                }
            }
        }
        catch (Exception)
        {
            draft.Suggestion = "unclear";
            if (string.IsNullOrWhiteSpace(draft.Note))
                draft.Note = "Review response parse error.";
        }

        return draft;
    }

    private async Task<List<string>> ExtractTailFramesAsync(
        string videoPath, string workDir, string prefix, int count, CancellationToken ct)
    {
        // Last ~1.5s at ~2 fps
        var pattern = Path.Combine(workDir, $"{prefix}_%02d.jpg");
        var args =
            $"-y -sseof -1.5 -i \"{videoPath}\" -vf fps=2 -frames:v {count} -q:v 5 \"{pattern}\"";
        await RunFfmpegAsync(args, ct);
        return Directory.GetFiles(workDir, $"{prefix}_*.jpg").OrderBy(f => f).ToList();
    }

    private async Task<List<string>> ExtractSpanFramesAsync(
        string videoPath, string workDir, string prefix, CancellationToken ct)
    {
        // ~3 frames across the clip (start-ish, mid, end-ish)
        var pattern = Path.Combine(workDir, $"{prefix}_%02d.jpg");
        var args =
            $"-y -i \"{videoPath}\" -vf \"fps=1/2\" -frames:v 3 -q:v 5 \"{pattern}\"";
        await RunFfmpegAsync(args, ct);
        var files = Directory.GetFiles(workDir, $"{prefix}_*.jpg").OrderBy(f => f).ToList();
        if (files.Count > 0) return files;

        // Fallback: single frame at 0.5s
        var one = Path.Combine(workDir, $"{prefix}_01.jpg");
        await RunFfmpegAsync($"-y -ss 0.5 -i \"{videoPath}\" -frames:v 1 -q:v 5 \"{one}\"", ct);
        return File.Exists(one) ? new List<string> { one } : new List<string>();
    }

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg.FfmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) throw new InvalidOperationException("Could not start ffmpeg.");
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            _log.LogWarning("ffmpeg frame extract exit {Code}: {Err}",
                proc.ExitCode, err.Length > 300 ? err[..300] : err);
        }
    }

    private static string GetStr(JsonElement el, string name, string fallback)
    {
        if (!el.TryGetProperty(name, out var p)) return fallback;
        return p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];

    private sealed class ClipPlan
    {
        public string VisualPrompt { get; set; } = "";
        public string Dialogue { get; set; } = "";
        public string Speaker { get; set; } = "";
        public string Delivery { get; set; } = "";
        public List<string> Characters { get; } = new();
    }
}
