using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Stage 1 bible → Stage 2 clip blueprint (deterministic, no API).
/// Covers plan_scene, visual prompt packing, wardrobe continuity, duration allocation.
/// </summary>
public sealed class Stage2PlannerService
{
    private const string GlobalNegative =
        "no legible text, no watermarks, no logos, no extra limbs, " +
        "blur/obscure environmental signage or screens, no name tags, no name badges, " +
        "no embroidered names, no lower thirds, no personal names on clothing or props";

    private const int GrokMinClip = 6;
    private const int GrokMaxClip = 10;
    private const int GrokAbsMax = 15;
    private const int GrokDefault = 8;
    private const int GrokSceneMin = 8;
    private const int PromptSoft = 500;
    private const int PromptHard = 800;

    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ProjectStore _projects;
    private readonly ILogger<Stage2PlannerService> _log;

    public Stage2PlannerService(ProjectStore projects, ILogger<Stage2PlannerService> log)
    {
        _projects = projects;
        _log = log;
    }

    public Stage2PlanResult PlanAsync(
        string projectId,
        string resolution = "720p",
        string scenes = "all",
        Action<string>? onProgress = null)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        var stage1Path = _projects.ResolveScenesJsonPath(projectId);
        if (!File.Exists(stage1Path))
            throw new InvalidOperationException($"Stage 1 bible not found: {stage1Path}");

        onProgress?.Invoke($"Loading Stage 1: {Path.GetFileName(stage1Path)}");
        var stage1 = GrokChatClient.ParseJsonObject(File.ReadAllText(stage1Path));
        var gpv = GetDict(stage1, "global_production_variables");
        var locSeeds = GetDict(gpv, "location_seed_tokens");
        var charSeeds = GetDict(gpv, "character_seed_tokens");
        NormalizeCharPlaceholders(charSeeds);

        var want = ParseSceneRange(scenes);
        var scenesIn = GetScenes(stage1)
            .Where(s =>
            {
                if (want is null) return true;
                var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
                return want.Contains(n);
            })
            .ToList();

        onProgress?.Invoke($"Planning {scenesIn.Count} scene(s) @ {resolution}…");
        var styleLock = CoerceString(gpv.TryGetValue("render_style_lock", out var rsl) ? rsl : null);
        var planned = new List<Dictionary<string, object?>>();
        foreach (var s in scenesIn)
        {
            var sn = ToInt(s.TryGetValue("scene_number", out var n) ? n : 0);
            onProgress?.Invoke($"  Scene {sn}…");
            planned.Add(PlanScene(s, resolution, locSeeds, charSeeds, styleLock));
        }

        var outPath = _projects.FindBlueprintPath(projectId)
            ?? Path.Combine(projectDir, "blueprint.clips.grok.json");
        if (File.Exists(outPath))
        {
            var bak = outPath + $".bak_pre_stage2_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(outPath, bak, overwrite: true);
            onProgress?.Invoke($"Backed up blueprint → {Path.GetFileName(bak)}");
        }

        // Partial scene filter: merge into existing blueprint so we don't wipe other scenes.
        // Backup was copied FROM outPath — outPath still has prior content until we write below.
        Dictionary<string, object?> plan;
        if (want is not null && File.Exists(outPath))
        {
            try
            {
                var existing = GrokChatClient.ParseJsonObject(File.ReadAllText(outPath));
                plan = MergePlannedScenes(existing, planned, stage1, gpv, stage1Path, resolution, scenes);
                onProgress?.Invoke("Merged planned scenes into existing blueprint");
            }
            catch
            {
                plan = BuildFullPlan(stage1, gpv, planned, stage1Path, resolution, scenes);
            }
        }
        else
        {
            plan = BuildFullPlan(stage1, gpv, planned, stage1Path, resolution, scenes);
        }

        File.WriteAllText(outPath, JsonSerializer.Serialize(plan, JsonWrite) + "\n");
        var meta = GetDict(plan, "stage2_meta");
        var totalClips = ToInt(meta.TryGetValue("total_clips", out var tc) ? tc : 0);
        var sceneCount = GetList(plan, "scenes").Count;
        var totalDur = ToInt(meta.TryGetValue("total_duration_seconds", out var td) ? td : 0);
        onProgress?.Invoke(
            $"Wrote {Path.GetFileName(outPath)} · {sceneCount} scenes · {totalClips} clips");

