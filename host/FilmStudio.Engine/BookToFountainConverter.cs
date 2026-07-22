using System.Text;
using System.Text.RegularExpressions;
using FilmStudio.Engine.Abstractions;

namespace FilmStudio.Engine;

/// <summary>
/// Book text → editable Fountain via chat (<c>prompts/book_to_fountain.txt</c>).
/// Prefers a single full-book pass when input fits the model budget; multi-chunk
/// adapt → stitch → merge is a fallback for over-budget books or weak quality.
/// </summary>
public static class BookToFountainConverter
{
    /// <summary>
    /// Historical single-shot length threshold (also used as a "large book" floor in tests).
    /// Path selection now uses <see cref="ResolvePromptBudget"/> instead of this alone.
    /// </summary>
    public const int SingleShotMaxChars = 28_000;

    /// <summary>Default soft max book chars per adapt chunk when caller omits budget.</summary>
    public const int ChunkSoftMaxChars = 16_000;

    /// <summary>Default cap on adapt calls for typical books (cost / latency).</summary>
    public const int MaxAdaptChunks = 8;

    /// <summary>
    /// Absolute ceiling on adapt calls even for very long books. <see cref="ResolveMaxChunks"/>
    /// scales past <see cref="MaxAdaptChunks"/> up to this when the book needs it, so a long
    /// novel doesn't dump everything past chunk N into one oversized final chunk.
    /// </summary>
    public const int AbsoluteMaxAdaptChunks = 24;

    /// <summary>Default max BOOK_TEXT chars for one chat call (large-context chat models).</summary>
    public const int DefaultSingleShotBookMaxChars = 120_000;

    /// <summary>Default soft max book chars per multi-chunk adapt call.</summary>
    public const int DefaultChunkSoftMaxChars = 40_000;

    /// <summary>Books shorter than this never use multi-chunk fallback (chunking won't help).</summary>
    public const int MinBookCharsForChunkFallback = 24_000;

    /// <summary>Product safety ceiling for a single book payload.</summary>
    public const int AbsoluteSingleShotCeiling = 400_000;

    /// <summary>Reserved chars for system prompt + scaffolding + continuity.</summary>
    public const int ReservedOverheadChars = 12_000;

    public enum AdaptPath
    {
        Single,
        Multi,
    }

    /// <summary>Per-model (or default) input budgets for book → Fountain.</summary>
    public sealed class PromptBudget
    {
        public required string ModelId { get; init; }

        /// <summary>Max chars of BOOK_TEXT in one chat call.</summary>
        public int SingleShotBookMaxChars { get; init; }

        /// <summary>Soft max book chars per adapt chunk.</summary>
        public int ChunkSoftMaxChars { get; init; }

        public int MaxChunks { get; init; }

        public int ReservedOverheadChars { get; init; }
    }

    /// <summary>Result of structural + coverage checks after a model draft.</summary>
    public sealed class QualityResult
    {
        public bool Ok { get; init; }
        public string Reason { get; init; } = "";
        public int SceneCount { get; init; }
        public int FountainChars { get; init; }
        public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
        public bool HasHardFailure { get; init; }
    }

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
    /// Generate Fountain from prepared book text.
    /// Single-shot first when the book fits the model budget; multi-chunk on budget miss or quality fail.
    /// </summary>
    public static async Task<string> ConvertAsync(
        string workspaceRoot,
        string title,
        string bookText,
        string? author = null,
        int totalRuntimeMinutes = 10,
        IChatClient? chat = null,
        string model = "grok-4.5",
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        PromptBudget? budgetOverride = null)
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
        var budget = budgetOverride ?? ResolvePromptBudget(model);
        totalRuntimeMinutes = Math.Clamp(totalRuntimeMinutes, 1, 180);

        string text;
        try
        {
            if (FitsSingleShot(bookText, budget))
            {
                onProgress?.Invoke("Adapting book → Fountain (single pass)…");
                var single = await TrySingleShotWithGateAsync(
                    system, title, author, pageCount, totalRuntimeMinutes, bookText,
                    chat, model, budget, onProgress, ct).ConfigureAwait(false);

                if (single is not null)
                {
                    text = single;
                }
                else if (ShouldChunkFallback(bookText, budget))
                {
                    onProgress?.Invoke("Falling back to multi-chunk adapt…");
                    text = await ConvertMultiChunkAsync(
                        system, title, author, pageCount, totalRuntimeMinutes, bookText,
                        chat, model, onProgress, ct,
                        softMaxChars: budget.ChunkSoftMaxChars,
                        maxChunks: ResolveMaxChunks(bookText, budget)).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Could not build a usable screenplay from the book. Try again or import a .fountain file.");
                }
            }
            else
            {
                onProgress?.Invoke("Book exceeds model budget — multi-chunk adapt…");
                text = await ConvertMultiChunkAsync(
                    system, title, author, pageCount, totalRuntimeMinutes, bookText,
                    chat, model, onProgress, ct,
                    softMaxChars: budget.ChunkSoftMaxChars,
                    maxChunks: ResolveMaxChunks(bookText, budget)).ConfigureAwait(false);
            }

            // Multi path: soft coverage failures still accept a structurally good draft
            var multiGate = EvaluateQuality(text, bookText, totalRuntimeMinutes, AdaptPath.Multi);
            if (!multiGate.Ok && multiGate.HasHardFailure)
                throw new InvalidOperationException(
                    "Could not build a usable screenplay from the book. Try again or import a .fountain file.");
        }
        catch (InvalidOperationException) when (LooksLikeGoodFountain(ConvertHeuristic(title, bookText, author)))
        {
            // Chat output failed structural gates — still give a usable draft from book text
            onProgress?.Invoke("Model draft unusable — building structured draft from book text…");
            text = ConvertHeuristic(title, bookText, author);
        }

