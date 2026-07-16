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
    public async Task<CharacterDesignResult> GenerateVariantsAsync(
        string projectId,
        string charKey,
        int n = 0,
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

        // 1 if already locked (refresh/refine), 3 if first lock set
        var alreadyLocked = _projects.ResolveCharacterRefPath(projectId, charKey) is not null;
        if (n <= 0)
            n = alreadyLocked ? 1 : 3;
        onProgress?.Invoke(
            alreadyLocked
                ? $"Locked ref present → generating {n} variant(s)"
                : $"No locked ref → generating {n} variants to pick from");

        var bookRefs = ResolveBookRefPaths(projectDir, seeds, maxRefs: 3);
        // Prefer locked ref as edit guide when refining
        var editRefs = new List<string>();
        var lockedPath = _projects.ResolveCharacterRefPath(projectId, charKey);
        if (lockedPath is not null)
            editRefs.Add(lockedPath);
        foreach (var br in bookRefs)
        {
            if (!editRefs.Contains(br, StringComparer.OrdinalIgnoreCase))
                editRefs.Add(br);
        }

        var prompt = BuildDesignPrompt(charKey, seeds, hasBookRefs: editRefs.Count > 0);
        var imageModel = GetConfigString(projectId, "image_model_name", _opts.DefaultImageModel);

        onProgress?.Invoke($"design prompt ready ({prompt.Length} chars)");
        IReadOnlyList<byte[]> blobs;
        var mode = "text_only";
        string? editError = null;

        if (editRefs.Count > 0)
        {
            onProgress?.Invoke(
                $"Using {editRefs.Count} ref image(s): " +
                string.Join(", ", editRefs.Select(Path.GetFileName)));
            try
            {
                // Prefer single best ref first (locked or first book plate)
                blobs = await _images.EditVariantsAsync(
                    prompt,
                    editRefs.Take(1).ToList(),
                    n,
                    aspectRatio: "1:1",
                    model: imageModel,
                    onProgress: onProgress,
                    ct: ct);
                mode = alreadyLocked ? "locked_ref_edit" : "book_edit";
            }
            catch (Exception ex)
            {
                editError = ex.Message;
                onProgress?.Invoke($"Primary-ref edit failed ({ex.Message}); retrying multi-ref…");
                try
                {
                    blobs = await _images.EditVariantsAsync(
                        prompt,
                        editRefs.Take(3).ToList(),
                        n,
                        aspectRatio: "1:1",
                        model: imageModel,
                        onProgress: onProgress,
                        ct: ct);
                    mode = alreadyLocked ? "locked_ref_edit_multi" : "book_edit_multi";
                    editError = null;
                }
                catch (Exception ex2)
                {
                    // Match product rule: do NOT invent a different look via text-only
                    throw new InvalidOperationException(
                        $"Reference character design failed for {charKey}. " +
                        $"References: {string.Join(", ", editRefs.Select(Path.GetFileName))}. " +
                        $"API error: {editError}; multi={ex2.Message}. " +
                        "Fix API/key or re-extract book images; text-only fallback is disabled.",
                        ex2);
                }
            }
        }
        else
        {
            onProgress?.Invoke("No book/locked images — text-only prompt (likeness may not match source art)");
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
            BookRefs = bookRefs.Select(Path.GetFileName).Where(s => s is not null).Cast<string>().ToList(),
            EditError = editError,
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

    public bool Unlock(string projectId, string charKey)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey);
        if (seeds is null) return false;
        if (IsVoiceOnly(charKey, seeds.Value))
            throw new InvalidOperationException($"{charKey} is voice-only — nothing to unlock.");

        var projectDir = _projects.GetProjectDir(projectId);
        var removed = false;
        // Delete any candidate locked ref names (placeholder vs short alias)
        var existing = _projects.ResolveCharacterRefPath(projectId, charKey);
        if (existing is not null)
        {
            try { File.Delete(existing); removed = true; } catch { /* ignore */ }
        }

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

        return removed;
    }

    // ---- helpers ----

    private static string BuildDesignPrompt(string charKey, JsonElement seedInfo, bool hasBookRefs)
    {
        var description = seedInfo.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
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

        const string treatment = "cinematic lighting";
        if (hasBookRefs)
        {
            return
                $"Create a clean character model-sheet portrait of {display} for film continuity. " +
                "MATCH the character identity, colors, markings, and children's-book illustration style " +
                "from the reference image(s) as closely as possible — same dog/person as in the book art. " +
                "Do NOT invent a different breed, palette, or realistic photo style unless the reference is photo. " +
                $"Description: {description}. {ageClause}{familyClause}" +
                "Character centered, facing camera, plain soft studio or simple background, " +
                "full head and upper body clear for video reference. " +
                $"Keep the whimsical picture-book look of the source art; {treatment}.";
        }

        return
            $"A detailed portrait model-sheet of {display}: {description}. " +
            $"{ageClause}{familyClause}" +
            $"Character centered in frame, look straight at camera, neutral expression, {treatment}. " +
            "If this is a children's picture-book character, use illustrated storybook style " +
            "(not a photorealistic stock photo). Isolated plain soft background.";
    }

    private static List<string> ResolveBookRefPaths(string projectDir, JsonElement seedInfo, int maxRefs)
    {
        var rels = new List<string>();
        foreach (var prop in new[] { "design_reference_images", "book_reference_images" })
        {
            if (!seedInfo.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var x in arr.EnumerateArray())
            {
                var s = x.GetString();
                if (!string.IsNullOrWhiteSpace(s) && !rels.Contains(s!))
                    rels.Add(s!);
            }
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
