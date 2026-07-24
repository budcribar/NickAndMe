using System.Text.Json;
using PageToMovie.Core.Models;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>Project edit/feedback log (edit_feedback_log.json) + clip/scene review state.</summary>
public sealed class EditLogService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ProjectStore _projects;
    private readonly ReviewEventStore _learning;
    private readonly ILogger<EditLogService> _log;

    public EditLogService(
        ProjectStore projects,
        ReviewEventStore learning,
        ILogger<EditLogService> log)
    {
        _projects = projects;
        _learning = learning;
        _log = log;
    }

    public async Task<EditLogDocument> LoadAsync(string projectId, CancellationToken ct = default)
    {
        var path = await LogPathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path))
            return new EditLogDocument();
        try
        {
            await using var stream = File.OpenRead(path);
            var doc = await JsonSerializer.DeserializeAsync<EditLogDocument>(stream, JsonOpts, ct)
                .ConfigureAwait(false);
            return doc ?? new EditLogDocument();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load edit log for {Project}", projectId);
            return new EditLogDocument();
        }
    }

    public async Task SaveAsync(string projectId, EditLogDocument doc, CancellationToken ct = default)
    {
        var path = await LogPathAsync(projectId, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(doc, JsonOpts) + "\n";
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task<EditLogEntry> AddAsync(
        string projectId,
        string entryType,
        string userNote,
        int? scene = null,
        int? clip = null,
        string? character = null,
        string actionTaken = "",
        string before = "",
        string after = "",
        string learningLayer = "clip",
        string? category = null,
        string? suggestion = null,
        string? confidence = null,
        string? continuity = null,
        int? suggestionCount = null,
        string? field = null,
        string? jobId = null,
        string? outcome = null,
        string? userId = null,
        CancellationToken ct = default)
    {
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        var entry = new EditLogEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Type = entryType,
            LearningLayer = learningLayer,
            Scene = scene,
            Clip = clip,
            Character = character,
            UserNote = (userNote ?? "").Trim(),
            ActionTaken = actionTaken,
            Before = before,
            After = after,
            SuggestedRule = SuggestRule(entryType, userNote, scene, clip, character),
        };
        doc.Entries.Insert(0, entry);
        await SaveAsync(projectId, doc, ct).ConfigureAwait(false);

        // Host-level learning stream (P0)
        try
        {
            // Parse category from actionTaken when not explicit (e.g. auto_review)
            var cat = category;
            var sug = suggestion;
            var conf = confidence;
            if (cat is null && actionTaken.Contains("category=", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in actionTaken.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (part.StartsWith("category=", StringComparison.OrdinalIgnoreCase))
                        cat = part["category=".Length..];
                    else if (part.StartsWith("suggestion=", StringComparison.OrdinalIgnoreCase))
                        sug ??= part["suggestion=".Length..];
                    else if (part.StartsWith("confidence=", StringComparison.OrdinalIgnoreCase))
                        conf ??= part["confidence=".Length..];
                }
            }

            _learning.AppendFromEditLog(
                projectId,
                entry,
                userId: userId,
                category: cat,
                suggestion: sug,
                confidence: conf,
                continuity: continuity,
                suggestionCount: suggestionCount,
                field: field,
                jobId: jobId,
                outcome: outcome);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "learning event mirror skip");
        }

        return entry;
    }

    public async Task<EditLogEntry?> GetAsync(
        string projectId,
        string entryId,
        CancellationToken ct = default)
    {
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        return doc.Entries.FirstOrDefault(e =>
            string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Minimum length for an override note when human-pass overrides auto-review fail.
    /// Bare "pass" / "ok" is not enough to ship a failed clip.
    /// </summary>
    public const int MinAutoFailOverrideNoteLength = 12;

    /// <summary>Pass/fail clip review status in pipeline_state.json + edit log.</summary>
    public async Task SetClipReviewAsync(
        string projectId,
        int scene,
        int clip,
        string status,
        string note = "",
        CancellationToken ct = default)
    {
        status = status.Trim().ToLowerInvariant();
        if (status is not ("pass" or "fail" or "pending"))
            throw new InvalidOperationException("status must be pass|fail|pending");

        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var statePath = Path.Combine(dir, "pipeline_state.json");
        var state = await LoadStateAsync(statePath, ct).ConfigureAwait(false);
        var key = $"S{scene:D2}C{clip:D2}";
        note ??= "";

        // Assembly gate: human pass after auto-review fail requires a real override reason.
        var auto = ReadAutoReviewRow(state, key);
        var autoFail = IsAutoFailSuggestion(auto.Suggestion);
        var overrodeAutoFail = false;
        if (status == "pass" && autoFail)
        {
            var trimmed = note.Trim();
            if (!IsValidAutoFailOverrideNote(trimmed))
            {
                throw new InvalidOperationException(
                    $"S{scene:D2}C{clip:D2}: auto-review failed ({auto.Suggestion}/{auto.Category}). " +
                    "Pass requires an explicit override reason " +
                    $"(≥{MinAutoFailOverrideNoteLength} characters, not just \"pass\"/\"ok\"). " +
                    "Example: why the fail is acceptable for the cut.");
            }
            overrodeAutoFail = true;
        }

        if (!state.TryGetValue("clip_review", out var cr) || cr is not Dictionary<string, object?> reviews)
        {
            reviews = new Dictionary<string, object?>();
            state["clip_review"] = reviews;
        }
        reviews[key] = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["note"] = note,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["overrode_auto_fail"] = overrodeAutoFail,
            ["auto_suggestion"] = auto.Suggestion ?? "",
            ["auto_category"] = auto.Category ?? "",
        };
        await SaveStateAsync(statePath, state, ct).ConfigureAwait(false);

        await AddAsync(
            projectId,
            status == "pass" ? "clip_pass" : status == "fail" ? "clip_fail" : "clip_review",
            note is { Length: > 0 } ? note : status,
            scene: scene,
            clip: clip,
            actionTaken: overrodeAutoFail
                ? $"review_status={status};overrode_auto_fail=true"
                : $"review_status={status}",
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drop a deleted clip's human/auto review state from pipeline_state.json so a stale
    /// pass/fail doesn't linger if the clip number is regenerated later. Logs a
    /// <c>clip_delete</c> edit-log entry for the audit trail.
    /// </summary>
    public async Task RemoveClipReviewStateAsync(
        string projectId,
        int scene,
        int clip,
        CancellationToken ct = default)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var statePath = Path.Combine(dir, "pipeline_state.json");
        var state = await LoadStateAsync(statePath, ct).ConfigureAwait(false);
        var key = $"S{scene:D2}C{clip:D2}";
        var changed = false;

        if (state.TryGetValue("clip_review", out var cr) && cr is Dictionary<string, object?> reviews)
            changed |= reviews.Remove(key);
        if (state.TryGetValue("clip_auto_review", out var car) && car is Dictionary<string, object?> autos)
            changed |= autos.Remove(key);

        if (changed)
            await SaveStateAsync(statePath, state, ct).ConfigureAwait(false);

        await AddAsync(
            projectId,
            "clip_delete",
            "Clip deleted",
            scene: scene,
            clip: clip,
            actionTaken: "clip_deleted",
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>Persist last auto-review suggestion for assembly-gate decisions.</summary>
    public async Task RecordAutoReviewAsync(
        string projectId,
        int scene,
        int clip,
        string suggestion,
        string category = "",
        string note = "",
        CancellationToken ct = default)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var statePath = Path.Combine(dir, "pipeline_state.json");
        var state = await LoadStateAsync(statePath, ct).ConfigureAwait(false);
        var key = $"S{scene:D2}C{clip:D2}";
        if (!state.TryGetValue("clip_auto_review", out var car) || car is not Dictionary<string, object?> autos)
        {
            autos = new Dictionary<string, object?>();
            state["clip_auto_review"] = autos;
        }
        autos[key] = new Dictionary<string, object?>
        {
            ["suggestion"] = (suggestion ?? "").Trim().ToLowerInvariant(),
            ["category"] = category ?? "",
            ["note"] = note ?? "",
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        await SaveStateAsync(statePath, state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a clip may enter scene remux / WIP. Fails and unresolved auto-fails are blocked;
    /// human pass (including override of auto-fail with note) is allowed.
    /// </summary>
    public bool IsClipEligibleForAssembly(
        string projectId,
        int scene,
        int clip,
        out string blockReason)
    {
        blockReason = "";
        try
        {
            var dir = _projects.GetProjectDir(projectId);
            var statePath = Path.Combine(dir, "pipeline_state.json");
            var state = LoadStateSync(statePath);
            var key = $"S{scene:D2}C{clip:D2}";
            var human = ReadHumanReviewRow(state, key);
            var auto = ReadAutoReviewRow(state, key);
            // Also check on-disk auto_review draft when pipeline_state has no row
            if (string.IsNullOrWhiteSpace(auto.Suggestion))
                auto = TryReadAutoReviewDraft(projectId, scene, clip);

            if (string.Equals(human.Status, "fail", StringComparison.OrdinalIgnoreCase))
            {
                blockReason = "human review = fail" +
                    (string.IsNullOrWhiteSpace(human.Note) ? "" : $": {human.Note.Trim()}");
                return false;
            }

            if (IsAutoFailSuggestion(auto.Suggestion))
            {
                // Must have explicit human pass that overrode auto-fail (or pass with valid override note)
                if (!string.Equals(human.Status, "pass", StringComparison.OrdinalIgnoreCase))
                {
                    blockReason =
                        $"auto-review fail ({auto.Suggestion}/{auto.Category}) — not override-passed";
                    return false;
                }

                if (!human.OverrodeAutoFail && !IsValidAutoFailOverrideNote(human.Note))
                {
                    blockReason =
                        $"auto-review fail ({auto.Suggestion}/{auto.Category}) — pass lacks override reason";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Assembly eligibility check failed S{Scene}C{Clip}", scene, clip);
            // Fail closed only when we cannot read state — safer for shipping cuts
            blockReason = "could not read review state";
            return false;
        }
    }

    private (string Suggestion, string Category, string Note) TryReadAutoReviewDraft(
        string projectId, int scene, int clip)
    {
        try
        {
            var path = Path.Combine(
                _projects.GetProjectDir(projectId),
                "assets", "review",
                $"S{scene:D2}C{clip:D2}.auto_review.json");
            if (!File.Exists(path)) return ("", "", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var suggestion = root.TryGetProperty("suggestion", out var s) ? s.GetString() ?? ""
                : root.TryGetProperty("Suggestion", out var s2) ? s2.GetString() ?? "" : "";
            var category = root.TryGetProperty("category", out var c) ? c.GetString() ?? ""
                : root.TryGetProperty("Category", out var c2) ? c2.GetString() ?? "" : "";
            var note = root.TryGetProperty("note", out var n) ? n.GetString() ?? ""
                : root.TryGetProperty("Note", out var n2) ? n2.GetString() ?? "" : "";
            return (suggestion, category, note);
        }
        catch
        {
            return ("", "", "");
        }
    }

    /// <summary>List blocked clips that exist as files under assets/video for this project.</summary>
    public IReadOnlyList<AssemblyBlockedClip> ListBlockedClipsOnDisk(string projectId)
    {
        var list = new List<AssemblyBlockedClip>();
        try
        {
            var videoDir = Path.Combine(_projects.GetProjectDir(projectId), "assets", "video");
            if (!Directory.Exists(videoDir)) return list;
            foreach (var fi in new DirectoryInfo(videoDir).EnumerateFiles("scene_*_clip_*.mp4"))
            {
                if (!FfmpegRemuxService.IsExactClipFileName(fi.Name)) continue;
                if (fi.Length < 1024) continue;
                // scene_01_clip_02.mp4
                var parts = Path.GetFileNameWithoutExtension(fi.Name).Split('_');
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[1], out var sn) || !int.TryParse(parts[3], out var cn))
                    continue;
                if (!IsClipEligibleForAssembly(projectId, sn, cn, out var reason))
                    list.Add(new AssemblyBlockedClip(sn, cn, reason, fi.FullName));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ListBlockedClipsOnDisk failed for {Project}", projectId);
        }

        return list.OrderBy(x => x.Scene).ThenBy(x => x.Clip).ToList();
    }

    public static bool IsValidAutoFailOverrideNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return false;
        var t = note.Trim();
        if (t.Length < MinAutoFailOverrideNoteLength) return false;
        if (t.Equals("pass", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Equals("ok", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Equals("okay", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Equals("lgtm", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Equals("fine", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Equals("ship it", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static bool IsAutoFailSuggestion(string? suggestion) =>
        string.Equals(suggestion?.Trim(), "fail", StringComparison.OrdinalIgnoreCase);

    private static (string Status, string Note, bool OverrodeAutoFail) ReadHumanReviewRow(
        Dictionary<string, object?> state, string key)
    {
        if (!state.TryGetValue("clip_review", out var cr) || cr is not Dictionary<string, object?> reviews)
            return ("", "", false);
        if (!reviews.TryGetValue(key, out var row) || row is not Dictionary<string, object?> d)
            return ("", "", false);
        var status = d.TryGetValue("status", out var st) ? st?.ToString() ?? "" : "";
        var note = d.TryGetValue("note", out var n) ? n?.ToString() ?? "" : "";
        var over = false;
        if (d.TryGetValue("overrode_auto_fail", out var o))
        {
            over = o is bool b && b
                || (o is string s && bool.TryParse(s, out var pb) && pb)
                || string.Equals(o?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }
        return (status, note, over);
    }

    private static (string Suggestion, string Category, string Note) ReadAutoReviewRow(
        Dictionary<string, object?> state, string key)
    {
        // Prefer pipeline_state clip_auto_review; fall back to draft file is caller's job
        if (state.TryGetValue("clip_auto_review", out var car) &&
            car is Dictionary<string, object?> autos &&
            autos.TryGetValue(key, out var row) &&
            row is Dictionary<string, object?> d)
        {
            return (
                d.TryGetValue("suggestion", out var s) ? s?.ToString() ?? "" : "",
                d.TryGetValue("category", out var c) ? c?.ToString() ?? "" : "",
                d.TryGetValue("note", out var n) ? n?.ToString() ?? "" : "");
        }

        return ("", "", "");
    }

    private static Dictionary<string, object?> LoadStateSync(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return GrokChatClient.ParseJsonObject(File.ReadAllText(path));
        }
        catch
        {
            return new();
        }
    }

    public async Task MarkSceneApprovedAsync(
        string projectId,
        int scene,
        string note = "",
        CancellationToken ct = default)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var statePath = Path.Combine(dir, "pipeline_state.json");
        var state = await LoadStateAsync(statePath, ct).ConfigureAwait(false);
        if (!state.TryGetValue("scene_review", out var sr) || sr is not Dictionary<string, object?> scenes)
        {
            scenes = new Dictionary<string, object?>();
            state["scene_review"] = scenes;
        }
        scenes[$"S{scene:D2}"] = new Dictionary<string, object?>
        {
            ["status"] = "approved",
            ["note"] = note ?? "",
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        await SaveStateAsync(statePath, state, ct).ConfigureAwait(false);
        await AddAsync(
            projectId,
            "scene_approve",
            note is { Length: > 0 } ? note : "Approved",
            scene: scene,
            actionTaken: "scene_review=approved",
            ct: ct).ConfigureAwait(false);
    }

    public async Task MarkSceneDirtyAsync(
        string projectId,
        int scene,
        string reason,
        string layer = "stage2",
        CancellationToken ct = default)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var statePath = Path.Combine(dir, "pipeline_state.json");
        var state = await LoadStateAsync(statePath, ct).ConfigureAwait(false);
        if (!state.TryGetValue("dirty_scenes", out var ds) || ds is not Dictionary<string, object?> dirty)
        {
            dirty = new Dictionary<string, object?>();
            state["dirty_scenes"] = dirty;
        }
        dirty[$"S{scene:D2}"] = new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["layer"] = layer,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        await SaveStateAsync(statePath, state, ct).ConfigureAwait(false);
        await AddAsync(
            projectId,
            "scene_dirty",
            reason,
            scene: scene,
            actionTaken: $"dirty layer={layer}",
            learningLayer: layer,
            ct: ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> GetClipReviewMapAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var statePath = Path.Combine(dir, "pipeline_state.json");
        var state = await LoadStateAsync(statePath, ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (state.TryGetValue("clip_review", out var cr) && cr is Dictionary<string, object?> reviews)
        {
            foreach (var (k, v) in reviews)
            {
                if (v is Dictionary<string, object?> row &&
                    row.TryGetValue("status", out var st) && st is not null)
                    map[k] = st.ToString() ?? "";
            }
        }
        return map;
    }

    private async Task<string> LogPathAsync(string projectId, CancellationToken ct)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        return Path.Combine(dir, "edit_feedback_log.json");
    }

    private static async Task<Dictionary<string, object?>> LoadStateAsync(
        string path,
        CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return GrokChatClient.ParseJsonObject(text);
        }
        catch { return new(); }
    }

    private static async Task SaveStateAsync(
        string path,
        Dictionary<string, object?> state,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(state, JsonOpts) + "\n";
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    private static string SuggestRule(
        string entryType,
        string? note,
        int? scene,
        int? clip,
        string? character)
    {
        var loc = scene is int s
            ? (clip is int c ? $"S{s:D2}C{c}" : $"S{s:D2}")
            : character is { Length: > 0 } ? $"character {character}" : "general";
        if (entryType.Contains("fail", StringComparison.OrdinalIgnoreCase))
            return $"Review fail at {loc}: {(note ?? "").Trim()}".Trim();
        if (entryType.Contains("pass", StringComparison.OrdinalIgnoreCase))
            return $"Review pass at {loc}";
        return $"Note at {loc}: {(note ?? "").Trim()}".Trim();
    }
}

/// <summary>Clip blocked from scene remux / WIP by the assembly review gate.</summary>
public readonly record struct AssemblyBlockedClip(int Scene, int Clip, string Reason, string Path);
