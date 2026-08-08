using System.Security.Cryptography;
using System.Text;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Adaptation.Conversion;
using PageToMovie.Adaptation.Validation;
using PageToMovie.Core.Abstractions;

namespace PageToMovie.Adaptation;

/// <summary>
/// Public Stage‑1 façade: pure book text in, fountain / estimates / analysis out.
/// No ProjectStore, paths, or HTTP. Callers inject <see cref="IChatClient"/>.
/// </summary>
public sealed class AdaptationService
{
    public const int MinRuntimeMinutes = NaturalRuntime.MinMinutes;
    public const int MaxRuntimeMinutes = NaturalRuntime.MaxMinutes;

    /// <summary>
    /// Kind, words, quality notes, natural minutes via <see cref="BookTextAnalyzer"/>.
    /// </summary>
    public BookAnalysisResult AnalyzeBook(string bookText)
    {
        var a = BookTextAnalyzer.Analyze(bookText ?? "");
        return new BookAnalysisResult
        {
            Pages = a.Pages,
            TextChars = a.TextChars,
            TextWords = a.TextWords,
            LetterRatio = a.LetterRatio,
            EmptyPageRatio = a.EmptyPageRatio,
            SparsePageRatio = a.SparsePageRatio,
            AvgCharsPerPage = a.AvgCharsPerPage,
            GarbageScore = a.GarbageScore,
            TextQuality = a.TextQuality,
            TextDensity = a.TextDensity,
            BookKind = a.BookKind,
            ReadyForStage1 = a.ReadyForStage1,
            SuggestedTotalMinutes = a.SuggestedTotalMinutes,
            SuggestedChunkPages = a.SuggestedChunkPages,
            Notes = a.Notes?.ToArray() ?? Array.Empty<string>(),
            TextEngine = a.TextEngine,
        };
    }

    /// <summary>
    /// Density-only natural runtime estimate via <see cref="AdaptationDensity"/> /
    /// <see cref="NaturalRuntime"/>.
    /// </summary>
    public NaturalRuntimeEstimate EstimateNaturalRuntime(string bookText)
    {
        var e = AdaptationDensity.EstimateNatural(bookText ?? "");
        var natural = NaturalRuntime.ClampMinutes(e.NaturalFilmMinutes);
        return ToNaturalRuntimeEstimate(e, targetMinutes: natural, mode: natural > 0 ? "natural" : "none");
    }

    /// <summary>
    /// Natural + optional override clamp (2–180). Pure — no store.
    /// </summary>
    public NaturalRuntimeEstimate ResolveTargetMinutes(string bookText, int? overrideMinutes = null)
    {
        var e = AdaptationDensity.EstimateNatural(bookText ?? "");
        var (natural, target, mode) = NaturalRuntime.Resolve(bookText, overrideMinutes);
        // Prefer density estimate details; clamp natural to the resolved value.
        return ToNaturalRuntimeEstimate(
            e,
            targetMinutes: target,
            mode: mode,
            naturalOverride: natural);
    }

    /// <summary>
    /// System prompt for book → Fountain (embedded <c>book_to_fountain.txt</c>).
    /// </summary>
    /// <param name="totalRuntimeMinutes">null or ≤0 = unlimited (default); positive = artificial target.</param>
    public Task<string> BuildSystemPromptAsync(
        int? totalRuntimeMinutes = null,
        CancellationToken ct = default,
        string? visualMedium = null) =>
        BookToFountainConverter.BuildSystemPromptAsync(
            totalRuntimeMinutes,
            ct,
            AdaptationPromptTokens.Default(totalRuntimeMinutes, visualMedium));

    /// <summary>
    /// Offline / test heuristic path (no chat).
    /// </summary>
    public string ConvertHeuristic(string title, string bookText, string? author = null) =>
        BookToFountainConverter.ConvertHeuristic(title, bookText, author);

    /// <summary>Normalize book text (Gutenberg strip + newlines) before convert/cache keys.</summary>
    public string NormalizeBookText(string bookText) =>
        BookToFountainConverter.NormalizeBookText(bookText);

    /// <summary>Stamp/fix Draft date line on a Fountain draft.</summary>
    public string FixDraftDate(string? fountain) =>
        BookToFountainConverter.FixDraftDate(fountain);

    /// <summary>Structural gate used by production quality checks.</summary>
    public bool LooksLikeGoodFountain(string text, bool requirePageTags = false) =>
        BookToFountainConverter.LooksLikeGoodFountain(text, requirePageTags);

