using System.Diagnostics;
using System.Text.Json;
using PageToMovie.Api;
using PageToMovie.Api.Auth;
using PageToMovie.Api.Hubs;
using PageToMovie.Api.Services;
using PageToMovie.Web.Components;
using PageToMovie.Web.Services;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.ModelBacked;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PageToMovie.Api.Collaboration;
using PageToMovie.Engine.Collaboration;

using PageToMovie.Core.Localization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAppLocalization();
builder.Services.AddSingleton<PageToMovie.Engine.Collaboration.IProjectInviteMailer, PageToMovie.Engine.LoggingEmailSender>();
builder.Services.AddSingleton<ProjectAclService>(sp =>
{
    var env = sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();
    var root = Path.Combine(env.ContentRootPath, "projects");
    var email = sp.GetService<PageToMovie.Engine.Collaboration.IProjectInviteMailer>();
    return new ProjectAclService(root, null, email);
});

var processStartedUtc = DateTimeOffset.UtcNow;

var OAuthCodeParamRegex = new System.Text.RegularExpressions.Regex(@"code=([^&]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
var OAuthStateParamRegex = new System.Text.RegularExpressions.Regex(@"state=([^&]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
var OAuthErrorParamRegex = new System.Text.RegularExpressions.Regex(@"error=([^&]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

var listenPorts = new HashSet<string> { "5088", "8080", "80" };
// Testability/deploy override: replace the default bind ports entirely (comma-separated). Lets a
// second local instance (e.g. UI tests with capabilities forced off) bind a distinct port without
// colliding on 5088. Unset in normal runs → the defaults above apply.
var bindPortsOverride = Environment.GetEnvironmentVariable("PAGETOMOVIE_BIND_PORTS");
if (!string.IsNullOrWhiteSpace(bindPortsOverride))
    listenPorts = new HashSet<string>(
        bindPortsOverride.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
var railwayEnvPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(railwayEnvPort))
{
    listenPorts.Add(railwayEnvPort.Trim());
}
var bindUrls = string.Join(";", listenPorts.Select(p => $"http://0.0.0.0:{p}"));
builder.WebHost.UseUrls(bindUrls);

builder.Services.Configure<PageToMovieOptions>(
    builder.Configuration.GetSection(PageToMovieOptions.SectionName));

// Optional ThreadPool pre-warm (PageToMovie:ThreadPool:MinWorkerThreads) for 100-VU ramps.
// 0 / unset = CLR defaults. Apply before host starts accepting requests.
{
    var tp = builder.Configuration.GetSection(PageToMovieOptions.SectionName)
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

// Default workspace = repo root (two levels up from host/PageToMovie.Api)
var repoGuess = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
builder.Services.PostConfigure<PageToMovieOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.WorkspaceRoot) || !Directory.Exists(o.WorkspaceRoot))
        o.WorkspaceRoot = repoGuess;

    o.Auth ??= new AuthOptions();
    var envKey = Environment.GetEnvironmentVariable("PageToMovie_JWT_KEY")
                 ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_JWT_KEY")
                 ?? Environment.GetEnvironmentVariable("PageToMovie__Auth__JwtSigningKey")
                 ?? Environment.GetEnvironmentVariable("FILMSTUDIO_JWT_KEY");

    var effective = !string.IsNullOrWhiteSpace(envKey) ? envKey.Trim() : o.Auth.JwtSigningKey;
    if (!builder.Environment.IsDevelopment() && AuthOptions.IsInsecureDefaultJwtSigningKey(effective))
    {
        var secureKey = System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*", 64);
        o.Auth.JwtSigningKey = secureKey;
    }
});

builder.Services.AddSingleton<MediaDurationProbe>();
builder.Services.AddSingleton<SceneListCache>();
builder.Services.AddSingleton<ProjectReadCache>();
builder.Services.AddSingleton<ProjectStore>();
// CostLedgerService takes a plain projects-root string, so it needs a factory rather than a bare
// AddSingleton<T>() — and without ANY registration, Minimal API's parameter-source inference can't
// recognize it as a service; it falls back to inferring [FromBody], which .NET disallows on GET
// endpoints and throws at route-table build time, failing every request through the host (see
// /api/projects/{id}/costs/summary). Root matches ProjectStore's own convention (WorkspaceRoot/projects),
// not IHostEnvironment.ContentRootPath — those differ under PageToMovie__WorkspaceRoot / fakes tests.
builder.Services.AddSingleton(sp =>
    new CostLedgerService(Path.Combine(sp.GetRequiredService<ProjectStore>().WorkspaceRoot, "projects")));
// Same unregistered-string-ctor issue as CostLedgerService above (SceneVersionHistory.razor's
// /versions endpoints, used by the Scenes-page scene-history panel).
builder.Services.AddSingleton(sp =>
    new PageToMovie.Engine.Collaboration.SceneVersionStore(
        Path.Combine(sp.GetRequiredService<ProjectStore>().WorkspaceRoot, "projects")));

builder.Services.AddSingleton<IProjectAclService>(sp => sp.GetRequiredService<ProjectAclService>());
builder.Services.AddSingleton<IProjectLeaseService, ProjectLeaseService>();
builder.Services.AddSingleton<IProjectPresenceService, ProjectPresenceService>();
builder.Services.AddSingleton<IAutoProjectMerger, AutoProjectMerger>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<ILockService, InMemoryLockService>();
builder.Services.AddSingleton<IServerMetricsService, ServerMetricsService>();
builder.Services.AddSingleton<IRuntimeConfigStore, RuntimeConfigStore>();
builder.Services.AddSingleton<ApiWorkerPool>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddSingleton<CreditService>();
builder.Services.AddSingleton<ProjectArchiveService>();
builder.Services.AddSingleton<CostReportService>();
builder.Services.AddSingleton<CatalogUpdateProbeService>();
builder.Services.AddHttpClient("catalog-probe", c =>
{
    c.Timeout = TimeSpan.FromSeconds(45);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("PageToMovie-CatalogProbe/1.0");
});
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
builder.Services.AddSingleton<SceneMusicCompositionService>();
builder.Services.AddSingleton<DepthOfFieldClassifier>();
builder.Services.AddSingleton<ColorPaletteGradingClassifier>();
builder.Services.AddSingleton<Stage2PlannerService>();
builder.Services.AddSingleton<VoicePreviewService>();
builder.Services.AddHttpClient("elevenlabs", c =>
{
    c.BaseAddress = new Uri(SupportedModelCatalog.ElevenLabsApiBase.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromMinutes(3);
});
// Real IVoiceClient (ElevenLabs) is registered only in the !useFakes branch below, alongside the
// other provider clients — under PageToMovie:UseFakes it resolves to FakeVoiceClient (via
// AddPageToMovieFakes) so voice clone / dialogue TTS never reaches ElevenLabs even when a key is set.
builder.Services.AddSingleton<PageToMovie.Engine.VoiceApply.VoicePreviewStore>();
// Strategy order: Fal first (specific CanHandle), ElevenLabs last (default / mock fallback).
builder.Services.AddSingleton<IVoiceApplyStrategy, PageToMovie.Engine.VoiceApply.FalVoiceApplyStrategy>();
builder.Services.AddSingleton<IVoiceApplyStrategy, PageToMovie.Engine.VoiceApply.ElevenLabsVoiceApplyStrategy>();
builder.Services.AddSingleton<VoiceCloneApplyService>();
builder.Services.AddSingleton<ReviewEventStore>();
builder.Services.AddSingleton<ProjectRulesService>();
builder.Services.AddSingleton<LearningProposalService>();
builder.Services.AddSingleton<ProposalChecklistService>();
builder.Services.AddSingleton<EditLogService>();
builder.Services.AddSingleton<ProjectTelemetryService>();
builder.Services.AddSingleton<AiCallAnalyticsService>();
builder.Services.AddSingleton<ReviewIndexService>();
builder.Services.AddSingleton<ClipAutoReviewService>();
builder.Services.AddSingleton<ClipDialogueVerificationService>();
builder.Services.AddSingleton<ProjectArtifactIndexService>();
builder.Services.AddSingleton<MediaShareService>();
builder.Services.AddSingleton<DemoCatalogService>();
builder.Services.AddSingleton<DemoUpvoteService>();
builder.Services.AddHostedService<ServerMediaPruningService>();
builder.Services.AddSingleton<MediaRegistryService>();
builder.Services.AddSingleton<MediaSyncLocator>();
builder.Services.AddSingleton<MediaProxyTicketStore>();
builder.Services.AddSingleton<ClipSidecarService>();
builder.Services.AddSingleton<VoiceAlignmentStore>();
builder.Services.AddSingleton<MusicSidecarService>();
builder.Services.AddSingleton<ProjectMigrationService>();
builder.Services.AddSingleton<VolumeDiskTelemetryService>();
builder.Services.AddSingleton<ProjectArchiveService>();
builder.Services.AddSingleton<ServerLogExportService>();
builder.Services.AddSingleton<YouTubeAuthService>();
builder.Services.AddSingleton<DemoYouTubePublisherService>();
builder.Services.AddSingleton<YouTubeChannelGallerySync>();
builder.Services.AddSingleton<ProjectGitRepositoryService>();
builder.Services.AddSingleton<ProjectAutoGitService>();
builder.Services.AddSingleton<MovieAutoReviewService>();
builder.Services.AddSingleton<ProjectInviteService>();
builder.Services.AddSingleton<CreatorProfileService>();
builder.Services.AddSingleton<ProjectContributionService>();
var configuredWorkspaceRoot = builder.Configuration
    .GetSection(PageToMovieOptions.SectionName)
    .GetValue<string>(nameof(PageToMovieOptions.WorkspaceRoot));
var dataRoot = UserDatabaseService.ResolveDataDirectory(
    string.IsNullOrWhiteSpace(configuredWorkspaceRoot) ? repoGuess : configuredWorkspaceRoot);
var dpKeysDir = Path.Combine(dataRoot, "keys");
var legacyDpKeysDir = Path.Combine(Path.GetTempPath(), "ptm-dp-keys");
if (!Directory.Exists(dpKeysDir) && !string.Equals(dpKeysDir, legacyDpKeysDir, StringComparison.OrdinalIgnoreCase) &&
    Directory.Exists(legacyDpKeysDir))
{
    Directory.CreateDirectory(dpKeysDir);
    foreach (var keyFile in Directory.EnumerateFiles(legacyDpKeysDir, "*", SearchOption.TopDirectoryOnly))
        File.Copy(keyFile, Path.Combine(dpKeysDir, Path.GetFileName(keyFile)), overwrite: false);
}
Directory.CreateDirectory(dpKeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));
builder.Services.AddSingleton<UserDatabaseService>();
builder.Services.AddSingleton<BookTextRegistryService>();
builder.Services.AddSingleton<GenerationErrorLogger>();
builder.Services.AddHttpContextAccessor();

// Blazor Web UI — Interactive WebAssembly (client DI lives in PageToMovie.Web Program.cs)
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "PageToMovie.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddSingleton<IUserContext, HttpUserContext>();
builder.Services.AddSingleton<IUserApiKeyProvider, DbUserApiKeyProvider>();
static IHttpClientBuilder ConfigurePooledSocketsHandler(IHttpClientBuilder b) =>
    b.SetHandlerLifetime(TimeSpan.FromMinutes(15))
     .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
     {
         PooledConnectionLifetime = TimeSpan.FromMinutes(15),
         PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
         EnableMultipleHttp2Connections = true,
     });

// Shared admin/operator authorization gate. Returns null when the caller is authorized (admin
// role, or the operator override secret supplied via ?me / ?admin_key / X-Admin-Key header), or a
// 403 JSON result to short-circuit the endpoint otherwise. Secret comparison is Ordinal.
static IResult? RequireAdminOrOperator(HttpContext http, IUserContext user, IOptions<PageToMovieOptions> opts)
{
    var secret = AuthOptions.ResolveOperatorOverrideSecret(opts.Value.Auth);
    var isOperator = !string.IsNullOrWhiteSpace(secret) &&
        (string.Equals(http.Request.Query["me"].ToString(), secret, StringComparison.Ordinal) ||
         string.Equals(http.Request.Query["admin_key"].ToString(), secret, StringComparison.Ordinal) ||
         string.Equals(http.Request.Headers["X-Admin-Key"].ToString(), secret, StringComparison.Ordinal));

    if (!user.IsAdmin && !isOperator)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    return null;
}

// Shared owner/admin gate for project-mutating endpoints. Loads the project (throwing the usual
// not-found when absent) and returns null when the caller may mutate it, or a 403 JSON result
// carrying the endpoint-specific <paramref name="denyMessage"/> otherwise. Mirrors the inline
// RequireProject + CanUserPublishDemo prologue these endpoints previously repeated verbatim.
static async Task<IResult?> RequireProjectOwnerOrAdmin(
    string id, ProjectStore store, IUserContext user, string denyMessage, CancellationToken ct)
{
    await store.RequireProjectAsync(id, ct);
    if (!await store.CanUserPublishDemoAsync(id, user.UserId, user.IsAdmin, ct))
        return Results.Json(new { ok = false, error = denyMessage },
            statusCode: StatusCodes.Status403Forbidden);
    return null;
}

// Shared body for the clip-version / audio-take mutation endpoints (promote / soft-delete /
// restore / trash-restore): login gate → load project → run the store mutation → map its
// success bool to the standard fail/success JSON. Only the store call and the two messages vary.
static async Task<IResult> RunProjectVersionActionAsync(
    string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts,
    Func<Task<bool>> action, string failureError, string successMessage, CancellationToken ct)
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var success = await action();
        if (!success)
            return Results.BadRequest(new { ok = false, error = failureError });
        return Results.Ok(new { ok = true, message = successMessage });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

// Shared response shaping for the adaptation draft-edit endpoints (reskin / embellish / trim):
// they all run a ScreenplayService.*DraftAsync, then map the shared DraftEditResult to the same
// fail/success JSON and (on apply) auto-commit with an endpoint-specific tag.
static IResult DraftEditResponse(
    ScreenplayService.DraftEditResult result, string id, string commitTag,
    ProjectStore store, IUserContext user)
{
    if (!result.Ok)
        return Results.BadRequest(new { ok = false, error = result.Error });

    if (result.Applied)
        store.TriggerAutoGitCommit(id, commitTag);

    return Results.Ok(new
    {
        ok = true,
        applied = result.Applied,
        projectId = id,
        message = result.Message,
        sceneCountBefore = result.SceneCountBefore,
        sceneCountAfter = result.SceneCountAfter,
        screenplay = result.Status,
        adaptation = result.Applied ? store.GetAdaptationStatus(id, user.UserId) : null,
    });
}

ConfigurePooledSocketsHandler(builder.Services.AddHttpClient("resend", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PageToMovie/1.0");
}));
ConfigurePooledSocketsHandler(builder.Services.AddHttpClient("media-proxy", c => c.Timeout = TimeSpan.FromMinutes(10)));
builder.Services.AddSingleton<IEmailSender>(sp =>
{
    var mail = sp.GetRequiredService<IOptions<PageToMovieOptions>>().Value.Mail;
    if (!string.IsNullOrWhiteSpace(MailOptions.ResolveResendApiKey(mail)))
        return ActivatorUtilities.CreateInstance<ResendEmailSender>(sp);
    if (!string.IsNullOrWhiteSpace(mail?.SmtpHost))
        return ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp);
    return ActivatorUtilities.CreateInstance<LoggingEmailSender>(sp);
});
builder.Services.AddSingleton<IAdminAuthService, AdminAuthService>();
builder.Services.AddSingleton<FilmJobService>();
builder.Services.AddSingleton<IJobProgressSink, SignalRJobProgressSink>();
builder.Services.AddSingleton<AdminMetricsPushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdminMetricsPushService>());
builder.Services.AddSingleton<HttpRequestMetrics>();
builder.Services.AddSingleton<LoadSimLiveStore>();
builder.Services.AddSingleton<ProcessHistoryStore>();

