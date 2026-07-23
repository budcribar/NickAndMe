using System.Diagnostics;
using System.Text.Json;
using FilmStudio.Api.Auth;
using FilmStudio.Api.Hubs;
using FilmStudio.Api.Services;
using FilmStudio.Core.Auth;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using FilmStudio.Engine;
using FilmStudio.Engine.Abstractions;
using FilmStudio.Fakes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var processStartedUtc = DateTimeOffset.UtcNow;

builder.Services.Configure<FilmStudioOptions>(
    builder.Configuration.GetSection(FilmStudioOptions.SectionName));

// Optional ThreadPool pre-warm (FilmStudio:ThreadPool:MinWorkerThreads) for 100-VU ramps.
// 0 / unset = CLR defaults. Apply before host starts accepting requests.
{
    var tp = builder.Configuration.GetSection(FilmStudioOptions.SectionName)
        .GetSection("ThreadPool");
    var minWorkers = tp.GetValue("MinWorkerThreads", 0);
    var minIo = tp.GetValue("MinIoThreads", 0);
    if (minWorkers > 0 || minIo > 0)
    {
        ThreadPool.GetMinThreads(out var curW, out var curIo);
        ThreadPool.GetMaxThreads(out var maxW, out var maxIo);
        var w = minWorkers > 0 ? Math.Clamp(minWorkers, 1, maxW) : curW;
        var io = minIo > 0
            ? Math.Clamp(minIo, 1, maxIo)
            : (minWorkers > 0 ? Math.Clamp(minWorkers, 1, maxIo) : curIo);
        if (w < curW) w = curW;
        if (io < curIo) io = curIo;
        if (ThreadPool.SetMinThreads(w, io))
            Console.WriteLine($"ThreadPool min threads set: workers={w} io={io} (was {curW}/{curIo})");
        else
            Console.WriteLine($"ThreadPool SetMinThreads failed (requested workers={w} io={io})");
    }
}

// Default workspace = repo root (two levels up from host/FilmStudio.Api)
var repoGuess = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
builder.Services.PostConfigure<FilmStudioOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.WorkspaceRoot) || !Directory.Exists(o.WorkspaceRoot))
        o.WorkspaceRoot = repoGuess;
});

builder.Services.AddSingleton<MediaDurationProbe>();
builder.Services.AddSingleton<SceneListCache>();
builder.Services.AddSingleton<ProjectReadCache>();
builder.Services.AddSingleton<ProjectStore>();
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<ILockService, InMemoryLockService>();
builder.Services.AddSingleton<IServerMetricsService, ServerMetricsService>();
builder.Services.AddSingleton<IRuntimeConfigStore, RuntimeConfigStore>();
builder.Services.AddSingleton<ApiWorkerPool>();
builder.Services.AddSingleton<LocalWorkerPool>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddSingleton<CostReportService>();
builder.Services.AddSingleton<CharacterDesignService>();
builder.Services.AddSingleton<CharacterBookPlateService>();
builder.Services.AddSingleton<CastVisualLiteralizeService>();
builder.Services.AddSingleton<CastFromScreenplayService>();
builder.Services.AddSingleton<BookPrepareService>();
builder.Services.AddSingleton<Stage1Service>();
builder.Services.AddSingleton<SilentBeatActionClassifier>();
builder.Services.AddSingleton<AmbientSfxClassifier>();
builder.Services.AddSingleton<OnScreenCastClassifier>();
builder.Services.AddSingleton<ExtendCutClassifier>();
builder.Services.AddSingleton<SpeciesKindClassifier>();
builder.Services.AddSingleton<PlateRankClassifier>();
builder.Services.AddSingleton<ShotPlanRefiningClassifier>();
builder.Services.AddSingleton<BeatPacingClassifier>();
builder.Services.AddSingleton<CinematicLightingClassifier>();
builder.Services.AddSingleton<CameraDirectorClassifier>();
builder.Services.AddSingleton<NegativePromptClassifier>();
builder.Services.AddSingleton<WardrobeContinuityClassifier>();
builder.Services.AddSingleton<CharacterEmotionArcClassifier>();
builder.Services.AddSingleton<SoundDesignComposerClassifier>();
builder.Services.AddSingleton<DepthOfFieldClassifier>();
builder.Services.AddSingleton<ColorPaletteGradingClassifier>();
builder.Services.AddSingleton<Stage2PlannerService>();
builder.Services.AddSingleton<FfmpegRemuxService>();
builder.Services.AddSingleton<IFfmpegRemux>(sp => sp.GetRequiredService<FfmpegRemuxService>());
builder.Services.AddSingleton<VoicePreviewService>();
builder.Services.AddSingleton<ReviewEventStore>();
builder.Services.AddSingleton<PromptPackService>();
builder.Services.AddSingleton<ProjectRulesService>();
builder.Services.AddSingleton<LearningProposalService>();
builder.Services.AddSingleton<ProposalChecklistService>();
builder.Services.AddSingleton<EditLogService>();
builder.Services.AddSingleton<ProjectTelemetryService>();
builder.Services.AddSingleton<ReviewIndexService>();
builder.Services.AddSingleton<ClipAutoReviewService>();
builder.Services.AddSingleton<ProjectArtifactIndexService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IUserContext, HttpUserContext>();
builder.Services.AddSingleton<IUserApiKeyProvider, ConfigUserApiKeyProvider>();
builder.Services.AddSingleton<IAdminAuthService, AdminAuthService>();
builder.Services.AddSingleton<FilmJobService>();
builder.Services.AddSingleton<IJobProgressSink, SignalRJobProgressSink>();
builder.Services.AddSingleton<AdminMetricsPushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdminMetricsPushService>());
builder.Services.AddSingleton<HttpRequestMetrics>();
builder.Services.AddSingleton<LoadSimLiveStore>();
builder.Services.AddSingleton<ProcessHistoryStore>();

