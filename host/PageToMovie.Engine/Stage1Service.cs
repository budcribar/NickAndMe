using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Book / Fountain → approved screenplay build for shot planning.
/// Operator source of truth is <c>source/screenplay.fountain</c> (via
/// <c>prompts/book_to_fountain.txt</c>). Internal Stage 1 JSON is materialised
/// only from Fountain so existing shot tools keep working — no scene-bible LLM prompt.
/// </summary>
public sealed class Stage1Service
{
    private readonly ProjectStore _projects;
    private readonly IChatClient _chat;
    private readonly BookPrepareService _books;
    private readonly CharacterBookPlateService _plates;
    private readonly ILogger<Stage1Service> _log;

    public Stage1Service(
        ProjectStore projects,
        IChatClient chat,
        BookPrepareService books,
        CharacterBookPlateService plates,
        IOptions<PageToMovieOptions> opts,
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
            onProgress: onProgress,
            ct: ct).ConfigureAwait(false);
        if (!draft.Ok)
            throw new InvalidOperationException(draft.Error ?? "Could not create Fountain draft from book.");

        onProgress?.Invoke("Fountain draft saved — approving screenplay…");
        var sign = ScreenplayService.SignOff(_projects, projectId);
        if (!sign.Ok)
            throw new InvalidOperationException(sign.Error ?? "Could not approve screenplay from Fountain.");

        // Book plate attach → cast_seeds.json (Stage 2 reads Fountain + overlay)
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
            _log.LogWarning(ex, "Book plate attach after Fountain approve failed");
            onProgress?.Invoke($"Book plate attach failed (non-fatal): {ex.Message}");
        }

        var stage1 = ScreenplayService.ReadStage1Lite(_projects, projectId);
        var fountainText = File.Exists(draftPath)
            ? await File.ReadAllTextAsync(draftPath, ct).ConfigureAwait(false)
            : "";
        var (voCues, totalCues) = FountainParser.CountVoiceoverCues(fountainText);
        var voPct = totalCues > 0 ? voCues * 100 / totalCues : 0;

        var result = new Stage1Result
        {
            Ok = stage1.Present && stage1.SceneCount > 0,
            OutPath = draftPath,
            SceneCount = stage1.SceneCount,
            CharacterCount = stage1.CharacterCount,
            LocationCount = stage1.LocationCount,
            RuntimeSeconds = (int)(stage1.RuntimeSeconds ?? 0),
            TotalMinutes = minutes,
            VoCueCount = voCues,
            TotalDialogueCues = totalCues,
            VoPercent = voPct,
            VerifyErrors = new List<string>(),
            HardErrors = new List<string>(),
            Warnings = new List<string>(),
        };

        if (!result.Ok)
            result.HardErrors.Add("Fountain approved but no scenes were found.");

        // Re-check for issues the generation-time auto-repair may have failed to clear
        // (e.g. a transient API failure on the repair call itself). Checked from the
        // saved draft every run, not just once at generation time, so it doesn't rely
        // on catching a one-off progress message.
        var stillVagueHeadings = BookToFountainConverter.FindVagueLocationHeadings(fountainText);
        if (stillVagueHeadings.Count > 0)
        {
            var msg = $"{stillVagueHeadings.Count} vague location heading(s) unresolved: " +
                      string.Join("; ", stillVagueHeadings.Take(3));
            result.Warnings.Add(msg);
            onProgress?.Invoke($"Warning: {msg}");
        }

        var stillGenericSpeakers = BookToFountainConverter.FindGenericNumberedSpeakers(fountainText);
        if (stillGenericSpeakers.Count > 0)
        {
            var msg = $"{stillGenericSpeakers.Count} generic numbered speaker(s) unresolved: " +
                      string.Join("; ", stillGenericSpeakers.Take(3));
            result.Warnings.Add(msg);
            onProgress?.Invoke($"Warning: {msg}");
        }

        // Surface-only: high V.O. share is fine for confessional prose but leans clip gen on narration
        if (totalCues > 0 && voPct >= 45)
        {
            onProgress?.Invoke(
                $"Note: {voCues}/{totalCues} dialogue cues are V.O. ({voPct}%) — " +
                "clip gen will lean on narration. Prefer on-camera frame cutbacks where possible.");
        }

        var softMaxScenes = BookToFountainConverter.SoftMaxSceneHeadings(analysis.BookKind);
        if (result.SceneCount > softMaxScenes)
        {
            onProgress?.Invoke(
                $"Note: {result.SceneCount} scenes (soft target ≤{softMaxScenes} for {analysis.BookKind}) — " +
                "shot plan / clip count may be high.");
        }

        var warningsSuffix = result.Warnings.Count > 0 ? $" · {result.Warnings.Count} warning(s)" : "";
        onProgress?.Invoke(
            $"Screenplay ready · {result.SceneCount} scenes · " +
            $"{result.CharacterCount} cast · {result.LocationCount} locations · " +
            $"V.O. {voCues}/{totalCues} ({voPct}%){warningsSuffix} · {Path.GetFileName(draftPath)}");
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
    /// <summary>Character cues tagged V.O. (from FountainParser).</summary>
    public int VoCueCount { get; set; }
    /// <summary>All character dialogue cues.</summary>
    public int TotalDialogueCues { get; set; }
    /// <summary>0–100 integer percent of cues that are V.O.</summary>
    public int VoPercent { get; set; }
    public List<string> VerifyErrors { get; set; } = new();
    public List<string> HardErrors { get; set; } = new();
    /// <summary>
    /// Non-fatal issues that survived generation-time auto-repair (e.g. a repair call
    /// that failed both attempts). Re-checked from the saved draft on every Stage1 run,
    /// so it resurfaces here even if the original progress message was missed.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