        return new Stage2PlanResult
        {
            Ok = true,
            OutPath = outPath,
            SceneCount = sceneCount,
            ClipCount = totalClips,
            DurationSeconds = totalDur,
        };
    }

    private static Dictionary<string, object?> BuildFullPlan(
        Dictionary<string, object?> stage1,
        Dictionary<string, object?> gpv,
        List<Dictionary<string, object?>> planned,
        string stage1Path,
        string resolution,
        string scenesFilter) => new()
    {
        ["schema_version"] = "stage2.v1",
        ["movie_title"] = stage1.TryGetValue("movie_title", out var mt) ? mt : null,
        ["source_book_title"] = stage1.TryGetValue("source_book_title", out var sbt) ? sbt : null,
        ["video_provider_profile"] = "grok",
        ["global_production_variables"] = gpv,
        ["scenes"] = planned.Cast<object?>().ToList(),
        ["stage2_meta"] = MakeMeta(stage1, planned, stage1Path, resolution, scenesFilter),
    };

    private static Dictionary<string, object?> MergePlannedScenes(
        Dictionary<string, object?> existing,
        List<Dictionary<string, object?>> planned,
        Dictionary<string, object?> stage1,
        Dictionary<string, object?> gpv,
        string stage1Path,
        string resolution,
        string scenesFilter)
    {
        var byN = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var s in GetList(existing, "scenes").OfType<Dictionary<string, object?>>())
        {
            var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
            if (n > 0) byN[n] = s;
        }
        foreach (var s in planned)
        {
            var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
            if (n > 0) byN[n] = s;
        }
        var all = byN.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        existing["schema_version"] = "stage2.v1";
        existing["movie_title"] = stage1.TryGetValue("movie_title", out var mt) ? mt
            : existing.TryGetValue("movie_title", out var emt) ? emt : null;
        existing["source_book_title"] = stage1.TryGetValue("source_book_title", out var sbt) ? sbt
            : existing.TryGetValue("source_book_title", out var esbt) ? esbt : null;
        existing["video_provider_profile"] = "grok";
        existing["global_production_variables"] = gpv;
        existing["scenes"] = all.Cast<object?>().ToList();
        existing["stage2_meta"] = MakeMeta(stage1, all, stage1Path, resolution, scenesFilter);
        return existing;
    }

    private static Dictionary<string, object?> MakeMeta(
        Dictionary<string, object?> stage1,
        List<Dictionary<string, object?>> planned,
        string stage1Path,
        string resolution,
        string scenesFilter) => new()
    {
        ["source_stage1"] = Path.GetFileName(stage1Path),
        ["resolution"] = resolution,
        ["scene_filter"] = scenesFilter,
        ["planner"] = "Stage2PlannerService (C#)",
        ["prompt_soft_max"] = PromptSoft,
        ["prompt_hard_max"] = PromptHard,
        ["stage1_fingerprint"] = Stage1Fingerprint(stage1),
        ["planned_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        ["total_duration_seconds"] = planned.Sum(s =>
            ToInt(s.TryGetValue("total_estimated_duration_seconds", out var d) ? d : 0)),
        ["total_clips"] = planned.Sum(s => GetList(s, "veo_clips").Count),
    };

    private static Dictionary<string, object?> PlanScene(
        Dictionary<string, object?> scene,
        string resolution,
        Dictionary<string, object?> locSeeds,
        Dictionary<string, object?> charSeeds,
        string? styleLock)
    {
        var beats = GetList(scene, "story_beats").OfType<Dictionary<string, object?>>().ToList();
        var lids = GetList(scene, "location_ids").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
        var primary = CoerceString(scene.TryGetValue("primary_location_id", out var pl) ? pl : null)
                      ?? (lids.Count > 0 ? lids[0] : null);
        var cast = UnionCharactersOnScreen(scene);

        if (beats.Count == 0)
        {
            return BaseSceneShell(scene, lids, primary, cast, GrokSceneMin, new List<object?>(), new List<object?>());
        }

        var target = ToInt(scene.TryGetValue("duration_target_seconds", out var dt) ? dt : beats.Count * GrokDefault);
        var durs = AllocateDurations(beats, target);
        var total = durs.Sum();

        var sceneWork = new Dictionary<string, object?>(scene)
        {
            ["characters_on_screen"] = cast.Cast<object?>().ToList(),
        };
        if (!string.IsNullOrWhiteSpace(styleLock))
            sceneWork["render_style_lock"] = styleLock;

        var wardrobe = InitWardrobeState(cast, charSeeds, scene);
        var clips = new List<object?>();
        var beatMap = new List<object?>();
        var t = 0;
        string? prevLid = null;
        Dictionary<string, object?>? prevBeat = null;

        for (var i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            var dur = durs[i];
            var lid = CoerceString(beat.TryGetValue("location_id", out var bl) ? bl : null)
                      ?? primary ?? (lids.Count > 0 ? lids[0] : null);
            var cont = ForceNone(beat, i, prevBeat, prevLid, lid) ? "none" : "extend_previous";
            if (string.Equals(CoerceString(beat.TryGetValue("action_class", out var ac) ? ac : null),
                    "big_action", StringComparison.OrdinalIgnoreCase))
                cont = "none";
            if (prevLid is not null && lid is not null && prevLid != lid)
                cont = "none";

            var clipCast = ClipCastTokens(sceneWork, beat);
            var ps = CoerceString(beat.TryGetValue("primary_subject", out var psv) ? psv : null) ?? "";
            if (ps.StartsWith("Character_", StringComparison.Ordinal) && !clipCast.Contains(ps))
                clipCast.Insert(0, ps);

            UpdateWardrobeFromBeat(wardrobe, beat, clipCast);

            var vp = BuildVisualPrompt(beat, sceneWork, resolution, locSeeds, charSeeds, wardrobe, i);
            if (cont == "extend_previous" &&
                !vp.Contains("continue from previous", StringComparison.OrdinalIgnoreCase))
            {
                var contCue =
                    "CONTINUE from previous last frame — same place and character positions; " +
                    "do not reset to the door or restart the walk; pick up exactly where the last clip ended";
                var m = Regex.Match(vp, @"\s*/\s*\d+p.*24fps\s*$", RegexOptions.IgnoreCase);
                var body = m.Success ? vp[..m.Index].Trim() : vp;
                var suffix = m.Success ? m.Value : $" / {resolution}, 24fps";
                var candidate = $"{body.TrimEnd('.', ' ')}. {contCue}{suffix}";
                if (candidate.Length <= PromptSoft + 40)
                    vp = candidate;
            }

            var neg = GlobalNegative;
            var extra = NegExtras(beat);
            if (!string.IsNullOrWhiteSpace(extra))
                neg = $"{neg}, {extra}";
            var wardNeg = WardrobeNegativeExtras(wardrobe, clipCast);
            if (!string.IsNullOrWhiteSpace(wardNeg))
                neg = $"{neg}, {wardNeg}";

            clips.Add(new Dictionary<string, object?>
            {
                ["clip_number"] = i + 1,
                ["timestamp"] = FormatTs(t, t + dur),
                ["veo_continuation_source"] = cont,
                ["location_id"] = lid,
                ["visual_prompt"] = vp,
                ["negative_prompt"] = neg,
                ["audio_payload"] = BuildAudioPayload(beat),
                ["stage1_beat_id"] = beat.TryGetValue("beat_id", out var bi) ? bi : $"b{i + 1}",
                ["primary_subject"] = beat.TryGetValue("primary_subject", out var psub) ? psub : null,
                ["duration_seconds"] = dur,
            });
            beatMap.Add(CoerceString(beat.TryGetValue("beat_id", out var bid) ? bid : null) ?? $"b{i + 1}");
            t += dur;
            prevLid = lid;
            prevBeat = beat;
        }

        return BaseSceneShell(scene, lids, primary, cast, total, clips, beatMap);
    }

    private static Dictionary<string, object?> BaseSceneShell(
        Dictionary<string, object?> scene,
        List<string> lids,
        string? primary,
        List<string> cast,
        int total,
        List<object?> clips,
        List<object?> beatMap) => new()
    {
        ["scene_number"] = scene.TryGetValue("scene_number", out var sn) ? sn : null,
        ["setting"] = scene.TryGetValue("setting", out var set) ? set : null,
        ["location_ids"] = lids.Cast<object?>().ToList(),
        ["primary_location_id"] = primary,
        ["characters_on_screen"] = cast.Cast<object?>().ToList(),
        ["scene_filename"] = scene.TryGetValue("scene_filename", out var sf) ? sf : null,
        ["transition_type"] = CoerceString(scene.TryGetValue("transition_type", out var tt) ? tt : null) ?? "cut",
        ["lighting_continuity_token"] =
            CoerceString(scene.TryGetValue("lighting_continuity_token", out var lc) ? lc : null) ?? "",
        ["total_estimated_duration_seconds"] = total,
        ["music_bed"] = MusicBed(scene, total),
        ["veo_clips"] = clips,
        ["stage1_scene_number"] = scene.TryGetValue("scene_number", out var s1) ? s1 : null,
        ["stage1_beat_map"] = beatMap,
        ["video_provider_profile"] = "grok",
        ["spoiler_constraints"] = scene.TryGetValue("spoiler_constraints", out var sp) ? sp : new List<object?>(),
        ["source_book_refs"] = scene.TryGetValue("source_book_refs", out var sbr) ? sbr : new List<object?>(),
    };

    private static string BuildVisualPrompt(
        Dictionary<string, object?> beat,
        Dictionary<string, object?> scene,
        string resolution,
        Dictionary<string, object?> locSeeds,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, List<string>> wardrobe,
        int clipIndex)
    {
        var ve = CoerceString(beat.TryGetValue("visual_event", out var vev) ? vev : null) ?? "";
        ve = Regex.Replace(ve, @"\s*/\s*\d+p.*$", "", RegexOptions.IgnoreCase).Trim();
        var cast = ClipCastTokens(scene, beat);
        var primary = CoerceString(beat.TryGetValue("primary_subject", out var ps) ? ps : null)
                      ?? (cast.Count > 0 ? cast[0] : "");

        var place = LocationLockPhrase(scene, beat, locSeeds);
        var style = RenderStyleLock(scene);
        if (string.IsNullOrWhiteSpace(style) &&
            cast.Any(t => t.Contains("mom", StringComparison.OrdinalIgnoreCase) ||
                          t.Contains("dad", StringComparison.OrdinalIgnoreCase) ||
                          t.Contains("human", StringComparison.OrdinalIgnoreCase)))
        {
            style =
                "STYLE LOCK: stylized 3D animated children's picture-book CG " +
                "(same render family as animal hero) -- not photoreal, not live-action";
        }

        if (!string.IsNullOrEmpty(primary) && !ve.Contains(primary, StringComparison.Ordinal))
            ve = $"{primary} {ve}".Trim();

        var others = cast.Where(t => t != primary && !ve.Contains(t, StringComparison.Ordinal)).Take(3).ToList();
        var othersBit = others.Count > 0 ? $"also on screen: {string.Join(", ", others)}" : "";

        var block = CoerceString(beat.TryGetValue("blocking_notes", out var bn) ? bn : null) ?? "";
        if (!string.IsNullOrWhiteSpace(block) &&
            !ve.Contains(block, StringComparison.OrdinalIgnoreCase))
            ve = $"{ve}. {block}".Trim();

        var ac = (CoerceString(beat.TryGetValue("action_class", out var acv) ? acv : null) ?? "").ToLowerInvariant();
        if (ac == "big_action" &&
            !ve.Contains("continuous", StringComparison.OrdinalIgnoreCase))
            ve = $"{ve}. ONE continuous take no cut; unbroken cause-to-effect motion";

        var speech = SpeechClause(beat, cast);
        var mustNot = GetList(beat, "must_not").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Take(3).ToList();
        var mustBit = mustNot.Count > 0 ? $"must not: {string.Join("; ", mustNot)}" : "";
        var ward = WardrobeContinuityClause(wardrobe, cast, clipIndex, primary);
        var idCue = IdentityCues(cast, charSeeds);

        var slots = new List<PromptSlot>
        {
            new("style", style, 0, 2, 50),
            new("place", !string.IsNullOrEmpty(place) && !ve.Contains(place, StringComparison.OrdinalIgnoreCase) ? place : "", 2, 4, 30),
            new("others", othersBit, 3, 5, 20),
            new("action", ve, 4, 0, 80),
            new("speech", speech, 5, 6, 50),
            new("must_not", mustBit, 6, 5, 20),
            new("wardrobe", ward, 7, 1, 40),
            new("identity", idCue, 8, 9, 0),
        };
        return PackVisualPromptSlots(slots, PromptSoft, resolution);
    }

    private sealed record PromptSlot(string Key, string Text, int Order, int DropRank, int MinKeep);

    private static string PackVisualPromptSlots(List<PromptSlot> slots, int budget, string resolution)
    {
        var suffix = $" / {resolution}, 24fps";
        var room = Math.Max(80, budget - suffix.Length);
        var active = slots.Where(s => !string.IsNullOrWhiteSpace(s.Text)).Select(s =>
            new PromptSlot(s.Key, Regex.Replace(s.Text.Trim(), @"\s+", " ").TrimEnd('.', ' '), s.Order, s.DropRank, s.MinKeep)
        ).ToList();

        if (active.Count == 0)
            return $"Scene action.{suffix}";

        string Body(List<PromptSlot> parts) =>
            string.Join(". ", parts.OrderBy(p => p.Order).Select(p => p.Text).Where(t => t.Length > 0));

        while (active.Count > 0 && Body(active).Length > room)
        {
            var candidates = active.Where(s => s.DropRank > 0).ToList();
            if (candidates.Count == 0) break;
            var victim = candidates.OrderByDescending(s => s.DropRank).First();
            active.Remove(victim);
        }

        var body = Body(active);
        if (body.Length > room)
        {
            for (var i = 0; i < active.Count; i++)
            {
                var s = active[i];
                if (s.Key is "action" or "wardrobe") continue;
                if (s.Text.Length > s.MinKeep && s.MinKeep > 0)
                {
                    var cut = s.Text[..Math.Max(1, s.MinKeep - 1)];
                    var sp = cut.LastIndexOf(' ');
                    active[i] = s with { Text = (sp > 20 ? cut[..sp] : cut) + "…" };
                    body = Body(active);
                    if (body.Length <= room) break;
                }
            }
        }
        if (body.Length > room)
        {
            body = body[..Math.Max(40, room - 1)];
            var sp = body.LastIndexOf(' ');
            if (sp > 20) body = body[..sp];
            body += "…";
        }
        return body + suffix;
    }

    private static List<int> AllocateDurations(List<Dictionary<string, object?>> beats, int target)
    {
        var n = beats.Count;
        if (n == 0) return new List<int>();
        target = Math.Max(GrokSceneMin, target);
        var weights = beats.Select(BeatDurationWeight).ToList();
        var wsum = weights.Sum();
        if (wsum <= 0) wsum = n;
        var raw = weights.Select(w => w / wsum * target).ToList();
        var durs = raw.Select(r => (int)Math.Round(Math.Clamp(r, GrokMinClip, GrokMaxClip))).ToList();
        // Fix sum
        var diff = target - durs.Sum();
        var guard = 0;
        while (diff != 0 && guard++ < 100)
        {
            if (diff > 0)
            {
                var i = durs.Select((d, idx) => (d, idx)).OrderBy(x => x.d).First().idx;
                if (durs[i] < GrokAbsMax) { durs[i]++; diff--; }
                else break;
            }
            else
            {
                var i = durs.Select((d, idx) => (d, idx)).OrderByDescending(x => x.d).First().idx;
                if (durs[i] > GrokMinClip) { durs[i]--; diff++; }
                else break;
            }
        }
        return durs;
    }

    private static double BeatDurationWeight(Dictionary<string, object?> beat)
    {
        double w = 1.0;
        if (beat.TryGetValue("time_weight", out var tw))
        {
            try { w = Convert.ToDouble(tw); } catch { w = 1.0; }
        }
        var ac = (CoerceString(beat.TryGetValue("action_class", out var a) ? a : null) ?? "").ToLowerInvariant();
        w *= ac switch
        {
            "big_action" => 1.4,
            "establishing" => 1.2,
            "dialogue" => 1.15,
            "hold" => 0.85,
            _ => 1.0,
        };
        var dlg = CoerceString(beat.TryGetValue("dialogue", out var d) ? d : null) ?? "";
        if (dlg.Length > 80) w *= 1.15;
        return Math.Max(0.25, w);
    }

    private static bool ForceNone(
        Dictionary<string, object?> beat,
        int clipIndex,
        Dictionary<string, object?>? prevBeat,
        string? prevLocationId,
        string? locationId)
    {
        if (clipIndex == 0) return true;
        var ac = (CoerceString(beat.TryGetValue("action_class", out var a) ? a : null) ?? "").ToLowerInvariant();
        var cont = (CoerceString(beat.TryGetValue("continuity", out var c) ? c : null) ?? "").ToLowerInvariant();
        if (ac is "big_action" or "establishing" or "hard_cut" or "flashback_enter" or "flashback_exit" or "montage")
            return true;
        if (cont is "new_setup" or "return_to_present" or "parallel")
            return true;
        if (prevLocationId is not null && locationId is not null && prevLocationId != locationId)
            return true;
        if (IsVoBeat(beat) && prevBeat is not null && IsOnCameraSpeech(prevBeat))
            return true;
        if (IsVoBeat(beat))
            return cont != "continuous_from_previous_beat";
        var ve = (CoerceString(beat.TryGetValue("visual_event", out var vev) ? vev : null) ?? "").ToLowerInvariant();
        if (Regex.IsMatch(ve,
                @"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket|wide shot|establishing|flashback|back to present|cut to)\b"))
            return true;
        return false;
    }

    private static bool IsVoBeat(Dictionary<string, object?> beat)
    {
        var (delivery, speaker) = BeatAudio(beat);
        return delivery is "voiceover_internal" or "internal" or "vo_internal" or "thought"
                   or "thinking" or "narration" or "vo"
               || speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOnCameraSpeech(Dictionary<string, object?> beat)
    {
        var (delivery, speaker) = BeatAudio(beat);
        return delivery == "spoken_on_camera" && !speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Delivery, string Speaker) BeatAudio(Dictionary<string, object?> beat)
    {
        var nested = beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;
        var delivery = (CoerceString(nested?.TryGetValue("delivery", out var d) == true ? d
            : beat.TryGetValue("delivery", out var d2) ? d2 : null) ?? "").ToLowerInvariant();
        var speaker = (CoerceString(nested?.TryGetValue("speaker", out var s) == true ? s
            : beat.TryGetValue("speaker", out var s2) ? s2 : null) ?? "").ToLowerInvariant();
        return (delivery, speaker);
    }

    private static Dictionary<string, object?> BuildAudioPayload(Dictionary<string, object?> beat)
    {
        var nested = beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;
        var delivery = CoerceString(nested?.TryGetValue("delivery", out var d) == true ? d
            : beat.TryGetValue("delivery", out var d2) ? d2 : null) ?? "none";
        var speaker = CoerceString(nested?.TryGetValue("speaker", out var s) == true ? s
            : beat.TryGetValue("speaker", out var s2) ? s2 : null) ?? "";
        var dialogue = CoerceString(nested?.TryGetValue("dialogue", out var dlg) == true ? dlg
            : beat.TryGetValue("dialogue", out var dlg2) ? dlg2 : null) ?? "";
        var sfx = CoerceString(nested?.TryGetValue("sfx", out var sx) == true ? sx
            : beat.TryGetValue("ambient_or_sfx", out var sx2) ? sx2 : null) ?? "";
        return new Dictionary<string, object?>
        {
            ["delivery"] = delivery.Trim().ToLowerInvariant(),
            ["speaker"] = speaker,
            ["dialogue"] = dialogue,
            ["sfx"] = sfx,
            ["ambient"] = "",
        };
    }

    private static string SpeechClause(Dictionary<string, object?> beat, List<string> cast)
    {
        var ap = BuildAudioPayload(beat);
        var delivery = (ap["delivery"] as string ?? "none").ToLowerInvariant();
        var speaker = ap["speaker"] as string ?? "";
        var dialogue = ap["dialogue"] as string ?? "";
        if (string.IsNullOrWhiteSpace(dialogue) || delivery is "none" or "")
            return "";
        var quote = dialogue.Length > 90 ? dialogue[..87] + "…" : dialogue;
        if (delivery is "spoken_on_camera")
            return $"{speaker} ON CAMERA lip-syncs \"{quote}\"";
        return $"OFF-CAMERA VOICEOVER {speaker} says \"{quote}\"";
    }

    private static List<string> ClipCastTokens(Dictionary<string, object?> scene, Dictionary<string, object?> beat)
    {
        var found = new List<string>();
        void AddFrom(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Regex.Matches(text, @"Character_[A-Za-z0-9_]+"))
            {
                if (!found.Contains(m.Value))
                    found.Add(m.Value);
            }
        }
        AddFrom(CoerceString(beat.TryGetValue("visual_event", out var ve) ? ve : null));
        AddFrom(CoerceString(beat.TryGetValue("primary_subject", out var ps) ? ps : null));
        AddFrom(CoerceString(beat.TryGetValue("speaker", out var sp) ? sp : null));
        AddFrom(CoerceString(beat.TryGetValue("blocking_notes", out var bn) ? bn : null));
        if (found.Count == 0)
            found.AddRange(UnionCharactersOnScreen(scene));
        return found;
    }

    private static List<string> UnionCharactersOnScreen(Dictionary<string, object?> scene)
    {
        var set = new List<string>();
        void Add(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            if (!t.StartsWith("Character_", StringComparison.Ordinal)) return;
            if (!set.Contains(t)) set.Add(t);
        }
        foreach (var x in GetList(scene, "characters_on_screen"))
            Add(x?.ToString());
        foreach (var b in GetList(scene, "story_beats").OfType<Dictionary<string, object?>>())
        {
            Add(CoerceString(b.TryGetValue("primary_subject", out var ps) ? ps : null));
            Add(CoerceString(b.TryGetValue("speaker", out var sp) ? sp : null));
            var ve = CoerceString(b.TryGetValue("visual_event", out var vev) ? vev : null) ?? "";
            foreach (Match m in Regex.Matches(ve, @"Character_[A-Za-z0-9_]+"))
                Add(m.Value);
        }
        return set;
    }

    private static string LocationLockPhrase(
        Dictionary<string, object?> scene,
        Dictionary<string, object?> beat,
        Dictionary<string, object?> locSeeds)
    {
        var lid = CoerceString(beat.TryGetValue("location_id", out var bl) ? bl : null)
                  ?? CoerceString(scene.TryGetValue("primary_location_id", out var pl) ? pl : null);
        if (string.IsNullOrEmpty(lid)) return "";
        if (locSeeds.TryGetValue(lid, out var seedObj) && seedObj is Dictionary<string, object?> seed)
        {
            var lockTxt = CoerceString(seed.TryGetValue("visual_lock", out var vl) ? vl : null)
                          ?? CoerceString(seed.TryGetValue("description", out var d) ? d : null)
                          ?? lid;
            return lockTxt.Length > 80 ? lockTxt[..77] + "…" : lockTxt;
        }
        return lid;
    }

    private static string RenderStyleLock(Dictionary<string, object?> scene) =>
        CoerceString(scene.TryGetValue("render_style_lock", out var r) ? r : null) ?? "";

    private static string IdentityCues(List<string> cast, Dictionary<string, object?> charSeeds)
    {
        var bits = new List<string>();
        foreach (var key in cast.Take(3))
        {
            if (!charSeeds.TryGetValue(key, out var s) || s is not Dictionary<string, object?> seed)
                continue;
            var vl = CoerceString(seed.TryGetValue("visual_lock", out var v) ? v : null)
                     ?? CoerceString(seed.TryGetValue("description", out var d) ? d : null);
            if (string.IsNullOrWhiteSpace(vl)) continue;
            var shortVl = vl.Length > 40 ? vl[..37] + "…" : vl;
            bits.Add($"{key}: {shortVl}");
        }
        if (bits.Count == 0) return "";
        var joined = string.Join("; ", bits);
        return joined.Length > 56 ? joined[..53] + "…" : joined;
    }

    private static Dictionary<string, List<string>> InitWardrobeState(
        List<string> cast,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, object?> scene)
    {
        var state = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in cast)
        {
            var items = new List<string>();
            if (charSeeds.TryGetValue(key, out var s) && s is Dictionary<string, object?> seed)
                items.AddRange(Stage1Normalizer.CoerceStringList(
                    seed.TryGetValue("wardrobe_always", out var wa) ? wa : null));
            if (scene.TryGetValue("wardrobe_by_character", out var wbc) &&
                wbc is Dictionary<string, object?> map &&
                map.TryGetValue(key, out var itemsObj))
                items.AddRange(Stage1Normalizer.CoerceStringList(itemsObj));
            state[key] = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return state;
    }

    private static void UpdateWardrobeFromBeat(
        Dictionary<string, List<string>> state,
        Dictionary<string, object?> beat,
        List<string> cast)
    {
        var putOn = Stage1Normalizer.CoerceStringList(
            beat.TryGetValue("wardrobe_put_on", out var po) ? po : null, 8);
        var remove = Stage1Normalizer.CoerceStringList(
            beat.TryGetValue("wardrobe_remove", out var rm) ? rm : null, 8);
        var subject = CoerceString(beat.TryGetValue("primary_subject", out var ps) ? ps : null)
                      ?? (cast.Count > 0 ? cast[0] : null);
        if (subject is null) return;
        if (!state.TryGetValue(subject, out var list))
        {
            list = new List<string>();
            state[subject] = list;
        }
        foreach (var r in remove)
            list.RemoveAll(x => x.Contains(r, StringComparison.OrdinalIgnoreCase) ||
                                r.Contains(x, StringComparison.OrdinalIgnoreCase));
        foreach (var p in putOn)
        {
            if (!list.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)))
                list.Add(p);
        }
    }

    private static string WardrobeContinuityClause(
        Dictionary<string, List<string>> state,
        List<string> cast,
        int clipIndex,
        string primary)
    {
        var bits = new List<string>();
        foreach (var key in cast.Take(4))
        {
            if (!state.TryGetValue(key, out var items) || items.Count == 0) continue;
            var shown = items.Take(clipIndex == 0 ? 3 : 2).ToList();
            if (shown.Count == 0) continue;
            bits.Add($"{key} still wears {string.Join(", ", shown)}");
        }
        if (bits.Count == 0) return "";
        var s = string.Join("; ", bits);
        return s.Length > 120 ? s[..117] + "…" : s;
    }

    private static string WardrobeNegativeExtras(
        Dictionary<string, List<string>> state,
        List<string> cast)
    {
        // Soft negatives: avoid inventing extra props when wardrobe is known
        if (cast.Count == 0) return "";
        var hasWardrobe = cast.Any(c => state.TryGetValue(c, out var i) && i.Count > 0);
        return hasWardrobe ? "no extra unmentioned hats or jackets" : "";
    }

    private static string NegExtras(Dictionary<string, object?> beat)
    {
        var must = GetList(beat, "must_not").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Take(4);
        return string.Join(", ", must);
    }

    private static Dictionary<string, object?> MusicBed(Dictionary<string, object?> scene, int total)
    {
        var mi = scene.TryGetValue("music_intent", out var m) && m is Dictionary<string, object?> md
            ? md : new Dictionary<string, object?>();
        return new Dictionary<string, object?>
        {
            ["style_description"] =
                CoerceString(mi.TryGetValue("style_description", out var sd) ? sd : null)
                ?? "cinematic underscore",
            ["duration_seconds"] = total,
        };
    }

    private static void NormalizeCharPlaceholders(Dictionary<string, object?> charSeeds)
    {
        foreach (var (_, val) in charSeeds)
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var ph = (CoerceString(seed.TryGetValue("reference_image_placeholder", out var p) ? p : null) ?? "")
                .Replace('\\', '/');
            if (ph.Contains('/') || ph.StartsWith("assets", StringComparison.OrdinalIgnoreCase))
                seed["reference_image_placeholder"] = Path.GetFileName(ph);
        }
    }

    private static string Stage1Fingerprint(Dictionary<string, object?> stage1)
    {
        var raw = JsonSerializer.Serialize(new
        {
            scenes = GetScenes(stage1).Select(s => new
            {
                n = s.TryGetValue("scene_number", out var sn) ? sn : null,
                b = GetList(s, "story_beats").Count,
                d = s.TryGetValue("duration_target_seconds", out var d) ? d : null,
            }),
            chars = GetDict(GetDict(stage1, "global_production_variables"), "character_seed_tokens").Keys.OrderBy(k => k),
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static HashSet<int>? ParseSceneRange(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) ||
            string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase))
            return null;
        var set = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var ends = part.Split('-', 2);
                if (int.TryParse(ends[0], out var a) && int.TryParse(ends[1], out var b))
                {
                    for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                        set.Add(i);
                }
            }
            else if (int.TryParse(part, out var n))
                set.Add(n);
        }
        return set.Count == 0 ? null : set;
    }

    private static string FormatTs(int start, int end)
    {
        static string Fmt(int s) => $"{s / 60:D2}:{s % 60:D2}";
        return $"{Fmt(start)}-{Fmt(end)}";
    }

    private static List<Dictionary<string, object?>> GetScenes(Dictionary<string, object?> d) =>
        GetList(d, "scenes").OfType<Dictionary<string, object?>>().ToList();

    private static Dictionary<string, object?> GetDict(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is Dictionary<string, object?> x ? x : new();

    private static List<object?> GetList(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object?> list ? list : new();

    private static int ToInt(object? v) => v switch
    {
        null => 0, int i => i, long l => (int)l, double d => (int)d,
        string s when int.TryParse(s, out var n) => n, _ => 0,
    };

    private static string? CoerceString(object? v) => v switch
    {
        null => null, string s => s, _ => v.ToString(),
    };
}

public sealed class Stage2PlanResult
{
    public bool Ok { get; set; }
    public string OutPath { get; set; } = "";
    public int SceneCount { get; set; }
    public int ClipCount { get; set; }
    public int DurationSeconds { get; set; }
}
