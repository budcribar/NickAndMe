using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Approved Fountain screenplay → Stage 2 clip blueprint (deterministic, no API).
/// Reads <c>source/screenplay.fountain</c> directly (in-memory beat model).
/// Covers plan_scene, visual prompt packing, wardrobe continuity, duration allocation.
/// </summary>
public sealed class Stage2PlannerService
{
    /// <summary>
    /// Provider-agnostic global negatives — applied at gen time by
    /// <see cref="ClipVideoPromptBuilder"/>, not baked into every blueprint row.
    /// </summary>
    public const string GlobalNegativeDefault =
        "no legible text, no watermarks, no logos, no extra limbs, " +
        "blur/obscure environmental signage or screens, no name tags, no name badges, " +
        "no embroidered names, no lower thirds, no personal names on clothing or props";

    // Duration floors/caps live in ClipDurationEstimator (dialogue-aware, cost-sensitive)
    private const int GrokMinClip = ClipDurationEstimator.MinSeconds;
    private const int GrokMaxClip = ClipDurationEstimator.MaxSeconds;
    private const int GrokAbsMax = ClipDurationEstimator.AbsMaxSeconds;
    private const int GrokDefault = 6;
    private const int GrokSceneMin = 6;
    // No design-time length budget — send full visual prompts.
    // If the video API rejects for length, GrokVideoClient shortens and retries.

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

    public async Task<Stage2PlanResult> PlanAsync(
        string projectId,
        string resolution = "720p",
        string scenes = "all",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);

        // Fountain is the only screenplay source of truth.
        ScreenplayService.EnsureCanonicalDraft(_projects, projectId);
        var fountainPath = ScreenplayService.GetDraftPath(_projects, projectId);
        if (!File.Exists(fountainPath))
            throw new InvalidOperationException(
                "No screenplay draft. Create and approve a Fountain screenplay first.");

        var screenplay = ScreenplayService.Get(_projects, projectId);
        if (!screenplay.Status.Signed && screenplay.Status.DraftExists)
            throw new InvalidOperationException(
                "Approve the screenplay before building a shot plan (draft has unapproved changes).");

        onProgress?.Invoke($"Loading screenplay: {Path.GetFileName(fountainPath)}");
        var stage1 = ScreenplayService.BuildModelFromFountainText(screenplay.Text);
        var sourceLabel = Path.GetFileName(fountainPath);

        // Overlay plate/voice edits from cast_seeds.json when present
        MergeCastSeedsOverlay(_projects, projectId, stage1);

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

        if (scenesIn.Count == 0)
            throw new InvalidOperationException("Screenplay has no scenes to plan.");

        onProgress?.Invoke($"Planning {scenesIn.Count} scene(s) @ {resolution}…");
        var styleLock = CoerceString(gpv.TryGetValue("render_style_lock", out var rsl) ? rsl : null);
        var planned = new List<Dictionary<string, object?>>();
        foreach (var s in scenesIn)
        {
            ct.ThrowIfCancellationRequested();
            var sn = ToInt(s.TryGetValue("scene_number", out var n) ? n : 0);
            onProgress?.Invoke($"  Scene {sn}…");
            var plannedScene = PlanScene(s, resolution, locSeeds, charSeeds, styleLock);
            // Skip transition-only phantoms (e.g. FADE IN before first heading)
            if (plannedScene is null)
            {
                onProgress?.Invoke($"  Scene {sn}: skipped (no filmable content)");
                continue;
            }
            planned.Add(plannedScene);
        }

        if (planned.Count == 0)
            throw new InvalidOperationException("Screenplay has no filmable scenes to plan.");

        var outPath = await _projects.FindBlueprintPathAsync(projectId, ct).ConfigureAwait(false)
            ?? Path.Combine(projectDir, "blueprint.clips.grok.json");
        if (File.Exists(outPath))
        {
            var bak = outPath + $".bak_pre_stage2_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(outPath, bak, overwrite: true);
            onProgress?.Invoke($"Backed up blueprint → {Path.GetFileName(bak)}");
        }

