namespace FilmStudio.Engine.Abstractions;

/// <summary>Video generate / poll / download. Grok, Gemini (Veo), or fake — see MultiProviderVideoClient.</summary>
public interface IVideoClient
{
    bool IsConfigured { get; }

    /// <param name="referenceImagePaths">
    /// Character/style refs for reference-to-video (<c>reference_images</c>).
    /// Prompt should use <c>&lt;IMAGE_1&gt;</c>… tags.
    /// Mutually exclusive with start-frame / video-continue modes.
    /// </param>
    /// <param name="startFrameImagePath">
    /// Optional still used as the first frame (image-to-video).
    /// Prefer <paramref name="continueFromVideoPath"/> for true clip-to-clip continue.
    /// </param>
    /// <param name="continueFromVideoPath">
    /// Local path to previous clip video. Uses Imagine <c>/videos/extensions</c>
    /// (continue from last frame with the new prompt). Result is prev+extension;
    /// caller should trim the new portion for a per-clip file.
    /// </param>
    Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null);

    Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct);

    Task DownloadToFileAsync(string url, string destPath, CancellationToken ct);
}

/// <summary>Image generate / edit. Grok, Gemini, or fake — see MultiProviderImageClient.</summary>
public interface IImageClient
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<byte[]>> EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        int maxRefs = 0,
        string? costumeRefPath = null,
        bool illustratedMedium = true,
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}

/// <summary>Chat completions. Grok, Anthropic, Gemini, or fake — see MultiProviderChatClient.</summary>
public interface IChatClient
{
    bool IsConfigured { get; }

    /// <param name="mode">
    /// Telemetry tag for <c>api_calls.jsonl</c> (<c>ApiCallTelemetry.Mode</c>), e.g.
    /// <c>book_to_fountain</c>, <c>cast_from_screenplay</c>, <c>cast_visual_literalize</c>.
    /// </param>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null);
}

/// <summary>Canonical <see cref="IChatClient.CompleteAsync"/> mode tags for telemetry.</summary>
public static class ChatCallModes
{
    public const string BookToFountain = "book_to_fountain";
    public const string BookToFountainRetry = "book_to_fountain_retry";
    public const string BookToFountainCoverage = "book_to_fountain_coverage";
    public const string BookToFountainChunk = "book_to_fountain_chunk";
    public const string BookToFountainChunkRetry = "book_to_fountain_chunk_retry";
    public const string BookToFountainMerge = "book_to_fountain_merge";
    public const string BookToFountainLocationsRetry = "book_to_fountain_locations_retry";
    public const string BookToFountainSpeakersRetry = "book_to_fountain_speakers_retry";
    public const string CastFromScreenplay = "cast_from_screenplay";
    public const string CastVisualLiteralize = "cast_visual_literalize";
    public const string LearningPropose = "learning_propose";
    public const string SilentBeatClassify = "silent_beat_classify";
    public const string AmbientSfxClassify = "ambient_sfx_classify";
    public const string OnScreenCastClassify = "onscreen_cast_classify";
    public const string ExtendCutClassify = "extend_cut_classify";
    public const string SpeciesKindClassify = "species_kind_classify";
    public const string PlateRankClassify = "plate_rank_classify";
    public const string ShotPlanRefineClassify = "shot_plan_refine_classify";
    public const string BeatPacingClassify = "beat_pacing_classify";
    public const string CinematicLightingClassify = "cinematic_lighting_classify";
    public const string CameraDirectorClassify = "camera_director_classify";
}

/// <summary>
/// Vision (transcribe / classify / multi-image completion). Grok, or fake; Anthropic and Gemini
/// only implement <see cref="CompleteWithImagesAsync"/> — see MultiProviderVisionClient.
/// </summary>
public interface IVisionClient
{
    bool IsConfigured { get; }

    Task<string> TranscribePageAsync(
        string imagePath,
        int page,
        string model = "grok-4.5",
        CancellationToken ct = default);

    Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast,
        string model = "grok-4.5",
        CancellationToken ct = default);

    /// <summary>
    /// Multi-image vision completion (clip auto-review: prev tail + current frames).
    /// Returns model text (JSON expected by caller).
    /// </summary>
    Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "grok-4.5",
        string detail = "low",
        CancellationToken ct = default);
}
