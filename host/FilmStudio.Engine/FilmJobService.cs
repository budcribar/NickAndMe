using System.Collections.Concurrent;
using System.Text.Json;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

public interface IJobProgressSink
{
    Task OnJobUpdatedAsync(JobSnapshot snapshot, CancellationToken ct = default);
    Task OnJobLogAsync(string message, CancellationToken ct = default);
}

/// <summary>
/// Native C# film job orchestrator (no Python): Stage 1/2, book prepare,
/// character design, multi-ref video, remux/WIP with SignalR progress.
/// </summary>
public sealed class FilmJobService
{
    private readonly ProjectStore _projects;
    private readonly GrokVideoClient _grok;
    private readonly CharacterDesignService _characters;
    private readonly CharacterBookPlateService _plates;
    private readonly BookPrepareService _books;
    private readonly Stage1Service _stage1;
    private readonly Stage2PlannerService _stage2;
    private readonly FfmpegRemuxService _remux;
    private readonly CostReportService _costs;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<FilmJobService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _snapLock = new(1, 1);
    private readonly ConcurrentQueue<string> _logLines = new();
    private CancellationTokenSource? _cts;
    private JobSnapshot _snapshot = new() { Status = "idle" };
    private IJobProgressSink? _sink;

    public FilmJobService(
        ProjectStore projects,
        GrokVideoClient grok,
        CharacterDesignService characters,
        CharacterBookPlateService plates,
        BookPrepareService books,
        Stage1Service stage1,
        Stage2PlannerService stage2,
        FfmpegRemuxService remux,
        CostReportService costs,
        IOptions<FilmStudioOptions> opts,
        ILogger<FilmJobService> log)
    {
        _projects = projects;
        _grok = grok;
        _characters = characters;
        _plates = plates;
        _books = books;
        _stage1 = stage1;
        _stage2 = stage2;
        _remux = remux;
        _costs = costs;
        _opts = opts.Value;
        _log = log;
    }

    public void SetProgressSink(IJobProgressSink sink) => _sink = sink;

    public JobSnapshot GetSnapshot() => Clone(_snapshot);

    public bool IsRunning =>
        string.Equals(_snapshot.Status, "running", StringComparison.OrdinalIgnoreCase);

    public async Task CancelAsync()
    {
        _cts?.Cancel();
        await AppendLogAsync("Cancel requested…");
        await UpdateAsync(s => s.Message = "Cancel requested — finishing current step if possible…");
    }

