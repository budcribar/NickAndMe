using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// Sort book page plates onto character seeds as design_reference_images.
/// Cast comes from Fountain (in-memory); plates persist to source/cast_seeds.json
/// and are mirrored into the blueprint when present.
/// Order of attack: cast <c>source_image_pages</c> → OCR name hits in <c>book_full.txt</c>
/// (neighbor art pages) → optional Grok vision on remaining art (early-stop at 3/character) →
/// heuristic fill for empties. pipeline_state.character_plates.sorted_by_character records completion.
/// </summary>
public sealed class CharacterBookPlateService
{
    private readonly ProjectStore _projects;
    private readonly IGrokVisionClient _vision;
    private readonly PlateRankClassifier? _plateRank;
    private readonly ILogger<CharacterBookPlateService> _log;

    public CharacterBookPlateService(
        ProjectStore projects,
        IGrokVisionClient vision,
        ILogger<CharacterBookPlateService> log,
        PlateRankClassifier? plateRank = null)
    {
        _projects = projects;
        _vision = vision;
        _log = log;
        _plateRank = plateRank;
    }

    public async Task<FilmStudio.Core.Models.AttachCharacterPlatesResult> AttachAsync(
        string projectId,
        bool force = false,
        bool copyIntoAssets = true,
        string? onlyCharKey = null,
        bool useGrok = true,
        string visionModel = "grok-4.5",
        int maxImages = 32,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        var castSeedsPath = ScreenplayService.GetCastSeedsPath(_projects, projectId);
        var result = new FilmStudio.Core.Models.AttachCharacterPlatesResult();
        var platesState = _projects.GetCharacterPlatesState(projectId);

        if (!force &&
            string.IsNullOrWhiteSpace(onlyCharKey) &&
            platesState.SortedByCharacter)
        {
            result.Ok = true;
            result.AlreadySorted = true;
            result.SortedByCharacter = true;
            result.SortedAt = platesState.SortedAt;
            result.Reason = "already_sorted";
            result.Method = platesState.Method;
            onProgress?.Invoke($"Already sorted at {platesState.SortedAt} ({platesState.Method})");
            return result;
        }

        // Prefer cast_seeds.json (full cast incl. silent leads); else Fountain
        JsonObject seeds;
        try
        {
            seeds = await LoadOrBuildCastSeedsAsync(projectId, castSeedsPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Reason = $"no_cast:{ex.Message}";
            return result;
        }

        if (seeds.Count == 0)
        {
            result.Reason = "no_character_seeds";
            onProgress?.Invoke("No cast seeds — build cast from screenplay first.");
            return result;
        }

        var inventoryAll = await LoadBookImageInventoryAsync(projectDir).ConfigureAwait(false);
        var inventory = inventoryAll.Where(r => !IsLikelyTextLayout(r)).ToList();
        if (inventory.Count == 0)
        {
            result.Reason = "no_illustrated_book_images";
            if (string.IsNullOrWhiteSpace(onlyCharKey))
            {
                // Don't claim "sorted" when we had cast but zero plates — operator can re-run after book images exist
                result.SortedByCharacter = false;
                result.Method = "none";
            }
            return result;
        }

        var cast = BuildCastHints(seeds, onlyCharKey);
        if (cast.Count == 0)
        {
            result.Reason = "no_on_screen_cast";
            return result;
        }

        var charsDir = Path.Combine(projectDir, "assets", "characters");
        if (copyIntoAssets)
            Directory.CreateDirectory(charsDir);

        // scores[charKey] = list of (row, score)
        var scores = cast.ToDictionary(
            c => c.Key,
            _ => new List<(BookImageRow Row, double Score)>(),
            StringComparer.OrdinalIgnoreCase);

        // Seed from AI cast source_image_pages first (high confidence)
        var fromPages = SeedScoresFromSourceImagePages(scores, inventory, seeds, onlyCharKey);
        if (fromPages > 0)
            onProgress?.Invoke($"Seeded {fromPages} plate hit(s) from cast source_image_pages…");

        // OCR text (book_full.txt already produced at import) → name hit → neighbor art pages
        var ocrPages = await BookOcrPlateShortlist.TryLoadAsync(projectDir, ct).ConfigureAwait(false);
        var fromOcr = 0;
        if (ocrPages.Count > 0)
        {
            fromOcr = SeedScoresFromOcrNeighbors(scores, inventory, seeds, ocrPages, onlyCharKey, maxPerCharacter: 3);
            if (fromOcr > 0)
                onProgress?.Invoke(
                    $"OCR text→art neighbors: {fromOcr} plate hit(s) from {BookOcrPlateShortlist.BookFullFileName}…");
        }
        else
        {
            onProgress?.Invoke("No book_full.txt OCR — skipping text→art neighbor shortlist");
        }

        var methodParts = new List<string>();
        if (fromPages > 0) methodParts.Add("source_image_pages");
        if (fromOcr > 0) methodParts.Add("ocr_neighbor");
        var method = methodParts.Count > 0 ? string.Join("+", methodParts) : "heuristic";
        var wantGrok = useGrok && _vision.IsConfigured && string.IsNullOrWhiteSpace(onlyCharKey);
        if (useGrok && !_vision.IsConfigured)
            onProgress?.Invoke("XAI_API_KEY missing — using page seeds / OCR / heuristic plate sort");

        // If OCR (+ source pages) already filled every cast member to 3, skip vision API entirely
        var ocrComplete = cast.All(c =>
            scores.TryGetValue(c.Key, out var list) && list.Count >= 3);

        if (wantGrok && !ocrComplete)
        {
            method = string.IsNullOrEmpty(method) || method == "heuristic"
                ? "grok_vision"
                : method + "+grok_vision";
            var toScan = BuildVisionScanList(inventory, scores, seeds, ocrPages, Math.Clamp(maxImages, 4, 64));
            onProgress?.Invoke(
                $"Grok vision: classifying up to {toScan.Count} book image(s) for {cast.Count} character(s) (OCR shortlist first)…");
            result.ImagesClassified = 0;
            result.ImagesSkippedText = 0;

            for (var i = 0; i < toScan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (CastScoresComplete(scores, cast, maxPerCharacter: 3))
                {
                    onProgress?.Invoke(
                        $"All cast have {3} plate candidate(s) — stopping vision early ({result.ImagesClassified} image(s) scanned)");
                    break;
                }

                var row = toScan[i];
                onProgress?.Invoke(
                    $"Grok vision {i + 1}/{toScan.Count}: {Path.GetFileName(row.AbsPath)} (p{row.Page})…");
                try
                {
                    var cls = await _vision.ClassifyCharactersOnImageAsync(
                        row.AbsPath, row.Page, cast, visionModel, ct);
                    result.ImagesClassified++;

                    if (cls.PageKind is "text_heavy" or "text")
                    {
                        result.ImagesSkippedText++;
                        continue;
                    }

                    // One page → one best cast match (avoids multiple cast claiming the same plate)
                    var bestMatch = PickBestMatch(cls);
                    if (bestMatch is null) continue;
                    if (!scores.TryGetValue(bestMatch.Key, out var list)) continue;
                    if (list.Count >= 3) continue; // already full
                    list.Add((row, bestMatch.Confidence));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Vision classify failed for {File}", row.Name);
                    onProgress?.Invoke($"  skip {row.Name}: {ex.Message}");
                }
            }

            // Fill any still-empty on-screen cast from pages / hero heuristic
            var emptyCast = cast.Count(c =>
                !scores.TryGetValue(c.Key, out var list) || list.Count == 0);
            if (emptyCast > 0)
            {
                onProgress?.Invoke(
                    $"{emptyCast} cast member(s) still empty — filling from pages / hero heuristic…");
                method += "+heuristic_fill";
                await ApplyHeuristicScoresAsync(scores, inventory, seeds, onlyCharKey, onlyEmpty: true, heroOnly: false, ct)
                    .ConfigureAwait(false);
            }
        }
        else if (wantGrok && ocrComplete)
        {
            onProgress?.Invoke("OCR shortlist already filled cast (3 each) — skipping vision API");
            method += method.Contains("ocr", StringComparison.Ordinal) ? "" : "+ocr_neighbor";
        }
        else if (!ocrComplete)
        {
            method = fromPages > 0 || fromOcr > 0 ? method + "+heuristic" : "heuristic";
            onProgress?.Invoke("Heuristic plate sort (OCR neighbors + cast pages + illustration ranking)…");
            await ApplyHeuristicScoresAsync(
                    scores, inventory, seeds, onlyCharKey,
                    onlyEmpty: fromPages > 0 || fromOcr > 0, ct: ct)
                .ConfigureAwait(false);
        }

        result.Method = method;

        // Exclusive assignment: each source image (and each page) to at most one character
        // → stops B0≈B2 duplicates and Mom/Dad sharing the same dog plate
        var assigned = AssignPlatesExclusively(scores, maxPerCharacter: 3);
        onProgress?.Invoke(
            $"Exclusive assign: {assigned.Sum(kv => kv.Value.Count)} unique plate(s) across {assigned.Count} character(s)");

        // Write top plates per character
        var index = 0;
        foreach (var (key, seedNode) in seeds.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (seedNode is not JsonObject seed)
            {
                index++;
                continue;
            }

            if (IsVoiceOnly(key, seed))
            {
                seed.Remove("design_reference_images");
                seed.Remove("book_reference_images");
                CleanupStaleBookrefs(charsDir, key, keepCount: 0);
                result.CharactersSkipped++;
                result.AttachedByCharacter[key] = new List<string> { "(voice_only)" };
                index++;
                continue;
            }

            if (!force &&
                seed["design_reference_images"] is JsonArray existing &&
                existing.Count > 0 &&
                existing.Any(x =>
                {
                    var s = x?.GetValue<string>() ?? "";
                    return s.Length > 0 && !ProjectStore.IsTextOnlyPlatePath(s);
                }))
            {
                result.CharactersSkipped++;
                result.AttachedByCharacter[key] = existing
                    .Select(x => x?.GetValue<string>() ?? "")
                    .Where(s => s.Length > 0 && !ProjectStore.IsTextOnlyPlatePath(s))
                    .ToList();
                index++;
                continue;
            }

            assigned.TryGetValue(key, out var picks);
            picks ??= new List<BookImageRow>();

            // Keep AI source_image_pages when present; else record pages we actually attached
            var priorPages = PagesForSeed(seed);
            var usedPages = picks.Where(p => p.Page > 0).Select(p => p.Page).Distinct().ToList();
            if (usedPages.Count > 0)
                seed["source_image_pages"] = new JsonArray(usedPages.Select(p => (JsonNode)p).ToArray());
            else if (priorPages.Count > 0)
                seed["source_image_pages"] = new JsonArray(priorPages.Select(p => (JsonNode)p).ToArray());

            var relPaths = CopyPlates(projectDir, charsDir, key, picks, copyIntoAssets);
            CleanupStaleBookrefs(charsDir, key, keepCount: relPaths.Count);

            if (relPaths.Count == 0)
            {
                seed.Remove("design_reference_images");
                seed.Remove("book_reference_images");
                result.CharactersSkipped++;
                result.AttachedByCharacter[key] = new List<string> { $"(none via {method})" };
            }
            else
            {
                seed["design_reference_images"] = new JsonArray(
                    relPaths.Select(r => (JsonNode)r).ToArray());
                seed["book_reference_images"] = new JsonArray(
                    relPaths.Select(r => (JsonNode)r).ToArray());
                result.CharactersUpdated++;
                result.AttachedByCharacter[key] = relPaths;
                _log.LogInformation(
                    "Attached {Count} book plate(s) to {Key} via {Method}",
                    relPaths.Count, key, method);
                onProgress?.Invoke(
                    $"{key}: {relPaths.Count} plate(s) pages=[{string.Join(",", picks.Select(p => p.Page))}]");
            }

            index++;
        }

        // Persist plates to cast_seeds.json (Fountain remains story source of truth)
        var castRoot = new JsonObject
        {
            ["schema_version"] = "cast_seeds.v1",
            ["character_seed_tokens"] = seeds,
        };
        try
        {
            if (File.Exists(castSeedsPath))
            {
                var bak = castSeedsPath + $".bak_attach_plates_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(castSeedsPath, bak, overwrite: true);
            }
        }
        catch { /* ignore */ }

        Directory.CreateDirectory(Path.GetDirectoryName(castSeedsPath)!);
        await File.WriteAllTextAsync(
            castSeedsPath,
            castRoot.ToJsonString(JsonDefaults.Indented) + "\n",
            ct).ConfigureAwait(false);

        await TryMirrorBlueprintAsync(projectDir, seeds);

        var onScreenNeedPlates = cast.Count; // BuildCastHints already skips voice-only
        var anyPlates = result.CharactersUpdated > 0;
        if (string.IsNullOrWhiteSpace(onlyCharKey))
        {
            // Only mark sorted when we actually attached plates (or cast is empty)
            if (anyPlates || onScreenNeedPlates == 0)
            {
                _projects.MarkCharacterPlatesSorted(projectId, result.CharactersUpdated, method: method);
                var after = _projects.GetCharacterPlatesState(projectId);
                result.SortedByCharacter = after.SortedByCharacter;
                result.SortedAt = after.SortedAt;
            }
            else
            {
                result.SortedByCharacter = false;
                result.Reason = "no_plates_attached";
                onProgress?.Invoke(
                    "No book pictures attached — try again after Build cast, or upload pictures manually.");
            }
        }
        else
        {
            result.SortedByCharacter = platesState.SortedByCharacter || result.CharactersUpdated > 0;
            result.SortedAt = platesState.SortedAt;
        }

        result.Ok = result.CharactersUpdated > 0 || result.CharactersSkipped > 0;
        if (!result.Ok)
            result.Reason ??= "nothing_attached";
        onProgress?.Invoke(
            $"Done ({method}): updated={result.CharactersUpdated} skipped={result.CharactersSkipped}");
        return result;
    }

    /// <summary>
    /// High-confidence scores from OCR name hits → neighboring art pages (book_full.txt).
    /// </summary>
    private static int SeedScoresFromOcrNeighbors(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        List<BookOcrPlateShortlist.PageText> ocrPages,
        string? onlyCharKey,
        int maxPerCharacter)
    {
        var hits = 0;
        var byPage = inventory
            .Where(r => r.Page > 0 && !IsLikelyTextLayout(r))
            .GroupBy(r => r.Page)
            .ToDictionary(g => g.Key, g => RankIllustrationFirst(g).ToList());

        foreach (var (key, seedNode) in seeds)
        {
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seedNode is not JsonObject seed) continue;
            if (IsVoiceOnly(key, seed)) continue;
            if (!scores.TryGetValue(key, out var list)) continue;

            var aliases = BookOcrPlateShortlist.AliasesForSeed(key, seed);
            var pages = BookOcrPlateShortlist.ShortlistArtPages(ocrPages, aliases, maxPerCharacter);
            foreach (var pg in pages)
            {
                if (!byPage.TryGetValue(pg, out var rows) || rows.Count == 0) continue;
                var row = rows[0];
                // Avoid duplicate page rows
                if (list.Any(x => x.Row.Page == pg ||
                                  x.Row.AbsPath.Equals(row.AbsPath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add((row, 0.93)); // high confidence: OCR name → art neighbor
                hits++;
            }
        }
        return hits;
    }

    /// <summary>Prefer OCR-shortlisted art pages, then remaining illustrated inventory.</summary>
    private static List<BookImageRow> BuildVisionScanList(
        List<BookImageRow> inventory,
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        JsonObject seeds,
        List<BookOcrPlateShortlist.PageText> ocrPages,
        int maxImages)
    {
        var priorityPages = new HashSet<int>();
        if (ocrPages.Count > 0)
        {
            foreach (var (key, seedNode) in seeds)
            {
                if (seedNode is not JsonObject seed) continue;
                if (IsVoiceOnly(key, seed)) continue;
                // Still need more plates for this cast?
                var have = scores.TryGetValue(key, out var list) ? list.Count : 0;
                if (have >= 3) continue;
                var aliases = BookOcrPlateShortlist.AliasesForSeed(key, seed);
                foreach (var pg in BookOcrPlateShortlist.ShortlistArtPages(ocrPages, aliases, maxPlates: 6))
                    priorityPages.Add(pg);
            }
        }

        var art = inventory.Where(r => !IsLikelyTextLayout(r)).ToList();
        var head = RankIllustrationFirst(art.Where(r => r.Page > 0 && priorityPages.Contains(r.Page)));
        var tail = RankIllustrationFirst(art.Where(r => r.Page <= 0 || !priorityPages.Contains(r.Page)));
        return head.Concat(tail)
            .GroupBy(r => r.AbsPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(maxImages)
            .ToList();
    }

    private static bool CastScoresComplete(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<CharacterClassifyHint> cast,
        int maxPerCharacter)
    {
        foreach (var c in cast)
        {
            if (!scores.TryGetValue(c.Key, out var list) || list.Count < maxPerCharacter)
                return false;
        }
        return cast.Count > 0;
    }

    /// <summary>
    /// High-confidence plate hits from cast AI <c>source_image_pages</c> (page → book_images).
    /// </summary>
    private static int SeedScoresFromSourceImagePages(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        string? onlyCharKey)
    {
        var hits = 0;
        foreach (var (key, seedNode) in seeds)
        {
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seedNode is not JsonObject seed || IsVoiceOnly(key, seed)) continue;
            if (!scores.ContainsKey(key))
                scores[key] = new List<(BookImageRow, double)>();

            var pages = PagesForSeed(seed);
            if (pages.Count == 0) continue;
            var picks = RowsForPages(inventory, pages);
            foreach (var p in picks)
            {
                scores[key].Add((p, 0.92));
                hits++;
            }
        }
        return hits;
    }

    private static List<CharacterClassifyHint> BuildCastHints(JsonObject seeds, string? onlyCharKey)
    {
        var cast = new List<CharacterClassifyHint>();
        foreach (var (key, node) in seeds)
        {
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (node is not JsonObject seed) continue;
            if (IsVoiceOnly(key, seed)) continue;
            var display = seed["canonical_given_name"]?.GetValue<string>()
                          ?? seed["voice_label"]?.GetValue<string>()
                          ?? key.Replace("Character_", "").Replace('_', ' ');
            var desc = seed["description"]?.GetValue<string>()
                       ?? seed["visual_lock"]?.GetValue<string>()
                       ?? "";
            cast.Add(new CharacterClassifyHint
            {
                Key = key,
                DisplayName = display,
                Description = desc,
            });
        }
        return cast;
    }

    private async Task ApplyHeuristicScoresAsync(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        List<BookImageRow> inventory,
        JsonObject seeds,
        string? onlyCharKey,
        bool onlyEmpty = false,
        bool heroOnly = false,
        CancellationToken ct = default)
    {
        var index = 0;
        foreach (var (key, seedNode) in seeds)
        {
            if (onlyCharKey is { Length: > 0 } &&
                !string.Equals(key, onlyCharKey, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }
            if (seedNode is not JsonObject seed || IsVoiceOnly(key, seed))
            {
                index++;
                continue;
            }
            if (!scores.TryGetValue(key, out var list))
            {
                list = new List<(BookImageRow, double)>();
                scores[key] = list;
            }
            if (onlyEmpty && list.Count > 0)
            {
                index++;
                continue;
            }

            var desc = (seed["description"]?.GetValue<string>() ?? "").ToLowerInvariant();
            // Hero = first cast or primary animal species — not humans whose text mentions the animal medium
            var isHero = index == 0 ||
                         CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                             key, ageBand: "", description: desc, visualLock: "", animalWord: "dog") ||
                         CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                             key, ageBand: "", description: desc, visualLock: "", animalWord: "cat");

            // heroOnly: never invent Mom/Dad plates from shared early covers
            if (heroOnly && !isHero)
            {
                index++;
                continue;
            }

            var pages = PagesForSeed(seed);
            List<BookImageRow> picks;
            if (pages.Count > 0)
            {
                picks = RowsForPages(inventory, pages);
                if (picks.Count == 0)
                    picks = await HeuristicPicksRankedAsync(inventory, key, seed, index, nameHitsOnly: !isHero, ct)
                        .ConfigureAwait(false);
            }
            else
            {
                // Non-hero: only filename/name hits — never dump generic early pages onto Mom/Dad
                picks = await HeuristicPicksRankedAsync(inventory, key, seed, index, nameHitsOnly: !isHero, ct)
                    .ConfigureAwait(false);
            }

            foreach (var p in picks.Where(r => !IsLikelyTextLayout(r)))
                list.Add((p, isHero ? 0.5 : 0.4));
            index++;
        }
    }

    /// <summary>
    /// Prefer primary_character_key when confident; else single highest-confidence match.
    /// Never multi-assign the same page to several cast members.
    /// </summary>
    private static CharacterPageMatch? PickBestMatch(CharacterPageClassification cls)
    {
        const double minConf = 0.55;
        var viable = cls.Matches.Where(m => m.Confidence >= minConf).ToList();
        if (viable.Count == 0) return null;

        if (cls.PrimaryCharacterKey is { Length: > 0 } primary)
        {
            var prim = viable.FirstOrDefault(m =>
                string.Equals(m.Key, primary, StringComparison.OrdinalIgnoreCase));
            if (prim is not null)
                return new CharacterPageMatch
                {
                    Key = prim.Key,
                    Confidence = Math.Min(1.0, prim.Confidence + 0.12),
                    Notes = prim.Notes,
                };
        }

        return viable.OrderByDescending(m => m.Confidence).First();
    }

    /// <summary>
    /// Greedy exclusive plate assignment ranked by score.
    /// Dedupes by content hash, absolute path, and page number so B0/B2 cannot be the same cover.
    /// Each image goes to at most one character so Mom/Dad never share identical plates.
    /// Characters with fewer candidate pages are preferred so supporting cast is not wiped by the hero.
    /// </summary>
    private static Dictionary<string, List<BookImageRow>> AssignPlatesExclusively(
        Dictionary<string, List<(BookImageRow Row, double Score)>> scores,
        int maxPerCharacter)
    {
        // Fewer unique pages ⇒ higher priority (supporting cast with one page beats hero with many)
        var uniquePageCount = scores.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(x => x.Row.Page).Where(p => p > 0).Distinct().Count(),
            StringComparer.OrdinalIgnoreCase);

        var flat = new List<(string Key, BookImageRow Row, double Score, string Fingerprint)>();
        foreach (var (key, list) in scores)
        {
            foreach (var (row, score) in list)
            {
                if (IsLikelyTextLayout(row)) continue;
                flat.Add((key, row, score, ContentFingerprint(row)));
            }
        }

        flat = flat
            .OrderByDescending(x => x.Score)
            .ThenBy(x => uniquePageCount.GetValueOrDefault(x.Key, 99)) // scarce pages first
            .ThenBy(x => IllustrationScore(x.Row))
            .ToList();

        var claimedFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedPages = new HashSet<int>(); // global page exclusivity for near-duplicate embeds
        var perCharPages = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var assigned = new Dictionary<string, List<BookImageRow>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, row, _, fp) in flat)
        {
            if (!assigned.TryGetValue(key, out var picks))
            {
                picks = new List<BookImageRow>();
                assigned[key] = picks;
                perCharPages[key] = new HashSet<int>();
            }
            if (picks.Count >= maxPerCharacter) continue;

            if (fp.Length > 0 && claimedFingerprints.Contains(fp)) continue;
            if (claimedPaths.Contains(row.AbsPath)) continue;

            // Same page number → almost always same art (cover embed + cover render)
            if (row.Page > 0 && claimedPages.Contains(row.Page)) continue;
            if (row.Page > 0 && perCharPages[key].Contains(row.Page)) continue;

            picks.Add(row);
            if (fp.Length > 0) claimedFingerprints.Add(fp);
            claimedPaths.Add(row.AbsPath);
            if (row.Page > 0)
            {
                claimedPages.Add(row.Page);
                perCharPages[key].Add(row.Page);
            }
        }