    /// <summary>
    /// Deterministic cast package gate: speaking Fountain cues must resolve to cast_seeds
    /// with usable look fields. Optional <paramref name="bookText"/> flags invented names.
    /// </summary>
    public CastPackageCrossCheck.Report CrossCheckCast(
        string? fountainText,
        string? castSeedsJson,
        string? bookText = null) =>
        CastPackageCrossCheck.Evaluate(fountainText, castSeedsJson, bookText);

    /// <summary>
    /// Full Stage‑1 convert via <see cref="BookToFountainConverter"/>.
    /// Optional <paramref name="bookSession"/> enables provider file_id + multi-turn
    /// (retry/coverage/merge/repair without re-billing full book tokens).
    /// </summary>
    public async Task<AdaptationResult> ConvertAsync(
        AdaptationRequest request,
        IChatClient chat,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        Func<StructuralGateFailure, CancellationToken, Task>? onStructuralGateFailure = null,
        IBookFileSession? bookSession = null,
        BookToFountainConverter.PromptBudget? budgetOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(chat);

        var analysis = AnalyzeBook(request.BookText);
        var runtime = ResolveTargetMinutes(request.BookText, request.TargetRuntimeMinutes);
        // Product default: no artificial minute budget in the prompt (unlimited / natural length).
        // Only inject a number when the caller set TargetRuntimeMinutes explicitly.
        int? promptMinutes = request.TargetRuntimeMinutes is > 0
            ? NaturalRuntime.ClampMinutes(request.TargetRuntimeMinutes.Value)
            : null;
        var minutes = promptMinutes ?? (runtime.NaturalMinutes > 0
            ? runtime.NaturalMinutes
            : NaturalRuntime.MinMinutes);

        Action<string>? onProgress = progress is null ? null : s => progress.Report(s);
        var usedHeuristic = false;

        string promptSha;
        try
        {
            // Hash the same prompt the converter will load (null = unlimited directive).
            var prompt = await BuildSystemPromptAsync(promptMinutes, ct, request.VisualMedium).ConfigureAwait(false);
            promptSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        }
        catch
        {
            promptSha = "";
        }

        var conversion = await BookToFountainConverter.ConvertWithMetadataAsync(
            title: string.IsNullOrWhiteSpace(request.Title) ? "Untitled" : request.Title!,
            bookText: request.BookText,
            author: request.Author,
            totalRuntimeMinutes: promptMinutes,
            chat: chat,
            model: request.ModelId,
            onProgress: onProgress,
            ct: ct,
            budgetOverride: budgetOverride,
            onHeuristicFallback: _ => usedHeuristic = true,
            reasoningEffort: request.ReasoningEffort,
            onStructuralGateFailure: onStructuralGateFailure,
            temperature: request.Temperature,
            bookSession: bookSession,
            visualMedium: request.VisualMedium).ConfigureAwait(false);

        // Re-emit runtime with the clamped minutes actually used for generation.
        var runtimeUsed = new NaturalRuntimeEstimate
        {
            NaturalMinutes = runtime.NaturalMinutes,
            TargetMinutes = minutes,
            Mode = promptMinutes is null
                ? "unlimited"
                : NaturalRuntime.ResolveMode(runtime.NaturalMinutes, minutes),
            Method = runtime.Method,
            SourceWords = runtime.SourceWords,
            SourceSyllables = runtime.SourceSyllables,
            BookKind = runtime.BookKind,
            MinutesPerThousandWords = runtime.MinutesPerThousandWords,
            TemporalCompressionRatio = runtime.TemporalCompressionRatio,
            QuotedDialogueFraction = runtime.QuotedDialogueFraction,
            Notes = runtime.Notes,
        };

        var sceneCount = 0;
        try { sceneCount = BookToFountainConverter.CountSceneHeadings(conversion.Fountain); }
        catch { /* ignore */ }

        var manifest = new AdaptationConvertManifest
        {
            CompletedUtc = DateTime.UtcNow.ToString("o"),
            ModelId = request.ModelId ?? "",
            Temperature = request.Temperature,
            ReasoningEffort = request.ReasoningEffort,
            PromptContentSha256 = promptSha,
            AdaptationVersion = AdaptationVersion.Current,
            RuntimeMode = promptMinutes is null
                ? "unlimited"
                : NaturalRuntime.ResolveMode(runtime.NaturalMinutes, minutes),
            NaturalRuntimeMinutes = runtime.NaturalMinutes,
            TargetRuntimeMinutes = promptMinutes,
            UsedHeuristicFallback = usedHeuristic,
            VisionMetaStatus = conversion.VisionMetaStatus.ToString(),
            AdaptationReportStatus = conversion.AdaptationReportStatus.ToString(),
            BookFileSessionId = bookSession?.FileId,
            Title = request.Title ?? "",
            Author = request.Author,
            FountainChars = conversion.Fountain.Length,
            SceneCountApprox = sceneCount,
        };

        return new AdaptationResult
        {
            Fountain = conversion.Fountain,
            VisionMeta = conversion.VisionMeta,
            VisionMetaStatus = conversion.VisionMetaStatus,
            VisionMetaError = conversion.VisionMetaError,
            AdaptationReport = conversion.AdaptationReport,
            AdaptationReportStatus = conversion.AdaptationReportStatus,
            AdaptationReportError = conversion.AdaptationReportError,
            Runtime = runtimeUsed,
            Analysis = analysis,
            UsedHeuristicFallback = usedHeuristic,
            PromptContentSha256 = promptSha,
            ConvertManifest = manifest,
            Notes = conversion.VisionMetaError,
        };
    }

