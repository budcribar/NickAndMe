using System.Text;
using System.Text.RegularExpressions;
using FilmStudio.Engine.Abstractions;

namespace FilmStudio.Engine;

/// <summary>
/// Book text → editable Fountain via chat (<c>prompts/book_to_fountain.txt</c>).
/// Short books: single shot. Long novels: multi-chunk adapt → stitch → merge pass
/// for full-arc coverage without head-only truncation.
/// </summary>
public static class BookToFountainConverter
{
    /// <summary>Above this, multi-chunk path runs instead of single-shot trim.</summary>
    public const int SingleShotMaxChars = 28_000;

    /// <summary>Soft max characters of book text per adapt chunk.</summary>
    public const int ChunkSoftMaxChars = 16_000;

    /// <summary>Hard cap on Grok adapt calls (cost / latency).</summary>
    public const int MaxAdaptChunks = 8;

    /// <summary>
    /// Fallback body if <c>prompts/book_to_fountain.txt</c> is missing (tests / broken workspace).
    /// </summary>
    public const string FountainOutputOverride = """
        Act as an expert screenwriter. Adapt the book into Fountain 1.1 only (no JSON).
        Target runtime about {{TOTAL_RUNTIME_MINUTES}} minutes. Real INT./EXT. locations.
        No page numbers in the script. NARRATOR for narration; CHARACTER cues for speech.
        Closed cast. VO↔visual fidelity. No major invented plot.
        DIALOGUE: prefer the book’s actual spoken words — do not paraphrase iconic lines
        into generic modern dialogue (classics, verse, first-person monologues especially).
        """;