// Grok clients: real HttpClient or fakes (FilmStudio:UseFakes)
var useFakes = builder.Configuration.GetValue("FilmStudio:UseFakes", false)
    || string.Equals(Environment.GetEnvironmentVariable("FILMSTUDIO_USE_FAKES"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("FILMSTUDIO_USE_FAKES"), "true", StringComparison.OrdinalIgnoreCase);

if (useFakes)
{
    builder.Services.AddFilmStudioFakes();
}
else
{
    // Concrete provider clients — each gets its own named HttpClient + base address.
    builder.Services.AddHttpClient<GrokVideoClient>(c =>
    {
        c.BaseAddress = new Uri(GrokVideoClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(15);
    });
    builder.Services.AddHttpClient<GeminiVideoClient>(c =>
    {
        c.BaseAddress = new Uri(GeminiVideoClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(15);
    });
    builder.Services.AddHttpClient<GrokImageClient>(c =>
    {
        c.BaseAddress = new Uri(GrokImageClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(5);
    });
    builder.Services.AddHttpClient<GeminiImageClient>(c =>
    {
        c.BaseAddress = new Uri(GeminiImageClient.ApiBase + "/");
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
    builder.Services.AddHttpClient<AnthropicChatClient>(c =>
    {
        c.BaseAddress = new Uri(AnthropicChatClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(20);
    });
    builder.Services.AddHttpClient<GeminiChatClient>(c =>
    {
        c.BaseAddress = new Uri(GeminiChatClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(20);
    });

    // Dispatchers: every existing caller keeps depending on IChatClient / IImageClient /
    // IVideoClient / IVisionClient and is routed to the right concrete provider client
    // per-call based on the requested model (see SupportedModelCatalog). Book-page OCR / cast
    // classify (TranscribePageAsync / ClassifyCharactersOnImageAsync) still only run on Grok in
    // practice — routing one of those to Anthropic or Gemini surfaces the NotSupportedException
    // those clients already throw for them — but clip/frame review (CompleteWithImagesAsync) is
    // real on all three and now follows the configured quality model.
    builder.Services.AddSingleton<IVideoClient, MultiProviderVideoClient>();
    builder.Services.AddSingleton<IImageClient, MultiProviderImageClient>();
    builder.Services.AddSingleton<MultiProviderChatClient>();
    // CachingChatClient wraps the real dispatcher so every classifier / planning call gets an
    // on-disk response cache for free (see CachingChatClient for why this beats local
    // tokenization for "speed"). Registered as itself too so admin endpoints (cache clear) can
    // reach it directly. Fakes mode below stays undecorated — tests assert call counts against
    // the fakes directly.
    builder.Services.AddSingleton(sp => new CachingChatClient(
        sp.GetRequiredService<MultiProviderChatClient>(),
        sp.GetRequiredService<IOptions<FilmStudioOptions>>(),
        sp.GetRequiredService<ILogger<CachingChatClient>>()));
    builder.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<CachingChatClient>());
    builder.Services.AddSingleton<IVisionClient, MultiProviderVisionClient>();
}

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

// Refuse default JWT signing key outside Development (forged admin tokens if key is public).
{
    var authOpts = app.Services.GetRequiredService<IOptions<FilmStudioOptions>>().Value.Auth
                   ?? new AuthOptions();
    var envKey = Environment.GetEnvironmentVariable("FILMSTUDIO_JWT_KEY");
    var effective = !string.IsNullOrWhiteSpace(envKey) ? envKey.Trim() : authOpts.JwtSigningKey;
    if (!app.Environment.IsDevelopment() && AuthOptions.IsInsecureDefaultJwtSigningKey(effective))
    {
        throw new InvalidOperationException(
            "JWT signing key is the insecure development default. " +
            "Set environment variable FILMSTUDIO_JWT_KEY (or Auth:JwtSigningKey) to a unique " +
            "secret of at least 32 characters before running outside Development.");
    }
}

// Wire SignalR sink into job service
var jobs = app.Services.GetRequiredService<FilmJobService>();
jobs.SetProgressSink(app.Services.GetRequiredService<IJobProgressSink>());

app.UseCors();
app.UseMiddleware<HttpRequestMetricsMiddleware>();
app.UseMiddleware<JwtHeaderMiddleware>();
app.MapHub<JobHub>("/hubs/jobs");

// ── Auth (Phase B + D rate limit) ───────────────────────────────────────────
app.MapPost("/api/auth/login", (LoginRequest body, IAdminAuthService auth, LoginRateLimiter limiter, HttpContext http) =>
{
    var key = $"{body.Username ?? ""}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new LoginResponse { Ok = false, Error = $"Too many login attempts. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var result = auth.Login(body.Username ?? "", body.Password ?? "");
    if (!result.Ok)
    {
        limiter.RecordFailure(key);
        return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
    }
    limiter.RecordSuccess(key);
    return Results.Ok(result);
});

app.MapPost("/api/auth/logout", () =>
    Results.Ok(new { ok = true, message = "Client should discard JWT" }));

app.MapGet("/api/auth/me", (IUserContext user, IUserApiKeyProvider keys) =>
{
    var roles = user.Roles.ToList();
    return Results.Ok(new MeResponse
    {
        Ok = true,
        UserId = user.UserId,
        Roles = roles,
        IsAdmin = user.IsAdmin,
        HasApiKey = keys.HasKey(user.UserId) || !string.IsNullOrWhiteSpace(user.RequestApiKey),
    });
});

// Live admin state (Phase C metrics + locks + jobs)
app.MapGet("/api/admin/state", (
    IUserContext user,
    ProjectStore store,
    AdminMetricsPushService metricsPush,
    HttpRequestMetrics httpMetrics,
    LoadSimLiveStore loadSimStore,
    ProcessHistoryStore processHistory) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    var snap = metricsPush.BuildSnapshot();
    var traffic = httpMetrics.Snapshot();
    // Ensure at least one memory sample even before background tick
    if (processHistory.GetHistory().Count == 0)
        processHistory.Sample();
    return Results.Ok(new
    {
        ok = true,
        state = snap,
        projects = new
        {
            active = store.ActiveProjectId,
            workspace = store.WorkspaceRoot,
        },
        caller = new { userId = user.UserId, roles = user.Roles },
        // Flatten common fields for Blazor DTO
        generatedAt = DateTimeOffset.UtcNow,
        process = snap.Process,
        capacity = snap.Capacity,
        jobs = new
        {
            running = snap.Jobs.Any(j =>
                string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase)),
            count = snap.Jobs.Count,
            items = snap.Jobs.Select(j => new
            {
                j.JobId,
                j.UserId,
                j.ProjectId,
                j.Kind,
                j.Scene,
                j.Clip,
                j.Status,
                j.Message,
                j.Index,
                j.Total,
                j.StartedAt,
                ageMs = j.StartedAt is DateTimeOffset s
                    ? (long)(DateTimeOffset.UtcNow - s).TotalMilliseconds
                    : (long?)null,
            }),
        },
        locks = snap.Locks,
        queueByUser = snap.QueueByUser,
        timings = snap.TimingsByKind,
        apiInFlight = snap.ApiInFlight,
        ffmpegInFlight = snap.FfmpegInFlight,
        capacityRejects = snap.CapacityRejects,
        lockConflicts = snap.LockConflicts,
        http = traffic,
        loadSim = loadSimStore.GetState(),
        processHistory = processHistory.GetHistory(),
    });
});

app.MapGet("/api/locks", (ILockService locks, IUserContext user) =>
{
    var list = locks.ListActive();
    return Results.Ok(new { ok = true, locks = list, userId = user.UserId });
});

// LoadSim live telemetry (no admin auth — sim posts from CLI)
app.MapPost("/api/loadsim/progress", (LoadSimProgressDto body, LoadSimLiveStore store) =>
{
    if (body is null)
        return Results.BadRequest(new { ok = false, error = "body required" });
    store.Publish(body);
    return Results.Accepted("/api/admin/loadsim", new { ok = true, runId = body.RunId, status = body.Status });
});

app.MapGet("/api/admin/loadsim", (IUserContext user, LoadSimLiveStore store) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var state = store.GetState();
    return Results.Ok(new { ok = true, loadSim = state });
});

// ── Admin config + actions (Phase D) ────────────────────────────────────────
// ---- Admin Learning (P0–P4) ----
app.MapGet("/api/admin/learning/insights", (
    IUserContext user,
    ReviewEventStore learning,
    string? projectId,
    int? take) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var insights = learning.BuildInsights(projectId, recentTake: take ?? 40);
    return Results.Ok(new { ok = true, insights });
});

app.MapGet("/api/admin/learning/events", (
    IUserContext user,
    ReviewEventStore learning,
    string? projectId,
    string? type,
    string? category,
    int? take) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var events = learning.Query(projectId, type, category, take: take ?? 100);
    return Results.Ok(new { ok = true, events });
});

app.MapGet("/api/admin/learning/packs", (IUserContext user, PromptPackService packs) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var m = packs.GetManifest();
    return Results.Ok(new { ok = true, manifest = m, packs = m.Packs });
});

app.MapGet("/api/admin/learning/packs/{packId}", (string packId, IUserContext user, PromptPackService packs) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var text = packs.LoadPackText(packId);
    if (text is null)
        return Results.NotFound(new { ok = false, error = "pack not found" });
    return Results.Ok(new { ok = true, packId, text });
});