        // Last resort: cast members still empty after exclusivity may share a high-score page
        // (e.g. two cast members on the same full-page plate) so we never leave someone blank.
        foreach (var (key, list) in scores)
        {
            if (!assigned.TryGetValue(key, out var picks))
            {
                picks = new List<BookImageRow>();
                assigned[key] = picks;
            }
            if (picks.Count > 0) continue;
            var best = list
                .Where(x => !IsLikelyTextLayout(x.Row))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => IllustrationScore(x.Row))
                .Select(x => x.Row)
                .FirstOrDefault();
            if (best is not null)
                picks.Add(best);
        }

        return assigned;
    }

    private static string ContentFingerprint(BookImageRow row)
    {
        try
        {
            if (!File.Exists(row.AbsPath)) return "";
            using var fs = File.OpenRead(row.AbsPath);
            // Hash first 256KB + length — enough to catch identical embeds cheaply
            var buf = new byte[256 * 1024];
            var n = fs.Read(buf, 0, buf.Length);
            // CA1850: static HashData avoids SHA256 instance allocation
            var hash = System.Security.Cryptography.SHA256.HashData(buf.AsSpan(0, n));
            var len = new FileInfo(row.AbsPath).Length;
            return Convert.ToHexString(hash)[..16] + ":" + len;
        }
        catch
        {
            return row.AbsPath;
        }
    }

    private static List<string> CopyPlates(
        string projectDir,
        string charsDir,
        string key,
        List<BookImageRow> picks,
        bool copyIntoAssets)
    {
        var relPaths = new List<string>();
        var usedDest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < Math.Min(3, picks.Count); j++)
        {
            var row = picks[j];
            if (ProjectStore.IsTextOnlyPlatePath(row.PathRel) || ProjectStore.IsTextOnlyPlatePath(row.Name))
                continue;
            if (copyIntoAssets && File.Exists(row.AbsPath))
            {
                var ext = Path.GetExtension(row.AbsPath).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var destName = $"{key.ToLowerInvariant()}_bookref_{j + 1}{ext}";
                var dest = Path.Combine(charsDir, destName);
                // Avoid writing two slots if somehow same source
                var srcFp = ContentFingerprint(row);
                if (srcFp.Length > 0 && usedDest.Contains(srcFp))
                    continue;
                try
                {
                    File.Copy(row.AbsPath, dest, overwrite: true);
                    relPaths.Add(Path.GetRelativePath(projectDir, dest).Replace('\\', '/'));
                    if (srcFp.Length > 0) usedDest.Add(srcFp);
                }
                catch
                {
                    if (!ProjectStore.IsTextOnlyPlatePath(row.PathRel))
                        relPaths.Add(row.PathRel);
                }
            }
            else if (!ProjectStore.IsTextOnlyPlatePath(row.PathRel))
            {
                relPaths.Add(row.PathRel);
            }
        }
        return relPaths;
    }

    /// <summary>Remove leftover bookref_N from earlier sorts that are no longer referenced.</summary>
    private static void CleanupStaleBookrefs(string charsDir, string key, int keepCount)
    {
        if (!Directory.Exists(charsDir)) return;
        var prefix = key.ToLowerInvariant() + "_bookref_";
        foreach (var fi in new DirectoryInfo(charsDir).EnumerateFiles())
        {
            var name = fi.Name;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            // character_x_bookref_2.png → index 2
            var m = Regex.Match(name, @"_bookref_(\d+)\.", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var idx)) continue;
            if (idx > keepCount)
            {
                try { File.Delete(fi.FullName); } catch { /* ignore */ }
            }
        }
        // Also drop alternate extensions for slots we rewrote (e.g. keep .jpg, delete old .png)
        if (keepCount <= 0) return;
        for (var i = 1; i <= keepCount; i++)
        {
            var matches = new DirectoryInfo(charsDir).GetFiles($"{prefix}{i}.*");
            if (matches.Length <= 1) continue;
            // Keep newest
            var ordered = matches.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var stale in ordered.Skip(1))
            {
                try { File.Delete(stale.FullName); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Prefer AI <c>cast_seeds.json</c> (full cast + source_image_pages), then Fountain parse,
    /// then scenes.json.
    /// </summary>
    private async Task<JsonObject> LoadOrBuildCastSeedsAsync(
        string projectId,
        string castSeedsPath,
        CancellationToken ct)
    {
        JsonObject? seeds = null;

        // 1) cast_seeds.json (canonical cast)
        var castPath = File.Exists(castSeedsPath)
            ? castSeedsPath
            : Path.Combine(_projects.GetProjectDir(projectId), "source", ScreenplayService.CastSeedsFileName);
        if (File.Exists(castPath))
        {
            try
            {
                var existing = JsonNode.Parse(await File.ReadAllTextAsync(castPath, ct).ConfigureAwait(false))
                               as JsonObject;
                var existingSeeds = existing?["character_seed_tokens"] as JsonObject
                    ?? existing?["global_production_variables"]?["character_seed_tokens"] as JsonObject;
                if (existingSeeds is not null && existingSeeds.Count > 0)
                    seeds = existingSeeds.DeepClone().AsObject();
            }
            catch { /* try fountain */ }
        }

        // 2) Fountain-derived cast (dialogue cues) — merge only missing keys
        var model = ScreenplayService.TryBuildModelFromProject(_projects, projectId);
        if (model is not null &&
            model.TryGetValue("global_production_variables", out var gpvObj) &&
            gpvObj is Dictionary<string, object?> gpv &&
            gpv.TryGetValue("character_seed_tokens", out var charObj) &&
            charObj is Dictionary<string, object?> charDict &&
            charDict.Count > 0)
        {
            var json = JsonSerializer.Serialize(charDict, JsonDefaults.Indented);
            var fromFountain = JsonNode.Parse(json) as JsonObject;
            if (fromFountain is not null)
            {
                seeds ??= new JsonObject();
                foreach (var (key, node) in fromFountain)
                {
                    if (node is null) continue;
                    if (seeds.ContainsKey(key)) continue; // AI cast wins
                    seeds[key] = node.DeepClone();
                }
            }
        }

        return seeds ?? new JsonObject();
    }

    private async Task TryMirrorBlueprintAsync(string projectDir, JsonObject stage1Seeds)
    {
        try
        {
            var projectId = Path.GetFileName(
                projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var bp = await _projects.FindBlueprintPathAsync(projectId, CancellationToken.None)
                .ConfigureAwait(false);
            if (bp is null || !File.Exists(bp)) return;

            var root = JsonNode.Parse(await File.ReadAllTextAsync(bp, CancellationToken.None)) as JsonObject;
            if (root is null) return;
            var gpv = root["global_production_variables"] as JsonObject ?? new JsonObject();
            root["global_production_variables"] = gpv;
            var bpSeeds = gpv["character_seed_tokens"] as JsonObject ?? new JsonObject();
            gpv["character_seed_tokens"] = bpSeeds;

            foreach (var (key, seedNode) in stage1Seeds)
            {
                if (seedNode is not JsonObject src) continue;
                if (bpSeeds[key] is not JsonObject dest)
                {
                    bpSeeds[key] = src.DeepClone();
                    continue;
                }
                if (src["design_reference_images"] is JsonArray arr)
                {
                    dest["design_reference_images"] = arr.DeepClone();
                    dest["book_reference_images"] = arr.DeepClone();
                }
                else
                {
                    dest.Remove("design_reference_images");
                    dest.Remove("book_reference_images");
                }
                if (src["source_image_pages"] is JsonArray pages)
                    dest["source_image_pages"] = pages.DeepClone();
            }

            await File.WriteAllTextAsync(bp, root.ToJsonString(JsonDefaults.Indented) + "\n");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not mirror book plates into blueprint");
        }
    }

    private static bool IsVoiceOnly(string key, JsonObject seed)
    {
        var pol = (seed["display_name_policy"]?.GetValue<string>() ?? "").ToLowerInvariant();
        return pol.Contains("never")
               || key.EndsWith("_Narrator", StringComparison.OrdinalIgnoreCase)
               || key.Equals("Character_Narrator", StringComparison.OrdinalIgnoreCase)
               || key.Contains("narrator", StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> PagesForSeed(JsonObject seed)
    {
        var outList = new List<int>();
        var raw = seed["source_image_pages"] ?? seed["image_pages"];
        if (raw is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var one))
                outList.Add(one);
            else if (jv.TryGetValue<string>(out var s))
            {
                foreach (Match m in Regex.Matches(s, @"\d+"))
                    if (int.TryParse(m.Value, out var n)) outList.Add(n);
            }
        }
        else if (raw is JsonArray arr)
        {
            foreach (var x in arr)
            {
                if (x is null) continue;
                if (x is JsonValue v && v.TryGetValue<int>(out var n))
                    outList.Add(n);
                else if (int.TryParse(x.ToString(), out var n2))
                    outList.Add(n2);
            }
        }
        return outList;
    }

    private static List<BookImageRow> RowsForPages(List<BookImageRow> inventory, List<int> pages)
    {
        var byPage = inventory.GroupBy(r => r.Page).ToDictionary(g => g.Key, g => g.ToList());
        var picks = new List<BookImageRow>();
        foreach (var pg in pages)
        {
            if (!byPage.TryGetValue(pg, out var cands) || cands.Count == 0) continue;
            var best = RankIllustrationFirst(cands).FirstOrDefault(r => !IsLikelyTextLayout(r));
            if (best is null) continue;
            picks.Add(best);
        }
        return RankIllustrationFirst(picks).Take(3).ToList();
    }

    private static List<BookImageRow> HeuristicPicks(
        List<BookImageRow> inventory,
        string key,
        JsonObject seed,
        int index,
        bool nameHitsOnly = false)
    {
        var ranked = RankIllustrationFirst(inventory.Where(r => !IsLikelyTextLayout(r)));
        var early = ranked.Where(r =>
                r.Page is > 0 and <= 8 ||
                r.Name.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains("sparse", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (early.Count == 0)
            early = ranked.Take(6).ToList();

        var token = key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        var given = (seed["canonical_given_name"]?.GetValue<string>() ?? "").ToLowerInvariant();
        // "mom"/"dad" tokens are useless for filename matching and caused false plate attaches
        var genericRoleToken = token is "mom" or "dad" or "daddy" or "mum" or "mother" or "father" or "parent";
        var nameHits = RankIllustrationFirst(inventory.Where(r =>
        {
            if (IsLikelyTextLayout(r)) return false;
            if (!genericRoleToken && token.Length >= 3 &&
                r.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
            if (given.Length >= 3 && !genericRoleToken &&
                r.Name.Contains(given, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }).ToList());

        var desc = seed["description"]?.GetValue<string>() ?? "";
        var isHero = index == 0 ||
                     CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                         key, "", desc, "", "dog") ||
                     CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(
                         key, "", desc, "", "cat");

        List<BookImageRow> baseline;
        if (nameHits.Count > 0) baseline = nameHits.Take(8).ToList();
        else if (nameHitsOnly) return new List<BookImageRow>(); // supporting cast: no plate is correct
        else if (isHero) baseline = early.Take(8).ToList();
        else return new List<BookImageRow>(); // never invent plates for supporting cast

        // Optional chat re-rank of basenames (sync path keeps baseline; async attach uses RankHeuristicAsync)
        return baseline.Take(3).ToList();
    }

    /// <summary>Async wrapper: heuristic candidates then optional chat re-rank.</summary>
    private async Task<List<BookImageRow>> HeuristicPicksRankedAsync(
        List<BookImageRow> inventory,
        string key,
        JsonObject seed,
        int index,
        bool nameHitsOnly = false,
        CancellationToken ct = default)
    {
        var baseline = HeuristicPicks(inventory, key, seed, index, nameHitsOnly);
        if (_plateRank is null || !_plateRank.IsEnabled || baseline.Count <= 1)
            return baseline;
        var names = baseline.Select(r => r.Name).ToList();
        var desc = seed["description"]?.GetValue<string>() ?? "";
        var (ranked, usedAi) = await _plateRank.RankAsync(key, desc, names, ct).ConfigureAwait(false);
        if (!usedAi || ranked.Count == 0) return baseline.Take(3).ToList();
        var byName = baseline.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<BookImageRow>();
        foreach (var n in ranked)
        {
            if (byName.TryGetValue(n, out var row))
                ordered.Add(row);
        }
        foreach (var row in baseline)
        {
            if (!ordered.Any(o => o.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)))
                ordered.Add(row);
        }
        return ordered.Take(3).ToList();
    }

    private static List<BookImageRow> RankIllustrationFirst(IEnumerable<BookImageRow> rows) =>
        rows.OrderBy(IllustrationScore)
            .ThenByDescending(r =>
            {
                try { return new FileInfo(r.AbsPath).Length; }
                catch { return 0L; }
            })
            .ThenBy(r => r.Page > 0 ? r.Page : 99)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int IllustrationScore(BookImageRow r)
    {
        var n = r.Name;
        var score = 50;
        if (n.Contains("cover", StringComparison.OrdinalIgnoreCase)) score -= 40;
        if (n.Contains("sparse", StringComparison.OrdinalIgnoreCase)) score -= 30;
        if (r.Kind == "rendered_page" || n.StartsWith("page_", StringComparison.OrdinalIgnoreCase))
            score -= 5;
        if (r.Kind == "embedded" || n.Contains("embedded", StringComparison.OrdinalIgnoreCase))
            score -= 8;
        if (IsLikelyTextLayout(r)) score += 35;
        if (n.Contains("text", StringComparison.OrdinalIgnoreCase) &&
            !n.Contains("sparse", StringComparison.OrdinalIgnoreCase))
            score += 10;
        return score;
    }

    private static bool IsLikelyTextLayout(BookImageRow r)
    {
        if (ProjectStore.IsTextOnlyPlatePath(r.Name) || ProjectStore.IsTextOnlyPlatePath(r.PathRel))
            return true;

        if (r.Name.Contains("text_page", StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains("text-only", StringComparison.OrdinalIgnoreCase) ||
            r.Kind.Contains("text", StringComparison.OrdinalIgnoreCase))
            return true;

        // Manifest relevance (book_images.v1) — pure text spreads must never seed portraits
        if (string.Equals(r.Relevance, "text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Relevance, "text_only", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Relevance, "text_heavy", StringComparison.OrdinalIgnoreCase))
            return true;

        // Full-page PDF renders / embeds are illustrations even when compressed under 400KB.
        if (r.Kind is "rendered_page" or "embedded" or "file")
        {
            if (r.Name.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains("sparse", StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains("bookref", StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains("embedded", StringComparison.OrdinalIgnoreCase) ||
                r.Name.StartsWith("page_", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(r.Name, @"^(page|p|embedded_p)\d", RegexOptions.IgnoreCase))
                return false;
        }

        try
        {
            var len = new FileInfo(r.AbsPath).Length;
            if (len is > 0 and < 400_000 &&
                !r.Name.Contains("cover", StringComparison.OrdinalIgnoreCase) &&
                !r.Name.Contains("sparse", StringComparison.OrdinalIgnoreCase) &&
                !r.Name.Contains("bookref", StringComparison.OrdinalIgnoreCase) &&
                !r.Name.StartsWith("page_", StringComparison.OrdinalIgnoreCase) &&
                !r.Name.Contains("embedded", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(r.Name, @"^(page|p|embedded_p)\d", RegexOptions.IgnoreCase))
                return true;
        }
        catch { /* ignore */ }
        return false;
    }

    private static async Task<List<BookImageRow>> LoadBookImageInventoryAsync(string projectDir)
    {
        var rows = new List<BookImageRow>();
        var source = Path.Combine(projectDir, "source");
        var imgDir = Path.Combine(source, "book_images");
        var man = Path.Combine(imgDir, "manifest.json");

        if (File.Exists(man))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(man));
                if (doc.RootElement.TryGetProperty("images", out var imgs) &&
                    imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var im in imgs.EnumerateArray())
                    {
                        var rel = im.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                        rel = rel.Replace('\\', '/');
                        var abs = Path.IsPathRooted(rel)
                            ? rel
                            : Path.Combine(source, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(abs))
                            abs = Path.Combine(imgDir, Path.GetFileName(rel));
                        if (!File.Exists(abs)) continue;
                        var page = im.TryGetProperty("page", out var pg) && pg.TryGetInt32(out var pn) ? pn : 0;
                        var kind = im.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                        var relevance = im.TryGetProperty("relevance", out var relEl)
                            ? relEl.GetString() ?? ""
                            : "";
                        var pathRel = Path.GetRelativePath(projectDir, abs).Replace('\\', '/');
                        rows.Add(new BookImageRow(
                            pathRel, abs, page, kind,
                            Path.GetFileName(abs).ToLowerInvariant(),
                            relevance));
                    }
                }
            }
            catch { /* fall through */ }
        }

        if (rows.Count == 0 && Directory.Exists(imgDir))
        {
            foreach (var fi in new DirectoryInfo(imgDir).EnumerateFiles()
                         .Where(f => Regex.IsMatch(f.Extension, @"\.(png|jpe?g|webp)$", RegexOptions.IgnoreCase))
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = fi.Name;
                var f = fi.FullName;
                var m = Regex.Match(name, @"(?:page|p|embedded_p)0*(\d+)", RegexOptions.IgnoreCase);
                var page = m.Success && int.TryParse(m.Groups[1].Value, out var pn) ? pn : 0;
                var pathRel = Path.GetRelativePath(projectDir, f).Replace('\\', '/');
                rows.Add(new BookImageRow(pathRel, f, page, "file", name.ToLowerInvariant()));
            }
        }

        return rows;
    }

    private sealed record BookImageRow(
        string PathRel,
        string AbsPath,
        int Page,
        string Kind,
        string Name,
        string Relevance = "");
}
