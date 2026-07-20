namespace FilmStudio.Core.Options;

public sealed class FilmStudioOptions
{
    public const string SectionName = "FilmStudio";

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
    /// emergency bypass: <c>FilmStudio__RequirePortraitStyleGate=false</c>.
    /// </summary>
    public bool RequirePortraitStyleGate { get; set; } = true;

    /// <summary>
    /// When true (default), enable scene-list + project/blueprint/dir read caches.
    /// Set false for A/B soaks: <c>FilmStudio__EnableReadCaches=false</c>.
    /// </summary>
    public bool EnableReadCaches { get; set; } = true;

    /// <summary>
    /// Optional ThreadPool min-thread ramp for multi-user / LoadSim ready-barrier.
    /// Leave defaults (0) unless soaks show global latency floors under concurrent clients.
    /// Env: <c>FilmStudio__ThreadPool__MinWorkerThreads=64</c>.
    /// </summary>
    public ThreadPoolOptions ThreadPool { get; set; } = new();

    public CapacityOptions Capacity { get; set; } = new();
    public FakesOptions Fakes { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
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
    /// Admin password for dev. Prefer <see cref="AdminPasswordHash"/> or env FILMSTUDIO_ADMIN_PASSWORD.
    /// </summary>
    public string AdminPassword { get; set; } = "";

    /// <summary>ASP.NET Core Identity v3 password hash (optional).</summary>
    public string AdminPasswordHash { get; set; } = "";

    /// <summary>JWT signing key (min 32 chars). Env FILMSTUDIO_JWT_KEY overrides.</summary>
    public string JwtSigningKey { get; set; } = "FilmStudio-Dev-Only-Change-Me-32chars!!";

    public int JwtHours { get; set; } = 8;

    /// <summary>Development only: accept any password for admin username.</summary>
    public bool AllowDevBypass { get; set; }
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

/// <summary>Fake client knobs when <see cref="FilmStudioOptions.UseFakes"/> is true.</summary>
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
