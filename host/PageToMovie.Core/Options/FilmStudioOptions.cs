namespace PageToMovie.Core.Options;

public sealed class PageToMovieOptions
{
    public const string SectionName = "PageToMovie";

    /// <summary>Repo / workspace root containing projects/ and prompts/.</summary>
    public string WorkspaceRoot { get; set; } = "";

    public string DefaultModel { get; set; } = "grok-imagine-video";
    public string DefaultImageModel { get; set; } = "grok-imagine-image-quality";
    /// <summary>
    /// Image backend for character portraits: grok | gemini.
    /// Also inferred from DefaultImageModel / project image_model_name when empty.
    /// </summary>
    public string ImageProvider { get; set; } = "grok";
    public string DefaultResolution { get; set; } = "480p";
    public int DefaultDurationSeconds { get; set; } = 6;
    public int GrokPollSeconds { get; set; } = 5;
    public int GrokTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// ffmpeg executable for scene remux / WIP.
    /// Empty → auto: NuGet Soenneker Resources/ffmpeg.exe, then PATH.
    /// Can be an absolute path or path relative to the API output directory.
    /// </summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>When true, DI registers fake Grok clients (no xAI spend).</summary>
    public bool UseFakes { get; set; }

    /// <summary>
    /// When true (default), vision-check character portraits before lock so a photoreal
    /// project cannot lock a sketch/illustration (and vice versa). Set false only for
    /// emergency bypass: <c>PageToMovie__RequirePortraitStyleGate=false</c>.
    /// </summary>
    public bool RequirePortraitStyleGate { get; set; } = true;

    /// <summary>
    /// When true (default), start a fresh gen with locked character refs whenever the
    /// on-screen cast set changes vs the previous clip (instead of video-extend).
    /// Extends still drop API refs; reseed restores identity plates mid-scene.
    /// Env: PageToMovie__IdentityReseedOnCastChange=false to always extend clip 2+.
    /// </summary>
    public bool IdentityReseedOnCastChange { get; set; } = true;

    /// <summary>
    /// When true (default), enable scene-list + project/blueprint/dir read caches.
    /// Set false for A/B soaks: <c>PageToMovie__EnableReadCaches=false</c>.
    /// </summary>
    public bool EnableReadCaches { get; set; } = true;

    /// <summary>
    /// When true (default), cache chat completions on disk under <c>.PageToMovie/chat_cache/</c>,
    /// keyed by a hash of (model, temperature, system prompt, user prompt). A repeated classifier
    /// call with byte-identical input skips the network round-trip entirely — free, instant, and
    /// (unlike a live call) exactly reproducible across reruns, which matters for evals and
    /// re-planning after an unrelated edit. Only caches temperature ≈ 0 calls by default — see
    /// <see cref="ChatCacheNonZeroTemperature"/>. Env: <c>PageToMovie__ChatCacheEnabled=false</c>.
    /// </summary>
    public bool ChatCacheEnabled { get; set; } = true;

    /// <summary>
    /// When true, also cache chat completions requested at temperature &gt; 0. Off by default:
    /// nonzero temperature is normally requested precisely to get varied responses across calls,
    /// so caching it would silently defeat the caller's intent.
    /// </summary>
    public bool ChatCacheNonZeroTemperature { get; set; }

    /// <summary>
    /// Cache key salt, folded into every cache key alongside model/temperature/prompts. The
    /// cache has no way to detect that a provider quietly changed a model's behavior under an
    /// unchanged model id (retrained weights, endpoint/schema change, etc.) — bump this string
    /// when that happens and every existing entry stops matching, effectively invalidating the
    /// whole cache without deleting files by hand. <c>POST /api/admin/chat-cache/clear</c>
    /// deletes them outright if you'd rather reclaim the disk space immediately.
    /// </summary>
    public string ChatCacheVersion { get; set; } = "1";

    /// <summary>
    /// When true (default), batch-classify silent beat <c>action_class</c> via chat at shot-plan
    /// time for duration budgeting. On failure: retry then heuristic fallback.
    /// Env: <c>PageToMovie__ClassifySilentBeatsWithChat=false</c>.
    /// </summary>
    public bool ClassifySilentBeatsWithChat { get; set; } = true;

