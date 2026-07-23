namespace FilmStudio.Core.Models;

/// <summary>
/// What the model is used for in Film Studio (drives Configuration dropdowns).
/// </summary>
public enum ModelCapability
{
    Video,
    Image,
    Chat,
    Vision,
}

/// <summary>
/// Backend family — maps to API base URL + required env keys.
/// User never picks this; it is derived from the model id via the catalog.
/// </summary>
public enum ModelProviderFamily
{
    /// <summary>xAI (api.x.ai) — <c>XAI_API_KEY</c>.</summary>
    Xai = 0,
    /// <summary>Google Gemini (reserved; not fully wired yet).</summary>
    Google = 1,
    /// <summary>Anthropic Claude (reserved; not fully wired yet).</summary>
    Anthropic = 2,
}

/// <summary>
/// One supported model. Only entries with <see cref="Enabled"/> true appear in user pickers.
/// Wishlist / not-yet-wired models stay off the list and can be tracked as GitHub feature requests.
/// </summary>
public sealed class SupportedModelEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ModelCapability Capability { get; init; }
    public required ModelProviderFamily Provider { get; init; }

    /// <summary>API origin, e.g. <c>https://api.x.ai/v1</c>.</summary>
    public required string ApiBase { get; init; }

    /// <summary>
    /// Primary relative path under <see cref="ApiBase"/> (e.g. <c>videos/generations</c>).
    /// Extensions / alternate routes stay in the client; this is the capability home.
    /// </summary>
    public required string EndpointPath { get; init; }

    /// <summary>Env var names that must be set (e.g. <c>XAI_API_KEY</c>).</summary>
    public required IReadOnlyList<string> RequiredEnvKeys { get; init; }

    /// <summary>When false, hidden from Configuration pickers.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Context window (max input tokens), for callers that need to budget large prompts against
    /// the actual model — e.g. book-to-screenplay chunking. Null for models where this isn't a
    /// meaningful concept (video/image) or isn't verified yet. Sourced from provider docs as of
    /// 2026-07; providers do increase these over time, so re-check before trusting an old number
    /// for a cost/quality-sensitive decision.
    /// </summary>
    public int? MaxInputTokens { get; init; }

    /// <summary>USD per 1,000,000 input tokens (Chat / Vision only). Null when not applicable.</summary>
    public double? InputCostPerMillionTokens { get; init; }

    /// <summary>USD per 1,000,000 output tokens (Chat / Vision only). Null when not applicable.</summary>
    public double? OutputCostPerMillionTokens { get; init; }

    /// <summary>
    /// USD per second of generated output, by resolution (Video only) — same key convention
    /// ("480p" / "720p" / "1080p") as the project-level <c>cost_estimates.video_output_per_sec</c>
    /// table in Configuration. That table is an operator-editable planning estimate for whichever
    /// video model is active; this is the catalog's own reference price per model, and a given
    /// model may not price every resolution (only confirmed keys are present). Null when no
    /// per-resolution pricing applies (non-video capabilities).
    /// </summary>
    public IReadOnlyDictionary<string, double>? VideoCostPerSecondByResolution { get; init; }

    /// <summary>USD per generated image (Image only). Null when not applicable.</summary>
    public double? ImageCostPerImage { get; init; }

    public string? Notes { get; init; }

    /// <summary>
    /// Optional link to a GitHub issue / feature request for models we plan to support.
    /// Prefer leaving unsupported models out of the enabled list and tracking them on GitHub.
    /// </summary>
    public string? FeatureRequestUrl { get; init; }

    /// <summary>
    /// When true (default for Grok Imagine Video), clip 2+ can continue via video-extend.
    /// False for providers that only support text/image-to-video (e.g. Veo today).
    /// </summary>
    public bool SupportsVideoContinue { get; init; } = true;

    /// <summary>
    /// When true, locked character reference plates can be attached on fresh gen.
    /// False for backends that reject multi-image / reference conditioning.
    /// </summary>
    public bool SupportsReferenceImages { get; init; } = true;

    /// <summary>Provider id for config / cost reports (<c>grok</c>, <c>gemini</c>, <c>anthropic</c>).</summary>
    public string ProviderId => Provider switch
    {
        ModelProviderFamily.Google => "gemini",
        ModelProviderFamily.Anthropic => "anthropic",
        _ => "grok",
    };
}

