using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Models;

namespace FilmStudio.Engine;

/// <summary>
/// Fountain draft lifecycle: load/save, create from book/import, sign-off.
/// Operator source of truth is <c>source/screenplay.fountain</c>.
/// Shot planning reads Fountain directly (in-memory beat model) — no scenes.json step.
/// Canonical file: source/screenplay.fountain (+ source/screenplay_meta.json).
/// </summary>
public static class ScreenplayService
{
    public const string CanonicalFileName = "screenplay.fountain";
    public const string MetaFileName = "screenplay_meta.json";
    /// <summary>Optional cast seed cache (plates / voice edits) under source/.</summary>
    public const string CastSeedsFileName = "cast_seeds.json";

    public sealed class ScreenplayDoc
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string Text { get; init; } = "";
        public ScreenplayStatus Status { get; init; } = new();
    }

    public sealed class SaveResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public ScreenplayStatus Status { get; init; } = new();
        public string? Message { get; set; }
    }

    public sealed class SignOffResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? Title { get; init; }
        public int SceneCount { get; init; }
        public int CharacterCount { get; init; }
        public int LocationCount { get; init; }
        public bool HashChanged { get; init; }
        public ScreenplayStatus Status { get; init; } = new();
        public string? Message { get; init; }
    }

    private sealed class MetaDto
    {
        public string? SignedHash { get; set; }
        public string? SignedAt { get; set; }
        public string? LastSavedHash { get; set; }
        public string? LastSavedAt { get; set; }
    }

    public static string GetDraftPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), "source", CanonicalFileName);

    public static string GetMetaPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), "source", MetaFileName);

    public static string GetCastSeedsPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), "source", CastSeedsFileName);

    /// <summary>
    /// Parse Fountain into the in-memory screenplay model used by Stage 2 / cast tooling
    /// (same shape as the old stage1.v1 dict, never written to disk for planning).
    /// </summary>
    public static Dictionary<string, object?> BuildModelFromFountainText(string fountainText)
    {
        var parsed = FountainParser.Parse(fountainText);
        var doc = FountainStage1Importer.BuildStage1(parsed);
        return Stage1Normalizer.Normalize(doc);
    }

    /// <summary>
    /// Load project Fountain and build the in-memory screenplay model.
    /// Returns null if there is no draft.
    /// </summary>
    public static Dictionary<string, object?>? TryBuildModelFromProject(ProjectStore store, string projectId)
    {
        EnsureCanonicalDraft(store, projectId);
        var path = GetDraftPath(store, projectId);
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return BuildModelFromFountainText(text);
    }

    /// <summary>Summarise Fountain into Stage1Status (UI / readiness). No scenes.json.</summary>
    public static Stage1Status StatusFromFountainModel(
        Dictionary<string, object?>? model,
        string? fountainPath = null)
    {
        var status = new Stage1Status
        {
            ScenesFile = fountainPath is null ? CanonicalFileName : Path.GetFileName(fountainPath),
        };
        if (model is null)
            return status;

        status.Present = true;
        status.MovieTitle = model.TryGetValue("movie_title", out var mt) ? mt?.ToString() : null;
        status.SourceBookTitle = model.TryGetValue("source_book_title", out var sbt) ? sbt?.ToString() : null;
        if (model.TryGetValue("cumulative_duration_target_seconds", out var rt) && rt is not null)
        {
            status.RuntimeSeconds = rt switch
            {
                int i => i,
                long l => l,
                double d => d,
                _ => double.TryParse(rt.ToString(), out var x) ? x : null,
            };
        }

        if (fountainPath is not null && File.Exists(fountainPath))
        {
            try { status.Mtime = File.GetLastWriteTime(fountainPath).ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { /* ignore */ }
        }

        if (model.TryGetValue("global_production_variables", out var gpvObj) &&
            gpvObj is Dictionary<string, object?> gpv)
        {
            if (gpv.TryGetValue("character_seed_tokens", out var chars) &&
                chars is Dictionary<string, object?> charDict)
            {
                status.CharacterCount = charDict.Count;
                foreach (var (key, val) in charDict)
                {
                    var display = key.Replace("Character_", "").Replace("_", " ");
                    if (val is Dictionary<string, object?> seed &&
                        seed.TryGetValue("canonical_given_name", out var cn) &&
                        cn is string cname && cname.Length > 0)
                        display = cname;
                    else if (val is Dictionary<string, object?> seed2 &&
                             seed2.TryGetValue("voice_label", out var vl) &&
                             vl is string lab && lab.Length > 0)
                        display = lab;
                    status.CastNames.Add(display);
                }
            }

            if (gpv.TryGetValue("location_seed_tokens", out var locs) &&
                locs is Dictionary<string, object?> locDict)
                status.LocationCount = locDict.Count;
        }

        if (model.TryGetValue("scenes", out var scenesObj) && scenesObj is List<object?> scenes)
        {
            foreach (var s in scenes.OfType<Dictionary<string, object?>>())
            {
                var sn = s.TryGetValue("scene_number", out var sne) ? ToInt(sne) : 0;
                var beats = 0;
                if (s.TryGetValue("story_beats", out var sb) && sb is List<object?> beatList)
                    beats = beatList.Count;
                status.BeatCount += beats;
                double? dur = null;
                if (s.TryGetValue("duration_target_seconds", out var d) ||
                    s.TryGetValue("estimated_duration_seconds", out d))
                {
                    if (d is double dd) dur = dd;
                    else if (d is int di) dur = di;
                    else if (double.TryParse(d?.ToString(), out var dx)) dur = dx;
                }

                status.Scenes.Add(new Stage1SceneRow
                {
                    SceneNumber = sn,
                    Setting = s.TryGetValue("setting", out var set) ? set?.ToString() ?? "" : "",
                    BeatCount = beats,
                    DurationSeconds = dur,
                });
            }

            status.SceneCount = status.Scenes.Count;
            status.Scenes = status.Scenes.OrderBy(x => x.SceneNumber).ToList();
        }

        return status;
    }

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var n) => n,
        _ => 0,
    };

    public static string ComputeHash(string text)
    {
        var normalized = NormalizeText(text);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string NormalizeText(string text)
    {
        text ??= "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > 0 && !text.EndsWith('\n'))
            text += "\n";
        return text;
    }

    /// <summary>Read Fountain draft + sign-off status. Pass Stage1 from GetAdaptationStatus to avoid re-reading.</summary>
    public static ScreenplayStatus ReadStatus(ProjectStore store, string projectId, Stage1Status stage1)
    {
        // Surface imported .fountain files that never got the canonical name
        try { EnsureCanonicalDraft(store, projectId); } catch { /* status still useful */ }

        var draftPath = GetDraftPath(store, projectId);
        var meta = ReadMeta(store, projectId);
        var status = new ScreenplayStatus();

        if (File.Exists(draftPath))
        {
            var text = File.ReadAllText(draftPath);
            var hash = ComputeHash(text);
            var fi = new FileInfo(draftPath);
            status.DraftExists = true;
            status.DraftBytes = fi.Length;
            status.DraftHash = hash;
            status.DraftMtime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var parsed = FountainParser.Parse(text);
            status.SceneHeadingCount = parsed.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading);
            if (parsed.TitlePage.TryGetValue("Title", out var t) && !string.IsNullOrWhiteSpace(t))
                status.Title = t.Replace("\n", " ").Trim();
            else if (parsed.TitlePage.TryGetValue("title", out t) && !string.IsNullOrWhiteSpace(t))
                status.Title = t.Replace("\n", " ").Trim();
        }

        status.SignedHash = meta.SignedHash;
        status.SignedAt = meta.SignedAt;

        if (status.DraftExists)
        {
            status.Signed = !string.IsNullOrEmpty(meta.SignedHash) &&
                            string.Equals(meta.SignedHash, status.DraftHash, StringComparison.OrdinalIgnoreCase);
            status.Dirty = !status.Signed;
        }
        else
        {
            // Legacy: Stage 1 without a Fountain draft — treat as ready (no draft to re-sign)
            status.Signed = stage1.Present && stage1.SceneCount > 0;
            status.Dirty = false;
        }

        // Ready when approved Fountain has scenes (Stage 2 reads Fountain directly).
        // Legacy: scenes-only projects without a draft still count as ready.
        status.ReadyForShots =
            (status.DraftExists && status.Signed && status.SceneHeadingCount > 0) ||
            (!status.DraftExists && stage1.Present && stage1.SceneCount > 0);

        return status;
    }

    public static ScreenplayDoc Get(ProjectStore store, string projectId)
    {
        // Prefer canonical draft; if missing, adopt any source/*.fountain from import
        EnsureCanonicalDraft(store, projectId);
        var draftPath = GetDraftPath(store, projectId);
        var stage1 = ReadStage1Lite(store, projectId);
        var status = ReadStatus(store, projectId, stage1);
        var text = File.Exists(draftPath) ? File.ReadAllText(draftPath) : "";
        return new ScreenplayDoc
        {
            Ok = true,
            Text = text,
            Status = status,
        };
    }

    /// <summary>
    /// If screenplay.fountain is missing, copy the newest source/*.fountain (or project root *.fountain)
    /// into the canonical path so the editor has something to load after import.
    /// </summary>
    public static bool EnsureCanonicalDraft(ProjectStore store, string projectId)
    {
        var draftPath = GetDraftPath(store, projectId);
        if (File.Exists(draftPath) && new FileInfo(draftPath).Length > 0)
            return false;

        var projectDir = store.GetProjectDir(projectId);
        var sourceDir = Path.Combine(projectDir, "source");
        Directory.CreateDirectory(sourceDir);

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        void Consider(string path)
        {
            if (!File.Exists(path)) return;
            if (Path.GetFileName(path).Equals(CanonicalFileName, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length == 0) return;
                if (fi.LastWriteTimeUtc >= bestTime)
                {
                    bestTime = fi.LastWriteTimeUtc;
                    best = path;
                }
            }
            catch { /* ignore */ }
        }

        if (Directory.Exists(sourceDir))
        {
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*.fountain"))
                Consider(f);
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*.spmd"))
                Consider(f);
        }
        foreach (var f in Directory.EnumerateFiles(projectDir, "*.fountain"))
            Consider(f);

        if (best is null)
            return false;

        var text = File.ReadAllText(best);
        File.WriteAllText(draftPath, NormalizeText(text));
        var meta = ReadMeta(store, projectId);
        meta.LastSavedHash = ComputeHash(text);
        meta.LastSavedAt = DateTime.UtcNow.ToString("o");
        // If Stage 1 already exists from a prior import, treat as signed so shot plan stays available
        var stage1 = ReadStage1Lite(store, projectId);
        if (stage1.Present && stage1.SceneCount > 0 && string.IsNullOrEmpty(meta.SignedHash))
        {
            meta.SignedHash = meta.LastSavedHash;
            meta.SignedAt = meta.LastSavedAt;
        }
        WriteMeta(store, projectId, meta);
        return true;
    }

    public static SaveResult SaveDraft(ProjectStore store, string projectId, string text)
    {
        text = NormalizeText(text ?? "");
        var sourceDir = Path.Combine(store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(sourceDir);
        var draftPath = GetDraftPath(store, projectId);
        File.WriteAllText(draftPath, text);

        var hash = ComputeHash(text);
        var meta = ReadMeta(store, projectId);
        meta.LastSavedHash = hash;
        meta.LastSavedAt = DateTime.UtcNow.ToString("o");
        WriteMeta(store, projectId, meta);

        var stage1 = ReadStage1Lite(store, projectId);
        var status = ReadStatus(store, projectId, stage1);
        return new SaveResult
        {
            Ok = true,
            Status = status,
            Message = status.Dirty
                ? "Draft saved — approve when ready"
                : "Draft saved",
        };
    }

    /// <summary>Import Fountain text as the editable draft (does not materialise Stage 1).</summary>
    public static SaveResult ImportAsDraft(
        ProjectStore store,
        string projectId,
        string text,
        string? originalFileName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SaveResult { Ok = false, Error = "Empty screenplay text" };

        var result = SaveDraft(store, projectId, text);

        // Keep a copy under the original name for reference when different
        if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            var safe = Path.GetFileName(originalFileName);
            if (!string.IsNullOrWhiteSpace(safe) &&
                !safe.Equals(CanonicalFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (!safe.EndsWith(".fountain", StringComparison.OrdinalIgnoreCase) &&
                    !safe.EndsWith(".spmd", StringComparison.OrdinalIgnoreCase))
                    safe = Path.GetFileNameWithoutExtension(safe) + ".fountain";
                var copyPath = Path.Combine(store.GetProjectDir(projectId), "source", safe);
                try { File.WriteAllText(copyPath, NormalizeText(text)); } catch { /* ignore */ }
            }
        }

        result.Message = "Screenplay draft ready — review and approve on Screenplay";
        return result;
    }

    /// <summary>
    /// Offline/test helper only — minimal stub. Production path is <see cref="CreateDraftFromBookAsync"/>.
    /// </summary>
    public static SaveResult CreateDraftFromBook(ProjectStore store, string projectId)
    {
        var projectDir = store.GetProjectDir(projectId);
        var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
        if (!File.Exists(bookPath))
            return new SaveResult { Ok = false, Error = "No prepared book text yet" };

        var book = File.ReadAllText(bookPath);
        if (string.IsNullOrWhiteSpace(book))
            return new SaveResult { Ok = false, Error = "Book text is empty" };

        var (title, author) = ReadProjectTitleAuthor(projectDir, projectId);
        var fountain = BookToFountainConverter.ConvertHeuristic(title, book, author);
        var save = SaveDraft(store, projectId, fountain);
        if (!save.Ok) return save;
        save.Message = "Screenplay draft ready — review and approve";
        return save;
    }

    /// <summary>
    /// Build screenplay draft from book_full.txt via chat (locations, dialogue, page tags).
    /// Requires a configured chat client.
    /// </summary>
    public static async Task<SaveResult> CreateDraftFromBookAsync(
        ProjectStore store,
        string projectId,
        FilmStudio.Engine.Abstractions.IGrokChatClient? chat = null,
        string model = "grok-4.5",
        CancellationToken ct = default)
    {
        var projectDir = store.GetProjectDir(projectId);
        var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
        if (!File.Exists(bookPath))
            return new SaveResult { Ok = false, Error = "No prepared book text yet" };

        var book = await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(book))
            return new SaveResult { Ok = false, Error = "Book text is empty" };

        var (title, author) = ReadProjectTitleAuthor(projectDir, projectId);
        var analysis = BookTextAnalyzer.Analyze(book);
        var minutes = Math.Clamp(analysis.SuggestedTotalMinutes, 3, 180);

        try
        {
            var fountain = await BookToFountainConverter.ConvertAsync(
                workspaceRoot: store.WorkspaceRoot,
                title: title,
                bookText: book,
                author: author,
                totalRuntimeMinutes: minutes,
                chat: chat,
                model: model,
                ct: ct).ConfigureAwait(false);

            var save = SaveDraft(store, projectId, fountain);
            if (!save.Ok) return save;
            save.Message = "Screenplay draft ready — review and approve";
            return save;
        }
        catch (Exception ex)
        {
            return new SaveResult { Ok = false, Error = ex.Message };
        }
    }

    private static (string Title, string? Author) ReadProjectTitleAuthor(string projectDir, string projectId)
    {
        var title = projectId;
        string? author = null;
        try
        {
            var pj = Path.Combine(projectDir, "project.json");
            if (File.Exists(pj))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                    title = t.GetString() ?? title;
                else if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    title = n.GetString() ?? title;
                if (doc.RootElement.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String)
                    author = a.GetString();
            }
        }
        catch { /* ignore */ }
        return (title, author);
    }

    public static SignOffResult SignOff(ProjectStore store, string projectId, string? text = null)
    {
        // Optional body text: save first
        if (text is not null)
        {
            var save = SaveDraft(store, projectId, text);
            if (!save.Ok)
                return new SignOffResult { Ok = false, Error = save.Error };
        }

        var draftPath = GetDraftPath(store, projectId);
        if (!File.Exists(draftPath))
            return new SignOffResult { Ok = false, Error = "No screenplay draft to approve" };

        var draftText = File.ReadAllText(draftPath);
        if (string.IsNullOrWhiteSpace(draftText))
            return new SignOffResult { Ok = false, Error = "Screenplay draft is empty" };

        draftText = NormalizeText(draftText);
        File.WriteAllText(draftPath, draftText);

        var hash = ComputeHash(draftText);
        var metaBefore = ReadMeta(store, projectId);
        var hashChanged = string.IsNullOrEmpty(metaBefore.SignedHash) ||
                          !string.Equals(metaBefore.SignedHash, hash, StringComparison.OrdinalIgnoreCase);

        // Validate Fountain has scenes (shot plan reads Fountain — no scenes.json write).
        Dictionary<string, object?> model;
        try
        {
            model = BuildModelFromFountainText(draftText);
        }
        catch (Exception ex)
        {
            return new SignOffResult { Ok = false, Error = $"Could not parse screenplay: {ex.Message}" };
        }

        var summary = StatusFromFountainModel(model, draftPath);
        if (summary.SceneCount <= 0)
            return new SignOffResult { Ok = false, Error = "Screenplay has no scenes (need INT./EXT. headings)." };

        var meta = ReadMeta(store, projectId);
        meta.SignedHash = hash;
        meta.SignedAt = DateTime.UtcNow.ToString("o");
        meta.LastSavedHash = hash;
        meta.LastSavedAt = meta.SignedAt;
        WriteMeta(store, projectId, meta);

        var stage1 = ReadStage1Lite(store, projectId);
        var status = ReadStatus(store, projectId, stage1);

        return new SignOffResult
        {
            Ok = true,
            Title = summary.MovieTitle,
            SceneCount = summary.SceneCount,
            CharacterCount = summary.CharacterCount,
            LocationCount = summary.LocationCount,
            HashChanged = hashChanged,
            Status = status,
            Message =
                $"Screenplay approved · {summary.SceneCount} scenes · {summary.CharacterCount} cast" +
                (hashChanged ? " · update shot plan if you already built one" : ""),
        };
    }

    /// <summary>Legacy entry point — structured book → Fountain conversion.</summary>
    public static string BookTextToFountainDraft(string title, string bookText) =>
        BookToFountainConverter.ConvertHeuristic(title, bookText);

    private static MetaDto ReadMeta(ProjectStore store, string projectId)
    {
        var path = GetMetaPath(store, projectId);
        if (!File.Exists(path)) return new MetaDto();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MetaDto>(json, JsonDefaults.CaseInsensitive) ?? new MetaDto();
        }
        catch
        {
            return new MetaDto();
        }
    }

    private static void WriteMeta(ProjectStore store, string projectId, MetaDto meta)
    {
        var path = GetMetaPath(store, projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(meta, JsonDefaults.Indented);
        File.WriteAllText(path, json + "\n");
    }

    /// <summary>Lightweight status from Fountain (and legacy scenes.json if no draft).</summary>
    public static Stage1Status ReadStage1Lite(ProjectStore store, string projectId)
    {
        try
        {
            EnsureCanonicalDraft(store, projectId);
            var draftPath = GetDraftPath(store, projectId);
            if (File.Exists(draftPath))
            {
                var model = TryBuildModelFromProject(store, projectId);
                return StatusFromFountainModel(model, draftPath);
            }
        }
        catch { /* fall through */ }

        // Legacy projects that only have scenes.json
        var path = store.ResolveScenesJsonPath(projectId);
        if (!File.Exists(path))
            return new Stage1Status { Present = false };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var scenes = root.TryGetProperty("scenes", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.GetArrayLength()
                : 0;
            var title = root.TryGetProperty("movie_title", out var t) ? t.GetString() : null;
            return new Stage1Status
            {
                Present = scenes > 0,
                SceneCount = scenes,
                MovieTitle = title,
                ScenesFile = Path.GetFileName(path),
            };
        }
        catch
        {
            return new Stage1Status { Present = File.Exists(path) };
        }
    }
}
