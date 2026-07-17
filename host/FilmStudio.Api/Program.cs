using System.Text.Json;
using FilmStudio.Api.Hubs;
using FilmStudio.Api.Services;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using FilmStudio.Engine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FilmStudioOptions>(
    builder.Configuration.GetSection(FilmStudioOptions.SectionName));

// Default workspace = repo root (two levels up from host/FilmStudio.Api)
var repoGuess = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
builder.Services.PostConfigure<FilmStudioOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.WorkspaceRoot) || !Directory.Exists(o.WorkspaceRoot))
        o.WorkspaceRoot = repoGuess;
});

builder.Services.AddSingleton<MediaDurationProbe>();
builder.Services.AddSingleton<ProjectStore>();
builder.Services.AddSingleton<CostReportService>();
builder.Services.AddSingleton<CharacterDesignService>();
builder.Services.AddSingleton<CharacterBookPlateService>();
builder.Services.AddSingleton<BookPrepareService>();
builder.Services.AddSingleton<Stage1Service>();
builder.Services.AddSingleton<Stage2PlannerService>();
builder.Services.AddSingleton<FfmpegRemuxService>();
builder.Services.AddSingleton<EditLogService>();
builder.Services.AddSingleton<FilmJobService>();
builder.Services.AddSingleton<IJobProgressSink, SignalRJobProgressSink>();
builder.Services.AddHttpClient<GrokVideoClient>(c =>
{
    c.BaseAddress = new Uri(GrokVideoClient.ApiBase + "/");
    c.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddHttpClient<GrokImageClient>(c =>
{
    c.BaseAddress = new Uri(GrokImageClient.ApiBase + "/");
    c.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<GrokVisionClient>(c =>
{
    c.BaseAddress = new Uri(GrokVisionClient.ApiBase + "/");
    c.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<GrokChatClient>(c =>
{
    c.BaseAddress = new Uri(GrokChatClient.ApiBase + "/");
    c.Timeout = TimeSpan.FromMinutes(20);
});

builder.Services.AddSignalR();
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p =>
        p.AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true));
});

var app = builder.Build();

// Wire SignalR sink into job service
var jobs = app.Services.GetRequiredService<FilmJobService>();
jobs.SetProgressSink(app.Services.GetRequiredService<IJobProgressSink>());

app.UseCors();
app.MapHub<JobHub>("/hubs/jobs");

app.MapGet("/health", (ProjectStore store) =>
    Results.Ok(new
    {
        ok = true,
        service = "FilmStudio.Api",
        workspace = store.WorkspaceRoot,
        activeProject = store.ActiveProjectId,
        xaiConfigured = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("XAI_API_KEY")),
    }));

app.MapGet("/api/projects", (ProjectStore store) =>
{
    var list = store.ListProjects();
    var activeId = store.ActiveProjectId;
    var active = list.FirstOrDefault(p =>
        string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase));
    return Results.Ok(new { ok = true, active, projects = list });
});

