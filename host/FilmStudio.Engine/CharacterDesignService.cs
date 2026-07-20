using System.Text.Json;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
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
    private readonly IGrokImageClient _images;
    private readonly IGrokVisionClient _vision;
    private readonly CostReportService _costs;
    private readonly CastVisualLiteralizeService _literalize;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<CharacterDesignService> _log;

    public CharacterDesignService(
        ProjectStore projects,
        IGrokImageClient images,
        IGrokVisionClient vision,
        CostReportService costs,
        CastVisualLiteralizeService literalize,
        IOptions<FilmStudioOptions> opts,
        ILogger<CharacterDesignService> log)
    {
        _projects = projects;
        _images = images;
        _vision = vision;
        _costs = costs;
        _literalize = literalize;
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
        var imageModel = await GetConfigStringAsync(projectId, "image_model_name", _opts.DefaultImageModel, ct)
            .ConfigureAwait(false);
        var imageProvider = await GetConfigStringAsync(projectId, "image_provider", _opts.ImageProvider, ct)
            .ConfigureAwait(false);
        var providerId = ImageApiLimits.ResolveProvider(imageProvider, imageModel);
        var maxRefs = ImageApiLimits.ClampMaxRefs(opts.MaxRefs, imageProvider, imageModel);
        // Wired client today is GrokImageClient (≤3). When GeminiImageClient lands, raise this.
        var clientCap = ImageApiLimits.GrokMaxReferenceImages;
        if (maxRefs > clientCap)
        {
            onProgress?.Invoke(
                $"Provider {providerId} allows {maxRefs} refs; active image client cap is {clientCap} — sending {clientCap}");
            maxRefs = clientCap;
        }
        var maxBook = Math.Clamp(opts.MaxBookHints < 0 ? Math.Max(0, maxRefs - 1) : opts.MaxBookHints, 0, maxRefs);

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

        // Resolve text for this generate, then AI-scrub (base look + literal) via prompt —
        // no special-case regex lists for pajamas / nicknames / etc.
        var descForGen = opts.DescriptionOverride;
        var visForGen = opts.VisualLockOverride;
        if (descForGen is null && seeds.TryGetProperty("description", out var d0))
            descForGen = d0.GetString();
        if (visForGen is null && seeds.TryGetProperty("visual_lock", out var v0))
            visForGen = v0.GetString();

        try
        {
            var (dScrub, vScrub, usedAi) = await _literalize.ScrubLookFieldsAsync(
                charKey,
                description: descForGen,
                visualLock: visForGen,
                model: "grok-4.5",
                onProgress: onProgress,
                ct: ct).ConfigureAwait(false);
            if (usedAi)
            {
                descForGen = dScrub;
                visForGen = vScrub;
                onProgress?.Invoke("AI scrub applied to look text for this generate");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Look AI scrub before portrait gen failed — using raw text");
        }

        // Optional persist of (scrubbed) description / visual_lock from Characters UI
        if (opts.PersistDescription &&
            (opts.DescriptionOverride is not null || opts.VisualLockOverride is not null ||
             descForGen is not null || visForGen is not null))
        {
            _projects.UpdateCharacterSeedText(
                projectId,
                charKey,
                description: descForGen,
                visualLock: visForGen);
            seeds = _projects.GetCharacterSeed(projectId, charKey) ?? seeds;
            onProgress?.Invoke("Saved scrubbed description / visual lock to cast seeds");
        }

        var hasImageHints = editRefs.Count > 0;
        var projectStyle = ReadProjectRenderStyleLock(projectDir);
        var prompt = BuildDesignPrompt(
            charKey,
            seeds,
            hasImageHints,
            descriptionOverride: descForGen,
            visualLockOverride: visForGen,
            projectRenderStyleLock: projectStyle);

        onProgress?.Invoke(
            $"design prompt ready ({prompt.Length} chars) · image_provider={ImageApiLimits.ResolveProvider(imageProvider, imageModel)} max_refs={maxRefs}");
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
                    var primary = editRefs.Take(maxRefs).ToList();
                    blobs = await _images.EditVariantsAsync(
                        prompt,
                        primary,
                        n,
                        aspectRatio: "1:1",
                        model: imageModel,
                        maxRefs: maxRefs,
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
                                maxRefs: 1,
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
                    await _costs.RecordImageGenerationAsync(
                        projectId, 1, imageModel, quality: true, ct: ct);
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
                // Prefer ordered keys from UI (rank 1..N). Fall back to separate lists.
                if (opts.SeedOrderKeys is { Count: > 0 })
                {
                    foreach (var raw in opts.SeedOrderKeys)
                    {
                        if (editRefs.Count >= maxRefs) break;
                        var key = (raw ?? "").Trim().ToLowerInvariant();
                        if (key is "p" or "pref" or "preferred")
                        {
                            Add(preferredPath);
                            continue;
                        }
                        if (key.Length >= 2 && key[0] == 'v' && int.TryParse(key[1..], out var vi) &&
                            vi is >= 1 and <= 3)
                        {
                            Add(Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_0{vi}.png"));
                            continue;
                        }
                        if (key.Length >= 2 && key[0] == 'b' && int.TryParse(key[1..], out var bi) &&
                            bi >= 0 && bi < allBookRefs.Count)
                        {
                            Add(allBookRefs[bi]);
                        }
                    }
                }
                else
                {
                    if (opts.IncludeLockedRef || opts.IncludePreferred)
                        Add(preferredPath);
                    foreach (var vi in opts.VariantIndices.Distinct())
                    {
                        if (vi is < 1 or > 3) continue;
                        Add(Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_0{vi}.png"));
                    }
                    foreach (var bi in opts.BookRefIndices.Distinct())
                    {
                        if (bi < 0 || bi >= allBookRefs.Count) continue;
                        Add(allBookRefs[bi]);
                    }
                }
                onProgress?.Invoke(
                    editRefs.Count == 0
                        ? "Explicit mode: no valid selections — will text-only"
                        : $"Explicit seeds ({editRefs.Count}/{maxRefs}): {string.Join(", ", editRefs.Select(Path.GetFileName))}");
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

    public Task<string> LockVariantAsync(
        string projectId,
        string charKey,
        int variantIndex,
        CancellationToken ct = default)
    {
        if (variantIndex is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(variantIndex), "variant index must be 1..3");
        var projectDir = _projects.GetProjectDir(projectId);
        var fileName = $"{charKey.ToLowerInvariant()}_variant_0{variantIndex}.png";
        var variantPath = Path.Combine(projectDir, "assets", "characters", fileName);
        if (!File.Exists(variantPath))
            throw new InvalidOperationException($"Variant not found: {fileName}");
        return LockFromPathAsync(projectId, charKey, variantPath, ct);
    }

    /// <summary>Sync wrapper for tests only — prefer <see cref="LockVariantAsync"/>.</summary>
    public string LockVariant(string projectId, string charKey, int variantIndex) =>
        LockVariantAsync(projectId, charKey, variantIndex).GetAwaiter().GetResult();

    public Task<string> LockBookRefAsync(
        string projectId,
        string charKey,
        int bookIndex,
        CancellationToken ct = default)
    {
        var path = _projects.ResolveCharacterBookRefPath(projectId, charKey, bookIndex)
            ?? throw new InvalidOperationException($"Book ref {bookIndex} not found for {charKey}");
        return LockFromPathAsync(projectId, charKey, path, ct);
    }

    public string LockBookRef(string projectId, string charKey, int bookIndex) =>
        LockBookRefAsync(projectId, charKey, bookIndex).GetAwaiter().GetResult();

    public async Task<string> LockFromPathAsync(
        string projectId,
        string charKey,
        string sourcePath,
        CancellationToken ct = default)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no reference image to lock.");

        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"Image not found: {sourcePath}");

        await EnsurePortraitStyleAllowedAsync(projectId, charKey, sourcePath, ct).ConfigureAwait(false);

        var projectDir = _projects.GetProjectDir(projectId);
        var refName = ProjectStore.CharacterRefFileName(charKey);
        var dest = Path.Combine(projectDir, "assets", "characters", refName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        // Copy (convert jpg→png name still just copies bytes if formats differ — OK for video ref)
        File.Copy(sourcePath, dest, overwrite: true);

        FinalizeLock(projectId, charKey, dest, $"Locked reference from {Path.GetFileName(sourcePath)}");
        return dest;
    }

    /// <summary>Sync wrapper for tests only — prefer <see cref="LockFromPathAsync"/>.</summary>
    public string LockFromPath(string projectId, string charKey, string sourcePath) =>
        LockFromPathAsync(projectId, charKey, sourcePath).GetAwaiter().GetResult();

    /// <summary>
    /// Operator upload: save image bytes as the locked character ref (preferred look for video).
    /// Accepts png/jpg/webp/gif; stored as the canonical <c>*_ref.png</c> name (bytes as-is).
    /// </summary>
    public async Task<string> LockFromUploadAsync(
        string projectId,
        string charKey,
        Stream content,
        string? originalFileName = null,
        CancellationToken ct = default)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character seed: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no reference image to lock.");

        if (content is null || !content.CanRead)
            throw new InvalidOperationException("Empty upload stream");

        var projectDir = _projects.GetProjectDir(projectId);
        var charDir = Path.Combine(projectDir, "assets", "characters");
        Directory.CreateDirectory(charDir);
        var staging = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_upload_staging_{Guid.NewGuid():N}.bin");

        try
        {
            await using (var fs = File.Create(staging))
            {
                await content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (new FileInfo(staging).Length < 64)
                throw new InvalidOperationException("Uploaded image is empty or too small.");

            await EnsurePortraitStyleAllowedAsync(projectId, charKey, staging, ct).ConfigureAwait(false);

            var refName = ProjectStore.CharacterRefFileName(charKey);
            var dest = Path.Combine(charDir, refName);
            File.Copy(staging, dest, overwrite: true);

            var label = string.IsNullOrWhiteSpace(originalFileName)
                ? "operator upload"
                : Path.GetFileName(originalFileName);
            FinalizeLock(projectId, charKey, dest, $"Locked reference from upload ({label})");
            return dest;
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Vision gate: refuse to lock a sketch/illustration when the project is photoreal
    /// (or a photo stock plate when the project is picture-book). Fail closed on mismatch.
    /// </summary>
    internal async Task EnsurePortraitStyleAllowedAsync(
        string projectId,
        string charKey,
        string imagePath,
        CancellationToken ct = default)
    {
        if (!_opts.RequirePortraitStyleGate)
        {
            _log.LogWarning("Portrait style gate disabled (RequirePortraitStyleGate=false)");
            return;
        }

        var projectDir = _projects.GetProjectDir(projectId);
        var styleLock = ReadProjectRenderStyleLock(projectDir);
        // No project medium → nothing to enforce (ambiguous mixed projects)
        if (string.IsNullOrWhiteSpace(styleLock))
            return;

        // Need a clear medium preference; pure free-form style text still runs the gate.
        var wantIllustrated = PrefersIllustratedPortraitStyle(
            styleLock, hasImageHints: false, isAnimal: false);
        // PrefersIllustrated with empty photoreal cues returns false only when photoreal cues
        // match; when style is neither, it returns false (not hasImageHints). Re-check:
        var hasPhotoCues = RegexContains(styleLock,
            @"\b(photoreal|photo-?real|live[- ]?action|cinematic|film photography|" +
            @"period drama|gothic drama|naturalistic skin)\b");
        var hasIllustCues = RegexContains(styleLock,
            @"\b(picture[- ]?book|illustration|illustrated|cartoon|painted cartoon|" +
            @"children'?s book|storybook|stylized 3d|cg animated)\b");
        if (!hasPhotoCues && !hasIllustCues)
            return; // style present but medium ambiguous — do not block lock

        if (!_vision.IsConfigured)
            throw new InvalidOperationException(
                $"Cannot lock {charKey}: portrait style gate requires vision (XAI_API_KEY) " +
                "to verify the image matches project medium. " +
                "Set FilmStudio__RequirePortraitStyleGate=false only as emergency bypass.");

        var expected = hasPhotoCues && !wantIllustrated ? "photoreal" : "illustration";
        var prompt =
            "PORTRAIT STYLE GATE — judge ONLY the attached character portrait image.\n" +
            $"Project style lock: {styleLock.Trim()}\n" +
            $"Expected medium for this project: {expected}\n\n" +
            "Classify the image medium:\n" +
            "- photoreal = live-action photography / cinematic photo of a real person or animal " +
            "(natural skin/fur, photographic lighting)\n" +
            "- illustration = painted, drawn, cartoon, picture-book, anime, comic, or 3D-toon\n" +
            "- sketch = pencil/charcoal/ink line art, unfinished drawing, grayscale sketch look\n" +
            "- other = diagram, collage, text, unusable\n\n" +
            "pass=true ONLY if the image medium matches Expected.\n" +
            "For expected=photoreal: FAIL sketch, illustration, cartoon, pencil drawing.\n" +
            "For expected=illustration: FAIL pure photoreal stock photography.\n\n" +
            "JSON ONLY (no markdown):\n" +
            "{\"pass\":true|false,\"medium\":\"photoreal|illustration|sketch|other\",\"reason\":\"short\"}\n";

        string raw;
        try
        {
            raw = await _vision.CompleteWithImagesAsync(
                prompt,
                new[] { imagePath },
                model: "grok-4.5",
                detail: "low",
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot lock {charKey}: portrait style gate vision failed ({ex.Message}). " +
                "Refusing to lock without a medium check.",
                ex);
        }

        var gate = ParsePortraitStyleGateResponse(raw);
        if (gate is null)
            throw new InvalidOperationException(
                $"Cannot lock {charKey}: portrait style gate returned unparseable response. " +
                $"Raw: {TrimForError(raw, 240)}");

        var result = gate.Value;
        if (!result.Pass)
        {
            throw new InvalidOperationException(
                $"Cannot lock {charKey}: portrait medium '{result.Medium}' does not match " +
                $"project expected '{expected}'. {result.Reason} " +
                "Re-generate with the project style lock (photoreal vs picture-book), then lock again.");
        }

        // Extra hard reject: never lock sketch on photoreal projects even if model said pass.
        if (expected == "photoreal" &&
            result.Medium is "sketch" or "illustration")
        {
            throw new InvalidOperationException(
                $"Cannot lock {charKey}: image is '{result.Medium}' but project is photoreal/live-action. " +
                $"{result.Reason}");
        }

        _log.LogInformation(
            "Portrait style gate OK for {CharKey}: medium={Medium} expected={Expected}",
            charKey, result.Medium, expected);
    }

    public static PortraitStyleGateResult? ParsePortraitStyleGateResponse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^```(?:json)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*```$", "").Trim();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var pass = false;
            if (root.TryGetProperty("pass", out var p))
            {
                pass = p.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(p.GetString(), out var b) && b,
                    _ => false,
                };
            }
            var medium = root.TryGetProperty("medium", out var m)
                ? (m.GetString() ?? "other").Trim().ToLowerInvariant()
                : "other";
            if (medium is "photo" or "photographic" or "live-action" or "live_action")
                medium = "photoreal";
            if (medium is "drawn" or "drawing" or "cartoon" or "picture-book" or "picture_book")
                medium = "illustration";
            var reason = root.TryGetProperty("reason", out var r) ? (r.GetString() ?? "").Trim() : "";
            return new PortraitStyleGateResult(pass, medium, reason);
        }
        catch
        {
            return null;
        }
    }

    private static string TrimForError(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public readonly record struct PortraitStyleGateResult(bool Pass, string Medium, string Reason);

    private void FinalizeLock(string projectId, string charKey, string destPath, string changeNote)
    {
        // Keep generated variants on disk so they stay available as reference tiles
        // for the next regenerate (preferred lock is a separate *_ref.png copy).
        _projects.UpdateCharacterSeedPlaceholder(projectId, charKey, ProjectStore.CharacterRefFileName(charKey));
        _projects.MarkCharacterChanged(projectId, charKey, changeNote);
        _ = destPath;
    }

    /// <summary>
    /// Delete a character reference image: preferred lock, generated variant, or book plate.
    /// Book plates are also removed from cast seed design_reference_images.
    /// </summary>
    public void DeleteImage(string projectId, string charKey, string kind, int index = 0)
    {
        var seeds = _projects.GetCharacterSeed(projectId, charKey)
            ?? throw new InvalidOperationException($"Unknown character: {charKey}");
        if (IsVoiceOnly(charKey, seeds))
            throw new InvalidOperationException($"{charKey} is voice-only — no image to delete.");

        var projectDir = _projects.GetProjectDir(projectId);
        var charDir = Path.Combine(projectDir, "assets", "characters");
        var k = (kind ?? "").Trim().ToLowerInvariant();

        if (k is "preferred" or "p" or "ref" or "lock" or "locked")
        {
            foreach (var name in ProjectStore.CharacterRefFileCandidates(charKey))
            {
                var full = Path.Combine(charDir, name);
                try { if (File.Exists(full)) File.Delete(full); } catch { /* ignore */ }
            }
            _projects.UpdateCharacterSeedPlaceholder(projectId, charKey, "");
            _projects.MarkCharacterChanged(projectId, charKey, "Deleted preferred/locked picture");
            return;
        }

        if (k is "variant" or "v")
        {
            var i = Math.Clamp(index, 1, 9);
            var full = Path.Combine(charDir, $"{charKey.ToLowerInvariant()}_variant_0{i}.png");
            if (!File.Exists(full))
                throw new InvalidOperationException($"Variant {i} not found.");
            File.Delete(full);
            _projects.MarkCharacterChanged(projectId, charKey, $"Deleted variant {i}");
            return;
        }

        if (k is "book" or "bookref" or "b")
        {
            // Seed paths are 0-based indices into design_reference_images
            _projects.RemoveCharacterBookRef(projectId, charKey, index);
            // Also delete common bookref filename if present
            var prefix = charKey.ToLowerInvariant() + "_bookref_";
            // index is 0-based in seeds; files are 1-based bookref_1
            var fileIdx = index + 1;
            if (Directory.Exists(charDir))
            {
                foreach (var fi in new DirectoryInfo(charDir).GetFiles($"{prefix}{fileIdx}.*"))
                {
                    try { fi.Delete(); } catch { /* ignore */ }
                }
            }
            _projects.MarkCharacterChanged(projectId, charKey, $"Deleted book picture {index}");
            return;
        }

        throw new InvalidOperationException($"Unknown image kind: {kind}");
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

    /// <summary>
    /// Project medium from cast extract (<c>render_style_lock</c>). Null when missing.
    /// </summary>
    internal static string? ReadProjectRenderStyleLock(string projectDir)
    {
        try
        {
            var castPath = Path.Combine(projectDir, "source", ScreenplayService.CastSeedsFileName);
            if (!File.Exists(castPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(castPath));
            if (doc.RootElement.TryGetProperty("render_style_lock", out var rsl) &&
                rsl.ValueKind == JsonValueKind.String &&
                rsl.GetString() is { Length: > 0 } s)
                return s.Trim();
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    /// Illustrated / picture-book when project or book art needs that medium;
    /// photoreal / live-action when cast style says so (e.g. Tell-Tale Heart).
    /// </summary>
    public static bool PrefersIllustratedPortraitStyle(
        string? projectRenderStyleLock,
        bool hasImageHints,
        bool isAnimal)
    {
        var style = projectRenderStyleLock ?? "";
        if (style.Length > 0)
        {
            // Explicit live-action / photoreal project wins over legacy picture-book default.
            if (RegexContains(style,
                    @"\b(photoreal|photo-?real|live[- ]?action|cinematic|film photography|" +
                    @"period drama|gothic drama|naturalistic skin)\b"))
                return false;
            if (RegexContains(style,
                    @"\b(picture[- ]?book|illustration|illustrated|cartoon|painted cartoon|" +
                    @"children'?s book|storybook|stylized 3d|cg animated)\b"))
                return true;
        }

        // No project style: book plates or animal heroes default to matching illustration medium.
        return hasImageHints || isAnimal;
    }

    private static bool RegexContains(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string BuildDesignPrompt(
        string charKey,
        JsonElement seedInfo,
        bool hasImageHints,
        string? descriptionOverride = null,
        string? visualLockOverride = null,
        string? projectRenderStyleLock = null)
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

        var isAnimalDog = CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
            charKey, ageBand, description, visualLock, "dog");
        var isAnimalOther =
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "cat") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "rabbit") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "bunny") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "bear") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "fox");
        var isAnimal = isAnimalDog || isAnimalOther;
        var isHumanAdult = CharacterVisualTextScrubber.IsHumanAdultCharacter(
            charKey, ageBand, description, visualLock);

        var illustrated = PrefersIllustratedPortraitStyle(projectRenderStyleLock, hasImageHints, isAnimal);

        var speciesClause = "";
        if (ageBand.StartsWith("child", StringComparison.OrdinalIgnoreCase) ||
            charKey.EndsWith("_Young", StringComparison.OrdinalIgnoreCase))
        {
            speciesClause =
                "SPECIES/AGE: CHILD human — child proportions, youthful face; not adult, not aged-up. ";
        }
        else if (ageBand.StartsWith("teen", StringComparison.OrdinalIgnoreCase) ||
                 charKey.EndsWith("_Teen", StringComparison.OrdinalIgnoreCase))
        {
            speciesClause =
                "SPECIES/AGE: TEEN human — younger than adult version; not middle-aged. ";
        }
        else if (isAnimalDog)
        {
            speciesClause = illustrated
                ? "SPECIES: DOG character (animal), not a human. " +
                  "Keep the illustrated breed/look from the book art — not a photoreal stock dog. " +
                  "Natural fur/coat only unless a reference image clearly shows clothing. "
                : "SPECIES: DOG (animal), not a human. Photoreal coat and anatomy matching the project medium. " +
                  "Natural fur only unless clothing is part of the locked look. ";
        }
        else if (isAnimalOther)
        {
            speciesClause = illustrated
                ? "SPECIES: animal character, not a person. Match the book-art creature; " +
                  "not photoreal wildlife photography. No costume unless clearly in refs. "
                : "SPECIES: animal character, not a person. Photoreal anatomy matching the project medium. ";
        }
        else if (isHumanAdult)
        {
            speciesClause = illustrated
                ? "SPECIES: HUMAN adult — a person, not an animal. " +
                  "Same illustrated picture-book medium as the film; not photoreal stock photography. "
                : "SPECIES: HUMAN adult — a real person, not an animal, not a drawing. " +
                  "Photoreal skin texture and period wardrobe matching the project medium. ";
        }

        var familyClause = "";
        if (!string.IsNullOrWhiteSpace(variantOf))
        {
            familyClause =
                $"FAMILY: younger version of {variantOf} " +
                "(same ethnicity/hair family, recognizable related features). ";
        }

        // Prompt-time text prep: keep filmable words; image model still gets strong IGNORE rules
        var descSafe = CharacterVisualTextScrubber.ScrubVisualProse(description);
        var visualSafe = CharacterVisualTextScrubber.ScrubVisualProse(visualLock);

        // Priority-ordered instructions work better than a long free-form paragraph for Imagine.
        const string ignoreRules =
            "IGNORE in the text notes (do not draw these): " +
            "later-story wardrobe or outfit changes; 'later wears…', 'afterwards…', 'once X is on…'; " +
            "scene actions and plot (pointing, offering treats, sleeping, chasing); " +
            "figurative nicknames or idioms taken as objects (food-as-hat, metaphor props); " +
            "model-sheet labels, arrows, color swatches, UI chrome. ";

        const string outputRules =
            "OUTPUT: one clean continuity portrait, head and upper body, facing camera, " +
            "plain soft background. No collage, no split views, no text overlays. ";

        // Honor project render_style_lock (live-action period, picture-book, etc.).
        // Legacy default was always picture-book — wrong for photoreal projects like Tell-Tale Heart.
        string styleLock;
        if (!string.IsNullOrWhiteSpace(projectRenderStyleLock))
        {
            var cleaned = projectRenderStyleLock.Trim().TrimEnd('.');
            if (!cleaned.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase))
                cleaned = "STYLE LOCK: " + cleaned;
            styleLock = illustrated
                ? $"{cleaned}. Match that illustrated medium exactly — not photoreal stock, not a different art style. "
                : $"{cleaned}. Photoreal / live-action continuity portrait — natural skin pores and fabric, " +
                  "NOT a sketch, NOT pencil drawing, NOT illustration, NOT cartoon, NOT anime, NOT 3D CGI beauty face. ";
        }
        else if (illustrated)
        {
            styleLock =
                "STYLE LOCK (hard): children's picture-book illustration matching the book references — " +
                "soft painted cartoon / illustrated medium, simplified shapes, gentle shading. " +
                "NOT photorealistic, NOT live-action photography, NOT stock-photo animal, " +
                "NOT hyper-detailed fur photography, NOT 3D CGI render. " +
                "If book plates are attached, copy their line, color, and medium exactly. ";
        }
        else
        {
            styleLock =
                "STYLE LOCK (hard): photoreal live-action continuity portrait — naturalistic face and wardrobe. " +
                "NOT a sketch, NOT pencil/charcoal drawing, NOT illustration, NOT cartoon, NOT anime. ";
        }

        var lookBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(descSafe))
            lookBits.Add(descSafe.Trim().TrimEnd('.'));
        if (!string.IsNullOrWhiteSpace(visualSafe))
            lookBits.Add("Hard constraints: " + visualSafe.Trim().TrimEnd('.'));
        var lookNotes = lookBits.Count > 0
            ? string.Join(". ", lookBits) + "."
            : "Match the character identity from context.";

        if (hasImageHints)
        {
            var matchBody = isAnimal
                ? (illustrated
                    ? "Match species, coat pattern, ears, and face shape from the illustrated book references. "
                    : "Match species, coat, and face from the attached reference images. ")
                : (illustrated
                    ? "Match face, hair, and default clothing from the preferred illustrated reference. "
                    : "Match face, hair, and default clothing from the preferred reference photo/portrait. ");

            return
                $"CHARACTER CONTINUITY PORTRAIT of {display}. " +
                styleLock +
                "PRIORITY 1 — IMAGES: The first attached image is the authoritative identity AND art style. " +
                "Further images are the SAME character (markings/style only). " +
                "When text and images disagree, trust the images. " +
                "Skip any reference that is mostly printed text with no character art. " +
                "Do not redesign; do not invent a new outfit not clearly visible in the character art. " +
                matchBody +
                speciesClause +
                familyClause +
                "PRIORITY 2 — BASE LOOK ONLY: default everyday appearance for a faceplate/lock. " +
                (isAnimal
                    ? "If book art shows an animal without clothes, draw no clothes or costumes. "
                    : "Use only default clothes visible in refs; do not add later-story costumes. ") +
                ignoreRules +
                $"PRIORITY 3 — TEXT NOTES (secondary hints only): {lookNotes} " +
                outputRules;
        }

        return
            $"CHARACTER CONTINUITY PORTRAIT of {display}. " +
            styleLock +
            "BASE LOOK ONLY — default everyday appearance for a faceplate/lock, not a story beat. " +
            speciesClause +
            familyClause +
            ignoreRules +
            (isAnimal
                ? (illustrated
                    ? "Illustrated animal appearance; clothing only if text clearly states it as the usual look (not 'later'). "
                    : "Photoreal animal appearance; clothing only if text states it as the usual look. ")
                : "Default clothes only; skip later-story outfit changes. ") +
            $"LOOK: {lookNotes} " +
            outputRules;
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

    private async Task<string> GetConfigStringAsync(
        string projectId,
        string key,
        string fallback,
        CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        if (cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? fallback;
        return fallback;
    }

    private static bool IsVoiceOnly(string key, JsonElement info)
    {
        // Prefer cast seed policy. Do not force voice-only just because key is "Narrator"
        // (on-camera confessor / POV roles are common).
        if (info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("display_name_policy", out var pol))
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