        // Generation repairs — no operator hand-edit path
        text = await RepairVagueLocationHeadingsAsync(
            system, text, chat, model, onProgress, ct).ConfigureAwait(false);
        text = NormalizeSceneHeadingWording(text);
        text = await RepairGenericNumberedSpeakersAsync(
            system, text, chat, model, onProgress, ct).ConfigureAwait(false);

        text = EnsureDraftDate(text);
        // Models invent wrong years (e.g. 3/25/2025) — stamp local today before save
        text = FixDraftDate(text);
        // Hard strip — models still emit tags even when the prompt forbids them
        text = StripBookPageTags(text);
        // Hard strip — models occasionally emit a Fountain page-break (===) right after
        // the title page; valid syntax, but nothing in the prompt asks for it here.
        text = StripFountainPageBreaks(text);
        // Belt-and-suspenders for the case above: at least once, the === was a straight
        // substitution for FADE IN: (not an addition alongside it), so stripping it left the
        // draft with no FADE IN: at all — observed in JungleBook's screenplay.fountain.
        text = EnsureFadeIn(text);
        if (!LooksLikeGoodFountain(text))
            throw new InvalidOperationException(
                "Could not build a usable screenplay from the book. Try again or import a .fountain file.");

        var stillVague = FindVagueLocationHeadings(text);
        if (stillVague.Count > 0)
        {
            onProgress?.Invoke(
                $"Warning: vague location heading(s) remain after repair: {string.Join("; ", stillVague.Take(3))}");
        }

        var stillGeneric = FindGenericNumberedSpeakers(text);
        if (stillGeneric.Count > 0)
        {
            onProgress?.Invoke(
                $"Warning: generic numbered speaker(s) remain: {string.Join(", ", stillGeneric.Take(5))}");
        }

        // Soft scene-count budget — warn only (Stage 2 clip cost), never block
        var analysis = BookTextAnalyzer.Analyze(bookText);
        var sceneCount = CountSceneHeadings(text);
        var softMax = SoftMaxSceneHeadings(analysis.BookKind);
        if (sceneCount > softMax)
        {
            onProgress?.Invoke(
                $"Note: {sceneCount} scene headings (soft target ≤{softMax} for {analysis.BookKind}) — " +
                "shot plan / clip count may be high. Consider merging same-location beats next pass.");
        }