    /// <summary>Result of a Fountain → Fountain descriptive-layer edit (re-skin or embellish).</summary>
    public sealed record FountainEditResult(
        bool Ok,
        string Fountain,
        int SceneCountBefore,
        int SceneCountAfter,
        bool StructurePreserved,
        string? Warning);

    /// <summary>
    /// Re-skin an existing Fountain screenplay to a new visual medium — descriptive layer only.
    /// Dialogue, character cues, scene headings, and scene count/order are preserved; if the model
    /// changes the scene count the original is kept and the result is flagged not-preserved.
    /// </summary>
    public Task<FountainEditResult> ReskinAsync(
        string fountain,
        string? visualMedium,
        IChatClient chat,
        string? model = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        ApplyDescriptiveEditAsync(
            fountain,
            AdaptationPromptPack.BuildReskinSystemPrompt(visualMedium),
            userContent: null,
            operation: "re-skin",
            mode: "fountain_reskin",
            chat: chat,
            model: model,
            progress: progress,
            progressMessage: "Applying look to the screenplay…",
            ct: ct);

    /// <summary>
    /// Enrich an existing Fountain screenplay's descriptive layer for the target medium — incorporating
    /// the best of the book's own language where <paramref name="bookText"/> is supplied. Dialogue, cues,
    /// scene headings, and scene count/order are preserved; on drift the original is kept.
    /// </summary>
    public Task<FountainEditResult> EmbellishAsync(
        string fountain,
        string? visualMedium,
        IChatClient chat,
        string? bookText = null,
        string? model = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // Ground enrichment in the source when we have it (kept bounded to protect the prompt budget).
        string? userContent = null;
        if (!string.IsNullOrWhiteSpace(bookText))
        {
            const int maxBookChars = 40_000;
            var book = bookText.Length <= maxBookChars ? bookText : bookText[..maxBookChars];
            userContent =
                "ORIGINAL BOOK TEXT (for descriptive grounding — do not add plot from it):\n" +
                book + "\n\n---\n\nSCREENPLAY TO ENRICH:\n";
        }

        return ApplyDescriptiveEditAsync(
            fountain,
            AdaptationPromptPack.BuildEmbellishSystemPrompt(visualMedium),
            userContent: userContent,
            operation: "enrichment",
            mode: "fountain_embellish",
            chat: chat,
            model: model,
            progress: progress,
            progressMessage: "Enriching the screenplay…",
            ct: ct);
    }

    /// <summary>
    /// Shared Fountain → Fountain descriptive-edit core: run a chat pass, clean the output, and enforce
    /// the structural guardrail (scene count preserved) — keeping the original on any drift/failure.
    /// </summary>
    private static async Task<FountainEditResult> ApplyDescriptiveEditAsync(
        string fountain,
        string system,
        string? userContent,
        string operation,
        string mode,
        IChatClient chat,
        string? model,
        IProgress<string>? progress,
        string progressMessage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chat);
        fountain ??= "";
        var before = SafeSceneCount(fountain);

        if (string.IsNullOrWhiteSpace(fountain))
            return new FountainEditResult(false, fountain, before, before, true, $"No screenplay to {operation}.");
        if (!chat.IsConfigured)
            return new FountainEditResult(false, fountain, before, before, true, "AI service not configured.");

        progress?.Report(progressMessage);
        var userPrompt = userContent is null ? fountain : userContent + fountain;

        var raw = await chat.CompleteAsync(
            system,
            userPrompt,
            model ?? "",
            mode: mode,
            ct: ct).ConfigureAwait(false);

        var cleaned = BookToFountainConverter.StripFences(raw ?? "");
        if (string.IsNullOrWhiteSpace(cleaned))
            return new FountainEditResult(false, fountain, before, before, true,
                $"The {operation} returned nothing; kept the original.");

        cleaned = BookToFountainConverter.FixDraftDate(cleaned);
        var after = SafeSceneCount(cleaned);

        // Structural guardrail: the automated pass must not add/remove scenes.
        if (before > 0 && after != before)
            return new FountainEditResult(
                false, fountain, before, after, false,
                $"The {operation} changed the scene count ({before} → {after}); kept the original screenplay.");

        if (!BookToFountainConverter.LooksLikeGoodFountain(cleaned))
            return new FountainEditResult(false, fountain, before, after, false,
                $"The {operation} output did not look like a valid screenplay; kept the original.");

        return new FountainEditResult(true, cleaned, before, after, true, null);
    }

