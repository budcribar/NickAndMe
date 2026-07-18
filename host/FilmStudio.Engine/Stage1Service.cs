using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Book / Fountain → approved screenplay build for shot planning.
/// Operator source of truth is <c>source/screenplay.fountain</c> (via
/// <c>prompts/book_to_fountain.txt</c>). Internal Stage 1 JSON is materialised
/// only from Fountain so existing shot tools keep working — no scene-bible LLM prompt.
/// </summary>
public sealed class Stage1Service
{
    private readonly ProjectStore _projects;
    private readonly IGrokChatClient _chat;
    private readonly BookPrepareService _books;
    private readonly CharacterBookPlateService _plates;
    private readonly ILogger<Stage1Service> _log;

    public Stage1Service(
        ProjectStore projects,
        IGrokChatClient chat,
        BookPrepareService books,
        CharacterBookPlateService plates,
        IOptions<FilmStudioOptions> opts,
        ILogger<Stage1Service> log)
    {
        _projects = projects;
        _chat = chat;
        _books = books;
        _plates = plates;
        _ = opts;
        _log = log;
    }

    /// <summary>
    /// Ensure a Fountain draft exists (from book when needed), then materialise the
    /// approved build from that Fountain. Does not use a book→JSON scene-bible prompt.
    /// </summary>
    public async Task<Stage1Result> RunAsync(
        string projectId,
        int chunkPages = 10,
        int? totalMinutes = null,
        string model = "grok-4.5",
        bool resume = false,
        int maxChunks = 0,
        double temperature = 0.2,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        // Legacy params kept for API compatibility (chunkPages / resume / maxChunks unused).
        _ = (chunkPages, resume, maxChunks, temperature);

        if (!_chat.IsConfigured)
            throw new InvalidOperationException(
                "Connect service (API key) to build a screenplay draft from the book.");

        var projectDir = _projects.GetProjectDir(projectId);
        var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
        var draftPath = ScreenplayService.GetDraftPath(_projects, projectId);

        onProgress?.Invoke("Checking book text…");
        if (!File.Exists(bookPath))
        {
            onProgress?.Invoke("No book_full.txt — running book prepare…");
            var prep = await _books.PrepareAsync(
                projectId,
                forceExtract: true,
                forceVision: false,
                autoVision: true,
                visionModel: model,
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);
            if (!prep.ReadyForStage1 && !File.Exists(bookPath))
                throw new InvalidOperationException(
                    prep.StrategyReason ?? "Book text is not ready. Prepare the book first.");
        }

        if (!File.Exists(bookPath))
            throw new InvalidOperationException("No prepared book text yet.");

        var book = await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);
        var analysis = BookTextAnalyzer.Analyze(book);
        if (analysis.TextQuality is "poor" or "empty" || analysis.GarbageScore >= 0.45)
            throw new InvalidOperationException(
                "book_full.txt is still garbled OCR. Prepare the book with vision first.");

        var minutes = totalMinutes is > 0
            ? Math.Clamp(totalMinutes.Value, 3, 180)
            : Math.Clamp(analysis.SuggestedTotalMinutes, 3, 180);

        onProgress?.Invoke(
            $"Target runtime {minutes} min · building Fountain from book (prompts/book_to_fountain.txt)…");

        var draft = await ScreenplayService.CreateDraftFromBookAsync(
            _projects,
            projectId,
            _chat,
            model,
            ct).ConfigureAwait(false);
        if (!draft.Ok)
            throw new InvalidOperationException(draft.Error ?? "Could not create Fountain draft from book.");

        onProgress?.Invoke("Fountain draft saved — approving to build scene list for shot plan…");
        var sign = ScreenplayService.SignOff(_projects, projectId);
        if (!sign.Ok)
            throw new InvalidOperationException(sign.Error ?? "Could not approve screenplay from Fountain.");

        // Book plate attach (same as old Stage 1 post-step)
        try
        {
            onProgress?.Invoke("Attaching book plate candidates to cast…");
            var plates = await _plates.AttachAsync(
                projectId,
                force: true,
                copyIntoAssets: true,
                useGrok: true,
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);
            if (plates.Ok)
                onProgress?.Invoke(
                    $"Book plates ({plates.Method}): updated={plates.CharactersUpdated} " +
                    $"skipped={plates.CharactersSkipped}");
            else
                onProgress?.Invoke($"Book plate attach skipped: {plates.Reason}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Book plate attach after Fountain materialize failed");
            onProgress?.Invoke($"Book plate attach failed (non-fatal): {ex.Message}");
        }

        var stage1 = ScreenplayService.ReadStage1Lite(_projects, projectId);
        var outPath = _projects.ResolveScenesJsonPath(projectId);
        var result = new Stage1Result
        {
            Ok = stage1.Present && stage1.SceneCount > 0,
            OutPath = outPath,
            SceneCount = stage1.SceneCount,
            CharacterCount = stage1.CharacterCount,
            LocationCount = stage1.LocationCount,
            RuntimeSeconds = (int)(stage1.RuntimeSeconds ?? 0),
            TotalMinutes = minutes,
            VerifyErrors = new List<string>(),
            HardErrors = new List<string>(),
        };

        if (!result.Ok)
            result.HardErrors.Add("Fountain approved but no scenes were materialised.");

        onProgress?.Invoke(
            $"Screenplay ready from Fountain · {result.SceneCount} scenes · " +
            $"{result.CharacterCount} cast · draft {Path.GetFileName(draftPath)}");
        return result;
    }
}

public sealed class Stage1Result
{
    public bool Ok { get; set; }
    public string OutPath { get; set; } = "";
    public int SceneCount { get; set; }
    public int CharacterCount { get; set; }
    public int LocationCount { get; set; }
    public int RuntimeSeconds { get; set; }
    public int TotalMinutes { get; set; }
    public List<string> VerifyErrors { get; set; } = new();
    public List<string> HardErrors { get; set; } = new();
}