    /// <summary>Chat model for silent beat classify (compare via BeatLabelEval).</summary>
    public string SilentBeatClassifyModel { get; set; } = "grok-4.5";

    /// <summary>Sampling temperature for silent beat classify (0 = most stable).</summary>
    public double SilentBeatClassifyTemperature { get; set; } = 0.0;

    /// <summary>Max chat attempts per batch (1 try + retries) before heuristic fallback.</summary>
    public int SilentBeatClassifyMaxAttempts { get; set; } = 3;

    /// <summary>Base ms for quadratic backoff between classify retries (tests may set 0).</summary>
    public int SilentBeatClassifyBackoffBaseMs { get; set; } = 400;

    public bool ClassifyAmbientSfxWithChat { get; set; } = true;
    public string AmbientSfxClassifyModel { get; set; } = "grok-4.5";
    public double AmbientSfxClassifyTemperature { get; set; } = 0.2;
    public int AmbientSfxClassifyMaxAttempts { get; set; } = 3;

    public bool ClassifyOnScreenCastWithChat { get; set; } = true;
    public string OnScreenCastClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyExtendCutWithChat { get; set; } = true;
    public string ExtendCutClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifySpeciesKindWithChat { get; set; } = true;
    public string SpeciesKindClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyPlateRankWithChat { get; set; } = true;
    public string PlateRankClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyShotPlanRefineWithChat { get; set; } = true;
    public string ShotPlanRefineClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyBeatPacingWithChat { get; set; } = true;
    public string BeatPacingClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyCinematicLightingWithChat { get; set; } = true;
    public string CinematicLightingClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyCameraDirectorWithChat { get; set; } = true;
    public string CameraDirectorClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyNegativePromptWithChat { get; set; } = true;
    public string NegativePromptClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyWardrobeContinuityWithChat { get; set; } = true;
    public string WardrobeContinuityClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyCharacterEmotionArcWithChat { get; set; } = true;
    public string CharacterEmotionArcClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifySoundDesignComposerWithChat { get; set; } = true;
    public string SoundDesignComposerClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyDepthOfFieldWithChat { get; set; } = true;
    public string DepthOfFieldClassifyModel { get; set; } = "grok-4.5";

    public bool ClassifyColorPaletteGradingWithChat { get; set; } = true;
    public string ColorPaletteGradingClassifyModel { get; set; } = "grok-4.5";

    /// <summary>
    /// Optional ThreadPool min-thread ramp for multi-user / LoadSim ready-barrier.
    /// Leave defaults (0) unless soaks show global latency floors under concurrent clients.
    /// Env: <c>PageToMovie__ThreadPool__MinWorkerThreads=64</c>.
    /// </summary>
    public ThreadPoolOptions ThreadPool { get; set; } = new();

    public CapacityOptions Capacity { get; set; } = new();
    public FakesOptions Fakes { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public YouTubeOptions YouTube { get; set; } = new();
    public CreditsOptions Credits { get; set; } = new();
}

/// <summary>
/// OAuth2 client for uploading the WIP movie to YouTube (Videos.Insert, youtube.upload
/// scope). Create an "OAuth client ID" (Web application) in Google Cloud Console → APIs
/// &amp; Services → Credentials, with <see cref="RedirectUri"/> added as an authorized
/// redirect URI. One shared channel connection per PageToMovie instance (admin-managed via
/// POST /api/youtube/connect), not per-user.
/// </summary>
public sealed class YouTubeOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Must exactly match an authorized redirect URI on the OAuth client, e.g. https://host/api/youtube/oauth2callback.</summary>
    public string RedirectUri { get; set; } = "";
}

