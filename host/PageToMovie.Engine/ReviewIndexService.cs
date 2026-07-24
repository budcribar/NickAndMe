using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Durable project review index: <c>assets/review/index.json</c> — one row per on-disk clip
/// with auto/human status, assembly eligibility, and durable frame paths (PR3).
/// </summary>
public sealed class ReviewIndexService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly Regex ExactClipNameRe = new(
        @"^scene_(\d{2})_clip_(\d{2})\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ProjectStore _projects;
    private readonly EditLogService _editLogs;
    private readonly ILogger<ReviewIndexService> _log;

    public ReviewIndexService(
        ProjectStore projects,
        EditLogService editLogs,
        ILogger<ReviewIndexService> log)
    {
        _projects = projects;
        _editLogs = editLogs;
        _log = log;
    }

    public string IndexPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "assets", "review", "index.json");

    public string FramesDir(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "assets", "review", "frames");

    public string DraftRelPath(int scene, int clip) =>
        $"assets/review/S{scene:D2}C{clip:D2}.auto_review.json";

    public string FrameRelPath(int scene, int clip, int frameIndex) =>
        $"assets/review/frames/S{scene:D2}C{clip:D2}_{frameIndex:D2}.jpg";

    public ReviewIndexDocument? Load(string projectId)
    {
        var path = IndexPath(projectId);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ReviewIndexDocument>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load review index for {ProjectId}", projectId);
            return null;
        }
    }

    public void Save(ReviewIndexDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.ProjectId))
            throw new ArgumentException("projectId required", nameof(doc));
        var path = IndexPath(doc.ProjectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc.BuiltAtUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts) + "\n");
    }

    /// <summary>Scan on-disk clips and rebuild full index (drafts, human, assembly, frames).</summary>
    public ReviewIndexDocument Rebuild(string projectId, int? sceneFilter = null)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        var clips = ListOnDiskClips(projectDir, sceneFilter);
        var doc = new ReviewIndexDocument
        {
            ProjectId = projectId,
            SchemaVersion = "1",
            BuiltAtUtc = DateTimeOffset.UtcNow,
        };

        foreach (var (scene, clip) in clips)
        {
            doc.Clips.Add(BuildRow(projectId, projectDir, scene, clip));
        }

        Save(doc);
        return doc;
    }

    /// <summary>Upsert one clip after auto-review (or frame persist).</summary>
    public ReviewIndexDocument UpsertClip(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<string>? durableFrameRelPaths = null,
        ClipAutoReviewDraft? draft = null)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        var doc = Load(projectId) ?? new ReviewIndexDocument
        {
            ProjectId = projectId,
            SchemaVersion = "1",
        };

        var row = BuildRow(projectId, projectDir, scene, clip, draft, durableFrameRelPaths);
        var key = row.Key;
        var idx = doc.Clips.FindIndex(c =>
            string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            doc.Clips[idx] = row;
        else
            doc.Clips.Add(row);

        doc.Clips = doc.Clips
            .OrderBy(c => c.Scene)
            .ThenBy(c => c.Clip)
            .ToList();
        Save(doc);
        return doc;
    }

    /// <summary>
    /// Remove one clip's row (if present) plus its auto-review draft and durable frames.
    /// Unlike <see cref="Rebuild"/>, this only touches the one row — other scenes/clips in
    /// the index are left untouched.
    /// </summary>
    public void RemoveClip(string projectId, int scene, int clip)
    {
        var key = $"S{scene:D2}C{clip:D2}";
        var doc = Load(projectId);
        if (doc is not null)
        {
            var removed = doc.Clips.RemoveAll(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                Save(doc);
        }

        var draftAbs = Path.Combine(_projects.GetProjectDir(projectId),
            DraftRelPath(scene, clip).Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(draftAbs))
        {
            try { File.Delete(draftAbs); } catch { /* best effort */ }
        }

        var framesDir = FramesDir(projectId);
        if (Directory.Exists(framesDir))
        {
            var prefix = $"{key}_";
            foreach (var f in Directory.EnumerateFiles(framesDir, prefix + "*.jpg"))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>On-disk (scene, clip) pairs under assets/video, optional scene filter.</summary>
    public IReadOnlyList<(int Scene, int Clip)> ListOnDiskClipCoords(
        string projectId,
        int? sceneFilter = null)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        return ListOnDiskClips(projectDir, sceneFilter);
    }

    /// <summary>Whether a draft file exists for this clip.</summary>
    public bool HasDraft(string projectId, int scene, int clip)
    {
        var path = Path.Combine(
            _projects.GetProjectDir(projectId),
            "assets", "review",
            $"S{scene:D2}C{clip:D2}.auto_review.json");
        return File.Exists(path);
    }

    /// <summary>
    /// Copy current-clip sample frames into durable <c>assets/review/frames/</c>.
    /// Returns project-relative paths (forward slashes).
    /// </summary>
    public IReadOnlyList<string> PersistDurableFrames(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<string> sourceFramePaths,
        int maxFrames = 4)
    {
        if (sourceFramePaths is null || sourceFramePaths.Count == 0)
            return Array.Empty<string>();

        var framesDir = FramesDir(projectId);
        Directory.CreateDirectory(framesDir);

        // Clear prior durable frames for this clip
        var prefix = $"S{scene:D2}C{clip:D2}_";
        try
        {
            foreach (var old in Directory.EnumerateFiles(framesDir, prefix + "*.jpg"))
            {
                try { File.Delete(old); } catch { /* best effort */ }
            }
        }
        catch { /* */ }

        var rel = new List<string>();
        var n = 0;
        foreach (var src in sourceFramePaths.Take(Math.Clamp(maxFrames, 1, 8)))
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) continue;
            n++;
            var relPath = FrameRelPath(scene, clip, n);
            var dest = Path.Combine(_projects.GetProjectDir(projectId),
                relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            rel.Add(relPath.Replace('\\', '/'));
        }

        return rel;
    }

    public IReadOnlyList<string> ListExistingFrameRelPaths(string projectId, int scene, int clip)
    {
        var framesDir = FramesDir(projectId);
        if (!Directory.Exists(framesDir)) return Array.Empty<string>();
        var prefix = $"S{scene:D2}C{clip:D2}_";
        return Directory.EnumerateFiles(framesDir, prefix + "*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => ("assets/review/frames/" + Path.GetFileName(f)).Replace('\\', '/'))
            .ToList();
    }

    private ReviewIndexClipRow BuildRow(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        ClipAutoReviewDraft? draft = null,
        IReadOnlyList<string>? durableFrameRelPaths = null)
    {
        var key = $"S{scene:D2}C{clip:D2}";
        var videoRel = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
        var videoAbs = Path.Combine(projectDir, "assets", "video",
            $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        var videoExists = File.Exists(videoAbs) && new FileInfo(videoAbs).Length >= 512;

        draft ??= TryLoadDraft(projectDir, scene, clip);
        var draftRel = DraftRelPath(scene, clip);
        var draftAbs = Path.Combine(projectDir, draftRel.Replace('/', Path.DirectorySeparatorChar));
        var hasDraft = draft is not null || File.Exists(draftAbs);

        var human = ReadHumanReview(projectDir, key);
        var eligible = _editLogs.IsClipEligibleForAssembly(projectId, scene, clip, out var blockReason);

        var frames = durableFrameRelPaths is { Count: > 0 }
            ? durableFrameRelPaths.Select(p => p.Replace('\\', '/')).ToList()
            : ListExistingFrameRelPaths(projectId, scene, clip).ToList();

        return new ReviewIndexClipRow
        {
            Key = key,
            Scene = scene,
            Clip = clip,
            VideoPath = videoRel.Replace('\\', '/'),
            VideoExists = videoExists,
            AutoSuggestion = draft?.Suggestion,
            AutoCategory = draft?.Category,
            AutoNote = draft?.Note,
            AutoReviewedAt = draft?.GeneratedAt,
            HumanStatus = string.IsNullOrWhiteSpace(human.Status) ? null : human.Status,
            HumanNote = string.IsNullOrWhiteSpace(human.Note) ? null : human.Note,
            AssemblyEligible = eligible,
            AssemblyBlockReason = eligible ? null : blockReason,
            DraftPath = hasDraft ? draftRel.Replace('\\', '/') : null,
            HasDraft = hasDraft,
            FramePaths = frames,
        };
    }

    private static ClipAutoReviewDraft? TryLoadDraft(string projectDir, int scene, int clip)
    {
        var path = Path.Combine(projectDir, "assets", "review",
            $"S{scene:D2}C{clip:D2}.auto_review.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClipAutoReviewDraft>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            return null;
        }
    }

    private static (string Status, string Note) ReadHumanReview(string projectDir, string key)
    {
        try
        {
            var statePath = Path.Combine(projectDir, "pipeline_state.json");
            if (!File.Exists(statePath)) return ("", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!doc.RootElement.TryGetProperty("clip_review", out var cr) ||
                cr.ValueKind != JsonValueKind.Object)
                return ("", "");
            if (!cr.TryGetProperty(key, out var row) || row.ValueKind != JsonValueKind.Object)
                return ("", "");
            var status = row.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var note = row.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            return (status, note);
        }
        catch
        {
            return ("", "");
        }
    }

    private static List<(int Scene, int Clip)> ListOnDiskClips(string projectDir, int? sceneFilter)
    {
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var list = new List<(int, int)>();
        if (!Directory.Exists(videoDir)) return list;

        foreach (var fi in new DirectoryInfo(videoDir).EnumerateFiles("scene_*_clip_*.mp4"))
        {
            if (!ExactClipNameRe.IsMatch(fi.Name)) continue;
            if (fi.Length < 512) continue;
            var m = ExactClipNameRe.Match(fi.Name);
            if (!int.TryParse(m.Groups[1].Value, out var sn) ||
                !int.TryParse(m.Groups[2].Value, out var cn))
                continue;
            if (sceneFilter is int only && only > 0 && sn != only) continue;
            list.Add((sn, cn));
        }

        return list
            .OrderBy(x => x.Item1)
            .ThenBy(x => x.Item2)
            .ToList();
    }
}
