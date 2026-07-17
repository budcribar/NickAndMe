using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly FilmStudioOptions _opts;
    private readonly MediaDurationProbe? _duration;
    private readonly SceneListCache? _sceneListCache;
    private string _activeProjectId = "";

    public ProjectStore(
        IOptions<FilmStudioOptions> opts,
        MediaDurationProbe? duration = null,
        SceneListCache? sceneListCache = null)
    {
        _opts = opts.Value;
        _duration = duration;
        _sceneListCache = sceneListCache;
        var root = ResolveWorkspaceRoot();
        var ws = Path.Combine(root, "projects", "workspace.json");
        if (File.Exists(ws))
        {
            try
            {
                var state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(ws), JsonOpts);
                _activeProjectId = state?.ActiveProject ?? "";
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Drop cached scene lists for a project (call after gen/remux/stage2).</summary>
    public void InvalidateSceneListCache(string? projectId) =>
        _sceneListCache?.Invalidate(projectId);

    public string WorkspaceRoot => ResolveWorkspaceRoot();

    public string ActiveProjectId =>
        string.IsNullOrWhiteSpace(_activeProjectId)
            ? ListProjects().FirstOrDefault()?.Id ?? ""
            : _activeProjectId;

    public IReadOnlyList<ProjectInfo> ListProjects()
    {
        var projectsDir = Path.Combine(WorkspaceRoot, "projects");
        if (!Directory.Exists(projectsDir))
            return Array.Empty<ProjectInfo>();

        var list = new List<ProjectInfo>();
        foreach (var dir in Directory.GetDirectories(projectsDir))
        {
            var id = Path.GetFileName(dir);
            if (string.Equals(id, "workspace.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var metaPath = Path.Combine(dir, "project.json");
            string? title = null;
            string? label = null;
            if (File.Exists(metaPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    if (doc.RootElement.TryGetProperty("title", out var t))
                        title = t.GetString();
                    if (doc.RootElement.TryGetProperty("label", out var l))
                        label = l.GetString();
                }
                catch { /* ignore */ }
            }
            list.Add(new ProjectInfo
            {
                Id = id,
                Title = title,
                Label = label ?? title ?? id,
                Path = dir,
            });
        }
        return list.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ProjectInfo? GetProject(string projectId)
    {
        return ListProjects().FirstOrDefault(p =>
            string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public ProjectInfo Activate(string projectId)
    {
        var p = GetProject(projectId)
            ?? throw new InvalidOperationException($"Unknown project: {projectId}");
        _activeProjectId = p.Id;
        var wsPath = Path.Combine(WorkspaceRoot, "projects", "workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(wsPath)!);
        File.WriteAllText(
            wsPath,
            JsonSerializer.Serialize(new WorkspaceState { ActiveProject = p.Id }, JsonOpts));
        return p;
    }

    public string GetProjectDir(string projectId)
    {
        var p = GetProject(projectId)
            ?? throw new InvalidOperationException($"Unknown project: {projectId}");
        return p.Path;
    }

    public string? FindBlueprintPath(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var configPath = Path.Combine(dir, "pipeline_config.json");
        var name = "blueprint.clips.grok.json";
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("blueprint_file", out var bf))
                {
                    var n = bf.GetString();
                    if (!string.IsNullOrWhiteSpace(n))
                        name = n;
                }
            }
            catch { /* ignore */ }
        }
        foreach (var candidate in new[]
                 {
                     name,
                     "blueprint.clips.grok.json",
                     "nickandme.clips.grok.json",
                 })
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    public JsonDocument? LoadBlueprint(string projectId)
    {
        var path = FindBlueprintPath(projectId);
        if (path is null)
            return null;
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    public string ConfigPath(string projectId) =>
        Path.Combine(GetProjectDir(projectId), "pipeline_config.json");

    public Dictionary<string, JsonElement> GetConfig(string projectId)
    {
        var path = ConfigPath(projectId);
        if (!File.Exists(path))
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.EnumerateObject())
            dict[p.Name] = p.Value.Clone();
        return dict;
    }

    public Dictionary<string, JsonElement> SaveConfig(string projectId, JsonElement updates)
    {
        var path = ConfigPath(projectId);
        Dictionary<string, object?> merged = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in existing.RootElement.EnumerateObject())
                merged[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
        }

        if (updates.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in updates.EnumerateObject())
                merged[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
        }

        var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n");
        return GetConfig(projectId);
    }

    /// <summary>
    /// Character seeds from blueprint, falling back to Stage 1 scenes.json.
    /// </summary>
    public IReadOnlyList<CharacterSummary> ListCharacters(string projectId)
    {
        var seeds = LoadCharacterSeeds(projectId);
        var projectDir = GetProjectDir(projectId);
        var rows = new List<CharacterSummary>();
        foreach (var (key, info) in seeds)
        {
            var voiceOnly = IsVoiceOnly(key, info);
            var display = info.TryGetProperty("canonical_given_name", out var cn) &&
                          cn.GetString() is { Length: > 0 } cname
                ? cname
                : (info.TryGetProperty("voice_label", out var vl) && vl.GetString() is { Length: > 0 } lab
                    ? lab
                    : key.Replace("Character_", "").Replace("_", " "));

            var refName = CharacterRefFileName(key);
            var resolvedRef = voiceOnly ? null : ResolveCharacterRefPath(projectId, key);
            var hasRef = resolvedRef is not null;
            if (hasRef && resolvedRef is not null)
                refName = Path.GetFileName(resolvedRef);

            // Plates come only from seed design_reference_images (scenes.json / mirrored blueprint).
            // Never invent plates from free-form book_images or untracked disk bookrefs.
            var bookRefs = CollectSeedPlatePaths(info);

            var wardrobe = new List<string>();
            if (info.TryGetProperty("wardrobe_always", out var wa) &&
                wa.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in wa.EnumerateArray())
                {
                    var s = x.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        wardrobe.Add(s!);
                }
            }

            var bookRefImages = new List<CharacterImageRef>();
            if (!voiceOnly)
            {
                for (var i = 0; i < bookRefs.Count; i++)
                {
                    var rel = bookRefs[i].Replace('\\', '/');
                    var full = ResolveProjectRelativePath(projectDir, rel);
                    // Same filename under assets/characters if seed path moved
                    if (full is null || !File.Exists(full))
                    {
                        var byName = Path.Combine(projectDir, "assets", "characters", Path.GetFileName(rel));
                        if (File.Exists(byName))
                        {
                            full = byName;
                            rel = Path.GetRelativePath(projectDir, byName).Replace('\\', '/');
                        }
                    }
                    var exists = full is not null && File.Exists(full);
                    bookRefImages.Add(new CharacterImageRef
                    {
                        Index = i,
                        RelativePath = rel,
                        FileName = Path.GetFileName(rel),
                        Exists = exists,
                        Url = exists
                            ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/bookrefs/{i}"
                            : null,
                    });
                }
            }

            var variants = new List<CharacterImageRef>();
            if (!voiceOnly)
            {
                for (var idx = 1; idx <= 3; idx++)
                {
                    var fileName = $"{key.ToLowerInvariant()}_variant_0{idx}.png";
                    var full = Path.Combine(projectDir, "assets", "characters", fileName);
                    var exists = File.Exists(full) && new FileInfo(full).Length > 64;
                    variants.Add(new CharacterImageRef
                    {
                        Index = idx,
                        RelativePath = $"assets/characters/{fileName}",
                        FileName = fileName,
                        Exists = exists,
                        Url = exists
                            ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/variants/{idx}"
                            : null,
                    });
                }
            }

            var hasPreferred = hasRef;
            string? preferredLabel = hasRef ? "locked" : null;
            string? preferredUrl = hasRef
                ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/ref"
                : null;
            if (!hasPreferred && !voiceOnly)
            {
                var v1 = Path.Combine(projectDir, "assets", "characters",
                    $"{key.ToLowerInvariant()}_variant_01.png");
                if (File.Exists(v1) && new FileInfo(v1).Length >= 64)
                {
                    hasPreferred = true;
                    preferredLabel = "best so far (variant 1)";
                    preferredUrl =
                        $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/variants/1";
                }
            }

            rows.Add(new CharacterSummary
            {
                Key = key,
                DisplayName = display,
                Description = info.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                VisualLock = info.TryGetProperty("visual_lock", out var v) ? v.GetString() ?? "" : "",
                VoiceProfile = info.TryGetProperty("voice_profile", out var vp) ? vp.GetString() ?? "" : "",
                VoiceLabel = info.TryGetProperty("voice_label", out var vlab) ? vlab.GetString() ?? "" : "",
                VoiceOnly = voiceOnly,
                Locked = voiceOnly
                    ? !string.IsNullOrWhiteSpace(
                        info.TryGetProperty("voice_profile", out var vpr) ? vpr.GetString() : null)
                    : hasRef,
                RefFileName = hasRef ? refName : null,
                RefUrl = hasRef
                    ? $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(key)}/ref"
                    : null,
                HasPreferred = hasPreferred,
                PreferredLabel = preferredLabel,
                PreferredUrl = preferredUrl,
                WardrobeAlways = wardrobe,
                DesignReferenceImages = bookRefs,
                BookRefs = bookRefImages,
                Variants = variants,
                AgeBand = info.TryGetProperty("age_band", out var ab) ? ab.GetString() : null,
            });
        }

        return rows
            .OrderBy(r => r.Key.EndsWith("_Young") ? 1 : r.Key.EndsWith("_Teen") ? 2 : 0)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? ResolveCharacterRefPath(string projectId, string charKey)
    {
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(charKey, info))
            return null;

        var charDir = Path.Combine(GetProjectDir(projectId), "assets", "characters");
        foreach (var name in CharacterRefFileCandidates(charKey))
        {
            var full = Path.Combine(charDir, name);
            if (File.Exists(full) && new FileInfo(full).Length >= 64)
                return full;
            // Legacy nested path from older tools
            var nested = Path.Combine(charDir, "assets", "characters", name);
            if (File.Exists(nested) && new FileInfo(nested).Length >= 64)
                return nested;
        }
        return null;
    }

    /// <summary>
    /// On-screen cast keys for a scene that are not voice-only and have no locked ref image.
    /// </summary>
    public IReadOnlyList<string> GetUnlockedOnScreenCharacters(string projectId, int sceneNumber)
    {
        using var bp = LoadBlueprint(projectId);
        if (bp is null)
            return Array.Empty<string>();

        JsonElement? sceneEl = null;
        if (bp.RootElement.TryGetProperty("scenes", out var scenes) &&
            scenes.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scenes.EnumerateArray())
            {
                if (s.TryGetProperty("scene_number", out var sn) && sn.TryGetInt32(out var n) && n == sceneNumber)
                {
                    sceneEl = s.Clone();
                    break;
                }
            }
        }

        if (sceneEl is null)
            return Array.Empty<string>();

        var cast = new HashSet<string>(StringComparer.Ordinal);
        if (sceneEl.Value.TryGetProperty("characters_on_screen", out var cos) &&
            cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
            {
                var k = x.GetString();
                if (!string.IsNullOrWhiteSpace(k))
                    cast.Add(k!);
            }
        }

        // Also scan prompts for Character_* mentions (clip-local cast)
        if (sceneEl.Value.TryGetProperty("veo_clips", out var clips) &&
            clips.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clips.EnumerateArray())
            {
                if (!c.TryGetProperty("visual_prompt", out var vp))
                    continue;
                var text = vp.GetString() ?? "";
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(text, @"Character_[A-Za-z0-9_]+"))
                {
                    if (m.Success)
                        cast.Add(m.Value);
                }
            }
        }

        var unlocked = new List<string>();
        foreach (var key in cast.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var seed = GetCharacterSeed(projectId, key);
            if (seed is not null && IsVoiceOnly(key, seed.Value))
                continue;
            // Unknown seed still counts as needing a lock if mentioned on-screen
            if (ResolveCharacterRefPath(projectId, key) is null)
                unlocked.Add(key);
        }

        return unlocked;
    }

    /// <summary>
    /// Canonical locked ref: <c>{character_key_lower}_ref.png</c>
    /// e.g. Character_Mom → character_mom_ref.png.
    /// </summary>
    public static string CharacterRefFileName(string charKey)
    {
        var k = (charKey ?? "").Trim().Replace(' ', '_').Replace('\\', '/');
        k = Path.GetFileName(k).ToLowerInvariant();
        if (k.EndsWith("_ref.png", StringComparison.OrdinalIgnoreCase))
            return k;
        return $"{k}_ref.png";
    }

    /// <summary>
    /// Candidate on-disk names for a locked ref (canonical + short aliases + common typos).
    /// </summary>
    public static IEnumerable<string> CharacterRefFileCandidates(string charKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileName(name.Trim().Replace(' ', '_')).ToLowerInvariant();
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = name.EndsWith("_ref", StringComparison.OrdinalIgnoreCase) ? name + ".png" : name + "_ref.png";
            if (seen.Add(name))
                list.Add(name);
        }

        Add(CharacterRefFileName(charKey));
        var raw = (charKey ?? "").Trim();
        var bare = raw.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
            ? raw["Character_".Length..]
            : raw;
        Add($"{bare}_ref.png");
        Add(bare);
        // Dad / Daddy alias
        if (bare.Equals("Dad", StringComparison.OrdinalIgnoreCase) ||
            bare.Equals("Daddy", StringComparison.OrdinalIgnoreCase))
        {
            Add("character_daddy_ref.png");
            Add("character_dad_ref.png");
            Add("daddy_ref.png");
            Add("dad_ref.png");
        }
        if (bare.Equals("Mom", StringComparison.OrdinalIgnoreCase) ||
            bare.Equals("Mum", StringComparison.OrdinalIgnoreCase))
        {
            Add("character_mom_ref.png");
            Add("mom_ref.png");
        }
        return list;
    }

    /// <summary>Character seed token object from blueprint/scenes, or null.</summary>
    public JsonElement? GetCharacterSeed(string projectId, string charKey)
    {
        var seeds = LoadCharacterSeeds(projectId);
        return seeds.TryGetValue(charKey, out var info) ? info : null;
    }

    /// <summary>
    /// Max multi-ref image seeds for Characters UI, based on image_provider / image_model_name.
    /// </summary>
    public ImageSeedLimits GetImageSeedLimits(string projectId)
    {
        var cfg = GetConfig(projectId);
        string? model = null;
        string? provider = null;
        if (cfg.TryGetValue("image_model_name", out var m) && m.ValueKind == JsonValueKind.String)
            model = m.GetString();
        if (cfg.TryGetValue("image_provider", out var p) && p.ValueKind == JsonValueKind.String)
            provider = p.GetString();

        model ??= _opts.DefaultImageModel;
        provider ??= _opts.ImageProvider;
        var resolved = ImageApiLimits.ResolveProvider(provider, model);
        return new ImageSeedLimits
        {
            Provider = resolved,
            ImageModel = model,
            MaxReferenceImages = ImageApiLimits.MaxReferenceImages(resolved, model),
        };
    }

    /// <summary>
    /// Update description / visual_lock / voice fields on character seeds in scenes.json
    /// (and blueprint when present). Null args leave that field unchanged; empty string clears.
    /// </summary>
    public void UpdateCharacterSeedText(
        string projectId,
        string charKey,
        string? description = null,
        string? visualLock = null,
        string? voiceProfile = null,
        string? voiceLabel = null)
    {
        void PatchFile(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                           as System.Text.Json.Nodes.JsonObject;
                if (root is null) return;
                var gpv = root["global_production_variables"] as System.Text.Json.Nodes.JsonObject
                          ?? new System.Text.Json.Nodes.JsonObject();
                root["global_production_variables"] = gpv;
                var seeds = gpv["character_seed_tokens"] as System.Text.Json.Nodes.JsonObject;
                if (seeds is null) return;
                // case-insensitive key find
                System.Text.Json.Nodes.JsonObject? seed = null;
                string? foundKey = null;
                foreach (var (k, v) in seeds)
                {
                    if (string.Equals(k, charKey, StringComparison.OrdinalIgnoreCase) &&
                        v is System.Text.Json.Nodes.JsonObject jo)
                    {
                        seed = jo;
                        foundKey = k;
                        break;
                    }
                }
                if (seed is null || foundKey is null) return;
                if (description is not null)
                    seed["description"] = CharacterVisualTextScrubber.ScrubVisualProse(description);
                if (visualLock is not null)
                    seed["visual_lock"] = CharacterVisualTextScrubber.ScrubVisualProse(visualLock);
                if (voiceProfile is not null)
                    seed["voice_profile"] = voiceProfile.Trim();
                if (voiceLabel is not null)
                    seed["voice_label"] = voiceLabel.Trim();
                seeds[foundKey] = seed;
                File.WriteAllText(
                    path,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            catch
            {
                /* non-fatal */
            }
        }

        PatchFile(ResolveScenesJsonPath(projectId));
        var bp = FindBlueprintPath(projectId);
        if (bp is not null)
            PatchFile(bp);
    }

    /// <summary>
    /// Map Character_* → { voice_profile, voice_label } for video prompt VOICE LOCK.
    /// Prefer scenes.json seeds (source of truth for voice edits), fall back to blueprint.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> LoadCharacterVoiceMap(string projectId)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        void Ingest(Dictionary<string, JsonElement> seeds)
        {
            foreach (var (key, info) in seeds)
            {
                var profile = info.TryGetProperty("voice_profile", out var vp)
                    ? (vp.GetString() ?? "").Trim()
                    : "";
                var label = info.TryGetProperty("voice_label", out var vl)
                    ? (vl.GetString() ?? "").Trim()
                    : "";
                if (profile.Length == 0 && label.Length == 0)
                    continue;
                map[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["voice_profile"] = profile,
                    ["voice_label"] = label,
                };
            }
        }

        // scenes.json first so Characters page edits win
        try
        {
            var scenesPath = ResolveScenesJsonPath(projectId);
            if (File.Exists(scenesPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(scenesPath));
                if (doc.RootElement.TryGetProperty("global_production_variables", out var gpv) &&
                    gpv.TryGetProperty("character_seed_tokens", out var seeds) &&
                    seeds.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in seeds.EnumerateObject())
                        dict[p.Name] = p.Value.Clone();
                    Ingest(dict);
                }
            }
        }
        catch { /* ignore */ }

        // Fill any missing keys from blueprint / full seed load
        try
        {
            var all = LoadCharacterSeeds(projectId);
            foreach (var (key, info) in all)
            {
                if (map.ContainsKey(key)) continue;
                var profile = info.TryGetProperty("voice_profile", out var vp)
                    ? (vp.GetString() ?? "").Trim()
                    : "";
                var label = info.TryGetProperty("voice_label", out var vl)
                    ? (vl.GetString() ?? "").Trim()
                    : "";
                if (profile.Length == 0 && label.Length == 0)
                    continue;
                map[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["voice_profile"] = profile,
                    ["voice_label"] = label,
                };
            }
        }
        catch { /* ignore */ }

        return map;
    }

    public void UpdateCharacterSeedPlaceholder(string projectId, string charKey, string refFileName)
    {
        var bpPath = FindBlueprintPath(projectId);
        if (bpPath is null || !File.Exists(bpPath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(bpPath));
            var root = doc.RootElement.Clone();
            // Rebuild via mutable dictionary tree
            var tree = JsonSerializer.Deserialize<Dictionary<string, object?>>(root.GetRawText())
                       ?? new Dictionary<string, object?>();
            if (!tree.TryGetValue("global_production_variables", out var gpvObj) || gpvObj is null)
                return;

            var gpvJson = JsonSerializer.Serialize(gpvObj);
            var gpv = JsonSerializer.Deserialize<Dictionary<string, object?>>(gpvJson)
                      ?? new Dictionary<string, object?>();
            if (!gpv.TryGetValue("character_seed_tokens", out var seedsObj) || seedsObj is null)
                return;

            var seedsJson = JsonSerializer.Serialize(seedsObj);
            var seeds = JsonSerializer.Deserialize<Dictionary<string, object?>>(seedsJson)
                        ?? new Dictionary<string, object?>();
            if (!seeds.TryGetValue(charKey, out var seedObj) || seedObj is null)
                return;

            var seedJson = JsonSerializer.Serialize(seedObj);
            var seed = JsonSerializer.Deserialize<Dictionary<string, object?>>(seedJson)
                       ?? new Dictionary<string, object?>();
            seed["reference_image_placeholder"] = CharacterRefFileName(charKey);
            seeds[charKey] = seed;
            gpv["character_seed_tokens"] = seeds;
            tree["global_production_variables"] = gpv;

            var outJson = JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(bpPath, outJson + "\n");
        }
        catch
        {
            // Non-fatal: lock file still written
        }
    }

    /// <summary>Resolve pipeline_state.json path (honors project.json state_file).</summary>
    public string ResolvePipelineStatePath(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var stateName = "pipeline_state.json";
        var metaPath = Path.Combine(dir, "project.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (meta.RootElement.TryGetProperty("state_file", out var sf) &&
                    sf.GetString() is { Length: > 0 } n)
                    stateName = n;
            }
            catch { /* ignore */ }
        }
        return Path.Combine(dir, stateName);
    }

    /// <summary>
    /// Whether book images have been sorted onto character seeds
    /// (pipeline_state.character_plates.sorted_by_character).
    /// </summary>
    public CharacterPlatesState GetCharacterPlatesState(string projectId)
    {
        var path = ResolvePipelineStatePath(projectId);
        var state = new CharacterPlatesState();
        if (!File.Exists(path)) return state;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            // Nested object preferred
            if (doc.RootElement.TryGetProperty("character_plates", out var cp) &&
                cp.ValueKind == JsonValueKind.Object)
            {
                state.SortedByCharacter = ReadJsonBool(cp, "sorted_by_character");
                if (cp.TryGetProperty("sorted_at", out var at) && at.ValueKind == JsonValueKind.String)
                    state.SortedAt = at.GetString();
                if (cp.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String &&
                    src.GetString() is { Length: > 0 } ss)
                    state.Source = ss;
                if (cp.TryGetProperty("characters_updated", out var cu) && cu.TryGetInt32(out var n))
                    state.CharactersUpdated = n;
                if (cp.TryGetProperty("method", out var meth) && meth.ValueKind == JsonValueKind.String)
                    state.Method = meth.GetString();
                return state;
            }
            // Flat legacy keys
            if (ReadJsonBool(doc.RootElement, "character_plates_sorted"))
            {
                state.SortedByCharacter = true;
                if (doc.RootElement.TryGetProperty("character_plates_sorted_at", out var at2) &&
                    at2.ValueKind == JsonValueKind.String)
                    state.SortedAt = at2.GetString();
                if (doc.RootElement.TryGetProperty("character_plates_method", out var m2) &&
                    m2.ValueKind == JsonValueKind.String)
                    state.Method = m2.GetString();
            }
        }
        catch { /* ignore */ }
        return state;
    }

    private static bool ReadJsonBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) && b,
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            _ => false,
        };
    }

    /// <summary>Record that character plates were sorted into scenes.json seeds.</summary>
    public void MarkCharacterPlatesSorted(string projectId, int charactersUpdated, string method = "heuristic")
    {
        var path = ResolvePipelineStatePath(projectId);
        var merged = LoadPipelineStateDict(path);
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        merged["character_plates"] = new Dictionary<string, object?>
        {
            ["sorted_by_character"] = true,
            ["sorted_at"] = now,
            ["source"] = "scenes.json#character_seed_tokens.design_reference_images",
            ["characters_updated"] = charactersUpdated,
            ["method"] = method,
        };
        // Keep flat keys in sync for simple greps / older tools
        merged["character_plates_sorted"] = true;
        merged["character_plates_sorted_at"] = now;
        merged["character_plates_method"] = method;
        var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n");
    }

    /// <summary>Clear the sorted flag (e.g. after book re-import invalidates plates).</summary>
    public void ClearCharacterPlatesSorted(string projectId)
    {
        var path = ResolvePipelineStatePath(projectId);
        if (!File.Exists(path)) return;
        var merged = LoadPipelineStateDict(path);
        merged["character_plates"] = new Dictionary<string, object?>
        {
            ["sorted_by_character"] = false,
            ["sorted_at"] = null,
            ["source"] = "scenes.json#character_seed_tokens.design_reference_images",
            ["characters_updated"] = 0,
            ["method"] = null,
        };
        merged["character_plates_sorted"] = false;
        merged.Remove("character_plates_sorted_at");
        merged.Remove("character_plates_method");
        var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n");
    }

    private static Dictionary<string, object?> LoadPipelineStateDict(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var rawDoc = JsonDocument.Parse(File.ReadAllText(path));
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in rawDoc.RootElement.EnumerateObject())
            merged[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
        return merged;
    }

    /// <summary>Bump character revision in pipeline_state (cascade stale marker).</summary>
    public void MarkCharacterChanged(string projectId, string charKey, string reason)
    {
        var path = ResolvePipelineStatePath(projectId);
        var merged = LoadPipelineStateDict(path);

        var revs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (merged.TryGetValue("character_revisions", out var crObj) && crObj is not null)
        {
            try
            {
                using var crDoc = JsonDocument.Parse(JsonSerializer.Serialize(crObj));
                if (crDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in crDoc.RootElement.EnumerateObject())
                        revs[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
                }
            }
            catch { /* ignore */ }
        }

        var prevRev = 0;
        if (revs.TryGetValue(charKey, out var prev) && prev is not null)
        {
            try
            {
                using var prevDoc = JsonDocument.Parse(JsonSerializer.Serialize(prev));
                if (prevDoc.RootElement.TryGetProperty("revision", out var r) && r.TryGetInt32(out var rv))
                    prevRev = rv;
            }
            catch { /* ignore */ }
        }

        revs[charKey] = new Dictionary<string, object?>
        {
            ["revision"] = prevRev + 1,
            ["updated_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["reason"] = reason,
        };
        merged["character_revisions"] = revs;
        merged["characters_designed"] = true;

        var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n");
    }

    public string? ResolveCharacterVariantPath(string projectId, string charKey, int variantIndex)
    {
        if (variantIndex is < 1 or > 3)
            return null;
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(charKey, info))
            return null;
        var fileName = $"{charKey.ToLowerInvariant()}_variant_0{variantIndex}.png";
        var full = Path.Combine(GetProjectDir(projectId), "assets", "characters", fileName);
        return File.Exists(full) && new FileInfo(full).Length >= 64 ? full : null;
    }

    public string? ResolveCharacterBookRefPath(string projectId, string charKey, int bookIndex)
    {
        var seeds = LoadCharacterSeeds(projectId);
        if (seeds.TryGetValue(charKey, out var info) && IsVoiceOnly(charKey, info))
            return null;

        var projectDir = GetProjectDir(projectId);
        // Only seed-tracked plates (scenes.json design_reference_images) — no free disk scan
        var bookRefs = seeds.TryGetValue(charKey, out info)
            ? CollectSeedPlatePaths(info)
            : new List<string>();

        if (bookIndex < 0 || bookIndex >= bookRefs.Count)
            return null;
        var rel = bookRefs[bookIndex];
        var full = ResolveProjectRelativePath(projectDir, rel);
        if (full is not null) return full;
        var byName = Path.Combine(projectDir, "assets", "characters", Path.GetFileName(rel));
        return File.Exists(byName) ? byName : null;
    }

    /// <summary>
    /// Paths from character seed design_reference_images (book_reference_images alias).
    /// Skips text-only / sampled layout filenames so they are never shown as plates.
    /// </summary>
    private static List<string> CollectSeedPlatePaths(JsonElement info)
    {
        var bookRefs = new List<string>();
        foreach (var prop in new[] { "design_reference_images", "book_reference_images" })
        {
            if (!info.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var x in arr.EnumerateArray())
            {
                var s = x.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (IsTextOnlyPlatePath(s)) continue;
                if (!bookRefs.Contains(s!, StringComparer.OrdinalIgnoreCase))
                    bookRefs.Add(s!);
            }
            if (bookRefs.Count > 0)
                break; // prefer design_reference_images when present
        }
        return bookRefs;
    }

    /// <summary>True for sampled/OCR/text-page paths that must never be character plates.</summary>
    internal static bool IsTextOnlyPlatePath(string pathOrName)
    {
        var n = Path.GetFileName(pathOrName);
        if (n.Contains("sampled", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("text_page", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("ocr", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? ResolveProjectRelativePath(string projectDir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        var norm = relative.Replace('\\', '/').TrimStart('/');
        // Reject path traversal
        if (norm.Contains("..", StringComparison.Ordinal))
            return null;
        var full = Path.GetFullPath(Path.Combine(projectDir, norm.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(projectDir);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Scene list from Stage 2 blueprint + on-disk clip counts.
    /// When <paramref name="probeDurations"/> is false (LoadSim), skip ffprobe — much faster under concurrency.
    /// Results are cached briefly (single-flight) when <see cref="SceneListCache"/> is registered.
    /// </summary>
    public IReadOnlyList<SceneSummary> ListScenes(string projectId, bool probeDurations = true)
    {
        if (_sceneListCache is null)
            return ListScenesCore(projectId, probeDurations);

        return _sceneListCache.GetOrBuild(
            projectId,
            probeDurations,
            () => ListScenesCore(projectId, probeDurations));
    }

    private IReadOnlyList<SceneSummary> ListScenesCore(string projectId, bool probeDurations)
    {
        using var bp = LoadBlueprint(projectId);
        if (bp is null ||
            !bp.RootElement.TryGetProperty("scenes", out var scenesEl) ||
            scenesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SceneSummary>();
        }

        var projectDir = GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var scenesDir = Path.Combine(projectDir, "assets", "scenes");
        var videoIndex = IndexDirFiles(videoDir);
        var scenesIndex = IndexDirFiles(scenesDir);

        var rows = new List<SceneSummary>();
        foreach (var s in scenesEl.EnumerateArray())
        {
            if (!s.TryGetProperty("scene_number", out var snEl) || !snEl.TryGetInt32(out var sn))
                continue;

            var clips = s.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array
                ? vc.EnumerateArray().ToList()
                : new List<JsonElement>();
            var nClips = clips.Count;
            var onDisk = 0;
            foreach (var c in clips)
            {
                var cn = c.TryGetProperty("clip_number", out var cnEl) && cnEl.TryGetInt32(out var n) ? n : 0;
                if (cn <= 0) continue;
                if (ClipOnDisk(videoIndex, sn, cn))
                    onDisk++;
            }

            var compositeOk =
                HasCompositeFile(videoIndex, scenesIndex, sn);

            double? planned = null;
            if (s.TryGetProperty("total_estimated_duration_seconds", out var dEl))
            {
                if (dEl.TryGetDouble(out var dd)) planned = dd;
                else if (dEl.TryGetInt32(out var di)) planned = di;
            }

            double? actual = null;
            if (probeDurations && _duration is not null)
            {
                var compositePath = ResolveCompositePath(projectId, sn);
                var clipPaths = new List<string>();
                foreach (var c in clips)
                {
                    var cn = c.TryGetProperty("clip_number", out var cnEl) && cnEl.TryGetInt32(out var n) ? n : 0;
                    if (cn <= 0) continue;
                    var cp = ResolveClipVideoPath(projectId, sn, cn);
                    if (cp is not null) clipPaths.Add(cp);
                }
                actual = _duration.GetSceneActualDurationSeconds(compositePath, clipPaths);
            }

            var chars = new List<string>();
            if (s.TryGetProperty("characters_on_screen", out var cos) && cos.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in cos.EnumerateArray())
                {
                    var name = x.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        chars.Add(name!);
                }
            }

            var locs = new List<string>();
            if (s.TryGetProperty("location_ids", out var lids) && lids.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in lids.EnumerateArray())
                {
                    var name = x.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        locs.Add(name!);
                }
            }

            string? primaryLoc = null;
            if (s.TryGetProperty("primary_location_id", out var pl) &&
                pl.GetString() is { Length: > 0 } plId)
            {
                primaryLoc = plId;
                if (!locs.Contains(plId, StringComparer.OrdinalIgnoreCase))
                    locs.Insert(0, plId);
            }

            var complete = nClips > 0 && onDisk >= nClips;
            var status = nClips == 0 || onDisk == 0
                ? "empty"
                : complete ? "complete" : "partial";

            rows.Add(new SceneSummary
            {
                SceneNumber = sn,
                Setting = s.TryGetProperty("setting", out var set) ? set.GetString() ?? "" : "",
                ClipCount = nClips,
                ClipsOnDisk = onDisk,
                ClipsComplete = complete,
                PlannedDurationSeconds = planned,
                ActualDurationSeconds = actual,
                DurationSeconds = actual ?? planned,
                CompositeExists = compositeOk,
                CharactersOnScreen = chars,
                LocationIds = locs,
                PrimaryLocationId = primaryLoc,
                Status = status,
            });
        }

        return rows.OrderBy(r => r.SceneNumber).ToList();
    }

    public SceneDetail? GetSceneDetail(string projectId, int sceneNumber, bool probeDurations = true)
    {
        using var bp = LoadBlueprint(projectId);
        if (bp is null)
            return null;

        JsonElement? sceneEl = null;
        if (bp.RootElement.TryGetProperty("scenes", out var scenesEl) &&
            scenesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scenesEl.EnumerateArray())
            {
                if (s.TryGetProperty("scene_number", out var snEl) &&
                    snEl.TryGetInt32(out var sn) &&
                    sn == sceneNumber)
                {
                    sceneEl = s.Clone();
                    break;
                }
            }
        }

        if (sceneEl is null)
            return null;

        var sEl = sceneEl.Value;
        var projectDir = GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var scenesDir = Path.Combine(projectDir, "assets", "scenes");
        var videoIndex = IndexDirFiles(videoDir);
        var scenesIndex = IndexDirFiles(scenesDir);

        var clips = new List<ClipSummary>();
        if (sEl.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in vc.EnumerateArray())
            {
                var cn = c.TryGetProperty("clip_number", out var cnEl) && cnEl.TryGetInt32(out var n) ? n : 0;
                if (cn <= 0) continue;

                var fileName = $"scene_{sceneNumber:D2}_clip_{cn:D2}.mp4";
                var onDisk = ClipOnDisk(videoIndex, sceneNumber, cn);
                long size = 0;
                if (onDisk && videoIndex.TryGetValue(fileName, out var sz))
                    size = sz;

                var dialogue = "";
                string? speaker = null;
                string? delivery = null;
                if (c.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object)
                {
                    if (ap.TryGetProperty("dialogue", out var d))
                        dialogue = d.GetString() ?? "";
                    if (ap.TryGetProperty("speaker", out var sp))
                        speaker = sp.GetString();
                    if (ap.TryGetProperty("delivery", out var del))
                        delivery = del.GetString();
                }

                var dur = 0;
                if (c.TryGetProperty("duration_seconds", out var dEl) && dEl.TryGetInt32(out var ds))
                    dur = ds;

                double? actualClip = null;
                if (probeDurations && onDisk && _duration is not null)
                {
                    var clipPath = ResolveClipVideoPath(projectId, sceneNumber, cn);
                    actualClip = _duration.GetDurationSeconds(clipPath);
                }

                clips.Add(new ClipSummary
                {
                    ClipNumber = cn,
                    Timestamp = c.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "",
                    DurationSeconds = dur,
                    ActualDurationSeconds = actualClip,
                    Continuation = c.TryGetProperty("veo_continuation_source", out var cont)
                        ? cont.GetString() ?? "none"
                        : "none",
                    PrimarySubject = c.TryGetProperty("primary_subject", out var ps)
                        ? ps.GetString() ?? ""
                        : "",
                    VisualPrompt = c.TryGetProperty("visual_prompt", out var vp) ? vp.GetString() ?? "" : "",
                    NegativePrompt = c.TryGetProperty("negative_prompt", out var np) ? np.GetString() ?? "" : "",
                    Dialogue = dialogue,
                    Speaker = speaker,
                    Delivery = delivery,
                    OnDisk = onDisk,
                    SizeBytes = size,
                    FileName = onDisk ? fileName : null,
                    VideoUrl = onDisk
                        ? $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{cn}/video"
                        : null,
                });
            }
        }

        clips = clips.OrderBy(c => c.ClipNumber).ToList();
        var onDiskCount = clips.Count(c => c.OnDisk);

        var compositeOk = HasCompositeFile(videoIndex, scenesIndex, sceneNumber);

        double? planned = null;
        if (sEl.TryGetProperty("total_estimated_duration_seconds", out var td))
        {
            if (td.TryGetDouble(out var dd)) planned = dd;
            else if (td.TryGetInt32(out var di)) planned = di;
        }

        double? actual = null;
        if (probeDurations && _duration is not null)
        {
            var compositePath = ResolveCompositePath(projectId, sceneNumber);
            var clipPaths = clips
                .Where(c => c.OnDisk)
                .Select(c => ResolveClipVideoPath(projectId, sceneNumber, c.ClipNumber))
                .Where(p => p is not null)
                .Cast<string>();
            actual = _duration.GetSceneActualDurationSeconds(compositePath, clipPaths);
        }

        var chars = new List<string>();
        if (sEl.TryGetProperty("characters_on_screen", out var cos) && cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
            {
                var name = x.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    chars.Add(name!);
            }
        }

        var locs = new List<string>();
        if (sEl.TryGetProperty("location_ids", out var lids) && lids.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in lids.EnumerateArray())
            {
                var name = x.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    locs.Add(name!);
            }
        }

        return new SceneDetail
        {
            SceneNumber = sceneNumber,
            Setting = sEl.TryGetProperty("setting", out var set) ? set.GetString() ?? "" : "",
            PlannedDurationSeconds = planned,
            ActualDurationSeconds = actual,
            DurationSeconds = actual ?? planned,
            ClipCount = clips.Count,
            ClipsOnDisk = onDiskCount,
            CompositeExists = compositeOk,
            CompositeUrl = compositeOk
                ? $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/composite"
                : null,
            CharactersOnScreen = chars,
            LocationIds = locs,
            PrimaryLocationId = sEl.TryGetProperty("primary_location_id", out var pl)
                ? pl.GetString()
                : null,
            Clips = clips,
        };
    }

    public string? ResolveClipVideoPath(string projectId, int sceneNumber, int clipNumber)
    {
        var path = Path.Combine(
            GetProjectDir(projectId),
            "assets",
            "video",
            $"scene_{sceneNumber:D2}_clip_{clipNumber:D2}.mp4");
        return File.Exists(path) && new FileInfo(path).Length >= 1024 ? path : null;
    }

    /// <summary>
    /// WIP full-movie path from config <c>wip_movie_path</c> (default assets/movie_wip.mp4).
    /// Returns null if the file is missing or empty.
    /// </summary>
    public string? ResolveWipMoviePath(string projectId)
    {
        var projectDir = GetProjectDir(projectId);
        var cfg = GetConfig(projectId);
        var wipRel = "assets/movie_wip.mp4";
        if (cfg.TryGetValue("wip_movie_path", out var w) &&
            w.ValueKind == JsonValueKind.String &&
            w.GetString() is { Length: > 0 } s)
            wipRel = s.Replace('\\', '/').TrimStart('/');

        if (wipRel.Contains("..", StringComparison.Ordinal))
            return null;

        var full = Path.IsPathRooted(wipRel)
            ? wipRel
            : Path.GetFullPath(Path.Combine(projectDir, wipRel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(projectDir);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) && new FileInfo(full).Length >= 1024 ? full : null;
    }

    public string ResolveScenesJsonPath(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var preferred = "scenes.json";
        var metaPath = Path.Combine(dir, "project.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("scenes_file", out var sf))
                {
                    var n = sf.GetString();
                    if (!string.IsNullOrWhiteSpace(n))
                        preferred = n!;
                }
            }
            catch { /* ignore */ }
        }

        foreach (var candidate in new[] { preferred, "scenes.json", "nickandme.scenes.json" })
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full))
                return full;
        }

        return Path.Combine(dir, preferred);
    }

    public AdaptationStatus GetAdaptationStatus(string projectId)
    {
        var dir = GetProjectDir(projectId);
        var book = ReadBookSourceStatus(dir);
        var stage1 = ReadStage1Status(projectId, dir);
        var stage2 = ReadStage2PlanStatus(projectId, dir, stage1);
        var xai = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"));

        var next = "done";
        if (!book.PdfExists && !book.BookTextExists)
            next = "import_book";
        else if (!book.ReadyForStage1)
            next = "fix_book_text";
        else if (!stage1.Present || stage1.SceneCount == 0)
            next = "run_stage1";
        else if (!stage2.Stage2Ready)
            next = "run_stage2";
        else if (stage2.Stage2Stale)
            next = "replan_stage2";
        else
            next = "generate_clips";

        return new AdaptationStatus
        {
            ProjectId = projectId,
            Book = book,
            Stage1 = stage1,
            Stage2 = stage2,
            XaiConfigured = xai,
            NextStep = next,
        };
    }

    public async Task<string> SaveBookUploadAsync(
        string projectId,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        var dir = GetProjectDir(projectId);
        var source = Path.Combine(dir, "source");
        Directory.CreateDirectory(source);

        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe))
            throw new InvalidOperationException("file name required");

        var ext = Path.GetExtension(safe).ToLowerInvariant();
        if (ext is not (".pdf" or ".txt"))
            throw new InvalidOperationException("Only .pdf or .txt uploads are supported");

        // Buffer once so we can write book_full.txt + original name for .txt uploads
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        if (ext == ".txt")
        {
            var bookFull = Path.Combine(source, "book_full.txt");
            await File.WriteAllBytesAsync(bookFull, bytes, ct);
            if (!safe.Equals("book_full.txt", StringComparison.OrdinalIgnoreCase))
                await File.WriteAllBytesAsync(Path.Combine(source, safe), bytes, ct);
            return bookFull;
        }

        var dest = Path.Combine(source, safe);
        await File.WriteAllBytesAsync(dest, bytes, ct);
        return dest;
    }

    private BookSourceStatus ReadBookSourceStatus(string projectDir)
    {
        var source = Path.Combine(projectDir, "source");
        var bookPath = Path.Combine(source, "book_full.txt");
        var metaPath = Path.Combine(source, "extract_meta.json");
        var imgDir = Path.Combine(source, "book_images");

        string? pdfName = null;
        if (Directory.Exists(source))
        {
            try
            {
                pdfName = Directory.EnumerateFiles(source, "*.pdf")
                    .Concat(Directory.EnumerateFiles(source, "*.PDF"))
                    .Select(Path.GetFileName)
                    .OrderBy(n => n?.Contains("nick", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
                    .ThenByDescending(n =>
                    {
                        try { return new FileInfo(Path.Combine(source, n!)).Length; }
                        catch { return 0L; }
                    })
                    .FirstOrDefault();
            }
            catch { /* ignore */ }
        }

        var status = new BookSourceStatus
        {
            PdfExists = !string.IsNullOrEmpty(pdfName),
            PdfName = pdfName,
            BookTextExists = File.Exists(bookPath),
            BookTextPath = File.Exists(bookPath) ? bookPath : null,
            BookTextBytes = File.Exists(bookPath) ? new FileInfo(bookPath).Length : 0,
        };

        if (Directory.Exists(imgDir))
        {
            try
            {
                status.PageImageCount = Directory.EnumerateFiles(imgDir)
                    .Count(f =>
                    {
                        var e = Path.GetExtension(f).ToLowerInvariant();
                        return e is ".jpg" or ".jpeg" or ".png" or ".webp";
                    });
            }
            catch { /* ignore */ }
        }

        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                status.TextQuality = root.TryGetProperty("text_quality", out var tq) ? tq.GetString() : null;
                status.BookKind = root.TryGetProperty("book_kind", out var bk) ? bk.GetString() : null;
                status.TextEngine = root.TryGetProperty("text_engine", out var te) ? te.GetString() : null;
                if (root.TryGetProperty("text_words", out var tw) && tw.TryGetInt32(out var words))
                    status.TextWords = words;
                if (root.TryGetProperty("suggested_total_minutes", out var sm) && sm.TryGetInt32(out var mins))
                    status.SuggestedTotalMinutes = mins;
                if (root.TryGetProperty("suggested_chunk_pages", out var sc) && sc.TryGetInt32(out var chunks))
                    status.SuggestedChunkPages = chunks;
                if (root.TryGetProperty("ready_for_stage1", out var r) &&
                    (r.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    status.ReadyForStage1 = r.GetBoolean();

                if (root.TryGetProperty("analysis", out var an) && an.ValueKind == JsonValueKind.Object)
                {
                    if (an.TryGetProperty("garbage_score", out var gs) && gs.TryGetDouble(out var gsv))
                        status.GarbageScore = gsv;
                    if (string.IsNullOrEmpty(status.TextQuality) &&
                        an.TryGetProperty("text_quality", out var atq))
                        status.TextQuality = atq.GetString();
                }

                if (root.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in notes.EnumerateArray())
                    {
                        var s = n.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            status.Notes.Add(s!);
                    }
                }
            }
            catch { /* ignore */ }
        }

        // Prefer extract_meta.ready_for_stage1 when present (set by BookPrepareService strategy).
        // Only fall back to heuristics when meta is missing or incomplete.
        var metaReadySet = File.Exists(metaPath) && status.TextQuality is not null;
        if (!status.BookTextExists)
        {
            status.ReadyForStage1 = false;
        }
        else if (!metaReadySet)
        {
            if (status.TextQuality is null && status.BookTextBytes > 200)
            {
                // No meta yet — allow Stage 1 if plain text looks present (user may have uploaded .txt)
                status.TextQuality = "unknown";
                status.ReadyForStage1 = true;
            }
            else if (string.Equals(status.TextQuality, "good", StringComparison.OrdinalIgnoreCase) &&
                     status.GarbageScore < 0.45)
            {
                status.ReadyForStage1 = true;
            }
        }
        else if (!status.ReadyForStage1)
        {
            // Strategy often sets ready=false for "prefer vision" even when text is usable
            // (picture books). Allow Stage 1 / re-run when quality is good enough.
            if (string.Equals(status.TextQuality, "good", StringComparison.OrdinalIgnoreCase) &&
                status.GarbageScore < 0.45 &&
                status.BookTextBytes > 200)
            {
                status.ReadyForStage1 = true;
                if (status.Notes.All(n => !n.Contains("Stage 1 unlocked", StringComparison.OrdinalIgnoreCase)))
                    status.Notes.Add(
                        "Stage 1 unlocked: text quality is good enough (vision still optional for better OCR).");
            }
        }

        if (status.BookTextExists)
        {
            try
            {
                var text = File.ReadAllText(bookPath);
                status.Preview = text.Length <= 600 ? text : text[..600] + "…";
                if (status.TextWords is null or 0)
                {
                    status.TextWords = text.Split(
                        new[] { ' ', '\n', '\r', '\t' },
                        StringSplitOptions.RemoveEmptyEntries).Length;
                }
            }
            catch { /* ignore */ }
        }

        // Re-run path: existing bible + book text is enough even if prepare still flags "not ready"
        try
        {
            foreach (var name in new[] { "scenes.json", "nickandme.scenes.json" })
            {
                var scenesPath = Path.Combine(projectDir, name);
                if (!status.ReadyForStage1 &&
                    status.BookTextExists &&
                    status.BookTextBytes > 200 &&
                    File.Exists(scenesPath) &&
                    new FileInfo(scenesPath).Length > 64)
                {
                    status.ReadyForStage1 = true;
                    if (status.Notes.All(n => !n.Contains("Re-run Stage 1", StringComparison.OrdinalIgnoreCase)))
                        status.Notes.Add(
                            "Re-run Stage 1 enabled: scenes.json already exists and book_full.txt is present.");
                    break;
                }
            }
        }
        catch { /* ignore */ }

        return status;
    }

    private Stage1Status ReadStage1Status(string projectId, string projectDir)
    {
        var path = ResolveScenesJsonPath(projectId);
        var status = new Stage1Status { ScenesFile = Path.GetFileName(path) };
        if (!File.Exists(path))
            return status;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            status.Present = true;
            status.MovieTitle = root.TryGetProperty("movie_title", out var mt) ? mt.GetString() : null;
            status.SourceBookTitle = root.TryGetProperty("source_book_title", out var sbt) ? sbt.GetString() : null;
            if (root.TryGetProperty("cumulative_duration_target_seconds", out var rt))
            {
                if (rt.TryGetDouble(out var rd)) status.RuntimeSeconds = rd;
                else if (rt.TryGetInt32(out var ri)) status.RuntimeSeconds = ri;
            }

            try
            {
                status.Mtime = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { /* ignore */ }

            var gpv = root.TryGetProperty("global_production_variables", out var g) ? g : default;
            if (gpv.ValueKind == JsonValueKind.Object)
            {
                if (gpv.TryGetProperty("character_seed_tokens", out var seeds) &&
                    seeds.ValueKind == JsonValueKind.Object)
                {
                    status.CharacterCount = seeds.EnumerateObject().Count();
                    foreach (var p in seeds.EnumerateObject())
                    {
                        var display = p.Name.Replace("Character_", "").Replace("_", " ");
                        if (p.Value.ValueKind == JsonValueKind.Object &&
                            p.Value.TryGetProperty("canonical_given_name", out var cn) &&
                            cn.GetString() is { Length: > 0 } cname)
                            display = cname;
                        status.CastNames.Add(display);
                    }
                }

                if (gpv.TryGetProperty("location_seed_tokens", out var locs) &&
                    locs.ValueKind == JsonValueKind.Object)
                    status.LocationCount = locs.EnumerateObject().Count();
            }

            if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in scenes.EnumerateArray())
                {
                    var sn = s.TryGetProperty("scene_number", out var sne) && sne.TryGetInt32(out var n) ? n : 0;
                    var beats = 0;
                    if (s.TryGetProperty("story_beats", out var sb) && sb.ValueKind == JsonValueKind.Array)
                        beats = sb.GetArrayLength();
                    status.BeatCount += beats;
                    double? dur = null;
                    // Stage 1 uses duration_target_seconds; accept legacy estimated_duration_seconds too
                    if (s.TryGetProperty("duration_target_seconds", out var d) ||
                        s.TryGetProperty("estimated_duration_seconds", out d))
                    {
                        if (d.TryGetDouble(out var dd)) dur = dd;
                        else if (d.TryGetInt32(out var di)) dur = di;
                    }

                    status.Scenes.Add(new Stage1SceneRow
                    {
                        SceneNumber = sn,
                        Setting = s.TryGetProperty("setting", out var set) ? set.GetString() ?? "" : "",
                        BeatCount = beats,
                        DurationSeconds = dur,
                    });
                }

                status.SceneCount = status.Scenes.Count;
                status.Scenes = status.Scenes.OrderBy(x => x.SceneNumber).ToList();
            }
        }
        catch (Exception)
        {
            status.Present = File.Exists(path);
        }

        return status;
    }

    private Stage2PlanStatus ReadStage2PlanStatus(string projectId, string projectDir, Stage1Status stage1)
    {
        var bpPath = FindBlueprintPath(projectId);
        var status = new Stage2PlanStatus
        {
            Stage1Exists = stage1.Present && stage1.SceneCount > 0,
            Stage1Scenes = stage1.SceneCount,
            BlueprintExists = bpPath is not null && File.Exists(bpPath),
            BlueprintPath = bpPath,
            BlueprintFileName = bpPath is not null ? Path.GetFileName(bpPath) : null,
        };

        if (bpPath is null || !File.Exists(bpPath))
            return status;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(bpPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
            {
                status.Stage2Scenes = scenes.GetArrayLength();
                foreach (var s in scenes.EnumerateArray())
                {
                    if (s.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array)
                        status.Stage2Clips += vc.GetArrayLength();
                }
            }

            status.Stage2Ready = status.Stage2Scenes > 0 && status.Stage2Clips > 0;

            if (root.TryGetProperty("stage2_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                status.LastCompletedAt = meta.TryGetProperty("completed_at", out var ca)
                    ? ca.GetString()
                    : meta.TryGetProperty("last_partial_at", out var lp) ? lp.GetString() : null;
                status.LastRunMessage = meta.TryGetProperty("last_run_message", out var lm)
                    ? lm.GetString()
                    : null;
                if (meta.TryGetProperty("validation_issue_count", out var vic) && vic.TryGetInt32(out var n))
                    status.ValidationIssueCount = n;
            }

            if (string.IsNullOrEmpty(status.LastCompletedAt))
            {
                try
                {
                    status.LastCompletedAt = File.GetLastWriteTime(bpPath).ToString("yyyy-MM-ddTHH:mm:ss");
                }
                catch { /* ignore */ }
            }

            // Stale when Stage 1 bible is newer than blueprint
            var s1Path = ResolveScenesJsonPath(projectId);
            if (File.Exists(s1Path) && status.Stage2Ready)
            {
                try
                {
                    var s1m = File.GetLastWriteTimeUtc(s1Path);
                    var bpm = File.GetLastWriteTimeUtc(bpPath);
                    status.Stage2Stale = s1m > bpm.AddSeconds(1);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        return status;
    }

    public string? ResolveCompositePath(string projectId, int sceneNumber)
    {
        var dir = GetProjectDir(projectId);
        // Remux writes scene_XX.mp4; older/python path used scene_XX_complete.mp4
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, "assets", "video", $"scene_{sceneNumber:D2}.mp4"),
                     Path.Combine(dir, "assets", "scenes", $"scene_{sceneNumber:D2}.mp4"),
                     Path.Combine(dir, "assets", "video", $"scene_{sceneNumber:D2}_complete.mp4"),
                     Path.Combine(dir, "assets", "scenes", $"scene_{sceneNumber:D2}_complete.mp4"),
                 })
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length >= 1024)
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Whether movie_wip is missing, older than inputs, or built from a different set of
    /// scenes (added/deleted). Does not trigger clip regen — mux freshness only.
    /// </summary>
    public WipFreshness AssessWipFreshness(string projectId)
    {
        var result = new WipFreshness();
        var projectDir = GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");

        // Physical path even when file missing (for manifest path)
        var wipFullPath = ResolveWipMovieFullPath(projectId);
        var wipExists = wipFullPath is not null &&
                        File.Exists(wipFullPath) &&
                        new FileInfo(wipFullPath).Length >= 1024;

        if (wipExists)
        {
            var fi = new FileInfo(wipFullPath!);
            result.Exists = true;
            result.Path = Path.GetRelativePath(projectDir, wipFullPath!).Replace('\\', '/');
            result.Bytes = fi.Length;
            result.UpdatedAt = fi.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ss");
        }
        else
        {
            result.Exists = false;
            result.Path = "assets/movie_wip.mp4";
            try
            {
                var cfg = GetConfig(projectId);
                if (cfg.TryGetValue("wip_movie_path", out var w) &&
                    w.ValueKind == JsonValueKind.String &&
                    w.GetString() is { Length: > 0 } s)
                    result.Path = s.Replace('\\', '/').TrimStart('/');
            }
            catch { /* ignore */ }
        }

        var clipsByScene = IndexExactClipsByScene(videoDir);
        var blueprintScenes = GetBlueprintSceneNumbers(projectId);
        var scenesToRemux = ListScenesToRemuxForWip(projectId, clipsByScene, blueprintScenes);
        result.ScenesToRemux = scenesToRemux;

        // Stale = missing composite, clips newer, no .sources.json (legacy polluted remux),
        // or manifest clip set ≠ current exact/blueprint clips.
        var staleScenes = new List<int>();
        foreach (var sn in scenesToRemux)
        {
            if (IsSceneCompositeDirty(projectId, sn, videoDir, clipsByScene))
                staleScenes.Add(sn);
        }
        result.StaleScenes = staleScenes;

        // WIP sources = Stage 2 scene composites (blueprint-filtered when available)
        var currentSources = ListWipSourceFilesForProject(projectId, videoDir, blueprintScenes);
        result.CanBuild = scenesToRemux.Count > 0 || currentSources.Count > 0;

        if (!result.CanBuild)
        {
            result.Stale = true;
            result.Reason = "No scene or clip videos on disk to build WIP";
            return result;
        }

        // Stage 2 blueprint newer than last WIP build → always remux
        var bpPath = FindBlueprintPath(projectId);
        DateTime? bpMtime = bpPath is not null && File.Exists(bpPath)
            ? new FileInfo(bpPath).LastWriteTimeUtc
            : null;

        if (staleScenes.Count > 0)
        {
            result.Stale = true;
            result.Reason =
                $"Scene composite(s) dirty (missing/legacy/out of date): {string.Join(", ", staleScenes.Select(n => $"S{n:D2}"))}";
            return result;
        }

        if (!result.Exists || wipFullPath is null)
        {
            result.Stale = true;
            result.Reason = "WIP missing — needs rebuild";
            return result;
        }

        var manifestPath = FfmpegRemuxService.WipSourcesManifestPath(wipFullPath);
        if (bpMtime is DateTime bpm && File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (doc.RootElement.TryGetProperty("blueprintMtimeUtc", out var bm) &&
                    bm.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(bm.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var recordedBp))
                {
                    if (bpm > recordedBp.ToUniversalTime().AddSeconds(1))
                    {
                        result.Stale = true;
                        result.Reason = "Stage 2 blueprint changed since last WIP — remux all scenes + rebuild";
                        return result;
                    }
                }
                else if (doc.RootElement.TryGetProperty("builtAtUtc", out var built) &&
                         built.ValueKind == JsonValueKind.String &&
                         DateTime.TryParse(built.GetString(), null,
                             System.Globalization.DateTimeStyles.RoundtripKind, out var builtAt) &&
                         bpm > builtAt.ToUniversalTime().AddSeconds(1))
                {
                    result.Stale = true;
                    result.Reason = "Stage 2 blueprint newer than WIP — remux all scenes + rebuild";
                    return result;
                }

                // Scene list in plan vs last build
                if (doc.RootElement.TryGetProperty("sceneNumbers", out var sns) &&
                    sns.ValueKind == JsonValueKind.Array &&
                    blueprintScenes is { Count: > 0 })
                {
                    var recorded = sns.EnumerateArray()
                        .Select(e => e.TryGetInt32(out var n) ? n : 0)
                        .Where(n => n > 0)
                        .OrderBy(n => n)
                        .ToList();
                    var planned = blueprintScenes.OrderBy(n => n).ToList();
                    if (!recorded.SequenceEqual(planned))
                    {
                        result.Stale = true;
                        result.Reason = "Stage 2 scene list changed — remux all scenes + rebuild";
                        return result;
                    }
                }
            }
            catch { /* fall through */ }
        }
        else if (bpMtime is DateTime bpm2)
        {
            var wipMtime = new FileInfo(wipFullPath).LastWriteTimeUtc;
            if (bpm2 > wipMtime.AddSeconds(1))
            {
                result.Stale = true;
                result.Reason = "Stage 2 blueprint newer than WIP — remux all scenes + rebuild";
                return result;
            }
        }

        // Manifest: detect added/removed/replaced sources vs last successful WIP build
        var manifestMismatch = CompareWipSourcesManifest(wipFullPath, currentSources);
        if (manifestMismatch is { Length: > 0 })
        {
            result.Stale = true;
            result.Reason = manifestMismatch;
            return result;
        }

        // No manifest (old WIP): fall back to mtime — any source newer than WIP
        if (!File.Exists(manifestPath))
        {
            var wipMtime = new FileInfo(wipFullPath).LastWriteTimeUtc;
            foreach (var src in currentSources)
            {
                try
                {
                    if (new FileInfo(src).LastWriteTimeUtc > wipMtime.AddSeconds(1))
                    {
                        result.Stale = true;
                        result.Reason = "Sources newer than WIP (no build manifest — rebuild recommended)";
                        return result;
                    }
                }
                catch { /* ignore */ }
            }
        }

        result.Stale = false;
        result.Reason = "Up to date";
        return result;
    }

    /// <summary>
    /// True when scene should be remuxed before play/WIP.
    /// Marks legacy composites dirty (no scene_XX.mp4.sources.json) so polluted 1:10 cuts get rebuilt.
    /// </summary>
    public bool IsSceneCompositeDirty(
        string projectId,
        int sceneNum,
        string? videoDir = null,
        Dictionary<int, List<FileInfo>>? clipsByScene = null)
    {
        videoDir ??= Path.Combine(GetProjectDir(projectId), "assets", "video");
        clipsByScene ??= IndexExactClipsByScene(videoDir);

        var expectedNames = GetExpectedClipFileNames(projectId, sceneNum, videoDir, clipsByScene);
        if (expectedNames.Count == 0)
            return false;

        var composite = ResolveCompositePath(projectId, sceneNum);
        if (composite is null || !File.Exists(composite))
            return true;

        var maxClipMtime = DateTime.MinValue;
        foreach (var name in expectedNames)
        {
            var path = Path.Combine(videoDir, name);
            if (!File.Exists(path)) continue;
            var mt = new FileInfo(path).LastWriteTimeUtc;
            if (mt > maxClipMtime) maxClipMtime = mt;
        }
        if (maxClipMtime > new FileInfo(composite).LastWriteTimeUtc.AddSeconds(1))
            return true;

        var manifestPath = FfmpegRemuxService.SceneSourcesManifestPath(composite);
        var remuxOut = Path.Combine(videoDir, $"scene_{sceneNum:D2}.mp4");
        if (!File.Exists(manifestPath) && File.Exists(remuxOut))
            manifestPath = FfmpegRemuxService.SceneSourcesManifestPath(remuxOut);

        // No strict manifest → treat as dirty (old remux may have concat'd .native + orphans)
        if (!File.Exists(manifestPath))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("clips", out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
                return true;

            var recorded = new List<string>();
            foreach (var el in clipsEl.EnumerateArray())
            {
                if (el.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    recorded.Add(name);
            }

            var expectedSorted = expectedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var recSorted = recorded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!expectedSorted.SequenceEqual(recSorted, StringComparer.OrdinalIgnoreCase))
                return true;

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Exact clip file names that should make up the scene composite.</summary>
    private List<string> GetExpectedClipFileNames(
        string projectId,
        int sceneNum,
        string videoDir,
        Dictionary<int, List<FileInfo>> clipsByScene)
    {
        var allowed = TryBlueprintClipNumbers(projectId, sceneNum);
        var names = new List<string>();
        if (clipsByScene.TryGetValue(sceneNum, out var files))
        {
            foreach (var fi in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = fi.Name;
                if (!FfmpegRemuxService.IsExactClipFileName(name)) continue;
                if (allowed is { Count: > 0 })
                {
                    if (!int.TryParse(name.AsSpan(14, 2), out var cn) || !allowed.Contains(cn))
                        continue;
                }
                names.Add(name);
            }
        }

        // Also include expected blueprint slots that exist as exact files
        if (allowed is { Count: > 0 })
        {
            foreach (var cn in allowed.OrderBy(c => c))
            {
                var name = $"scene_{sceneNum:D2}_clip_{cn:D2}.mp4";
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var path = Path.Combine(videoDir, name);
                if (File.Exists(path) && new FileInfo(path).Length >= 1024)
                    names.Add(name);
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private HashSet<int>? TryBlueprintClipNumbers(string projectId, int sceneNum)
    {
        try
        {
            using var bp = LoadBlueprint(projectId);
            if (bp is null) return null;
            if (!bp.RootElement.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var s in scenes.EnumerateArray())
            {
                var sn = s.TryGetProperty("scene_number", out var snEl) && snEl.TryGetInt32(out var v) ? v : 0;
                if (sn != sceneNum) continue;
                if (!s.TryGetProperty("veo_clips", out var clips) || clips.ValueKind != JsonValueKind.Array)
                    return null;
                var set = new HashSet<int>();
                foreach (var c in clips.EnumerateArray())
                {
                    if (c.TryGetProperty("clip_number", out var cnEl) && cnEl.TryGetInt32(out var cn) && cn > 0)
                        set.Add(cn);
                }
                return set.Count > 0 ? set : null;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Stage 2 scene numbers from blueprint, or null if no plan.</summary>
    public List<int>? GetBlueprintSceneNumbers(string projectId)
    {
        try
        {
            using var bp = LoadBlueprint(projectId);
            if (bp is null) return null;
            if (!bp.RootElement.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<int>();
            foreach (var s in scenes.EnumerateArray())
            {
                if (s.TryGetProperty("scene_number", out var sn) && sn.TryGetInt32(out var n) && n > 0)
                    list.Add(n);
            }
            return list.Count > 0 ? list.Distinct().OrderBy(x => x).ToList() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Scenes to remux before WIP: Stage 2 order when blueprint exists, only those with clips on disk.
    /// </summary>
    public List<int> ListScenesToRemuxForWip(string projectId) =>
        ListScenesToRemuxForWip(
            projectId,
            IndexExactClipsByScene(Path.Combine(GetProjectDir(projectId), "assets", "video")),
            GetBlueprintSceneNumbers(projectId));

    private static List<int> ListScenesToRemuxForWip(
        string projectId,
        Dictionary<int, List<FileInfo>> clipsByScene,
        List<int>? blueprintScenes)
    {
        var withClips = clipsByScene.Keys.Where(k => clipsByScene[k].Count > 0).ToHashSet();
        if (blueprintScenes is { Count: > 0 })
            return blueprintScenes.Where(withClips.Contains).ToList();
        return withClips.OrderBy(n => n).ToList();
    }

    private static Dictionary<int, List<FileInfo>> IndexExactClipsByScene(string videoDir)
    {
        var clipsByScene = new Dictionary<int, List<FileInfo>>();
        if (!Directory.Exists(videoDir)) return clipsByScene;
        foreach (var f in Directory.EnumerateFiles(videoDir, "scene_*_clip_*.mp4"))
        {
            var name = Path.GetFileName(f);
            if (!FfmpegRemuxService.IsExactClipFileName(name)) continue;
            if (!int.TryParse(name.AsSpan(6, 2), out var sn) || sn <= 0) continue;
            var fi = new FileInfo(f);
            if (fi.Length < 1024) continue;
            if (!clipsByScene.TryGetValue(sn, out var list))
            {
                list = new List<FileInfo>();
                clipsByScene[sn] = list;
            }
            list.Add(fi);
        }
        return clipsByScene;
    }

    /// <summary>WIP concat inputs: scene composites for Stage 2 scenes only when blueprint exists.</summary>
    public List<string> ListWipSourceFilesForProject(string projectId)
    {
        var videoDir = Path.Combine(GetProjectDir(projectId), "assets", "video");
        return ListWipSourceFilesForProject(projectId, videoDir, GetBlueprintSceneNumbers(projectId));
    }

    private List<string> ListWipSourceFilesForProject(
        string projectId,
        string videoDir,
        List<int>? blueprintScenes)
    {
        if (!Directory.Exists(videoDir))
            return new List<string>();

        if (blueprintScenes is { Count: > 0 })
        {
            var list = new List<string>();
            foreach (var sn in blueprintScenes)
            {
                var path = ResolveCompositePath(projectId, sn);
                if (path is not null)
                    list.Add(path);
            }
            if (list.Count > 0)
                return list;
        }

        return FfmpegRemuxService.ListWipSourceFiles(videoDir);
    }

    /// <summary>Full path for WIP file from config (may not exist yet).</summary>
    public string ResolveWipMovieFullPath(string projectId)
    {
        var projectDir = GetProjectDir(projectId);
        var cfg = GetConfig(projectId);
        var wipRel = "assets/movie_wip.mp4";
        if (cfg.TryGetValue("wip_movie_path", out var w) &&
            w.ValueKind == JsonValueKind.String &&
            w.GetString() is { Length: > 0 } s)
            wipRel = s.Replace('\\', '/').TrimStart('/');

        if (wipRel.Contains("..", StringComparison.Ordinal))
            return Path.Combine(projectDir, "assets", "movie_wip.mp4");

        return Path.IsPathRooted(wipRel)
            ? wipRel
            : Path.GetFullPath(Path.Combine(projectDir, wipRel.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// null if match or no check; reason string if sources added/removed/changed vs last WIP build.
    /// </summary>
    private static string? CompareWipSourcesManifest(string wipPath, IReadOnlyList<string> currentSources)
    {
        var manifestPath = FfmpegRemuxService.WipSourcesManifestPath(wipPath);
        if (!File.Exists(manifestPath))
            return null; // caller uses mtime fallback

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("sources", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return "WIP manifest invalid — rebuild needed";

            var recorded = new List<(string Name, long Bytes, DateTime Mtime)>();
            foreach (var el in arr.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                long bytes = 0;
                if (el.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bl))
                    bytes = bl;
                var mtime = DateTime.MinValue;
                if (el.TryGetProperty("mtimeUtc", out var m) &&
                    m.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(m.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var mt))
                    mtime = mt.ToUniversalTime();
                recorded.Add((name, bytes, mtime));
            }

            var current = currentSources
                .Select(f =>
                {
                    var fi = new FileInfo(f);
                    return (Name: fi.Name, Bytes: fi.Length, Mtime: fi.LastWriteTimeUtc);
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var recSorted = recorded
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var curNames = current.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recNames = recSorted.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = curNames.Except(recNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var removed = recNames.Except(curNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

            if (added.Count > 0 || removed.Count > 0)
            {
                var parts = new List<string>();
                if (added.Count > 0)
                    parts.Add("added " + string.Join(", ", added));
                if (removed.Count > 0)
                    parts.Add("removed " + string.Join(", ", removed));
                return "Scene set changed (" + string.Join("; ", parts) + ")";
            }

            foreach (var c in current)
            {
                var r = recSorted.FirstOrDefault(x =>
                    string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                if (r.Name is null) continue;
                if (c.Bytes != r.Bytes ||
                    Math.Abs((c.Mtime - r.Mtime).TotalSeconds) > 1.5)
                    return $"Source changed: {c.Name}";
            }

            if (current.Count != recSorted.Count)
                return $"Source count {current.Count} vs last build {recSorted.Count}";

            return null;
        }
        catch
        {
            return "WIP manifest unreadable — rebuild needed";
        }
    }

    private static bool HasCompositeFile(
        Dictionary<string, long> videoIndex,
        Dictionary<string, long> scenesIndex,
        int sceneNumber)
    {
        foreach (var name in new[]
                 {
                     $"scene_{sceneNumber:D2}.mp4",
                     $"scene_{sceneNumber:D2}_complete.mp4",
                 })
        {
            if (videoIndex.TryGetValue(name, out var v) && v >= 1024) return true;
            if (scenesIndex.TryGetValue(name, out var s) && s >= 1024) return true;
        }
        return false;
    }

    private static Dictionary<string, long> IndexDirFiles(string dir)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
            return map;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var info = new FileInfo(f);
                    map[info.Name] = info.Length;
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
        return map;
    }

    private static bool ClipOnDisk(Dictionary<string, long> videoIndex, int scene, int clip)
    {
        var name = $"scene_{scene:D2}_clip_{clip:D2}.mp4";
        return videoIndex.TryGetValue(name, out var sz) && sz >= 1024;
    }

    private Dictionary<string, JsonElement> LoadCharacterSeeds(string projectId)
    {
        // Prefer blueprint, then scenes.json — case-insensitive keys
        try
        {
            using var bp = LoadBlueprint(projectId);
            if (bp is not null &&
                bp.RootElement.TryGetProperty("global_production_variables", out var gpv) &&
                gpv.TryGetProperty("character_seed_tokens", out var seeds) &&
                seeds.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in seeds.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                if (dict.Count > 0)
                    return dict;
            }
        }
        catch { /* fall through */ }

        var scenesPath = Path.Combine(GetProjectDir(projectId), "scenes.json");
        var alt = Path.Combine(GetProjectDir(projectId), "nickandme.scenes.json");
        var path = File.Exists(scenesPath) ? scenesPath : (File.Exists(alt) ? alt : null);
        if (path is null)
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("global_production_variables", out var g2) &&
            g2.TryGetProperty("character_seed_tokens", out var s2) &&
            s2.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in s2.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
            return dict;
        }
        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetSeed(
        Dictionary<string, JsonElement> seeds,
        string charKey,
        out JsonElement info) =>
        seeds.TryGetValue(charKey, out info);

    private static bool IsVoiceOnly(string key, JsonElement info)
    {
        if (key.Contains("Narrator", StringComparison.OrdinalIgnoreCase))
            return true;
        if (info.TryGetProperty("display_name_policy", out var pol))
        {
            var p = pol.GetString() ?? "";
            if (p.Contains("never", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }



    private string ResolveWorkspaceRoot()
    {
        if (!string.IsNullOrWhiteSpace(_opts.WorkspaceRoot) &&
            Directory.Exists(_opts.WorkspaceRoot))
        {
            return Path.GetFullPath(_opts.WorkspaceRoot);
        }

        // host/FilmStudio.Engine → host → repo
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "projects")) &&
                Directory.Exists(Path.Combine(dir.FullName, "renderer")))
            {
                return dir.FullName;
            }
            // running from host/FilmStudio.Api/bin/...
            if (dir.Name.Equals("host", StringComparison.OrdinalIgnoreCase) &&
                dir.Parent is not null &&
                Directory.Exists(Path.Combine(dir.Parent.FullName, "projects")))
            {
                return dir.Parent.FullName;
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
