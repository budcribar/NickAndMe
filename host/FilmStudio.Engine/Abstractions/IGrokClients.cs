namespace FilmStudio.Engine.Abstractions;

/// <summary>xAI (or fake) video generate / poll / download.</summary>
public interface IGrokVideoClient
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

/// <summary>xAI (or fake) image generate / edit.</summary>
public interface IGrokImageClient
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
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}

/// <summary>xAI (or fake) chat completions.</summary>
public interface IGrokChatClient
{
    bool IsConfigured { get; }

    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default);
}

/// <summary>xAI (or fake) vision (transcribe / classify).</summary>
public interface IGrokVisionClient
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
}