        return ScreenplayService.NormalizeText(text);
    }

    /// <summary>
    /// Scene headings that use multi-place filler language (VARIOUS, MULTIPLE, …).
    /// Detected on raw heading text so "HOUSE - VARIOUS ROOMS" is caught before sanitize.
    /// </summary>
    public static IReadOnlyList<string> FindVagueLocationHeadings(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return Array.Empty<string>();

        return FountainParser.Parse(fountain).Elements
            .Where(e => e.Type == FountainParser.ElementType.SceneHeading)
            .Select(e => (e.Text ?? "").Trim())
            .Where(h => h.Length > 0 && HeadingContainsVagueLocationLanguage(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True if a scene heading line contains non-filmable multi-place filler.</summary>
    public static bool HeadingContainsVagueLocationLanguage(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading)) return false;
        return Regex.IsMatch(
            heading,
            @"\b(VARIOUS|MULTIPLE|SEVERAL|ELSEWHERE)\b"
            + @"|\bDIFFERENT\s+(ROOMS?|PLACES?|LOCATIONS?)\b"
            + @"|\b(AROUND|THROUGHOUT)\s+THE\s+(HOUSE|HOME|BUILDING)\b"
            + @"|\b(VARIOUS|MULTIPLE|SEVERAL)\s+(ROOMS?|PLACES?|LOCATIONS?|AREAS?)\b",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Runs one chat completion attempt, retrying once after a transient failure
    /// (network error, timeout). Repair calls are best-effort; a timeout should not
    /// permanently leave the original problem when a retry would likely succeed.
    /// Returns null only if both attempts fail. Does not retry cancellation.
    /// </summary>
    private static async Task<string?> CompleteWithOneRetryAsync(
        IChatClient chat,
        string system,
        string user,
        string model,
        double temperature,
        string mode,
        string retryLabel,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await chat.CompleteAsync(system, user, model, temperature, ct, mode: mode)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt == 1)
            {
                onProgress?.Invoke($"{retryLabel} call failed — retrying once…");
            }
        }
        return null;
    }

    /// <summary>
    /// One automatic rewrite pass when the draft still has vague multi-place headings.
    /// Generation path — do not require operator hand-edits.
    /// </summary>
    private static async Task<string> RepairVagueLocationHeadingsAsync(
        string system,
        string fountain,
        IChatClient chat,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var bad = FindVagueLocationHeadings(fountain);
        if (bad.Count == 0 || !chat.IsConfigured)
            return fountain;

        onProgress?.Invoke(
            $"Repairing {bad.Count} vague location heading(s) (must be concrete rooms)…");

        var listed = string.Join("\n", bad.Select(h => "  - " + h));
        var user = $"""
            LOCATION HEADING REPAIR (HARD)
            The Fountain draft below is almost ready, but these scene headings use vague
            multi-place language that cannot be filmed as a single location:

            {listed}

            Rules:
            - Return the COMPLETE Fountain screenplay again (not a patch list).
            - Rewrite ONLY those bad headings (and adjust Action if a heading is removed).
            - Every heading must name 1–2 concrete, filmable places a crew can light/dress.
            - Forbidden in headings: VARIOUS, VARIOUS ROOMS, MULTIPLE, MULTIPLE LOCATIONS,
              SEVERAL, SEVERAL ROOMS, ELSEWHERE, DIFFERENT ROOMS/PLACES/LOCATIONS,
              AROUND THE HOUSE, THROUGHOUT THE HOUSE.
            - Good replacements: INT. HALL AND SITTING ROOM - NIGHT, INT. STAIRS AND HALL - NIGHT.
              Or drop the heading and fold a brief walk into the Action of the following scene.
            - Do not change plot, cast tokens, or dialogue wording except as needed for heading fixes.
            - No markdown fences. Fountain only.

            --- BEGIN FOUNTAIN ---
            {fountain}
            --- END FOUNTAIN ---
            """;

        try
        {
            var raw = await CompleteWithOneRetryAsync(
                    chat, system, user, model, temperature: 0.1,
                    mode: ChatCallModes.BookToFountainLocationsRetry,
                    retryLabel: "Location repair", onProgress, ct)
                .ConfigureAwait(false);
            if (raw is null)
            {
                onProgress?.Invoke("Location repair failed twice — keeping prior draft.");
                return fountain;
            }

            var repaired = StripBookPageTags(StripFences(raw));
            if (!LooksLikeGoodFountain(repaired))
            {
                onProgress?.Invoke("Location repair unusable — keeping prior draft.");
                return fountain;
            }

            var remaining = FindVagueLocationHeadings(repaired);
            if (remaining.Count < bad.Count)
            {
                onProgress?.Invoke(
                    remaining.Count == 0
                        ? "Location headings repaired."
                        : $"Location repair partial — {remaining.Count} vague heading(s) left.");
                return repaired;
            }

            onProgress?.Invoke("Location repair did not clear vague headings — keeping prior draft.");
            return fountain;
        }
        catch (Exception)
        {
            onProgress?.Invoke("Location repair failed — keeping prior draft.");
            return fountain;
        }
    }

    /// <summary>
    /// Character cues that are ordinal/numbered role placeholders
    /// (FIRST OFFICER, SECOND MERCHANT, BUSINESSMAN 2) — unstable cast keys.
    /// </summary>
    public static IReadOnlyList<string> FindGenericNumberedSpeakers(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return Array.Empty<string>();

        return FountainParser.Parse(fountain).Elements
            .Where(e => e.Type == FountainParser.ElementType.Character)
            .Select(e => (e.Text ?? "").Trim())
            .Where(n => n.Length > 0 && IsGenericNumberedSpeaker(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True for ordinal/numbered role placeholders: FIRST BUSINESSMAN, SECOND MERCHANT,
    /// OFFICER 1, GUEST #2, MAN 3, etc. Named people (SCROOGE, OFFICER REYNOLDS) are false.
    /// </summary>
    public static bool IsGenericNumberedSpeaker(string? characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName)) return false;
        var n = Regex.Replace(characterName.Trim(), @"\s+", " ");

        // FIRST/SECOND/… + any role noun (OFFICER, BUSINESSMAN, MERCHANT, GUEST, …)
        if (Regex.IsMatch(
                n,
                @"^(FIRST|SECOND|THIRD|FOURTH|FIFTH|SIXTH|SEVENTH|EIGHTH|NINTH|TENTH|1ST|2ND|3RD|4TH|5TH)\s+\S+",
                RegexOptions.IgnoreCase))
            return true;

        // Role + number / #number (broad role list)
        if (Regex.IsMatch(
                n,
                @"^(OFFICER|POLICE|POLICEMAN|POLICE\s+OFFICER|GUARD|SOLDIER|DETECTIVE|AGENT|COP|DEPUTY|TROOPER|"
                + @"BUSINESSMAN|BUSINESS\s*MAN|MERCHANT|GENTLEMAN|GENTLEMEN|LADY|GUEST|SERVANT|CLERK|PORTER|"
                + @"WAITER|MAID|NURSE|DOCTOR|LAWYER|SAILOR|CREWMAN|SOLDIER|CITIZEN|MAN|WOMAN|BOY|GIRL|"
                + @"ATTENDANT|MESSENGER|COURIER|DRIVER|COACHMAN|FOOTMAN|BUTLER|COOK|WORKMAN|LABORER|"
                + @"VILLAGER|TOWNSMAN|SHOPKEEPER|CUSTOMER|PATIENT|PRISONER|INMATE|SOLDIER|SAILOR)\s*[#]?\s*\d+\b",
                RegexOptions.IgnoreCase))
            return true;

        // Role + ONE/TWO/THREE…
        if (Regex.IsMatch(
                n,
                @"^(OFFICER|POLICE|POLICE\s+OFFICER|GUARD|SOLDIER|DETECTIVE|AGENT|DEPUTY|BUSINESSMAN|"
                + @"MERCHANT|GENTLEMAN|GUEST|SERVANT|CLERK|MAN|WOMAN)\s+"
                + @"(ONE|TWO|THREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|TEN)\b",
                RegexOptions.IgnoreCase))
            return true;

        // Trailing digit on multi-word ALL-CAPS role: "STOCK EXCHANGE MAN 1"
        if (Regex.IsMatch(n, @"\b\d{1,2}$") &&
            Regex.IsMatch(n, @"\b(OFFICER|MERCHANT|BUSINESS|GENTLEMAN|GUEST|SERVANT|MAN|WOMAN|CLERK)\b",
                RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Soft scene-heading budget for operator warnings (not a hard fail).
    /// picture_book ≤20, short ≤22, novel ≤45 for a short-film cut.
    /// </summary>
    public static int SoftMaxSceneHeadings(string? bookKind) =>
        (bookKind ?? "").ToLowerInvariant() switch
        {
            "picture_book" => 20,
            "short" => 22,
            "novel" => 45,
            _ => 30,
        };

    /// <summary>
    /// Chat repair: replace generic numbered speakers with stable proper-name tokens.
    /// </summary>
    private static async Task<string> RepairGenericNumberedSpeakersAsync(
        string system,
        string fountain,
        IChatClient chat,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var bad = FindGenericNumberedSpeakers(fountain);
        if (bad.Count == 0 || !chat.IsConfigured)
            return fountain;

        onProgress?.Invoke(
            $"Naming {bad.Count} generic numbered speaker(s) (stable cast tokens)…");

        var listed = string.Join("\n", bad.Select(n => "  - " + n));
        var user = $"""
            SPEAKER NAMING REPAIR (HARD)
            The Fountain draft below uses generic numbered / ordinal role cues that make
            unstable cast keys for production (portraits, continuity, shot plans):

            {listed}

            Rules:
            - Return the COMPLETE Fountain screenplay again (not a patch list).
            - Replace EVERY occurrence of those cues (including CONT'D / V.O. / O.S. lines)
              with a proper ALL-CAPS name token. Examples:
                FIRST OFFICER → OFFICER REYNOLDS
                SECOND MERCHANT → MERCHANT HALES
                FIRST BUSINESSMAN → MR. TOPPER (or a period surname)
                MAN 2 / GUEST #3 → named people, not numbers
            - Invent period-appropriate given names or surnames if the book is silent.
            - Same person = same token every time. Distinct people = distinct tokens.
            - Do not leave FIRST/SECOND/THIRD, OFFICER 1, BUSINESSMAN 2, MERCHANT #1, etc.
            - Do not change plot, locations, or book-faithful dialogue wording except the cue names.
            - No markdown fences. Fountain only.

            --- BEGIN FOUNTAIN ---
            {fountain}
            --- END FOUNTAIN ---
            """;

        try
        {
            var raw = await CompleteWithOneRetryAsync(
                    chat, system, user, model, temperature: 0.15,
                    mode: ChatCallModes.BookToFountainSpeakersRetry,
                    retryLabel: "Speaker naming repair", onProgress, ct)
                .ConfigureAwait(false);
            if (raw is null)
            {
                onProgress?.Invoke("Speaker naming repair failed twice — keeping prior draft.");
                return fountain;
            }

            var repaired = StripBookPageTags(StripFences(raw));
            if (!LooksLikeGoodFountain(repaired))
            {
                onProgress?.Invoke("Speaker naming repair unusable — keeping prior draft.");
                return fountain;
            }

            var remaining = FindGenericNumberedSpeakers(repaired);
            if (remaining.Count < bad.Count)
            {
                onProgress?.Invoke(
                    remaining.Count == 0
                        ? "Generic speakers named."
                        : $"Speaker naming partial — {remaining.Count} generic cue(s) left.");
                return repaired;
            }

            onProgress?.Invoke("Speaker naming did not clear generic cues — keeping prior draft.");
            return fountain;
        }
        catch (Exception)
        {
            onProgress?.Invoke("Speaker naming repair failed — keeping prior draft.");
            return fountain;
        }
    }

    /// <summary>
    /// Deterministic: when two scene headings name the same place with drifted wording
    /// (e.g. "OLD HOUSE - HALL OUTSIDE CHAMBER" vs "HALL OUTSIDE CHAMBER"), rewrite
    /// all visits to one canonical location phrase so location_seed_tokens stay unified.
    /// Public for tests and SaveDraft.
    /// </summary>
    public static string NormalizeSceneHeadingWording(string? fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain))
            return fountain ?? "";

        fountain = fountain.Replace("\r\n", "\n").Replace('\r', '\n');
        var headings = FountainParser.Parse(fountain).Elements
            .Where(e => e.Type == FountainParser.ElementType.SceneHeading)
            .Select(e => (e.Text ?? "").Trim())
            .Where(h => h.Length > 0)
            .ToList();
        if (headings.Count < 2)
            return fountain;

        // Unique heading forms + frequency
        var forms = headings
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var freq = headings
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.First(), g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // locName per heading form
        var locByHeading = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in forms)
        {
            var (_, loc, _) = FountainStage1Importer.ParseHeading(h);
            locByHeading[h] = loc;
        }

        var locNames = locByHeading.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Alias longer "PREFIX - CORE" names to shorter CORE when CORE is also used
        var canonicalLoc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in locNames)
            canonicalLoc[loc] = loc;

        foreach (var longer in locNames.OrderByDescending(l => l.Length))
        {
            foreach (var shorter in locNames
                         .Where(s => s.Length < longer.Length)
                         .OrderByDescending(s => s.Length))
            {
                if (!IsLocationNameAlias(longer, shorter)) continue;
                // Prefer shorter core as canonical (stable key, less drift)
                var root = canonicalLoc.TryGetValue(shorter, out var c) ? c : shorter;
                canonicalLoc[longer] = root;
                break;
            }
        }

        // Only rewrite if at least one alias collapsed
        if (!canonicalLoc.Any(kv =>
                !kv.Key.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)))
            return fountain;

        // Preferred loc phrase = canonical; rebuild each heading with original time-of-day
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in forms)
        {
            var loc = locByHeading[h];
            var canon = canonicalLoc.TryGetValue(loc, out var c) ? c : loc;
            if (loc.Equals(canon, StringComparison.OrdinalIgnoreCase))
            {
                map[h] = h;
                continue;
            }

            var (prefix, _, time) = SplitSceneHeadingParts(h);
            var rebuilt = string.IsNullOrEmpty(time)
                ? $"{prefix}{canon}"
                : $"{prefix}{canon} - {time}";
            map[h] = rebuilt;
        }

        // Prefer most frequent original form's casing for the same rebuilt target? use as-is
        var lines = fountain.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd('\r');
            var key = t.Trim();
            if (map.TryGetValue(key, out var repl) &&
                !key.Equals(repl, StringComparison.Ordinal))
            {
                // preserve leading whitespace if any
                var lead = t.Length - t.TrimStart().Length;
                lines[i] = (lead > 0 ? t[..lead] : "") + repl;
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// True when <paramref name="longer"/> is the same place as <paramref name="shorter"/>
    /// with a redundant building/site prefix (e.g. "OLD HOUSE - HALL…" vs "HALL…").
    /// </summary>
    public static bool IsLocationNameAlias(string longer, string shorter)
    {
        longer = (longer ?? "").Trim();
        shorter = (shorter ?? "").Trim();
        if (longer.Length <= shorter.Length || shorter.Length < 4)
            return false;
        if (!longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase))
            return false;
        var prefix = longer[..^shorter.Length];
        return prefix.EndsWith(" - ", StringComparison.Ordinal)
               || prefix.EndsWith(" – ", StringComparison.Ordinal);
    }

    private static (string Prefix, string LocName, string Time) SplitSceneHeadingParts(string heading)
    {
        heading = (heading ?? "").Trim();
        var m = Regex.Match(
            heading,
            @"^(INT\./EXT|INT/EXT|I\./E|I/E|INT\.?|EXT\.?|EST\.?)\s*",
            RegexOptions.IgnoreCase);
        var prefix = m.Success ? m.Value : "INT. ";
        if (!prefix.EndsWith(' ') && prefix.Length > 0)
            prefix += " ";
        var rest = m.Success ? heading[m.Length..].Trim() : heading;
        var dash = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0) dash = rest.LastIndexOf(" – ", StringComparison.Ordinal);
        if (dash > 0)
            return (prefix, rest[..dash].Trim(), rest[(dash + 3)..].Trim());
        return (prefix, rest, "");
    }

    /// <summary>
    /// Overwrite any existing Draft date: line with today's local date (M/d/yyyy).
    /// Call after <see cref="EnsureDraftDate"/> so a missing key is inserted first.
    /// </summary>
    public static string FixDraftDate(string? fountain)
    {
        if (string.IsNullOrEmpty(fountain)) return fountain ?? "";
        var today = DateTime.Now.ToString("M/d/yyyy");
        return Regex.Replace(
            fountain,
            @"(?im)^(Draft date:)\s*.*$",
            $"$1 {today}");
    }

    /// <summary>
    /// Resolve single-shot / chunk budgets for a chat model id.
    /// Catalog has no context-window fields yet — known Grok ids get large defaults.
    /// </summary>
    public static PromptBudget ResolvePromptBudget(string? modelId)
    {
        var id = string.IsNullOrWhiteSpace(modelId) ? "grok-4.5" : modelId.Trim();
        // ~3.2 chars/token heuristic; leave headroom for system + user scaffolding.
        // Phase 2: read MaxInputTokens from SupportedModelCatalog when present.
        var inputTokens = id.ToLowerInvariant() switch
        {
            "grok-4.5" or "grok-4" => 128_000,
            _ => 128_000,
        };

        var reserved = ReservedOverheadChars;
        var tokenDerivedBookMax = Math.Clamp(
            (int)(inputTokens * 3.2) - reserved,
            8_000,
            AbsoluteSingleShotCeiling);
        // Product default caps large windows; smaller models keep the tighter token-derived max.
        var bookMax = Math.Min(DefaultSingleShotBookMaxChars, tokenDerivedBookMax);

        var chunkSoft = Math.Clamp(
            Math.Min(DefaultChunkSoftMaxChars, Math.Max(4_000, bookMax / 2)),
            4_000,
            Math.Min(bookMax, 120_000));

        return new PromptBudget
        {
            ModelId = id,
            SingleShotBookMaxChars = bookMax,
            ChunkSoftMaxChars = chunkSoft,
            MaxChunks = MaxAdaptChunks,
            ReservedOverheadChars = reserved,
        };
    }

    /// <summary>
    /// Chunk count actually needed for this book at the budget's soft-max chunk size,
    /// floored at <paramref name="budget"/>.MaxChunks (usually <see cref="MaxAdaptChunks"/>)
    /// and capped at <see cref="AbsoluteMaxAdaptChunks"/> for cost/latency safety.
    /// Without this, ChunkBookForAdaptation silently packs everything past the flat chunk
    /// cap into the LAST chunk — e.g. an 838K-char book at a 40K soft-max and an 8-chunk
    /// cap produced 7 normal ~40K chunks and one ~660K-char final chunk, which measurably
    /// lost adaptation density (much lower response/prompt ratio) versus the earlier ones.
    /// </summary>
    public static int ResolveMaxChunks(string? bookText, PromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var normalized = NormalizeBookText(bookText ?? "");
        if (normalized.Length == 0)
            return budget.MaxChunks;

        var softMax = Math.Max(1, budget.ChunkSoftMaxChars);
        // Unit packing rarely fills soft-max exactly; size as if each pack holds ~85%.
        var effectiveCapacity = Math.Max(1, (int)(softMax * 0.85));
        var needed = (int)Math.Ceiling(normalized.Length / (double)effectiveCapacity);
        return Math.Clamp(Math.Max(budget.MaxChunks, needed), budget.MaxChunks, AbsoluteMaxAdaptChunks);
    }

    /// <summary>True when the full book fits one adapt call under <paramref name="budget"/>.</summary>
    public static bool FitsSingleShot(string bookText, PromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        bookText ??= "";
        return bookText.Length <= budget.SingleShotBookMaxChars;
    }

    /// <summary>
    /// Whether multi-chunk is worth attempting (book large enough and ≥2 chunks possible).
    /// </summary>
    public static bool ShouldChunkFallback(string bookText, PromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        bookText = NormalizeBookText(bookText ?? "");
        if (bookText.Length < MinBookCharsForChunkFallback)
            return false;

        var soft = Math.Min(budget.ChunkSoftMaxChars, Math.Max(MinBookCharsForChunkFallback, bookText.Length / 2));
        var chunks = ChunkBookForAdaptation(bookText, budget.MaxChunks, soft);
        return chunks.Count >= 2;
    }

    /// <summary>
    /// Structural + coverage gate. Soft failures fail single-shot (trigger chunk fallback);
    /// multi-chunk only hard-fails on structure / excerpt markers.
    /// </summary>
    public static QualityResult EvaluateQuality(
        string? fountain,
        string bookText,
        int totalRuntimeMinutes,
        AdaptPath path)
    {
        fountain = StripBookPageTags(fountain ?? "");
        bookText = NormalizeBookText(bookText ?? "");
        totalRuntimeMinutes = Math.Clamp(totalRuntimeMinutes, 1, 180);

        var fails = new List<string>();
        var scenes = CountSceneHeadings(fountain);

        if (!LooksLikeGoodFountain(fountain))
            fails.Add("structure");

        if (Regex.IsMatch(
                fountain,
                @"\[\[.*(truncat|omitted for length|cut off|excerpted).*\]\]",
                RegexOptions.IgnoreCase))
            fails.Add("excerpt_marker");

        // Soft: long books should resolve
        if (bookText.Length > 40_000 &&
            path == AdaptPath.Single &&
            fountain.Length >= 80 &&
            !Regex.IsMatch(fountain, @"(?im)(FADE OUT|THE END)\b"))
            fails.Add("missing_ending");

        var minScenes = Math.Clamp(totalRuntimeMinutes / 2, 3, 40);
        if (bookText.Length > 50_000)
            minScenes = Math.Max(minScenes, 8);
        // Short picture books: do not demand many scenes
        if (bookText.Length < 8_000)
            minScenes = Math.Min(minScenes, 2);
        if (scenes < minScenes && bookText.Length >= MinBookCharsForChunkFallback)
            fails.Add($"scene_count:{scenes}<{minScenes}");

        if (path == AdaptPath.Single &&
            bookText.Length > 60_000 &&
            fountain.Length < Math.Min(8_000, Math.Max(500, bookText.Length / 40)))
            fails.Add("suspiciously_short");

        var hard = fails.Contains("structure") || fails.Contains("excerpt_marker");
        var ok = path == AdaptPath.Multi
            ? !hard && LooksLikeGoodFountain(fountain)
            : fails.Count == 0;

        return new QualityResult
        {
            Ok = ok,
            Reason = fails.Count == 0 ? "ok" : string.Join(",", fails),
            SceneCount = scenes,
            FountainChars = fountain.Length,
            Failures = fails,
            HasHardFailure = hard,
        };
    }

    /// <summary>
    /// Remove operator-facing book page tags from Fountain
    /// (<c>= page N</c>, <c>[[page N]]</c>). Book linkage uses text/order match in the UI.
    /// </summary>
    public static string StripBookPageTags(string? fountain)
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

    /// <summary>
    /// Remove standalone Fountain page-break markers (a line of three or more `=`,
    /// optionally with a page number, e.g. <c>===</c> or <c>===13===</c>). Nothing in
    /// the prompt asks for these; the model still emits one after the title page on
    /// roughly a third of runs. Valid Fountain syntax, but an unrequested artifact here —
    /// stripped rather than banned-only-by-prompt, same reasoning as StripBookPageTags.
    /// </summary>
    public static string StripFountainPageBreaks(string? fountain)
    {
        if (string.IsNullOrEmpty(fountain)) return fountain ?? "";

        fountain = Regex.Replace(
            fountain,
            @"(?m)^[ \t]*={3,}[ \t]*(?:\d+[ \t]*=+[ \t]*)?$\r?\n?",
            "");

        fountain = Regex.Replace(fountain, @"\n{3,}", "\n\n");
        var trimmed = fountain.TrimEnd();
        return trimmed.Length == 0 ? "" : trimmed + "\n";
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
        maxChunks = Math.Clamp(maxChunks, 1, AbsoluteMaxAdaptChunks);
        softMaxChars = Math.Clamp(softMaxChars, 4_000, 120_000);

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
    public static string StitchFountainParts(IReadOnlyList<string>? parts)
    {
        if (parts is null || parts.Count == 0)
            return "";

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
        _ = requirePageTags; // unused; page tags are not part of the product gate
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

    /// <summary>
    /// Single-shot with structure retry + quality gate + optional coverage retry.
    /// Returns null when the draft is not acceptable and multi-chunk fallback should be considered.
    /// </summary>
    private static async Task<string?> TrySingleShotWithGateAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int totalMinutes,
        string bookText,
        IChatClient chat,
        string model,
        PromptBudget budget,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        try
        {
            var draft = await ConvertSingleShotAsync(
                system, title, author, pageCount, totalMinutes, bookText,
                chat, model, ct,
                bookMaxChars: budget.SingleShotBookMaxChars).ConfigureAwait(false);

            var gate = EvaluateQuality(draft, bookText, totalMinutes, AdaptPath.Single);
            if (gate.Ok)
                return draft;

            onProgress?.Invoke($"Single pass weak ({gate.Reason}) — retry…");
            draft = await ConvertSingleShotAsync(
                system, title, author, pageCount, totalMinutes, bookText,
                chat, model, ct,
                bookMaxChars: budget.SingleShotBookMaxChars,
                extraUserSuffix: CoverageRetrySuffix()).ConfigureAwait(false);

            gate = EvaluateQuality(draft, bookText, totalMinutes, AdaptPath.Single);
            return gate.Ok ? draft : null;
        }
        catch (InvalidOperationException)
        {
            // Structure failed after retries, or transport/API wrapped as InvalidOperationException
            return null;
        }
    }

    private static async Task<string> ConvertSingleShotAsync(
        string system,
        string title,
        string? author,
        int pageCount,
        int totalMinutes,
        string bookText,
        IChatClient chat,
        string model,
        CancellationToken ct,
        int bookMaxChars = DefaultSingleShotBookMaxChars,
        string? extraUserSuffix = null)
    {
        // Happy path: full book. Trim only if somehow over the call budget (prefer multi-chunk instead).
        var bookForPrompt = bookText.Length <= bookMaxChars
            ? bookText
            : TrimBookForPrompt(bookText, bookMaxChars);
        var user = BuildUserPrompt(title, author, pageCount, totalMinutes, bookForPrompt, chunkIndex: 0, chunkTotal: 1);
        if (!string.IsNullOrEmpty(extraUserSuffix))
            user += extraUserSuffix;

        var firstMode = string.IsNullOrEmpty(extraUserSuffix)
            ? ChatCallModes.BookToFountain
            : ChatCallModes.BookToFountainCoverage;
        var text = await CompleteWithOneRetryAsync(
                chat, system, user, model, temperature: 0.2,
                mode: firstMode,
                retryLabel: "Book adapt",
                onProgress: null,
                ct)
            .ConfigureAwait(false);
        if (text is null)
            throw new InvalidOperationException(
                "Book adapt timed out or failed after retry. Try again or import a .fountain file.");
        text = StripBookPageTags(StripFences(text));

        if (!LooksLikeGoodFountain(text))
        {
            var retryUser = user + RetrySuffix(hasPageMarkers: false);
            var retryText = await CompleteWithOneRetryAsync(
                    chat, system, retryUser, model, temperature: 0.15,
                    mode: ChatCallModes.BookToFountainRetry,
                    retryLabel: "Book adapt structure",
                    onProgress: null,
                    ct)
                .ConfigureAwait(false);
            if (retryText is not null)
                text = StripBookPageTags(StripFences(retryText));
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
        IChatClient chat,
        string model,
        Action<string>? onProgress,
        CancellationToken ct,
        int softMaxChars = ChunkSoftMaxChars,
        int maxChunks = MaxAdaptChunks)
    {
        var chunks = ChunkBookForAdaptation(bookText, maxChunks, softMaxChars);
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

            // One transport retry on timeout/cancel (chunk calls can exceed short proxies)
            var part = await CompleteWithOneRetryAsync(
                    chat, system, user, model, temperature: 0.2,
                    mode: ChatCallModes.BookToFountainChunk,
                    retryLabel: $"Chunk {i + 1}/{chunks.Count}",
                    onProgress, ct)
                .ConfigureAwait(false);
            if (part is null)
                throw new InvalidOperationException(
                    $"Book adapt chunk {i + 1}/{chunks.Count} failed after retry (timeout or network). Try again.");
            part = StripBookPageTags(StripFences(part));

            if (!LooksLikeGoodFountain(part) && part.Length < 80)
            {
                var retryPart = await CompleteWithOneRetryAsync(
                        chat, system, user + RetrySuffix(false), model, temperature: 0.15,
                        mode: ChatCallModes.BookToFountainChunkRetry,
                        retryLabel: $"Chunk {i + 1} structure",
                        onProgress, ct)
                    .ConfigureAwait(false);
                if (retryPart is not null)
                    part = StripBookPageTags(StripFences(retryPart));
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
        IChatClient chat,
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

        var text = await chat.CompleteAsync(
                mergeSystem, sb.ToString(), model, temperature: 0.15, ct,
                mode: ChatCallModes.BookToFountainMerge)
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

    private static string CoverageRetrySuffix() => """


        IMPORTANT: Previous draft was too short or incomplete for the full book.
        Re-output a complete Fountain screenplay covering the full arc present in BOOK_TEXT.
        - Include enough INT./EXT. scenes for the target runtime.
        - Carry the story through resolution; end with FADE OUT / THE END.
        - Do not stop after the opening chapters only.
        - Do not emit = page N or [[page N]] tags.
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
    /// Insert <c>FADE IN:</c> before the first scene heading if the model omitted it.
    /// Idempotent — no-op if a FADE IN: line already exists anywhere in the draft. Needed
    /// because StripFountainPageBreaks only removes an unwanted === marker; if that === was
    /// a straight substitution for FADE IN: rather than an addition alongside it, stripping
    /// it leaves nothing behind (observed in JungleBook's screenplay.fountain). Public so
    /// tests can exercise it directly, same as StripFountainPageBreaks.
    /// </summary>
    public static string EnsureFadeIn(string text)
    {
        if (Regex.IsMatch(text, @"(?im)^FADE IN\s*:"))
            return text;
        var m = Regex.Match(text, @"(?im)^(INT\.|EXT\.|INT\./EXT\.|I/E\.|EST\.)");
        return m.Success ? text.Insert(m.Index, "FADE IN:\n\n") : text;
    }

    /// <summary>
    /// Fit oversize books into a single-shot window (start/middle/end).
    /// Prefer multi-chunk when the book exceeds the model budget; this is a last-resort trim.
    /// </summary>
    private static string TrimBookForPrompt(string bookText, int maxChars = DefaultSingleShotBookMaxChars)
    {
        bookText = NormalizeBookText(bookText);
        var max = Math.Clamp(maxChars, 4_000, AbsoluteSingleShotCeiling);
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