app.MapPost("/api/admin/learning/packs/activate", (
    ActivatePromptPackRequest body,
    IUserContext user,
    PromptPackService packs) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var m = packs.Activate(body.PackId);
        return Results.Ok(new { ok = true, manifest = m });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/admin/learning/packs", (
    CreatePromptPackBody body,
    IUserContext user,
    PromptPackService packs) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var info = packs.CreateVersion(body.Kind ?? "gen", body.Version ?? "next", body.Body ?? "", body.Notes);
        return Results.Ok(new { ok = true, pack = info });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/admin/learning/propose", async (
    ProposeLearningRulesRequest body,
    IUserContext user,
    LearningProposalService proposals,
    ProposalChecklistService checklist,
    CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var result = await proposals.ProposeAsync(body, ct);
    if (result.Ok && !string.IsNullOrWhiteSpace(result.Proposal))
    {
        try
        {
            var list = checklist.IngestProposal(
                result.Proposal,
                sourceLabel: $"propose_fails_n{body.LastNFails}");
            return Results.Ok(new
            {
                result.Ok,
                result.Proposal,
                result.FailEventsUsed,
                result.Categories,
                result.Error,
                checklist = list,
            });
        }
        catch { /* still return proposal */ }
    }
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/admin/learning/proposal-checklist", (
    IUserContext user,
    ProposalChecklistService checklist) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(new { ok = true, checklist = checklist.Load() });
});

