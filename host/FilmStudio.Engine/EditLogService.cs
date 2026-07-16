using System.Text.Json;
using FilmStudio.Core.Models;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

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
    private readonly ILogger<EditLogService> _log;

    public EditLogService(ProjectStore projects, ILogger<EditLogService> log)
    {
        _projects = projects;
        _log = log;
    }

    public EditLogDocument Load(string projectId)
    {
        var path = LogPath(projectId);
        if (!File.Exists(path))
            return new EditLogDocument();
        try
        {
            var doc = JsonSerializer.Deserialize<EditLogDocument>(File.ReadAllText(path), JsonOpts);
            return doc ?? new EditLogDocument();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load edit log for {Project}", projectId);
            return new EditLogDocument();
        }
    }

    public void Save(string projectId, EditLogDocument doc)
    {
        var path = LogPath(projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts) + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    public EditLogEntry Add(
        string projectId,
        string entryType,
        string userNote,
        int? scene = null,
        int? clip = null,
        string? character = null,
        string actionTaken = "",
        string before = "",
        string after = "",
        string learningLayer = "clip")
    {
        var doc = Load(projectId);
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
        Save(projectId, doc);
        return entry;
    }

    public EditLogEntry? Get(string projectId, string entryId) =>
        Load(projectId).Entries.FirstOrDefault(e =>
            string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pass/fail clip review status in pipeline_state.json + edit log.</summary>
    public void SetClipReview(
        string projectId,
        int scene,
        int clip,
        string status,
        string note = "")
    {
        status = status.Trim().ToLowerInvariant();
        if (status is not ("pass" or "fail" or "pending"))
            throw new InvalidOperationException("status must be pass|fail|pending");

        var statePath = Path.Combine(_projects.GetProjectDir(projectId), "pipeline_state.json");
        var state = LoadState(statePath);
        var key = $"S{scene:D2}C{clip:D2}";
        if (!state.TryGetValue("clip_review", out var cr) || cr is not Dictionary<string, object?> reviews)
        {
            reviews = new Dictionary<string, object?>();
            state["clip_review"] = reviews;
        }
        reviews[key] = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["note"] = note ?? "",
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        SaveState(statePath, state);

        Add(projectId,
            status == "pass" ? "clip_pass" : status == "fail" ? "clip_fail" : "clip_review",
            note is { Length: > 0 } ? note : status,
            scene: scene,
            clip: clip,
            actionTaken: $"review_status={status}");
    }

    public void MarkSceneApproved(string projectId, int scene, string note = "")
    {
        var statePath = Path.Combine(_projects.GetProjectDir(projectId), "pipeline_state.json");
        var state = LoadState(statePath);
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
        SaveState(statePath, state);
        Add(projectId, "scene_approve", note is { Length: > 0 } ? note : "Approved",
            scene: scene, actionTaken: "scene_review=approved");
    }

    public void MarkSceneDirty(string projectId, int scene, string reason, string layer = "stage2")
    {
        var statePath = Path.Combine(_projects.GetProjectDir(projectId), "pipeline_state.json");
        var state = LoadState(statePath);
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
        SaveState(statePath, state);
        Add(projectId, "scene_dirty", reason, scene: scene, actionTaken: $"dirty layer={layer}",
            learningLayer: layer);
    }

    public Dictionary<string, string> GetClipReviewMap(string projectId)
    {
        var statePath = Path.Combine(_projects.GetProjectDir(projectId), "pipeline_state.json");
        var state = LoadState(statePath);
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

    private string LogPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "edit_feedback_log.json");

    private static Dictionary<string, object?> LoadState(string path)
    {
        if (!File.Exists(path)) return new();
        try { return GrokChatClient.ParseJsonObject(File.ReadAllText(path)); }
        catch { return new(); }
    }

    private static void SaveState(string path, Dictionary<string, object?> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts) + "\n");
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