app.MapPost("/api/projects/{id}/activate", (string id, ProjectStore store) =>
{
    try
    {
        var p = store.Activate(id);
        return Results.Ok(new { ok = true, active = p });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/jobs", (FilmJobService jobService) =>
{
    var snap = jobService.GetSnapshot();
    return Results.Ok(new
    {
        ok = true,
        running = jobService.IsRunning,
        job = snap,
    });
});

app.MapPost("/api/jobs/gen-scene", async (StartSceneGenRequest body, FilmJobService jobService) =>
{
    try
    {
        if (body.Scene <= 0)
            return Results.BadRequest(new { ok = false, error = "scene required" });
        await jobService.StartSceneGenAsync(body);
        return Results.Accepted("/api/jobs", new
        {
            ok = true,
            message = $"Started scene {body.Scene}",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

app.MapPost("/api/jobs/gen-batch", async (StartBatchGenRequest body, FilmJobService jobService) =>
{
    try
    {
        if (body.Scenes is null || body.Scenes.Count == 0)
            return Results.BadRequest(new { ok = false, error = "scenes required" });
        await jobService.StartBatchGenAsync(body);
        return Results.Accepted("/api/jobs", new
        {
            ok = true,
            message = $"Started batch for {body.Scenes.Count} scene(s)",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

app.MapPost("/api/jobs/cancel", async (FilmJobService jobService) =>
{
    await jobService.CancelAsync();
    return Results.Ok(new { ok = true, job = jobService.GetSnapshot() });
});

app.MapGet("/api/stage2-status", (ProjectStore store) =>
{
    var id = store.ActiveProjectId;
    if (string.IsNullOrEmpty(id))
        return Results.Ok(new { ok = true, stage2_ready = false });
    var bp = store.FindBlueprintPath(id);
    var ready = bp is not null && File.Exists(bp);
    var scenes = 0;
    var clips = 0;
    if (ready)
    {
        try
        {
            using var doc = store.LoadBlueprint(id);
            if (doc is not null &&
                doc.RootElement.TryGetProperty("scenes", out var sc) &&
                sc.ValueKind == JsonValueKind.Array)
            {
                scenes = sc.GetArrayLength();
                foreach (var s in sc.EnumerateArray())
                {
                    if (s.TryGetProperty("veo_clips", out var vc) &&
                        vc.ValueKind == JsonValueKind.Array)
                        clips += vc.GetArrayLength();
                }
            }
        }
        catch { /* ignore */ }
    }
    return Results.Ok(new
    {
        ok = true,
        stage2_ready = ready && clips > 0,
        stage2_scenes = scenes,
        stage2_clips = clips,
        blueprint_path = bp,
        project_id = id,
    });
});

// ---- Configuration (pipeline_config.json) ----
app.MapGet("/api/projects/{id}/config", (string id, ProjectStore store) =>
{
    try
    {
        var cfg = store.GetConfig(id);
        return Results.Ok(new { ok = true, projectId = id, config = cfg });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPut("/api/projects/{id}/config", async (string id, HttpRequest req, ProjectStore store) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var saved = store.SaveConfig(id, doc.RootElement);
        return Results.Ok(new { ok = true, projectId = id, config = saved });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// ---- Characters ----
app.MapGet("/api/projects/{id}/characters", (string id, ProjectStore store) =>
{
    try
    {
        var chars = store.ListCharacters(id);
        var plates = store.GetCharacterPlatesState(id);
        var seedLimits = store.GetImageSeedLimits(id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            characters = chars,
            // Plates live on scenes.json seeds; this flag tracks whether import sorted them
            characterPlates = plates,
            // Grok ≤ 3 refs, Gemini ≤ 14 — UI ranks more, sends only maxReferenceImages
            imageSeedLimits = seedLimits,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{projectId}/characters/{charKey}/ref", (string projectId, string charKey, ProjectStore store) =>
{
    try
    {
        var path = store.ResolveCharacterRefPath(projectId, charKey);
        if (path is null || !File.Exists(path))
            return Results.NotFound(new { ok = false, error = "ref image not found" });
        return Results.File(path, GuessImageContentType(path));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{projectId}/characters/{charKey}/variants/{index:int}",
    (string projectId, string charKey, int index, ProjectStore store) =>
{
    try
    {
        var path = store.ResolveCharacterVariantPath(projectId, charKey, index);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "variant not found" });
        return Results.File(path, GuessImageContentType(path));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{projectId}/characters/{charKey}/bookrefs/{index:int}",
    (string projectId, string charKey, int index, ProjectStore store) =>
{
    try
    {
        var path = store.ResolveCharacterBookRefPath(projectId, charKey, index);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "book ref not found" });
        return Results.File(path, GuessImageContentType(path));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/jobs/character-variants", async (StartCharacterVariantsRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || string.IsNullOrWhiteSpace(body.CharKey))
            return Results.BadRequest(new { ok = false, error = "projectId and charKey required" });
        await jobService.StartCharacterVariantsAsync(body);
        return Results.Accepted("/api/jobs", new
        {
            ok = true,
            message = $"Started portrait generation for {body.CharKey}",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

/// <summary>
/// Sync heuristic attach (no Grok). Prefer POST /api/jobs/sort-character-plates for vision sort.
/// </summary>
app.MapPost("/api/projects/{id}/characters/attach-book-plates", (
    string id,
    AttachCharacterPlatesRequest? body,
    CharacterBookPlateService plates) =>
{
    try
    {
        body ??= new AttachCharacterPlatesRequest();
        var result = plates.Attach(
            id,
            force: body.Force,
            copyIntoAssets: body.CopyIntoAssets,
            onlyCharKey: body.CharKey);
        return result.Ok
            ? Results.Ok(new { ok = true, projectId = id, attach = result })
            : Results.BadRequest(new { ok = false, projectId = id, attach = result, error = result.Reason });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Job: Grok vision sorts book images onto characters → scenes.json design_reference_images.
/// Progress via SignalR; cancel with /api/jobs/cancel.
/// </summary>
app.MapPost("/api/jobs/sort-character-plates", async (
    AttachCharacterPlatesRequest body,
    FilmJobService jobService) =>
{
    try
    {
        body.Force = true; // explicit user/job start always re-sorts
        if (body.MaxImages <= 0) body.MaxImages = 32;
        await jobService.StartSortCharacterPlatesAsync(body);
        return Results.Ok(new
        {
            ok = true,
            message = body.UseGrok
                ? "Started Grok vision character plate sort"
                : "Started heuristic character plate sort",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

app.MapPost("/api/projects/{id}/characters/{charKey}/lock-variant",
    async (string id, string charKey, HttpRequest req, FilmJobService jobService) =>
{
    try
    {
        var index = 1;
        if (req.HasJsonContentType())
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            if (doc.RootElement.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n))
                index = n;
            else if (doc.RootElement.TryGetProperty("variantIndex", out var vx) && vx.TryGetInt32(out var n2))
                index = n2;
        }
        var result = await jobService.RunCharacterDesignActionAsync(id, "lock-variant", charKey, index);
        return Results.Ok(new { ok = true, message = result, projectId = id, charKey, index });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/characters/{charKey}/lock-bookref",
    async (string id, string charKey, HttpRequest req, FilmJobService jobService) =>
{
    try
    {
        var index = 0;
        if (req.HasJsonContentType())
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            if (doc.RootElement.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n))
                index = n;
        }
        // variantIndex slot reused as book-ref index for lock-bookref
        var result = await jobService.RunCharacterDesignActionAsync(
            id, "lock-bookref", charKey, variantIndex: index);
        return Results.Ok(new { ok = true, message = result, projectId = id, charKey, index });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/characters/{charKey}/unlock",
    async (string id, string charKey, FilmJobService jobService) =>
{
    try
    {
        var result = await jobService.RunCharacterDesignActionAsync(id, "unlock", charKey);
        return Results.Ok(new { ok = true, message = result, projectId = id, charKey });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

static string GuessImageContentType(string path) =>
    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
    : path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
      path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
    : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
    : "application/octet-stream";

// ---- Adaptation (book / Stage 1 / Stage 2 status + jobs) ----
app.MapGet("/api/projects/{id}/adaptation", (string id, ProjectStore store) =>
{
    try
    {
        var status = store.GetAdaptationStatus(id);
        return Results.Ok(new { ok = true, projectId = id, adaptation = status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/jobs/book-prepare", async (StartBookPrepareRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        await jobService.StartBookPrepareAsync(body);
        return Results.Accepted("/api/jobs", new
        {
            ok = true,
            message = "Started book prepare (C# PDF extract / vision OCR)",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

app.MapPost("/api/projects/{id}/adaptation/upload", async (string id, HttpRequest req, ProjectStore store) =>
{
    try
    {
        if (!req.HasFormContentType)
            return Results.BadRequest(new { ok = false, error = "multipart form required" });
        var form = await req.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { ok = false, error = "file required" });
        await using var stream = file.OpenReadStream();
        var path = await store.SaveBookUploadAsync(id, file.FileName, stream);
        var status = store.GetAdaptationStatus(id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            savedPath = path,
            message = $"Saved {file.FileName} ({file.Length} bytes)",
            adaptation = status,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/jobs/stage1", async (StartStage1Request body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        await jobService.StartStage1Async(body);
        return Results.Accepted("/api/jobs", new
        {
            ok = true,
            message = "Started Stage 1 (C# Grok chat)",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

app.MapPost("/api/jobs/stage2", async (StartStage2Request body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        await jobService.StartStage2Async(body);
        return Results.Accepted("/api/jobs", new
        {
            ok = true,
            message = "Started Stage 2 (C# planner)",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

app.MapPost("/api/jobs/remux", async (StartRemuxRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        await jobService.StartRemuxAsync(body);
        return Results.Accepted("/api/jobs", new
        {
            ok = true,
            message = "Started remux / WIP",
            job = jobService.GetSnapshot(),
        });
    }
    catch (Exception ex)
    {
        return Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() });
    }
});

// ---- Review / edit log ----
app.MapGet("/api/projects/{id}/edit-log", (string id, EditLogService logs) =>
{
    try
    {
        var doc = logs.Load(id);
        return Results.Ok(new { ok = true, projectId = id, editLog = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/clips/review", (string id, ClipReviewRequest body, EditLogService logs) =>
{
    try
    {
        body.ProjectId = id;
        logs.SetClipReview(id, body.Scene, body.Clip, body.Status, body.Note);
        return Results.Ok(new { ok = true, projectId = id, scene = body.Scene, clip = body.Clip, status = body.Status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/approve", async (
    string id, int scene, SceneApproveRequest? body, EditLogService logs, FilmJobService jobs) =>
{
    try
    {
        body ??= new SceneApproveRequest();
        logs.MarkSceneApproved(id, scene, body.Note ?? "");
        if (body.Remux || body.RebuildWip)
        {
            await jobs.StartRemuxAsync(new StartRemuxRequest
            {
                ProjectId = id,
                Scene = body.Remux ? scene : null,
                RebuildWip = body.RebuildWip,
            });
        }
        return Results.Ok(new { ok = true, projectId = id, scene, message = "Scene approved" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/clip-reviews", (string id, EditLogService logs) =>
{
    try
    {
        var map = logs.GetClipReviewMap(id);
        return Results.Ok(new { ok = true, projectId = id, reviews = map });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// ---- Cost (ledger + estimates) ----
app.MapGet("/api/projects/{id}/cost", (
    string id,
    ProjectStore store,
    CostReportService costs,
    string? draftResolution,
    string? heroResolution,
    double? assumeAvgRetries) =>
{
    try
    {
        _ = store.GetProject(id) ?? throw new InvalidOperationException($"Unknown project: {id}");
        var report = costs.GetReport(id, draftResolution, heroResolution, assumeAvgRetries);
        return Results.Ok(new { ok = true, projectId = id, cost = report });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/cost/backfill", (string id, ProjectStore store, CostReportService costs) =>
{
    try
    {
        _ = store.GetProject(id) ?? throw new InvalidOperationException($"Unknown project: {id}");
        var result = costs.BackfillFromDisk(id, onlyMissing: true);
        return Results.Ok(new { ok = true, projectId = id, backfill = result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// ---- Scenes & Clips ----
app.MapGet("/api/projects/{id}/scenes", (string id, ProjectStore store) =>
{
    try
    {
        var scenes = store.ListScenes(id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            sceneCount = scenes.Count,
            clipCount = scenes.Sum(s => s.ClipCount),
            clipsOnDisk = scenes.Sum(s => s.ClipsOnDisk),
            scenes,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}", (string id, int sceneNumber, ProjectStore store) =>
{
    try
    {
        var detail = store.GetSceneDetail(id, sceneNumber);
        if (detail is null)
            return Results.NotFound(new { ok = false, error = $"Scene {sceneNumber} not found" });
        return Results.Ok(new { ok = true, projectId = id, scene = detail });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}/clips/{clipNumber:int}/video",
    (string id, int sceneNumber, int clipNumber, ProjectStore store) =>
{
    try
    {
        var path = store.ResolveClipVideoPath(id, sceneNumber, clipNumber);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "clip video not found" });
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}/composite",
    (string id, int sceneNumber, ProjectStore store) =>
{
    try
    {
        var path = store.ResolveCompositePath(id, sceneNumber);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "composite not found" });
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Stream the WIP full movie (assets/movie_wip.mp4 by default).</summary>
app.MapGet("/api/projects/{id}/movie/wip", (string id, ProjectStore store) =>
{
    try
    {
        var path = store.ResolveWipMoviePath(id);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "WIP movie not found — rebuild WIP first" });
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/movie/wip/meta", (string id, ProjectStore store) =>
{
    try
    {
        var f = store.AssessWipFreshness(id);
        return Results.Ok(new
        {
            ok = true,
            exists = f.Exists,
            stale = f.Stale,
            canBuild = f.CanBuild,
            reason = f.Reason,
            projectId = id,
            path = f.Path,
            bytes = f.Bytes,
            updatedAt = f.UpdatedAt,
            staleScenes = f.StaleScenes,
            url = f.Exists
                ? $"/api/projects/{Uri.EscapeDataString(id)}/movie/wip"
                : (string?)null,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.Run();