app.MapPost("/api/admin/learning/proposal-checklist", (
    ProposalChecklistUpsertRequest body,
    IUserContext user,
    ProposalChecklistService checklist) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = checklist.Upsert(body ?? new ProposalChecklistUpsertRequest());
        return Results.Ok(new { ok = true, checklist = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/admin/learning/proposal-checklist/toggle", (
    ProposalChecklistToggleRequest body,
    IUserContext user,
    ProposalChecklistService checklist) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = checklist.Toggle(body ?? new ProposalChecklistToggleRequest());
        return Results.Ok(new { ok = true, checklist = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Mark checklist items done when matching project-rule text is approved.</summary>
app.MapPost("/api/admin/learning/proposal-checklist/accept-matching", (
    ProposalChecklistAcceptMatchingRequest body,
    IUserContext user,
    ProposalChecklistService checklist) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = checklist.MarkAcceptedFromRuleTexts(
            body?.Texts ?? new List<string>(),
            body?.Disposition ?? "accepted",
            body?.Note);
        return Results.Ok(new { ok = true, checklist = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/admin/learning/project-rules/{projectId}", (
    string projectId,
    IUserContext user,
    ProjectRulesService rules) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(new { ok = true, projectId, rules = rules.Load(projectId) });
});

app.MapPost("/api/admin/learning/project-rules/{projectId}/suggest", (
    string projectId,
    IUserContext user,
    ProjectRulesService rules,
    int? minFails) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = rules.SuggestFromFails(projectId, minFails ?? ProjectRulesService.DefaultMinFailsForSuggest);
        return Results.Ok(new { ok = true, projectId, rules = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/admin/learning/project-rules/{projectId}/approve", (
    string projectId,
    ApproveProjectRuleRequest body,
    IUserContext user,
    ProjectRulesService rules,
    ProposalChecklistService checklist) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        // Capture text before approve (suggestion removed from pending)
        var before = rules.Load(projectId);
        var sug = before.Pending.FirstOrDefault(p =>
            string.Equals(p.Id, body.SuggestionId, StringComparison.OrdinalIgnoreCase));
        var approvedText = !string.IsNullOrWhiteSpace(body.Text)
            ? body.Text!.Trim()
            : (sug?.Text ?? "").Trim();

        var doc = rules.Approve(projectId, body.SuggestionId, body.Text, user.UserId);

        // Keep admin checklist in sync (theme match) so Propose doesn't look "reset"
        ProposalChecklistDocument? checklistDoc = null;
        if (!string.IsNullOrWhiteSpace(approvedText))
        {
            try
            {
                checklistDoc = checklist.MarkAcceptedFromRuleTexts(
                    new[] { approvedText },
                    disposition: "accepted",
                    note: $"Approved project rule on {projectId}");
            }
            catch
            {
                /* non-fatal */
            }
        }

        return Results.Ok(new { ok = true, projectId, rules = doc, checklist = checklistDoc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/admin/learning/project-rules/{projectId}/reject", (
    string projectId,
    RejectProjectRuleRequest body,
    IUserContext user,
    ProjectRulesService rules) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var doc = rules.Reject(projectId, body.SuggestionId);
        return Results.Ok(new { ok = true, projectId, rules = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/admin/config", (IUserContext user, IRuntimeConfigStore config) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(config.Get());
});

app.MapPut("/api/admin/config", async (
    RuntimeConfigUpdateRequest body,
    IUserContext user,
    IRuntimeConfigStore config,
    IHubContext<JobHub> hub,
    CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var updated = await config.UpdateAsync(body, user.UserId, ct);
        _ = hub.Clients.Group(JobHub.AdminOpsGroup)
            .SendAsync(JobHubEvents.AdminState, new { configChanged = true, config = updated }, ct);
        return Results.Ok(updated);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/admin/chat-cache/clear", (IUserContext user, IServiceProvider sp) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    // Not registered under FilmStudio:UseFakes (fakes never hit the network, so there's nothing
    // to cache) — report that plainly instead of a DI resolution error.
    var cache = sp.GetService<CachingChatClient>();
    if (cache is null)
        return Results.Ok(new { ok = true, filesRemoved = 0, note = "chat cache not active (fakes mode)" });
    var removed = cache.ClearCache();
    return Results.Ok(new { ok = true, filesRemoved = removed });
});

app.MapPost("/api/admin/jobs/{jobId}/cancel", async (string jobId, IUserContext user, FilmJobService jobService) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    await jobService.CancelAsync(jobId);
    return Results.Ok(new { ok = true, jobId, job = jobService.GetJob(jobId) });
});

app.MapPost("/api/admin/locks/release", (AdminReleaseLockRequest body, IUserContext user, ILockService locks) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(body.Resource))
        return Results.BadRequest(new { ok = false, error = "resource required" });
    var ok = locks.Release(body.Resource.Trim(), user.UserId, force: body.Force || true);
    return Results.Ok(new { ok, resource = body.Resource, locks = locks.ListActive() });
});

app.MapPost("/api/jobs/{jobId}/cancel", async (string jobId, FilmJobService jobService, IUserContext user) =>
{
    var job = jobService.GetJob(jobId);
    if (job is null)
        return Results.NotFound(new { ok = false, error = "job not found" });
    if (!user.IsAdmin &&
        !string.Equals(job.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { ok = false, error = "not your job" },
            statusCode: StatusCodes.Status403Forbidden);
    await jobService.CancelAsync(jobId);
    return Results.Ok(new { ok = true, job = jobService.GetJob(jobId) });
});

app.MapGet("/health", (ProjectStore store, IOptions<FilmStudioOptions> opts, IUserContext user) =>
    Results.Ok(new
    {
        ok = true,
        service = "FilmStudio.Api",
        workspace = store.WorkspaceRoot,
        activeProject = store.ActiveProjectId,
        useFakes = opts.Value.UseFakes || useFakes,
        enableReadCaches = store.ReadCachesEnabled,
        capacity = opts.Value.Capacity,
        // Specifically XAI, not "is any video provider configured" — video is now
        // multi-provider (Grok/Gemini) via MultiProviderVideoClient, so IVideoClient.IsConfigured
        // would be true whenever *either* is set. Match xaiKeyPresent's own env-var check.
        xaiConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY")) || useFakes,
        xaiKeyPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY")),
        userId = user.UserId,
        isAdmin = user.IsAdmin,
    }));

app.MapGet("/api/capacity", (FilmJobService jobService, IOptions<FilmStudioOptions> opts) =>
{
    var cap = opts.Value.Capacity ?? new CapacityOptions();
    // Use O(1) counters — do not scan job list on this hot browse path
    var runningCount = jobService.RunningCount;
    return Results.Ok(new
    {
        ok = true,
        capacity = cap,
        running = runningCount > 0,
        runningCount,
        useFakes = opts.Value.UseFakes || useFakes,
    });
});

app.MapGet("/api/projects", async (ProjectStore store, CancellationToken ct) =>
{
    var list = await store.ListProjectsAsync(ct);
    var activeId = store.ActiveProjectId;
    if (string.IsNullOrWhiteSpace(activeId) && list.Count > 0)
        activeId = list[0].Id;
    var active = list.FirstOrDefault(p =>
        string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase));
    return Results.Ok(new { ok = true, active, projects = list });
});

app.MapPost("/api/projects/{id}/activate", async (string id, ProjectStore store, CancellationToken ct) =>
{
    try
    {
        var p = await store.ActivateAsync(id, ct);
        return Results.Ok(new { ok = true, active = p });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Create a new project folder under projects/ and make it active.</summary>
app.MapPost("/api/projects", async (CreateProjectRequest? body, ProjectStore store, CancellationToken ct) =>
{
    try
    {
        var name = body?.Name ?? body?.Id ?? body?.Title ?? "";
        var title = body?.Title;
        var p = await store.CreateProjectAsync(name, title, ct);
        var list = await store.ListProjectsAsync(ct);
        return Results.Ok(new
        {
            ok = true,
            active = p,
            projects = list,
            message = $"Created project “{p.Label ?? p.Title ?? p.Id}”",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Delete a project folder under projects/.</summary>
app.MapDelete("/api/projects/{id}", async (string id, ProjectStore store, CancellationToken ct) =>
{
    try
    {
        await store.DeleteProjectAsync(id, ct);
        var list = await store.ListProjectsAsync(ct);
        var activeId = store.ActiveProjectId;
        var active = list.FirstOrDefault(p =>
            string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase));
        return Results.Ok(new
        {
            ok = true,
            deleted = id,
            active,
            projects = list,
            message = $"Deleted project “{id}”",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// Phase F: multi-job list only — bare GET is 400 (no single-job shim)
app.MapGet("/api/jobs", (FilmJobService jobService, IUserContext user, string? mine, string? projectId, string? userId) =>
{
    var wantMine = string.Equals(mine, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mine, "true", StringComparison.OrdinalIgnoreCase);
    if (!wantMine && string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new
        {
            ok = false,
            error = "Specify mine=1, projectId, or userId. Single-job GET /api/jobs was removed (Phase F).",
            examples = new[]
            {
                "/api/jobs?mine=1",
                "/api/jobs?projectId=Buster",
                "/api/jobs/{jobId}",
            },
        });
    }

    var filterUser = wantMine ? user.UserId : userId;
    var list = jobService.ListJobs(filterUser, projectId, take: 50);
    return Results.Ok(new
    {
        ok = true,
        running = list.Any(j =>
            string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase)),
        jobs = list,
        count = list.Count,
        userId = user.UserId,
    });
});

app.MapGet("/api/jobs/{jobId}", (string jobId, FilmJobService jobService) =>
{
    var job = jobService.GetJob(jobId);
    if (job is null)
        return Results.NotFound(new { ok = false, error = "job not found" });
    return Results.Ok(new { ok = true, job });
});

static IResult JobStartError(Exception ex, FilmJobService jobService) => ex switch
{
    LockConflictException lx => Results.Conflict(new
    {
        ok = false,
        error = lx.Message,
        code = "lock_conflict",
        resource = lx.Resource,
        ownerUserId = lx.OwnerUserId,
        expiresAt = lx.ExpiresAt,
        job = jobService.GetSnapshot(),
    }),
    CapacityRejectedException cx => Results.Conflict(new
    {
        ok = false,
        error = cx.Message,
        code = "capacity",
        job = jobService.GetSnapshot(),
    }),
    _ => Results.Conflict(new { ok = false, error = ex.Message, job = jobService.GetSnapshot() }),
};

app.MapPost("/api/jobs/gen-scene", async (StartSceneGenRequest body, FilmJobService jobService) =>
{
    try
    {
        if (body.Scene <= 0)
            return Results.BadRequest(new { ok = false, error = "scene required" });
        var job = await jobService.StartSceneGenAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == "queued"
                ? $"Queued scene {body.Scene} (waiting for lock/worker)"
                : $"Started scene {body.Scene}",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

app.MapPost("/api/jobs/gen-batch", async (StartBatchGenRequest body, FilmJobService jobService) =>
{
    try
    {
        var hasClips = body.Clips is { Count: > 0 };
        if ((body.Scenes is null || body.Scenes.Count == 0) && !hasClips)
            return Results.BadRequest(new { ok = false, error = "scenes or clips required" });
        var job = await jobService.StartBatchGenAsync(body);
        var count = hasClips ? body.Clips!.Count : body.Scenes?.Count ?? 0;
        var unit = hasClips ? "clip" : "scene";
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == "queued"
                ? $"Queued batch for {count} {unit}(s)"
                : $"Started batch for {count} {unit}(s)",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>
/// Cancel active jobs. Non-admin: caller's jobs only.
/// Admin: same unless <c>?all=true</c> (cancel every user's jobs).
/// Prefer <c>POST /api/jobs/{jobId}/cancel</c> when a specific id is known.
/// </summary>
app.MapPost("/api/jobs/cancel", async (
    FilmJobService jobService,
    IUserContext user,
    bool? all) =>
{
    var cancelAllUsers = user.IsAdmin && all == true;
    if (all == true && !user.IsAdmin)
    {
        return Results.Json(
            new { ok = false, error = "admin role required to cancel all users' jobs" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var cancelled = await jobService.CancelAsync(
        jobId: null,
        userId: cancelAllUsers ? null : user.UserId,
        cancelAllUsers: cancelAllUsers);

    return Results.Ok(new
    {
        ok = true,
        cancelled,
        scope = cancelAllUsers ? "all" : "user",
        userId = cancelAllUsers ? null : user.UserId,
        job = jobService.GetSnapshot(),
    });
});

app.MapGet("/api/stage2-status", async (ProjectStore store, CancellationToken ct) =>
{
    var id = store.ActiveProjectId;
    if (string.IsNullOrEmpty(id))
        return Results.Ok(new { ok = true, stage2_ready = false });
    var bp = await store.FindBlueprintPathAsync(id, ct);
    var ready = bp is not null && File.Exists(bp);
    var scenes = 0;
    var clips = 0;
    if (ready)
    {
        try
        {
            using var doc = await store.LoadBlueprintAsync(id, ct);
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

// ---- Supported models (master catalog: model id → endpoint + required keys) ----
app.MapGet("/api/models", (string? capability) =>
{
    IReadOnlyList<SupportedModelDto> list;
    if (!string.IsNullOrWhiteSpace(capability) &&
        Enum.TryParse<ModelCapability>(capability, ignoreCase: true, out var cap))
    {
        list = SupportedModelCatalog.ForCapability(cap)
            .Select(SupportedModelCatalog.ToDto)
            .ToList();
    }
    else
    {
        list = SupportedModelCatalog.ToDtoList(enabledOnly: true);
    }

    return Results.Ok(new
    {
        ok = true,
        models = list,
        note =
            "User picks model ids only. Provider, API base, endpoint, and required env keys come from this catalog. " +
            "Request new models via GitHub feature request, then add them here when wired.",
    });
});

// ---- Configuration (pipeline_config.json) ----
app.MapGet("/api/projects/{id}/config", async (string id, ProjectStore store, CancellationToken ct) =>
{
    try
    {
        var cfg = await store.GetConfigAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, config = cfg });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPut("/api/projects/{id}/config", async (string id, HttpRequest req, ProjectStore store, CancellationToken ct) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var saved = await store.SaveConfigAsync(id, doc.RootElement, ct);
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
        // ListCharacters still has seed/json paths for Pass 3.5; keeps working via sync wrappers
        var chars = store.ListCharacters(id);
        var plates = store.GetCharacterPlatesState(id);
        var seedLimits = store.GetImageSeedLimits(id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            characters = chars,
            characterPlates = plates,
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
        var job = await jobService.StartCharacterVariantsAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued portrait generation for {body.CharKey}",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>Save voice_label / voice_profile into cast_seeds (+ blueprint) character seeds.</summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/voice",
    (string id, string charKey, UpdateCharacterVoiceRequest? body, ProjectStore store) =>
{
    try
    {
        body ??= new UpdateCharacterVoiceRequest();
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        store.UpdateCharacterSeedText(
            id,
            charKey,
            voiceProfile: body.VoiceProfile,
            voiceLabel: body.VoiceLabel);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            message = "Voice seed updated",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Film-pipeline voice sample job: short Grok video (VOICE LOCK + dialogue) → MP3 only.
/// Use Force=true after editing the profile to regenerate.
/// </summary>
app.MapPost("/api/jobs/voice-preview", async (StartVoicePreviewRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || string.IsNullOrWhiteSpace(body.CharKey))
            return Results.BadRequest(new { ok = false, error = "projectId and charKey required" });
        var job = await jobService.StartVoicePreviewAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.Force
                ? $"Queued voice regenerate for {body.CharKey}"
                : $"Queued voice sample for {body.CharKey}",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>Cache status for film voice sample (matches current profile text?).</summary>
app.MapGet("/api/projects/{id}/characters/{charKey}/voice/audio/status", (
    string id,
    string charKey,
    string? voiceProfile,
    string? voiceLabel,
    string? sampleText,
    VoicePreviewService voices) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        var info = voices.GetCacheInfo(id, charKey, voiceProfile, voiceLabel, sampleText, displayName: null);
        return Results.Ok(new VoicePreviewStatusDto
        {
            Ok = true,
            Exists = info.Exists,
            Matches = info.Matches,
            Fingerprint = info.Fingerprint,
            GeneratedAt = info.GeneratedAt,
            AudioUrl = info.Exists
                ? $"/api/projects/{Uri.EscapeDataString(id)}/characters/{Uri.EscapeDataString(charKey)}/voice/audio"
                : null,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Serve cached film voice sample MP3 (audio only — no video).</summary>
app.MapGet("/api/projects/{id}/characters/{charKey}/voice/audio", (
    string id,
    string charKey,
    VoicePreviewService voices) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        var path = voices.GetMp3Path(id, charKey);
        if (!File.Exists(path) || new FileInfo(path).Length < 64)
            return Results.NotFound(new { ok = false, error = "No voice sample yet — generate one first." });
        return Results.File(path, "audio/mpeg", fileDownloadName: $"{charKey}_voice.mp3", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Save description / visual_lock for portrait continuity (cast_seeds + blueprint).
/// By default runs AI prompt scrub (literal filmable + base look, not later-story wardrobe).
/// </summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/look", async (
    string id,
    string charKey,
    UpdateCharacterLookRequest? body,
    ProjectStore store,
    CastVisualLiteralizeService literalize,
    CancellationToken ct) =>
{
    try
    {
        body ??= new UpdateCharacterLookRequest();
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });

        var desc = body.Description;
        var vis = body.VisualLock;
        var scrubbed = false;

        // Skip AI scrub when posted text matches what is already stored
        string? storedDesc = null;
        string? storedVis = null;
        var existing = store.GetCharacterSeed(id, charKey);
        if (existing is not null)
        {
            if (existing.Value.TryGetProperty("description", out var d0))
                storedDesc = d0.GetString();
            if (existing.Value.TryGetProperty("visual_lock", out var v0))
                storedVis = v0.GetString();
        }

        var lookUnchanged =
            string.Equals(desc ?? "", storedDesc ?? "", StringComparison.Ordinal) &&
            string.Equals(vis ?? "", storedVis ?? "", StringComparison.Ordinal);

        if (lookUnchanged)
        {
            return Results.Ok(new
            {
                ok = true,
                projectId = id,
                charKey,
                scrubbedWithAi = false,
                description = storedDesc ?? desc,
                visualLock = storedVis ?? vis,
                message = "Look unchanged",
            });
        }

        if (body.ScrubWithAi && (desc is not null || vis is not null))
        {
            var (d2, v2, usedAi) = await literalize.ScrubLookFieldsAsync(
                charKey,
                description: desc ?? "",
                visualLock: vis ?? "",
                model: string.IsNullOrWhiteSpace(body.Model) ? "grok-4.5" : body.Model,
                ct: ct).ConfigureAwait(false);
            if (usedAi)
            {
                if (desc is not null) desc = d2;
                if (vis is not null) vis = v2;
                scrubbed = true;
            }
        }

        store.UpdateCharacterSeedText(
            id,
            charKey,
            description: desc,
            visualLock: vis);

        // Return cleaned text so the UI can refresh editors without a second guess
        var seed = store.GetCharacterSeed(id, charKey);
        string? savedDesc = null;
        string? savedVis = null;
        if (seed is not null)
        {
            if (seed.Value.TryGetProperty("description", out var dEl))
                savedDesc = dEl.GetString();
            if (seed.Value.TryGetProperty("visual_lock", out var vEl))
                savedVis = vEl.GetString();
        }

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            scrubbedWithAi = scrubbed,
            description = savedDesc ?? desc,
            visualLock = savedVis ?? vis,
            message = scrubbed
                ? "Look saved (AI scrubbed: base + literal)"
                : "Look (description / visual lock) updated",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// AI: Fountain (+ book) → source/cast_seeds.json.
/// Closed cast for Characters UI — not dialogue-cue parse only.
/// </summary>
app.MapPost("/api/projects/{id}/characters/extract-cast", async (
    string id,
    ExtractCastRequest? body,
    CastFromScreenplayService castService,
    CancellationToken ct) =>
{
    try
    {
        body ??= new ExtractCastRequest();
        var result = await castService.ExtractAsync(
            id,
            model: string.IsNullOrWhiteSpace(body.Model) ? "grok-4.5" : body.Model!,
            force: body.Force,
            ct: ct);
        return result.Ok
            ? Results.Ok(new
            {
                ok = true,
                projectId = id,
                path = result.OutPath,
                characterCount = result.CharacterCount,
                characters = result.CharacterKeys,
                movieTitle = result.MovieTitle,
                message =
                    $"Cast ready · {result.CharacterCount} character(s) — looks filled from screenplay" +
                    " + book when available; review then lock portraits",
            })
            : Results.BadRequest(new
            {
                ok = false,
                projectId = id,
                error = result.Error,
                rawPath = result.RawPath,
            });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Heuristic attach (no Grok). Prefer POST /api/jobs/sort-character-plates for vision sort.
/// </summary>
app.MapPost("/api/projects/{id}/characters/attach-book-plates", async (
    string id,
    AttachCharacterPlatesRequest? body,
    CharacterBookPlateService plates,
    CancellationToken ct) =>
{
    try
    {
        body ??= new AttachCharacterPlatesRequest();
        var result = await plates.AttachAsync(
            id,
            force: body.Force,
            copyIntoAssets: body.CopyIntoAssets,
            onlyCharKey: body.CharKey,
            useGrok: false,
            ct: ct);
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
        var job = await jobService.StartSortCharacterPlatesAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.UseGrok
                ? "Queued Grok vision character plate sort"
                : "Queued heuristic character plate sort",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
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

/// <summary>
/// Upload an operator-provided image and lock it as the character reference (preferred look).
/// Multipart form field name: <c>file</c> (png/jpg/webp/gif).
/// </summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/upload-ref", async (
    string id,
    string charKey,
    HttpRequest req,
    CharacterDesignService characters,
    CancellationToken ct) =>
{
    try
    {
        if (!req.HasFormContentType)
            return Results.BadRequest(new { ok = false, error = "multipart form required (field: file)" });

        var form = await req.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No image file in form (field name: file)" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp"))
            return Results.BadRequest(new { ok = false, error = "Use a PNG, JPG, WEBP, or GIF image." });

        if (file.Length > 25 * 1024 * 1024)
            return Results.BadRequest(new { ok = false, error = "Image too large (max 25 MB)." });

        await using var stream = file.OpenReadStream();
        var path = await characters.LockFromUploadAsync(id, charKey, stream, file.FileName, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            path = Path.GetFileName(path),
            message = "Locked preferred look from your upload",
        });
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

/// <summary>
/// Delete a character picture: preferred/lock, variant, or book plate.
/// Body: { "kind": "preferred"|"variant"|"bookref", "index": 0 }
/// </summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/delete-image",
    (string id, string charKey, DeleteCharacterImageRequest? body, CharacterDesignService characters) =>
{
    try
    {
        body ??= new DeleteCharacterImageRequest();
        if (string.IsNullOrWhiteSpace(body.Kind))
            return Results.BadRequest(new { ok = false, error = "kind required" });
        characters.DeleteImage(id, charKey, body.Kind, body.Index);
        return Results.Ok(new { ok = true, projectId = id, charKey, kind = body.Kind, index = body.Index });
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
        var job = await jobService.StartBookPrepareAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued book prepare (C# PDF extract / vision OCR)",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>Prepare (optional) + book→Fountain draft as one background job.</summary>
app.MapPost("/api/jobs/book-import", async (StartBookImportRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        var job = await jobService.StartBookImportAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.SkipPrepare
                ? "Queued screenplay draft from book"
                : "Queued book import (prepare + screenplay)",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
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

/// <summary>
/// Import a Fountain file as the editable screenplay draft (does not approve / Stage 1 yet).
/// User reviews on Screenplay, then sign-off materialises Stage 1.
/// </summary>
app.MapPost("/api/projects/{id}/adaptation/import-fountain", async (string id, HttpRequest req, ProjectStore store) =>
{
    try
    {
        string text;
        string? fileName = null;
        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { ok = false, error = "file required" });
            fileName = file.FileName;
            using var reader = new StreamReader(file.OpenReadStream());
            text = await reader.ReadToEndAsync();
        }
        else
        {
            using var reader = new StreamReader(req.Body);
            text = await reader.ReadToEndAsync();
            fileName = ScreenplayService.CanonicalFileName;
        }

        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(new { ok = false, error = "empty fountain text" });

        var result = ScreenplayService.ImportAsDraft(store, id, text, fileName);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        var status = store.GetAdaptationStatus(id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            title = result.Status.Title,
            sceneHeadingCount = result.Status.SceneHeadingCount,
            draftBytes = result.Status.DraftBytes,
            dirty = result.Status.Dirty,
            signed = result.Status.Signed,
            message = result.Message ?? "Screenplay draft ready — review and approve",
            adaptation = status,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Get the editable Fountain draft + status.</summary>
app.MapGet("/api/projects/{id}/screenplay", (string id, ProjectStore store) =>
{
    try
    {
        var doc = ScreenplayService.Get(store, id);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            text = doc.Text,
            screenplay = doc.Status,
            adaptation = store.GetAdaptationStatus(id),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Save Fountain draft (no Stage 1 write).</summary>
app.MapPut("/api/projects/{id}/screenplay", async (string id, HttpRequest req, ProjectStore store) =>
{
    try
    {
        string text;
        if (req.HasFormContentType)
        {
            var form = await req.ReadFormAsync();
            text = form["text"].ToString() ?? form["content"].ToString() ?? "";
            if (string.IsNullOrEmpty(text) && form.Files.Count > 0)
            {
                using var reader = new StreamReader(form.Files[0].OpenReadStream());
                text = await reader.ReadToEndAsync();
            }
        }
        else
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            // Accept raw text or JSON { "text": "..." }
            text = body;
            if (body.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("text", out var t))
                        text = t.GetString() ?? "";
                    else if (doc.RootElement.TryGetProperty("content", out var c))
                        text = c.GetString() ?? "";
                }
                catch { /* treat as raw */ }
            }
        }

        var result = ScreenplayService.SaveDraft(store, id, text);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            message = result.Message,
            screenplay = result.Status,
            adaptation = store.GetAdaptationStatus(id),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Approve the Fountain draft: materialise Stage 1 (scenes.json).
/// Optional body text saves first. Marks shot plan stale when hash changes.
/// </summary>
app.MapPost("/api/projects/{id}/screenplay/sign-off", async (
    string id,
    HttpRequest req,
    ProjectStore store,
    CastFromScreenplayService castService,
    FilmStudio.Engine.Abstractions.IChatClient chat,
    CancellationToken ct) =>
{
    try
    {
        string? text = null;
        if (req.ContentLength is > 0 || req.ContentType is not null)
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                if (body.TrimStart().StartsWith('{'))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("text", out var t))
                            text = t.GetString();
                    }
                    catch { text = body; }
                }
                else
                {
                    text = body;
                }
            }
        }

        var result = ScreenplayService.SignOff(store, id, text);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        // AI cast sidecar after approve (closed cast for Characters / plates)
        object? cast = null;
        if (chat.IsConfigured)
        {
            try
            {
                var castResult = await castService.ExtractAsync(id, force: true, ct: ct);
                cast = new
                {
                    ok = castResult.Ok,
                    characterCount = castResult.CharacterCount,
                    characters = castResult.CharacterKeys,
                    error = castResult.Error,
                    path = castResult.OutPath,
                };
            }
            catch (Exception ex)
            {
                cast = new { ok = false, error = ex.Message };
            }
        }

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            title = result.Title,
            sceneCount = result.SceneCount,
            characterCount = result.CharacterCount,
            locationCount = result.LocationCount,
            hashChanged = result.HashChanged,
            message = result.Message,
            screenplay = result.Status,
            adaptation = store.GetAdaptationStatus(id),
            cast,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Create an editable Fountain draft from prepared book text (structured + page tags).</summary>
app.MapPost("/api/projects/{id}/screenplay/from-book", async (
    string id,
    ProjectStore store,
    FilmStudio.Engine.Abstractions.IChatClient chat,
    CancellationToken ct) =>
{
    try
    {
        var result = await ScreenplayService.CreateDraftFromBookAsync(store, id, chat, ct: ct);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            message = result.Message,
            screenplay = result.Status,
            adaptation = store.GetAdaptationStatus(id),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Book excerpt for a screenplay scene (click scene in editor).
/// Query: sceneIndex (1-based), line (optional), heading (optional).
/// Body optional: { "body": "scene action text for fuzzy match" }.
/// </summary>
app.MapMethods("/api/projects/{id}/screenplay/book-context", new[] { "GET", "POST" },
    async (string id, HttpRequest req, ProjectStore store) =>
{
    try
    {
        var q = req.Query;
        _ = int.TryParse(q["sceneIndex"], out var sceneIndex);
        if (sceneIndex < 1) sceneIndex = 1;
        _ = int.TryParse(q["line"], out var line);
        var heading = q["heading"].ToString();
        string? body = null;
        string? fountainText = null;

        if (HttpMethods.IsPost(req.Method) && req.ContentLength is > 0)
        {
            using var reader = new StreamReader(req.Body);
            var raw = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("body", out var b))
                        body = b.GetString();
                    if (doc.RootElement.TryGetProperty("text", out var tx))
                        fountainText = tx.GetString();
                    if (doc.RootElement.TryGetProperty("heading", out var h) && string.IsNullOrEmpty(heading))
                        heading = h.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("sceneIndex", out var si) && si.TryGetInt32(out var sii) && sii > 0)
                        sceneIndex = sii;
                    if (doc.RootElement.TryGetProperty("line", out var ln) && ln.TryGetInt32(out var lni))
                        line = lni;
                }
                catch { /* ignore */ }
            }
        }

        // Prefer live editor text for extract; fall back to saved draft
        if (string.IsNullOrWhiteSpace(fountainText))
            fountainText = ScreenplayService.Get(store, id).Text;

        if (string.IsNullOrWhiteSpace(body) && line > 0 && !string.IsNullOrEmpty(fountainText))
        {
            body = BookContextService.ExtractSceneBody(fountainText, line);
            if (string.IsNullOrWhiteSpace(heading))
            {
                var lines = fountainText.Replace("\r\n", "\n").Split('\n');
                if (line - 1 >= 0 && line - 1 < lines.Length)
                    heading = lines[line - 1].Trim().TrimStart('.');
            }
        }

        var ctx = BookContextService.GetContext(store, id, sceneIndex, heading, body);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            hasBook = ctx.HasBook,
            pageNumber = ctx.PageNumber,
            sceneIndex = ctx.SceneIndex,
            heading = ctx.Heading,
            excerpt = ctx.Excerpt,
            matchReason = ctx.MatchReason,
            totalPages = ctx.TotalPages,
            message = ctx.HasBook
                ? (ctx.PageNumber is int p
                    ? $"Book · page {p}"
                    : "Book")
                : "No prepared book text for this project",
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
        var job = await jobService.StartStage1Async(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued Stage 1 (C# Grok chat)",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

app.MapPost("/api/jobs/stage2", async (StartStage2Request body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        var job = await jobService.StartStage2Async(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued Stage 2 (C# planner)",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

app.MapPost("/api/jobs/remux", async (StartRemuxRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        var job = await jobService.StartRemuxAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued remux / WIP",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

// ---- Review / edit log ----
app.MapGet("/api/projects/{id}/edit-log", async (string id, EditLogService logs, CancellationToken ct) =>
{
    try
    {
        var doc = await logs.LoadAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, editLog = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/clips/review", async (
    string id, ClipReviewRequest body, EditLogService logs, CancellationToken ct) =>
{
    try
    {
        body.ProjectId = id;
        await logs.SetClipReviewAsync(id, body.Scene, body.Clip, body.Status, body.Note, ct);
        return Results.Ok(new { ok = true, projectId = id, scene = body.Scene, clip = body.Clip, status = body.Status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/approve", async (
    string id, int scene, SceneApproveRequest? body, EditLogService logs, FilmJobService jobs, CancellationToken ct) =>
{
    try
    {
        body ??= new SceneApproveRequest();
        await logs.MarkSceneApprovedAsync(id, scene, body.Note ?? "", ct);
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

app.MapGet("/api/projects/{id}/clip-reviews", async (string id, EditLogService logs, CancellationToken ct) =>
{
    try
    {
        var map = await logs.GetClipReviewMapAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, reviews = map });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Queue AI auto-review for one clip (prev tail + current → draft suggestions).</summary>
app.MapPost("/api/jobs/clip-auto-review", async (StartClipAutoReviewRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || body.Scene <= 0 || body.Clip <= 0)
            return Results.BadRequest(new { ok = false, error = "projectId, scene, clip required" });
        var job = await jobService.StartClipAutoReviewAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued AI review S{body.Scene:D2}C{body.Clip:D2}",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>Batch AI auto-review for on-disk clips (onlyMissing default true). Rebuilds assets/review/index.json.</summary>
app.MapPost("/api/jobs/clip-auto-review-batch", async (StartClipAutoReviewBatchRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        var job = await jobService.StartClipAutoReviewBatchAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = body.Scene is int sn && sn > 0
                ? $"Queued batch AI review S{sn:D2}"
                : "Queued batch AI review (all scenes)",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>Load or rebuild assets/review/index.json (one row per on-disk clip).</summary>
app.MapGet("/api/projects/{id}/review/index", (
    string id, bool? rebuild, ReviewIndexService reviewIndex) =>
{
    try
    {
        var doc = rebuild == true
            ? reviewIndex.Rebuild(id)
            : reviewIndex.Load(id) ?? reviewIndex.Rebuild(id);
        return Results.Ok(new { ok = true, index = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Rebuild project-local ARTIFACTS.md + artifact_index.json (+ telemetry cost/models snapshots).
/// Use before manual whole-project review (Claude on the project folder). Zip export deferred.
/// </summary>
app.MapPost("/api/projects/{id}/artifacts/index", async (
    string id, ProjectArtifactIndexService artifacts, CancellationToken ct) =>
{
    try
    {
        var doc = await artifacts.RebuildAsync(id, ct);
        return Results.Ok(new
        {
            ok = true,
            readyForManualFinalReview = doc.ReadyForManualFinalReview,
            missingRequired = doc.MissingRequired,
            index = doc,
            paths = new
            {
                artifactsMd = "ARTIFACTS.md",
                artifactIndexJson = "artifact_index.json",
                telemetry = "telemetry/",
            },
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/artifacts/index", async (
    string id, ProjectArtifactIndexService artifacts, bool? rebuild, CancellationToken ct) =>
{
    try
    {
        if (rebuild == true)
        {
            var doc = await artifacts.RebuildAsync(id, ct);
            return Results.Ok(new { ok = true, index = doc });
        }

        var path = artifacts.IndexJsonPath(id);
        if (!File.Exists(path))
        {
            var doc = await artifacts.RebuildAsync(id, ct);
            return Results.Ok(new { ok = true, index = doc, rebuilt = true });
        }

        var json = await File.ReadAllTextAsync(path, ct);
        var existing = System.Text.Json.JsonSerializer.Deserialize<ArtifactIndexDocument>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Results.Ok(new { ok = true, index = existing });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Load latest auto-review draft for a clip (if any).</summary>
app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/auto-review", (
    string id, int scene, int clip, ClipAutoReviewService reviews) =>
{
    try
    {
        var draft = reviews.LoadDraft(id, scene, clip);
        if (draft is null)
            return Results.NotFound(new { ok = false, error = "No auto-review draft yet." });
        return Results.Ok(new { ok = true, draft });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Write accepted suggestion fields (cast / clip prompt). Does not regen — client starts gen after.</summary>
app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/auto-review/apply", (
    string id, int scene, int clip, ApplyClipAutoReviewRequest? body, ClipAutoReviewService reviews) =>
{
    try
    {
        body ??= new ApplyClipAutoReviewRequest();
        body.ProjectId = id;
        body.Scene = scene;
        body.Clip = clip;
        reviews.ApplySuggestions(id, scene, clip, body.Items);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            scene,
            clip,
            message = $"Applied {body.Items.Count} suggestion(s)",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// ---- Cost (ledger + estimates) ----
app.MapGet("/api/projects/{id}/cost", async (
    string id,
    ProjectStore store,
    CostReportService costs,
    string? draftResolution,
    string? heroResolution,
    double? assumeAvgRetries,
    CancellationToken ct) =>
{
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        var report = await costs.GetReportAsync(id, draftResolution, heroResolution, assumeAvgRetries, ct: ct);
        return Results.Ok(new { ok = true, projectId = id, cost = report });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/cost/backfill", async (
    string id, ProjectStore store, CostReportService costs, CancellationToken ct) =>
{
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        var result = await costs.BackfillFromDiskAsync(id, onlyMissing: true, ct);
        return Results.Ok(new { ok = true, projectId = id, backfill = result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// ---- Scenes & Clips ----
// light=1 skips ffprobe duration probes (required for LoadSim / high concurrency)
// Async I/O on the browse path so Kestrel threads are not blocked on disk (Pass 1).
app.MapGet("/api/projects/{id}/scenes", async (
    string id,
    ProjectStore store,
    ILockService locks,
    IUserContext user,
    string? light,
    CancellationToken ct) =>
{
    try
    {
        var probe = !string.Equals(light, "1", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(light, "true", StringComparison.OrdinalIgnoreCase);
        var scenes = (await store.ListScenesAsync(id, probeDurations: probe, ct)).ToList();
        var active = locks.ListActive();
        foreach (var s in scenes)
        {
            var key = LockKeys.Scene(id, s.SceneNumber);
            var held = active.FirstOrDefault(l =>
                string.Equals(l.Resource, key, StringComparison.OrdinalIgnoreCase));
            if (held is null) continue;
            s.LockOwnerUserId = held.UserId;
            s.LockReason = held.Reason;
            s.LockedByOther = !string.Equals(held.UserId, user.UserId, StringComparison.OrdinalIgnoreCase);
        }
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            sceneCount = scenes.Count,
            clipCount = scenes.Sum(s => s.ClipCount),
            clipsOnDisk = scenes.Sum(s => s.ClipsOnDisk),
            callerUserId = user.UserId,
            light = !probe,
            scenes,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}", async (
    string id,
    int sceneNumber,
    ProjectStore store,
    string? light,
    CancellationToken ct) =>
{
    try
    {
        var probe = !string.Equals(light, "1", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(light, "true", StringComparison.OrdinalIgnoreCase);
        var detail = await store.GetSceneDetailAsync(id, sceneNumber, probeDurations: probe, ct);
        if (detail is null)
            return Results.NotFound(new { ok = false, error = $"Scene {sceneNumber} not found" });
        return Results.Ok(new { ok = true, projectId = id, scene = detail, light = !probe });
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

// Expose entry assembly for WebApplicationFactory integration tests.
public partial class Program { }
