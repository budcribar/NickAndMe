using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// AI: approved Fountain (+ optional book) → <c>source/cast_seeds.json</c>.
/// Cast identity for Characters UI / plates — not parsed solely from dialogue cues.
/// Book text is used heavily for description / visual_lock so portrait gen starts closer to right.
/// </summary>
public sealed class CastFromScreenplayService
{
    public const string PromptRelativePath = "prompts/fountain_to_cast.txt";

    /// <summary>Fountain budget for cast extract user messages.</summary>
    public const int FountainPromptChars = 80_000;
    /// <summary>
    /// Book budget for cast extract. Full text when under this size; otherwise
    /// name-targeted look excerpts from the whole book + multi-window spine samples
    /// so late-appearing / wardrobe-change cues still reach visual_lock.
    /// </summary>
    public const int BookPromptChars = 100_000;

    /// <summary>Share of book budget reserved for name-targeted look excerpts on long novels.</summary>
    public const double BookNameExcerptBudgetShare = 0.55;

    /// <summary>Evenly spaced narrative windows when the book is truncated (covers late chapters).</summary>
    public const int BookSpineWindowCount = 5;

    private readonly ProjectStore _projects;
    private readonly IGrokChatClient _chat;
    private readonly CastVisualLiteralizeService _literalize;
    private readonly ProjectRulesService _projectRules;
    private readonly ILogger<CastFromScreenplayService> _log;

    public CastFromScreenplayService(
        ProjectStore projects,
        IGrokChatClient chat,
        CastVisualLiteralizeService literalize,
        ProjectRulesService projectRules,
        ILogger<CastFromScreenplayService> log)
    {
        _projects = projects;
        _chat = chat;
        _literalize = literalize;
        _projectRules = projectRules;
        _log = log;
    }