    /// <summary>
    /// Trim an existing Fountain screenplay toward a target runtime — condense / merge / cut only.
    /// Unlike re-skin/embellish the scene count may shrink; it must not grow, and the output must be a
    /// valid screenplay. On any drift/failure the original is kept.
    /// </summary>
    public async Task<FountainEditResult> TrimAsync(
        string fountain,
        int targetMinutes,
        int naturalMinutes,
        IChatClient chat,
        string? model = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        fountain ??= "";
        var before = SafeSceneCount(fountain);

        if (string.IsNullOrWhiteSpace(fountain))
            return new FountainEditResult(false, fountain, before, before, true, "No screenplay to trim.");
        if (!chat.IsConfigured)
            return new FountainEditResult(false, fountain, before, before, true, "AI service not configured.");

        progress?.Report($"Trimming the screenplay toward ~{Math.Max(1, targetMinutes)} min…");
        var system = AdaptationPromptPack.BuildTrimSystemPrompt(targetMinutes, naturalMinutes);

        var raw = await chat.CompleteAsync(
            system, fountain, model ?? "", mode: "fountain_trim", ct: ct).ConfigureAwait(false);

        var cleaned = BookToFountainConverter.StripFences(raw ?? "");
        if (string.IsNullOrWhiteSpace(cleaned))
            return new FountainEditResult(false, fountain, before, before, true,
                "The trim returned nothing; kept the original.");

        cleaned = BookToFountainConverter.FixDraftDate(cleaned);
        var after = SafeSceneCount(cleaned);

        // Trimming must never expand the scene count.
        if (before > 0 && after > before)
            return new FountainEditResult(
                false, fountain, before, after, false,
                $"Trim added scenes ({before} → {after}); kept the original screenplay.");

        if (!BookToFountainConverter.LooksLikeGoodFountain(cleaned))
            return new FountainEditResult(false, fountain, before, after, false,
                "Trim output did not look like a valid screenplay; kept the original.");

        return new FountainEditResult(true, cleaned, before, after, true, null);
    }

    private static int SafeSceneCount(string fountain)
    {
        try { return BookToFountainConverter.CountSceneHeadings(fountain); }
        catch { return 0; }
    }

    private static NaturalRuntimeEstimate ToNaturalRuntimeEstimate(
        AdaptationDensity.Estimate e,
        int targetMinutes,
        string mode,
        int? naturalOverride = null) =>
        new()
        {
            NaturalMinutes = naturalOverride ?? NaturalRuntime.ClampMinutes(e.NaturalFilmMinutes),
            TargetMinutes = targetMinutes,
            Mode = mode,
            Method = e.Method,
            SourceWords = e.SourceWords,
            SourceSyllables = e.SourceSyllables,
            BookKind = e.BookKind,
            MinutesPerThousandWords = e.MinutesPerThousandWords,
            TemporalCompressionRatio = e.TemporalCompressionRatio,
            QuotedDialogueFraction = e.QuotedDialogueFraction,
            Notes = e.Notes,
        };
}
