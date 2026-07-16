using System.Text.Json;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Native character portrait design: Grok image gen/edit, lock/unlock refs.
/// Character portrait variants + lock/unlock to character_*_ref.png.
/// </summary>
public sealed class CharacterDesignService
{
    private readonly ProjectStore _projects;
    private readonly GrokImageClient _images;
    private readonly CostReportService _costs;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<CharacterDesignService> _log;

    public CharacterDesignService(
        ProjectStore projects,
        GrokImageClient images,
        CostReportService costs,
        IOptions<FilmStudioOptions> opts,
        ILogger<CharacterDesignService> log)
    {
        _projects = projects;
        _images = images;
        _costs = costs;
        _opts = opts.Value;
        _log = log;
    }

    /// <param name="n">Variant count. Pass 0 (default) for auto: 1 if locked, else 3.</param>
    /// <param name="seedOptions">Flexible seed policy (auto / preferred / book / explicit multi-select).</param>
    public async Task<CharacterDesignResult> GenerateVariantsAsync(
        string projectId,
        string charKey,
        int n = 0,
        FilmStudio.Core.Models.StartCharacterVariantsRequest? seedOptions = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_images.IsConfigured)
            throw new InvalidOperationException("XAI_API_KEY is not set (required for portrait generation).");

        var projectDir = _projects.GetProjectDir(projectId);
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no portrait variants.");

        var charDir = Path.Combine(projectDir, "assets", "characters");
        Directory.CreateDirectory(charDir);

        var opts = seedOptions ?? new FilmStudio.Core.Models.StartCharacterVariantsRequest
        {
            ProjectId = projectId,
            CharKey = charKey,
        };
        var maxRefs = Math.Clamp(opts.MaxRefs <= 0 ? 3 : opts.MaxRefs, 1, 7);
        var maxBook = Math.Clamp(opts.MaxBookHints < 0 ? 2 : opts.MaxBookHints, 0, maxRefs);

        var preferredPath = ResolvePreferredImagePath(projectId, charKey, charDir);
        var preferredName = preferredPath is null ? "" : Path.GetFileName(preferredPath);
        var alreadyLocked = preferredPath is not null &&
            ProjectStore.CharacterRefFileCandidates(charKey)
                .Any(c => string.Equals(c, preferredName, StringComparison.OrdinalIgnoreCase));
        if (n <= 0)
            n = opts.Count > 0 ? opts.Count : (alreadyLocked ? 1 : 3);
        n = Math.Clamp(n, 1, 6);

        var allBookRefs = ResolveBookRefPaths(projectDir, seeds, maxRefs: 12);
        var editRefs = ResolveEditRefs(
            projectId, charKey, charDir, preferredPath, allBookRefs, opts, maxRefs, maxBook, onProgress);
        // Preferred / locked ref first so multi-image edit treats it as primary identity
        if (preferredPath is not null && editRefs.Count > 0)
        {
            var prefInList = editRefs.FirstOrDefault(p =>
                string.Equals(p, preferredPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(p), preferredName, StringComparison.OrdinalIgnoreCase));
            if (prefInList is not null)
            {
                editRefs.Remove(prefInList);
                editRefs.Insert(0, prefInList);
            }
        }

        onProgress?.Invoke(
            $"Seed mode={NormalizeSeedMode(opts.SeedMode)} · refs={editRefs.Count}/{maxRefs} · variants={n}");

        // Optional edit of description / visual_lock from Characters UI before generating
        if (opts.PersistDescription &&
            (opts.DescriptionOverride is not null || opts.VisualLockOverride is not null))
        {
            _projects.UpdateCharacterSeedText(
                projectId,
                charKey,
                description: opts.DescriptionOverride,
                visualLock: opts.VisualLockOverride);
            seeds = _projects.GetCharacterSeed(projectId, charKey) ?? seeds;
            onProgress?.Invoke("Saved description / visual lock to scenes.json seeds");
        }

        var hasImageHints = editRefs.Count > 0;
        var prompt = BuildDesignPrompt(
            charKey,
            seeds,
            hasImageHints,
            descriptionOverride: opts.DescriptionOverride,
            visualLockOverride: opts.VisualLockOverride);
        var imageModel = GetConfigString(projectId, "image_model_name", _opts.DefaultImageModel);

        onProgress?.Invoke($"design prompt ready ({prompt.Length} chars)");
        IReadOnlyList<byte[]> blobs;
        var mode = "text_only";
        string? editError = null;

