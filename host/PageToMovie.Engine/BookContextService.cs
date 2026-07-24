using System.Text;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Link screenplay scenes to prepared book text (source/book_full.txt),
/// especially after book → draft import (page markers, sequential pages).
/// </summary>
public static class BookContextService
{
    private static readonly Regex PageMarker = new(
        @"^---\s*PAGE\s+(\d+)\s*---\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex HeadingPage = new(
        @"\bPAGE\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SynopsisPage = new(
        @"(?im)^=\s*pages?\s+(\d+)(?:\s*[-–]\s*(\d+))?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex NotePage = new(
        @"\[\[\s*page\s+(\d+)\s*\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed class BookPage
    {
        public int PageNumber { get; init; }
        public string Text { get; init; } = "";
    }

    public sealed class BookContextResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public bool HasBook { get; init; }
        public int? PageNumber { get; init; }
        public string? Heading { get; init; }
        public int SceneIndex { get; init; }
        public string Excerpt { get; init; } = "";
        public string MatchReason { get; init; } = "";
        public int TotalPages { get; init; }
    }

    public static bool HasBookText(ProjectStore store, string projectId)
    {
        var path = Path.Combine(store.GetProjectDir(projectId), "source", "book_full.txt");
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    public static List<BookPage> ParseBookPages(string bookText)
    {
        bookText ??= "";
        bookText = bookText.Replace("\r\n", "\n").Replace('\r', '\n');
        var pages = new List<BookPage>();

        var matches = PageMarker.Matches(bookText);
        if (matches.Count > 0)
        {
            for (var i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var num = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var start = m.Index + m.Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : bookText.Length;
                var body = bookText[start..end].Trim();
                pages.Add(new BookPage { PageNumber = num, Text = body });
            }
            return pages;
        }

        // No page markers: split into ~paragraph chunks as synthetic pages
        var paras = Regex.Split(bookText.Trim(), @"\n\s*\n+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (paras.Count == 0 && bookText.Trim().Length > 0)
            paras.Add(bookText.Trim());

        for (var i = 0; i < paras.Count; i++)
            pages.Add(new BookPage { PageNumber = i + 1, Text = paras[i] });
        return pages;
    }

    /// <summary>
    /// Resolve book excerpt for a scene click.
    /// </summary>
    /// <param name="sceneIndex">1-based scene order in the draft (from scene list).</param>
    /// <param name="sceneHeading">Heading text without leading period.</param>
    /// <param name="sceneBody">Optional action/dialogue under the scene for fuzzy match.</param>
    public static BookContextResult GetContext(
        ProjectStore store,
        string projectId,
        int sceneIndex,
        string? sceneHeading,
        string? sceneBody = null)
    {
        var bookPath = Path.Combine(store.GetProjectDir(projectId), "source", "book_full.txt");
        if (!File.Exists(bookPath))
        {
            return new BookContextResult
            {
                Ok = true,
                HasBook = false,
                SceneIndex = sceneIndex,
                Heading = sceneHeading,
                MatchReason = "no_book",
                Excerpt = "",
                Error = null,
            };
        }

        var book = File.ReadAllText(bookPath);
        var pages = ParseBookPages(book);
        if (pages.Count == 0)
        {
            return new BookContextResult
            {
                Ok = true,
                HasBook = true,
                SceneIndex = sceneIndex,
                Heading = sceneHeading,
                TotalPages = 0,
                MatchReason = "empty_book",
                Excerpt = "",
            };
        }

        // 1) Page tags in scene body: = page N  or  [[page N]]
        if (!string.IsNullOrWhiteSpace(sceneBody))
        {
            var nm = NotePage.Match(sceneBody);
            if (nm.Success && int.TryParse(nm.Groups[1].Value, out var notePn))
            {
                var page = pages.FirstOrDefault(p => p.PageNumber == notePn);
                if (page is not null)
                    return Result(page, sceneHeading, sceneIndex, pages.Count, "note_page");
            }

            foreach (var line in sceneBody.Replace("\r\n", "\n").Split('\n'))
            {
                var sm = SynopsisPage.Match(line.Trim());
                if (sm.Success && int.TryParse(sm.Groups[1].Value, out var synPn))
                {
                    var page = pages.FirstOrDefault(p => p.PageNumber == synPn);
                    if (page is not null)
                        return Result(page, sceneHeading, sceneIndex, pages.Count, "synopsis_page");
                }
            }
        }

        // 2) Explicit PAGE n in heading
        if (!string.IsNullOrWhiteSpace(sceneHeading))
        {
            var hm = HeadingPage.Match(sceneHeading);
            if (hm.Success && int.TryParse(hm.Groups[1].Value, out var pn))
            {
                var page = pages.FirstOrDefault(p => p.PageNumber == pn) ??
                           pages.ElementAtOrDefault(Math.Clamp(pn - 1, 0, pages.Count - 1));
                if (page is not null)
                    return Result(page, sceneHeading, sceneIndex, pages.Count, "heading_page");
            }
        }

        // 3) Fuzzy match scene body to a book page
        if (!string.IsNullOrWhiteSpace(sceneBody) && sceneBody.Trim().Length >= 16)
        {
            var fuzzy = BestFuzzyPage(pages, sceneBody);
            if (fuzzy is not null && fuzzy.Score >= 2)
                return Result(fuzzy.Page, sceneHeading, sceneIndex, pages.Count, "text_match");
        }

        // 4) Sequential: scene i → page i
        if (sceneIndex >= 1 && sceneIndex <= pages.Count)
            return Result(pages[sceneIndex - 1], sceneHeading, sceneIndex, pages.Count, "scene_index");

        var fallback = pages[Math.Clamp(sceneIndex - 1, 0, pages.Count - 1)];
        return Result(fallback, sceneHeading, sceneIndex, pages.Count, "fallback");
    }

    private static BookContextResult Result(
        BookPage page,
        string? sceneHeading,
        int sceneIndex,
        int totalPages,
        string reason) =>
        new()
        {
            Ok = true,
            HasBook = true,
            PageNumber = page.PageNumber,
            Heading = sceneHeading,
            SceneIndex = sceneIndex,
            Excerpt = Truncate(page.Text, 6000),
            MatchReason = reason,
            TotalPages = totalPages,
        };

    /// <summary>
    /// Extract action/dialogue body for a scene starting at 1-based line in fountain text.
    /// </summary>
    public static string ExtractSceneBody(string fountainText, int sceneStartLine1Based)
    {
        if (string.IsNullOrEmpty(fountainText) || sceneStartLine1Based < 1)
            return "";
        var lines = fountainText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = sceneStartLine1Based - 1;
        if (start >= lines.Length) return "";

        var sceneStart = new Regex(
            @"^(INT\.?\/EXT\.?|INT\/EXT|I\.?\/E\.?|INT\.?|EXT\.?|EST\.?)(\s|\.|$)",
            RegexOptions.IgnoreCase);
        var sb = new StringBuilder();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                continue;
            }
            // Next scene heading
            if (sceneStart.IsMatch(t) ||
                (t.StartsWith('.') && t.Length > 1 && char.IsLetterOrDigit(t[1])))
                break;
            if (t.StartsWith('#')) break;
            sb.AppendLine(t);
            if (sb.Length > 1200) break;
        }
        return sb.ToString().Trim();
    }

    private sealed class FuzzyHit
    {
        public required BookPage Page { get; init; }
        public int Score { get; init; }
    }

    private static FuzzyHit? BestFuzzyPage(List<BookPage> pages, string sceneBody)
    {
        var tokens = Tokenize(sceneBody);
        if (tokens.Count == 0) return null;

        FuzzyHit? best = null;
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page.Text)) continue;
            var pageTokens = Tokenize(page.Text);
            var set = new HashSet<string>(pageTokens, StringComparer.OrdinalIgnoreCase);
            var score = tokens.Count(t => set.Contains(t));
            // Bonus for longer shared words
            score += tokens.Where(t => t.Length >= 6 && set.Contains(t)).Count();
            if (best is null || score > best.Score)
                best = new FuzzyHit { Page = page, Score = score };
        }
        return best;
    }

    private static List<string> Tokenize(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"[a-z]{3,}")
            .Select(m => m.Value)
            .Where(w => w is not ("the" or "and" or "for" or "with" or "that" or "this" or "from" or "have" or "was" or "are"))
            .Distinct()
            .Take(40)
            .ToList();
    }

    private static string Truncate(string s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        return s[..max].TrimEnd() + "…";
    }
}