    /// <summary>
    /// Generate Fountain from prepared book text. Long books use multi-chunk adapt→merge.
    /// </summary>
    public static async Task<string> ConvertAsync(
        string workspaceRoot,
        string title,
        string bookText,
        string? author = null,
        int totalRuntimeMinutes = 10,
        IGrokChatClient? chat = null,
        string model = "grok-4.5",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bookText))
            throw new InvalidOperationException("Book text is empty");

        if (chat is null || !chat.IsConfigured)
            throw new InvalidOperationException(
                "Connect service to build a screenplay draft from the book.");

        bookText = NormalizeBookText(bookText);
        var system = await BuildSystemPromptAsync(workspaceRoot, totalRuntimeMinutes, ct)
            .ConfigureAwait(false);
        var pageCount = CountPageMarkers(bookText);

        string text;
        try
        {
            if (bookText.Length <= SingleShotMaxChars)
            {
                onProgress?.Invoke("Adapting book → Fountain (single pass)…");
                text = await ConvertSingleShotAsync(
                    system, title, author, pageCount, totalRuntimeMinutes, bookText,
                    chat, model, ct).ConfigureAwait(false);
            }
            else
            {
                onProgress?.Invoke("Long book — multi-chunk adapt → merge…");
                text = await ConvertMultiChunkAsync(
                    system, title, author, pageCount, totalRuntimeMinutes, bookText,
                    chat, model, onProgress, ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) when (LooksLikeGoodFountain(ConvertHeuristic(title, bookText, author)))
        {
            // Chat output failed structural gates — still give a usable draft from book text
            onProgress?.Invoke("Model draft unusable — building structured draft from book text…");
            text = ConvertHeuristic(title, bookText, author);
        }

        text = EnsureDraftDate(text);
        // Hard strip — models still emit tags even when the prompt forbids them
        text = StripBookPageTags(text);
        if (!LooksLikeGoodFountain(text))
            throw new InvalidOperationException(
                "Could not build a usable screenplay from the book. Try again or import a .fountain file.");

        return ScreenplayService.NormalizeText(text);
    }

    /// <summary>
    /// Remove operator-facing book page tags from Fountain
    /// (<c>= page N</c>, <c>[[page N]]</c>). Book linkage uses text/order match in the UI.
    /// </summary>
    public static string StripBookPageTags(string fountain)
    {
        if (string.IsNullOrEmpty(fountain)) return fountain ?? "";

        // Whole-line synopsis tags: = page 2  /  = pages 2-4
        fountain = Regex.Replace(
            fountain,
            @"(?im)^[ \t]*=\s*pages?\s+\d+(?:\s*[-–]\s*\d+)?\s*\r?\n?",
            "");

        // Standalone note lines: [[page 2]] or [[pages 2-3]]
        fountain = Regex.Replace(
            fountain,
            @"(?im)^[ \t]*\[\[\s*pages?\s+\d+(?:\s*[-–]\s*\d+)?\s*\]\]\s*\r?\n?",
            "");

        // Inline notes left in a line of other text
        fountain = Regex.Replace(
            fountain,
            @"\[\[\s*pages?\s+\d+(?:\s*[-–]\s*\d+)?\s*\]\]",
            "",
            RegexOptions.IgnoreCase);

        // Collapse excess blank lines left behind
        fountain = Regex.Replace(fountain, @"\n{3,}", "\n\n");
        return fountain.TrimEnd() + (fountain.EndsWith('\n') || fountain.Length == 0 ? "" : "\n");
    }

    /// <summary>Load <c>prompts/book_to_fountain.txt</c>.</summary>
    public static Task<string> BuildSystemPromptAsync(
        string workspaceRoot,
        int totalRuntimeMinutes,
        CancellationToken ct = default) =>
        Stage1PromptPack.LoadBookToFountainSystemPromptAsync(
            workspaceRoot,
            totalRuntimeMinutes,
            fallbackBody: FountainOutputOverride,
            ct);

    /// <summary>
    /// Split book into ordered chunks for multi-pass adaptation (public for tests).
    /// </summary>
    public static IReadOnlyList<string> ChunkBookForAdaptation(
        string bookText,
        int maxChunks = MaxAdaptChunks,
        int softMaxChars = ChunkSoftMaxChars)
    {
        bookText = NormalizeBookText(bookText);
        maxChunks = Math.Clamp(maxChunks, 1, 16);
        softMaxChars = Math.Clamp(softMaxChars, 4_000, 40_000);

        if (bookText.Length <= softMaxChars)
            return new[] { bookText };

        var units = SplitIntoUnits(bookText);
        if (units.Count == 0)
            return new[] { bookText };

        // Pack units into ≤ maxChunks buckets without exceeding softMax when possible
        var targetChunks = Math.Min(
            maxChunks,
            Math.Max(2, (int)Math.Ceiling(bookText.Length / (double)softMaxChars)));

        var chunks = new List<string>();
        var current = new StringBuilder();
        var idealSize = Math.Max(softMaxChars / 2, bookText.Length / targetChunks);

        foreach (var unit in units)
        {
            if (current.Length > 0 &&
                current.Length + unit.Length > softMaxChars &&
                chunks.Count < maxChunks - 1)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            else if (current.Length > 0 &&
                     current.Length >= idealSize &&
                     chunks.Count < targetChunks - 1 &&
                     chunks.Count < maxChunks - 1)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            if (unit.Length > softMaxChars)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }
                foreach (var slice in SliceLongUnit(unit, softMaxChars))
                {
                    if (chunks.Count >= maxChunks - 1)
                    {
                        current.AppendLine(slice);
                    }
                    else
                        chunks.Add(slice);
                }
                continue;
            }

            if (current.Length > 0) current.Append("\n\n");
            current.Append(unit);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        // If we still overshot maxChunks (shouldn't), merge tails
        while (chunks.Count > maxChunks)
        {
            var last = chunks[^1];
            chunks.RemoveAt(chunks.Count - 1);
            chunks[^1] = chunks[^1] + "\n\n" + last;
        }

        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    /// <summary>Stitch partial Fountain scripts (title page from first only). Public for tests.</summary>
    public static string StitchFountainParts(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0) return "";
        if (parts.Count == 1) return ScreenplayService.NormalizeText(parts[0]);

        var sb = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = StripFences(parts[i] ?? "");
            if (string.IsNullOrWhiteSpace(part)) continue;

            if (i == 0)
            {
                part = StripTrailingEndMarkers(part);
                sb.Append(part.TrimEnd());
            }
            else
            {
                part = StripTitlePage(part);
                part = StripTrailingEndMarkers(part);
                if (i < parts.Count - 1)
                    part = StripTrailingEndMarkers(part);
                if (string.IsNullOrWhiteSpace(part)) continue;
                sb.Append("\n\n");
                sb.Append(part.Trim());
            }
        }

        var merged = sb.ToString().Trim();
        if (!Regex.IsMatch(merged, @"(?im)^(FADE OUT\.|THE END)\s*$"))
            merged += "\n\nFADE OUT.\n\nTHE END\n";
        return ScreenplayService.NormalizeText(merged);
    }

    /// <summary>
    /// Minimal offline stub (tests / emergency). Production always uses chat.
    /// </summary>
    public static string ConvertHeuristic(string title, string bookText, string? author = null)
    {
        var pages = BookContextService.ParseBookPages(bookText);
        var sb = new StringBuilder();
        sb.Append("Title: ").Append(string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(author))
        {
            sb.Append("Credit: Written by\nAuthor: ").Append(author.Trim()).Append('\n');
        }
        sb.Append("Source: Adapted from book\n");
        sb.Append("Draft date: ").Append(DateTime.Now.ToString("M/d/yyyy")).Append("\n\n");

        if (pages.Count == 0)
        {
            sb.Append("INT. ROOM - DAY\n\nNARRATOR\n[[No book text.]]\n");
            return ScreenplayService.NormalizeText(sb.ToString());
        }

        foreach (var page in pages)
        {
            var body = (page.Text ?? "").Trim();
            if (body.Length < 12) continue;
            if (Regex.IsMatch(body, @"^\(illustration", RegexOptions.IgnoreCase)) continue;

            sb.Append("INT. ROOM - DAY\n\n");
            sb.Append("NARRATOR\n");
            var line = Regex.Replace(body, @"\s+", " ").Trim();
            if (line.Length > 400) line = line[..400] + "…";
            sb.Append(line).Append("\n\n");
        }

        return ScreenplayService.NormalizeText(StripBookPageTags(sb.ToString()));
    }

    /// <summary>
    /// Structural check for usable Fountain. Page tags are never required (stripped for operators).
    /// </summary>
    public static bool LooksLikeGoodFountain(string text, bool requirePageTags = false)
    {
        _ = requirePageTags; // legacy param — page tags are no longer part of the product gate
        text = StripBookPageTags(text ?? "");
        if (string.IsNullOrWhiteSpace(text) || text.Length < 80) return false;

        var hasScene = Regex.IsMatch(text, @"(?im)^(INT|EXT|EST|I/E)[\./ ]");
        var dumpCount = Regex.Matches(text, @"(?im)^INT\.\s+STORY\s+-\s+PAGE\s+\d+").Count;
        if (dumpCount >= 2) return false;

        // Prefer real locations; INT. SCENE is ok if there is dialogue/narration body
        var realLoc = Regex.IsMatch(text, @"(?im)^(INT|EXT)\.\s+(?!SCENE\b)[A-Z0-9]");
        var hasNarratorOrDialogue =
            Regex.IsMatch(text, @"(?im)^NARRATOR\s*$") ||
            Regex.IsMatch(text, @"(?m)^[A-Z][A-Z0-9 &'.\-]{1,40}\s*$");
        var hasActionBody = Regex.IsMatch(text, @"(?m)^[a-zA-Z].{20,}");

        if (!hasScene) return false;
        return realLoc || hasNarratorOrDialogue || hasActionBody;
    }

    // ── single / multi paths ─────────────────────────────────────────────

    private static async Task<string> ConvertSingleShotAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int totalMinutes,
        string bookText,
        IGrokChatClient chat,
        string model,
        CancellationToken ct)
    {
        var bookForPrompt = bookText.Length <= SingleShotMaxChars
            ? bookText
            : TrimBookForPrompt(bookText);
        var user = BuildUserPrompt(title, author, pageCount, totalMinutes, bookForPrompt, chunkIndex: 0, chunkTotal: 1);

        var text = await chat.CompleteAsync(system, user, model, temperature: 0.2, ct)
            .ConfigureAwait(false);
        text = StripBookPageTags(StripFences(text));

        if (!LooksLikeGoodFountain(text))
        {
            var retryUser = user + RetrySuffix(hasPageMarkers: false);
            text = await chat.CompleteAsync(system, retryUser, model, temperature: 0.15, ct)
                .ConfigureAwait(false);
            text = StripBookPageTags(StripFences(text));
        }

        if (!LooksLikeGoodFountain(text))
            throw new InvalidOperationException(
                "Could not build a usable screenplay from the book. Try again or import a .fountain file.");

        return text;
    }

    private static async Task<string> ConvertMultiChunkAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int totalMinutes,
        string bookText,
        IGrokChatClient chat,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var chunks = ChunkBookForAdaptation(bookText);
        onProgress?.Invoke($"Book split into {chunks.Count} chunk(s) for adaptation…");

        var parts = new List<string>();
        string? continuity = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"Adapting chunk {i + 1}/{chunks.Count}…");

            var user = BuildUserPrompt(
                title, author, pageCount, totalMinutes, chunks[i],
                chunkIndex: i, chunkTotal: chunks.Count, continuity: continuity);

            var part = await chat.CompleteAsync(system, user, model, temperature: 0.2, ct)
                .ConfigureAwait(false);
            part = StripBookPageTags(StripFences(part));

            if (!LooksLikeGoodFountain(part) && part.Length < 80)
            {
                part = await chat.CompleteAsync(system, user + RetrySuffix(false), model, 0.15, ct)
                    .ConfigureAwait(false);
                part = StripBookPageTags(StripFences(part));
            }

            parts.Add(part);
            continuity = BuildContinuityBrief(part, i + 1, chunks.Count);
        }

        onProgress?.Invoke("Stitching chunk screenplays…");
        var stitched = StripBookPageTags(StitchFountainParts(parts));

        // Merge pass: unify cast tokens, cut duplicate setups, one ending
        if (parts.Count >= 2)
        {
            onProgress?.Invoke("Merge pass — unifying full-novel screenplay…");
            try
            {
                var merged = StripBookPageTags(await MergeFountainPartsAsync(
                    system, title, author, totalMinutes, parts, chat, model, ct)
                    .ConfigureAwait(false));
                if (LooksLikeGoodFountain(merged) &&
                    CountSceneHeadings(merged) >= Math.Max(2, CountSceneHeadings(stitched) / 3))
                {
                    return merged;
                }

                onProgress?.Invoke("Merge pass weak — using stitched chunks…");
            }
            catch (Exception)
            {
                onProgress?.Invoke("Merge pass failed — using stitched chunks…");
            }
        }

        if (!LooksLikeGoodFountain(stitched))
            throw new InvalidOperationException(
                "Could not build a usable multi-chunk screenplay from the book.");

        return stitched;
    }

    private static async Task<string> MergeFountainPartsAsync(
        string system,
        string title,
        string? author,
        int totalMinutes,
        IReadOnlyList<string> parts,
        IGrokChatClient chat,
        string model,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MULTI-CHUNK MERGE TASK");
        sb.AppendLine($"Project title hint: {title}");
        sb.AppendLine($"Author hint: {author ?? "(unknown)"}");
        sb.AppendLine($"TOTAL_RUNTIME_MINUTES = {totalMinutes}");
        sb.AppendLine();
        sb.AppendLine("You are given ordered Fountain partials adapted from successive book chunks.");
        sb.AppendLine("Merge them into ONE complete Fountain 1.1 screenplay:");
        sb.AppendLine("- Single title page only (start of file).");
        sb.AppendLine("- Consistent CHARACTER tokens (same person = same ALL-CAPS name).");
        sb.AppendLine("- Real INT./EXT. locations; no INT. STORY / PAGE headings.");
        sb.AppendLine("- Full story arc across all parts (do not drop the ending).");
        sb.AppendLine("- Remove duplicate cold opens / repeated setups when chunks overlap.");
        sb.AppendLine("- One FADE OUT / THE END at the finish.");
        sb.AppendLine("- Preserve book-faithful dialogue from the parts; do not re-paraphrase iconic lines.");
        sb.AppendLine("- No markdown fences, no JSON, no commentary.");
        sb.AppendLine("- Do not include = page N or [[page N]] tags — strip them if present in parts.");
        sb.AppendLine();

        // Budget merge input (~60k total for parts)
        const int budget = 60_000;
        var per = Math.Max(4_000, budget / Math.Max(1, parts.Count));
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i] ?? "";
            if (p.Length > per)
                p = p[..per] + "\n\n[[… partial truncated for merge prompt …]]\n";
            sb.AppendLine($"===== FOUNTAIN_PART {i + 1}/{parts.Count} =====");
            sb.AppendLine(p.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("===== END PARTS =====");
        sb.AppendLine("Return the merged Fountain screenplay only.");

        var mergeSystem = system + """


            ================================================================================
            MERGE MODE (HARD)
            ================================================================================
            You are merging multi-chunk Fountain partials into one final screenplay.
            Prefer story completeness and cast/location consistency over preserving every line.
            """;

        var text = await chat.CompleteAsync(mergeSystem, sb.ToString(), model, temperature: 0.15, ct)
            .ConfigureAwait(false);
        return StripFences(text);
    }

    // ── prompts / continuity ─────────────────────────────────────────────

    private static string BuildUserPrompt(
        string title,
        string? author,
        int pageCount,
        int totalMinutes,
        string bookForPrompt,
        int chunkIndex,
        int chunkTotal,
        string? continuity = null)
    {
        var lines = new List<string>
        {
            $"TOTAL_RUNTIME_MINUTES = {totalMinutes}",
            $"BOOK_CHUNK {chunkIndex + 1}/{chunkTotal}",
            "",
            $"Project title hint: {title}",
            $"Author hint: {author ?? "(unknown — infer from book if present)"}",
            $"Book page markers (approx): {pageCount}",
            "",
        };

        if (chunkTotal <= 1)
        {
            lines.Add("Write the complete Fountain screenplay only (see system prompt).");
            lines.Add("Do not emit page numbers or page tags.");
        }
        else if (chunkIndex == 0)
        {
            lines.Add("This is chunk 1 of a multi-chunk novel adaptation.");
            lines.Add("Write Fountain with a full title page + scenes for THIS chunk only.");
            lines.Add("Establish cast tokens and locations you will reuse later.");
            lines.Add("Do NOT write FADE OUT / THE END yet — more story follows.");
            lines.Add("Do not emit page numbers or page tags.");
        }
        else
        {
            lines.Add($"This is chunk {chunkIndex + 1} of {chunkTotal} of a multi-chunk novel adaptation.");
            lines.Add("Continue the SAME screenplay — NO title page.");
            lines.Add("Reuse established CHARACTER tokens and location heading wording.");
            lines.Add("Output only new INT./EXT. scenes for this chunk's story.");
            if (chunkIndex < chunkTotal - 1)
                lines.Add("Do NOT write FADE OUT / THE END yet — more story follows.");
            else
                lines.Add("This is the FINAL chunk — include resolution and FADE OUT / THE END.");
            lines.Add("Do not emit page numbers or page tags.");
            if (!string.IsNullOrWhiteSpace(continuity))
            {
                lines.Add("");
                lines.Add("CONTINUITY FROM PRIOR CHUNKS:");
                lines.Add(continuity.Trim());
            }
        }

        lines.Add("");
        lines.Add("BOOK_TEXT:");
        lines.Add(bookForPrompt);
        return string.Join("\n", lines);
    }

    private static string BuildContinuityBrief(string fountainPart, int chunkDone, int chunkTotal)
    {
        var heads = Regex.Matches(fountainPart, @"(?im)^(INT|EXT|EST|I/E)[^\n]+")
            .Select(m => m.Value.Trim())
            .Where(h => h.Length > 0)
            .ToList();
        var chars = Regex.Matches(fountainPart, @"(?m)^([A-Z][A-Z0-9 &'.\-]{1,40})\s*$")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(c => !Regex.IsMatch(c, @"^(INT|EXT|EST|I/E|FADE|CUT|TITLE|THE)\b", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Chunks completed: {chunkDone}/{chunkTotal}");
        if (chars.Count > 0)
            sb.AppendLine("Cast tokens so far: " + string.Join(", ", chars));
        if (heads.Count > 0)
        {
            sb.AppendLine("Recent scene headings:");
            foreach (var h in heads.TakeLast(4))
                sb.AppendLine("  " + h);
        }

        // Short body sample for voice continuity
        var sample = fountainPart.Length > 1200 ? fountainPart[^1200..] : fountainPart;
        sb.AppendLine("Tail of prior Fountain (do not repeat; continue after):");
        sb.AppendLine(sample.Trim());
        return sb.ToString();
    }

    private static string RetrySuffix(bool hasPageMarkers) => """


        IMPORTANT: Previous output was not valid Fountain for our pipeline.
        Re-output Fountain only.
        - Every scene: INT./EXT. real location (not STORY, not PAGE in the heading).
        - Do not emit = page N or [[page N]] tags.
        - Use NARRATOR and CHARACTER dialogue where the book has narration or speech.
        """;

    // ── chunking helpers ─────────────────────────────────────────────────

    private static List<string> SplitIntoUnits(string bookText)
    {
        // 1) Page markers
        var pageParts = Regex.Split(bookText, @"(?=---\s*PAGE\s+\d+\s*---)", RegexOptions.IgnoreCase)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (pageParts.Count >= 3)
            return pageParts;

        // 2) Chapters
        var chapterParts = Regex.Split(
                bookText,
                @"(?m)(?=^(?:CHAPTER|Chapter|BOOK|Book|PART|Part)\s+([IVXLCDM\d]+|[A-Z][A-Z\s]{0,40})\b)",
                RegexOptions.Multiline)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (chapterParts.Count >= 3)
            return chapterParts;

        // 3) Double-newline paragraphs packed later
        var paras = Regex.Split(bookText, @"\n\s*\n+")
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (paras.Count >= 4)
            return paras;

        return new List<string> { bookText };
    }

    private static IEnumerable<string> SliceLongUnit(string unit, int softMax)
    {
        if (unit.Length <= softMax)
        {
            yield return unit;
            yield break;
        }

        var i = 0;
        while (i < unit.Length)
        {
            var len = Math.Min(softMax, unit.Length - i);
            if (i + len < unit.Length)
            {
                var window = unit.AsSpan(i, len);
                var breakAt = window.LastIndexOf("\n\n");
                if (breakAt < softMax / 3)
                    breakAt = window.LastIndexOf('\n');
                if (breakAt >= softMax / 3)
                    len = breakAt;
            }

            yield return unit.Substring(i, len).Trim();
            i += Math.Max(1, len);
        }
    }

    private static string NormalizeBookText(string bookText) =>
        bookText.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static int CountPageMarkers(string bookText) =>
        Regex.Matches(bookText, @"---\s*PAGE\s+\d+\s*---", RegexOptions.IgnoreCase).Count;

    private static int CountSceneHeadings(string fountain) =>
        Regex.Matches(fountain ?? "", @"(?im)^(INT|EXT|EST|I/E)[\./ ]").Count;

    // ── fountain text surgery ────────────────────────────────────────────

    private static string StripTitlePage(string fountain)
    {
        fountain = fountain.Replace("\r\n", "\n").Trim();
        // Drop leading title-page key lines until first scene heading
        var lines = fountain.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) { i++; continue; }
            if (Regex.IsMatch(t, @"^(Title|Credit|Author|Authors|Source|Draft date|Contact|Notes)\s*:", RegexOptions.IgnoreCase))
            {
                i++;
                continue;
            }
            if (t is "===" or "---")
            {
                i++;
                continue;
            }
            break;
        }

        // If no scene heading yet, scan forward to first INT./EXT.
        var rest = string.Join("\n", lines.Skip(i));
        var m = Regex.Match(rest, @"(?im)^(INT|EXT|EST|I/E)[\./ ]");
        if (m.Success && m.Index > 0)
            rest = rest[m.Index..];
        return rest.Trim();
    }

    private static string StripTrailingEndMarkers(string fountain)
    {
        fountain = fountain.TrimEnd();
        fountain = Regex.Replace(
            fountain,
            @"\n(?:FADE OUT\.?\s*\n+)?THE END\s*$",
            "",
            RegexOptions.IgnoreCase);
        fountain = Regex.Replace(
            fountain,
            @"\nFADE OUT\.?\s*$",
            "",
            RegexOptions.IgnoreCase);
        return fountain.TrimEnd();
    }

    private static string EnsureDraftDate(string text)
    {
        if (Regex.IsMatch(text, @"(?im)^Draft date:"))
            return text;
        var m = Regex.Match(text, @"(?im)^Title:\s*.+$");
        if (m.Success)
        {
            return text.Insert(
                m.Index + m.Length,
                $"\nDraft date: {DateTime.Now:M/d/yyyy}");
        }
        return text;
    }

    /// <summary>
    /// Fit long novels into a single-shot window (start/middle/end) when multi-chunk is disabled
    /// or as fallback inside a too-large single unit.
    /// </summary>
    private static string TrimBookForPrompt(string bookText)
    {
        bookText = NormalizeBookText(bookText);
        const int max = SingleShotMaxChars;
        if (bookText.Length <= max) return bookText;

        var pages = Regex.Split(bookText, @"(?=---\s*PAGE\s+\d+\s*---)", RegexOptions.IgnoreCase)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (pages.Count >= 6)
        {
            var take = Math.Max(2, pages.Count / 3);
            var head = string.Concat(pages.Take(take));
            var midStart = Math.Max(0, (pages.Count - take) / 2);
            var mid = string.Concat(pages.Skip(midStart).Take(take));
            var tail = string.Concat(pages.Skip(Math.Max(0, pages.Count - take)));
            var assembled = string.Join(
                "\n\n[[… middle of book omitted for length …]]\n\n",
                new[] { head.Trim(), mid.Trim(), tail.Trim() }.Where(s => s.Length > 0));
            if (assembled.Length <= max)
                return assembled + "\n\n[[Book excerpted (start/middle/end) — adapt a complete short film from these parts.]]\n";
            bookText = assembled;
        }

        var headBudget = (int)(max * 0.40);
        var midBudget = (int)(max * 0.28);
        var tailBudget = max - headBudget - midBudget - 200;
        if (tailBudget < 2000)
        {
            return bookText[..max] +
                   "\n\n[[Book text truncated for length — adapt what is above.]]\n";
        }

        var headPart = bookText[..headBudget];
        var midCenter = bookText.Length / 2;
        var midStartIdx = Math.Clamp(midCenter - midBudget / 2, 0, bookText.Length - midBudget);
        var midPart = bookText.Substring(midStartIdx, midBudget);
        var tailPart = bookText[^tailBudget..];
        return headPart.TrimEnd() +
               "\n\n[[… middle of book omitted for length …]]\n\n" +
               midPart.Trim() +
               "\n\n[[… later chapters omitted for length …]]\n\n" +
               tailPart.TrimStart() +
               "\n\n[[Book excerpted (start/middle/end) — adapt a complete short film covering the full arc present across these parts. Do not invent missing chapters.]]\n";
    }

    private static string StripFences(string text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = Regex.Replace(text, @"^```(?:fountain|text|markdown)?\s*", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*```\s*$", "");
        }
        return text.Trim();
    }
}