/// <summary>
/// Master list of models Film Studio knows how to call.
/// User picks <see cref="SupportedModelEntry.Id"/> only; app resolves endpoint + keys.
/// </summary>
public static class SupportedModelCatalog
{
    public const string XaiApiBase = "https://api.x.ai/v1";
    public const string XaiApiKeyEnv = "XAI_API_KEY";

    /// <summary>Google Gemini API. No client wired yet — see notes on entries below.</summary>
    public const string GoogleApiBase = "https://generativelanguage.googleapis.com/v1beta";
    public const string GoogleApiKeyEnv = "GEMINI_API_KEY";

    /// <summary>Anthropic Messages API. No client wired yet — see notes on entries below.</summary>
    public const string AnthropicApiBase = "https://api.anthropic.com/v1";
    public const string AnthropicApiKeyEnv = "ANTHROPIC_API_KEY";

    private static readonly SupportedModelEntry[] All =
    [
        // ── Video ──────────────────────────────────────────────────────────
        new()
        {
            Id = "grok-imagine-video",
            DisplayName = "Grok Imagine Video",
            Capability = ModelCapability.Video,
            Provider = ModelProviderFamily.Xai,
            ApiBase = XaiApiBase,
            EndpointPath = "videos/generations",
            RequiredEnvKeys = [XaiApiKeyEnv],
            // xAI docs, 2026-07 — matches this project's own Configuration → Cost estimates
            // defaults exactly.
            VideoCostPerSecondByResolution = new Dictionary<string, double>
            {
                ["480p"] = 0.05,
                ["720p"] = 0.07,
                ["1080p"] = 0.25,
            },
            Notes = "Also uses videos/extensions for clip continue.",
            SupportsVideoContinue = true,
            SupportsReferenceImages = true,
        },
        new()
        {
            Id = "veo-3.1",
            DisplayName = "Google Veo 3.1",
            Capability = ModelCapability.Video,
            Provider = ModelProviderFamily.Google,
            ApiBase = GoogleApiBase,
            EndpointPath = "models/veo-3.1:predictLongRunning",
            RequiredEnvKeys = [GoogleApiKeyEnv],
            // Google AI pricing, 2026-07: Standard quality — same $0.40/sec at both 720p and
            // 1080p. Lite ($0.05/sec) and Fast ($0.10/sec) quality tiers exist but aren't
            // separately selectable in this catalog (only one veo-3.1 entry).
            VideoCostPerSecondByResolution = new Dictionary<string, double>
            {
                ["720p"] = 0.40,
                ["1080p"] = 0.40,
            },
            // Fail-closed: multi-clip scenes and locked cast plates need these.
            SupportsVideoContinue = false,
            SupportsReferenceImages = false,
            Notes = "Wired via GeminiVideoClient (text/image-to-video only). No clip-to-clip continue " +
                    "and no locked character reference plates — multi-clip scenes and cast-gated gen " +
                    "require Grok Imagine Video. Not smoke-tested against a live account yet. " +
                    "$0.40/sec Standard (720p/1080p).",
            FeatureRequestUrl = "https://github.com/budcribar/FilmStudio/issues",
        },

        // ── Image / portraits ──────────────────────────────────────────────
        new()
        {
            Id = "grok-imagine-image-quality",
            DisplayName = "Grok Imagine Image (quality)",
            Capability = ModelCapability.Image,
            Provider = ModelProviderFamily.Xai,
            ApiBase = XaiApiBase,
            EndpointPath = "images/generations",
            RequiredEnvKeys = [XaiApiKeyEnv],
            ImageCostPerImage = 0.05, // xAI docs, 2026-07 (1K); 2K is $0.07
            Notes = "Edits use the multi-image edit path on the same family. $0.05/image (1K), " +
                    "$0.07/image (2K).",
        },
        new()
        {
            Id = "grok-imagine-image",
            DisplayName = "Grok Imagine Image",
            Capability = ModelCapability.Image,
            Provider = ModelProviderFamily.Xai,
            ApiBase = XaiApiBase,
            EndpointPath = "images/generations",
            RequiredEnvKeys = [XaiApiKeyEnv],
            ImageCostPerImage = 0.02, // xAI docs, 2026-07 (1K and 2K both $0.02)
        },
        new()
        {
            Id = "gemini-3-pro-image",
            DisplayName = "Gemini 3 Pro Image",
            Capability = ModelCapability.Image,
            Provider = ModelProviderFamily.Google,
            ApiBase = GoogleApiBase,
            EndpointPath = "models/gemini-3-pro-image:generateContent",
            RequiredEnvKeys = [GoogleApiKeyEnv],
            ImageCostPerImage = 0.134, // Google pricing, 2026-07 (1K/2K); 4K is $0.24
            Notes = "Wired via GeminiImageClient. Supports up to 14 reference images (see " +
                    "ImageApiLimits.cs), vs. Grok's 3. Response-shape parsing is not smoke-tested " +
                    "against a live account yet. $0.134/image (1K/2K), $0.24/image (4K).",
            FeatureRequestUrl = "https://github.com/budcribar/FilmStudio/issues",
        },
        // Note: no Claude/Anthropic entry here on purpose — Anthropic does not offer an image
        // generation API, so there is no real backend this could ever call. Claude is added
        // below under Chat instead, where it's a real, callable capability.

        // ── Chat / planning / scrub ────────────────────────────────────────
        new()
        {
            Id = "grok-4.5",
            DisplayName = "Grok 4.5",
            Capability = ModelCapability.Chat,
            Provider = ModelProviderFamily.Xai,
            ApiBase = XaiApiBase,
            EndpointPath = "chat/completions",
            RequiredEnvKeys = [XaiApiKeyEnv],
            MaxInputTokens = 500_000, // xAI docs, 2026-07: docs.x.ai/developers/models/grok-4.5
            // xAI docs, 2026-07: base tier (<200k tokens in request). Tiered rate above 200k
            // doubles to $4/$12 per docs.x.ai/developers/pricing.
            InputCostPerMillionTokens = 2.00,
            OutputCostPerMillionTokens = 6.00,
            Notes = "Stage planning, cast scrub, screenplay helpers. $2/$6 per 1M in/out tokens " +
                    "under 200k-token requests; $4/$12 above that threshold.",
        },
        new()
        {
            Id = "grok-4",
            DisplayName = "Grok 4",
            Capability = ModelCapability.Chat,
            Provider = ModelProviderFamily.Xai,
            ApiBase = XaiApiBase,
            EndpointPath = "chat/completions",
            RequiredEnvKeys = [XaiApiKeyEnv],
            MaxInputTokens = 256_000, // OpenRouter x-ai/grok-4 listing, 2026-07
            InputCostPerMillionTokens = 3.00, // OpenRouter x-ai/grok-4, 2026-07
            OutputCostPerMillionTokens = 15.00,
        },
        new()
        {
            Id = "claude-sonnet-5",
            DisplayName = "Claude Sonnet 5",
            Capability = ModelCapability.Chat,
            Provider = ModelProviderFamily.Anthropic,
            ApiBase = AnthropicApiBase,
            EndpointPath = "messages",
            RequiredEnvKeys = [AnthropicApiKeyEnv],
            // Anthropic docs, 2026-07: platform.claude.com/docs — 1M is both default and max,
            // no smaller context variant. (128k max output, separate from this input figure.)
            MaxInputTokens = 1_000_000,
            // Introductory pricing, active through 2026-08-31 per Anthropic docs; standard
            // pricing after that is $3/$15 per 1M in/out tokens (same page).
            InputCostPerMillionTokens = 2.00,
            OutputCostPerMillionTokens = 10.00,
            Notes = "Wired via AnthropicChatClient, routed automatically through " +
                    "MultiProviderChatClient for planning/QA calls. $2/$10 per 1M in/out tokens " +
                    "(introductory, through 2026-08-31); $3/$15 standard after.",
            FeatureRequestUrl = "https://github.com/budcribar/FilmStudio/issues",
        },
        new()
        {
            Id = "gemini-3-pro",
            DisplayName = "Gemini 3 Pro",
            Capability = ModelCapability.Chat,
            Provider = ModelProviderFamily.Google,
            ApiBase = GoogleApiBase,
            EndpointPath = "models/gemini-3-pro:generateContent",
            RequiredEnvKeys = [GoogleApiKeyEnv],
            MaxInputTokens = 1_000_000, // Google AI docs, 2026-07 (64k max output, separate)
            // Google AI pricing, 2026-07: base tier (<200k tokens). Above 200k: $4/$18.
            InputCostPerMillionTokens = 2.00,
            OutputCostPerMillionTokens = 12.00,
            Notes = "Wired via GeminiChatClient, routed automatically through " +
                    "MultiProviderChatClient for planning/QA calls. Response-shape parsing is not " +
                    "smoke-tested against a live account yet. $2/$12 per 1M in/out tokens under " +
                    "200k-token requests; $4/$18 above that threshold.",
            FeatureRequestUrl = "https://github.com/budcribar/FilmStudio/issues",
        },

        // ── Vision (same chat models with image input; listed for QA config) ─
        new()
        {
            Id = "grok-4.5",
            DisplayName = "Grok 4.5 (vision)",
            Capability = ModelCapability.Vision,
            Provider = ModelProviderFamily.Xai,
            ApiBase = XaiApiBase,
            EndpointPath = "chat/completions",
            RequiredEnvKeys = [XaiApiKeyEnv],
            MaxInputTokens = 500_000,
            InputCostPerMillionTokens = 2.00, // same rate as the chat entry — same underlying model
            OutputCostPerMillionTokens = 6.00,
            Notes = "Book plates / frame QA when wired.",
        },
        new()
        {
            Id = "claude-sonnet-5",
            DisplayName = "Claude Sonnet 5 (vision)",
            Capability = ModelCapability.Vision,
            Provider = ModelProviderFamily.Anthropic,
            ApiBase = AnthropicApiBase,
            EndpointPath = "messages",
            RequiredEnvKeys = [AnthropicApiKeyEnv],
            MaxInputTokens = 1_000_000,
            InputCostPerMillionTokens = 2.00, // same rate as the chat entry — same underlying model
            OutputCostPerMillionTokens = 10.00,
            Notes = "Wired for clip/frame review (CompleteWithImagesAsync) via " +
                    "MultiProviderVisionClient. Book-page OCR and cast classify still run on Grok " +
                    "only — those two methods are not implemented for Anthropic.",
            FeatureRequestUrl = "https://github.com/budcribar/FilmStudio/issues",
        },
        new()
        {
            Id = "gemini-3-pro",
            DisplayName = "Gemini 3 Pro (vision)",
            Capability = ModelCapability.Vision,
            Provider = ModelProviderFamily.Google,
            ApiBase = GoogleApiBase,
            EndpointPath = "models/gemini-3-pro:generateContent",
            RequiredEnvKeys = [GoogleApiKeyEnv],
            MaxInputTokens = 1_000_000,
            InputCostPerMillionTokens = 2.00, // same rate as the chat entry — same underlying model
            OutputCostPerMillionTokens = 12.00,
            Notes = "Wired for clip/frame review (CompleteWithImagesAsync) via " +
                    "MultiProviderVisionClient. Book-page OCR and cast classify still run on Grok " +
                    "only — those two methods are not implemented for Gemini. Response-shape " +
                    "parsing is not smoke-tested against a live account yet.",
            FeatureRequestUrl = "https://github.com/budcribar/FilmStudio/issues",
        },

        // Film character voice samples use video (VOICE LOCK), not a separate TTS model.
    ];