    public async Task StartSceneGenAsync(StartSceneGenRequest req)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunSceneGenAsync(req, ct);
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    public async Task StartBatchGenAsync(StartBatchGenRequest req)
    {
        if (req.Scenes is null || req.Scenes.Count == 0)
            throw new InvalidOperationException("At least one scene is required.");

        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunBatchGenAsync(req, ct);
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>Stage 1 (book → scenes.json) via C# Grok chat. Requires XAI_API_KEY.</summary>
    public async Task StartStage1Async(StartStage1Request req)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunStage1Async(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>Stage 2 planner (scenes.json → blueprint). Deterministic C#; no API key.</summary>
    public async Task StartStage2Async(StartStage2Request req)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunStage2Async(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>C# PDF extract + optional Grok vision OCR → book_full.txt.</summary>
    public async Task StartBookPrepareAsync(StartBookPrepareRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException("projectId required");

        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunBookPrepareAsync(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    private async Task RunBookPrepareAsync(StartBookPrepareRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        _projects.Activate(projectId);
        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "book_prepare",
            ProjectId = projectId,
            Message = "Preparing book (PDF extract / vision OCR)…",
            Index = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            await AppendLogAsync("Book prepare (C# PdfPig + optional Grok vision)");
            var result = await _books.PrepareAsync(
                projectId,
                forceExtract: req.ForceExtract,
                forceVision: req.ForceVision,
                autoVision: req.AutoVision,
                visionModel: string.IsNullOrWhiteSpace(req.VisionModel) ? "grok-4.5" : req.VisionModel,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    if (line.Contains("Extract", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = 1; s.Message = line; });
                    else if (line.Contains("Vision", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("page", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = Math.Max(s.Index, 2); s.Message = line; });
                    else
                        _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            await UpdateAsync(s => s.Index = 3);
            var msg = result.ReadyForStage1
                ? $"Book ready · {result.TextWords} words · quality={result.TextQuality} · {result.TextEngine}"
                : $"Book prepared but Stage 1 not ready · {result.Strategy}: {result.StrategyReason}";
            await FinishAsync(result.Ok ? "done" : "error", msg, result.Ok ? null : msg);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Book prepare failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    /// <summary>Generate portrait variants via C# Grok image API.</summary>
    public async Task StartCharacterVariantsAsync(StartCharacterVariantsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CharKey))
            throw new InvalidOperationException("charKey required");

        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunCharacterVariantsAsync(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Grok vision: classify book images → which characters appear, write plates to scenes.json.
    /// Cancellable. Falls back to heuristics if no API key.
    /// </summary>
    public async Task StartSortCharacterPlatesAsync(AttachCharacterPlatesRequest req)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunSortCharacterPlatesAsync(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    private async Task RunSortCharacterPlatesAsync(AttachCharacterPlatesRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "character-plates",
            ProjectId = projectId,
            Message = req.UseGrok
                ? "Sorting book images onto characters with Grok vision…"
                : "Sorting book images onto characters (heuristic)…",
            Index = 0,
            Total = Math.Max(1, req.MaxImages),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            await AppendLogAsync(
                req.UseGrok
                    ? "Character plate sort (Grok vision classifies who appears on each page)"
                    : "Character plate sort (heuristic only)");

            var result = await _plates.AttachAsync(
                projectId,
                force: true, // job is always an explicit re-sort from UI
                copyIntoAssets: req.CopyIntoAssets,
                onlyCharKey: req.CharKey,
                useGrok: req.UseGrok,
                visionModel: string.IsNullOrWhiteSpace(req.VisionModel) ? "grok-4.5" : req.VisionModel,
                maxImages: req.MaxImages > 0 ? req.MaxImages : 32,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    // "Grok vision 3/20: …"
                    var m = System.Text.RegularExpressions.Regex.Match(
                        line, @"Grok vision\s+(\d+)/(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success &&
                        int.TryParse(m.Groups[1].Value, out var i) &&
                        int.TryParse(m.Groups[2].Value, out var t))
                    {
                        _ = UpdateAsync(s =>
                        {
                            s.Index = i;
                            s.Total = t;
                            s.Message = line;
                        });
                    }
                    else
                        _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            if (result.AlreadySorted)
            {
                await FinishAsync("done", $"Already sorted ({result.SortedAt})");
                return;
            }

            if (!result.Ok && !string.IsNullOrEmpty(result.Reason))
            {
                await FinishAsync("error", result.Reason, result.Reason);
                return;
            }

            await UpdateAsync(s =>
            {
                s.Index = Math.Max(s.Index, result.ImagesClassified);
                if (result.ImagesClassified > 0)
                    s.Total = Math.Max(s.Total, result.ImagesClassified);
            });
            await AppendLogAsync(
                $"method={result.Method} updated={result.CharactersUpdated} " +
                $"skipped={result.CharactersSkipped} classified={result.ImagesClassified} " +
                $"text_skipped={result.ImagesSkippedText}");
            await FinishAsync(
                "done",
                $"Plates sorted ({result.Method}): {result.CharactersUpdated} character(s) updated");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character plate sort failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    /// <summary>Lock/unlock character reference images.</summary>
    public Task<string> RunCharacterDesignActionAsync(
        string projectId,
        string action,
        string charKey,
        int variantIndex = 1,
        string? imagePath = null,
        CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("A generation job is already running.");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return action switch
            {
                "lock-variant" =>
                    _characters.LockVariant(projectId, charKey, Math.Clamp(variantIndex, 1, 3)),
                "lock-image" when !string.IsNullOrWhiteSpace(imagePath) =>
                    _characters.LockFromPath(
                        projectId,
                        charKey,
                        ResolveLockImagePath(projectId, imagePath!)),
                "lock-bookref" =>
                    _characters.LockBookRef(projectId, charKey, Math.Max(0, variantIndex)),
                "unlock" =>
                    _characters.Unlock(projectId, charKey)
                        ? $"Unlocked {charKey} — previous lock kept as variant 1 (best so far)"
                        : $"No locked ref for {charKey}",
                _ => throw new InvalidOperationException($"Unknown character action: {action}"),
            };
        }, ct);
    }

    private string ResolveLockImagePath(string projectId, string imagePath)
    {
        if (File.Exists(imagePath))
            return Path.GetFullPath(imagePath);
        var projectDir = _projects.GetProjectDir(projectId);
        var cand = Path.Combine(projectDir, imagePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(cand))
            return Path.GetFullPath(cand);
        throw new InvalidOperationException($"Image not found: {imagePath}");
    }

    private async Task RunCharacterVariantsAsync(StartCharacterVariantsRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "character",
            ProjectId = projectId,
            CharKey = req.CharKey,
            Message = $"Generating portraits for {req.CharKey}…",
            Index = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            await AppendLogAsync($"Character design (C# / Grok image API) for {req.CharKey}");
            await UpdateAsync(s => s.Message = "Resolving refs + design prompt…");

            var result = await _characters.GenerateVariantsAsync(
                projectId,
                req.CharKey,
                n: req.Count,
                seedOptions: req,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    var idx = TryParseVariantProgress(line);
                    if (idx > 0)
                        _ = UpdateAsync(s => { s.Index = idx; s.Message = line; });
                    else if (line.Contains("generating", StringComparison.OrdinalIgnoreCase))
                    {
                        // "generating 1 variant(s)" / "generating 3 variants"
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"generating\s+(\d+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var total) && total > 0)
                            _ = UpdateAsync(s => { s.Total = total; s.Message = line; });
                        else
                            _ = UpdateAsync(s => s.Message = line);
                    }
                    else if (line.Contains("edit variant", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Grok", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("book ref", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("ref image", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s =>
                        {
                            s.Index = Math.Max(s.Index, 1);
                            s.Message = line;
                        });
                },
                ct: ct);

            await UpdateAsync(s =>
            {
                s.Index = result.Paths.Count;
                s.Total = Math.Max(s.Total, result.Paths.Count);
            });
            await AppendLogAsync(
                $"mode={result.Mode} · {result.Paths.Count} file(s)" +
                (result.BookRefs.Count > 0
                    ? $" · book refs: {string.Join(", ", result.BookRefs)}"
                    : ""));
            await FinishAsync(
                "done",
                $"Variants ready for {req.CharKey} ({result.Mode}, {result.Paths.Count} image(s))");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character variants failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private static int TryParseVariantProgress(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            line, @"variant[_\s-]*0*([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return n;
        m = System.Text.RegularExpressions.Regex.Match(line, @"\b([1-3])\s*/\s*3\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        m = System.Text.RegularExpressions.Regex.Match(
            line, @"saved variant\s+([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        return 0;
    }

    private async Task RunStage1Async(StartStage1Request req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "stage1",
            ProjectId = projectId,
            Message = "Starting Stage 1 (C# Grok chat)…",
            Index = 0,
            Total = 0,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            await AppendLogAsync("Stage 1: Stage1Service (Grok chat)");
            var result = await _stage1.RunAsync(
                projectId,
                chunkPages: Math.Clamp(req.ChunkPages, 5, 30),
                totalMinutes: req.TotalMinutes,
                model: string.IsNullOrWhiteSpace(req.Model) ? "grok-4.5" : req.Model,
                resume: req.Resume,
                maxChunks: req.MaxChunks,
                onProgress: line =>
                {
                    // Awaited via GetAwaiter so Grok chunk lines aren't dropped by fire-and-forget races
                    ReportStage1ProgressAsync(line).GetAwaiter().GetResult();
                },
                ct: ct);

            var msg =
                $"Stage 1 complete: {result.SceneCount} scenes · {result.CharacterCount} chars · " +
                $"{result.LocationCount} locs · ~{result.RuntimeSeconds}s";
            if (result.HardErrors.Count > 0)
                msg += $" · {result.HardErrors.Count} verify warning(s)";
            await FinishAsync(result.Ok || result.SceneCount > 0 ? "done" : "error", msg,
                result.Ok ? null : string.Join("; ", result.HardErrors.Take(3)));
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 1 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunStage2Async(StartStage2Request req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "stage2",
            ProjectId = projectId,
            Message = "Starting Stage 2 planner (C#)…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            await AppendLogAsync("Stage 2: C# Stage2PlannerService (deterministic, no API)");
            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(() => _stage2.PlanAsync(
                projectId,
                resolution: string.IsNullOrWhiteSpace(req.Resolution) ? "720p" : req.Resolution,
                scenes: string.IsNullOrWhiteSpace(req.Scenes) ? "all" : req.Scenes,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s => s.Message = line);
                }), ct);

            await FinishAsync(
                "done",
                $"Stage 2 complete: {result.SceneCount} scenes · {result.ClipCount} clips · ~{result.DurationSeconds}s");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 2 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    public async Task StartRemuxAsync(StartRemuxRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException("projectId required");
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunRemuxAsync(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);
        await Task.CompletedTask;
    }

    private async Task RunRemuxAsync(StartRemuxRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        _projects.Activate(projectId);
        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "remux",
            ProjectId = projectId,
            Scene = req.Scene,
            Message = "Remux / WIP…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();
        try
        {
            if (!_remux.IsAvailable())
                throw new InvalidOperationException(
                    "ffmpeg not found. Install ffmpeg and ensure it is on PATH (or set FilmStudio:FfmpegPath).");

            if (req.Scene is int sn && sn > 0)
            {
                await _remux.RemuxSceneAsync(projectId, sn,
                    line => { _ = OnRemuxProgressAsync(line); }, ct);
            }
            if (req.RebuildWip)
            {
                await _remux.RebuildWipAsync(projectId,
                    line => { _ = OnRemuxProgressAsync(line); }, ct);
            }
            await FinishAsync("done", "Remux / WIP complete");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Remux failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunBatchGenAsync(StartBatchGenRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        var scenes = req.Scenes.Distinct().OrderBy(s => s).ToList();
        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "batch",
            ProjectId = projectId,
            Message = $"Batch: {scenes.Count} scene(s)…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            if (!_grok.IsConfigured)
                throw new InvalidOperationException("XAI_API_KEY is not set.");

            using var bp = _projects.LoadBlueprint(projectId)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            if (req.RequireLockedCharacters)
            {
                foreach (var sn in scenes)
                    EnsureCharactersLocked(projectId, sn);
            }

            var projectDir = _projects.GetProjectDir(projectId);
            Directory.CreateDirectory(Path.Combine(projectDir, "assets", "video"));

            // Pre-count work units
            var work = new List<(int Scene, int Clip, JsonElement ClipEl)>();
            foreach (var sn in scenes)
            {
                var sceneEl = FindScene(bp.RootElement, sn);
                if (sceneEl is null)
                {
                    await AppendLogAsync($"Scene {sn}: not in blueprint — skip");
                    continue;
                }
                if (!sceneEl.Value.TryGetProperty("veo_clips", out var clipsEl) ||
                    clipsEl.ValueKind != JsonValueKind.Array)
                {
                    await AppendLogAsync($"Scene {sn}: no veo_clips — skip");
                    continue;
                }

                foreach (var c in clipsEl.EnumerateArray())
                {
                    var cn = c.TryGetProperty("clip_number", out var n) && n.TryGetInt32(out var v) ? v : 0;
                    if (cn <= 0) continue;
                    var path = Path.Combine(projectDir, "assets", "video", $"scene_{sn:D2}_clip_{cn:D2}.mp4");
                    var missing = !File.Exists(path) || new FileInfo(path).Length < 1024;
                    if (!req.OnlyMissing || missing)
                        work.Add((sn, cn, c.Clone()));
                }
            }

            if (work.Count == 0)
            {
                await AppendLogAsync("Batch: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            await UpdateAsync(s =>
            {
                s.Total = work.Count;
                s.Index = 0;
                s.Message = $"Batch: {work.Count} clip(s) across {scenes.Count} scene(s)";
            });
            await AppendLogAsync(_snapshot.Message!);

            var done = 0;
            var failed = 0;
            for (var i = 0; i < work.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (sn, cn, clip) = work[i];
                await UpdateAsync(s =>
                {
                    s.Index = i + 1;
                    s.Scene = sn;
                    s.Clip = cn;
                    s.Message = $"Generating S{sn:D2} C{cn} ({i + 1}/{work.Count})…";
                });
                await AppendLogAsync(_snapshot.Message!);

                try
                {
                    await GenerateOneClipAsync(projectDir, sn, cn, clip, ct);
                    done++;
                    await AppendLogAsync($"Done S{sn:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", sn, cn);
                    await AppendLogAsync($"Failed S{sn:D2} C{cn}: {ex.Message}");
                }
            }

            var status = failed > 0 && done == 0 ? "error" : "done";
            var msg = failed > 0
                ? $"Batch finished with errors ({done} ok, {failed} failed)"
                : $"Batch finished ({done} clip(s))";
            await FinishAsync(status, msg, failed > 0 ? msg : null);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunSceneGenAsync(StartSceneGenRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "scene",
            ProjectId = projectId,
            Scene = req.Scene,
            Message = "Starting…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            if (!_grok.IsConfigured)
                throw new InvalidOperationException("XAI_API_KEY is not set.");

            using var bp = _projects.LoadBlueprint(projectId)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            var sceneEl = FindScene(bp.RootElement, req.Scene)
                ?? throw new InvalidOperationException($"Scene {req.Scene} not in blueprint.");

            if (req.RequireLockedCharacters)
                EnsureCharactersLocked(projectId, req.Scene);

            if (!sceneEl.TryGetProperty("veo_clips", out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Scene {req.Scene} has no veo_clips.");
            }

            var clips = clipsEl.EnumerateArray().ToList();
            var projectDir = _projects.GetProjectDir(projectId);
            var videoDir = Path.Combine(projectDir, "assets", "video");
            Directory.CreateDirectory(videoDir);

            var todo = new List<(int ClipNum, JsonElement Clip)>();
            foreach (var c in clips)
            {
                var cn = c.TryGetProperty("clip_number", out var n) && n.TryGetInt32(out var v) ? v : 0;
                if (cn <= 0) continue;
                var path = Path.Combine(videoDir, $"scene_{req.Scene:D2}_clip_{cn:D2}.mp4");
                var missing = !File.Exists(path) || new FileInfo(path).Length < 1024;
                if (!req.OnlyMissing || missing)
                    todo.Add((cn, c.Clone()));
            }

            if (todo.Count == 0)
            {
                await AppendLogAsync($"Scene {req.Scene}: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            var startMsg = $"Scene {req.Scene}: {todo.Count} clip(s)";
            await UpdateAsync(s =>
            {
                s.Total = todo.Count;
                s.Index = 0;
                s.Message = startMsg;
            });
            await AppendLogAsync(startMsg);

            var done = 0;
            var failed = 0;
            for (var i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (cn, clip) = todo[i];
                await UpdateAsync(s =>
                {
                    s.Index = i + 1;
                    s.Clip = cn;
                    s.Message = $"Generating S{req.Scene:D2} C{cn} ({i + 1}/{todo.Count})…";
                });
                await AppendLogAsync(_snapshot.Message!);

                try
                {
                    await GenerateOneClipAsync(projectDir, req.Scene, cn, clip, ct);
                    done++;
                    await AppendLogAsync($"Done S{req.Scene:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", req.Scene, cn);
                    await AppendLogAsync($"Failed S{req.Scene:D2} C{cn}: {ex.Message}");
                }
            }

            var status = failed > 0 && done == 0 ? "error" : "done";
            var msg = failed > 0
                ? $"Finished with errors ({done} ok, {failed} failed)"
                : $"Generation finished ({done} clip(s))";
            await FinishAsync(status, msg, failed > 0 ? msg : null);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scene gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task GenerateOneClipAsync(
        string projectDir,
        int scene,
        int clip,
        JsonElement clipEl,
        CancellationToken ct)
    {
        var prompt = ClipVideoPromptBuilder.BuildPrompt(clipEl, projectDir);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("clip missing visual_prompt");

        var refPaths = ClipVideoPromptBuilder.FindCharacterRefPaths(clipEl, projectDir, maxRefs: 3);
        if (refPaths.Count > 0)
            await AppendLogAsync(
                $"  [Refs] {refPaths.Count}: {string.Join(", ", refPaths.Select(Path.GetFileName))}");

        var duration = _opts.DefaultDurationSeconds;
        if (clipEl.TryGetProperty("duration_seconds", out var d) && d.TryGetInt32(out var ds))
            duration = Math.Clamp(ds, 1, 15);
        if (refPaths.Count > 0)
            duration = Math.Min(duration, 10);

        var model = _opts.DefaultModel;
        var resolution = _opts.DefaultResolution;

        await AppendLogAsync(
            $"  [Grok] Submit S{scene:D2}C{clip} duration={duration}s res={resolution} " +
            $"refs={refPaths.Count} promptLen={prompt.Length}");
        var requestId = await _grok.SubmitGenerationAsync(
            prompt, duration, resolution, model, ct, referenceImagePaths: refPaths);
        await AppendLogAsync($"  [Grok] request_id={requestId}");

        var url = await _grok.PollForVideoUrlAsync(
            requestId,
            msg => { _ = AppendLogAsync($"  [Grok] {msg}"); },
            ct);

        var outPath = Path.Combine(
            projectDir, "assets", "video", $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        await _grok.DownloadToFileAsync(url, outPath, ct);
        await AppendLogAsync($"  [Grok] saved {outPath}");

        try
        {
            var cont = clipEl.TryGetProperty("veo_continuation_source", out var ce)
                ? ce.GetString() ?? "none"
                : "none";
            var projectId = _snapshot.ProjectId ?? _projects.ActiveProjectId;
            _costs.RecordVideoGeneration(
                projectId,
                scene,
                clip,
                duration,
                resolution,
                model,
                hasRefImage: refPaths.Count > 0,
                isExtend: string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase),
                requestId: requestId);
            await AppendLogAsync($"  [Cost] tracked list-rate for S{scene:D2}C{clip}");
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Cost] ledger write skipped: {ex.Message}");
        }
    }

    private static JsonElement? FindScene(JsonElement root, int sceneNum)
    {
        if (!root.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in scenes.EnumerateArray())
        {
            if (s.TryGetProperty("scene_number", out var n) && n.TryGetInt32(out var sn) && sn == sceneNum)
                return s;
        }
        return null;
    }

    private void EnsureCharactersLocked(string projectId, int sceneNumber)
    {
        var unlocked = _projects.GetUnlockedOnScreenCharacters(projectId, sceneNumber);
        if (unlocked.Count == 0)
            return;

        var names = string.Join(", ", unlocked);
        throw new InvalidOperationException(
            $"Scene {sceneNumber}: locked character refs required before video gen. " +
            $"Missing lock(s): {names}. " +
            "Open Characters → lock a book plate or generate + lock a portrait. " +
            "(Narrator is voice-only and does not need an image.)");
    }

    /// <summary>
    /// Remux progress → job Message + log + SignalR JobLog.
    /// Parses <c>(12.3%)</c> from ffmpeg progress lines into Index/Total (0–1000).
    /// </summary>
    private async Task OnRemuxProgressAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Prefer compact progress lines for the badge; still log everything.
        var isProgress = line.Contains('[') && (
            line.Contains("time ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains('%') ||
            line.Contains("frame ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("speed ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("complete", StringComparison.OrdinalIgnoreCase));

        int? pctTenths = null;
        var m = System.Text.RegularExpressions.Regex.Match(line, @"\((\d+(?:\.\d+)?)%\)");
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
        {
            pctTenths = (int)Math.Clamp(Math.Round(pct * 10), 0, 1000);
        }

        await UpdateAsync(s =>
        {
            s.Log.Add(line);
            if (s.Log.Count > 120)
                s.Log = s.Log.TakeLast(120).ToList();
            // Keep live badge on progress; still update message for probe/errors
            if (isProgress || s.Message is null || s.Message.Length == 0 ||
                line.StartsWith("ffmpeg:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Remux", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("WIP", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Probing", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("complete", StringComparison.OrdinalIgnoreCase))
            {
                s.Message = line;
            }
            if (pctTenths is int p)
            {
                s.Total = 1000;
                s.Index = p;
            }
        });

        if (_sink is not null)
            await _sink.OnJobLogAsync(line);
    }

    private async Task ReportStage1ProgressAsync(string line)
    {
        // Single UpdateAsync so Index/Total + log stay atomic (no race losing counters)
        await UpdateAsync(s =>
        {
            if (s.Log.Count == 0 || s.Log[^1] != line)
            {
                s.Log.Add(line);
                if (s.Log.Count > 120)
                    s.Log = s.Log.TakeLast(120).ToList();
            }
            s.Message = line;

            var mChunks = System.Text.RegularExpressions.Regex.Match(
                line, @"Stage 1:\s+(\d+)\s+book chunk", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mChunks.Success && int.TryParse(mChunks.Groups[1].Value, out var totalChunks) && totalChunks > 0)
            {
                s.Total = Math.Max(s.Total, totalChunks);
                if (s.Index < 0) s.Index = 0;
                return;
            }

            var m = System.Text.RegularExpressions.Regex.Match(
                line, @"chunk\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out var idx) &&
                int.TryParse(m.Groups[2].Value, out var tot) &&
                tot > 0)
            {
                s.Index = Math.Max(s.Index, idx);
                s.Total = Math.Max(s.Total, tot);
                return;
            }

            var mVis = System.Text.RegularExpressions.Regex.Match(
                line, @"Grok vision\s+(\d+)/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mVis.Success &&
                int.TryParse(mVis.Groups[1].Value, out var vi) &&
                int.TryParse(mVis.Groups[2].Value, out var vt) &&
                vt > 0)
            {
                s.Index = Math.Max(s.Index, vi);
                s.Total = Math.Max(s.Total, vt);
            }
        });
        if (_sink is not null)
            await _sink.OnJobLogAsync(line);
    }

    private async Task AppendLogAsync(string message)
    {
        _logLines.Enqueue(message);
        await UpdateAsync(s =>
        {
            // Avoid duplicate consecutive lines (AppendLog after Update that already set Message)
            if (s.Log.Count == 0 || s.Log[^1] != message)
            {
                s.Log.Add(message);
                if (s.Log.Count > 120)
                    s.Log = s.Log.TakeLast(120).ToList();
            }
            s.Message = message;
        });
        if (_sink is not null)
            await _sink.OnJobLogAsync(message);
    }

    private async Task UpdateAsync(Action<JobSnapshot> mutate)
    {
        await _snapLock.WaitAsync();
        try
        {
            mutate(_snapshot);
            await PublishAsync();
        }
        finally
        {
            _snapLock.Release();
        }
    }

    private async Task FinishAsync(string status, string message, string? error = null)
    {
        await UpdateAsync(s =>
        {
            s.Status = status;
            s.Message = message;
            s.Error = error;
            s.FinishedAt = DateTimeOffset.UtcNow;
            if (s.Total > 0 && status == "done")
                s.Index = s.Total;
        });
        await AppendLogAsync(message);
    }

    private async Task PublishAsync()
    {
        if (_sink is not null)
            await _sink.OnJobUpdatedAsync(Clone(_snapshot));
    }

    private static JobSnapshot Clone(JobSnapshot s) => new()
    {
        Status = s.Status,
        Kind = s.Kind,
        Message = s.Message,
        ProjectId = s.ProjectId,
        CharKey = s.CharKey,
        Scene = s.Scene,
        Clip = s.Clip,
        Index = s.Index,
        Total = s.Total,
        Log = s.Log.ToList(),
        Error = s.Error,
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
    };
}