/// <summary>Options for automatic end credits clip generation and movie WIP appending.</summary>
public sealed class CreditsOptions
{
    public bool AutoAppendCredits { get; set; } = true;
    public string SoftwareName { get; set; } = "PageToMovie";
    public string SoftwareAuthor { get; set; } = "Bud Cribar";
    public string RepositoryUrl { get; set; } = "https://github.com/budcribar/PageToMovie";
    public string FairUseNotice { get; set; } = "Produced under Fair Use and Public Domain for Non-Commercial Creative Purposes.";
}

/// <summary>ThreadPool pre-warm for sudden multi-user load (optional).</summary>
public sealed class ThreadPoolOptions
{
    /// <summary>
    /// Minimum worker threads. 0 = leave CLR default (no change).
    /// Typical experiment: 32–64 on a 16-core host (≈2–4× ProcessorCount).
    /// </summary>
    public int MinWorkerThreads { get; set; }

    /// <summary>
    /// Minimum I/O completion port threads. 0 = same as <see cref="MinWorkerThreads"/> when that is set,
    /// otherwise leave CLR default.
    /// </summary>
    public int MinIoThreads { get; set; }
}

/// <summary>User identity, per-user API keys, admin login (Phase B).</summary>
public sealed class AuthOptions
{
    /// <summary>Default user id when no X-User-Id / JWT (single-operator mode).</summary>
    public string DefaultUserId { get; set; } = "local";

    /// <summary>User ids that receive admin role (in addition to admin login).</summary>
    public List<string> AdminUserIds { get; set; } = new();

    /// <summary>Map userId → xAI API key (optional; env USERKEY_{id} also works).</summary>
    public Dictionary<string, string> UserApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Admin login username (POST /api/auth/login).</summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// Admin password for dev. Prefer <see cref="AdminPasswordHash"/> or env PageToMovie_ADMIN_PASSWORD.
    /// </summary>
    public string AdminPassword { get; set; } = "";

    /// <summary>ASP.NET Core Identity v3 password hash (optional).</summary>
    public string AdminPasswordHash { get; set; } = "";

    /// <summary>
    /// Insecure development-only default. Host must refuse this outside Development
    /// (see PageToMovie.Api startup + PageToMovie_JWT_KEY).
    /// </summary>
    public const string DefaultDevJwtSigningKey = "PageToMovie-Dev-Only-Change-Me-32chars!!";

    /// <summary>JWT signing key (min 32 chars). Env PageToMovie_JWT_KEY overrides.</summary>
    public string JwtSigningKey { get; set; } = DefaultDevJwtSigningKey;

    public int JwtHours { get; set; } = 8;

    /// <summary>Development only: accept any password for admin username.</summary>
    public bool AllowDevBypass { get; set; }

    /// <summary>True when the effective key is missing or still the committed dev default.</summary>
    public static bool IsInsecureDefaultJwtSigningKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ||
        string.Equals(key.Trim(), DefaultDevJwtSigningKey, StringComparison.Ordinal);
}

/// <summary>Server-side concurrency caps (Phase A+; multi-worker in later phases).</summary>
public sealed class CapacityOptions
{
    /// <summary>
    /// Max concurrent video/API jobs on this host.
    /// Default 4: compromise for multi-user browse+gen (8+ starves browse on a single box).
    /// </summary>
    public int MaxVideoInFlight { get; set; } = 4;
    /// <summary>Max concurrent video jobs per user (fairness; default 1).</summary>
    public int MaxVideoInFlightPerUser { get; set; } = 1;
    public int MaxFfmpegInFlight { get; set; } = 2;
    public int MaxQueuePerUser { get; set; } = 5;
}

/// <summary>Fake client knobs when <see cref="PageToMovieOptions.UseFakes"/> is true.</summary>
public sealed class FakesOptions
{
    /// <summary>MergeRealistic | LoadLight</summary>
    public string VideoMode { get; set; } = "MergeRealistic";
    public int VideoDelayMs { get; set; } = 200;
    /// <summary>0–1 probability of synthetic failure after delay.</summary>
    public double FailRate { get; set; }
    /// <summary>Throw rate-limit every N submits (0 = never).</summary>
    public int RateLimitEveryN { get; set; }
}