        Dictionary<string, object?> plan;
        if (want is not null && File.Exists(outPath))
        {
            try
            {
                var existingText = await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false);
                var existing = GrokChatClient.ParseJsonObject(existingText);
                plan = MergePlannedScenes(existing, planned, stage1, gpv, sourceLabel, resolution, scenes);
                onProgress?.Invoke("Merged planned scenes into existing blueprint");
            }
            catch
            {
                plan = BuildFullPlan(stage1, gpv, planned, sourceLabel, resolution, scenes);
            }
        }
        else
        {
            plan = BuildFullPlan(stage1, gpv, planned, sourceLabel, resolution, scenes);
        }

        await File.WriteAllTextAsync(
            outPath,
            JsonSerializer.Serialize(plan, JsonWrite) + "\n",
            ct).ConfigureAwait(false);
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
        string sourceLabel,
        string resolution,
        string scenesFilter) => new()
    {
        ["schema_version"] = "stage2.v1",
        ["movie_title"] = stage1.TryGetValue("movie_title", out var mt) ? mt : null,
        ["source_book_title"] = stage1.TryGetValue("source_book_title", out var sbt) ? sbt : null,
        ["video_provider_profile"] = "grok",
        ["global_production_variables"] = gpv,
        ["scenes"] = planned.Cast<object?>().ToList(),
        ["stage2_meta"] = MakeMeta(stage1, planned, sourceLabel, resolution, scenesFilter),
    };

    private static Dictionary<string, object?> MergePlannedScenes(
        Dictionary<string, object?> existing,
        List<Dictionary<string, object?>> planned,
        Dictionary<string, object?> stage1,
        Dictionary<string, object?> gpv,
        string sourceLabel,
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
        existing["stage2_meta"] = MakeMeta(stage1, all, sourceLabel, resolution, scenesFilter);
        return existing;
    }

    private static Dictionary<string, object?> MakeMeta(
        Dictionary<string, object?> stage1,
        List<Dictionary<string, object?>> planned,
        string sourceLabel,
        string resolution,
        string scenesFilter) => new()
    {
        ["source_screenplay"] = sourceLabel,
        ["source_stage1"] = sourceLabel,
        ["resolution"] = resolution,
        ["scene_filter"] = scenesFilter,
        ["planner"] = "Stage2PlannerService (C# Fountain)",
        ["prompt_truncates"] = false,
        ["prompt_length_policy"] = "full_then_api_retry_shorten",
        ["screenplay_fingerprint"] = Stage1Fingerprint(stage1),
        ["stage1_fingerprint"] = Stage1Fingerprint(stage1),
        ["planned_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        ["total_duration_seconds"] = planned.Sum(s =>
            ToInt(s.TryGetValue("total_estimated_duration_seconds", out var d) ? d : 0)),
        ["total_clips"] = planned.Sum(s => GetList(s, "veo_clips").Count),
    };

    /// <summary>
    /// Overlay design_reference_images / voice fields from source/cast_seeds.json onto the
    /// in-memory model derived from Fountain.
    /// </summary>
    private static void MergeCastSeedsOverlay(
        ProjectStore projects,
        string projectId,
        Dictionary<string, object?> stage1)
    {
        var path = ScreenplayService.GetCastSeedsPath(projects, projectId);
        if (!File.Exists(path))
            return;
        try
        {
            var overlay = GrokChatClient.ParseJsonObject(File.ReadAllText(path));
            // Shapes: { character_seed_tokens } or { global_production_variables.character_seed_tokens }
            var overlaySeeds = GetDict(overlay, "character_seed_tokens");
            if (overlaySeeds.Count == 0)
                overlaySeeds = GetDict(GetDict(overlay, "global_production_variables"), "character_seed_tokens");
            if (overlaySeeds.Count == 0)
                return;

            var gpv = GetDict(stage1, "global_production_variables");
            var seeds = GetDict(gpv, "character_seed_tokens");
            foreach (var (key, val) in overlaySeeds)
            {
                if (val is not Dictionary<string, object?> ov)
                    continue;
                if (!seeds.TryGetValue(key, out var existing) || existing is not Dictionary<string, object?> cur)
                {
                    seeds[key] = ov;
                    continue;
                }
                foreach (var (fk, fv) in ov)
                    cur[fk] = fv;
                seeds[key] = cur;
            }
            gpv["character_seed_tokens"] = seeds;
            stage1["global_production_variables"] = gpv;
        }
        catch
        {
            /* non-fatal */
        }
    }

    /// <summary>
    /// Build one scene’s clip plan. Returns null when the scene has nothing filmable
    /// (transition-only / phantom unspecified), so callers can omit it.
    /// </summary>
    private static Dictionary<string, object?>? PlanScene(
        Dictionary<string, object?> scene,
        string resolution,
        Dictionary<string, object?> locSeeds,
        Dictionary<string, object?> charSeeds,
        string? styleLock)
    {
        var beats = GetList(scene, "story_beats").OfType<Dictionary<string, object?>>()
            .Where(b => !IsNoopTransitionBeat(b))
            .ToList();
        // Idempotent: monologues already split at fountain import stay; legacy long cues expand here
        beats = ClipDurationEstimator.ExpandLongDialogueBeats(beats);
        var lids = GetList(scene, "location_ids").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
        var primary = CoerceString(scene.TryGetValue("primary_location_id", out var pl) ? pl : null)
                      ?? (lids.Count > 0 ? lids[0] : null);
        var cast = UnionCharactersOnScreen(scene);

        // Entire scene was only FADE IN / CUT TO — omit (no empty clip)
        var setting = CoerceString(scene.TryGetValue("setting", out var set) ? set : null) ?? "";
        if (beats.Count == 0 &&
            setting.Contains("UNSPECIFIED", StringComparison.OrdinalIgnoreCase))
            return null;

        if (beats.Count == 0)
        {
            return BaseSceneShell(scene, lids, primary, cast, GrokSceneMin, new List<object?>(), new List<object?>());
        }

        // Prefer per-beat dialogue/action estimates over padding every clip to fill a scene budget
        var target = ToInt(scene.TryGetValue("duration_target_seconds", out var dt) ? dt : 0);
        var durs = ClipDurationEstimator.AllocateForBeats(
            beats,
            sceneTargetSeconds: target > 0 ? target : null);
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

            var clipCast = ClipCastTokens(sceneWork, beat, charSeeds);
            var ps = CoerceString(beat.TryGetValue("primary_subject", out var psv) ? psv : null) ?? "";
            if (ps.StartsWith("Character_", StringComparison.Ordinal) && !clipCast.Contains(ps))
                clipCast.Insert(0, ps);

            UpdateWardrobeFromBeat(wardrobe, beat, clipCast);

            // Continuity + resolution/fps are owned by ClipVideoPromptBuilder at gen time —
            // keep blueprint visual_prompt declarative (action/style only).
            var vp = BuildVisualPrompt(beat, sceneWork, locSeeds, charSeeds, wardrobe, i);

            // Story-specific negatives only; provider global negatives applied at gen time.
            var neg = BuildStoryNegativePrompt(beat, wardrobe, clipCast);

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
                ["characters_on_screen"] = clipCast.Cast<object?>().ToList(),
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
        Dictionary<string, object?> locSeeds,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, List<string>> wardrobe,
        int clipIndex)
    {
        var ve = CoerceString(beat.TryGetValue("visual_event", out var vev) ? vev : null) ?? "";
        // Strip accidental technical suffix from beat text (res/fps owned at gen time)
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

        // Attach subject as readable display name — never "Character_X He steadies…"
        if (!string.IsNullOrEmpty(primary) && !VisualMentionsSubject(ve, primary))
        {
            var display = DisplayNameForKey(primary, charSeeds);
            ve = AttachPrimaryToVisual(ve, primary, display);
        }

        var others = cast.Where(t => t != primary && !ve.Contains(t, StringComparison.Ordinal)).Take(3).ToList();
        var othersBit = others.Count > 0 ? $"also on screen: {string.Join(", ", others)}" : "";
        // CAST COUNT + CHARACTER VARIABLES owned by ClipVideoPromptBuilder at gen time.

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
        // Same wardrobe phrase length for all clips in the scene (consistent continuity language).
        var ward = WardrobeContinuityClause(wardrobe, cast, clipIndex, primary);

        // Join full slots — no length budget, no dropping fields, no ellipsis packing.
        // Identity cues omitted: gen-time CHARACTER VARIABLES + locked refs own identity.
        var parts = new List<(int Order, string Text)>
        {
            (0, style),
            (2, !string.IsNullOrEmpty(place) && !ve.Contains(place, StringComparison.OrdinalIgnoreCase) ? place : ""),
            (3, othersBit),
            (5, ve),
            (6, speech),
            (7, mustBit),
            (8, ward),
        };
        return JoinVisualPromptParts(parts);
    }

    /// <summary>
    /// True if visual text already names the character (full key or bare name like NARRATOR / Old Man).
    /// </summary>
    public static bool VisualMentionsSubject(string visual, string primaryKey)
    {
        if (string.IsNullOrWhiteSpace(visual) || string.IsNullOrWhiteSpace(primaryKey))
            return false;
        if (visual.Contains(primaryKey, StringComparison.OrdinalIgnoreCase))
            return true;
        var bare = primaryKey.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
            ? primaryKey["Character_".Length..]
            : primaryKey;
        if (string.IsNullOrWhiteSpace(bare)) return false;
        // Character_Old_Man → "Old Man", "Old_Man", "OLDMAN"
        var spaced = bare.Replace('_', ' ');
        if (visual.Contains(spaced, StringComparison.OrdinalIgnoreCase))
            return true;
        if (visual.Contains(bare, StringComparison.OrdinalIgnoreCase))
            return true;
        var compact = Regex.Replace(bare, @"[_ ]+", "");
        if (compact.Length >= 3 &&
            Regex.IsMatch(visual, $@"\b{Regex.Escape(compact)}\b", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Join primary subject into action prose as a display name.
    /// Pronoun leads (He/She/They…) become named subjects — never <c>Character_* He …</c>.
    /// </summary>
    public static string AttachPrimaryToVisual(
        string? visualEvent,
        string primaryKey,
        string? displayName = null)
    {
        var ve = (visualEvent ?? "").Trim();
        if (ve.Length == 0)
            return ve;
        if (string.IsNullOrWhiteSpace(primaryKey))
            return ve;
        if (VisualMentionsSubject(ve, primaryKey))
            return ve;

        var name = (displayName ?? "").Trim();
        if (name.Length == 0)
        {
            var bare = primaryKey.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
                ? primaryKey["Character_".Length..]
                : primaryKey;
            name = bare.Replace('_', ' ').Trim();
        }
        if (name.Length == 0)
            return ve;

        // He steadies… / She turns… / They wait…
        var m = Regex.Match(
            ve,
            @"^(He|She|They|Him|Her|Them)\b(\s+)(?<rest>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
            return $"{name} {m.Groups["rest"].Value.Trim()}".Trim();

        // His hands… / Her eyes…
        m = Regex.Match(
            ve,
            @"^(His|Her|Their)\b(\s+)(?<rest>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
            return $"{name}'s {m.Groups["rest"].Value.Trim()}".Trim();

        // Prefer human-readable name in action (CAST COUNT / variables still use Character_*)
        return $"{name} {ve}".Trim();
    }

    private static string DisplayNameForKey(
        string primaryKey,
        Dictionary<string, object?> charSeeds)
    {
        if (charSeeds.TryGetValue(primaryKey, out var seed) &&
            seed is Dictionary<string, object?> d)
        {
            var cn = CoerceString(d.TryGetValue("canonical_given_name", out var c) ? c : null);
            if (!string.IsNullOrWhiteSpace(cn))
                return cn!;
            var vl = CoerceString(d.TryGetValue("voice_label", out var v) ? v : null);
            if (!string.IsNullOrWhiteSpace(vl))
                return vl!.Replace('_', ' ');
        }
        var bare = primaryKey.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
            ? primaryKey["Character_".Length..]
            : primaryKey;
        return bare.Replace('_', ' ').Trim();
    }

    private static string JoinVisualPromptParts(IEnumerable<(int Order, string Text)> parts)
    {
        var sentences = parts
            .OrderBy(p => p.Order)
            .Select(p => NormalizeSentencePart(p.Text))
            .Where(t => t.Length > 0)
            .ToList();
        if (sentences.Count == 0)
            sentences.Add("Scene action");
        var body = string.Join(". ", sentences);
        return body.TrimEnd('.', ' ', '\t');
    }

    private static string NormalizeSentencePart(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Regex.Replace(text.Trim(), @"\s+", " ");
        // Collapse internal double punctuation / trailing junk
        t = Regex.Replace(t, @"\s*\.\s*\.+", ".");
        t = t.TrimEnd('.', ',', ';', ' ', '\t');
        return t;
    }

    /// <summary>Story-specific negatives only (must_not + wardrobe soft ban), deduped.</summary>
    public static string BuildStoryNegativePrompt(
        Dictionary<string, object?> beat,
        Dictionary<string, List<string>> wardrobe,
        List<string> clipCast)
    {
        var items = new List<string>();
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (piece.Length == 0) continue;
                if (items.Any(x => x.Equals(piece, StringComparison.OrdinalIgnoreCase)))
                    continue;
                items.Add(piece);
            }
        }

        Add(NegExtras(beat));
        Add(WardrobeNegativeExtras(wardrobe, clipCast));
        return string.Join(", ", items);
    }

    /// <summary>FADE IN / CUT TO-only beats produce empty visual prompts — never plan clips for them.</summary>
    private static bool IsNoopTransitionBeat(Dictionary<string, object?> beat)
    {
        var dlg = CoerceString(beat.TryGetValue("dialogue", out var d) ? d : null) ?? "";
        if (!string.IsNullOrWhiteSpace(dlg)) return false;
        var ve = CoerceString(beat.TryGetValue("visual_event", out var v) ? v : null) ?? "";
        if (string.IsNullOrWhiteSpace(ve)) return true;
        if (FountainParser.IsStandaloneTransitionLine(ve)) return true;
        return Regex.IsMatch(
            ve.Trim(),
            @"^(FADE\s+IN|FADE\s+OUT|FADE\s+TO\s+BLACK|FADE\s+TO\s+WHITE|CUT\s+TO(\s+BLACK)?|DISSOLVE\s+TO|SMASH\s+CUT\s+TO|BLACK\s+OUT|THE\s+END)[\s\.:]*$",
            RegexOptions.IgnoreCase);
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
        return IsOnCameraDelivery(delivery) &&
               !speaker.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Blueprint may use on_camera or spoken_on_camera for lip-sync dialogue.</summary>
    public static bool IsOnCameraDelivery(string? delivery)
    {
        var d = (delivery ?? "").Trim().ToLowerInvariant();
        return d is "spoken_on_camera" or "on_camera" or "spoken";
    }

    /// <summary>Normalize delivery aliases to canonical tokens for audio_payload.</summary>
    public static string NormalizeDelivery(string? delivery)
    {
        var d = (delivery ?? "none").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(d)) return "none";
        if (d is "on_camera" or "spoken" or "dialogue_on_camera")
            return "spoken_on_camera";
        if (d is "vo" or "voiceover" or "voice_over" or "off_camera" or "offcamera")
            return "voiceover_internal";
        return d;
    }

    private static (string Delivery, string Speaker) BeatAudio(Dictionary<string, object?> beat)
    {
        var nested = beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;
        var delivery = NormalizeDelivery(CoerceString(nested?.TryGetValue("delivery", out var d) == true ? d
            : beat.TryGetValue("delivery", out var d2) ? d2 : null));
        var speaker = (CoerceString(nested?.TryGetValue("speaker", out var s) == true ? s
            : beat.TryGetValue("speaker", out var s2) ? s2 : null) ?? "").ToLowerInvariant();
        return (delivery, speaker);
    }

    private static Dictionary<string, object?> BuildAudioPayload(Dictionary<string, object?> beat)
    {
        // Prefer normalized separate keys (Stage1Normalizer / Fountain importer)
        Stage1Normalizer.NormalizeBeatAudioKeys(beat);

        var nested = beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> ad ? ad : null;
        var delivery = NormalizeDelivery(CoerceString(nested?.TryGetValue("delivery", out var d) == true ? d
            : beat.TryGetValue("delivery", out var d2) ? d2 : null) ?? "none");
        var speaker = CoerceString(nested?.TryGetValue("speaker", out var s) == true ? s
            : beat.TryGetValue("speaker", out var s2) ? s2 : null) ?? "";
        var dialogue = CoerceString(nested?.TryGetValue("dialogue", out var dlg) == true ? dlg
            : beat.TryGetValue("dialogue", out var dlg2) ? dlg2 : null) ?? "";
        var ambient = CoerceString(nested?.TryGetValue("ambient", out var am) == true ? am
            : beat.TryGetValue("ambient", out var am2) ? am2 : null) ?? "";
        var sfx = CoerceString(nested?.TryGetValue("sfx", out var sx) == true ? sx
            : beat.TryGetValue("sfx", out var sx2) ? sx2 : null) ?? "";

        return new Dictionary<string, object?>
        {
            ["delivery"] = delivery,
            ["speaker"] = speaker,
            ["dialogue"] = dialogue,
            ["sfx"] = sfx,
            ["ambient"] = ambient,
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
        // Keep full dialogue in the visual speech cue — audio_payload already has the full line;
        // truncating here produced mid-quote "…" and incomplete lip-sync guidance.
        var quote = dialogue.Trim();
        if (IsOnCameraDelivery(delivery))
            return $"{speaker} ON CAMERA lip-syncs \"{quote}\"";
        return $"OFF-CAMERA VOICEOVER {speaker} says \"{quote}\"";
    }

    private static List<string> ClipCastTokens(
        Dictionary<string, object?> scene,
        Dictionary<string, object?> beat,
        Dictionary<string, object?>? charSeeds = null)
    {
        var found = new List<string>();
        void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!key.StartsWith("Character_", StringComparison.Ordinal)) return;
            if (!found.Contains(key)) found.Add(key);
        }
        void AddFrom(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Regex.Matches(text, @"Character_[A-Za-z0-9_]+"))
                Add(m.Value);
        }
        var veText = CoerceString(beat.TryGetValue("visual_event", out var ve) ? ve : null) ?? "";
        AddFrom(veText);
        AddFrom(CoerceString(beat.TryGetValue("primary_subject", out var ps) ? ps : null));
        AddFrom(CoerceString(beat.TryGetValue("speaker", out var sp) ? sp : null));
        AddFrom(CoerceString(beat.TryGetValue("blocking_notes", out var bn) ? bn : null));

        // Promote free-text names (OLD MAN, three officers) using cast seed keys
        if (charSeeds is { Count: > 0 })
        {
            var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in charSeeds)
            {
                if (v is not Dictionary<string, object?> d) continue;
                profiles[k] = new ClipVideoPromptBuilder.CharacterProfile
                {
                    Key = k,
                    DisplayName = CoerceString(d.TryGetValue("canonical_given_name", out var cn) ? cn : null)
                        ?? CoerceString(d.TryGetValue("voice_label", out var vl) ? vl : null)
                        ?? k.Replace("Character_", "").Replace('_', ' '),
                };
            }
            var prose = string.Join(" ", new[]
            {
                veText,
                CoerceString(beat.TryGetValue("blocking_notes", out var bn2) ? bn2 : null) ?? "",
            });
            foreach (var key in ClipVideoPromptBuilder.InferKeysFromProse(prose, profiles))
                Add(key);
        }

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
            if (IsPlaceholderIdentityText(lockTxt))
                return lid;
            return lockTxt;
        }
        return lid;
    }

    private static string RenderStyleLock(Dictionary<string, object?> scene) =>
        CoerceString(scene.TryGetValue("render_style_lock", out var r) ? r : null) ?? "";

    /// <summary>
    /// True for empty / generic import stubs that must not appear in visual prompts.
    /// </summary>
    public static bool IsPlaceholderIdentityText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.Contains("as described in the screenplay", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("as described in the scr", StringComparison.OrdinalIgnoreCase))
            return true;
        // "Match Name as cast for this production." — old EnsureCharacter visual_lock
        if (Regex.IsMatch(t, @"^Match\s+.+\s+as cast for this production\.?$", RegexOptions.IgnoreCase))
            return true;
        // Bare name-only or "Name (voice only…)" without real appearance detail is OK to skip for visual
        if (t.Contains("voice only", StringComparison.OrdinalIgnoreCase) &&
            t.Length < 80)
            return true;
        return false;
    }

    private static Dictionary<string, List<string>> InitWardrobeState(
        List<string> cast,
        Dictionary<string, object?> charSeeds,
        Dictionary<string, object?> scene)
    {
        var state = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in cast)
        {
            // Order: wardrobe_always (identity) then scene sticky; put_on prepends later
            var items = new List<string>();
            if (charSeeds.TryGetValue(key, out var s) && s is Dictionary<string, object?> seed)
                items.AddRange(Stage1Normalizer.CoerceStringList(
                    seed.TryGetValue("wardrobe_always", out var wa) ? wa : null));
            if (scene.TryGetValue("wardrobe_by_character", out var wbc) &&
                wbc is Dictionary<string, object?> map &&
                map.TryGetValue(key, out var itemsObj))
                items.AddRange(Stage1Normalizer.CoerceStringList(itemsObj));
            state[key] = PrioritizeWardrobeItems(items).ToList();
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
        // Newest put-on first (most important for current continuity)
        for (var i = putOn.Count - 1; i >= 0; i--)
        {
            var p = putOn[i];
            list.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, p);
        }
        state[subject] = PrioritizeWardrobeItems(list).ToList();
    }

    private static string WardrobeContinuityClause(
        Dictionary<string, List<string>> state,
        List<string> cast,
        int clipIndex,
        string primary)
    {
        // Full sticky list, importance-ordered. Primary subject first among cast.
        var bits = new List<string>();
        var orderedCast = cast
            .OrderBy(k => string.Equals(k, primary, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in orderedCast)
        {
            if (!state.TryGetValue(key, out var items) || items.Count == 0) continue;
            var shown = PrioritizeWardrobeItems(items);
            if (shown.Count == 0) continue;
            bits.Add($"{key} still wears {string.Join(", ", shown)}");
        }
        if (bits.Count == 0) return "";
        return string.Join("; ", bits);
    }

    /// <summary>
    /// Order wardrobe phrases by continuity importance (signature / face-adjacent first,
    /// main garments, then accessories). Keeps all items — no artificial cap.
    /// Stable within rank so recent put-on (front of list) stays preferred when ranks tie.
    /// </summary>
    public static IReadOnlyList<string> PrioritizeWardrobeItems(IEnumerable<string>? items)
    {
        if (items is null) return Array.Empty<string>();
        var list = items
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count <= 1) return list;

        return list
            .Select((item, index) => (item, index, rank: WardrobeImportanceRank(item)))
            .OrderBy(t => t.rank)
            .ThenBy(t => t.index) // preserve relative order within rank
            .Select(t => t.item)
            .ToList();
    }

    /// <summary>0 = identity-critical, 1 = main garments, 2 = other.</summary>
    public static int WardrobeImportanceRank(string item)
    {
        var t = (item ?? "").ToLowerInvariant();
        if (t.Length == 0) return 9;
        // Face / silhouette / signature props — highest continuity value
        if (Regex.IsMatch(t,
                @"\b(hat|cap|bonnet|hood|wig|glasses|spectacles|monocle|mask|veil|" +
                @"badge|collar|leash|nightshirt|nightgown|robe|uniform|armor|" +
                @"scarf|cravat|tie|eyepatch)\b"))
            return 0;
        // Core clothing body
        if (Regex.IsMatch(t,
                @"\b(coat|cloak|jacket|dress|gown|suit|shirt|blouse|vest|waistcoat|" +
                @"trousers|pants|skirt|boots|shoes|slippers|pajamas|pyjamas|" +
                @"sweater|jumper|overalls|apron)\b"))
            return 1;
        return 2;
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