// Grok clients: real HttpClient or fakes (PageToMovie:UseFakes)
var useFakes = builder.Configuration.GetValue("PageToMovie:UseFakes", false)
    || string.Equals(Environment.GetEnvironmentVariable("PageToMovie_USE_FAKES"), "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("PageToMovie_USE_FAKES"), "true", StringComparison.OrdinalIgnoreCase);

if (useFakes)
{
    builder.Services.AddPageToMovieFakes();
    // Propagate the resolved UseFakes to an env var so PageToMovie.Core (which has no config
    // access) merges the fake test-vendor catalog — regardless of whether UseFakes came from
    // config/appsettings or an env var. See SupportedModelCatalog.FakeCatalogEnabled.
    Environment.SetEnvironmentVariable("PAGETOMOVIE_USE_FAKES", "1");
}
else
{
    // Concrete provider clients — each gets its own named HttpClient + base address + connection pooling.
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokVideoClient>(c =>
    {
        c.BaseAddress = new Uri(GrokVideoClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(15);
    }));
    // Single provider (xAI only) — bind IVideoEditClient straight to the concrete client, same
    // pattern as ILipSyncClient/FalLipSyncClient below (no MultiProvider* dispatcher needed).
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokVideoEditClient>(c =>
    {
        c.BaseAddress = new Uri(GrokVideoEditClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(15);
    }));
    builder.Services.AddSingleton<IVideoEditClient>(sp => sp.GetRequiredService<GrokVideoEditClient>());
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GeminiVideoClient>(c =>
    {
        c.BaseAddress = new Uri(GeminiVideoClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(15);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalVideoClient>(c =>
    {
        c.BaseAddress = new Uri(FalVideoClient.ApiBase.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(15);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokImageClient>(c =>
    {
        c.BaseAddress = new Uri(GrokImageClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(5);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GeminiImageClient>(c =>
    {
        c.BaseAddress = new Uri(GeminiImageClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(5);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalImageClient>(c =>
    {
        c.BaseAddress = new Uri(FalImageClient.ApiBase.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(5);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokVisionClient>(c =>
    {
        c.BaseAddress = new Uri(GrokVisionClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(5);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GrokChatClient>(c =>
    {
        c.BaseAddress = new Uri(GrokChatClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(20);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<AnthropicChatClient>(c =>
    {
        c.BaseAddress = new Uri(AnthropicChatClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(20);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<GeminiChatClient>(c =>
    {
        c.BaseAddress = new Uri(GeminiChatClient.ApiBase + "/");
        c.Timeout = TimeSpan.FromMinutes(20);
    }));
    // ClipDialogueVerificationService needs Gemini's real native-video capability specifically
    // (not whatever IVisionClient's routing config points at) — see IGeminiVideoAnalysisClient.
    builder.Services.AddSingleton<IGeminiVideoAnalysisClient>(sp => sp.GetRequiredService<GeminiChatClient>());
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalAudioClient>(c =>
    {
        c.BaseAddress = new Uri(FalAudioClient.ApiBase.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(5);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<SunoClient>(c =>
    {
        c.BaseAddress = new Uri(SunoClient.ApiBase);
        c.Timeout = TimeSpan.FromMinutes(2); // each submit/poll call is short; overall wait spans many such calls
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<AiMusicApiClient>(c =>
    {
        c.BaseAddress = new Uri(AiMusicApiClient.ApiBase);
        c.Timeout = TimeSpan.FromMinutes(2);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<ElevenLabsMusicClient>(c =>
    {
        c.BaseAddress = new Uri(SupportedModelCatalog.ElevenLabsApiBase.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(5); // composing a full-scene track can take a while
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<ElevenLabsScribeClient>(c =>
    {
        c.BaseAddress = new Uri(SupportedModelCatalog.ElevenLabsApiBase.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(3); // STT on a short dialogue segment
    }));
    builder.Services.AddSingleton<IAudioClient, MultiProviderAudioClient>();
    // Lip-sync and voice-clone narration: explicit, human-triggered actions only (never wired
    // into any automatic job/pipeline — see the lip-sync / voice/clone / voice/speak routes).
    // Fal.ai is the only provider today, so these bind straight to the concrete client (no
    // MultiProvider* dispatcher yet — same pattern as IGeminiVideoAnalysisClient below).
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalLipSyncClient>(c =>
    {
        c.BaseAddress = new Uri(FalLipSyncClient.ApiBase.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(6);
    }));
    ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<FalVoiceCloneClient>(c =>
    {
        c.BaseAddress = new Uri(FalVoiceCloneClient.ApiBase.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(4);
    }));
    builder.Services.AddSingleton<ILipSyncClient>(sp => sp.GetRequiredService<FalLipSyncClient>());
    builder.Services.AddSingleton<IVoiceCloneClient>(sp => sp.GetRequiredService<FalVoiceCloneClient>());

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
        sp.GetRequiredService<IOptions<PageToMovieOptions>>(),
        sp.GetRequiredService<ILogger<CachingChatClient>>()));
    builder.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<CachingChatClient>());
    builder.Services.AddSingleton<IVisionClient, MultiProviderVisionClient>();

    // Voice clone + TTS (ElevenLabs). Real client only in the non-fakes branch; the fakes branch
    // above binds IVoiceClient to FakeVoiceClient so no clone/TTS call reaches ElevenLabs.
    builder.Services.AddSingleton<IVoiceClient>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("elevenlabs");
        var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ElevenLabsVoiceClient>>();
        return new ElevenLabsVoiceClient(http, log, allowMockFallback: true);
    });
}

// xAI Files + Responses — Stage‑1 multi-turn (file_id + previous_response_id).
// Registered in both real and fakes mode so DI always resolves IBookFileSessionFactory for
// FilmJobService / Stage1Service: TryCreateAsync returns null when xAI is unconfigured, and is
// hard-disabled under PageToMovie:UseFakes (disableForFakes) so no book is ever uploaded to
// api.x.ai in fakes mode — Stage 1 falls back to the fake IChatClient instead.
ConfigurePooledSocketsHandler(builder.Services.AddHttpClient<XaiResponsesClient>(c =>
{
    c.BaseAddress = new Uri(SupportedModelCatalog.XaiApiBase + "/");
    c.Timeout = TimeSpan.FromMinutes(20);
}));
builder.Services.AddSingleton<PageToMovie.Core.Abstractions.IBookFileSessionFactory, BookFileSessionFactory>();

// Provider-agnostic telemetry/scoring services — registered regardless of PageToMovie:UseFakes
// so admin route handlers that take them as parameters resolve as DI services rather than
// being misinferred as request-body parameters, and so they're available at all in fakes mode.
builder.Services.AddSingleton<SceneMusicScoringService>();
builder.Services.AddSingleton<SmartClassifierModelRouter>();
builder.Services.AddSingleton<ActionCameraOverheadLedger>();
builder.Services.AddSingleton<AiActionOverheadClassifier>();
builder.Services.AddSingleton<JitBenchmarkService>();
builder.Services.AddSingleton<ClipTimingTelemetryRepository>(sp =>
{
    var dbPath = Environment.GetEnvironmentVariable("PAGETOMOVIE_DB_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "pagetomovie.db");
    return new ClipTimingTelemetryRepository(dbPath, sp.GetService<ILogger<ClipTimingTelemetryRepository>>());
});
builder.Services.AddSingleton<GlobalTimingCalibrationService>();

builder.Services.AddSignalR();
builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p =>
        p.AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true));
});

// Large picture-book PDFs + full project zip import (book_images + assets).
// Blazor InputFile / admin import allow up to 512MB; match server form + Kestrel limits.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 512L * 1024 * 1024;
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = 64 * 1024;
});
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
});

var app = builder.Build();
app.UseMiddleware<ProjectAccessMiddleware>();

if (useFakes)
    app.Logger.LogWarning("DEV: fakes mode — login bypass ENABLED (auto dev-user sign-in via /api/auth/dev-login; provider calls resolve to fakes)");

// Cross-Origin Isolation headers required for SharedArrayBuffer (ffmpeg.wasm, WebAssembly threads).
// Must be applied to every response, including the Blazor index.html and all static assets.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    await next();
});

var staticFileProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticFileProvider.Mappings[".wasm"] = "application/wasm";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticFileProvider
});
app.MapStaticAssets();
app.UseAntiforgery();

// Map Blazor UI (PageToMovie.Web WASM) — same origin as REST + SignalR.
// App lives in PageToMovie.Web; do not AddAdditionalAssemblies for that assembly
// (duplicate registration → "Assembly already defined" at startup).
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode();

app.UseCors();

// Wire SignalR sink into job service
var jobs = app.Services.GetRequiredService<FilmJobService>();
jobs.SetProgressSink(app.Services.GetRequiredService<IJobProgressSink>());

// ── Seed demos on first boot ─────────────────────────────────────────────────
// Copy any bundled seed_demos/* entries into /data/_demos/ if not already present.
// This ensures TellTaleHeart (and any future seeds) are available as public demos
// for all new Railway deployments without manual admin steps.
try
{
    var store = app.Services.GetRequiredService<ProjectStore>();
    var demoCatalog = app.Services.GetRequiredService<DemoCatalogService>();
    var demosDir = demoCatalog.DemosDir;
    Directory.CreateDirectory(demosDir);

    // seed_demos/ is baked into the image at /app/seed_demos/
    var seedRoot = Path.Combine(AppContext.BaseDirectory, "seed_demos");
    if (Directory.Exists(seedRoot))
    {
        foreach (var seedDir in Directory.EnumerateDirectories(seedRoot))
        {
            var id = Path.GetFileName(seedDir);
            var targetDir = Path.Combine(demosDir, id);
            var targetMeta = Path.Combine(targetDir, "meta.json");
            var targetMovie = Path.Combine(targetDir, "movie.mp4");

            if (File.Exists(targetMeta) && File.Exists(targetMovie))
                continue; // already seeded — never overwrite user data

            Directory.CreateDirectory(targetDir);

            // Copy meta.json
            var srcMeta = Path.Combine(seedDir, "meta.json");
            if (File.Exists(srcMeta))
                File.Copy(srcMeta, targetMeta, overwrite: true);

            // Copy movie.mp4 — may be bundled in image or referenced from project WIP
            var srcMovie = Path.Combine(seedDir, "movie.mp4");
            if (!File.Exists(srcMovie))
            {
                // Fall back: resolve movie from linked projectId in meta.json
                try
                {
                    var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                        File.ReadAllText(srcMeta));
                    if (meta.TryGetProperty("projectId", out var pidEl) &&
                        pidEl.GetString() is { Length: > 0 } pid)
                    {
                        var wipPath = store.ResolveWipMoviePath(pid);
                        if (wipPath is not null && File.Exists(wipPath))
                            srcMovie = wipPath;
                    }
                }
                catch { /* ignore — seed gracefully skipped if movie unavailable */ }
            }

            if (File.Exists(srcMovie))
                File.Copy(srcMovie, targetMovie, overwrite: true);

            if (File.Exists(targetMeta) && File.Exists(targetMovie))
                app.Logger.LogInformation("Seeded demo {Id} into {TargetDir}", id, targetDir);
            else
                app.Logger.LogWarning("Demo seed {Id} skipped — movie not found at {Src}", id, srcMovie);
        }
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Demo seeding failed (non-fatal)");
}



app.UseMiddleware<HttpRequestMetricsMiddleware>();
app.UseMiddleware<JwtHeaderMiddleware>();
app.Use(async (context, next) =>
{
    var keyProvider = context.RequestServices.GetService<IUserApiKeyProvider>();
    var user = context.RequestServices.GetService<IUserContext>();
    var uid = user?.UserId;
    // Request header override is treated as xAI/Grok (legacy X-Api-Key).
    var xai = !string.IsNullOrWhiteSpace(user?.RequestApiKey)
        ? user!.RequestApiKey
        : keyProvider?.GetKey(uid, "grok");
    var gemini = keyProvider?.GetKey(uid, "gemini");
    var anthropic = keyProvider?.GetKey(uid, "anthropic");
    var fal = keyProvider?.GetKey(uid, "fal");
    var suno = keyProvider?.GetKey(uid, "suno");
    var aimusicapi = keyProvider?.GetKey(uid, "aimusicapi");
    var elevenlabs = keyProvider?.GetKey(uid, "elevenlabs");
    using (ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["grok"] = xai,
        ["gemini"] = gemini,
        ["anthropic"] = anthropic,
        ["fal"] = fal,
        ["suno"] = suno,
        ["aimusicapi"] = aimusicapi,
        ["elevenlabs"] = elevenlabs,
    }))
    using (UserApiCallScope.Push(uid))
    {
        await next();
    }
});
app.MapHub<JobHub>("/hubs/jobs");

// ── Auth (Phase B + D rate limit) ───────────────────────────────────────────
app.MapPost("/api/auth/signup", async (LoginRequest body, IAdminAuthService auth, LoginRateLimiter limiter, HttpContext http) =>
{
    var key = $"{body.Username ?? ""}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new LoginResponse { Ok = false, Error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var result = await auth.SignupAsync(body.Username ?? "", body.Password ?? "", body.Email, http.RequestAborted);
    if (!result.Ok)
    {
        limiter.RecordFailure(key);
        return Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
    }
    limiter.RecordSuccess(key);
    return Results.Ok(result);
});

app.MapPost("/api/auth/login", async (LoginRequest body, IAdminAuthService auth, LoginRateLimiter limiter, HttpContext http) =>
{
    var key = $"{body.Username ?? ""}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new LoginResponse { Ok = false, Error = $"Too many login attempts. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var result = await auth.LoginAsync(body.Username ?? "", body.Password ?? "", http.RequestAborted);
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

/// <summary>
/// Forgot password — emails a reset link when the account has an email; also marks admin request.
/// Always returns the same generic success message (no user enumeration).
/// </summary>
app.MapPost("/api/auth/forgot-password", async (
    ForgotPasswordRequest? body,
    UserDatabaseService userDb,
    IAdminAuthService auth,
    LoginRateLimiter limiter,
    HttpContext http) =>
{
    var name = (body?.Username ?? "").Trim();
    var key = $"forgot|{name}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new { ok = false, error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (name.Length >= 3 || name.Contains('@'))
    {
        try
        {
            await userDb.NotePasswordResetRequestedAsync(name);
            var user = await userDb.ResolveUserAsync(name)
                       ?? await userDb.GetUserByEmailAsync(name);
            if (user is not null && !user.IsDisabled && !string.IsNullOrWhiteSpace(user.Email))
            {
                if (auth is AdminAuthService concrete)
                    await concrete.SendPasswordResetEmailAsync(user);
            }
        }
        catch { /* never leak */ }
    }

    limiter.RecordSuccess(key);
    return Results.Ok(new
    {
        ok = true,
        message = "If that account exists and has a confirmed email, a reset link was sent to your inbox.",
    });
});

/// <summary>Confirm email with one-time token from signup email.</summary>
app.MapPost("/api/auth/confirm-email", async (
    ConfirmEmailRequest? body,
    UserDatabaseService userDb) =>
{
    var token = (body?.Token ?? "").Trim();
    if (token.Length < 10)
        return Results.BadRequest(new { ok = false, error = "Invalid or missing token." });

    var userId = await userDb.ConsumeAuthTokenAsync(token, UserDatabaseService.AuthPurposeEmailConfirm);
    if (userId is null)
    {
        var existingUserId = await userDb.GetUserIdFromAuthTokenHashAsync(token, UserDatabaseService.AuthPurposeEmailConfirm);
        if (existingUserId is not null)
        {
            var user = await userDb.ResolveUserAsync(existingUserId);
            if (UserDatabaseService.IsEmailConfirmed(user))
            {
                return Results.Ok(new { ok = true, message = "Email is already confirmed. You can sign in now." });
            }
        }
        return Results.BadRequest(new { ok = false, error = "This confirmation link is invalid or expired." });
    }

    await userDb.ConfirmEmailAsync(userId);
    return Results.Ok(new { ok = true, message = "Email confirmed. You can sign in now." });
});

/// <summary>Resend confirmation email (by username or email).</summary>
app.MapPost("/api/auth/resend-confirmation", async (
    ForgotPasswordRequest? body,
    UserDatabaseService userDb,
    IAdminAuthService auth,
    LoginRateLimiter limiter,
    HttpContext http) =>
{
    var name = (body?.Username ?? "").Trim();
    var key = $"reconfirm|{name}|{http.Connection.RemoteIpAddress}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new { ok = false, error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    try
    {
        var user = await userDb.ResolveUserAsync(name) ?? await userDb.GetUserByEmailAsync(name);
        if (user is not null && !UserDatabaseService.IsEmailConfirmed(user) && auth is AdminAuthService concrete)
            await concrete.SendEmailConfirmAsync(user);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to resend confirmation email to user={Name}", name);
    }

    limiter.RecordSuccess(key);
    return Results.Ok(new
    {
        ok = true,
        message = "If that account needs confirmation, a new email was sent (or logged in development).",
    });
});

/// <summary>Complete password reset with token from email.</summary>
app.MapPost("/api/auth/reset-password", async (
    ResetPasswordWithTokenRequest? body,
    UserDatabaseService userDb) =>
{
    var token = (body?.Token ?? "").Trim();
    var pw = body?.NewPassword ?? "";
    if (token.Length < 10)
        return Results.BadRequest(new { ok = false, error = "Invalid or missing token." });
    if (pw.Length < 4)
        return Results.BadRequest(new { ok = false, error = "Password must be at least 4 characters." });

    var userId = await userDb.ConsumeAuthTokenAsync(token, UserDatabaseService.AuthPurposePasswordReset);
    if (userId is null)
        return Results.BadRequest(new { ok = false, error = "This reset link is invalid or expired." });

    if (!await userDb.SetPasswordAsync(userId, pw))
        return Results.BadRequest(new { ok = false, error = "Could not update password." });

    // If they had unconfirmed email, allow login after proving inbox via reset link
    await userDb.ConfirmEmailAsync(userId);

    return Results.Ok(new { ok = true, message = "Password updated. You can sign in." });
});

/// <summary>
/// Short-lived media token for &lt;img&gt;/&lt;video src&gt; query auth (?mt=).
/// Requires a full session Bearer JWT. Media tokens carry token_use=media and expire in ~30m.
/// </summary>
app.MapPost("/api/auth/media-token", (HttpContext http, IAdminAuthService auth, IUserContext user) =>
{
    if (http.User?.Identity?.IsAuthenticated != true)
        return Results.Json(new { ok = false, error = "Sign in required" }, statusCode: StatusCodes.Status401Unauthorized);
    // Must be a full session token, not another media token (prevents refresh loops with weak tokens).
    if (auth.IsMediaToken(http.User))
        return Results.Json(new { ok = false, error = "Use a session JWT (Authorization: Bearer)" }, statusCode: StatusCodes.Status401Unauthorized);

    try
    {
        var token = auth.IssueMediaToken(http.User);
        var expires = DateTimeOffset.UtcNow.AddMinutes(IAdminAuthService.MediaTokenMinutes);
        return Results.Ok(new
        {
            ok = true,
            token,
            expiresAt = expires,
            tokenUse = IAdminAuthService.TokenUseMedia,
            minutes = IAdminAuthService.MediaTokenMinutes,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Operator override: POST { "secret": "…" } matching PageToMovie_LOGIN_OVERRIDE.
/// Used by <c>?me=SECRET</c> bootstrap on Railway (not localhost-only).
/// </summary>
app.MapPost("/api/auth/operator-override", (OperatorOverrideRequest? body, IAdminAuthService auth, LoginRateLimiter limiter, HttpContext http) =>
{
    var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var key = $"override|{ip}";
    if (limiter.IsBlocked(key, out var retryAfter))
    {
        return Results.Json(
            new LoginResponse { Ok = false, Error = $"Too many requests. Retry in {retryAfter}s." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var result = auth.LoginWithOperatorOverride(body?.Secret ?? "");
    if (!result.Ok)
    {
        limiter.RecordFailure(key);
        return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
    }
    limiter.RecordSuccess(key);
    return Results.Ok(result);
});

// DEV ONLY: fakes-mode login bypass. When the whole server runs on fakes
// (PageToMovie:UseFakes), the WASM UI calls this on boot to auto-sign-in a deterministic dev user
// so the app is browsable end-to-end without a login screen. Hard-gated on UseFakes at BOTH the
// endpoint (returns 404) and the service (IssueDevFakesLogin fails closed) — a real (non-fakes)
// deployment can never mint a session here.
app.MapPost("/api/auth/dev-login", (IAdminAuthService auth, IOptions<PageToMovieOptions> opts) =>
{
    if (!opts.Value.UseFakes)
        return Results.NotFound();
    var result = auth.IssueDevFakesLogin();
    return result.Ok ? Results.Ok(result) : Results.NotFound();
});

app.MapGet("/api/auth/me", async (IUserContext user, IUserApiKeyProvider keys, UserDatabaseService userDb) =>
{
    var roles = user.Roles.ToList();
    var personal = false;
    try
    {
        personal = !string.IsNullOrWhiteSpace(
            await userDb.GetDecryptedXaiApiKeyAsync(user.UserId).ConfigureAwait(false));
    }
    catch { /* ignore */ }

    return Results.Ok(new MeResponse
    {
        Ok = true,
        UserId = user.UserId,
        Roles = roles,
        IsAdmin = user.IsAdmin,
        IsAuthenticated = user.IsAuthenticated,
        // Personal key only when signed in; otherwise false even if server env has XAI_API_KEY.
        HasApiKey = user.IsAuthenticated && (personal || !string.IsNullOrWhiteSpace(user.RequestApiKey)),
    });
});

// Live admin state (Phase C metrics + locks + jobs)
app.MapGet("/api/admin/state", (
    IUserContext user,
    ProjectStore store,
    AdminMetricsPushService metricsPush,
    HttpRequestMetrics httpMetrics,
    LoadSimLiveStore loadSimStore,
    ProcessHistoryStore processHistory,
    VolumeDiskTelemetryService diskTelemetry) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    diskTelemetry.RecordDailySnapshotIfNeeded();
    var disk = diskTelemetry.GetDiskStatus();
    var diskHistory = diskTelemetry.GetDiskHistory(30);

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
        disk,
        diskHistory,
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

/// <summary>
/// Admin: book text registry + adaptation_conversion artifacts + xAI provider file_id handles.
/// </summary>
app.MapGet("/api/admin/book-cache", async (
    IUserContext user,
    BookTextRegistryService books,
    int? take,
    CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var snap = await books.GetAdminCacheSnapshotAsync(take ?? 100, ct).ConfigureAwait(false);
    return Results.Ok(new
    {
        ok = true,
        bookCount = snap.BookCount,
        artifactCount = snap.ArtifactCount,
        providerFileCount = snap.ProviderFileCount,
        totalBookBytes = snap.TotalBookBytes,
        books = snap.Books.Select(b => new
        {
            bookId = b.BookId,
            sha256 = b.Sha256,
            bookTitle = b.BookTitle,
            projects = b.Projects,
            byteCount = b.ByteCount,
            createdAt = b.CreatedAt,
            artifactCount = b.ArtifactCount,
            accessLinkCount = b.AccessLinkCount,
            provider = b.Provider,
            providerFileId = b.ProviderFileId,
            fileExpiresAtUnix = b.FileExpiresAtUnix,
            lastResponseId = b.LastResponseId,
            providerFileUpdatedAt = b.ProviderFileUpdatedAt,
        }),
        recentArtifacts = snap.RecentArtifacts.Select(a => new
        {
            artifactId = a.ArtifactId,
            bookId = a.BookId,
            artifactKind = a.ArtifactKind,
            modelId = a.ModelId,
            promptVersion = a.PromptVersion,
            temperature = a.Temperature,
            createdAt = a.CreatedAt,
            contentBytes = a.ContentBytes,
        }),
    });
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

app.MapGet("/api/admin/learning/review-comparison", (
    IUserContext user,
    ReviewEventStore learning,
    string? projectId) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var comparison = learning.GetReviewComparison(projectId);
    return Results.Ok(comparison);
});

app.MapPost("/api/admin/learning/synthesize-prompt-improvements", async (
    IUserContext user,
    LearningProposalService proposals,
    string? projectId,
    CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    var result = await proposals.SynthesizePromptImprovementsAsync(projectId, ct);
    return Results.Ok(result);
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

// Users & credits overview (admin)
app.MapGet("/api/admin/users", async (IUserContext user, CreditService credits) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    var overview = await credits.GetAdminOverviewAsync(recentLedger: 50);
    return Results.Ok(new { ok = true, overview });
});

/// <summary>Admin: download full project folder as zip for local debug.</summary>
app.MapGet("/api/admin/projects/{id}/export", async (
    string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    HttpContext http,
    ProjectArchiveService archives,
    CancellationToken ct) =>
{
    if (RequireAdminOrOperator(http, user, opts) is { } forbidden)
        return forbidden;
    try
    {
        var exp = await archives.ExportAsync(id, ct);
        return Results.File(
            exp.Stream,
            exp.ContentType,
            exp.FileName,
            enableRangeProcessing: false);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Admin: Download all server diagnostic logs (jobs, edit logs, prompts, system info) as a zip archive.</summary>
app.MapGet("/api/admin/logs/export", async (
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    HttpContext http,
    ServerLogExportService logExporter,
    CancellationToken ct) =>
{
    if (RequireAdminOrOperator(http, user, opts) is { } forbidden)
        return forbidden;

    try
    {
        var bytes = await logExporter.ExportLogsZipAsync(ct);
        var fileName = $"pagetomovie-server-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
        return Results.File(bytes, "application/zip", fileName);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Admin: Get JSON summary of server diagnostic state and active job logs.</summary>
app.MapGet("/api/admin/logs", async (
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    HttpContext http,
    FilmJobService jobs,
    ProjectStore projects,
    CancellationToken ct) =>
{
    if (RequireAdminOrOperator(http, user, opts) is { } forbidden)
        return forbidden;

    var projectList = await projects.ListProjectsAsync(ct);

    return Results.Ok(new
    {
        ok = true,
        exportUrl = "/api/admin/logs/export",
        system = new
        {
            machineName = Environment.MachineName,
            osVersion = Environment.OSVersion.ToString(),
            activeProject = projects.ActiveProjectId,
            utcTime = DateTimeOffset.UtcNow,
        },
        jobs = jobs.ListJobs(take: 50),
        projects = projectList.Select(p => p.Id),
    });
});

app.MapGet("/api/admin/timing-telemetry/trend", async (
    IUserContext user,
    GlobalTimingCalibrationService calibration) =>
{
    var stats = await calibration.GetStatsAsync();
    var trend = await calibration.GetTrendAsync(maxPoints: 30);
    return Results.Ok(new
    {
        ok = true,
        stats,
        trend
    });
});

app.MapPost("/api/admin/timing-telemetry/seed", async (
    IUserContext user,
    GlobalTimingCalibrationService calibration) =>
{
    int count = await calibration.SeedDefaultBenchmarksAsync();
    return Results.Ok(new
    {
        ok = true,
        message = $"Seeded {count} empirical benchmark entries into SQLite database.",
        count
    });
});

/// <summary>Admin: recent generation_errors rows (partial-coverage / structural-gate / transient-retry events).</summary>
app.MapGet("/api/admin/generation-errors", async (
    IUserContext user,
    UserDatabaseService userDb,
    string? errorType,
    string? projectId,
    int? take,
    CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    var rows = await userDb.ListGenerationErrorsAsync(errorType, projectId, take ?? 100, ct);
    return Results.Ok(new { ok = true, rows });
});

/// <summary>Aggregated AI/model-call telemetry (user_api_calls table) for the admin AI-Calls analytics page.</summary>
app.MapGet("/api/admin/ai-calls", async (IUserContext user, AiCallAnalyticsService analytics, int? maxRows, CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var data = await analytics.BuildAsync(Math.Clamp(maxRows ?? 4000, 100, 20000), ct);
        return Results.Ok(new { ok = true, data });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});

/// <summary>Open a local folder on disk in Windows File Explorer (or OS file manager).</summary>
app.MapPost("/api/system/open-folder", (OpenFolderRequest body, ProjectStore store) =>
{
    var path = body?.Path;
    if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(body?.ProjectId))
    {
        path = store.GetProjectDir(body.ProjectId);
    }
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { ok = false, error = "Path is required." });
    }

    try
    {
        var targetPath = path.Trim();
        if (OperatingSystem.IsWindows())
        {
            targetPath = targetPath.Replace('/', '\\');
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{targetPath}\"",
                    UseShellExecute = true
                });
                return Results.Ok(new { ok = true, opened = targetPath });
            }
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{parent}\"",
                    UseShellExecute = true
                });
                return Results.Ok(new { ok = true, opened = parent });
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            System.Diagnostics.Process.Start("open", $"\"{targetPath}\"");
            return Results.Ok(new { ok = true, opened = targetPath });
        }
        else if (OperatingSystem.IsLinux())
        {
            System.Diagnostics.Process.Start("xdg-open", $"\"{targetPath}\"");
            return Results.Ok(new { ok = true, opened = targetPath });
        }

        return Results.BadRequest(new { ok = false, error = $"Path '{targetPath}' does not exist on server disk." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Open a scene composite or full cut in the user's preferred external video editor.</summary>
app.MapPost("/api/system/open-editor", (OpenEditorRequest body, ProjectStore store) =>
{
    if (string.IsNullOrWhiteSpace(body?.ProjectId))
        return Results.BadRequest(new { ok = false, error = "ProjectId is required." });

    var projectDir = store.GetProjectDir(body.ProjectId);
    var editorName = string.IsNullOrWhiteSpace(body.EditorName) ? "ClipChamp" : body.EditorName.Trim();

    string? videoPath = null;
    if (body.SceneNumber is int sn && sn > 0)
    {
        if (body.ClipNumber is int cn && cn > 0)
        {
            var cPath = Path.Combine(projectDir, "assets", "video", $"scene_{sn:D3}_clip_{cn:D2}.mp4");
            if (File.Exists(cPath)) videoPath = cPath;
        }
        if (videoPath is null)
        {
            var compPath = Path.Combine(projectDir, "assets", "video", $"scene_{sn:D3}_composite.mp4");
            if (File.Exists(compPath)) videoPath = compPath;
        }
    }

    if (videoPath is null)
    {
        var wipMovie = Path.Combine(projectDir, "movie.mp4");
        if (File.Exists(wipMovie)) videoPath = wipMovie;
        else
        {
            var altWip = Path.Combine(projectDir, "assets", "video", "wip_movie.mp4");
            if (File.Exists(altWip)) videoPath = altWip;
        }
    }

    if (videoPath is null)
    {
        var videoDir = Path.Combine(projectDir, "assets", "video");
        if (Directory.Exists(videoDir)) videoPath = videoDir;
        else videoPath = projectDir;
    }

    try
    {
        var targetPath = videoPath.Trim();
        string? relativeVideoUrl = null;
        if (body.SceneNumber is int targetSn && targetSn > 0)
        {
            if (body.ClipNumber is int targetCn && targetCn > 0)
                relativeVideoUrl = $"/api/projects/{body.ProjectId}/scenes/{targetSn}/clips/{targetCn}/video";
            else
                relativeVideoUrl = $"/api/projects/{body.ProjectId}/scenes/{targetSn}/composite";
        }
        else
        {
            relativeVideoUrl = $"/api/projects/{body.ProjectId}/movie";
        }

        if (OperatingSystem.IsWindows())
        {
            targetPath = targetPath.Replace('/', '\\');

            if (string.Equals(editorName, "ClipChamp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(editorName, "Clipchamp", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Launch Microsoft Clipchamp via registered Windows protocol ms-clipchamp:
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ms-clipchamp:",
                        UseShellExecute = true
                    });

                    // Reveal/select the target video file in Explorer so user can easily drag into Clipchamp
                    if (File.Exists(targetPath))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                        }
                        catch { /* best-effort explorer reveal */ }
                    }

                    return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = "Clipchamp", VideoUrl = relativeVideoUrl });
                }
                catch { /* fallback to default */ }
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = editorName,
                    Arguments = $"\"{targetPath}\"",
                    UseShellExecute = true
                });
                return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = editorName, VideoUrl = relativeVideoUrl });
            }
            catch
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
                return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = "Default OS Editor", VideoUrl = relativeVideoUrl });
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                System.Diagnostics.Process.Start("open", $"\"{targetPath}\"");
                return Results.Ok(new OpenEditorResponse { Ok = true, Opened = targetPath, Editor = editorName, VideoUrl = relativeVideoUrl });
            }
            catch (Exception)
            {
                return Results.Ok(new OpenEditorResponse { Ok = false, IsRemote = true, VideoUrl = relativeVideoUrl, Error = $"Remote server cannot open desktop app. Stream video to open in {editorName}." });
            }
        }
        else
        {
            // Linux / Cloud container (e.g. Railway)
            return Results.Ok(new OpenEditorResponse
            {
                Ok = false,
                IsRemote = true,
                VideoUrl = relativeVideoUrl,
                Error = $"Server is running in cloud. Streaming video file to open in {editorName} on your device."
            });
        }
    }
    catch (Exception ex)
    {
        return Results.Ok(new OpenEditorResponse { Ok = false, Error = ex.Message });
    }
});

/// <summary>Download project folder as zip (logged in user / operator).</summary>
app.MapGet("/api/projects/{id}/export", async (
    string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    ProjectArchiveService archives,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var exp = await archives.ExportAsync(id, ct);
        return Results.File(
            exp.Stream,
            exp.ContentType,
            exp.FileName,
            enableRangeProcessing: false);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Admin: import a project zip (full folder). Multipart field <c>file</c>;
/// optional form fields <c>projectId</c>, <c>overwrite</c>=true|false.
/// </summary>
app.MapPost("/api/admin/projects/import", async (
    HttpRequest req,
    IUserContext user,
    ProjectArchiveService archives,
    CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    if (!req.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form with file required" });

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "file required (project zip)" });

    var preferredId = form["projectId"].ToString();
    if (string.IsNullOrWhiteSpace(preferredId))
        preferredId = form["id"].ToString();

    var targetUserId = form["targetUserId"].ToString();
    if (string.IsNullOrWhiteSpace(targetUserId))
        targetUserId = form["userId"].ToString();
    if (string.IsNullOrWhiteSpace(targetUserId))
        targetUserId = form["ownerUserId"].ToString();

    var overwrite = string.Equals(form["overwrite"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(form["overwrite"].ToString(), "1", StringComparison.OrdinalIgnoreCase);

    try
    {
        await using var stream = file.OpenReadStream();
        var result = await archives.ImportAsync(
            stream,
            preferredId: string.IsNullOrWhiteSpace(preferredId) ? null : preferredId.Trim(),
            overwrite: overwrite,
            targetUserId: string.IsNullOrWhiteSpace(targetUserId) ? null : targetUserId.Trim(),
            ct: ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = result.ProjectId,
            active = result.Project,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// User-mode import: any signed-in user imports a project zip into their OWN namespace. The owner is
/// forced to the caller (the zip's original owner is ignored) so a user can't import into — or
/// overwrite — someone else's project. Multipart field <c>file</c>; optional <c>overwrite</c>=true.
/// </summary>
app.MapPost("/api/projects/import", async (
    HttpRequest req,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    ProjectArchiveService archives,
    ProjectStore store,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (string.IsNullOrWhiteSpace(user.UserId))
        return Results.Json(new { ok = false, error = "sign in required" },
            statusCode: StatusCodes.Status401Unauthorized);
    if (!req.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form with file required" });

    var form = await req.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "file required (project zip)" });

    // Optional target name — import under a name of the caller's choosing instead of the zip's slug
    // (forceOwnerUserId still re-namespaces it under the caller, so only the slug is taken from this).
    var name = form["name"].ToString();
    if (string.IsNullOrWhiteSpace(name)) name = form["projectId"].ToString();

    var overwrite = string.Equals(form["overwrite"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(form["overwrite"].ToString(), "1", StringComparison.OrdinalIgnoreCase);

    try
    {
        await using var stream = file.OpenReadStream();
        var result = await archives.ImportAsync(
            stream,
            preferredId: string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            overwrite: overwrite,
            targetUserId: user.UserId,
            forceOwnerUserId: user.UserId,
            ct: ct);

        // A custom name only re-slugged the folder/id above; also set the display title to match so
        // the imported project shows the chosen name, not the zip's original title.
        var active = result.Project;
        if (result.Ok && !string.IsNullOrWhiteSpace(name))
        {
            try { active = await store.RenameProjectAsync(result.ProjectId, name.Trim(), ct); }
            catch { /* id/slug already correct; title is best-effort */ }
        }

        return Results.Ok(new
        {
            ok = true,
            projectId = result.ProjectId,
            active,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/admin/users/credits", async (
    AdminGrantCreditsRequest body,
    IUserContext user,
    CreditService credits) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = "userId is required" });
    if (Math.Abs(body.AmountUsd) < 0.0001)
        return Results.BadRequest(new { ok = false, error = "amountUsd must be non-zero" });

    var summary = await credits.GrantAsync(body.UserId.Trim(), body.AmountUsd, body.Note);
    if (summary is null)
        return Results.NotFound(new { ok = false, error = "user not found" });

    return Results.Ok(new { ok = true, user = summary });
});

/// <summary>Admin: set a user's password (forgot-password completion or support).</summary>
app.MapPost("/api/admin/users/set-password", async (
    AdminSetUserPasswordRequest body,
    IUserContext user,
    UserDatabaseService userDb,
    IAdminAuthService auth) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = "userId is required" });
    if (string.IsNullOrWhiteSpace(body.NewPassword) || body.NewPassword.Length < 4)
        return Results.BadRequest(new { ok = false, error = "New password must be at least 4 characters." });
    if (!await auth.VerifyCallerPasswordAsync(user.UserId, body.AdminPassword ?? ""))
        return Results.Json(new { ok = false, error = "Admin password is incorrect." },
            statusCode: StatusCodes.Status403Forbidden);

    var target = await userDb.ResolveUserAsync(body.UserId.Trim());
    if (target is null)
        return Results.NotFound(new { ok = false, error = "user not found" });

    var ok = await userDb.SetPasswordAsync(target.UserId, body.NewPassword);
    if (!ok)
        return Results.BadRequest(new { ok = false, error = "Could not update password." });

    return Results.Ok(new
    {
        ok = true,
        userId = target.UserId,
        username = target.Username,
        message = $"Password updated for {target.Username}.",
    });
});

/// <summary>Admin: disable or re-enable a user account.</summary>
app.MapPost("/api/admin/users/disabled", async (
    AdminSetUserDisabledRequest body,
    IUserContext user,
    UserDatabaseService userDb) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = "userId is required" });

    var target = await userDb.ResolveUserAsync(body.UserId.Trim());
    if (target is null)
        return Results.NotFound(new { ok = false, error = "user not found" });

    if (string.Equals(target.UserId, user.UserId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Username, user.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { ok = false, error = "You cannot disable your own account." });

    if (body.Disabled &&
        string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase))
    {
        var activeAdmins = await userDb.CountActiveAdminsAsync();
        // If this admin is currently active, disabling them must leave ≥1 admin.
        if (!target.IsDisabled && activeAdmins <= 1)
            return Results.BadRequest(new { ok = false, error = "Cannot disable the last active admin." });
    }

    var summary = await userDb.SetUserDisabledAsync(target.UserId, body.Disabled);
    if (summary is null)
        return Results.NotFound(new { ok = false, error = "user not found" });

    return Results.Ok(new
    {
        ok = true,
        user = summary,
        message = body.Disabled
            ? $"Disabled {summary.Username}."
            : $"Re-enabled {summary.Username}.",
    });
});

/// <summary>
/// Admin hard-delete: requires typing the target username + the acting admin's password
/// (or operator override secret). Cascades credit ledger, demos, and owned projects.
/// </summary>
app.MapPost("/api/admin/users/delete", async (
    AdminDeleteUserRequest body,
    IUserContext user,
    IAdminAuthService auth,
    UserDatabaseService userDb,
    ProjectStore projects,
    DemoCatalogService demos,
    CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);

    if (body is null || string.IsNullOrWhiteSpace(body.UserId))
        return Results.BadRequest(new { ok = false, error = "userId is required" });
    if (string.IsNullOrWhiteSpace(body.ConfirmUsername))
        return Results.BadRequest(new { ok = false, error = "confirmUsername is required" });
    if (string.IsNullOrEmpty(body.AdminPassword))
        return Results.BadRequest(new { ok = false, error = "adminPassword is required" });

    if (!await auth.VerifyCallerPasswordAsync(user.UserId, body.AdminPassword, ct))
        return Results.Json(new { ok = false, error = "Admin password is incorrect." },
            statusCode: StatusCodes.Status403Forbidden);

    var target = await userDb.ResolveUserAsync(body.UserId.Trim(), ct);
    if (target is null)
        return Results.NotFound(new { ok = false, error = "user not found" });

    if (!string.Equals(body.ConfirmUsername.Trim(), target.Username, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(body.ConfirmUsername.Trim(), target.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new
        {
            ok = false,
            error = "confirmUsername must match the target username exactly.",
        });

    if (string.Equals(target.UserId, user.UserId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Username, user.UserId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { ok = false, error = "You cannot delete your own account." });

    if (string.Equals(target.Role, "Admin", StringComparison.OrdinalIgnoreCase))
    {
        var activeAdmins = await userDb.CountActiveAdminsAsync(ct);
        var countsAsActive = !target.IsDisabled;
        if (countsAsActive && activeAdmins <= 1)
            return Results.BadRequest(new { ok = false, error = "Cannot delete the last active admin." });
    }

    var deletedProjects = 0;
    var projectErrors = new List<string>();
    if (body.DeleteOwnedProjects)
    {
        var all = await projects.ListProjectsAsync(ct);
        var owned = all.Where(p =>
            !string.IsNullOrWhiteSpace(p.OwnerUserId) &&
            (string.Equals(p.OwnerUserId, target.UserId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.OwnerUserId, target.Username, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var p in owned)
        {
            try
            {
                await projects.DeleteProjectAsync(p.Id, ct);
                deletedProjects++;
            }
            catch (Exception ex)
            {
                projectErrors.Add($"{p.Id}: {ex.Message}");
            }
        }
    }

    var deletedDemos = demos.HardDeleteAllByUser(target.UserId);
    // Also match demos stored under username if different from user_id.
    if (!string.Equals(target.UserId, target.Username, StringComparison.OrdinalIgnoreCase))
        deletedDemos += demos.HardDeleteAllByUser(target.Username);

    var removed = await userDb.HardDeleteUserAsync(target.UserId, ct);
    if (!removed)
        return Results.NotFound(new { ok = false, error = "user not found or already deleted" });

    return Results.Ok(new
    {
        ok = true,
        userId = target.UserId,
        username = target.Username,
        deletedProjects,
        deletedDemos,
        projectErrors = projectErrors.Count > 0 ? projectErrors : null,
        message = $"Deleted {target.Username} (projects: {deletedProjects}, demos: {deletedDemos}).",
    });
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
app.MapGet("/api/admin/models-catalog", (IUserContext user) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" }, statusCode: StatusCodes.Status403Forbidden);

    return Results.Ok(new
    {
        ok = true,
        catalogPath = SupportedModelCatalog.GetCatalogSourceLabel(),
        rawJson = SupportedModelCatalog.GetEmbeddedCatalogJson(),
        editable = false,
        models = SupportedModelCatalog.Entries,
        capabilities = SupportedModelCatalog.RegisteredCapabilities,
        taskRankings = SupportedModelCatalog.TaskRankings,
    });
});

app.MapPut("/api/admin/models-catalog", (IUserContext user) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" }, statusCode: StatusCodes.Status403Forbidden);

    // The catalog is the single source of truth, embedded at build time. Runtime edits are gone:
    // change PageToMovie.Core/config/models_catalog.json in git and redeploy.
    return Results.Json(new
    {
        ok = false,
        error = "The models catalog is embedded at build time and cannot be edited at runtime. " +
                "Edit PageToMovie.Core/config/models_catalog.json in git and redeploy.",
    }, statusCode: StatusCodes.Status405MethodNotAllowed);
});

app.MapPost("/api/admin/models-catalog/reload", (IUserContext user) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" }, statusCode: StatusCodes.Status403Forbidden);

    SupportedModelCatalog.ReloadCatalog();
    return Results.Ok(new
    {
        ok = true,
        message = "Models catalog reloaded successfully.",
        modelsCount = SupportedModelCatalog.Entries.Count,
    });
});

app.MapPost("/api/admin/models-catalog/validate", async (HttpContext http, IUserContext user) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" }, statusCode: StatusCodes.Status403Forbidden);

    using var reader = new StreamReader(http.Request.Body);
    var rawJson = await reader.ReadToEndAsync();
    try
    {
        if (!SupportedModelCatalog.TryLoadFromJson(rawJson))
            return Results.BadRequest(new { ok = false, error = "Invalid catalog JSON" });
        var errors = SupportedModelCatalog.ValidateEnabledModels();
        // Reload real on-disk catalog so in-memory state is not left on the draft payload
        SupportedModelCatalog.ReloadCatalog();
        return Results.Ok(new
        {
            ok = errors.Count == 0,
            errorCount = errors.Count,
            errors,
            message = errors.Count == 0
                ? "All enabled models have required fields."
                : $"{errors.Count} validation issue(s) — fix before save.",
        });
    }
    catch (Exception ex)
    {
        try { SupportedModelCatalog.ReloadCatalog(); } catch { /* best effort */ }
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});



app.MapPost("/api/admin/models-catalog/check-updates", async (IUserContext user, CatalogUpdateProbeService probe, CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var result = await probe.ScanAsync(user.UserId, ct).ConfigureAwait(false);
        return Results.Ok(new { ok = true, result });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});


app.MapPost("/api/admin/chat-cache/clear", (IUserContext user, IServiceProvider sp) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    // Not registered under PageToMovie:UseFakes (fakes never hit the network, so there's nothing
    // to cache) — report that plainly instead of a DI resolution error.
    var cache = sp.GetService<CachingChatClient>();
    if (cache is null)
        return Results.Ok(new { ok = true, filesRemoved = 0, note = "chat cache not active (fakes mode)" });
    var removed = cache.ClearCache();
    return Results.Ok(new { ok = true, filesRemoved = removed });
});

app.MapPost("/api/admin/test-email", async (
    TestEmailRequest? body,
    IUserContext user,
    IEmailSender sender,
    IOptions<PageToMovieOptions> opts) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" }, statusCode: StatusCodes.Status403Forbidden);

    var to = (body?.ToEmail ?? "").Trim();
    if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
        return Results.BadRequest(new { ok = false, error = "Valid recipient email address (toEmail) is required." });

    var senderType = sender.GetType().Name;
    var resolvedKey = MailOptions.ResolveResendApiKey(opts.Value.Mail);
    var resendKeyResolved = !string.IsNullOrWhiteSpace(resolvedKey);

    var checkedEnvs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
    {
        var k = de.Key?.ToString();
        if (!string.IsNullOrWhiteSpace(k) && (k.StartsWith("Resend", StringComparison.OrdinalIgnoreCase) || k.Contains("Mail", StringComparison.OrdinalIgnoreCase)))
        {
            checkedEnvs[k] = !string.IsNullOrWhiteSpace(de.Value?.ToString());
        }
    }

    try
    {
        await sender.SendAsync(
            to,
            "PageToMovie Resend Test Email",
            $"<h1>PageToMovie Email Test</h1><p>This email was successfully sent via <strong>{senderType}</strong> on Railway.</p>",
            $"PageToMovie Email Test: Sent via {senderType} on Railway.");

        return Results.Ok(new
        {
            ok = true,
            message = $"Test email sent to {to} via {senderType}.",
            senderType,
            resendKeyResolved,
            checkedEnvs,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            ok = false,
            error = ex.Message,
            senderType,
            resendKeyResolved,
            checkedEnvs,
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
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

// Shared, instance-wide YouTube channel connection (not per-user). Status is readable by
// anyone; connecting/disconnecting the channel is admin-only.
app.MapGet("/api/youtube/status", async (YouTubeAuthService youTube, CancellationToken ct) =>
{
    var connected = youTube.IsConfigured && await youTube.IsConnectedAsync(ct);
    return Results.Ok(new { ok = true, configured = youTube.IsConfigured, connected });
});

app.MapGet("/api/youtube/connect-url", (IUserContext user, YouTubeAuthService youTube, string? returnTo) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    if (!youTube.IsConfigured)
        return Results.Json(new
        {
            ok = false,
            error = "YouTube OAuth is not configured (PageToMovie:YouTube:ClientId/ClientSecret/RedirectUri).",
        }, statusCode: StatusCodes.Status409Conflict);
    var state = Guid.NewGuid().ToString("N");
    return Results.Ok(new { ok = true, url = youTube.BuildAuthorizationUrl(state, returnTo) });
});

async Task ProcessYouTubeOAuthCallbackAsync(HttpContext http, YouTubeAuthService youTube, CancellationToken ct)
{
    var code = http.Request.Query["code"].FirstOrDefault();
    var state = http.Request.Query["state"].FirstOrDefault();
    var error = http.Request.Query["error"].FirstOrDefault();

    // Fallback: If parameters were not bound from query (e.g. proxy path normalization), extract from raw request URL
    var rawUrl = (http.Request.Path.Value ?? "") + (http.Request.QueryString.Value ?? "");
    if (string.IsNullOrWhiteSpace(code))
    {
        var mCode = OAuthCodeParamRegex.Match(rawUrl);
        if (mCode.Success)
            code = Uri.UnescapeDataString(mCode.Groups[1].Value);
    }
    if (string.IsNullOrWhiteSpace(state))
    {
        var mState = OAuthStateParamRegex.Match(rawUrl);
        if (mState.Success)
            state = Uri.UnescapeDataString(mState.Groups[1].Value);
    }
    if (string.IsNullOrWhiteSpace(error))
    {
        var mErr = OAuthErrorParamRegex.Match(rawUrl);
        if (mErr.Success)
            error = Uri.UnescapeDataString(mErr.Groups[1].Value);
    }

    var returnPath = "/review";
    var stateOk = !string.IsNullOrWhiteSpace(state) && youTube.TryConsumeState(state!, out returnPath);

    if (!string.IsNullOrWhiteSpace(error))
    {
        http.Response.Redirect($"{returnPath}?youtube=error&message={Uri.EscapeDataString(error)}");
        return;
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        http.Response.Redirect(returnPath + "?youtube=error&message=" + Uri.EscapeDataString("Missing authorization code from Google."));
        return;
    }

    if (!stateOk)
    {
        http.Response.Redirect(returnPath + "?youtube=error&message=" + Uri.EscapeDataString("Invalid or expired request."));
        return;
    }

    try
    {
        await youTube.ExchangeCodeAsync(code, ct);
        http.Response.Redirect($"{returnPath}?youtube=connected");
    }
    catch (Exception ex)
    {
        http.Response.Redirect($"{returnPath}?youtube=error&message={Uri.EscapeDataString(ex.Message)}");
    }
}

app.MapGet("/api/youtube/oauth2callback/{*remainder}", ProcessYouTubeOAuthCallbackAsync);
app.MapGet("/api/youtube/oauth2callback", ProcessYouTubeOAuthCallbackAsync);

app.MapPost("/api/youtube/disconnect", async (IUserContext user, YouTubeAuthService youTube, CancellationToken ct) =>
{
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "admin role required" },
            statusCode: StatusCodes.Status403Forbidden);
    await youTube.DisconnectAsync(ct);
    return Results.Ok(new { ok = true });
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

app.MapGet("/health", (ProjectStore store, IOptions<PageToMovieOptions> opts, IUserContext user, IUserApiKeyProvider keyProvider) =>
    Results.Ok(new
    {
        ok = true,
        service = "PageToMovie.Api",
        workspace = store.WorkspaceRoot,
        activeProject = store.ActiveProjectId,
        useFakes = opts.Value.UseFakes || useFakes,
        enableReadCaches = store.ReadCachesEnabled,
        capacity = opts.Value.Capacity,
        xaiConfigured = keyProvider.HasKey(user.UserId) || (opts.Value.AllowServerApiKeyFallback && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"))) || useFakes,
        xaiKeyPresent = keyProvider.HasKey(user.UserId) || (opts.Value.AllowServerApiKeyFallback && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY"))),
        userId = user.UserId,
        isAdmin = user.IsAdmin,
    }));

app.MapGet("/api/capacity", (FilmJobService jobService, IOptions<PageToMovieOptions> opts) =>
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

app.MapGet("/api/projects", async (
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb,
    CancellationToken ct) =>
{
    // Project inventory is not public — requires sign-in (prevents anonymous enumeration).
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var all = await store.ListProjectsAsync(ct);

    IReadOnlyList<ProjectInfo> list;
    if (user.IsAdmin)
    {
        list = all;
    }
    else
    {
        // Resolve all known identities for this account so projects created under a
        // previous handle / email-shaped id (folder budcribarmsn_com vs budcribar) still appear.
        UserEntity? me = null;
        try
        {
            me = await userDb.GetUserByIdAsync(user.UserId, ct).ConfigureAwait(false)
                 ?? await userDb.GetUserByUsernameAsync(user.UserId, ct).ConfigureAwait(false);
        }
        catch { /* offline */ }

        var aliases = ProjectOwnership.CollectAliases(
            user.UserId,
            canonicalUserId: me?.UserId,
            username: me?.Username,
            email: me?.Email);
        list = all.Where(p => ProjectOwnership.IsOwnedBy(p, aliases)).ToList();

        // Self-heal: if folder/owner field used a stale alias, rewrite ownerUserId to canonical id
        // so future filters and admin tools stay consistent. Best-effort; never delete.
        var canonical = !string.IsNullOrWhiteSpace(me?.UserId) ? me!.UserId.Trim() : user.UserId.Trim();
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            foreach (var p in list)
            {
                if (string.Equals(p.OwnerUserId, canonical, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    await store.RepairProjectOwnerAsync(p.Id, canonical, ct).ConfigureAwait(false);
                    p.OwnerUserId = canonical;
                }
                catch { /* non-fatal */ }
            }
        }
    }

    var userActiveId = await userDb.GetUserActiveProjectAsync(user.UserId, ct);
    var activeId = !string.IsNullOrWhiteSpace(userActiveId) ? userActiveId : store.ActiveProjectId;
    if (string.IsNullOrWhiteSpace(activeId) && list.Count > 0)
        activeId = list[0].Id;
    var active = list.FirstOrDefault(p =>
        string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase)) ?? (list.Count > 0 ? list[0] : null);
    return Results.Ok(new { ok = true, active, projects = list });
});

app.MapPost("/api/projects/{id}/activate", async (
    string id,
    ProjectStore store,
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var p = await store.ActivateAsync(id, ct);
        await userDb.SetUserActiveProjectAsync(user.UserId, p.Id, ct);
        return Results.Ok(new { ok = true, active = p });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Create a new project folder under projects/ and make it active.</summary>
app.MapPost("/api/projects", async (
    CreateProjectRequest? body,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb,
    CancellationToken ct) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        var name = body?.Name ?? body?.Id ?? body?.Title ?? "";
        var title = body?.Title;
        // Prefer stable DB UserId so folder + ownerUserId stay consistent across re-login.
        var ownerId = user.UserId;
        try
        {
            var me = await userDb.GetUserByIdAsync(user.UserId, ct).ConfigureAwait(false)
                     ?? await userDb.GetUserByUsernameAsync(user.UserId, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(me?.UserId))
                ownerId = me!.UserId.Trim();
        }
        catch { /* use JWT id */ }

        var p = await store.CreateProjectAsync(
            name, title, ct, ownerUserId: ownerId, studioPath: body?.StudioPath);
        await userDb.SetUserActiveProjectAsync(user.UserId, p.Id, ct);
        var all = await store.ListProjectsAsync(ct);
        var aliases = ProjectOwnership.CollectAliases(ownerId, user.UserId);
        var list = user.IsAdmin
            ? all
            : all.Where(x => ProjectOwnership.IsOwnedBy(x, aliases)).ToList();
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
app.MapDelete("/api/projects/{id}", async (
    string id,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
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
                "/api/jobs?projectId=MyStory",
                "/api/jobs/{jobId}",
            },
        });
    }

    var filterUser = wantMine ? user.UserId : userId;
    if (!user.IsAdmin && !string.IsNullOrWhiteSpace(filterUser) && !string.Equals(filterUser, user.UserId, StringComparison.OrdinalIgnoreCase))
    {
        filterUser = user.UserId;
    }
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

app.MapGet("/api/jobs/{jobId}", (string jobId, FilmJobService jobService, IUserContext user) =>
{
    var job = jobService.GetJob(jobId);
    if (job is null)
        return Results.NotFound(new { ok = false, error = "job not found" });
    if (!user.IsAdmin &&
        !string.IsNullOrWhiteSpace(job.UserId) &&
        !string.Equals(job.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new { ok = false, error = "not your job" },
            statusCode: StatusCodes.Status403Forbidden);
    }
    return Results.Ok(new { ok = true, job });
});

/// <summary>
/// Record a user override of the portrait style classifier into the AI-call telemetry stream —
/// the highest-signal feedback there is (a human explicitly overruling a model verdict). The
/// reason distinguishes "classifier was wrong" (a defect to tune) from "my creative choice"
/// (the classifier was right and the user wants mixed media — not a defect).
/// </summary>

/// <summary>Shared body parse for lock-variant / lock-bookref (index + style override fields).</summary>
static async Task<(int Index, bool OverrideStyle, string? Reason, string? Note)> ParseCharacterLockBodyAsync(
    HttpRequest req, int defaultIndex, bool acceptVariantIndexAlias = false)
{
    var index = defaultIndex;
    var overrideStyle = false;
    string? overrideReason = null, overrideNote = null;
    if (req.HasJsonContentType())
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        if (doc.RootElement.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n))
            index = n;
        else if (acceptVariantIndexAlias
                 && doc.RootElement.TryGetProperty("variantIndex", out var vx)
                 && vx.TryGetInt32(out var n2))
            index = n2;
        if (doc.RootElement.TryGetProperty("overrideStyle", out var os) && os.ValueKind is JsonValueKind.True or JsonValueKind.False)
            overrideStyle = os.GetBoolean();
        if (doc.RootElement.TryGetProperty("overrideReason", out var orr) && orr.ValueKind == JsonValueKind.String)
            overrideReason = orr.GetString();
        if (doc.RootElement.TryGetProperty("overrideNote", out var onote) && onote.ValueKind == JsonValueKind.String)
            overrideNote = onote.GetString();
    }
    return (index, overrideStyle, overrideReason, overrideNote);
}

static async Task LogStyleOverrideAsync(
    ProjectTelemetryService telemetry,
    IOptions<PageToMovieOptions> opts,
    string projectId,
    string charKey,
    string? reason,
    string? note)
{
    try
    {
        await telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = "style_gate_override",
            ProjectId = projectId,
            CharKey = charKey,
            // ai_wrong | user_preference | other — the user's stated reason for overriding.
            Mode = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim().ToLowerInvariant(),
            Error = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Fakes = opts.Value.UseFakes,
            Ok = true,
        });
    }
    catch { /* telemetry is best-effort */ }
}

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

app.MapPost("/api/jobs/gen-scene", async (
    StartSceneGenRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
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

app.MapPost("/api/jobs/gen-batch", async (
    StartBatchGenRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
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
/// Batch TTS for re-voice (keys stay on server). Progress + per-line audio handoff over SignalR
/// (<c>Kind = speak-batch</c>, <c>ClientMediaUrl</c> / <c>ClientRelativePath</c>).
/// </summary>
app.MapPost("/api/jobs/speak-batch", async (
    StartSpeakBatchRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        if (string.IsNullOrWhiteSpace(body.CharKey))
            body.CharKey = "Character_Narrator";
        var job = await jobService.StartSpeakBatchAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == "queued"
                ? "Queued speak-batch (waiting for lock/worker)"
                : "Started speak-batch",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>
/// Movie-wide voice substitution: walk every clip, associate each dialogue line with its speaker,
/// synthesize the character's cloned voice per line, and maintain the persisted speech alignment.
/// Tracked job (<c>Kind = voice-substitution</c>); per-line audio handoff over SignalR.
/// </summary>
app.MapPost("/api/jobs/voice-substitution", async (
    StartVoiceSubstitutionRequest body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        if (string.IsNullOrWhiteSpace(body.CharKey))
            body.CharKey = "Character_Narrator";
        var job = await jobService.StartVoiceSubstitutionAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = job.Status == "queued"
                ? "Queued voice substitution (waiting for lock/worker)"
                : "Started voice substitution",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>Read the persisted per-clip speech alignment for a project (empty when never built).</summary>
app.MapGet("/api/projects/{id}/voice-alignment", async (
    string id,
    VoiceAlignmentStore alignmentStore,
    CancellationToken ct) =>
{
    var alignment = await alignmentStore.LoadAsync(id, ct);
    return Results.Ok(new { ok = true, alignment });
});

/// <summary>
/// Persist client-detected speech timestamps (from browser ffmpeg silence detection) onto the saved
/// alignment so a future voice substitution reuses them and skips re-detection. Merges by segment
/// index; character/text/audio paths are preserved.
/// </summary>
app.MapPost("/api/projects/{id}/voice-alignment/timestamps", async (
    string id,
    List<ClipTimestampUpdate> updates,
    VoiceAlignmentStore alignmentStore,
    CancellationToken ct) =>
{
    if (updates is null || updates.Count == 0)
        return Results.BadRequest(new { ok = false, error = "no updates" });

    var alignment = await alignmentStore.LoadAsync(id, ct);
    if (alignment is null)
        return Results.BadRequest(new { ok = false, error = "no alignment to update — run voice substitution first" });

    var applied = 0;
    foreach (var u in updates)
    {
        var clip = alignment.Find(u.Scene, u.Clip);
        if (clip is null) continue;
        VoiceAlignmentStore.ApplyTimestamps(clip, u);
        applied++;
    }

    await alignmentStore.SaveAsync(id, alignment, ct);
    return Results.Ok(new { ok = true, clipsUpdated = applied });
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
/// <summary>Raw models_catalog.json for Blazor WASM bootstrap (public read).</summary>
app.MapGet("/api/models/catalog-json", (IUserContext user) =>
{
    try
    {
        // Single source of truth: the catalog embedded in PageToMovie.Core (real, or the fake vendor
        // catalog in fakes mode). The WASM client hydrates from this so its dropdowns match the server.
        var raw = SupportedModelCatalog.GetEmbeddedCatalogJson();

        if (user.IsAdmin)
            return Results.Text(raw, "application/json");

        // Non-admin: strip labMode models so WASM bootstrap cannot offer them.
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("models", out var modelsEl)
            || modelsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Results.Text(raw, "application/json");

        using var streamOut = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(streamOut, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("models"))
                {
                    writer.WritePropertyName("models");
                    writer.WriteStartArray();
                    foreach (var m in modelsEl.EnumerateArray())
                    {
                        if (m.ValueKind == System.Text.Json.JsonValueKind.Object
                            && m.TryGetProperty("labMode", out var lab)
                            && lab.ValueKind == System.Text.Json.JsonValueKind.True)
                            continue;
                        m.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Results.Text(System.Text.Encoding.UTF8.GetString(streamOut.ToArray()), "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/models", (string? capability, IUserContext user) =>
{
    // Lab models are admin-only — never offer incomplete/experimental rows to regular users.
    var includeLab = user.IsAdmin;
    IReadOnlyList<SupportedModelDto> list;
    if (!string.IsNullOrWhiteSpace(capability) &&
        Enum.TryParse<ModelCapability>(capability, ignoreCase: true, out var cap))
    {
        list = SupportedModelCatalog.ForCapability(cap, includeLabModels: includeLab)
            .Select(SupportedModelCatalog.ToDto)
            .ToList();
    }
    else
    {
        list = SupportedModelCatalog.ToDtoList(enabledOnly: true, includeLabModels: includeLab);
    }

    return Results.Ok(new
    {
        ok = true,
        models = list,
        includeLabModels = includeLab,
        note =
            "User picks model ids only. Provider, API base, endpoint, and required env keys come from this catalog. " +
            "Lab-mode models are visible to admins only.",
    });
});

// ---- Configuration (pipeline_config.json) ----
app.MapGet("/api/projects/{id}/config", async (string id, ProjectStore store, CancellationToken ct) =>
{
    try
    {
        var cfg = await store.GetConfigAsync(id, ct);
        var projectDir = store.GetProjectDir(id);
        return Results.Ok(new { ok = true, projectId = id, projectDir, config = cfg });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPut("/api/projects/{id}/config", async (
    string id,
    HttpRequest req,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        if (!user.IsAdmin)
        {
            string[] modelKeys =
            [
                "video_model_name", "image_model_name", "planning_model_name", "vision_model_name",
                "video_review_model_name", "audio_model_name", "voice_model_name", "tts_model_name"
            ];
            foreach (var key in modelKeys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                    continue;
                var mid = el.GetString();
                if (SupportedModelCatalog.IsLabModel(mid))
                {
                    return Results.BadRequest(new
                    {
                        ok = false,
                        error = $"Model '{mid}' is lab-mode (admin-only). Choose a production catalog model.",
                    });
                }
            }
        }
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

// immutable=true is only correct for files that can never change at the same URL (e.g. book
// page images extracted once at import). Character ref/variant/book-ref images are overwritten
// in place at the same path on regeneration — "public, max-age=31536000, immutable" would tell
// the browser to never even ask the server again, silently showing a stale portrait for up to a
// year after regeneration. Those use "no-cache" instead: still ETag/304-validated (saves the body
// transfer when unchanged), but always revalidated so a regeneration is picked up immediately.
static IResult ServeCachedFile(
    HttpContext ctx, string path, string? contentType = null, bool enableRangeProcessing = false, bool immutable = false)
{
    try
    {
        if (!File.Exists(path))
            return Results.NotFound(new { ok = false, error = "File not found" });
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var etag = $"\"{lastWrite.Ticks:x}\"";
        if (ctx.Request.Headers.IfNoneMatch == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = immutable
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        return Results.File(path, contentType ?? GuessImageContentType(path), enableRangeProcessing: enableRangeProcessing);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}

app.MapGet("/api/projects/{projectId}/characters/{charKey}/ref", (HttpContext ctx, string projectId, string charKey, ProjectStore store) =>
{
    var path = store.ResolveCharacterRefPath(projectId, charKey);
    return path is null ? Results.NotFound(new { ok = false, error = "ref image not found" }) : ServeCachedFile(ctx, path);
});

app.MapGet("/api/projects/{projectId}/characters/{charKey}/variants/{index:int}",
    (HttpContext ctx, string projectId, string charKey, int index, ProjectStore store) =>
{
    var path = store.ResolveCharacterVariantPath(projectId, charKey, index);
    return path is null ? Results.NotFound(new { ok = false, error = "variant not found" }) : ServeCachedFile(ctx, path);
});

app.MapGet("/api/projects/{projectId}/characters/{charKey}/bookrefs/{index:int}",
    (HttpContext ctx, string projectId, string charKey, int index, ProjectStore store) =>
{
    var path = store.ResolveCharacterBookRefPath(projectId, charKey, index);
    return path is null ? Results.NotFound(new { ok = false, error = "book ref not found" }) : ServeCachedFile(ctx, path);
});

app.MapGet("/api/projects/{projectId}/book-images/{fileName}",
    (HttpContext ctx, string projectId, string fileName, ProjectStore store) =>
{
    var dir = Path.Combine(store.GetProjectDir(projectId), "source", "book_images");
    var file = Path.GetFileName(fileName);
    var path = Path.Combine(dir, file);
    return ServeCachedFile(ctx, path, immutable: true);
});

app.MapGet("/api/projects/{projectId}/characters/{charKey}/book-candidates",
    async (string projectId, string charKey, CharacterBookPlateService service, CancellationToken ct) =>
{
    try
    {
        var candidates = await service.GetRankedBookCandidatesAsync(projectId, charKey, ct);
        return Results.Ok(new { ok = true, candidates });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{projectId}/characters/{charKey}/set-book-refs",
    (string projectId, string charKey, SetBookRefsRequest body, ProjectStore store) =>

{
    try
    {
        var paths = body.ImagePaths ?? new List<string>();
        store.SetCharacterBookRefs(projectId, charKey, paths);
        return Results.Ok(new { ok = true, message = $"Saved {paths.Count} book reference picture(s) for {charKey}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});


/// <summary>
/// Prompt-based edit of an already-generated clip (xAI /v1/videos/edits) — human-triggered,
/// per-clip, spends real provider money. Job-queue family (like character-variants), NOT the
/// synchronous "media" endpoint family lip-sync uses: edit processing time is not guaranteed short
/// just because the input clip is short, so this must never block the HTTP request — the client
/// polls/subscribes the returned job the same way it already does for scene generation.
/// </summary>
app.MapPost("/api/jobs/video-edit", async (StartVideoEditRequest body, FilmJobService jobService) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.ProjectId) || body.Scene <= 0 || body.Clip <= 0)
            return Results.BadRequest(new { ok = false, error = "projectId, scene, and clip required" });
        if (string.IsNullOrWhiteSpace(body.Prompt))
            return Results.BadRequest(new { ok = false, error = "prompt required" });
        var job = await jobService.StartVideoEditAsync(body);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued AI edit for S{body.Scene:D2}C{body.Clip:D2}",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
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
/// Upload or replace voice-clone template audio (mic recording or file).
/// Multipart field: file. Stored under assets/characters/{key}/voice_clone_sample.*.
/// Used as a reference for future TTS clone providers; does not replace voice_profile text.
/// </summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/voice/clone-sample", async (
    string id,
    string charKey,
    HttpRequest req,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        if (!req.HasFormContentType)
            return Results.BadRequest(new { ok = false, error = "multipart form required (field: file)" });

        var form = await req.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No audio file (field: file)" });
        if (file.Length > 15 * 1024 * 1024)
            return Results.BadRequest(new { ok = false, error = "Audio too large (max 15 MB)." });

        await using var stream = file.OpenReadStream();
        var path = await store.SaveVoiceCloneSampleAsync(id, charKey, stream, file.FileName, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            fileName = Path.GetFileName(path),
            url = $"/api/projects/{Uri.EscapeDataString(id)}/characters/{Uri.EscapeDataString(charKey)}/voice/clone-sample",
            message = "Voice clone sample saved — optional add-on template for personal voice.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/characters/{charKey}/voice/clone-sample",
    async (string id, string charKey, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        var path = store.GetVoiceCloneSamplePath(id, charKey);
        if (!File.Exists(path))
            return Results.NotFound(new { ok = false, error = "No voice clone sample yet." });
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            _ => "audio/webm",
        };
        return Results.File(path, contentType, enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapDelete("/api/projects/{id}/characters/{charKey}/voice/clone-sample",
    async (string id, string charKey, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        var removed = store.DeleteVoiceCloneSample(id, charKey);
        return Results.Ok(new { ok = true, removed, projectId = id, charKey });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Clone a voice from this character's saved voice-clone sample (reuses the same per-character
/// storage as the /voice/clone-sample upload above — a narration flow can point charKey at a
/// caller-chosen pseudo-character like "Narrator" rather than an on-screen cast member). Explicit,
/// human-triggered only — spends real provider money ($1.50/clone as of 2026-08, see
/// models_catalog.json) and is never called automatically from any job/pipeline. The returned
/// provider voice id is cached on the character seed so repeat narration calls reuse it instead of
/// re-cloning (and re-paying) every time.
/// </summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/voice/clone", async (
    string id,
    string charKey,
    CloneVoiceApiRequest? body,
    VoiceCloneApplyService apply,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        // Unified router: catalog voice_model_name (or body.Model) → Fal MiniMax or ElevenLabs.
        var result = await apply.ApplyFromSampleAsync(
            id, charKey, modelOverride: body?.Model, ct: ct);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            voiceId = result.ProviderVoiceId,
            providerId = result.ProviderId,
            modelId = result.ModelId,
            usedMock = result.UsedMock,
            estimatedUsd = result.EstimatedCloneUsd,
            previewUrl = result.PreviewUrl,
            message = result.Message ?? "Voice cloned — reused for narration until the sample is replaced.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/characters/{charKey}/voice/speak", async (
    string id,
    string charKey,
    SpeakVoiceApiRequest? body,
    ProjectStore store,
    IVoiceCloneClient voiceClone,
    IVoiceClient voiceClient,
    IHttpClientFactory httpFactory,
    MediaProxyTicketStore tickets,
    ProjectTelemetryService telemetry,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        var text = body?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Results.BadRequest(new { ok = false, error = "text required" });

        var voiceId = body?.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
            voiceId = store.GetVoiceCloneProviderId(id, charKey);
        if (string.IsNullOrWhiteSpace(voiceId))
            return Results.BadRequest(new { ok = false, error = "No cloned voice yet — record and apply a voice sample first." });

        // Prefer seed provider (who created the clone) so we don't TTS with the wrong stack.
        var seedProvider = store.GetVoiceProviderId(id, charKey) ?? "";
        var model = body?.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            var cfg = await store.GetConfigAsync(id, ct);
            if (cfg.TryGetValue("voice_model_name", out var vm) && vm.ValueKind == JsonValueKind.String)
                model = vm.GetString();
        }

        // Resolve speak-shaped catalog entry (not the clone step).
        SupportedModelEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(model))
            entry = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                    ?? SupportedModelCatalog.Find(model);
        if (entry is { IsVoiceCloneStep: true })
        {
            // User selected the clone model — pair to same-provider speak model.
            entry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Enabled &&
                    string.Equals(m.ProviderId, entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            model = entry?.Id;
        }
        if (entry is null)
        {
            // Infer from seed provider id.
            entry = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Enabled &&
                    (string.IsNullOrWhiteSpace(seedProvider) ||
                     string.Equals(m.ProviderId, seedProvider, StringComparison.OrdinalIgnoreCase)));
            model = entry?.Id ?? model;
        }

        var maxLen = entry?.MaxPromptLength ?? 5000;
        if (text.Length > maxLen)
            return Results.BadRequest(new { ok = false, error = $"Text is {text.Length} characters — this voice model's limit is {maxLen} per call. Split into multiple calls." });

        var providerId = entry?.ProviderId
                         ?? (string.IsNullOrWhiteSpace(seedProvider) ? null : seedProvider)
                         ?? "unknown";
        var useEleven = providerId.Equals("elevenlabs", StringComparison.OrdinalIgnoreCase)
                        || (entry?.Provider == ModelProviderFamily.ElevenLabs)
                        || voiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase);

        byte[]? audioBytes = null;
        string contentType = "audio/mpeg";
        string fileExt = ".mp3";
        string? clientUrl = null;
        string? error = null;
        var usedMock = false;

        if (useEleven)
        {
            if (!voiceClient.IsConfigured && !voiceId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { ok = false, error = "ElevenLabs key is not configured. Open Settings → Voice." });
            var speakModelId = entry?.Id
                               ?? SupportedModelCatalog.Find("eleven_multilingual_v2", ModelCapability.Voice)?.Id
                               ?? model
                               ?? "eleven_multilingual_v2";
            var tts = await voiceClient.TextToSpeechAsync(voiceId!, text, speakModelId, ct);
            if (!tts.Ok || tts.AudioBytes is not { Length: > 0 })
            {
                error = tts.Error ?? "Speech synthesis failed";
            }
            else
            {
                audioBytes = tts.AudioBytes;
                contentType = tts.ContentType ?? "audio/mpeg";
                fileExt = tts.FileExtension ?? ".mp3";
                usedMock = tts.UsedMock;
            }
        }
        else
        {
            if (!voiceClone.IsConfigured)
                return Results.BadRequest(new { ok = false, error = "Connect a voice service (Fal) in Settings for MiniMax speech." });
            var speakModelId = entry?.Id
                               ?? SupportedModelCatalog.Find("fal-ai/minimax/speech-02-hd", ModelCapability.Voice)?.Id
                               ?? model;
            var audioUrl = await voiceClone.SynthesizeSpeechAsync(text, voiceId!, speakModelId, ct);
            if (string.IsNullOrWhiteSpace(audioUrl))
            {
                error = "Speech synthesis failed — see server logs.";
            }
            else
            {
                try
                {
                    var http = httpFactory.CreateClient();
                    using var resp = await http.GetAsync(audioUrl, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        audioBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        contentType = resp.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
                    }
                    else
                    {
                        // Fall back to proxy URL if download fails
                        var ticket = tickets.Issue(audioUrl, TimeSpan.FromMinutes(45));
                        clientUrl = $"/api/media/proxy/{ticket}";
                    }
                }
                catch
                {
                    var ticket = tickets.Issue(audioUrl, TimeSpan.FromMinutes(45));
                    clientUrl = $"/api/media/proxy/{ticket}";
                }
            }
        }

        var estimatedUsd = entry?.CostPerThousandCharsUsd is { } rate
            ? Math.Round(rate * text.Length / 1000.0, 4)
            : (double?)null;
        var ok = audioBytes is { Length: > 0 } || !string.IsNullOrWhiteSpace(clientUrl);
        await telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            ProjectId = id,
            Kind = "tts",
            Mode = "dialogue_tts",
            Model = entry?.Id ?? model,
            Provider = providerId,
            CharKey = charKey,
            PromptChars = text.Length,
            EstimatedUsd = estimatedUsd,
            Ok = ok,
            Error = ok ? null : error ?? "Speech synthesis failed",
        }, ct);

        if (!ok)
            return Results.BadRequest(new { ok = false, error = error ?? "Speech synthesis failed" });

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            voiceId,
            clientUrl,
            audioBase64 = audioBytes is { Length: > 0 } ? Convert.ToBase64String(audioBytes) : null,
            contentType,
            fileExtension = fileExt,
            characterCount = text.Length,
            estimatedUsd,
            usedMock,
            message = "Narration audio ready.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Video lip-sync: resync a video clip's mouth movement to a separate dialogue/narration audio
/// track (multipart upload: fields "video" and "audio", both required; optional "model" and
/// "syncMode" fields). Explicit, human-triggered per-clip action — spends real provider money
/// (~$5/min of output video as of 2026-08, see models_catalog.json) and is never called
/// automatically from any job/pipeline. Returns a media-proxy URL, not the raw provider URL.
/// </summary>
app.MapPost("/api/projects/{id}/media/lip-sync", async (
    string id,
    HttpRequest req,
    ProjectStore store,
    ILipSyncClient lipSync,
    MediaProxyTicketStore tickets,
    ProjectTelemetryService telemetry,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (await AuthGate.RequireProjectOwnerAsync(id, user, store, opts, ct) is { } denied)
        return denied;
    if (!req.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form required (fields: video, audio)" });

    string? videoTemp = null;
    string? audioTemp = null;
    try
    {
        await store.RequireProjectAsync(id, ct);
        if (!lipSync.IsConfigured)
            return Results.BadRequest(new { ok = false, error = "Connect a lip-sync service (FAL_API_KEY) in Configuration." });

        var form = await req.ReadFormAsync(ct);
        var videoFile = form.Files.GetFile("video");
        var audioFile = form.Files.GetFile("audio");
        if (videoFile is null || videoFile.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No video file (field: video)" });
        if (audioFile is null || audioFile.Length == 0)
            return Results.BadRequest(new { ok = false, error = "No audio file (field: audio)" });

        var model = form["model"].FirstOrDefault();
        var syncMode = form["syncMode"].FirstOrDefault();
        var entry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.LipSync);

        videoTemp = Path.Combine(Path.GetTempPath(), $"lipsync_video_{Guid.NewGuid():N}{Path.GetExtension(videoFile.FileName)}");
        audioTemp = Path.Combine(Path.GetTempPath(), $"lipsync_audio_{Guid.NewGuid():N}{Path.GetExtension(audioFile.FileName)}");
        await using (var vfs = File.Create(videoTemp))
            await videoFile.CopyToAsync(vfs, ct);
        await using (var afs = File.Create(audioTemp))
            await audioFile.CopyToAsync(afs, ct);

        var resultUrl = await lipSync.GenerateLipSyncAsync(
            videoTemp, audioTemp, model,
            string.IsNullOrWhiteSpace(syncMode) ? "cut_off" : syncMode!,
            onProgress: null, ct);
        await telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            ProjectId = id,
            Kind = "lip_sync",
            Model = entry.Id,
            Provider = entry.ProviderId,
            Ok = !string.IsNullOrWhiteSpace(resultUrl),
            Error = string.IsNullOrWhiteSpace(resultUrl) ? "Lip-sync failed" : null,
        }, ct);
        if (string.IsNullOrWhiteSpace(resultUrl))
            return Results.BadRequest(new { ok = false, error = "Lip-sync failed — see server logs." });

        var ticket = tickets.Issue(resultUrl, TimeSpan.FromMinutes(45));
        var clientUrl = $"/api/media/proxy/{ticket}";

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            clientUrl,
            model = entry.Id,
            costPerMinuteUsd = entry.CostPerMinuteUsd,
            message = "Lip-synced clip ready.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
    finally
    {
        foreach (var tmp in new[] { videoTemp, audioTemp })
        {
            if (tmp is null) continue;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
});


/// <summary>List provider voices (ElevenLabs premade + clones, or mock catalog).</summary>
app.MapGet("/api/voices", async (IVoiceClient voices, CancellationToken ct) =>
{
    try
    {
        var list = await voices.ListVoicesAsync(ct);
        return Results.Ok(new
        {
            ok = true,
            provider = voices.ProviderId,
            configured = voices.IsConfigured,
            voices = list,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Create/apply a voice clone for a character from the saved sample (or generate a demo sample),
/// store provider voice_id on the seed, and synthesize a short TTS preview.
/// Complements POST .../voice/clone (Fal MiniMax) — this path uses IVoiceClient (ElevenLabs).
/// </summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/voice/apply-clone", async (
    string id,
    string charKey,
    VoiceCloneApplyService apply,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        var result = await apply.ApplyFromSampleAsync(id, charKey, ct: ct);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            providerId = result.ProviderId,
            providerVoiceId = result.ProviderVoiceId,
            voiceId = result.ProviderVoiceId,
            modelId = result.ModelId,
            usedMock = result.UsedMock,
            voiceLabel = result.VoiceLabel,
            previewUrl = result.PreviewUrl,
            previewRelativePath = result.PreviewRelativePath,
            estimatedUsd = result.EstimatedCloneUsd,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Assign a catalog/premade provider voice id to a character (no sample clone).</summary>
app.MapPost("/api/projects/{id}/characters/{charKey}/voice/apply-catalog", async (
    string id,
    string charKey,
    ApplyCatalogVoiceRequest? body,
    VoiceCloneApplyService apply,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        body ??= new ApplyCatalogVoiceRequest();
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        if (string.IsNullOrWhiteSpace(body.ProviderVoiceId))
            return Results.BadRequest(new { ok = false, error = "providerVoiceId required" });
        var result = await apply.ApplyCatalogVoiceAsync(
            id, charKey, body.ProviderVoiceId!, body.DisplayName, body.PreviewText, ct);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            charKey,
            providerId = result.ProviderId,
            providerVoiceId = result.ProviderVoiceId,
            voiceId = result.ProviderVoiceId,
            usedMock = result.UsedMock,
            voiceLabel = result.VoiceLabel,
            previewUrl = result.PreviewUrl,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Serve the last TTS preview for a character (from apply-clone / apply-catalog).</summary>
app.MapGet("/api/projects/{id}/characters/{charKey}/voice/tts-preview",
    (string id, string charKey, VoiceCloneApplyService apply) =>
{
    try
    {
        var path = apply.GetTtsPreviewPath(id, charKey);
        if (path is null || !File.Exists(path))
            return Results.NotFound(new { ok = false, error = "No TTS preview yet — run apply-clone first." });
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            _ => "application/octet-stream",
        };
        return Results.File(path, contentType, Path.GetFileName(path));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});


app.MapGet("/api/users/{id}/terms", async (string id, UserDatabaseService userDb) =>
{
    var hasAccepted = await userDb.HasAcceptedTermsAsync(id);
    return Results.Ok(new { hasAccepted, accepted = hasAccepted });
});

app.MapPost("/api/users/terms/accept", async (AcceptTermsRequest body, UserDatabaseService userDb) =>
{
    var ok = await userDb.AcceptTermsAsync(body.UserId, body.Version ?? "1.0");
    return Results.Ok(new { ok });
});

/// <summary>
/// Manually commit a project's current text/metadata state to its own Git repository
/// (owner or admin only). Not called automatically on every edit — see host/docs/issues for
/// why auto-commit-on-save needs a decision about where user projects live relative to any
/// Git repo the app itself is checked out into before it's safe to wire in as a background hook.
/// </summary>
app.MapPost("/api/projects/{id}/commit", async (
    string id,
    CommitProjectApiRequest? body,
    ProjectStore store,
    ProjectGitRepositoryService git,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can commit it.", ct) is { } forbidden)
            return forbidden;

        var info = await git.CommitProjectStateAsync(
            store.GetProjectDir(id), user.UserId ?? "PageToMovie", body?.Message ?? "Project update");
        return Results.Ok(new { ok = true, commit = info });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/git/history", async (
    string id,
    int? limit,
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var history = await store.GetProjectGitHistoryAsync(id, limit ?? 20);
        return Results.Ok(new { ok = true, history });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/git/undo", async (
    string id,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can undo project changes.", ct) is { } forbidden)
            return forbidden;

        var result = await store.UndoLastProjectChangeAsync(id, user.UserId);
        if (result is null)
        {
            return Results.BadRequest(new { ok = false, error = "No prior commit to undo to." });
        }
        return Results.Ok(new { ok = true, commit = result, message = "Successfully reverted project to previous commit state." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/git/revert/{commitHash}", async (
    string id,
    string commitHash,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can revert project state.", ct) is { } forbidden)
            return forbidden;

        var result = await store.RevertProjectToCommitAsync(id, commitHash, user.UserId);
        if (result is null)
        {
            return Results.BadRequest(new { ok = false, error = $"Failed to revert to commit {commitHash}." });
        }
        return Results.Ok(new { ok = true, commit = result, message = $"Successfully reverted project to commit {commitHash[..Math.Min(8, commitHash.Length)]}." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/scenes/{scene:int}/history", async (
    string id,
    int scene,
    int? limit,
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var history = await store.GetSceneGitHistoryAsync(id, scene, limit ?? 20);
        return Results.Ok(new { ok = true, history });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/revert/{commitHash}", async (
    string id,
    int scene,
    string commitHash,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can revert scene changes.", ct) is { } forbidden)
            return forbidden;

        var success = await store.RevertSceneToCommitAsync(id, scene, commitHash, user.UserId);
        if (!success)
        {
            return Results.BadRequest(new { ok = false, error = $"Failed to revert Scene {scene} to commit {commitHash[..Math.Min(8, commitHash.Length)]}." });
        }
        return Results.Ok(new { ok = true, message = $"Successfully reverted Scene {scene} to commit {commitHash[..Math.Min(8, commitHash.Length)]}." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/git/status", async (
    string id,
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var status = await store.GetProjectUncommittedStatusAsync(id);
        return Results.Ok(new { ok = true, status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/git/commit", async (
    string id,
    CommitProjectApiRequest? body,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var msg = body?.Message ?? "Manual scene/clip updates";
        var result = await store.CommitProjectChangesAsync(id, msg, user.UserId, forceCommit: body?.ForceCommit ?? false);
        return Results.Ok(new { ok = true, commit = result, message = "Successfully committed project changes." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions", async (
    string id,
    int scene,
    int clip,
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetClipVersionsAsync(id, scene, clip);
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Whether the *active* clip file's bytes currently live on server disk, are only registered as
/// synced to the client, or both — with the registered sha256/size. Lets the client decide
/// whether a local blob it already has is still current before trusting it for playback, instead
/// of assuming "file exists locally" means "file is current" (it may be an older take that was
/// never overwritten locally after a later regen/promote).
/// </summary>
app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/media-status", async (
    string id,
    int scene,
    int clip,
    ProjectStore store,
    MediaSyncLocator locator,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var status = await locator.GetClipStatusAsync(id, store.GetProjectDir(id), scene, clip, ct);
        return Results.Ok(new
        {
            ok = true,
            onServer = status.OnServer,
            onClient = status.OnClient,
            sha256 = status.Sha256,
            clientSizeBytes = status.ClientSizeBytes,
            serverSizeBytes = status.ServerSizeBytes,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/{versionId}/promote", async (
    string id,
    int scene,
    int clip,
    string versionId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
    await RunProjectVersionActionAsync(id, store, user, opts,
        () => store.PromoteClipVersionAsync(id, scene, clip, versionId, user.UserId),
        "Failed to promote clip version.",
        $"Promoted clip version {versionId} to active clip.", ct));

app.MapDelete("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/{versionId}", async (
    string id,
    int scene,
    int clip,
    string versionId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
    await RunProjectVersionActionAsync(id, store, user, opts,
        () => store.SoftDeleteClipVersionAsync(id, scene, clip, versionId),
        "Failed to delete clip version.",
        $"Soft-deleted clip version {versionId}.", ct));

app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/trash", async (
    string id,
    int scene,
    int clip,
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetTrashClipVersionsAsync(id, scene, clip);
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/{versionId}/restore", async (
    string id,
    int scene,
    int clip,
    string versionId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
    await RunProjectVersionActionAsync(id, store, user, opts,
        () => store.RestoreSoftDeletedClipVersionAsync(id, scene, clip, versionId),
        "Failed to restore clip version from trash.",
        $"Restored clip version {versionId} from trash.", ct));

app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/versions/trash/empty", async (
    string id,
    int scene,
    int clip,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var count = await store.EmptyClipTrashAsync(id, scene, clip);
        return Results.Ok(new { ok = true, purgedCount = count, message = $"Permanently purged {count} take(s)." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// Scene audio takes — mirrors the clip-versions endpoints above (GetMusicVersionsAsync etc.),
// keyed by scene + takeId instead of scene/clip + versionId since one take is a group of segments.
app.MapGet("/api/projects/{id}/scenes/{scene:int}/music-versions", async (
    string id,
    int scene,
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetMusicVersionsAsync(id, scene);
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/music-versions/{takeId}/promote", async (
    string id,
    int scene,
    string takeId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
    await RunProjectVersionActionAsync(id, store, user, opts,
        () => store.PromoteMusicVersionAsync(id, scene, takeId),
        "Failed to promote audio take.",
        $"Promoted audio take {takeId} to active.", ct));

app.MapDelete("/api/projects/{id}/scenes/{scene:int}/music-versions/{takeId}", async (
    string id,
    int scene,
    string takeId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
    await RunProjectVersionActionAsync(id, store, user, opts,
        () => store.SoftDeleteMusicVersionAsync(id, scene, takeId),
        "Failed to delete audio take.",
        $"Soft-deleted audio take {takeId}.", ct));

app.MapGet("/api/projects/{id}/scenes/{scene:int}/music-versions/trash", async (
    string id,
    int scene,
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        await store.RequireProjectAsync(id, ct);
        var versions = await store.GetTrashMusicVersionsAsync(id, scene);
        return Results.Ok(new { ok = true, versions });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/music-versions/{takeId}/restore", async (
    string id,
    int scene,
    string takeId,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
    await RunProjectVersionActionAsync(id, store, user, opts,
        () => store.RestoreSoftDeletedMusicVersionAsync(id, scene, takeId),
        "Failed to restore audio take from trash.",
        $"Restored audio take {takeId} from trash.", ct));

/// <summary>
/// Push the project's text package (video excluded) to the configured Projects remote.
/// Owner/admin only. Optional body.commitFirst + message creates a local commit first.
/// Returns historyUrl when the remote is GitHub. See host/docs/github-projects-backup-checklist.md.
/// </summary>
app.MapPost("/api/projects/{id}/push", async (
    string id,
    PushProjectApiRequest? body,
    ProjectStore store,
    ProjectGitRepositoryService git,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var proj = await store.RequireProjectAsync(id, ct);
        if (!await store.CanUserPublishDemoAsync(id, user.UserId, user.IsAdmin, ct))
        {
            return Results.Json(new { ok = false, error = "Only the project owner or an admin can push it." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var dir = store.GetProjectDir(id);
        GitCommitInfo? commit = null;
        if (body?.CommitFirst == true)
        {
            commit = await git.CommitProjectStateAsync(
                dir, user.UserId ?? "PageToMovie", body?.Message ?? "Project update");
        }

        var push = await git.PushProjectAsync(dir, proj.Id);
        if (!push.Success)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = push.Message,
                branch = push.Branch,
                commitHash = push.CommitHash ?? commit?.CommitHash,
                historyUrl = push.HistoryUrl,
                commit,
            });
        }

        return Results.Ok(new
        {
            ok = true,
            branch = push.Branch,
            commitHash = push.CommitHash,
            historyUrl = push.HistoryUrl,
            message = push.Message,
            commit,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Merge another project's committed state into this one (owner or admin of the target project).
/// Real LibGit2Sharp 3-way merge — reports <c>hasConflicts</c> rather than auto-resolving; the
/// caller must inspect and commit manually when that happens (no conflict-resolution UI yet).
/// </summary>
app.MapPost("/api/projects/{id}/sync-origin", async (
    string id,
    SyncOriginApiRequest body,
    ProjectStore store,
    ProjectGitRepositoryService git,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (string.IsNullOrWhiteSpace(body?.ParentProjectId))
        return Results.BadRequest(new { ok = false, error = "parentProjectId required" });
    try
    {
        await store.RequireProjectAsync(id, ct);
        await store.RequireProjectAsync(body.ParentProjectId, ct);
        if (!await store.CanUserPublishDemoAsync(id, user.UserId, user.IsAdmin, ct))
        {
            return Results.Json(new { ok = false, error = "Only the project owner or an admin can sync it." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        PageToMovie.Engine.GitMergeResult res;
        if (!string.IsNullOrWhiteSpace(body.AutoResolveStrategy)
            && Enum.TryParse<PageToMovie.Engine.Collaboration.AutoTextMerger.Strategy>(
                body.AutoResolveStrategy, ignoreCase: true, out var strategy))
        {
            res = await git.SyncForkFromOriginWithAutoResolveAsync(
                store.GetProjectDir(id), store.GetProjectDir(body.ParentProjectId), strategy);
        }
        else
        {
            res = await git.SyncForkFromOriginAsync(
                store.GetProjectDir(id), store.GetProjectDir(body.ParentProjectId));
        }
        return Results.Ok(new
        {
            ok = res.Success,
            hasConflicts = res.HasConflicts,
            commitHash = res.CommitHash,
            message = res.Message,
            autoResolvedCount = res.AutoResolvedCount,
            remainingConflictPaths = res.RemainingConflictPaths,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Computes structured visual diffs between a project and its origin parent project.
/// </summary>
app.MapGet("/api/projects/{id}/contribution-diff", async (
    string id,
    string? originProjectId,
    ProjectStore store,
    ProjectContributionService contribService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    var parentId = originProjectId;
    if (string.IsNullOrWhiteSpace(parentId))
    {
        var proj = await store.GetProjectAsync(id, ct);
        parentId = proj?.ParentProjectId;
    }

    if (string.IsNullOrWhiteSpace(parentId))
        return Results.BadRequest(new { ok = false, error = "originProjectId or parent project required for diff" });

    try
    {
        var targetDir = store.GetProjectDir(id);
        var originDir = store.GetProjectDir(parentId);
        var diff = await contribService.ComputeDiffAsync(id, parentId, targetDir, originDir, ct);
        return Results.Ok(diff);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/contribution-sync-media", async (
    string id,
    SyncOriginApiRequest req,
    ProjectStore store,
    ProjectContributionService contribService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    IHttpClientFactory httpFactory,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    var parentId = req?.ParentProjectId;
    if (string.IsNullOrWhiteSpace(parentId))
    {
        var proj = await store.GetProjectAsync(id, ct);
        parentId = proj?.ParentProjectId;
    }

    if (string.IsNullOrWhiteSpace(parentId))
        return Results.BadRequest(new { ok = false, error = "parentProjectId required for media sync" });

    try
    {
        var targetDir = store.GetProjectDir(id);
        var originDir = store.GetProjectDir(parentId);
        var result = await contribService.SyncContributionMediaAsync(
            targetDir, originDir, httpFactory.CreateClient("media-proxy"), ct);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/visibility", async (
    string id,
    ProjectVisibilityRequest req,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can change visibility mode.", ct) is { } forbidden)
        return forbidden;

    var proj = await store.SetProjectVisibilityModeAsync(id, req.VisibilityMode, ct);
    await books.SetProjectVisibilityAsync(proj.OwnerUserId ?? user.UserId, id, proj.VisibilityMode, ct);
    return Results.Ok(new { ok = true, projectId = proj.Id, visibilityMode = proj.VisibilityMode });
});


/// <summary>Persist product path: full | simple-voice (library book + narrator voice).</summary>
app.MapPost("/api/projects/{id}/studio-path", async (
    string id,
    SetStudioPathRequest? body,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can change studio path.", ct) is { } forbidden)
        return forbidden;

    var proj = await store.SetProjectStudioPathAsync(id, body?.StudioPath, ct);
    return Results.Ok(new { ok = true, projectId = proj.Id, studioPath = proj.StudioPath });
});

app.MapPost("/api/projects/{id}/rename", async (
    string id,
    RenameProjectRequest? body,
    ProjectStore store,
    ProjectArchiveService archives,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can rename this project.", ct) is { } forbidden)
        return forbidden;

    try
    {
        var title = body?.Title ?? body?.Name ?? "";
        // Re-slug rename: export → import under the new id → delete old (folder + display name both
        // change). Degrades to a display-name-only change when the slug is unchanged.
        var result = await archives.RenameViaReimportAsync(id, title, force: false, ct: ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = result.NewId,
            previousProjectId = result.OldId,
            reSlugged = result.ReSlugged,
            title = result.Project?.Title ?? title,
            label = result.Project?.Label ?? title,
            message = result.Message,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/creators/{handle}", async (
    string handle,
    CreatorProfileService creatorService,
    CancellationToken ct) =>
{
    var profile = await creatorService.GetProfileAsync(handle, ct);
    if (profile == null)
        return Results.NotFound(new { ok = false, error = "Creator profile not found." });
    return Results.Ok(profile);
});

// Phase 6: Privacy Search & Invite Delivery — handles only (never emails)
app.MapGet("/api/users/search", async (
    string? q,
    UserDatabaseService userDb,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (string.IsNullOrWhiteSpace(q) || q.Trim().TrimStart('@').Length < 1)
        return Results.Ok(new { ok = true, handles = Array.Empty<string>() });

    var found = await userDb.SearchUsernamesAsync(q, take: 15, ct);
    var handles = found.Select(u => u.StartsWith('@') ? u : "@" + u).ToList();
    return Results.Ok(new { ok = true, handles });
});

/// <summary>
/// Create a real, persisted, single-use invite (48h) for a project and email the recipient a
/// /join link. Owner or admin only. Never reveals whether a target email has an account.
/// </summary>
app.MapPost("/api/projects/{id}/invites", async (
    string id,
    SendInviteApiRequest? body,
    ProjectStore store,
    ProjectInviteService invites,
    UserDatabaseService userDb,
    IEmailSender email,
    IAdminAuthService auth,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can invite collaborators.", ct) is { } forbidden)
            return forbidden;

        var targetHandle = string.IsNullOrWhiteSpace(body?.TargetHandle) ? null : body!.TargetHandle!.TrimStart('@').Trim();
        var targetEmail = string.IsNullOrWhiteSpace(body?.TargetEmail) ? null : body!.TargetEmail!.Trim();
        if (targetHandle is null && targetEmail is null)
            return Results.BadRequest(new { ok = false, error = "A handle or email is required." });

        // Resolve a handle to its email so we can actually deliver the invite — the client
        // never sees this; the /api/users/search endpoint already keeps raw emails server-side.
        if (targetHandle is not null && targetEmail is null)
        {
            var target = await userDb.GetUserByUsernameAsync(targetHandle);
            targetEmail = target?.Email;
        }

        var invite = await invites.CreateAsync(id, user.UserId ?? "unknown", targetHandle, targetEmail, ct);
        var link = auth is AdminAuthService concrete
            ? concrete.BuildAppLink($"/join?token={Uri.EscapeDataString(invite.Token)}")
            : $"/join?token={Uri.EscapeDataString(invite.Token)}";

        if (!string.IsNullOrWhiteSpace(targetEmail))
        {
            var subject = "You're invited to fork a PageToMovie project";
            var text = $"{user.UserId} invited you to fork \"{id}\" on PageToMovie.\n\n{link}\n\nThis link expires in 48 hours.";
            var html = $"<p><strong>{System.Net.WebUtility.HtmlEncode(user.UserId)}</strong> invited you to fork " +
                       $"\"{System.Net.WebUtility.HtmlEncode(id)}\" on PageToMovie.</p>" +
                       $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">Accept invite</a></p>" +
                       "<p>This link expires in 48 hours.</p>";
            await email.SendAsync(targetEmail, subject, html, text, ct);
        }

        return Results.Ok(new
        {
            ok = true,
            // Returned for the inviter's own "copy link" convenience — the recipient's copy
            // comes via email above, not by exposing whether their account/email exists.
            inviteUrl = link,
            delivered = !string.IsNullOrWhiteSpace(targetEmail),
            expiresAt = invite.ExpiresAt,
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Accept an invite (must be signed in): forks the project under the accepting user.</summary>
app.MapPost("/api/invites/accept", async (
    AcceptInviteApiRequest? body,
    ProjectInviteService invites,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var token = (body?.Token ?? "").Trim();
    if (token.Length < 10)
        return Results.BadRequest(new { ok = false, error = "Invalid or missing invite token." });

    var outcome = await invites.ConsumeAsync(token, user.UserId ?? "", ct);
    if (!outcome.Ok || outcome.ProjectId is null)
        return Results.BadRequest(new { ok = false, error = outcome.Error ?? "Could not accept this invite." });

    try
    {
        var fork = await store.ForkProjectAsync(outcome.ProjectId, user.UserId!, isInvite: true, ct);
        await books.LinkForkAsync(outcome.ProjectId, user.UserId!, fork.Id, invitationAuthorized: true, ct);
        return Results.Ok(new { ok = true, projectId = fork.Id, title = fork.Title });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Public forkable movies (visibility "Open"/"PublicForkable") — the source list for the
/// Easy Start "story in your voice" picker. Any signed-in user can see them to fork.</summary>
app.MapGet("/api/projects/forkable", async (
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var all = await store.ListProjectsAsync(ct);
    var forkable = all
        .Where(p => (string.Equals(p.VisibilityMode, "Open", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.VisibilityMode, "PublicForkable", StringComparison.OrdinalIgnoreCase))
                    // Exclude forks themselves — only original forkable sources are pickable stories.
                    && string.IsNullOrWhiteSpace(p.ParentProjectId))
        .OrderBy(p => p.Label ?? p.Title ?? p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(p => new
        {
            id = p.Id,
            title = p.Label ?? p.Title ?? p.Id,
            ownerUserId = p.OwnerUserId,
        })
        .ToList();
    return Results.Ok(new { ok = true, projects = forkable });
});

/// <summary>1-click community fork endpoint for Open (Public Forkable) projects.</summary>
app.MapPost("/api/projects/{id}/fork", async (
    string id,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    try
    {
        var fork = await store.ForkProjectAsync(id, user.UserId!, ct: ct);
        await books.LinkForkAsync(id, user.UserId!, fork.Id, invitationAuthorized: false, ct);
        return Results.Ok(new { ok = true, id = fork.Id, title = fork.Title, parentProjectId = fork.ParentProjectId, visibilityMode = fork.VisibilityMode });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Film-pipeline voice sample job: short video (voice style + dialogue) kept as MP4 (no ffmpeg extract).
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
            ContentType = info.ContentType,
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

/// <summary>Serve cached film voice sample (MP4 preferred; legacy MP3 still supported).</summary>
app.MapGet("/api/projects/{id}/characters/{charKey}/voice/audio", (
    string id,
    string charKey,
    VoicePreviewService voices) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(charKey))
            return Results.BadRequest(new { ok = false, error = "charKey required" });
        var path = voices.GetSampleMediaPath(id, charKey);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "No voice sample yet — generate one first." });
        var isMp3 = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        var contentType = isMp3 ? "audio/mpeg" : "video/mp4";
        var fileName = isMp3 ? $"{charKey}_voice.mp3" : $"{charKey}_voice.mp4";
        return Results.File(path, contentType, fileDownloadName: fileName, enableRangeProcessing: true);
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
                model: string.IsNullOrWhiteSpace(body.Model)
                    ? ProjectModelSelection.RequirePlanning(
                        await store.GetConfigAsync(id, ct).ConfigureAwait(false),
                        "Character look scrub")
                    : ProjectModelSelection.RequireExplicit(body.Model, ModelCapability.Chat, "Character look scrub"),
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
    ProjectStore store,
    CancellationToken ct) =>
{
    try
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(50));

        body ??= new ExtractCastRequest();
        var cfg = await store.GetConfigAsync(id, timeoutCts.Token).ConfigureAwait(false);
        var castModel = string.IsNullOrWhiteSpace(body.Model)
            ? ProjectModelSelection.RequirePlanning(cfg, "Cast extract")
            : ProjectModelSelection.RequireExplicit(body.Model, ModelCapability.Chat, "Cast extract");
        var result = await castService.ExtractAsync(
            id,
            model: castModel,
            force: body.Force,
            ct: timeoutCts.Token);
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
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.BadRequest(new { ok = false, error = "Cast extraction timed out (exceeded 50s). Please try again." });
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
    async (string id, string charKey, HttpRequest req, FilmJobService jobService,
           ProjectTelemetryService telemetry, IOptions<PageToMovieOptions> opts) =>
{
    try
    {
        var (index, overrideStyle, overrideReason, overrideNote) =
            await ParseCharacterLockBodyAsync(req, defaultIndex: 1, acceptVariantIndexAlias: true);
        var result = await jobService.RunCharacterDesignActionAsync(id, "lock-variant", charKey, index, allowStyleOverride: overrideStyle);
        if (overrideStyle)
            await LogStyleOverrideAsync(telemetry, opts, id, charKey, overrideReason, overrideNote);
        return Results.Ok(new { ok = true, message = result, projectId = id, charKey, index });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/characters/{charKey}/lock-bookref",
    async (string id, string charKey, HttpRequest req, FilmJobService jobService,
           ProjectTelemetryService telemetry, IOptions<PageToMovieOptions> opts) =>
{
    try
    {
        var (index, overrideStyle, overrideReason, overrideNote) =
            await ParseCharacterLockBodyAsync(req, defaultIndex: 0);
        // variantIndex slot reused as book-ref index for lock-bookref
        var result = await jobService.RunCharacterDesignActionAsync(
            id, "lock-bookref", charKey, variantIndex: index, allowStyleOverride: overrideStyle);
        if (overrideStyle)
            await LogStyleOverrideAsync(telemetry, opts, id, charKey, overrideReason, overrideNote);
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

        var overrideStyle = string.Equals(form["overrideStyle"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        await using var stream = file.OpenReadStream();
        var path = await characters.LockFromUploadAsync(id, charKey, stream, file.FileName, overrideStyle, ct);
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
app.MapGet("/api/projects/{id}/adaptation", (string id, ProjectStore store, IUserContext user) =>
{
    try
    {
        var status = store.GetAdaptationStatus(id, user.UserId);
        return Results.Ok(new { ok = true, projectId = id, adaptation = status });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/jobs/book-prepare", async (
    StartBookPrepareRequest body,
    FilmJobService jobService,
    IUserContext user,
    UserDatabaseService userDb,
    IUserApiKeyProvider keys,
    IOptions<PageToMovieOptions> opts) =>
{
    // PDF extract / plain text needs no AI key. Vision OCR only if requested or auto-selected later.
    if (AuthGate.RequireLogin(user, opts) is { } deniedLogin)
        return deniedLogin;
    if (body.ForceVision &&
        await AuthGate.RequirePersonalGrokKeyAsync(user, userDb, opts, useFakes, keys, requireVisionKey: true) is { } deniedVision)
        return deniedVision;
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
app.MapPost("/api/jobs/book-import", async (
    StartBookImportRequest body,
    FilmJobService jobService,
    IUserContext user,
    UserDatabaseService userDb,
    IUserApiKeyProvider keys,
    IOptions<PageToMovieOptions> opts) =>
{
    if (await AuthGate.RequirePersonalGrokKeyAsync(user, userDb, opts, useFakes, keys) is { } denied)
        return denied;
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

app.MapPost("/api/projects/{id}/adaptation/upload", async (
    string id,
    HttpRequest req,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
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
        BookTextIdentity? bookIdentity = null;
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var text = await File.ReadAllTextAsync(path, req.HttpContext.RequestAborted);
            var project = await store.GetProjectAsync(id, req.HttpContext.RequestAborted);
            bookIdentity = await books.RegisterAsync(
                text, user.UserId, id, project?.VisibilityMode ?? "Private",
                req.HttpContext.RequestAborted);
        }
        var status = store.GetAdaptationStatus(id, user.UserId);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            savedPath = path,
            bookId = bookIdentity?.BookId,
            bookSha256 = bookIdentity?.Sha256,
            message = $"Saved {file.FileName} ({file.Length} bytes)",
            adaptation = status,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/books/{idOrHash}", async (
    string idOrHash,
    BookTextRegistryService books,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var book = await books.ResolveAsync(idOrHash, user.UserId, ct);
    return book is null ? Results.NotFound(new { ok = false, error = "Book text not found." }) : Results.Ok(new
    {
        ok = true,
        bookId = book.BookId,
        sha256 = book.Sha256,
        byteCount = book.ByteCount,
        text = book.Text,
    });
});

app.MapPost("/api/books/{bookId}/projects/{projectId}", async (
    string bookId, string projectId, BookTextRegistryService books,
    IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied) return denied;
    await books.LinkToProjectAsync(bookId, user.UserId, projectId, ct);
    return Results.Ok(new { ok = true, bookId, projectId });
});

app.MapPost("/api/books/{bookId}/artifacts", async (
    string bookId, JsonElement body, BookTextRegistryService books,
    IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied) return denied;
    static string Required(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException($"{name} required");
    var artifact = await books.RegisterArtifactAsync(
        bookId, user.UserId,
        Required(body, "artifactKind"), Required(body, "content"), Required(body, "modelId"),
        Required(body, "promptVersion"), Required(body, "promptSha256"),
        body.TryGetProperty("temperature", out var temp) ? temp.GetDouble() : 0,
        body.TryGetProperty("behaviorVersions", out var behaviors) ? behaviors.GetRawText() : "{}",
        ct);
    return Results.Ok(new
    {
        ok = true,
        artifactId = artifact.ArtifactId,
        derivationSha256 = artifact.DerivationSha256,
        contentSha256 = artifact.ContentSha256,
    });
});

app.MapGet("/api/book-artifacts/{artifactId}", async (
    string artifactId, BookTextRegistryService books,
    IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied) return denied;
    var artifact = await books.ResolveArtifactAsync(artifactId, user.UserId, ct);
    return artifact is null
        ? Results.NotFound(new { ok = false, error = "Derived book artifact not found." })
        : Results.Ok(new { ok = true, artifact });
});

/// <summary>
/// Import a Fountain file as the editable screenplay draft (does not approve / Stage 1 yet).
/// User reviews on Screenplay, then sign-off materialises Stage 1.
/// </summary>
app.MapPost("/api/projects/{id}/adaptation/import-fountain", async (string id, HttpRequest req, ProjectStore store, IUserContext user) =>
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

        var status = store.GetAdaptationStatus(id, user.UserId);
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
app.MapGet("/api/projects/{id}/screenplay", (string id, ProjectStore store, IUserContext user) =>
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
            adaptation = store.GetAdaptationStatus(id, user.UserId),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Save Fountain draft (no Stage 1 write).</summary>
app.MapPut("/api/projects/{id}/screenplay", async (string id, HttpRequest req, ProjectStore store, IUserContext user) =>
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
            adaptation = store.GetAdaptationStatus(id, user.UserId),
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
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
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
            adaptation = store.GetAdaptationStatus(id, user.UserId),
            cast,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Get Stage‑1 visual medium preference (auto | photoreal | picture book | …).</summary>
app.MapGet("/api/projects/{id}/visual-medium", async (
    string id,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var dir = store.GetProjectDir(id);
        var medium = ProjectVisionMeta.GetAdaptationMediumPreference(dir);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            visualMedium = medium,
            options = new[]
            {
                new { id = ProjectVisionMeta.MediumAuto, label = "Auto (infer from book)" },
                new { id = ProjectVisionMeta.MediumPhotoreal, label = "Photoreal / live action" },
                new { id = ProjectVisionMeta.MediumIllustrated, label = "Picture book / illustrated" },
                new { id = ProjectVisionMeta.MediumStylized3d, label = "Stylized 3D animation" },
                new { id = ProjectVisionMeta.MediumOther, label = "Other / stylized" },
            },
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Set Stage‑1 visual medium preference before (or after) import.</summary>
app.MapPut("/api/projects/{id}/visual-medium", async (
    string id,
    HttpRequest req,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        string? medium = null;
        if (root.TryGetProperty("visualMedium", out var vm) && vm.ValueKind == JsonValueKind.String)
            medium = vm.GetString();
        else if (root.TryGetProperty("visual_medium", out var vm2) && vm2.ValueKind == JsonValueKind.String)
            medium = vm2.GetString();
        if (string.IsNullOrWhiteSpace(medium))
            return Results.BadRequest(new { ok = false, error = "visualMedium required" });

        var written = ProjectVisionMeta.SetAdaptationMediumPreference(store.GetProjectDir(id), medium);
        store.TriggerAutoGitCommit(id, $"ptm:stage=visual_medium_preference medium={written.VisualMedium}");
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            visualMedium = written.VisualMedium,
            message = string.Equals(written.VisualMedium, ProjectVisionMeta.MediumAuto, StringComparison.Ordinal)
                ? "Medium set to Auto — Stage‑1 will infer from the book."
                : $"Medium locked to {written.VisualMedium} for Stage‑1.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Re-skin the current Fountain draft to a visual medium (descriptive layer only).
/// Lightweight fountain → fountain regeneration so changing the look does not require a re-import.
/// Body (optional): { "visualMedium": "..." } — defaults to the stored preference.
/// Saves the result as the editable draft when the scene structure is preserved.
/// </summary>
app.MapPost("/api/projects/{id}/adaptation/reskin", async (
    string id,
    HttpRequest req,
    ProjectStore store,
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var dir = store.GetProjectDir(id);

        string? medium = null;
        if (req.ContentLength is > 0)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.TryGetProperty("visualMedium", out var vm) && vm.ValueKind == JsonValueKind.String)
                    medium = vm.GetString();
                else if (root.TryGetProperty("visual_medium", out var vm2) && vm2.ValueKind == JsonValueKind.String)
                    medium = vm2.GetString();
            }
            catch { /* no/invalid body — fall back to stored preference */ }
        }
        if (string.IsNullOrWhiteSpace(medium))
            medium = ProjectVisionMeta.GetAdaptationMediumPreference(dir);

        var result = await ScreenplayService.ReskinDraftAsync(store, id, medium, chat, ct: ct);
        return DraftEditResponse(result, id, $"ptm:stage=reskin medium={medium}", store, user);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Enrich the current Fountain draft's descriptive layer for the stored medium (Scene Embellishment).
/// Incorporates the book's own language where prepared text exists; dialogue / scenes / structure preserved.
/// Saves the enriched result as the editable draft when the scene structure is preserved.
/// </summary>
app.MapPost("/api/projects/{id}/adaptation/embellish", async (
    string id,
    ProjectStore store,
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var medium = ProjectVisionMeta.GetAdaptationMediumPreference(store.GetProjectDir(id));

        var result = await ScreenplayService.EmbellishDraftAsync(store, id, medium, chat, ct: ct);
        return DraftEditResponse(result, id, "ptm:stage=embellish", store, user);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Trim the screenplay toward the project's current target runtime (Trim to cost/length).
/// Derives the working draft from the immutable full-length base; re-running with a new target
/// re-derives cheaply without re-import. Set the target first via PUT /film-runtime.
/// </summary>
app.MapPost("/api/projects/{id}/adaptation/trim", async (
    string id,
    ProjectStore store,
    PageToMovie.Core.Abstractions.IChatClient chat,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);

        var result = await ScreenplayService.TrimDraftAsync(store, id, chat, ct: ct);
        return DraftEditResponse(result, id, "ptm:stage=trim", store, user);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Get natural + target film length for cost/Stage1.</summary>
app.MapGet("/api/projects/{id}/film-runtime", async (
    string id,
    ProjectStore store,
    IUserContext user,
    CancellationToken ct) =>
{
    try
    {
        var snap = await FilmRuntime.ResolveAsync(store, id, ct: ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            hasBookText = snap.HasBookText,
            naturalMinutes = snap.NaturalMinutes,
            targetMinutes = snap.TargetMinutes,
            mode = snap.Mode,
            textWords = snap.TextWords,
            bookKind = snap.BookKind,
            source = snap.Source,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Set target film length (shorter = typically lower cost). Does not re-run Stage1.</summary>
app.MapPut("/api/projects/{id}/film-runtime", async (
    string id,
    HttpRequest req,
    ProjectStore store,
    IUserContext user,
    CancellationToken ct) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        int target = 0;
        if (root.TryGetProperty("targetMinutes", out var tm) && tm.TryGetInt32(out var t1))
            target = t1;
        else if (root.TryGetProperty("target_runtime_minutes", out var tm2) && tm2.TryGetInt32(out var t2))
            target = t2;
        if (target <= 0)
            return Results.BadRequest(new { ok = false, error = "targetMinutes required (2–180)" });

        var snap = await FilmRuntime.SetTargetAsync(store, id, target, ct).ConfigureAwait(false);
        store.TriggerAutoGitCommit(id, $"ptm:stage=runtime_retarget target={snap.TargetMinutes}");
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            hasBookText = snap.HasBookText,
            naturalMinutes = snap.NaturalMinutes,
            targetMinutes = snap.TargetMinutes,
            mode = snap.Mode,
            message = snap.TargetMinutes < snap.NaturalMinutes
                ? $"Target set to {snap.TargetMinutes} min (shorter than natural ~{snap.NaturalMinutes} min — typically fewer clips / lower cost)."
                : snap.TargetMinutes == snap.NaturalMinutes
                    ? $"Target set to natural length (~{snap.NaturalMinutes} min)."
                    : $"Target set to {snap.TargetMinutes} min (longer than natural ~{snap.NaturalMinutes} min).",
            adaptation = store.GetAdaptationStatus(id, user.UserId),
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
    PageToMovie.Core.Abstractions.IChatClient chat,
    BookTextRegistryService books,
    IUserContext user,
    UserDatabaseService userDb,
    IUserApiKeyProvider keys,
    IOptions<PageToMovieOptions> opts,
    PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions,
    CancellationToken ct) =>
{
    if (await AuthGate.RequirePersonalGrokKeyAsync(user, userDb, opts, useFakes, keys) is { } denied)
        return denied;
    try
    {
        var result = await ScreenplayService.CreateDraftFromBookAsync(
            store, id, chat, ct: ct, bookRegistry: books, cacheUserId: user.UserId,
            bookFileSessionFactory: bookFileSessions);
        if (!result.Ok)
            return Results.BadRequest(new { ok = false, error = result.Error });

        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            message = result.Message,
            screenplay = result.Status,
            adaptation = store.GetAdaptationStatus(id, user.UserId),
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

        var ctx = await BookContextService.GetContextAsync(store, id, sceneIndex, heading, body, req.HttpContext.RequestAborted);
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

app.MapPost("/api/jobs/stage1", async (
    StartStage1Request body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
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

app.MapPost("/api/jobs/stage2", async (
    StartStage2Request body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    UserDatabaseService userDb) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;
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

app.MapPost("/api/jobs/youtube-upload", async (
    HttpRequest request,
    FilmJobService jobService,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    // Shared channel OAuth lives on the server — clients only upload via this UI/API path.
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    try
    {
        string? projectId = null;
        string? title = null;
        string? description = null;
        string? privacyStatus = null;
        IFormFile? file = null;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            projectId = form["projectId"].ToString();
            title = form["title"].ToString();
            description = form["description"].ToString();
            privacyStatus = form["privacyStatus"].ToString();
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        else
        {
            var body = await request.ReadFromJsonAsync<StartYouTubeUploadRequest>(cancellationToken: ct);
            if (body is not null)
            {
                projectId = body.ProjectId;
                title = body.Title;
                description = body.Description;
                privacyStatus = body.PrivacyStatus;
            }
        }

        if (string.IsNullOrWhiteSpace(projectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });

        if (file is not null && file.Length > 0)
        {
            var pDir = store.GetProjectDir(projectId);
            var videoDir = Path.Combine(pDir, "assets", "video");
            Directory.CreateDirectory(videoDir);
            var savePath = Path.Combine(videoDir, "wip_movie.mp4");
            await using var stream = File.Create(savePath);
            await file.CopyToAsync(stream, ct);
        }

        var req = new StartYouTubeUploadRequest
        {
            ProjectId = projectId!,
            Title = title,
            Description = description,
            PrivacyStatus = privacyStatus ?? "unlisted",
        };

        var job = await jobService.StartYouTubeUploadAsync(req);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = "Queued YouTube upload",
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

app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips", (
    string id, int scene, ClipEditRequest body, ProjectStore store) =>
{
    try
    {
        body.ProjectId = id;
        body.Scene = scene;
        store.AddClip(id, scene, body);
        return Results.Ok(new { ok = true, projectId = id, scene, clip = body.Clip, added = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPut("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}", (
    string id, int scene, int clip, ClipEditRequest body, ProjectStore store) =>
{
    try
    {
        body.ProjectId = id;
        body.Scene = scene;
        body.Clip = clip;
        store.UpdateClipFields(id, scene, clip, body);
        return Results.Ok(new { ok = true, projectId = id, scene, clip });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapDelete("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}", async (
    string id, int scene, int clip, ProjectStore store, ReviewIndexService reviewIndex,
    EditLogService logs, CancellationToken ct) =>
{
    try
    {
        var wasInBlueprint = store.DeleteClip(id, scene, clip);
        await reviewIndex.RemoveClipAsync(id, scene, clip, ct);
        await logs.RemoveClipReviewStateAsync(id, scene, clip, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            scene,
            clip,
            deleted = true,
            wasInBlueprint,
            message = $"Deleted S{scene:D2}C{clip:D2} — Play scene / Play WIP to refresh the assembled cut",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Delete a whole scene from the shot plan (persisted — removes it from the blueprint and
/// deletes the scene's on-disk media). Owner/admin only.</summary>
app.MapDelete("/api/projects/{id}/scenes/{scene:int}", async (
    string id, int scene, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can edit the shot plan.", ct) is { } forbidden)
        return forbidden;
    try
    {
        var removed = store.DeleteScene(id, scene);
        return Results.Ok(new { ok = true, projectId = id, scene, deleted = removed,
            message = $"Deleted Scene {scene:D2}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Append a new empty scene to the shot plan. Owner/admin only.</summary>
app.MapPost("/api/projects/{id}/scenes", async (
    string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can edit the shot plan.", ct) is { } forbidden)
        return forbidden;
    try
    {
        var sceneNo = store.AddScene(id);
        return Results.Ok(new { ok = true, projectId = id, scene = sceneNo, message = $"Added Scene {sceneNo:D2}" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>One-click add a prefilled (editable) end-credits scene. Owner/admin only.</summary>
app.MapPost("/api/projects/{id}/scenes/credits", async (
    string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (await RequireProjectOwnerOrAdmin(id, store, user, "Only the project owner or an admin can edit the shot plan.", ct) is { } forbidden)
        return forbidden;
    try
    {
        var sceneNo = store.AddCreditsScene(id);
        return Results.Ok(new { ok = true, projectId = id, scene = sceneNo, message = $"Added credits (Scene {sceneNo:D2}) — edit or generate it" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/scenes/{scene:int}/approve", async (
    string id, int scene, SceneApproveRequest? body, EditLogService logs, CancellationToken ct) =>
{
    try
    {
        body ??= new SceneApproveRequest();
        await logs.MarkSceneApprovedAsync(id, scene, body.Note ?? "", ct);
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
app.MapGet("/api/projects/{id}/review/index", async (
    string id, bool? rebuild, ReviewIndexService reviewIndex, CancellationToken ct) =>
{
    try
    {
        var doc = rebuild == true
            ? await reviewIndex.RebuildAsync(id, ct: ct)
            : await reviewIndex.LoadAsync(id, ct) ?? await reviewIndex.RebuildAsync(id, ct: ct);
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
app.MapGet("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/auto-review", async (
    string id, int scene, int clip, ClipAutoReviewService reviews, CancellationToken ct) =>
{
    try
    {
        var draft = await reviews.LoadDraftAsync(id, scene, clip, ct);
        if (draft is null)
            return Results.NotFound(new { ok = false, error = "No auto-review draft yet." });
        return Results.Ok(new { ok = true, draft });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Trigger automated dialogue verification for a clip on demand. Accepts optional uploaded video file which is deleted immediately after API call.</summary>
app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/verify-dialogue", async (
    string id, int scene, int clip, HttpContext httpContext, ClipDialogueVerificationService verifier, bool force = false, CancellationToken ct = default) =>
{
    string? tempFilePath = null;
    try
    {
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("video");
            if (file is { Length: > 0 })
            {
                tempFilePath = Path.Combine(Path.GetTempPath(), $"dialogue_verify_{Guid.NewGuid():N}.mp4");
                using (var stream = File.Create(tempFilePath))
                {
                    await file.CopyToAsync(stream, ct).ConfigureAwait(false);
                }
            }
        }

        var result = await verifier.VerifyClipDialogueAsync(id, scene, clip, overrideVideoPath: tempFilePath, force: force, ct: ct);
        return Results.Ok(new { ok = true, result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
    finally
    {
        if (!string.IsNullOrWhiteSpace(tempFilePath) && File.Exists(tempFilePath))
        {
            try { File.Delete(tempFilePath); } catch { }
        }
    }
});

/// <summary>Upload local client clip MP4 file to server assets/video directory.</summary>
app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/upload", async (
    string id, int scene, int clip, string? kind, HttpContext httpContext, ProjectStore store, CancellationToken ct) =>
{
    if (!httpContext.Request.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "Form data expected." });

    var form = await httpContext.Request.ReadFormAsync(ct);
    var file = form.Files.GetFile("video");
    if (file is null || file.Length < 1024)
        return Results.BadRequest(new { ok = false, error = "Valid MP4 file expected." });

    var projectDir = store.GetProjectDir(id);
    var destDir = Path.Combine(projectDir, "assets", "video");
    Directory.CreateDirectory(destDir);
    // "extend-source": the client's tail-trimmed continuation input for video-extend (see
    // FilmJobService.GenerateOneClipAsync) — fixed name, ignores any client-supplied filename so
    // the server always finds it at the exact path it expects.
    var fileName = string.Equals(kind, "extend-source", StringComparison.OrdinalIgnoreCase)
        ? $"_extend_src_s{scene:D2}c{clip:D2}.mp4"
        : !string.IsNullOrWhiteSpace(file.FileName) && file.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(file.FileName)
            : $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4";
    var destPath = Path.Combine(destDir, fileName);

    using (var stream = File.Create(destPath))
    {
        await file.CopyToAsync(stream, ct).ConfigureAwait(false);
    }

    return Results.Ok(new { ok = true, projectId = id, scene, clip, path = destPath });
});

/// <summary>
/// Queue background-music generation for a scene (client-side job: mirrors clip gen — the
/// server never spawns ffmpeg or persists audio bytes; segments proxy straight to the client).
/// </summary>
app.MapPost("/api/jobs/scene-music", async (
    StartSceneMusicGenRequest? body,
    FilmJobService jobService,
    IUserContext user,
    IOptions<PageToMovieOptions> opts) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var projectId = body?.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            return Results.BadRequest(new { ok = false, error = "projectId required" });
        if (body is null || body.Scene <= 0)
            return Results.BadRequest(new { ok = false, error = "scene required" });
        var job = await jobService.StartSceneMusicGenAsync(projectId.Trim(), body.Scene, body.Model, body.IsVocal);
        return Results.Accepted($"/api/jobs/{job.JobId}", new
        {
            ok = true,
            message = $"Queued background music for Scene {body.Scene:D2}",
            job,
        });
    }
    catch (Exception ex)
    {
        return JobStartError(ex, jobService);
    }
});

/// <summary>
/// Non-destructively augments blueprint.clips.grok.json with AI-composed scene music prompts across all scenes.
/// </summary>
app.MapPost("/api/projects/{id}/augment-music", async (
    string id,
    ProjectStore store,
    SceneMusicCompositionService composer,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    string? model = null,
    CancellationToken ct = default) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;

    var pDir = await store.GetProjectDirAsync(id, ct);
    if (string.IsNullOrWhiteSpace(pDir) || !Directory.Exists(pDir))
        return Results.NotFound(new { ok = false, error = "Project not found." });

    var success = await composer.AugmentProjectMusicAsync(pDir, model, ct);
    if (!success)
        return Results.BadRequest(new { ok = false, error = "Music score augmentation failed. Ensure blueprint.clips.grok.json exists." });

    store.TriggerAutoGitCommit(id, "Augment blueprint with AI music score prompts");
    return Results.Ok(new { ok = true, message = "Successfully augmented blueprint with AI background music scores." });
});

/// <summary>Write accepted suggestion fields (cast / clip prompt). Does not regen — client starts gen after.</summary>
app.MapPost("/api/projects/{id}/scenes/{scene:int}/clips/{clip:int}/auto-review/apply", async (
    string id, int scene, int clip, ApplyClipAutoReviewRequest? body, ClipAutoReviewService reviews, CancellationToken ct) =>
{
    try
    {
        body ??= new ApplyClipAutoReviewRequest();
        body.ProjectId = id;
        body.Scene = scene;
        body.Clip = clip;
        await reviews.ApplySuggestionsAsync(id, scene, clip, body.Items, ct);
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


app.MapGet("/api/me/api-calls", async (
    int? take,
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var rows = await userDb.ListUserApiCallsAsync(user.UserId, take ?? 100, ct);
    var totalUsd = rows.Where(r => r.EstimatedUsd is > 0).Sum(r => r.EstimatedUsd!.Value);
    return Results.Ok(new
    {
        ok = true,
        userId = user.UserId,
        count = rows.Count,
        estimatedUsdSum = Math.Round(totalUsd, 4),
        notes = "List-rate estimates at call time (catalog). Not provider invoices. Full prompts stay on the project telemetry file.",
        calls = rows,
    });
});

// ---- Cost (ledger + estimates) ----
// Capability availability — JIT, user-level, capability-focused. Which generation capabilities
// have at least one provider configured right now (a key, or fakes). Any provider that offers the
// capability counts (MultiProvider*.IsConfigured). NOT project-scoped: a project's required
// capabilities change as it develops and keys change anytime, so the UI checks this live to
// disable a model/key-dependent action with a "Set up →" hint rather than show it and fail on click.
app.MapGet("/api/capabilities", (
    IVideoClient video,
    IImageClient image,
    IVisionClient vision,
    IAudioClient audio,
    IVoiceClient voice,
    IChatClient chat) =>
{
    var caps = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        ["video"] = video.IsConfigured,
        ["image"] = image.IsConfigured,
        ["vision"] = vision.IsConfigured,
        ["review"] = vision.IsConfigured,   // multimodal auto-review runs on the vision client
        ["music"] = audio.IsConfigured,
        ["voice"] = voice.IsConfigured,
        ["planning"] = chat.IsConfigured,
    };

    // Dev/testing affordance: force capabilities off (comma-separated) to preview and test the
    // gated UI — fakes mode reports everything configured, so the disabled state is otherwise
    // unreachable locally. No effect in production unless the env var is set.
    var forcedOff = Environment.GetEnvironmentVariable("PAGETOMOVIE_FAKE_DISABLED_CAPABILITIES");
    if (!string.IsNullOrWhiteSpace(forcedOff))
    {
        foreach (var c in forcedOff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (caps.ContainsKey(c)) caps[c] = false;
    }

    return Results.Ok(new { ok = true, capabilities = caps });
});

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

/// <summary>Resolution already used by this project's on-disk clips, if consistent — null once no clips exist yet.</summary>
app.MapGet("/api/projects/{id}/resolution-lock", async (
    string id, FilmJobService jobs, CancellationToken ct) =>
{
    try
    {
        var locked = await jobs.GetLockedResolutionAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, locked });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Actual spend by provider for this project.
/// Default: <b>signed-in user's</b> spend on this project. Pass <c>?all=true</c> (admin) for every user.
/// </summary>
app.MapGet("/api/projects/{id}/cost/by-provider", async (
    string id,
    ProjectStore store,
    UserDatabaseService userDb,
    IUserContext user,
    bool? all,
    CancellationToken ct) =>
{
    try
    {
        _ = await store.GetProjectAsync(id, ct)
            ?? throw new InvalidOperationException($"Unknown project: {id}");
        var allUsers = all == true && user.IsAdmin;
        var userId = allUsers ? null : (string.IsNullOrWhiteSpace(user.UserId) ? null : user.UserId);
        var stats = await userDb.GetApiCostByProviderAsync(userId: userId, projectId: id, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = id,
            userId = userId,
            scope = allUsers ? "all_users" : "current_user",
            notes = "List vs charge: list_usd = vendor catalog; charge = list × admin multiplier. Grouped by provider (xAI, Google, ElevenLabs, …).",
            stats,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Signed-in user's spend: grand total, by project, by vendor, by category.
/// Optional <c>?projectId=</c> filters to one project.
/// </summary>
app.MapGet("/api/me/spend", async (
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    string? projectId,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var uid = user.UserId?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(uid))
        return Results.Unauthorized();
    var summary = await userDb.GetUserSpendSummaryAsync(uid, projectId, ct);
    return Results.Ok(new
    {
        ok = true,
        summary,
        notes = "Per-user tracking from user_api_calls. Charge = list × admin multiplier. Provider = catalog vendor id (xai, google, elevenlabs, …).",
    });
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
    async (string id, int sceneNumber, int clipNumber, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var path = store.ResolveClipVideoPath(id, sceneNumber, clipNumber);
        if (path is null)
        {
            // Fork fallback: a fork skips video (ForkSkipExtensions), so source the clip from its
            // parent project — a forkable source keeps its media server-side (keep_media_on_server)
            // — letting the client download it to dub/edit. The dubbed output stays per-user client-side.
            try
            {
                var proj = await store.GetProjectAsync(id, ct);
                var parent = proj?.ParentProjectId;
                if (!string.IsNullOrWhiteSpace(parent))
                    path = store.ResolveClipVideoPath(parent, sceneNumber, clipNumber);
            }
            catch { /* fall through to 404 */ }
        }
        if (path is null)
            return Results.NotFound(new { ok = false, error = "clip video not found" });
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Structured end-credits card content (title/author/software/site) — the client renders these
/// exact strings deterministically instead of asking a generative model to draw text.</summary>
app.MapGet("/api/projects/{id}/credits-content",
    (string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try { return Results.Ok(store.BuildCreditsContent(id)); }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
});

/// <summary>Archived prompt (+ paired video, if the client's media folder still has it) versions for one clip.</summary>
app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}/clips/{clipNumber:int}/prompt-history",
    (string id, int sceneNumber, int clipNumber, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var projectDir = store.GetProjectDir(id);
        string? currentPrompt = null;
        var currentMetaPath = Path.Combine(
            projectDir, "assets", "video", "prompts", $"S{sceneNumber:D2}C{clipNumber:D2}.meta.json");
        if (File.Exists(currentMetaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(currentMetaPath));
                if (doc.RootElement.TryGetProperty("prompt", out var p))
                    currentPrompt = p.GetString();
            }
            catch { /* ignore unreadable current meta */ }
        }

        var history = FilmJobService.ListClipPromptHistory(projectDir, sceneNumber, clipNumber);
        return Results.Ok(new
        {
            ok = true,
            current = new
            {
                prompt = currentPrompt,
                videoRelativePath = MediaRegistryService.ClipRelativePath(sceneNumber, clipNumber),
            },
            history = history.Select(h => new
            {
                timestampUtc = h.TimestampUtc,
                prompt = h.Prompt,
                videoRelativePath = h.VideoRelativePath,
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/scenes/{sceneNumber:int}/composite",
    (string id, int sceneNumber, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
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

/// <summary>Stream or download the WIP full movie for client external editor / playback.</summary>
app.MapGet("/api/projects/{id}/movie", (string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var path = store.ResolveWipMoviePath(id);
        if (path is null || !File.Exists(path))
        {
            var pDir = store.GetProjectDir(id);
            var altWip = Path.Combine(pDir, "assets", "video", "wip_movie.mp4");
            if (File.Exists(altWip)) path = altWip;
        }
        if (path is null || !File.Exists(path))
            return Results.NotFound(new { ok = false, error = "Full movie file not found on server — build or play movie first." });
        return Results.File(path, "video/mp4", fileDownloadName: $"{id}_full.mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Stream the WIP full movie (authenticated). Public share uses /api/share/{{token}}.</summary>
app.MapGet("/api/projects/{id}/movie/wip", (string id, ProjectStore store, IUserContext user, IOptions<PageToMovieOptions> opts) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var path = store.ResolveWipMoviePath(id);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "WIP movie not found — Play first so the cut is built" });
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Create or reuse a public share link for the WIP movie (login required).</summary>
app.MapPost("/api/projects/{id}/movie/wip/share", (
    string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    MediaShareService shares,
    HttpContext http) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        var rec = shares.EnsureWipShare(id, user.UserId);
        var path = $"/api/share/{Uri.EscapeDataString(rec.Token)}";
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        return Results.Ok(new
        {
            ok = true,
            token = rec.Token,
            path,
            url = baseUrl + path,
            expiresAt = rec.ExpiresAt,
            projectId = rec.ProjectId,
            kind = rec.Kind,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Client media sync: list available project MP4s and sidecars with proxy tickets.</summary>
app.MapGet("/api/projects/{id}/media/sync", async (
    string id,
    ProjectStore store,
    MediaProxyTicketStore tickets,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var projectDir = store.GetProjectDir(id);
        // Media that may have arrived via full project import (video, music, audio, history).
        var list = new List<object>();
        var assetsRoot = Path.Combine(projectDir, "assets");
        var mediaExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mov", ".mkv", ".m4v",
            ".mp3", ".wav", ".m4a", ".ogg", ".aac", ".flac", ".opus",
            ".png", ".jpg", ".jpeg", ".webp", ".gif",
        };

        if (Directory.Exists(assetsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name is "Thumbs.db" or ".DS_Store") continue;
                var ext = Path.GetExtension(file);
                var isClipJson = name.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase);
                if (!isClipJson && !mediaExts.Contains(ext))
                    continue;

                var relPath = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
                var fi = new FileInfo(file);
                var isMp4 = ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase);

                string? sha256 = null;
                try
                {
                    if (fi.Length <= 64L * 1024 * 1024)
                    {
                        using var fs = File.OpenRead(file);
                        var hashBytes = System.Security.Cryptography.SHA256.HashData(fs);
                        sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                }
                catch { /* best-effort sha256 */ }

                var ticketToken = tickets.Issue($"{id}:{relPath}", TimeSpan.FromHours(2));
                var streamUrl = $"/api/projects/{Uri.EscapeDataString(id)}/media/file?path={Uri.EscapeDataString(relPath)}&ticket={ticketToken}";

                list.Add(new
                {
                    relativePath = relPath,
                    fileName = name,
                    sizeBytes = fi.Length,
                    sha256,
                    isMp4,
                    streamUrl,
                });
            }
        }

        return Results.Ok(new { ok = true, projectId = id, files = list });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Download a specific media file (MP4 clip or sidecar manifest) from a project.</summary>
app.MapGet("/api/projects/{id}/media/file", async (
    string id,
    string path,
    string? ticket,
    ProjectStore store,
    MediaProxyTicketStore tickets,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    var ticketValid = false;
    if (!string.IsNullOrWhiteSpace(ticket))
    {
        var target = tickets.TryTakeUrl(ticket);
        if (target is not null && string.Equals(target, $"{id}:{path}", StringComparison.OrdinalIgnoreCase))
        {
            ticketValid = true;
        }
    }

    if (!ticketValid && AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { ok = false, error = "path parameter required" });

        var projectDir = store.GetProjectDir(id);
        var cleanRelPath = path.TrimStart('/', '\\').Replace('\\', '/');

        var fullPath = Path.GetFullPath(Path.Combine(projectDir, cleanRelPath.Replace('/', Path.DirectorySeparatorChar)));
        var fullProjDir = Path.GetFullPath(projectDir);
        if (!fullPath.StartsWith(fullProjDir, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, error = "Invalid media path" });

        if (!File.Exists(fullPath))
            return Results.NotFound(new { ok = false, error = "File not found" });

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp4" => "video/mp4",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".webm" => "audio/webm",
            _ => "application/octet-stream"
        };

        return Results.File(fullPath, contentType, Path.GetFileName(fullPath), enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Public stream for a shared WIP (no login — token is the capability).</summary>
app.MapGet("/api/share/{token}", (string token, MediaShareService shares, ProjectStore store) =>
{
    var rec = shares.TryGet(token);
    if (rec is null)
        return Results.NotFound(new { ok = false, error = "Share link not found or expired" });
    if (!string.Equals(rec.Kind, "wip", StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(new { ok = false, error = "Unsupported share kind" });
    try
    {
        var path = store.ResolveWipMoviePath(rec.ProjectId);
        if (path is null)
            return Results.NotFound(new { ok = false, error = "Shared movie is no longer available" });
        return Results.File(path, "video/mp4", enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

static object DemoPublicDto(
    DemoCatalogService.DemoEntry d,
    int upvoteCount = 0,
    bool upvotedByMe = false,
    bool canFork = false,
    string visibilityMode = "Private") => new
{
    d.Id,
    d.Title,
    d.Description,
    d.ProjectId,
    d.CreatedBy,
    d.CreatedAt,
    d.SizeBytes,
    d.Status,
    d.ReportCount,
    upvoteCount,
    upvotedByMe,
    // True when this public film's studio project still exists (gallery Fork button).
    canFork,
    // YouTube is gallery playback SoT. Local videoPath only for staging (owner) before upload finishes.
    videoPath = string.IsNullOrWhiteSpace(d.YoutubeId)
        ? $"/api/demos/{Uri.EscapeDataString(d.Id)}/video"
        : null,
    d.YoutubeId,
    d.YoutubeUrl,
    d.Category,
    d.Tags,
    youtubeWatchUrl = string.IsNullOrWhiteSpace(d.YoutubeId)
        ? null
        : (string.IsNullOrWhiteSpace(d.YoutubeUrl) ? $"https://www.youtube.com/watch?v={d.YoutubeId}" : d.YoutubeUrl),
    d.YoutubeLikeCount,
    d.YoutubeViewCount,
    visibilityMode,
};

static object DemoAdminDto(DemoCatalogService.DemoEntry d) => new
{
    d.Id,
    d.Title,
    d.Description,
    d.ProjectId,
    d.CreatedBy,
    d.CreatedAt,
    d.SizeBytes,
    d.Status,
    d.AcceptedGuidelines,
    d.ReportCount,
    d.ReportNotes,
    d.ReviewedBy,
    d.ReviewedAt,
    d.ReviewNote,
    videoPath = $"/api/demos/{Uri.EscapeDataString(d.Id)}/video",
    d.YoutubeId,
    d.YoutubeUrl,
    d.YoutubeUploadStatus,
    d.YoutubeUploadError,
    d.MadeForKids,
    d.IsAiSyntheticContent,
    d.PrivacyStatus,
};

/// <summary>Public gallery: demos on YouTube (no login). sort=top|new (default top by upvotes).</summary>
app.MapGet("/api/demos", async (
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    ProjectStore store,
    IUserContext user,
    YouTubeChannelGallerySync channelSync,
    int? take,
    string? sort,
    CancellationToken ct) =>
{
    // YouTube channel is SoT: quietly refresh catalog when connected (throttled).
    try { await channelSync.EnsureSyncedAsync(force: false, ct: ct); }
    catch { /* non-fatal for public list */ }

    var list = demos.ListPublic(take ?? 50).ToList();
    var ids = list.Select(d => d.Id).ToList();
    var counts = await upvotes.GetCountsAsync(ids, ct);
    var mine = await upvotes.GetUpvotedSetAsync(user.UserId, ids, ct);

    var visibilityMap = new Dictionary<string, string>();
    var forkableProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in list)
    {
        if (!string.IsNullOrWhiteSpace(d.ProjectId))
        {
            try
            {
                var proj = await store.GetProjectAsync(d.ProjectId, ct);
                if (proj is not null)
                {
                    visibilityMap[d.Id] = proj.VisibilityMode;
                    forkableProjectIds.Add(d.ProjectId);
                }
            }
            catch { /* skip */ }
        }
    }

    var sortKey = (sort ?? "top").Trim().ToLowerInvariant();
    IEnumerable<DemoCatalogService.DemoEntry> ordered = sortKey switch
    {
        "new" => list.OrderByDescending(d => d.CreatedAt),
        _ => list
            .OrderByDescending(d => counts.GetValueOrDefault(d.Id))
            .ThenByDescending(d => d.CreatedAt),
    };

    return Results.Ok(new
    {
        ok = true,
        sort = sortKey is "new" ? "new" : "top",
        youtubeSync = new
        {
            lastSuccessUtc = channelSync.LastSuccessUtc,
            lastError = channelSync.LastError,
        },
        demos = ordered.Select(d => DemoPublicDto(
            d,
            counts.GetValueOrDefault(d.Id),
            mine.Contains(d.Id),
            canFork: !string.IsNullOrWhiteSpace(d.ProjectId) && forkableProjectIds.Contains(d.ProjectId!),
            visibilityMode: visibilityMap.GetValueOrDefault(d.Id, "Private"))),
    });
});

/// <summary>Admin list of demos (reports/removed). Content approval queue is retired — YouTube is the gate.</summary>
app.MapGet("/api/admin/demos", (
    DemoCatalogService demos,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    string? status,
    int? take) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "Admin only" }, statusCode: StatusCodes.Status403Forbidden);

    var st = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
    var list = demos.List(take ?? 100, st);
    return Results.Ok(new
    {
        ok = true,
        status = st,
        demos = list.Select(DemoAdminDto),
        pendingCount = demos.List(200, DemoCatalogService.DemoStatuses.Pending).Count,
    });
});

/// <summary>
/// Admin: register an existing YouTube video on the public gallery (no local MP4 upload).
/// Body: { youtubeIdOrUrl, title, description?, projectId? }
/// </summary>
app.MapPost("/api/admin/demos/from-youtube", (
    DemoCatalogService demos,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    RegisterYouTubeDemoRequest? body) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "Admin only" }, statusCode: StatusCodes.Status403Forbidden);
    if (body is null || string.IsNullOrWhiteSpace(body.YoutubeIdOrUrl) || string.IsNullOrWhiteSpace(body.Title))
        return Results.BadRequest(new { ok = false, error = "youtubeIdOrUrl and title are required" });
    try
    {
        var entry = demos.RegisterFromYouTube(
            body.YoutubeIdOrUrl,
            body.Title,
            body.Description,
            createdBy: user.UserId,
            projectId: body.ProjectId);
        return Results.Ok(new
        {
            ok = true,
            message = $"“{entry.Title}” is on the public gallery (YouTube).",
            demo = DemoAdminDto(entry),
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Admin: pull every upload from the connected YouTube channel into the public gallery catalog.</summary>
app.MapPost("/api/admin/demos/sync-youtube", async (
    YouTubeChannelGallerySync channelSync,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "Admin only" }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var (added, updated, total, skipped) = await channelSync.EnsureSyncedAsync(
            force: true,
            createdBy: user.UserId,
            maxVideos: 100,
            ct: ct);
        return Results.Ok(new
        {
            ok = true,
            added,
            updated,
            total,
            skipped,
            message = total == 0 && skipped
                ? "Nothing to sync (channel not connected or empty)."
                : $"Synced {total} channel video(s): {added} new, {updated} updated.",
            lastError = channelSync.LastError,
            lastSuccessUtc = channelSync.LastSuccessUtc,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Public metadata for a public demo; owner/admin can see pending.</summary>
app.MapGet("/api/demos/{demoId}", async (
    string demoId,
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    IUserContext user,
    CancellationToken ct) =>
{
    var d = demos.TryGet(demoId);
    if (d is null)
        return Results.NotFound(new { ok = false, error = "Demo not found" });
    if (!demos.CanUserViewVideo(d, user.UserId, user.IsAdmin))
        return Results.NotFound(new { ok = false, error = "Demo not found" });
    var count = await upvotes.GetCountAsync(demoId, ct);
    var me = await upvotes.HasUpvotedAsync(demoId, user.UserId, ct);
    if (user.IsAdmin)
    {
        return Results.Ok(new
        {
            ok = true,
            demo = DemoAdminDto(d),
            upvoteCount = count,
            upvotedByMe = me,
        });
    }
    return Results.Ok(new { ok = true, demo = DemoPublicDto(d, count, me) });
});

/// <summary>Star / upvote a public demo (signed-in). Idempotent. No self-upvote.</summary>
app.MapPost("/api/demos/{demoId}/upvote", async (
    string demoId,
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var d = demos.TryGet(demoId);
    if (d is null || !demos.IsPubliclyStreamable(d))
        return Results.NotFound(new { ok = false, error = "Demo not found" });
    if (!string.IsNullOrWhiteSpace(d.CreatedBy) &&
        string.Equals(d.CreatedBy, user.UserId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new
        {
            ok = false,
            error = "You can’t star your own demo.",
            code = "self_upvote",
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    await upvotes.TryAddAsync(demoId, user.UserId!, ct);
    var newCount = await upvotes.GetCountAsync(demoId, ct);
    return Results.Ok(new
    {
        ok = true,
        upvoteCount = newCount,
        upvotedByMe = true,
    });
});

/// <summary>
/// Feature 11: fork the studio project behind a public gallery film (lightweight package, no video).
/// Requires sign-in. Visibility modes are not fully productized yet — any public demo with a
/// still-existing source project is forkable from the gallery.
/// </summary>
app.MapPost("/api/demos/{demoId}/fork", async (
    string demoId,
    DemoCatalogService demos,
    ProjectStore store,
    BookTextRegistryService books,
    IUserContext user,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;

    var d = demos.TryGet(demoId);
    if (d is null || !demos.IsPubliclyStreamable(d))
        return Results.NotFound(new { ok = false, error = "Demo not found" });

    var sourceId = (d.ProjectId ?? "").Trim();
    if (sourceId.Length == 0)
    {
        return Results.BadRequest(new
        {
            ok = false,
            error = "This film has no studio project to fork.",
            code = "no_source_project",
        });
    }

    try
    {
        var source = await store.GetProjectAsync(sourceId, ct);
        if (source is null)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "The studio project for this film is no longer available.",
                code = "source_missing",
            });
        }

        // A demo already confirmed public via demos.IsPubliclyStreamable(d) above is exactly the
        // "explicit authorization to fork" this endpoint's own doc comment promises — same bypass
        // ForkProjectAsync gives real invite-accepts, regardless of the source project's own
        // (possibly still-Private) VisibilityMode.
        var fork = await store.ForkProjectAsync(sourceId, user.UserId!, isInvite: true, ct: ct);
        await books.LinkForkAsync(sourceId, user.UserId!, fork.Id, invitationAuthorized: true, ct);
        return Results.Ok(new
        {
            ok = true,
            projectId = fork.Id,
            title = fork.Title,
            parentProjectId = sourceId,
            demoId,
            message = $"Created “{fork.Title ?? fork.Id}” from this film’s project.",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Remove star / upvote (signed-in).</summary>
app.MapDelete("/api/demos/{demoId}/upvote", async (
    string demoId,
    DemoCatalogService demos,
    DemoUpvoteService upvotes,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var d = demos.TryGet(demoId);
    if (d is null || !demos.IsPubliclyStreamable(d))
        return Results.NotFound(new { ok = false, error = "Demo not found" });

    await upvotes.TryRemoveAsync(demoId, user.UserId!, ct);
    var newCount = await upvotes.GetCountAsync(demoId, ct);
    return Results.Ok(new
    {
        ok = true,
        upvoteCount = newCount,
        upvotedByMe = false,
    });
});

/// <summary>Load full movie AI review report.</summary>
app.MapGet("/api/projects/{id}/review/movie", async (
    string id,
    ProjectStore store,
    MovieAutoReviewService movieReview,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var report = movieReview.LoadReport(id);
        return Results.Ok(new { ok = true, projectId = id, report });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Run full movie AI review with scene group chunking.</summary>
app.MapPost("/api/projects/{id}/review/movie", async (
    string id,
    MovieReviewRequest? body,
    ProjectStore store,
    MovieAutoReviewService movieReview,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var keyframes = body?.Keyframes ?? new List<MovieAutoReviewKeyframe>();
        var report = await movieReview.ReviewMovieAsync(id, keyframes, null, ct);
        return Results.Ok(new { ok = true, projectId = id, report });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Demo playback: redirect to YouTube when published there (source of truth).
/// Local MP4 only while staging (owner/admin) before upload completes.
/// </summary>
app.MapGet("/api/demos/{demoId}/video", (
    string demoId,
    DemoCatalogService demos,
    IUserContext user) =>
{
    var d = demos.TryGet(demoId);
    if (d is null)
        return Results.NotFound(new { ok = false, error = "Demo video not found" });
    if (!demos.CanUserViewVideo(d, user.UserId, user.IsAdmin))
        return Results.NotFound(new { ok = false, error = "Demo video not found" });

    // YouTube is the public source of truth — never stream server MP4 once YT id exists.
    if (!string.IsNullOrWhiteSpace(d.YoutubeId))
    {
        var url = !string.IsNullOrWhiteSpace(d.YoutubeUrl)
            ? d.YoutubeUrl!
            : $"https://www.youtube.com/watch?v={d.YoutubeId.Trim()}";
        return Results.Redirect(url);
    }

    var path = demos.ResolveMoviePath(demoId);
    if (path is null)
        return Results.NotFound(new
        {
            ok = false,
            error = "Film is uploading to YouTube — try the gallery again in a moment.",
            code = "awaiting_youtube",
        });
    return Results.File(path, "video/mp4", enableRangeProcessing: true);
});

/// <summary>Register client-side media hash (clips/exports) so the server need not store MP4s.</summary>
app.MapPost("/api/projects/{id}/media/register", async (
    string id,
    MediaRegisterRequest body,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    MediaRegistryService media,
    ProjectStore store,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Sha256) || string.IsNullOrWhiteSpace(body.RelativePath))
            return Results.BadRequest(new { ok = false, error = "relativePath and sha256 required" });

        var dto = await media.UpsertAsync(
            id,
            body.RelativePath,
            body.Sha256,
            body.SizeBytes,
            body.Kind ?? "clip",
            body.Scene,
            body.Clip,
            user.UserId,
            ct);

        // Character reference images are kept server-side (small; Cast readiness + thumbnails depend on
        // the ref file surviving reload). Client-storage offload is for large video clips only.
        var isCharacterImage = dto.RelativePath.Replace('\\', '/')
            .Contains("assets/characters/", StringComparison.OrdinalIgnoreCase);

        // Sidecar so scene lists treat clip as present without server MP4.
        try
        {
            var dir = store.GetProjectDir(id);
            var rel = dto.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // Curated/forkable source projects opt out of offload (project.json "keep_media_on_server":
            // true) so their clips stay server-side and remain available to forks + the voice-dub input.
            // A stopgap for clips generated before source_url capture; rebuilt movies re-fetch by URL.
            var keepMediaOnServer = false;
            try
            {
                var pjPath = Path.Combine(dir, "project.json");
                if (File.Exists(pjPath))
                {
                    using var pjDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(pjPath, ct));
                    keepMediaOnServer = pjDoc.RootElement.TryGetProperty("keep_media_on_server", out var kEl)
                        && kEl.ValueKind == System.Text.Json.JsonValueKind.True;
                }
            }
            catch { /* default: offload as usual */ }

            if (!isCharacterImage && !keepMediaOnServer)
            {
                var marker = full + ".client.json";
                await File.WriteAllTextAsync(marker, System.Text.Json.JsonSerializer.Serialize(new
                {
                    storage = "client",
                    sha256 = dto.Sha256,
                    sizeBytes = dto.SizeBytes,
                    registeredAt = dto.CreatedAt,
                    userId = user.UserId,
                }) + "\n", ct);

                // Reclaim server volume storage: if server MP4 exists and matches verified client registration size, delete server copy.
                if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    if (dto.SizeBytes <= 0 || fi.Length == dto.SizeBytes)
                    {
                        File.Delete(full);
                    }
                }
            }

            store.InvalidateSceneListCache(id);
        }
        catch { /* non-fatal */ }

        return Results.Ok(new { ok = true, media = dto });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/media", async (
    string id,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    MediaRegistryService media,
    ProjectStore store,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var list = await media.ListProjectAsync(id, ct);
        return Results.Ok(new { ok = true, media = list });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>CORS-safe download of provider video URL (short-lived ticket from gen job).</summary>
// Speech-to-text (ElevenLabs Scribe) for voice-capture verification: the client uploads an
// extracted dialogue segment and we return the transcript (+ word timings). Used to confirm a
// detected window contains the expected narrator line — never for the user's own takes.
app.MapPost("/api/transcribe", async (
    HttpRequest request,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    ElevenLabsScribeClient scribe,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!request.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "multipart form with audio 'file' required" });

    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { ok = false, error = "audio file required" });

    var lang = form["language_code"].ToString();
    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);

    var result = await scribe.TranscribeAsync(
        ms.ToArray(), file.FileName, string.IsNullOrWhiteSpace(lang) ? null : lang, ct);
    if (!result.Ok)
        return Results.Json(new { ok = false, error = result.Error }, statusCode: StatusCodes.Status502BadGateway);

    return Results.Ok(new
    {
        ok = true,
        text = result.Text,
        languageCode = result.LanguageCode,
        words = result.Words.Select(w => new { text = w.Text, start = w.Start, end = w.End, type = w.Type }),
    });
});

// Per-scene solo lines for a target character (default: narrator), straight from the blueprint (no
// dub / TTS needed) — lets the capture page build its phrase cache standalone. Returns each scene's
// line texts for that character + whether the scene also has another speaker (those scenes aren't
// capture material — mixed dialogue would bleed into the recording).
app.MapGet("/api/projects/{id}/voice-capture/narrator-lines", async (
    string id, string? charKey, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    using var blueprint = await store.LoadBlueprintAsync(id, ct);
    if (blueprint is null)
        return Results.Ok(new { ok = true, scenes = Array.Empty<object>() });

    var targetKey = string.IsNullOrWhiteSpace(charKey) ? null : charKey.Trim();
    bool IsTarget(string? spk)
    {
        if (string.IsNullOrWhiteSpace(spk)) return false;
        // Explicit character key: exact match — this is a deliberate user pick, not a guess.
        if (targetKey is not null)
            return string.Equals(spk.Trim(), targetKey, StringComparison.OrdinalIgnoreCase);
        // Default (no key given): the original narrator heuristic.
        return string.Equals(spk.Trim(), "Character_Narrator", StringComparison.OrdinalIgnoreCase) ||
               spk.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    var all = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, null);
    var scenesWithOther = new HashSet<int>();
    foreach (var cl in all)
        if (cl.Lines.Any(l => !IsTarget(l.CharacterKey)))
            scenesWithOther.Add(cl.Scene);

    var byScene = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, IsTarget)
        .GroupBy(c => c.Scene)
        .OrderBy(g => g.Key)
        .Select(g => new
        {
            scene = g.Key,
            hasOtherSpeakers = scenesWithOther.Contains(g.Key),
            lines = g.OrderBy(c => c.Clip)
                .SelectMany(c => c.Lines)
                .Select(l => l.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList(),
        })
        .ToList();

    return Results.Ok(new { ok = true, scenes = byScene });
});

// Voice-capture phrase cache (per project, computed once per book): the confident STT-verified
// dialogue phrases used by the capture UI and by the dub overlay's line↔window mapping.
app.MapGet("/api/projects/{id}/voice-capture/phrases", async (
    string id, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var path = Path.Combine(store.GetProjectDir(id), "assets", "voice_capture", "phrases.json");
    if (!File.Exists(path))
        return Results.Ok(new { ok = true, phrases = (VoiceCapturePhrases?)null });
    try
    {
        var json = await File.ReadAllTextAsync(path, ct);
        var data = System.Text.Json.JsonSerializer.Deserialize<VoiceCapturePhrases>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Results.Ok(new { ok = true, phrases = data });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/projects/{id}/voice-capture/phrases", async (
    string id, VoiceCapturePhrases body, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (body is null)
        return Results.BadRequest(new { ok = false, error = "phrases body required" });
    var dir = Path.Combine(store.GetProjectDir(id), "assets", "voice_capture");
    Directory.CreateDirectory(dir);
    body.ProjectId = id;
    body.GeneratedAtUtc = DateTime.UtcNow;
    var writeOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
    await File.WriteAllTextAsync(
        Path.Combine(dir, "phrases.json"),
        System.Text.Json.JsonSerializer.Serialize(body, writeOpts) + "\n", ct);
    return Results.Ok(new { ok = true, count = body.Phrases?.Count ?? 0 });
});

// All dialogue lines (every speaker) per scene, straight from the blueprint — the "script" side of
// the dialogue-timing review. No STT here; the client runs that pass and posts the result below.
app.MapGet("/api/projects/{id}/dialogue/lines", async (
    string id, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    using var blueprint = await store.LoadBlueprintAsync(id, ct);
    if (blueprint is null)
        return Results.Ok(new { ok = true, scenes = Array.Empty<object>() });

    var clips = VoiceAlignmentStore.BuildDialogueLinesFromBlueprint(blueprint.RootElement, null);
    var scenes = clips
        .GroupBy(c => c.Scene)
        .OrderBy(g => g.Key)
        .Select(g => new
        {
            scene = g.Key,
            lines = g.OrderBy(c => c.Clip)
                     .SelectMany(c => c.Lines.Select(l => new { clip = c.Clip, speaker = l.CharacterKey, text = l.Text }))
                     .ToList(),
        })
        .Where(s => s.lines.Count > 0)
        .ToList();

    return Results.Ok(new { ok = true, scenes });
});

// Cached dialogue-timing review (STT vs script per scene). Computed once per scene by the client.
app.MapGet("/api/projects/{id}/dialogue/timing", async (
    string id, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    var path = Path.Combine(store.GetProjectDir(id), "assets", "alignment", "dialogue_timing.json");
    if (!File.Exists(path))
        return Results.Ok(new { ok = true, timing = (DialogueTimingDoc?)null });
    try
    {
        var json = await File.ReadAllTextAsync(path, ct);
        var data = System.Text.Json.JsonSerializer.Deserialize<DialogueTimingDoc>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Results.Ok(new { ok = true, timing = data });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// Merge one analyzed/edited scene into the cache (scenes are reviewed independently).
app.MapPost("/api/projects/{id}/dialogue/timing/scene", async (
    string id, DialogueTimingScene body, IUserContext user, IOptions<PageToMovieOptions> opts, ProjectStore store, CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (body is null || body.Scene <= 0)
        return Results.BadRequest(new { ok = false, error = "scene body with a scene number required" });

    var dir = Path.Combine(store.GetProjectDir(id), "assets", "alignment");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "dialogue_timing.json");
    var webOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

    DialogueTimingDoc doc;
    if (File.Exists(path))
    {
        try { doc = System.Text.Json.JsonSerializer.Deserialize<DialogueTimingDoc>(await File.ReadAllTextAsync(path, ct), webOpts) ?? new(); }
        catch { doc = new(); }
    }
    else doc = new();

    doc.ProjectId = id;
    doc.GeneratedAtUtc = DateTime.UtcNow;
    doc.Scenes.RemoveAll(s => s.Scene == body.Scene);
    doc.Scenes.Add(body);
    doc.Scenes.Sort((a, b) => a.Scene.CompareTo(b.Scene));

    var writeOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
    await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(doc, writeOpts) + "\n", ct);
    return Results.Ok(new { ok = true, scene = body.Scene, rows = body.Rows?.Count ?? 0 });
});

app.MapGet("/api/media/proxy/{token}", async (
    string token,
    MediaProxyTicketStore tickets,
    IHttpClientFactory httpFactory,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var url = tickets.TryTakeUrl(token);
    if (string.IsNullOrWhiteSpace(url))
        return Results.NotFound(new { ok = false, error = "Media ticket expired or invalid" });

    // Inline provider bytes (e.g. ElevenLabs Music streams audio back rather than hosting a URL):
    // decode the self-contained data: URL and serve it, so no media is persisted on the API host.
    if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        var comma = url.IndexOf(',');
        var meta = comma > 0 ? url[5..comma] : "";
        if (comma < 0 || !meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, error = "Unsupported data URL" });
        var dataCtype = meta.Split(';')[0];
        if (string.IsNullOrWhiteSpace(dataCtype)) dataCtype = "application/octet-stream";
        byte[] dataBytes;
        try { dataBytes = Convert.FromBase64String(url[(comma + 1)..]); }
        catch { return Results.BadRequest(new { ok = false, error = "Malformed data URL" }); }
        var ext = dataCtype.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? ".mp3"
            : dataCtype.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".bin";
        return Results.Bytes(dataBytes, contentType: dataCtype, fileDownloadName: "track" + ext);
    }

    // Fakes-mode local fixture (no upstream provider to fetch from) — same ticket
    // mechanism as a real provider URL, just served from disk instead of proxied over HTTP.
    if (url.StartsWith("fixture:", StringComparison.OrdinalIgnoreCase))
    {
        var fixturePath = url["fixture:".Length..];
        if (!File.Exists(fixturePath))
            return Results.NotFound(new { ok = false, error = "Fixture file not found" });
        var fixtureCtype = Path.GetExtension(fixturePath).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };
        var fixtureStream = File.OpenRead(fixturePath);
        return Results.Stream(fixtureStream, contentType: fixtureCtype, fileDownloadName: Path.GetFileName(fixturePath));
    }

    var http = httpFactory.CreateClient("media-proxy");
    HttpResponseMessage? resp = null;
    try
    {
        resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            return Results.Json(new { ok = false, error = $"Upstream HTTP {code}" }, statusCode: code);
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var ctype = resp.Content.Headers.ContentType?.ToString() ?? "video/mp4";
        // Results.Stream has no completion callback — RegisterForDisposeAsync guarantees resp is
        // disposed once the response body finishes writing, on every exit path (success or client abort).
        httpContext.Response.RegisterForDispose(resp);
        return Results.Stream(stream, contentType: ctype, fileDownloadName: "clip.mp4");
    }
    catch (Exception ex)
    {
        resp?.Dispose();
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/demos", async (
    HttpRequest request,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    DemoCatalogService demos,
    ProjectStore store,
    MediaRegistryService media,
    UserDatabaseService userDb,
    DemoYouTubePublisherService youTubePublisher) =>
{
    if (await AuthGate.RequireTermsAcceptedAsync(user, userDb, opts) is { } denied)
        return denied;

    // Uploads go only through this API → shared "Page to Movie" YouTube channel (OAuth on server).
    // Creators never need YouTube Studio; admins alone connect the channel.
    try
    {
        string? title = null;
        string? description = null;
        string? projectId = null;
        var acceptedGuidelines = false;
        var madeForKids = false;
        var isAiSynthetic = true;
        string? privacyStatus = null;
        string? tagsRaw = null;
        // When true and a public demo already exists for this project/user, replace its movie
        // and re-upload to YouTube (V2 pointer replace) instead of creating a new demo entry.
        var replaceExisting = true;
        IFormFile? file = null;

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            title = form["title"].ToString();
            description = form["description"].ToString();
            projectId = form["projectId"].ToString();
            acceptedGuidelines = string.Equals(form["acceptedGuidelines"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                                 || form["acceptedGuidelines"] == "1"
                                 || form["acceptedGuidelines"] == "on";
            madeForKids = string.Equals(form["madeForKids"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (bool.TryParse(form["isAiSynthetic"].ToString(), out var aiForm)) isAiSynthetic = aiForm;
            privacyStatus = form["privacyStatus"].ToString();
            tagsRaw = form["tags"].ToString();
            if (bool.TryParse(form["replaceExisting"].ToString(), out var reForm))
                replaceExisting = reForm;
            else if (string.Equals(form["replaceExisting"].ToString(), "0", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(form["replaceExisting"].ToString(), "false", StringComparison.OrdinalIgnoreCase))
                replaceExisting = false;
            file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        }
        else
        {
            using var doc = await JsonDocument.ParseAsync(request.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t)) title = t.GetString();
            if (root.TryGetProperty("description", out var d)) description = d.GetString();
            if (root.TryGetProperty("projectId", out var p)) projectId = p.GetString();
            if (root.TryGetProperty("acceptedGuidelines", out var ag))
                acceptedGuidelines = ag.ValueKind == JsonValueKind.True
                                     || (ag.ValueKind == JsonValueKind.String
                                         && bool.TryParse(ag.GetString(), out var b) && b);
            if (root.TryGetProperty("madeForKids", out var mfk))
                madeForKids = mfk.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("isAiSynthetic", out var ai))
                isAiSynthetic = ai.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("privacyStatus", out var ps)) privacyStatus = ps.GetString();
            if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.String)
                tagsRaw = tg.GetString();
            if (root.TryGetProperty("replaceExisting", out var re) && re.ValueKind == JsonValueKind.False)
                replaceExisting = false;
        }

        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        // Default unlisted: channel is operated privately; gallery embeds still work.
        // true "private" would hide films from everyone except the channel owner.
        privacyStatus = privacyStatus is "public" or "unlisted" or "private" ? privacyStatus : "unlisted";
        var tags = string.IsNullOrWhiteSpace(tagsRaw)
            ? null
            : tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (!acceptedGuidelines)
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "Accept the gallery guidelines (no NSFW / illegal content) before publishing.",
                code = "guidelines_required",
            });
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Results.BadRequest(new
            {
                ok = false,
                error = "projectId is required to publish a demo",
                code = "project_required",
            });
        }

        await store.RequireProjectAsync(projectId, CancellationToken.None);
        if (!await store.CanUserPublishDemoAsync(projectId, user.UserId, user.IsAdmin, CancellationToken.None))
        {
            return Results.Json(new
            {
                ok = false,
                error =
                    "You can only publish demos for projects you own. " +
                    "Legacy projects without an owner require an admin.",
                code = "project_forbidden",
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        demos.EnsureUserMayPublish(user.UserId, user.IsAdmin);

        DemoCatalogService.DemoEntry entry;
        var autoPublic = false;
        var replacedExisting = false;

        // Item 11: re-publish → attach new movie to existing public demo and V2 YouTube replace.
        var existingPublic = replaceExisting
            ? demos.FindPublicDemoForProject(projectId!, user.UserId)
            : null;
        var canReplace = existingPublic is not null
                         && !string.IsNullOrWhiteSpace(existingPublic.YoutubeId);

        if (file is not null && file.Length > 0)
        {
            var ctHeader = file.ContentType ?? "";
            if (!string.IsNullOrWhiteSpace(ctHeader) &&
                !ctHeader.Contains("video", StringComparison.OrdinalIgnoreCase) &&
                !ctHeader.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) &&
                !ctHeader.Contains("mp4", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    ok = false,
                    error = $"Unsupported content type for demo upload: {ctHeader}",
                    code = "invalid_media_type",
                });
            }

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var sha = MediaRegistryService.HashBytes(bytes);
            autoPublic = await media.IsTrustedShaAsync(projectId!, sha);

            await using var stream = new MemoryStream(bytes);
            if (canReplace)
            {
                entry = await demos.AttachMovieFromStreamAsync(
                    existingPublic!.Id,
                    stream,
                    title ?? existingPublic.Title,
                    description,
                    madeForKids,
                    isAiSynthetic,
                    privacyStatus,
                    tags);
                replacedExisting = true;
                // Always overwrite assets/movie_wip.mp4 on server disk so WIP movie matches the fresh cut!
                try
                {
                    var wipPath = Path.Combine(store.GetProjectDir(projectId!), "assets", "movie_wip.mp4");
                    Directory.CreateDirectory(Path.GetDirectoryName(wipPath)!);
                    await File.WriteAllBytesAsync(wipPath, bytes);
                    try
                    {
                        FilmBuildService.Register(
                            store,
                            projectId!,
                            FilmBuildService.HashBytes(bytes),
                            durationSeconds: 0,
                            segments: null,
                            byteLength: bytes.Length,
                            assemblyWhere: "server");
                    }
                    catch { /* non-fatal film_build */ }
                }
                catch { /* non-fatal */ }

                // Keep public; re-upload to YouTube in background (V2 replace).
                if (!string.Equals(entry.Status, DemoCatalogService.DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
                    demos.SetStatus(entry.Id, DemoCatalogService.DemoStatuses.Public, user.UserId, "Re-publish: YouTube V2 replace");
                entry = demos.TryGet(entry.Id) ?? entry;
                var demoIdForUpload = entry.Id;
                _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
                autoPublic = true; // already public / re-pointing gallery
            }
            else
            {
                entry = await demos.PublishFromStreamAsync(
                    stream,
                    title ?? projectId ?? file.FileName ?? "Demo",
                    description,
                    projectId,
                    user.UserId,
                    acceptedGuidelines: true,
                    madeForKids: madeForKids,
                    isAiSyntheticContent: isAiSynthetic,
                    privacyStatus: privacyStatus,
                    tags: tags);

                demos.SetStatus(entry.Id, DemoCatalogService.DemoStatuses.Public, user.UserId,
                    "Auto-public: creator publish");
                entry = demos.TryGet(entry.Id) ?? entry;

                // Always overwrite assets/movie_wip.mp4 on server disk so WIP movie matches the fresh cut!
                try
                {
                    var wipPath = Path.Combine(store.GetProjectDir(projectId!), "assets", "movie_wip.mp4");
                    Directory.CreateDirectory(Path.GetDirectoryName(wipPath)!);
                    await File.WriteAllBytesAsync(wipPath, bytes);
                    try
                    {
                        FilmBuildService.Register(
                            store,
                            projectId!,
                            FilmBuildService.HashBytes(bytes),
                            durationSeconds: 0,
                            segments: null,
                            byteLength: bytes.Length,
                            assemblyWhere: "server");
                    }
                    catch { /* non-fatal film_build */ }
                }
                catch { /* non-fatal */ }

                try
                {
                    await media.UpsertAsync(
                        projectId!,
                        $"_demos/{entry.Id}/movie.mp4",
                        sha,
                        bytes.LongLength,
                        "demo",
                        scene: null,
                        clip: null,
                        user.UserId);
                }
                catch { /* non-fatal */ }

                var demoIdForUpload = entry.Id;
                _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
            }
        }
        else if (canReplace)
        {
            entry = demos.AttachMovieFromWip(
                existingPublic!.Id,
                projectId!,
                title ?? existingPublic.Title,
                description,
                madeForKids,
                isAiSynthetic,
                privacyStatus,
                tags);
            replacedExisting = true;
            if (!string.Equals(entry.Status, DemoCatalogService.DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
                demos.SetStatus(entry.Id, DemoCatalogService.DemoStatuses.Public, user.UserId, "Re-publish: YouTube V2 replace");
            entry = demos.TryGet(entry.Id) ?? entry;
            var demoIdForUpload = entry.Id;
            _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
            autoPublic = true;
        }
        else
        {
            entry = demos.PublishFromWip(
                projectId,
                title ?? projectId,
                description,
                user.UserId,
                acceptedGuidelines: true,
                madeForKids: madeForKids,
                isAiSyntheticContent: isAiSynthetic,
                privacyStatus: privacyStatus,
                tags: tags);
            // Always push to YouTube — gallery only lists films with a YouTube id.
            var demoIdForUpload = entry.Id;
            _ = Task.Run(() => youTubePublisher.PublishAsync(demoIdForUpload, CancellationToken.None));
            autoPublic = true;
        }

        return Results.Ok(new
        {
            ok = true,
            // No admin review queue — YouTube upload is the gate for the public wall.
            pendingReview = false,
            awaitingYouTube = string.IsNullOrWhiteSpace(entry.YoutubeId),
            autoPublic = true,
            replacedExisting,
            message = replacedExisting
                ? "Updated cut — uploading to YouTube. Gallery shows it when the upload finishes."
                : string.IsNullOrWhiteSpace(entry.YoutubeId)
                    ? "Publishing to YouTube… It appears in the gallery when the upload finishes."
                    : "Film is live on YouTube and in the gallery.",
            demo = DemoPublicDto(entry),
            pagePath = "/demo",
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Report a public demo (any viewer; optional login). Auto-removed after 3 reports.</summary>
app.MapPost("/api/demos/{demoId}/report", (
    string demoId,
    DemoReportRequest? body,
    DemoCatalogService demos,
    IUserContext user) =>
{
    var note = body?.Note;
    var d = demos.Report(demoId, note, user.IsAuthenticated ? user.UserId : null);
    if (d is null)
        return Results.NotFound(new { ok = false, error = "Demo not found" });
    return Results.Ok(new
    {
        ok = true,
        reportCount = d.ReportCount,
        status = d.Status,
        message = d.ReportCount >= 3
            ? "Thanks — this film was queued for re-review."
            : "Thanks — report recorded.",
    });
});

/// <summary>Admin: approve / reject / re-queue a demo (no AI).</summary>
app.MapPost("/api/admin/demos/{demoId}/review", (
    string demoId,
    DemoReviewRequest? body,
    DemoCatalogService demos,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    DemoYouTubePublisherService youTubePublisher) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!user.IsAdmin)
        return Results.Json(new { ok = false, error = "Admin only" }, statusCode: StatusCodes.Status403Forbidden);

    var status = (body?.Status ?? "").Trim().ToLowerInvariant();
    if (status is not (
        DemoCatalogService.DemoStatuses.Public
        or DemoCatalogService.DemoStatuses.Rejected
        or DemoCatalogService.DemoStatuses.Pending
        or DemoCatalogService.DemoStatuses.Removed))
    {
        return Results.BadRequest(new
        {
            ok = false,
            error = "status must be public, rejected, pending, or removed",
        });
    }

    try
    {
        var d = demos.SetStatus(demoId, status, user.UserId, body?.Note);
        if (d is null)
            return Results.NotFound(new { ok = false, error = "Demo not found" });

        // Newly approved, or re-approved with a new local movie (V2 replace) → publish in the background.
        // Publisher no-ops when already on YouTube with no local movie.mp4.
        if (status == DemoCatalogService.DemoStatuses.Public)
            _ = Task.Run(() => youTubePublisher.PublishAsync(demoId, CancellationToken.None));

        return Results.Ok(new { ok = true, demo = DemoAdminDto(d) });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Delete a demo (owner or admin).</summary>
app.MapDelete("/api/demos/{demoId}", (
    string demoId,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    DemoCatalogService demos) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    if (!demos.Delete(demoId, user.UserId, user.IsAdmin))
        return Results.NotFound(new { ok = false, error = "Demo not found or not allowed" });
    return Results.Ok(new { ok = true });
});

/// <summary>Most recent YouTube upload for this project's WIP movie, if any.</summary>
app.MapGet("/api/projects/{id}/movie/youtube", async (string id, ProjectStore store, CancellationToken ct) =>
{
    try
    {
        var info = await store.GetYouTubeUploadInfoAsync(id, ct);
        return Results.Ok(new { ok = true, projectId = id, upload = info });
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
        // url must be a string (or null) — never a bool (breaks System.Text.Json on the client).
        string? wipUrl = f.Exists
            ? $"/api/projects/{Uri.EscapeDataString(id)}/movie/wip"
            : null;
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
            url = wipUrl,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>
/// Register a stitched studio cut: film_build.v1 (EDL + studio.sha256) on project disk + stage commit.
/// Client stitch should POST after producing the WIP blob; server may also call when bytes land.
/// </summary>
app.MapPost("/api/projects/{id}/film-build", async (
    string id,
    HttpRequest request,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var body = await request.ReadFromJsonAsync<FilmBuildRegisterRequest>(cancellationToken: ct);
        if (body is null)
            return Results.BadRequest(new { ok = false, error = "JSON body required" });

        var sha = (body.StudioSha256 ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(sha) && body.HashFromServerWip == true)
        {
            var docFromFile = FilmBuildService.RegisterFromWipFile(store, id, body.StudioPath);
            if (docFromFile is null)
                return Results.BadRequest(new { ok = false, error = "WIP file not found to hash" });
            return Results.Ok(new { ok = true, filmId = docFromFile.FilmId, path = FilmBuildService.RelativePath, filmBuild = docFromFile });
        }

        if (string.IsNullOrWhiteSpace(sha) || sha.Length < 32)
            return Results.BadRequest(new { ok = false, error = "studioSha256 required (or hashFromServerWip=true)" });

        var segments = body.Segments?.Select((s, i) => new FilmBuildSegment
        {
            Index = s.Index >= 0 ? s.Index : i,
            Scene = s.Scene,
            Clip = s.Clip,
            Take = s.Take,
            TStart = s.TStart,
            TEnd = s.TEnd,
            Src = s.Src ?? "",
            SrcSha256 = s.SrcSha256,
            Sidecar = s.Sidecar,
        }).ToList();

        var doc = FilmBuildService.Register(
            store,
            id,
            sha,
            body.DurationSeconds,
            segments,
            body.ByteLength,
            string.IsNullOrWhiteSpace(body.AssemblyWhere) ? "client" : body.AssemblyWhere!);

        if (!string.IsNullOrWhiteSpace(body.StudioPath))
            doc.Studio.Path = body.StudioPath!;

        FilmBuildService.Write(store.GetProjectDir(id), doc);

        return Results.Ok(new
        {
            ok = true,
            filmId = doc.FilmId,
            path = FilmBuildService.RelativePath,
            filmBuild = doc,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/film-build", async (
    string id,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var doc = FilmBuildService.TryRead(store.GetProjectDir(id));
        if (doc is null)
            return Results.Ok(new { ok = true, exists = false, path = FilmBuildService.RelativePath });
        return Results.Ok(new { ok = true, exists = true, path = FilmBuildService.RelativePath, filmBuild = doc });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

/// <summary>Create a learning package from current project artifacts (Stage‑1 + film_build + publish).</summary>
app.MapPost("/api/projects/{id}/learning-package", async (
    string id,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        string? workspace = null;
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "prompts")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "evals")))
                {
                    workspace = dir.FullName;
                    break;
                }
            }
        }
        catch { /* ignore */ }

        var result = LearningPackageService.CreateFromProject(store, id, workspaceRoot: workspace);
        return Results.Ok(new
        {
            ok = true,
            packageId = result.PackageId,
            path = result.ProjectRelativePath,
            labPath = result.LabRelativePath,
            publishPath = result.PublishPath,
            filmId = result.FilmId,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/projects/{id}/learning-packages", async (
    string id,
    ProjectStore store,
    IUserContext user,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(user, opts) is { } denied)
        return denied;
    try
    {
        await store.RequireProjectAsync(id, ct);
        var root = LearningPackageService.PackagesRoot(store.GetProjectDir(id));
        var list = new List<object>();
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
            {
                var pkg = Path.Combine(dir, "package.json");
                if (!File.Exists(pkg)) continue;
                list.Add(new
                {
                    packageId = Path.GetFileName(dir),
                    path = Path.Combine("artifacts", "learning_packages", Path.GetFileName(dir)).Replace('\\', '/'),
                });
            }
        }
        return Results.Ok(new { ok = true, packages = list });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapGet("/api/user/settings", async (
    IUserContext userCtx,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(userCtx, opts) is { } denied)
        return denied;
    try
    {
        var settings = await userDb.GetUserSettingsDtoAsync(userCtx.UserId, ct);
        return Results.Ok(new { ok = true, settings });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/user/settings", async (
    UpdateUserSettingsRequest req,
    IUserContext userCtx,
    UserDatabaseService userDb,
    IOptions<PageToMovieOptions> opts,
    CancellationToken ct) =>
{
    if (AuthGate.RequireLogin(userCtx, opts) is { } denied)
        return denied;
    try
    {
        // Null fields leave existing keys; empty string clears that provider's personal key.
        await userDb.UpdateUserSettingsAsync(userCtx.UserId, req, ct);
        var updated = await userDb.GetUserSettingsDtoAsync(userCtx.UserId, ct);
        var saved = new List<string>();
        if (req.XaiApiKey is not null) saved.Add("xAI / Grok");
        if (req.GeminiApiKey is not null) saved.Add("Gemini");
        if (req.AnthropicApiKey is not null) saved.Add("Claude");
        if (req.FalApiKey is not null) saved.Add("Fal.ai");
        var msg = saved.Count > 0
            ? $"Saved personal key(s): {string.Join(", ", saved)}."
            : "No key fields provided.";
        return Results.Ok(new { ok = true, settings = updated, message = msg });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});
// ── One-Time Startup Migration: catch up every project's schema_version ───────────────────
// ProjectMigrationService already versions each project via a "schema_version" field in
// project.json (mirrors UserDatabaseService's PRAGMA user_version approach for the SQL DB) —
// today it's only invoked from ProjectArchiveService's export/import paths, so a project that's
// never been exported/imported could sit on an old schema indefinitely. Running it for every
// project at startup closes that gap and is how the v1 -> v2 visual_prompt tag migration
// (Camera directive:/Performance:/Optics: -> <Camera>/<Performance>/<Optics>) actually reaches
// existing projects. Idempotent — MigrateIfNeededAsync no-ops once a project is already current.
try
{
    var opts = app.Services.GetRequiredService<IOptions<PageToMovieOptions>>().Value;
    var workspaceRoot = opts.WorkspaceRoot ?? Directory.GetCurrentDirectory();
    var projectsDir = Path.Combine(workspaceRoot, "projects");
    var projectMigrations = app.Services.GetRequiredService<ProjectMigrationService>();

    if (Directory.Exists(projectsDir))
    {
        var migratedCount = 0;
        foreach (var projectJsonPath in Directory.EnumerateFiles(projectsDir, "project.json", SearchOption.AllDirectories))
        {
            var projectDir = Path.GetDirectoryName(projectJsonPath);
            if (string.IsNullOrWhiteSpace(projectDir)) continue;
            try
            {
                if (await projectMigrations.MigrateIfNeededAsync(projectDir))
                    migratedCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Project migration skipped for {projectDir}: {ex.Message}");
            }
        }
        if (migratedCount > 0)
            Console.WriteLine($"Startup schema migration: upgraded {migratedCount} project(s) to {ProjectMigrationService.CurrentSchemaVersion}.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Project schema migration error: {ex.Message}");
}

// Clean up any leftover staged demo movie files under _demos to reclaim server volume space
try
{
    var demosService = app.Services.GetRequiredService<DemoCatalogService>();
    demosService.CleanupStagedDemoMovies();
}
catch { /* non-fatal */ }

// One-time self-heal: legacy demo records may store an email in CreatedBy (before ownership ids
// were normalized to a non-email UserId). Rewrite each to the account's canonical id so the public
// byline shows a handle and ownership checks line up. Idempotent — no-ops once records are clean.
try
{
    var demosService = app.Services.GetRequiredService<DemoCatalogService>();
    var userDb = app.Services.GetRequiredService<UserDatabaseService>();
    var migrated = demosService.MigrateEmailCreatedBy(email =>
    {
        var u = userDb.GetUserByEmailAsync(email).GetAwaiter().GetResult();
        if (u is null) return null;
        return string.IsNullOrWhiteSpace(u.UserId)
            ? (string.IsNullOrWhiteSpace(u.Username) ? null : u.Username.Trim())
            : u.UserId.Trim();
    });
    if (migrated > 0)
        Console.WriteLine($"Startup demo migration: healed CreatedBy on {migrated} demo record(s).");
}
catch (Exception ex)
{
    Console.WriteLine($"Demo CreatedBy migration error: {ex.Message}");
}

app.MapCollaborationEndpoints();
app.MapMergeEndpoints();
app.MapHub<ProjectHub>("/hubs/project");

// ---- Project cost summary (adaptation vs video split) ----
app.MapGet("/api/projects/{id}/costs/summary", (
    string id,
    CostLedgerService ledger,
    ProjectStore store) =>
{
    try
    {
        // Same root convention as ProjectStore itself (WorkspaceRoot/projects) — ContentRootPath
        // would point at the wrong directory whenever PageToMovie__WorkspaceRoot differs from the
        // app's own content root (fakes-mode tests, /data mount in production).
        var root = Path.Combine(store.WorkspaceRoot, "projects");
        var summary = ProjectCostAggregator.BuildSummary(id, root, ledger);
        return Results.Ok(summary);
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/projects/{id}/costs/record", async (
    string id,
    CostLedgerService ledger,
    HttpRequest req,
    CancellationToken ct) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var category = root.TryGetProperty("category", out var c) ? c.GetString() ?? "video" : "video";
        var usd = root.TryGetProperty("usd", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        var note = root.TryGetProperty("note", out var n) ? n.GetString() : null;
        var modelId = root.TryGetProperty("modelId", out var m) ? m.GetString() : null;
        ledger.Record(id, category, usd, note, modelId);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});



// ---- Scene version history ----
app.MapGet("/api/projects/{projectId}/scenes/{sceneKey}/versions", async (
    string projectId,
    string sceneKey,
    PageToMovie.Engine.Collaboration.SceneVersionStore versions,
    CancellationToken ct) =>
{
    var list = await versions.ListHistoryAsync(projectId, sceneKey, ct);
    return Results.Ok(new { ok = true, versions = list });
});

app.MapPost("/api/projects/{projectId}/scenes/{sceneKey}/versions", async (
    string projectId,
    string sceneKey,
    PageToMovie.Engine.Collaboration.SceneVersionStore versions,
    HttpRequest req,
    CancellationToken ct) =>
{
    string? note = null;
    string? createdBy = null;
    string? sceneStateJson = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("note", out var n)) note = n.GetString();
        if (root.TryGetProperty("createdBy", out var c)) createdBy = c.GetString();
        if (root.TryGetProperty("sceneStateJson", out var s)) sceneStateJson = s.GetString();
        else if (root.TryGetProperty("sceneState", out var s2)) sceneStateJson = s2.GetRawText();
    }
    catch { }

    var info = await versions.SnapshotAsync(projectId, sceneKey, sceneStateJson, null, note, createdBy, ct);
    return Results.Ok(new { ok = true, version = info });
});

app.MapPost("/api/projects/{projectId}/scenes/{sceneKey}/versions/{versionId}/restore", async (
    string projectId,
    string sceneKey,
    string versionId,
    PageToMovie.Engine.Collaboration.SceneVersionStore versions,
    CancellationToken ct) =>
{
    var result = await versions.RestoreAsync(projectId, sceneKey, versionId, null, ct);
    if (!result.Ok)
        return Results.BadRequest(new { ok = false, error = result.Error });

    return Results.Ok(new
    {
        ok = true,
        version = result.Version,
        sceneStateJson = result.SceneStateJson,
        restoredFiles = result.RestoredFiles
    });
});


app.MapInviteEndpoints();
app.Run();

namespace PageToMovie.Api
{
    public record AcceptTermsRequest(string UserId, string? Version);
    public record SendInviteApiRequest(string? ProjectId, string? TargetHandle, string? TargetEmail);
    public record AcceptInviteApiRequest(string? Token);
    public record CommitProjectApiRequest(string? Message, bool ForceCommit = false);
    public record PushProjectApiRequest(bool CommitFirst = false, string? Message = null);
    public record SyncOriginApiRequest(string? ParentProjectId, string? AutoResolveStrategy = null);
    public record ProjectVisibilityRequest(string VisibilityMode);
    public record SetBookRefsRequest(List<string>? ImagePaths);
    public record MovieReviewRequest(List<MovieAutoReviewKeyframe>? Keyframes);
    public record RegisterYouTubeDemoRequest(string? YoutubeIdOrUrl, string? Title, string? Description, string? ProjectId);
    // Expose entry assembly for WebApplicationFactory integration tests.
    public partial class Program { }
}



file sealed class FilmBuildRegisterRequest
{
    public string? StudioSha256 { get; set; }
    public double DurationSeconds { get; set; }
    public long? ByteLength { get; set; }
    public string? StudioPath { get; set; }
    public string? AssemblyWhere { get; set; }
    public bool? HashFromServerWip { get; set; }
    public List<FilmBuildSegmentDto>? Segments { get; set; }
}

file sealed class FilmBuildSegmentDto
{
    public int Index { get; set; } = -1;
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public int? Take { get; set; }
    public double TStart { get; set; }
    public double TEnd { get; set; }
    public string? Src { get; set; }
    public string? SrcSha256 { get; set; }
    public string? Sidecar { get; set; }
}
