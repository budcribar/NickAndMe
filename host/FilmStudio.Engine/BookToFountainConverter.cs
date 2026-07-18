using System.Text;
using System.Text.RegularExpressions;
using FilmStudio.Engine.Abstractions;

namespace FilmStudio.Engine;

/// <summary>
/// Book text → editable Fountain via chat.
/// Uses <c>prompts/book_to_fountain.txt</c>, which carries the key narrative learnings
/// from the old Stage 1 scene-bible prompt (fidelity, closed cast, locations, spoilers,
/// wardrobe consistency) without requiring JSON as the operator artifact.
/// </summary>
public static class BookToFountainConverter
{
    /// <summary>
    /// Fallback body if <c>prompts/book_to_fountain.txt</c> is missing (tests / broken workspace).
    /// Prefer the file on disk — that is the maintained product prompt.
    /// </summary>
    public const string FountainOutputOverride = """
        Act as an expert screenwriter. Adapt the book into Fountain 1.1 only (no JSON).
        Target runtime about {{TOTAL_RUNTIME_MINUTES}} minutes. Real INT./EXT. locations.
        After every scene heading: = page N and [[page N]]. NARRATOR for narration;
        CHARACTER cues for speech. Closed cast. VO↔visual fidelity. No major invented plot.
        """;

    /// <summary>
    /// Generate Fountain from prepared book text using the book_to_fountain prompt pack.
    /// Requires a configured chat client.
    /// </summary>
    public static async Task<string> ConvertAsync(
        string workspaceRoot,
        string title,
        string bookText,
        string? author = null,
        int totalRuntimeMinutes = 10,
        IGrokChatClient? chat = null,
        string model = "grok-4.5",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bookText))
            throw new InvalidOperationException("Book text is empty");

        if (chat is null || !chat.IsConfigured)
            throw new InvalidOperationException(
                "Connect service to build a screenplay draft from the book.");

        var system = await BuildSystemPromptAsync(workspaceRoot, totalRuntimeMinutes, ct)
            .ConfigureAwait(false);

        var pages = BookContextService.ParseBookPages(bookText);
        var bookForPrompt = TrimBookForPrompt(bookText);
        var user = BuildUserPrompt(title, author, pages.Count, totalRuntimeMinutes, bookForPrompt);

        var text = await chat.CompleteAsync(system, user, model, temperature: 0.2, ct)
            .ConfigureAwait(false);
        text = StripFences(text);

        if (!LooksLikeGoodFountain(text))
        {
            var retryUser = user + """


                IMPORTANT: Previous output was not valid Fountain for our pipeline.
                Re-output the FULL Fountain screenplay only.
                - Every scene: INT./EXT. real location (not STORY, not PAGE in the heading).
                - After every scene heading:
                  = page N
                  [[page N]]
                - Use NARRATOR and CHARACTER dialogue where the book has narration or speech.
                """;
            text = await chat.CompleteAsync(system, retryUser, model, temperature: 0.15, ct)
                .ConfigureAwait(false);
            text = StripFences(text);
        }

        if (!LooksLikeGoodFountain(text))
            throw new InvalidOperationException(
                "Could not build a usable screenplay from the book. Try again or import a .fountain file.");

        if (!Regex.IsMatch(text, @"(?im)^Draft date:"))
        {
            var m = Regex.Match(text, @"(?im)^Title:\s*.+$");
            if (m.Success)
            {
                text = text.Insert(
                    m.Index + m.Length,
                    $"\nDraft date: {DateTime.Now:M/d/yyyy}");
            }
        }

        return ScreenplayService.NormalizeText(text);
    }

    /// <summary>Load <c>prompts/book_to_fountain.txt</c> (Stage 1 learnings → Fountain).</summary>
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
    /// Minimal offline stub (tests / emergency). Production always uses chat + book_to_fountain prompt.
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
            sb.Append("INT. ROOM - DAY\n\n= page 1\n\n[[page 1]]\n\n[[No book text.]]\n");
            return ScreenplayService.NormalizeText(sb.ToString());
        }

        foreach (var page in pages)
        {
            var body = (page.Text ?? "").Trim();
            if (body.Length < 12) continue;
            if (Regex.IsMatch(body, @"^\(illustration", RegexOptions.IgnoreCase)) continue;

            sb.Append("INT. SCENE - DAY\n\n");
            sb.Append("= page ").Append(page.PageNumber).Append("\n\n");
            sb.Append("[[page ").Append(page.PageNumber).Append("]]\n\n");
            sb.Append("NARRATOR\n");
            var line = Regex.Replace(body, @"\s+", " ").Trim();
            if (line.Length > 400) line = line[..400] + "…";
            sb.Append(line).Append("\n\n");
        }

        return ScreenplayService.NormalizeText(sb.ToString());
    }

    public static bool LooksLikeGoodFountain(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 120) return false;

        var hasScene = Regex.IsMatch(text, @"(?im)^(INT|EXT|EST|I/E)[\./ ]");
        var hasPageTag =
            Regex.IsMatch(text, @"(?im)^=\s*pages?\s+\d+") ||
            Regex.IsMatch(text, @"\[\[\s*page\s+\d+\s*\]\]", RegexOptions.IgnoreCase);

        var dumpCount = Regex.Matches(text, @"(?im)^INT\.\s+STORY\s+-\s+PAGE\s+\d+").Count;
        if (dumpCount >= 2) return false;

        var realLoc = Regex.IsMatch(text, @"(?im)^(INT|EXT)\.\s+(?!SCENE\b)[A-Z]");
        return hasScene && hasPageTag && (realLoc || Regex.IsMatch(text, @"(?im)^NARRATOR\s*$"));
    }

    private static string BuildUserPrompt(
        string title,
        string? author,
        int pageCount,
        int totalMinutes,
        string bookForPrompt)
    {
        var lines = new List<string>
        {
            $"TOTAL_RUNTIME_MINUTES = {totalMinutes}",
            "",
            $"Project title hint: {title}",
            $"Author hint: {author ?? "(unknown — infer from book if present)"}",
            $"Book page count (approx): {pageCount}",
            "",
            "Write the Fountain screenplay only (see system prompt).",
            "Respect --- PAGE N --- markers for page tags on each scene.",
            "",
            "BOOK_TEXT:",
            bookForPrompt,
        };
        return string.Join("\n", lines);
    }

    private static string TrimBookForPrompt(string bookText)
    {
        bookText = bookText.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        const int max = 32_000;
        if (bookText.Length <= max) return bookText;
        return bookText[..max] + "\n\n[[Book text truncated for length — adapt what is above.]]\n";
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