    public sealed class ExtractResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? OutPath { get; init; }
        public int CharacterCount { get; init; }
        public List<string> CharacterKeys { get; init; } = new();
        public string? MovieTitle { get; init; }
        public string? RawPath { get; init; }
    }

    /// <summary>
    /// Build cast_seeds.json from screenplay.fountain (+ book_full.txt when present).
    /// </summary>
    public async Task<ExtractResult> ExtractAsync(
        string projectId,
        string model = "grok-4.5",
        bool force = false,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_chat.IsConfigured)
            throw new InvalidOperationException("Connect service (API key) to build cast from the screenplay.");

        ScreenplayService.EnsureCanonicalDraft(_projects, projectId);
        var draftPath = ScreenplayService.GetDraftPath(_projects, projectId);
        if (!File.Exists(draftPath))
            return new ExtractResult { Ok = false, Error = "No screenplay draft. Import/approve a Fountain first." };

        var fountain = await File.ReadAllTextAsync(draftPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(fountain))
            return new ExtractResult { Ok = false, Error = "Screenplay draft is empty." };

        var outPath = ScreenplayService.GetCastSeedsPath(_projects, projectId);
        if (!force && File.Exists(outPath))
        {
            try
            {
                using var existing = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, ct).ConfigureAwait(false));
                var seeds = GetSeedsElement(existing.RootElement);
                if (seeds.ValueKind == JsonValueKind.Object && seeds.EnumerateObject().Count() > 0)
                {
                    onProgress?.Invoke("Cast file already present — use force to rebuild.");
                    var existingKeys = seeds.EnumerateObject().Select(p => p.Name).ToList();
                    return new ExtractResult
                    {
                        Ok = true,
                        OutPath = outPath,
                        CharacterCount = existingKeys.Count,
                        CharacterKeys = existingKeys,
                        MovieTitle = existing.RootElement.TryGetProperty("movie_title", out var mt)
                            ? mt.GetString()
                            : null,
                    };
                }
            }
            catch { /* rebuild */ }
        }

        var book = await LoadBookTextAsync(projectId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(book))
            onProgress?.Invoke($"Book context loaded ({book.Length:N0} chars) for look extraction…");
        else
            onProgress?.Invoke("No book_full.txt — looks will come from screenplay action only.");

        onProgress?.Invoke("Loading cast prompt…");
        var system = await LoadSystemPromptAsync(_projects.WorkspaceRoot, ct).ConfigureAwait(false);
        var user = BuildUserPrompt(fountain, book);

        onProgress?.Invoke("Calling Grok for closed cast (book-aware looks)…");
        var raw = await _chat.CompleteAsync(
                system, user, model, temperature: 0.2, ct,
                mode: ChatCallModes.CastFromScreenplay)
            .ConfigureAwait(false);
        raw = StripFences(raw);

        Dictionary<string, object?> parsed;
        try
        {
            parsed = GrokChatClient.ParseJsonObject(raw);
        }
        catch (Exception ex)
        {
            var dump = Path.Combine(
                _projects.GetProjectDir(projectId),
                "source",
                $"cast_raw_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
                await File.WriteAllTextAsync(dump, raw, ct).ConfigureAwait(false);
            }
            catch { /* ignore */ }

            _log.LogWarning(ex, "Cast JSON parse failed for {Project}", projectId);
            return new ExtractResult
            {
                Ok = false,
                Error = $"Could not parse cast JSON: {ex.Message}",
                RawPath = dump,
            };
        }

        var normalized = NormalizeCastDoc(parsed, projectId);
        var seedsObj = GetSeedsDict(normalized);
        if (seedsObj.Count == 0)
            return new ExtractResult { Ok = false, Error = "Model returned no character_seed_tokens." };

        // Second AI pass: figurative / idiomatic visual language → literal filmable prose
        // (no never-ending regex nickname lists)
        var literalSeeds = await _literalize.LiteralizeSeedsAsync(
            seedsObj, model, onProgress, ct).ConfigureAwait(false);
        normalized["character_seed_tokens"] = literalSeeds;
        seedsObj = literalSeeds;

        onProgress?.Invoke($"Writing {seedsObj.Count} character seed(s)…");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (File.Exists(outPath))
        {
            try
            {
                File.Copy(outPath, outPath + $".bak_{DateTime.Now:yyyyMMdd_HHmmss}", overwrite: true);
            }
            catch { /* ignore */ }
        }

        var json = JsonSerializer.Serialize(normalized, JsonDefaults.Indented);
        await File.WriteAllTextAsync(outPath, json + "\n", ct).ConfigureAwait(false);

        // Project style + performance rules from book/screenplay (medium + audience address)
        try
        {
            if (normalized.TryGetValue("render_style_lock", out var rslObj) &&
                rslObj?.ToString() is { Length: > 0 } rsl &&
                _projectRules.EnsureStyleRuleFromRenderLock(projectId, rsl, approvedBy: "cast_extract"))
                onProgress?.Invoke("Project style rule updated from book/screenplay medium.");

            if (normalized.TryGetValue("performance_lock", out var perfObj) &&
                perfObj?.ToString() is { Length: > 0 } perf &&
                _projectRules.EnsurePerformanceRuleFromLock(projectId, perf, approvedBy: "cast_extract"))
                onProgress?.Invoke("Project performance/address rule updated from book/screenplay.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write style/performance project rules for {Project}", projectId);
        }

        var keys = seedsObj.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        onProgress?.Invoke($"Cast ready · {keys.Count} character(s)");
        return new ExtractResult
        {
            Ok = true,
            OutPath = outPath,
            CharacterCount = keys.Count,
            CharacterKeys = keys,
            MovieTitle = normalized.TryGetValue("movie_title", out var t) ? t?.ToString() : null,
        };
    }

    public static async Task<string> LoadSystemPromptAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var path = Path.Combine(
            workspaceRoot,
            PromptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException($"Cast prompt not found: {path}");
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<string?> LoadBookTextAsync(string projectId, CancellationToken ct)
    {
        var source = Path.Combine(_projects.GetProjectDir(projectId), "source");
        foreach (var name in new[] { "book_full.txt", "book.txt", "source.txt" })
        {
            var path = Path.Combine(source, name);
            if (!File.Exists(path)) continue;
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    private static string BuildUserPrompt(string fountain, string? book)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extract the closed cast for production pinning.");
        sb.AppendLine("Include silent on-screen characters named only in action (e.g. BUSTER the dog).");
        sb.AppendLine("CRITICAL: Fill description + visual_lock with concrete filmable appearance for every");
        sb.AppendLine("on-screen role (age, build, face, hair, eyes, wardrobe, era). Mine BOOK text when present.");
        sb.AppendLine("When BOOK is sampled, prefer LOOK EXCERPTS tagged by character name for description/visual_lock.");
        sb.AppendLine("Never use stubs like \"as described in the screenplay\".");
        sb.AppendLine("On-camera POV/confessor narrators = ok_anytime (not voice-only).");
        sb.AppendLine("Return JSON only (schema_version cast_seeds.v1, character_seed_tokens).");
        sb.AppendLine();
        sb.AppendLine("--- BEGIN FOUNTAIN ---");
        sb.AppendLine(SelectTextForPrompt(fountain, FountainPromptChars));
        sb.AppendLine("--- END FOUNTAIN ---");
        if (!string.IsNullOrWhiteSpace(book))
        {
            var nameHints = ExtractNameHintsFromFountain(fountain);
            sb.AppendLine();
            sb.AppendLine("--- BEGIN BOOK (primary source for looks / likeness) ---");
            sb.AppendLine(SelectBookTextForCastPrompt(book, BookPromptChars, nameHints));
            sb.AppendLine("--- END BOOK ---");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("(No book text attached — infer looks only from Fountain action/description.)");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Prefer full text when under budget; otherwise evenly spaced windows (spine sample).
    /// For books with cast names, prefer <see cref="SelectBookTextForCastPrompt"/>.
    /// </summary>
    public static string SelectTextForPrompt(string text, int maxChars)
    {
        text = NormalizeNewlines(text);
        if (text.Length <= maxChars) return text;
        if (maxChars < 4_000)
            return text[..maxChars] + "\n\n[[truncated for length]]\n";

        return SelectSpineWindows(text, maxChars, BookSpineWindowCount)
               + "\n\n[[sampled for length; full source remains on disk]]\n";
    }

    /// <summary>
    /// Book text for cast look extraction. Under budget → full book. Over budget →
    /// (1) paragraphs that mention Fountain cast names (prioritize look/wardrobe language),
    /// (2) remaining budget as multi-window spine samples across the novel.
    /// </summary>
    public static string SelectBookTextForCastPrompt(
        string bookText,
        int maxChars,
        IReadOnlyList<string>? nameHints = null)
    {
        bookText = NormalizeNewlines(bookText);
        if (bookText.Length <= maxChars) return bookText;
        if (maxChars < 4_000)
            return bookText[..maxChars] + "\n\n[[book truncated for length]]\n";

        nameHints ??= Array.Empty<string>();
        var sb = new StringBuilder(maxChars + 512);
        var used = 0;
        const int markerReserve = 200;

        // 1) Name-targeted look harvest from the full book (late descriptions, wardrobe changes)
        if (nameHints.Count > 0)
        {
            var nameBudget = Math.Max(2_000, (int)(maxChars * BookNameExcerptBudgetShare) - markerReserve);
            var excerpts = HarvestNameLookExcerpts(bookText, nameHints, nameBudget);
            if (!string.IsNullOrWhiteSpace(excerpts))
            {
                sb.AppendLine("[[LOOK EXCERPTS — paragraphs mentioning cast names (full-book scan)]]");
                sb.AppendLine(excerpts.TrimEnd());
                sb.AppendLine();
                used = sb.Length;
            }
        }

        // 2) Spine windows so plot context / unnamed look prose still appears
        var spineBudget = maxChars - used - 160;
        if (spineBudget >= 2_000)
        {
            sb.AppendLine("[[NARRATIVE SPINE — evenly spaced samples]]");
            sb.Append(SelectSpineWindows(bookText, spineBudget, BookSpineWindowCount));
            sb.AppendLine();
        }

        sb.AppendLine("[[book sampled for length; name look-excerpts scan full source; remainder on disk]]");
        var result = sb.ToString();
        if (result.Length > maxChars + 500)
            result = result[..maxChars] + "\n\n[[truncated for length]]\n";
        return result;
    }

    /// <summary>
    /// Character-name tokens from Fountain (dialogue cues + ALL-CAPS action names)
    /// for full-book look harvest. Public for tests.
    /// </summary>
    public static IReadOnlyList<string> ExtractNameHintsFromFountain(string? fountain)
    {
        fountain = NormalizeNewlines(fountain ?? "");
        if (fountain.Length == 0) return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var parsed = FountainParser.Parse(fountain);
            foreach (var el in parsed.Elements)
            {
                if (el.Type != FountainParser.ElementType.Character) continue;
                var raw = (el.Text ?? "").Trim();
                // Strip extensions: JANE (V.O.) / JANE (O.S.)
                raw = Regex.Replace(raw, @"\s*\([^)]*\)\s*$", "").Trim();
                raw = raw.TrimStart('@', '^', '*').Trim();
                if (raw.Length < 2) continue;
                if (IsNoiseCastToken(raw)) continue;
                names.Add(raw);
                // Title-case variant for prose books ("JANE" → "Jane")
                var title = ToTitleToken(raw);
                if (title.Length >= 2) names.Add(title);
            }
        }
        catch
        {
            // fall through to regex
        }

        // Silent heroes often only in action: "BUSTER, a small dog," / "MOMMA smiles"
        foreach (Match m in Regex.Matches(
                     fountain,
                     @"(?m)^[ \t]*([A-Z][A-Z0-9][A-Z0-9 \-']{0,40}?)(?=,|\s+(?:the|a|an|is|was|runs|walks|smiles|looks|stands|sits)\b)"))
        {
            var tok = m.Groups[1].Value.Trim();
            if (tok.Length < 2 || IsNoiseCastToken(tok)) continue;
            if (tok.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4) continue;
            names.Add(tok);
            var title = ToTitleToken(tok);
            if (title.Length >= 2) names.Add(title);
        }

        return names
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(48)
            .ToList();
    }

    /// <summary>
    /// Pull paragraphs that mention cast names, preferring appearance/wardrobe language.
    /// Scans the full book regardless of length. Public for tests.
    /// </summary>
    public static string HarvestNameLookExcerpts(
        string bookText,
        IReadOnlyList<string> nameHints,
        int maxChars)
    {
        bookText = NormalizeNewlines(bookText);
        if (string.IsNullOrWhiteSpace(bookText) || nameHints.Count == 0 || maxChars < 200)
            return "";

        var paras = Regex.Split(bookText.Trim(), @"\n\s*\n+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paras.Count == 0)
            paras.Add(bookText.Trim());

        // Build name matchers (word-boundary, case-insensitive)
        var nameRes = new List<(string Name, Regex Rx)>();
        foreach (var n in nameHints
                     .Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(40))
        {
            var escaped = Regex.Escape(n.Trim());
            // Allow multi-word names; avoid matching inside longer tokens
            nameRes.Add((n.Trim(), new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
        }

        // Cap per-name so one lead doesn't consume the whole budget
        var perNameCap = Math.Max(800, maxChars / Math.Max(3, Math.Min(nameRes.Count, 12)));
        var perNameUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(int Score, int Index, string Name, string Para)>();
        var selectedIdx = new HashSet<int>();
        var totalUsed = 0;

        for (var i = 0; i < paras.Count; i++)
        {
            var para = paras[i];
            if (para.Length < 24) continue;
            string? hitName = null;
            foreach (var (name, rx) in nameRes)
            {
                if (!rx.IsMatch(para)) continue;
                hitName = name;
                break;
            }
            if (hitName is null) continue;

            var score = 1;
            if (LookLanguage.IsMatch(para)) score += 5;
            if (para.Length is >= 80 and <= 1_200) score += 1;
            // Prefer later paragraphs slightly (wardrobe reveals often come mid/late)
            score += Math.Min(3, i * 3 / Math.Max(1, paras.Count));

            candidates.Add((score, i, hitName, para));
        }

        // Best paragraphs first; keep total under maxChars
        foreach (var item in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Index))
        {
            if (selectedIdx.Contains(item.Index)) continue;
            perNameUsed.TryGetValue(item.Name, out var usedForName);

            var slice = item.Para.Length > 1_600 ? item.Para[..1_600] + "…" : item.Para;
            var addCost = slice.Length + item.Name.Length + 32;
            if (usedForName > 0 && usedForName + addCost > perNameCap) continue;
            if (usedForName == 0 && addCost > perNameCap * 2) continue; // pathological mega-para
            if (totalUsed + addCost > maxChars) continue;

            selectedIdx.Add(item.Index);
            perNameUsed[item.Name] = usedForName + addCost;
            totalUsed += addCost;
        }

        // Emit in book order for readability
        var sb = new StringBuilder();
        foreach (var item in candidates
                     .Where(c => selectedIdx.Contains(c.Index))
                     .GroupBy(c => c.Index)
                     .Select(g => g.First())
                     .OrderBy(c => c.Index))
        {
            var slice = item.Para.Length > 1_600 ? item.Para[..1_600] + "…" : item.Para;
            var block = $"[{item.Name}] {slice}\n\n";
            if (sb.Length + block.Length > maxChars)
            {
                var room = maxChars - sb.Length - 20;
                if (room > 200)
                    sb.Append(block.AsSpan(0, Math.Min(room, block.Length))).Append("…\n");
                break;
            }
            sb.Append(block);
        }

        return sb.ToString().TrimEnd();
    }

    private static readonly Regex LookLanguage = new(
        @"\b(hair|eyes?|wore|wearing|dressed|dress|coat|cloak|hat|beard|mustache|face|skin|pale|dark|"
        + @"fair|tall|short|thin|stout|slender|young|old|aged|years?\s+old|beautiful|handsome|"
        + @"blonde|blond|brunette|redhead|curly|bald|scar|freckle|wardrobe|gown|suit|boots?|"
        + @"complexion|features|figure|build|shoulder|cheek|lip|brow|forehead|chin|nose|"
        + @"species|fur|mane|paw|tail|whisker|feather|scale)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string SelectSpineWindows(string text, int maxChars, int windowCount)
    {
        text = NormalizeNewlines(text);
        if (text.Length <= maxChars) return text;
        windowCount = Math.Clamp(windowCount, 2, 8);

        // Leave room for separators between windows
        var sepOverhead = (windowCount - 1) * 40;
        var usable = Math.Max(1_000, maxChars - sepOverhead);
        var winLen = Math.Max(400, usable / windowCount);

        var sb = new StringBuilder(maxChars + 64);
        for (var w = 0; w < windowCount; w++)
        {
            int start;
            if (windowCount == 1)
                start = 0;
            else if (w == 0)
                start = 0;
            else if (w == windowCount - 1)
                start = Math.Max(0, text.Length - winLen);
            else
            {
                // Even interior anchors
                var frac = w / (double)(windowCount - 1);
                var center = (int)(frac * text.Length);
                start = Math.Clamp(center - winLen / 2, 0, Math.Max(0, text.Length - winLen));
            }

            var len = Math.Min(winLen, text.Length - start);
            // Snap to nearby newline to avoid mid-word cuts
            if (start > 0 && start < text.Length)
            {
                var searchLen = Math.Min(200, text.Length - start);
                var nl = text.IndexOf('\n', start, searchLen);
                if (nl >= start && nl < start + searchLen)
                {
                    start = nl + 1;
                    len = Math.Min(winLen, text.Length - start);
                }
            }

            if (len <= 0) continue;
            var slice = text.Substring(start, len).Trim();
            if (slice.Length == 0) continue;

            if (sb.Length > 0)
                sb.Append("\n\n[[… sample ").Append(w + 1).Append('/').Append(windowCount).Append(" …]]\n\n");
            sb.Append(slice);

            if (sb.Length >= maxChars)
                break;
        }

        if (sb.Length > maxChars)
            return sb.ToString(0, maxChars);
        return sb.ToString();
    }

    private static string NormalizeNewlines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static bool IsNoiseCastToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (Regex.IsMatch(raw, @"^(INT|EXT|EST|I/E|FADE|CUT|TITLE|THE END|OMITTED|CONTINUED)\b",
                RegexOptions.IgnoreCase))
            return true;
        // Pure transitions / camera
        if (raw is "V.O." or "O.S." or "O.C." or "CONT'D" or "CONTINUED") return true;
        return false;
    }

    private static string ToTitleToken(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return raw;
        // "JANE DOE" / "JANE" → "Jane Doe" / "Jane"
        var parts = raw.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) continue;
            parts[i] = char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..].ToLowerInvariant() : "");
        }
        // Preserve hyphenated form loosely
        if (raw.Contains('-', StringComparison.Ordinal))
            return string.Join('-', parts);
        return string.Join(' ', parts);
    }

    /// <summary>Weak placeholder looks that must not be written as final seeds.</summary>
    public static bool IsStubLook(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.Contains("as described in the screenplay", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("as in the screenplay", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("as described in the book", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("see screenplay", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(t, @"^Match\s+.+\s+consistently", RegexOptions.IgnoreCase)) return true;
        if (t.Length < 12) return true;
        return false;
    }

    private static string StripFences(string text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = Regex.Replace(text, @"^```(?:json|text)?\s*", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*```\s*$", "");
        }
        return text.Trim();
    }

    private static Dictionary<string, object?> NormalizeCastDoc(
        Dictionary<string, object?> parsed,
        string projectId)
    {
        var outDoc = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["schema_version"] = "cast_seeds.v1",
            ["generation"] = new Dictionary<string, object?>
            {
                ["method"] = "CastFromScreenplayService",
                ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            },
        };

        if (parsed.TryGetValue("movie_title", out var mt) && mt is not null)
            outDoc["movie_title"] = mt.ToString();
        else
            outDoc["movie_title"] = projectId;

        if (parsed.TryGetValue("render_style_lock", out var rsl) && rsl is not null)
            outDoc["render_style_lock"] = rsl.ToString();
        // Film-level audience/performance conventions inferred from book (not hardcoded gaze recipes)
        if (parsed.TryGetValue("performance_lock", out var pl) && pl is not null &&
            !string.IsNullOrWhiteSpace(pl.ToString()))
            outDoc["performance_lock"] = pl.ToString()!.Trim();

        var seedsIn = GetSeedsDict(parsed);
        var seedsOut = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in seedsIn)
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var k = key.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
                ? key
                : "Character_" + Regex.Replace(key, @"[^A-Za-z0-9]+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(k) || k == "Character_") continue;

            var name = seed.TryGetValue("canonical_given_name", out var cn) && cn is not null
                ? cn.ToString()!
                : k.Replace("Character_", "").Replace('_', ' ');

            var off = string.Equals(
                seed.TryGetValue("display_name_policy", out var pol) ? pol?.ToString() : null,
                "never_on_screen",
                StringComparison.OrdinalIgnoreCase);

            // Prefer real model looks; never invent "as in the screenplay" stubs.
            var desc = CoerceString(seed, "description") ?? "";
            if (IsStubLook(desc))
                desc = off ? $"{name} (voice only; not on screen)." : "";
            else if (off && string.IsNullOrWhiteSpace(desc))
                desc = $"{name} (voice only; not on screen).";

            var clean = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["description"] = desc,
                ["canonical_given_name"] = name,
                ["display_name_policy"] = off ? "never_on_screen" : "ok_anytime",
                ["voice_label"] = CoerceString(seed, "voice_label") ?? name.Replace(' ', '_'),
                ["voice_profile"] = CoerceString(seed, "voice_profile")
                    ?? "Consistent character voice every scene.",
                ["reference_image_placeholder"] = CoerceString(seed, "reference_image_placeholder")
                    ?? ProjectStore.CharacterRefFileName(k),
            };

            var vlock = CoerceString(seed, "visual_lock") ?? "";
            if (!off)
            {
                clean["visual_lock"] = IsStubLook(vlock) ? "" : vlock;
            }

            if (seed.TryGetValue("wardrobe_always", out var wa) && wa is List<object?> list)
                clean["wardrobe_always"] = list;
            // Common model aliases
            if (!clean.ContainsKey("wardrobe_always") &&
                seed.TryGetValue("wardrobe", out var w2) && w2 is List<object?> list2)
                clean["wardrobe_always"] = list2;
            if (seed.TryGetValue("source_image_pages", out var sip) && sip is List<object?> pages)
                clean["source_image_pages"] = pages;

            var perfNotes = CoerceString(seed, "performance_notes");
            if (!string.IsNullOrWhiteSpace(perfNotes))
                clean["performance_notes"] = perfNotes;

            seedsOut[k] = clean;
        }

        outDoc["character_seed_tokens"] = seedsOut;
        return outDoc;
    }

    private static Dictionary<string, object?> GetSeedsDict(Dictionary<string, object?> doc)
    {
        if (doc.TryGetValue("character_seed_tokens", out var s) && s is Dictionary<string, object?> d)
            return d;
        // Offline fakes / older models sometimes use cast_seeds
        if (doc.TryGetValue("cast_seeds", out var cs) && cs is Dictionary<string, object?> dCs)
            return NormalizeLooseSeedMap(dCs);
        if (doc.TryGetValue("global_production_variables", out var g) &&
            g is Dictionary<string, object?> gpv &&
            gpv.TryGetValue("character_seed_tokens", out var s2) &&
            s2 is Dictionary<string, object?> d2)
            return d2;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Map display_name / wardrobe aliases into seed shape.</summary>
    private static Dictionary<string, object?> NormalizeLooseSeedMap(Dictionary<string, object?> raw)
    {
        var outMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in raw)
        {
            if (val is not Dictionary<string, object?> seed)
            {
                outMap[key] = val;
                continue;
            }
            var copy = new Dictionary<string, object?>(seed, StringComparer.OrdinalIgnoreCase);
            if (!copy.ContainsKey("canonical_given_name") &&
                copy.TryGetValue("display_name", out var dn) && dn is not null)
                copy["canonical_given_name"] = dn.ToString();
            if (!copy.ContainsKey("wardrobe_always") &&
                copy.TryGetValue("wardrobe", out var w) && w is List<object?> wl)
                copy["wardrobe_always"] = wl;
            outMap[key] = copy;
        }
        return outMap;
    }

    private static JsonElement GetSeedsElement(JsonElement root)
    {
        if (root.TryGetProperty("character_seed_tokens", out var s) && s.ValueKind == JsonValueKind.Object)
            return s;
        if (root.TryGetProperty("global_production_variables", out var g) &&
            g.TryGetProperty("character_seed_tokens", out var s2) &&
            s2.ValueKind == JsonValueKind.Object)
            return s2;
        return default;
    }

    private static string? CoerceString(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString()?.Trim() : null;
}