    /// <summary>All catalog rows (enabled + disabled).</summary>
    public static IReadOnlyList<SupportedModelEntry> Entries => All;

    public static IReadOnlyList<SupportedModelEntry> ForCapability(
        ModelCapability capability,
        bool enabledOnly = true) =>
        All.Where(e => e.Capability == capability && (!enabledOnly || e.Enabled)).ToList();

    public static SupportedModelEntry? Find(string? modelId, ModelCapability? capability = null)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var id = modelId.Trim();
        var exact = All.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) return null;

        if (capability is not { } cap)
            return exact[0];

        var match = exact.FirstOrDefault(e => e.Capability == cap);
        if (match is not null) return match;

        // Only share Chat ↔ Vision for the same model id (e.g. grok-4.5).
        // Do not return a video model when the caller asked for chat/image/etc.
        if (cap is ModelCapability.Chat or ModelCapability.Vision)
        {
            return exact.FirstOrDefault(e =>
                e.Capability is ModelCapability.Chat or ModelCapability.Vision);
        }

        return null;
    }

    /// <summary>
    /// Resolve a configured model id for a capability, or a safe default.
    /// Unknown ids: keep the string (forward-compatible) but provider metadata falls back to Xai.
    /// </summary>
    public static SupportedModelEntry ResolveOrDefault(
        string? modelId,
        ModelCapability capability,
        string? fallbackId = null)
    {
        var hit = Find(modelId, capability);
        if (hit is not null) return hit;

        // Known id under a different capability (e.g. video id for chat) → do not keep that id.
        // Truly unknown id → keep the string (forward-compatible) with Xai defaults.
        var knownUnderAnyCap = !string.IsNullOrWhiteSpace(modelId) && Find(modelId) is not null;
        if (!string.IsNullOrWhiteSpace(modelId) && !knownUnderAnyCap)
        {
            var id = modelId.Trim();
            return MakeSynthetic(id, capability);
        }

        if (!string.IsNullOrWhiteSpace(fallbackId))
        {
            hit = Find(fallbackId, capability);
            if (hit is not null) return hit;
        }

        hit = ForCapability(capability).FirstOrDefault();
        if (hit is not null) return hit;

        return MakeSynthetic(
            string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId.Trim(),
            capability);
    }

    private static SupportedModelEntry MakeSynthetic(string id, ModelCapability capability) => new()
    {
        Id = id,
        DisplayName = id,
        Capability = capability,
        Provider = ModelProviderFamily.Xai,
        ApiBase = XaiApiBase,
        EndpointPath = capability switch
        {
            ModelCapability.Video => "videos/generations",
            ModelCapability.Image => "images/generations",
            _ => "chat/completions",
        },
        RequiredEnvKeys = [XaiApiKeyEnv],
        Enabled = false,
        Notes = "Not in master catalog — add via PR or track as GitHub feature request.",
    };

    /// <summary>Provider string for project config / cost UI.</summary>
    public static string ProviderIdFor(string? modelId, ModelCapability capability) =>
        ResolveOrDefault(modelId, capability).ProviderId;

    /// <summary>Missing env keys for this model (empty if ready).</summary>
    public static IReadOnlyList<string> MissingEnvKeys(SupportedModelEntry model)
    {
        var missing = new List<string>();
        foreach (var key in model.RequiredEnvKeys)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                missing.Add(key);
        }
        return missing;
    }

    /// <summary>DTO list for API / Configuration UI.</summary>
    public static IReadOnlyList<SupportedModelDto> ToDtoList(bool enabledOnly = true) =>
        All.Where(e => !enabledOnly || e.Enabled)
            .Select(ToDto)
            .ToList();

    public static SupportedModelDto ToDto(SupportedModelEntry e) => new()
    {
        Id = e.Id,
        DisplayName = e.DisplayName,
        Capability = e.Capability.ToString().ToLowerInvariant(),
        Provider = e.Provider.ToString().ToLowerInvariant(),
        ApiBase = e.ApiBase,
        EndpointPath = e.EndpointPath,
        RequiredEnvKeys = e.RequiredEnvKeys.ToList(),
        Enabled = e.Enabled,
        MaxInputTokens = e.MaxInputTokens,
        InputCostPerMillionTokens = e.InputCostPerMillionTokens,
        OutputCostPerMillionTokens = e.OutputCostPerMillionTokens,
        VideoCostPerSecondByResolution = e.VideoCostPerSecondByResolution is { } v
            ? new Dictionary<string, double>(v)
            : null,
        ImageCostPerImage = e.ImageCostPerImage,
        Notes = e.Notes,
        FeatureRequestUrl = e.FeatureRequestUrl,
        ProviderId = e.ProviderId,
        SupportsVideoContinue = e.SupportsVideoContinue,
        SupportsReferenceImages = e.SupportsReferenceImages,
    };
}

/// <summary>JSON-friendly model catalog row for the API and Web UI.</summary>
public sealed class SupportedModelDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Capability { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ApiBase { get; set; } = "";
    public string EndpointPath { get; set; } = "";
    public List<string> RequiredEnvKeys { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int? MaxInputTokens { get; set; }
    public double? InputCostPerMillionTokens { get; set; }
    public double? OutputCostPerMillionTokens { get; set; }
    public Dictionary<string, double>? VideoCostPerSecondByResolution { get; set; }
    public double? ImageCostPerImage { get; set; }
    public string? Notes { get; set; }
    public string? FeatureRequestUrl { get; set; }
    public string? ProviderId { get; set; }
    public bool SupportsVideoContinue { get; set; } = true;
    public bool SupportsReferenceImages { get; set; } = true;
}
