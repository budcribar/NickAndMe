using System.Text;
using System.Text.Json;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Writes project-local maps for manual whole-project review (Claude/operator):
/// <c>ARTIFACTS.md</c> + <c>artifact_index.json</c>, plus snapshots under <c>telemetry/</c>
/// of cost ledger and resolved models. No zip — data stays in the project tree (PR5 prep).
/// </summary>
public sealed class ProjectArtifactIndexService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ProjectStore _projects;
    private readonly CostReportService _costs;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<ProjectArtifactIndexService> _log;

    public ProjectArtifactIndexService(
        ProjectStore projects,
        CostReportService costs,
        IOptions<PageToMovieOptions> opts,
        ILogger<ProjectArtifactIndexService> log)
    {
        _projects = projects;
        _costs = costs;
        _opts = opts.Value;
        _log = log;
    }

    public string IndexJsonPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "artifact_index.json");

    public string ArtifactsMdPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "ARTIFACTS.md");

    /// <summary>
    /// Scan project, snapshot cost/models into telemetry/, write index JSON + ARTIFACTS.md.
    /// Safe to call anytime; intended before manual final review.
    /// </summary>
    public async Task<ArtifactIndexDocument> RebuildAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var dir = _projects.GetProjectDir(projectId);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"Project directory not found: {projectId}");

        await SnapshotTelemetryAsync(projectId, dir, ct).ConfigureAwait(false);

        var entries = new List<ArtifactIndexEntry>();
        void Add(string rel, string role, bool requiredForManualReview = false)
        {
            var abs = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            var exists = File.Exists(abs) || Directory.Exists(abs);
            long? bytes = null;
            int? fileCount = null;
            if (File.Exists(abs))
            {
                try { bytes = new FileInfo(abs).Length; } catch { /* */ }
            }
            else if (Directory.Exists(abs))
            {
                try
                {
                    var files = Directory.GetFiles(abs, "*", SearchOption.AllDirectories);
                    fileCount = files.Length;
                    bytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                }
                catch { /* */ }
            }

            entries.Add(new ArtifactIndexEntry
            {
                Path = rel.Replace('\\', '/'),
                Role = role,
                Exists = exists,
                Bytes = bytes,
                FileCount = fileCount,
                RequiredForManualReview = requiredForManualReview,
            });
        }

        // Core narrative
        Add("source/book_full.txt", "Source book / prose text", requiredForManualReview: true);
        Add("source/screenplay.fountain", "Signed/working Fountain screenplay", requiredForManualReview: true);
        Add("source/screenplay_meta.json", "Screenplay sign-off metadata");
        Add("source/cast_seeds.json", "Cast seeds (looks, locks, voices)", requiredForManualReview: true);
        Add("source/tell_tale_heart.fountain", "Imported Poe fountain (if used)");

        // Project config / state
        Add("project.json", "Project id/title");
        Add("project_rules.json", "Approved house rules / style locks", requiredForManualReview: true);
        Add("pipeline_state.json", "Clip reviews, auto-review state, cost_ledger", requiredForManualReview: true);
        Add("pipeline_config.json", "Per-project gen config (model, resolution)");
        Add("edit_feedback_log.json", "Human edit / pass-fail log");
        Add("blueprint.clips.grok.json", "Stage 2 shot plan / clips", requiredForManualReview: true);

        // Media
        Add("assets/movie_wip.mp4", "Full cut (WIP)", requiredForManualReview: true);
        Add("assets/movie_wip.mp4.sources.json", "WIP concat sources + assembly note");
        Add("assets/characters", "Locked character plates + variants", requiredForManualReview: true);
        Add("assets/video", "Clips + scene composites + duration sidecars", requiredForManualReview: true);
        Add("assets/video/prompts", "Full prompt .txt + .meta.json per clip", requiredForManualReview: true);

        // Review
        Add("assets/review", "Auto-review drafts, frames, index", requiredForManualReview: true);
        Add("assets/review/index.json", "Per-clip review index (rebuild via batch review)", requiredForManualReview: true);
        Add("assets/review/frames", "Durable auto-review sample frames");
        Add("assets/review/final_review.json", "Manual/AI final rubric scores (when filled)");
        Add("assets/review/FINAL_REVIEW_TEMPLATE.json", "Rubric template for manual final review");

        // Telemetry (live streams + snapshots)
        Add("telemetry/cost_ledger.json", "Cost events snapshot (from pipeline_state)", requiredForManualReview: true);
        Add("telemetry/models.json", "Resolved models/options snapshot");
        Add("telemetry/api_calls.jsonl", "Live API call log (full prompts)", requiredForManualReview: false);
        Add("telemetry/ffmpeg.jsonl", "Condensed ffmpeg ops", requiredForManualReview: false);
        // Written by this rebuild — not "required" for readiness (would always be missing mid-scan)
        Add("ARTIFACTS.md", "Human map of this project for Claude/manual review");
        Add("artifact_index.json", "Machine-readable artifact presence map");

        // Scene sources (assembly gate)
        var videoDir = Path.Combine(dir, "assets", "video");
        if (Directory.Exists(videoDir))
        {
            foreach (var src in Directory.EnumerateFiles(videoDir, "scene_*.mp4.sources.json")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(dir, src).Replace('\\', '/');
                Add(rel, "Scene remux include/exclude manifest");
            }
        }

        var missingRequired = entries
            .Where(e => e.RequiredForManualReview && !e.Exists)
            .Select(e => e.Path)
            .ToList();

        var doc = new ArtifactIndexDocument
        {
            ProjectId = projectId,
            BuiltAtUtc = DateTimeOffset.UtcNow,
            SchemaVersion = "1",
            Purpose =
                "Map of project-local artifacts for manual whole-project review " +
                "(e.g. point Claude/Codex at this folder). Zip export deferred; data lives here.",
            Entries = entries,
            MissingRequired = missingRequired,
            ReadyForManualFinalReview = missingRequired.Count == 0,
        };

        // Enrich with clip/prompt/review counts
        doc.Stats = CollectStats(dir);

        await EnsureFinalReviewTemplateAsync(dir, ct).ConfigureAwait(false);

        var indexPath = IndexJsonPath(projectId);
        await File.WriteAllTextAsync(
            indexPath,
            JsonSerializer.Serialize(doc, JsonOpts) + "\n",
            ct).ConfigureAwait(false);

        var md = BuildMarkdown(doc);
        await File.WriteAllTextAsync(ArtifactsMdPath(projectId), md, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Artifact index rebuilt for {ProjectId}: {Present}/{Total} paths, ready={Ready}",
            projectId,
            entries.Count(e => e.Exists),
            entries.Count,
            doc.ReadyForManualFinalReview);

        return doc;
    }

    private async Task SnapshotTelemetryAsync(string projectId, string dir, CancellationToken ct)
    {
        var tel = Path.Combine(dir, "telemetry");
        Directory.CreateDirectory(tel);

        try
        {
            var ledger = await _costs.GetCostLedgerAsync(projectId, ct).ConfigureAwait(false);
            var costPath = Path.Combine(tel, "cost_ledger.json");
            var payload = new
            {
                projectId,
                builtAtUtc = DateTimeOffset.UtcNow,
                source = "pipeline_state.cost_ledger",
                disclaimer = "List-rate estimates, not xAI invoices.",
                eventCount = ledger.Count,
                events = ledger,
            };
            await File.WriteAllTextAsync(
                costPath,
                JsonSerializer.Serialize(payload, JsonOpts) + "\n",
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "cost_ledger snapshot skipped for {ProjectId}", projectId);
        }

        try
        {
            var models = new Dictionary<string, object?>
            {
                ["builtAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["projectId"] = projectId,
                ["defaultVideoModel"] = _opts.DefaultModel,
                ["defaultImageModel"] = _opts.DefaultImageModel,
                ["imageProvider"] = _opts.ImageProvider,
                ["defaultResolution"] = _opts.DefaultResolution,
                ["defaultDurationSeconds"] = _opts.DefaultDurationSeconds,
                ["identityReseedOnCastChange"] = _opts.IdentityReseedOnCastChange,
                ["requirePortraitStyleGate"] = _opts.RequirePortraitStyleGate,
            };

            // Overlay pipeline_config if present
            var cfgPath = Path.Combine(dir, "pipeline_config.json");
            if (File.Exists(cfgPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cfgPath, ct));
                    models["pipelineConfig"] = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
                }
                catch { /* */ }
            }

            await File.WriteAllTextAsync(
                Path.Combine(tel, "models.json"),
                JsonSerializer.Serialize(models, JsonOpts) + "\n",
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "models snapshot skipped for {ProjectId}", projectId);
        }

        // Placeholder INDEX for future jsonl streams
        var telIndex = Path.Combine(tel, "INDEX.md");
        // Always refresh INDEX so it documents live streams
        await File.WriteAllTextAsync(telIndex,
            """
            # Project telemetry

            | File | Purpose |
            |------|---------|
            | `cost_ledger.json` | Snapshot of cost events from `pipeline_state` (list rates) |
            | `models.json` | Resolved model/options snapshot at last artifact-index rebuild |
            | `api_calls.jsonl` | Append-only: one JSON line per live API call (full prompts) |
            | `ffmpeg.jsonl` | Append-only: condensed remux / WIP / frame-sample ops |

            `api_calls` and `ffmpeg` are written during jobs (project scope).  
            Rebuild this folder’s snapshots via `POST /api/projects/{id}/artifacts/index`.
            """ + "\n", ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> CollectStats(string dir)
    {
        var stats = new Dictionary<string, object?>();
        var video = Path.Combine(dir, "assets", "video");
        if (Directory.Exists(video))
        {
            var clips = Directory.GetFiles(video, "scene_*_clip_*.mp4")
                .Where(f => FfmpegRemuxService.IsExactClipFileName(Path.GetFileName(f)))
                .ToList();
            var scenes = Directory.GetFiles(video, "scene_??.mp4");
            stats["clipMp4Count"] = clips.Count;
            stats["sceneCompositeCount"] = scenes.Length;
            var prompts = Path.Combine(video, "prompts");
            if (Directory.Exists(prompts))
            {
                stats["promptTxtCount"] = Directory.GetFiles(prompts, "S*.txt").Length;
                stats["promptMetaCount"] = Directory.GetFiles(prompts, "S*.meta.json").Length;
            }
        }

        var review = Path.Combine(dir, "assets", "review");
        if (Directory.Exists(review))
        {
            stats["autoReviewDraftCount"] = Directory.GetFiles(review, "S*.auto_review.json").Length;
            stats["hasReviewIndex"] = File.Exists(Path.Combine(review, "index.json"));
            var frames = Path.Combine(review, "frames");
            stats["reviewFrameCount"] = Directory.Exists(frames)
                ? Directory.GetFiles(frames, "*.jpg").Length
                : 0;
        }

        stats["hasWip"] = File.Exists(Path.Combine(dir, "assets", "movie_wip.mp4"));
        return stats;
    }

    private static string BuildMarkdown(ArtifactIndexDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Project artifacts — `{doc.ProjectId}`");
        sb.AppendLine();
        sb.AppendLine(doc.Purpose);
        sb.AppendLine();
        sb.AppendLine($"Built: **{doc.BuiltAtUtc:u}** · Ready for manual final review: **{(doc.ReadyForManualFinalReview ? "yes" : "no")}**");
        sb.AppendLine();
        if (doc.Stats is { Count: > 0 })
        {
            sb.AppendLine("## Stats");
            sb.AppendLine();
            foreach (var kv in doc.Stats)
                sb.AppendLine($"- **{kv.Key}**: {kv.Value}");
            sb.AppendLine();
        }

        if (doc.MissingRequired.Count > 0)
        {
            sb.AppendLine("## Missing (recommended for manual review)");
            sb.AppendLine();
            foreach (var m in doc.MissingRequired)
                sb.AppendLine($"- `{m}`");
            sb.AppendLine();
        }

        sb.AppendLine("## Map");
        sb.AppendLine();
        sb.AppendLine("| Present | Path | Role |");
        sb.AppendLine("|--------:|------|------|");
        foreach (var e in doc.Entries)
        {
            var mark = e.Exists ? "yes" : "no";
            var req = e.RequiredForManualReview ? " *(core)*" : "";
            sb.AppendLine($"| {mark} | `{e.Path}` | {e.Role}{req} |");
        }

        sb.AppendLine();
        sb.AppendLine("## How to review manually (Claude / external AI)");
        sb.AppendLine();
        sb.AppendLine("1. Open **this project folder** in Claude Code (or similar) — not a zip.");
        sb.AppendLine("2. Start with `ARTIFACTS.md` + `artifact_index.json` (this map).");
        sb.AppendLine("3. Story triad: `source/book_full.txt` + `source/screenplay.fountain` + `assets/movie_wip.mp4`.");
        sb.AppendLine("4. Identity: `assets/characters/*_ref.png`, `assets/video/prompts/*.meta.json` (`prompt`, `castCount`, `refsAttachedToApi`).");
        sb.AppendLine("5. QC: `assets/review/*.auto_review.json`, `assets/review/index.json`, `assets/review/frames/`.");
        sb.AppendLine("6. Assembly: `assets/video/scene_*.mp4.sources.json` (`included` / `excluded`).");
        sb.AppendLine("7. Telemetry: `telemetry/api_calls.jsonl` (full prompts), `telemetry/ffmpeg.jsonl`, `telemetry/cost_ledger.json`.");
        sb.AppendLine("8. Scores: copy `assets/review/FINAL_REVIEW_TEMPLATE.json` → `final_review.json` and fill **human** (and optionally **ai** notes).");
        sb.AppendLine("9. Zip export is deferred — all durable data stays in this directory.");
        sb.AppendLine();
        sb.AppendLine("Refresh this map: `POST /api/projects/{id}/artifacts/index` or Review UI **Refresh artifact map**.");
        sb.AppendLine();
        return sb.ToString();
    }

    private static async Task EnsureFinalReviewTemplateAsync(string projectDir, CancellationToken ct)
    {
        var reviewDir = Path.Combine(projectDir, "assets", "review");
        Directory.CreateDirectory(reviewDir);
        var path = Path.Combine(reviewDir, "FINAL_REVIEW_TEMPLATE.json");
        if (File.Exists(path)) return;

        var template = new
        {
            schemaVersion = "1",
            note = "Copy to final_review.json and fill scores (1–5). AI automation deferred; use for manual Claude + human pass.",
            projectId = "",
            createdAtUtc = "",
            wipPath = "assets/movie_wip.mp4",
            categories = new[]
            {
                "fidelity_plot",
                "fidelity_character",
                "adaptation_craft",
                "visual_identity",
                "continuity",
                "audio_dialogue",
                "completeness",
                "technical",
                "watchability",
            },
            scale = "1 = broken, 3 = acceptable, 5 = excellent",
            ai = new
            {
                model = "",
                scoredAtUtc = "",
                categories = new Dictionary<string, object>
                {
                    ["fidelity_plot"] = new { score = 0, note = "" },
                    ["fidelity_character"] = new { score = 0, note = "" },
                    ["adaptation_craft"] = new { score = 0, note = "" },
                    ["visual_identity"] = new { score = 0, note = "" },
                    ["continuity"] = new { score = 0, note = "" },
                    ["audio_dialogue"] = new { score = 0, note = "" },
                    ["completeness"] = new { score = 0, note = "" },
                    ["technical"] = new { score = 0, note = "" },
                    ["watchability"] = new { score = 0, note = "" },
                },
                overall = 0,
                summary = "",
                risks = Array.Empty<string>(),
                missingBeats = Array.Empty<string>(),
                strengths = Array.Empty<string>(),
                suggestedFixes = Array.Empty<string>(),
            },
            human = new
            {
                scoredAtUtc = "",
                raterId = "",
                categories = new Dictionary<string, object>
                {
                    ["fidelity_plot"] = new { score = 0, note = "" },
                    ["fidelity_character"] = new { score = 0, note = "" },
                    ["adaptation_craft"] = new { score = 0, note = "" },
                    ["visual_identity"] = new { score = 0, note = "" },
                    ["continuity"] = new { score = 0, note = "" },
                    ["audio_dialogue"] = new { score = 0, note = "" },
                    ["completeness"] = new { score = 0, note = "" },
                    ["technical"] = new { score = 0, note = "" },
                    ["watchability"] = new { score = 0, note = "" },
                },
                overall = 0,
                wouldShip = false,
                notes = "",
            },
        };

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(template, JsonOpts) + "\n",
            ct).ConfigureAwait(false);
    }
}

public sealed class ArtifactIndexDocument
{
    public string ProjectId { get; set; } = "";
    public DateTimeOffset BuiltAtUtc { get; set; }
    public string SchemaVersion { get; set; } = "1";
    public string Purpose { get; set; } = "";
    public bool ReadyForManualFinalReview { get; set; }
    public List<string> MissingRequired { get; set; } = new();
    public List<ArtifactIndexEntry> Entries { get; set; } = new();
    public Dictionary<string, object?> Stats { get; set; } = new();
}

public sealed class ArtifactIndexEntry
{
    public string Path { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Exists { get; set; }
    public long? Bytes { get; set; }
    public int? FileCount { get; set; }
    public bool RequiredForManualReview { get; set; }
}
