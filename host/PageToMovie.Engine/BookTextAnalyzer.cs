using System.Text.RegularExpressions;

namespace PageToMovie.Engine;

/// <summary>Port of extract_book_source.analyze_book_text — quality + Stage 1 defaults.</summary>
public static class BookTextAnalyzer
{
    private static readonly Regex PageMarker = new(
        @"---\s*PAGE\s+(\d+)\s*---",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WeirdChars = new(
        @"[^\w\s'.,!?;:\-""()…°]",
        RegexOptions.Compiled);

    private static readonly Regex BadTokens = new(
        @"\b\w*[0-9]\w*\b",
        RegexOptions.Compiled);

    private static readonly Regex GarbleHits = new(
        @"\b(?:[A-Za-z]*[0-9][A-Za-z0-9]*|[A-Za-z]{1,2}[;:][A-Za-z]{2,})\b",
        RegexOptions.Compiled);

    public static BookTextAnalysis Analyze(string text, int? pagesHint = null)
    {
        text ??= "";
        var bodies = PageBodies(text);
        var pages = pagesHint is > 0 ? pagesHint.Value : (bodies.Count > 0 ? bodies.Count : 1);
        if (bodies.Count == 0 && !string.IsNullOrWhiteSpace(text))
            bodies = new List<string> { text.Trim() };

        var contentBodies = bodies.Where(b => !IsIllustrationOnly(b)).ToList();
        var plain = PageMarker.Replace(text ?? "", " ");
        plain = Regex.Replace(plain, @"\(\s*illustration only\s*\)", " ", RegexOptions.IgnoreCase);
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        var chars = plain.Length;
        var words = string.IsNullOrEmpty(plain) ? 0 : plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var letters = plain.Count(char.IsLetter);
        var letterRatio = chars > 0 ? letters / (double)chars : 0.0;

        var emptyPages = bodies.Count(b => IsIllustrationOnly(b) || b.Length < 20);
        var sparsePages = bodies.Count(b => b.Length < 120);
        var emptyRatio = emptyPages / (double)Math.Max(bodies.Count, 1);
        var sparseRatio = sparsePages / (double)Math.Max(bodies.Count, 1);
        var avgChars = chars / (double)Math.Max(pages, 1);

        var garbage = 0.0;
        var wordList = string.IsNullOrEmpty(plain)
            ? Array.Empty<string>()
            : plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (chars > 40)
        {
            // CA1875: Regex.Count avoids allocating MatchCollection
            var weird = WeirdChars.Count(plain);
            garbage += Math.Min(1.0, weird / (double)Math.Max(chars, 1) * 10);
            if (letterRatio < 0.55) garbage += 0.35;
            if (letterRatio < 0.4) garbage += 0.35;
            var badTokens = BadTokens.Count(plain);
            garbage += Math.Min(0.35, badTokens / (double)Math.Max(words, 1));
            var garbleHits = GarbleHits.Count(plain);
            garbage += Math.Min(0.3, garbleHits / (double)Math.Max(words, 1) * 2);
            // OCR soup: low vowels in longer tokens
            if (wordList.Length > 8)
            {
                // CA1827: Any() would early-out for existence checks; here we need a count ratio
                var shortJunk = 0;
                foreach (var w in wordList)
                {
                    if (w.Length is < 4 or > 12) continue;
                    var hasVowel = false;
                    foreach (var c in w)
                    {
                        if ("aeiouAEIOU".Contains(c)) { hasVowel = true; break; }
                    }
                    if (!hasVowel) shortJunk++;
                }
                garbage += Math.Min(0.35, shortJunk / (double)wordList.Length);
            }
        }

        garbage = Math.Clamp(garbage, 0, 1.5);

        string quality;
        if (words < 8 && contentBodies.Count == 0)
            quality = "empty";
        else if (garbage >= 0.45 || letterRatio < 0.4)
            quality = "poor";
        else if (words < 40 && sparseRatio > 0.6)
            quality = "good"; // picture book clean short text
        else if (letterRatio >= 0.55 && garbage < 0.35)
            quality = "good";
        else
            quality = "poor";

        var density = sparseRatio > 0.45 || avgChars < 200 ? "sparse" : "normal";
        var bookKind = pages <= 40 && (density == "sparse" || words < 800)
            ? "picture_book"
            : words < 15000 ? "short" : "novel";

        var suggestedMinutes = bookKind switch
        {
            "picture_book" => Math.Clamp(Math.Max(5, pages / 2), 3, 25),
            "short" => Math.Clamp(words / 120, 8, 45),
            _ => Math.Clamp(words / 150, 30, 180),
        };
        var suggestedChunks = bookKind == "picture_book"
            ? Math.Clamp(pages, 5, 20)
            : 10;

        var notes = new List<string>();
        if (density == "sparse")
            notes.Add("Layout is illustration-heavy (normal for picture books) but wording may still be usable.");
        if (bookKind == "picture_book")
            notes.Add($"Treated as picture book (~{pages} pages). Suggested Stage 1 runtime {suggestedMinutes} min.");
        if (quality == "poor")
            notes.Add("Text looks garbled (OCR noise). Prefer Grok vision on page images.");
        if (quality == "empty")
            notes.Add("Almost no readable text. Use Grok vision or paste a transcript.");

        return new BookTextAnalysis
        {
            Pages = pages,
            TextChars = chars,
            TextWords = words,
            LetterRatio = Math.Round(letterRatio, 3),
            EmptyPageRatio = Math.Round(emptyRatio, 3),
            SparsePageRatio = Math.Round(sparseRatio, 3),
            AvgCharsPerPage = Math.Round(avgChars, 1),
            GarbageScore = Math.Round(garbage, 3),
            TextQuality = quality,
            TextDensity = density,
            BookKind = bookKind,
            ReadyForStage1 = quality == "good" && garbage < 0.45,
            SuggestedTotalMinutes = suggestedMinutes,
            SuggestedChunkPages = suggestedChunks,
            Notes = notes,
        };
    }

    /// <summary>
    /// Page bodies for density/quality heuristics. Same split rules as
    /// <see cref="BookContextService.ParseBookPages"/>: <c>--- PAGE N ---</c> markers
    /// when present, otherwise paragraph-based synthetic pages (plain .txt).
    /// </summary>
    public static List<string> PageBodies(string text) =>
        BookContextService.ParseBookPages(text ?? "")
            .Select(p => p.Text ?? "")
            .ToList();

    public static bool IsIllustrationOnly(string body)
    {
        var b = (body ?? "").Trim().ToLowerInvariant();
        if (b.Length == 0) return true;
        if (b is "(illustration only)" or "illustration only" or "[illustration only]")
            return true;
        return Regex.IsMatch(b, @"^\(.*illustration.*\)$");
    }
}

public sealed class BookTextAnalysis
{
    public int Pages { get; set; }
    public int TextChars { get; set; }
    public int TextWords { get; set; }
    public double LetterRatio { get; set; }
    public double EmptyPageRatio { get; set; }
    public double SparsePageRatio { get; set; }
    public double AvgCharsPerPage { get; set; }
    public double GarbageScore { get; set; }
    public string TextQuality { get; set; } = "unknown";
    public string TextDensity { get; set; } = "normal";
    public string BookKind { get; set; } = "unknown";
    public bool ReadyForStage1 { get; set; }
    public int SuggestedTotalMinutes { get; set; }
    public int SuggestedChunkPages { get; set; }
    public List<string> Notes { get; set; } = new();
    public string? TextEngine { get; set; }
}