        // If preferred lives in variant_01, snapshot it so we can overwrite variant slots safely
        string? preferredSnapshot = null;
        try
        {
            if (preferredPath is not null &&
                Path.GetFileName(preferredPath).Contains("_variant_", StringComparison.OrdinalIgnoreCase))
            {
                preferredSnapshot = Path.Combine(
                    charDir, $"{charKey.ToLowerInvariant()}_preferred_snap.png");
                File.Copy(preferredPath, preferredSnapshot, overwrite: true);
                var i = editRefs.FindIndex(p =>
                    string.Equals(p, preferredPath, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) editRefs[i] = preferredSnapshot;
            }

            if (hasImageHints)
            {
                onProgress?.Invoke(
                    $"Grok image edit with {editRefs.Count} hint(s) [primary={Path.GetFileName(editRefs[0])}]: " +
                    string.Join(", ", editRefs.Select(Path.GetFileName)));
                try
                {
                    var primary = editRefs.Take(Math.Min(3, editRefs.Count)).ToList();
                    blobs = await _images.EditVariantsAsync(
                        prompt,
                        primary,
                        n,
                        aspectRatio: "1:1",
                        model: imageModel,
                        onProgress: onProgress,
                        ct: ct);
                    mode = alreadyLocked
                        ? (primary.Count > 1 ? "preferred_multi" : "preferred_locked")
                        : (primary.Count > 1 ? "preferred_or_book_multi" : "preferred_or_book");
                }
                catch (Exception ex)
                {
                    editError = ex.Message;
                    // User picked image seeds — never silently invent a different dog from text
                    // Retry once with preferred-only if multi-ref failed
                    if (preferredPath is not null && File.Exists(preferredPath) && editRefs.Count > 1)
                    {
                        onProgress?.Invoke(
                            $"Multi-ref edit failed ({ex.Message}); retry preferred-only…");
                        try
                        {
                            blobs = await _images.EditVariantsAsync(
                                prompt,
                                new[] { preferredPath },
                                n,
                                aspectRatio: "1:1",
                                model: imageModel,
                                onProgress: onProgress,
                                ct: ct);
                            mode = "preferred_only_retry";
                        }
                        catch (Exception ex2)
                        {
                            throw new InvalidOperationException(
                                "Image-guided edit failed (multi-ref and preferred-only). " +
                                "Not falling back to text-only — that invents a different character. " +
                                $"Last error: {ex2.Message}", ex2);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Image-guided edit failed. Not falling back to text-only " +
                            $"(would ignore your selected seeds). Error: {ex.Message}", ex);
                    }
                }
            }
            else
            {
                onProgress?.Invoke("Text-only generation (no preferred image and no book plates)");
                blobs = await _images.GenerateVariantsAsync(
                    prompt, n, aspectRatio: "1:1", model: imageModel, ct: ct);
                mode = "text_only";
            }

            var paths = new List<string>();
            for (var i = 0; i < blobs.Count && i < n; i++)
            {
                var idx = i + 1;
                var fileName = $"{charKey.ToLowerInvariant()}_variant_0{idx}.png";
                var full = Path.Combine(charDir, fileName);
                await File.WriteAllBytesAsync(full, blobs[i], ct);
                paths.Add(full);
                onProgress?.Invoke($"saved variant {idx}/{n} → {fileName}");

                try
                {
                    _costs.RecordImageGeneration(projectId, 1, imageModel, quality: true);
                }
                catch (Exception costEx)
                {
                    _log.LogWarning(costEx, "Could not record image cost");
                }
            }

            if (paths.Count < 1)
                throw new InvalidOperationException($"No variants generated for {charKey}");

            return new CharacterDesignResult
            {
                CharKey = charKey,
                Mode = mode,
                Paths = paths,
                BookRefs = editRefs.Select(Path.GetFileName).Where(s => s is not null).Cast<string>().ToList(),
                EditError = editError,
            };
        }
        finally
        {
            if (preferredSnapshot is not null)
            {
                try { File.Delete(preferredSnapshot); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Build ordered image seeds for Grok from flexible policy.
    /// Preferred first (when included), then book / explicit selections, capped at maxRefs.
    /// </summary>
    private List<string> ResolveEditRefs(
        string projectId,
        string charKey,
        string charDir,
        string? preferredPath,
        List<string> allBookRefs,
        FilmStudio.Core.Models.StartCharacterVariantsRequest opts,
        int maxRefs,
        int maxBook,
        Action<string>? onProgress)
    {
        var mode = NormalizeSeedMode(opts.SeedMode);
        var editRefs = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (new FileInfo(path).Length < 64) return;
            if (editRefs.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
            if (editRefs.Count >= maxRefs) return;
            editRefs.Add(path);
        }

        switch (mode)
        {
            case "none":
                onProgress?.Invoke("Seed mode=none → text-only (no image refs)");
                return editRefs;

            case "preferred_only":
                if (opts.IncludePreferred)
                    Add(preferredPath);
                onProgress?.Invoke(
                    preferredPath is null
                        ? "Preferred-only mode but no preferred image"
                        : $"Preferred-only: {Path.GetFileName(preferredPath)}");
                return editRefs;

            case "explicit":
                if (opts.IncludeLockedRef || opts.IncludePreferred)
                    Add(preferredPath);
                foreach (var vi in opts.VariantIndices.Distinct().OrderBy(x => x))
                {
                    if (vi is < 1 or > 3) continue;
                    Add(Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_0{vi}.png"));
                }
                // Book indices match design_reference_images order / BookRefs Index
                foreach (var bi in opts.BookRefIndices.Distinct().OrderBy(x => x))
                {
                    if (bi < 0 || bi >= allBookRefs.Count) continue;
                    Add(allBookRefs[bi]);
                }
                onProgress?.Invoke(
                    editRefs.Count == 0
                        ? "Explicit mode: no valid selections — will text-only"
                        : $"Explicit seeds ({editRefs.Count}): {string.Join(", ", editRefs.Select(Path.GetFileName))}");
                return editRefs;

            case "book_hints":
                if (opts.IncludePreferred)
                    Add(preferredPath);
                foreach (var br in allBookRefs.Take(maxRefs))
                    Add(br);
                onProgress?.Invoke(
                    allBookRefs.Count == 0
                        ? "Book-hints mode: no plates attached for character"
                        : $"Book-hints + preferred ({editRefs.Count}): {string.Join(", ", editRefs.Select(Path.GetFileName))}");
                return editRefs;

            default: // auto
                if (opts.IncludePreferred)
                    Add(preferredPath);
                foreach (var br in allBookRefs.Take(maxBook))
                    Add(br);
                onProgress?.Invoke(
                    editRefs.Count == 0
                        ? "Auto seeds: none — description only"
                        : $"Auto seeds ({editRefs.Count}): {string.Join(", ", editRefs.Select(Path.GetFileName))}");
                return editRefs;
        }
    }

    private static string NormalizeSeedMode(string? mode)
    {
        mode = (mode ?? "auto").Trim().ToLowerInvariant().Replace('-', '_');
        return mode switch
        {
            "preferred" or "preferred_only" or "pref" => "preferred_only",
            "book" or "book_hints" or "search_book" => "book_hints",
            "explicit" or "custom" or "manual" => "explicit",
            "none" or "text" or "text_only" => "none",
            _ => "auto",
        };
    }

    public string LockVariant(string projectId, string charKey, int variantIndex)
    {
        if (variantIndex is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(variantIndex), "variant index must be 1..3");
        var projectDir = _projects.GetProjectDir(projectId);
        var fileName = $"{charKey.ToLowerInvariant()}_variant_0{variantIndex}.png";
        var variantPath = Path.Combine(projectDir, "assets", "characters", fileName);
        if (!File.Exists(variantPath))
            throw new InvalidOperationException($"Variant not found: {fileName}");
        return LockFromPath(projectId, charKey, variantPath);
    }

    public string LockBookRef(string projectId, string charKey, int bookIndex)
    {
        var path = _projects.ResolveCharacterBookRefPath(projectId, charKey, bookIndex)
            ?? throw new InvalidOperationException($"Book ref {bookIndex} not found for {charKey}");
        return LockFromPath(projectId, charKey, path);
    }

    public string LockFromPath(string projectId, string charKey, string sourcePath)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no reference image to lock.");

        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"Image not found: {sourcePath}");

        var projectDir = _projects.GetProjectDir(projectId);
        var refName = ProjectStore.CharacterRefFileName(charKey);
        var dest = Path.Combine(projectDir, "assets", "characters", refName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        // Copy (convert jpg→png name still just copies bytes if formats differ — OK for video ref)
        File.Copy(sourcePath, dest, overwrite: true);

        // Clear open variants so UI focuses on lock
        for (var i = 1; i <= 3; i++)
        {
            var vp = Path.Combine(
                projectDir, "assets", "characters",
                $"{charKey.ToLowerInvariant()}_variant_0{i}.png");
            try
            {
                if (File.Exists(vp)) File.Delete(vp);
            }
            catch { /* ignore */ }
        }

        _projects.UpdateCharacterSeedPlaceholder(projectId, charKey, ProjectStore.CharacterRefFileName(charKey));
        _projects.MarkCharacterChanged(projectId, charKey, $"Locked reference from {Path.GetFileName(sourcePath)}");
        return dest;
    }

    /// <summary>
    /// Clear the official lock so video gen requires re-lock, but keep the image
    /// as variant_01 — the "best so far" seed for comparison / regenerate.
    /// Does not delete other variants.
    /// </summary>
    public bool Unlock(string projectId, string charKey)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey);
        if (seeds is null) return false;
        if (IsVoiceOnly(charKey, seeds.Value))
            throw new InvalidOperationException($"{charKey} is voice-only — nothing to unlock.");

        var projectDir = _projects.GetProjectDir(projectId);
        var existing = _projects.ResolveCharacterRefPath(projectId, charKey);
        if (existing is null)
            return false;

        var charDir = Path.Combine(projectDir, "assets", "characters");
        Directory.CreateDirectory(charDir);
        var bestVariant = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_01.png");

        // Demote lock → variant 1 (best option) instead of discarding the image
        try
        {
            File.Copy(existing, bestVariant, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not preserve locked image as variant 1: {ex.Message}", ex);
        }

        try
        {
            File.Delete(existing);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Demoted lock to variant_01 but could not delete {Path}", existing);
            // Still treat as unlock if variant was written and lock is gone, or if lock remains
            // try again once — if still present, report failure so UI doesn't lie
            if (File.Exists(existing))
                throw new InvalidOperationException(
                    $"Saved best-so-far as variant 1, but could not remove locked file: {ex.Message}", ex);
        }

        _log.LogInformation(
            "Unlocked {CharKey}: preserved {Ref} as {Variant}",
            charKey, Path.GetFileName(existing), Path.GetFileName(bestVariant));
        return true;
    }

    // ---- helpers ----

    /// <summary>Locked ref if present, else variant_01 (best-so-far after unlock).</summary>
    private string? ResolvePreferredImagePath(string projectId, string charKey, string charDir)
    {
        var locked = _projects.ResolveCharacterRefPath(projectId, charKey);
        if (locked is not null) return locked;
        var best = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_01.png");
        if (File.Exists(best) && new FileInfo(best).Length >= 64)
            return best;
        return null;
    }

    private static string BuildDesignPrompt(
        string charKey,
        JsonElement seedInfo,
        bool hasImageHints,
        string? descriptionOverride = null,
        string? visualLockOverride = null)
    {
        var description = !string.IsNullOrWhiteSpace(descriptionOverride)
            ? descriptionOverride!
            : seedInfo.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var visualLock = !string.IsNullOrWhiteSpace(visualLockOverride)
            ? visualLockOverride!
            : seedInfo.TryGetProperty("visual_lock", out var vlck) ? vlck.GetString() ?? "" : "";
        var ageBand = seedInfo.TryGetProperty("age_band", out var ab) ? ab.GetString() ?? "" : "";
        var variantOf = seedInfo.TryGetProperty("variant_of", out var vo) ? vo.GetString() ?? "" : "";
        var display =
            seedInfo.TryGetProperty("canonical_given_name", out var cn) && cn.GetString() is { Length: > 0 } cname
                ? cname
                : seedInfo.TryGetProperty("voice_label", out var vl) && vl.GetString() is { Length: > 0 } lab
                    ? lab
                    : charKey.Replace("Character_", "").Replace("_", " ");

        var ageClause = "";
        if (ageBand.StartsWith("child", StringComparison.OrdinalIgnoreCase) ||
            charKey.EndsWith("_Young", StringComparison.OrdinalIgnoreCase))
        {
            ageClause =
                "CRITICAL: this is a CHILD portrait with child proportions, smaller head-to-body ratio, " +
                "youthful face — NOT an adult, NOT a bodybuilder, NOT aged-up. ";
        }
        else if (ageBand.StartsWith("teen", StringComparison.OrdinalIgnoreCase) ||
                 charKey.EndsWith("_Teen", StringComparison.OrdinalIgnoreCase))
        {
            ageClause =
                "CRITICAL: this is a TEEN / late-teen portrait — younger than the adult version, " +
                "not a middle-aged adult. ";
        }
        else if (ageBand.Contains("dog", StringComparison.OrdinalIgnoreCase) ||
                 description.Contains("dog", StringComparison.OrdinalIgnoreCase))
        {
            ageClause =
                "CRITICAL: this is a DOG portrait (animal), not a human. " +
                "Match breed look, ear shape, coat color/markings from the description. ";
        }

        var familyClause = "";
        if (!string.IsNullOrWhiteSpace(variantOf))
        {
            familyClause =
                $"Should clearly read as a younger version of {variantOf} " +
                "(same ethnicity, hair color family, recognizable family features). ";
        }

        var descSafe = CharacterVisualTextScrubber.ScrubVisualProse(description);
        var visualSafe = CharacterVisualTextScrubber.ScrubVisualProse(visualLock);
        var visualClauseSafe = string.IsNullOrWhiteSpace(visualSafe) ? "" : $"Visual lock: {visualSafe}. ";
        const string treatment = "soft even studio lighting";

        if (hasImageHints)
        {
            // Image refs are primary — description only clarifies, never invents a new design
            return
                $"IDENTITY-LOCKED character continuity portrait of {display}. " +
                "The FIRST attached reference image is the authoritative face/body identity. " +
                "Any additional attached images are the SAME character from the picture book " +
                "(coat markings, hat, illustration style) — copy that identity closely. " +
                "CRITICAL: Match the preferred/reference face shape, fur color pattern, ear set, " +
                "eye shape, and hat design from the images. Do NOT redesign the character. " +
                "Do NOT turn book nicknames or metaphors into literal objects or props. " +
                "Do NOT output a labeled model-sheet, callouts, color swatches, arrows, or UI chrome — " +
                "one clean portrait only, plain soft background, head and upper body, facing camera. " +
                $"{ageClause}{familyClause}" +
                $"Supporting text (secondary to images): {descSafe}. {visualClauseSafe}" +
                $"Style: match the attached art (picture-book / soft 3D as in the refs). {treatment}.";
        }

        return
            $"A clean character continuity portrait of {display}: {descSafe}. {visualClauseSafe}" +
            $"{ageClause}{familyClause}" +
            "Character centered, facing camera, plain soft background, head and upper body. " +
            "Use only filmable look words from the description (colors, markings, real garments). " +
            "No model-sheet labels or annotations. " +
            "Children's picture-book character style unless text says otherwise. " +
            treatment + ".";
    }

    private static List<string> ResolveBookRefPaths(string projectDir, JsonElement seedInfo, int maxRefs)
    {
        // Only seed-tracked plates; never text-only / sampled paths
        var rels = new List<string>();
        foreach (var prop in new[] { "design_reference_images", "book_reference_images" })
        {
            if (!seedInfo.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var x in arr.EnumerateArray())
            {
                var s = x.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (ProjectStore.IsTextOnlyPlatePath(s!)) continue;
                if (!rels.Contains(s!, StringComparer.OrdinalIgnoreCase))
                    rels.Add(s!);
            }
            if (rels.Count > 0)
                break;
        }

        var full = new List<string>();
        foreach (var rel in rels)
        {
            if (full.Count >= maxRefs) break;
            var norm = rel.Replace('\\', '/').TrimStart('/');
            if (norm.Contains("..", StringComparison.Ordinal)) continue;
            var path = Path.GetFullPath(Path.Combine(projectDir, norm.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(path))
                full.Add(path);
        }

        return full;
    }

    private string GetConfigString(string projectId, string key, string fallback)
    {
        var cfg = _projects.GetConfig(projectId);
        if (cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? fallback;
        return fallback;
    }

    private static bool IsVoiceOnly(string key, JsonElement info)
    {
        if (key.Contains("Narrator", StringComparison.OrdinalIgnoreCase))
            return true;
        if (info.TryGetProperty("display_name_policy", out var pol))
        {
            var p = pol.GetString() ?? "";
            if (p.Contains("never", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

}

public sealed class CharacterDesignResult
{
    public string CharKey { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public List<string> BookRefs { get; set; } = new();
    public string? EditError { get; set; }
}
