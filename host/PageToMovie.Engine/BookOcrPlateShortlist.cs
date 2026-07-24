using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PageToMovie.Engine;

/// <summary>
/// Plate candidates from existing book OCR (<c>source/book_full.txt</c>):
/// name hit on page N → prefer art neighbors N+1, N−1, N; never attach pure text pages;
/// stop at <paramref name="maxPlates"/> per character.
/// </summary>
public static class BookOcrPlateShortlist
{
    public const string BookFullFileName = "book_full.txt";

    public static string? FindBookFullPath(string projectDir)
    {
        var a = Path.Combine(projectDir, "source", BookFullFileName);
        if (File.Exists(a)) return a;
        var b = Path.Combine(projectDir, BookFullFileName);
        return File.Exists(b) ? b : null;
    }

    public static List<PageText> ParseBookFull(string raw)
    {
        var list = new List<PageText>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        var parts = Regex.Split(raw, @"---\s*PAGE\s+(\d+)\s*---", RegexOptions.IgnoreCase);
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out var page)) continue;
            list.Add(new PageText(page, parts[i + 1] ?? ""));
        }
        return list;
    }

    public static async Task<List<PageText>> TryLoadAsync(string projectDir, CancellationToken ct = default)
    {
        var path = FindBookFullPath(projectDir);
        if (path is null) return new List<PageText>();
        try
        {
            var raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return ParseBookFull(raw);
        }
        catch
        {
            return new List<PageText>();
        }
    }

    /// <summary>Aliases from cast seed key + display names (general, not title-specific).</summary>
    public static List<string> AliasesForSeed(string key, JsonObject? seed)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length < 3) return;
            set.Add(s.ToLowerInvariant());
            // multi-word: also last token if long enough (e.g. "Head Dog" skip; "Buster" ok)
            foreach (var part in Regex.Split(s, @"[\s_\-]+"))
                if (part.Length >= 4)
                    set.Add(part.ToLowerInvariant());
        }

        var suffix = key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
        Add(suffix);
        if (seed is not null)
        {
            Add(seed["canonical_given_name"]?.GetValue<string>());
            Add(seed["display_name"]?.GetValue<string>());
            // light species tokens from description for supporting animals
            var desc = seed["description"]?.GetValue<string>() ?? "";
            foreach (Match m in Regex.Matches(desc.ToLowerInvariant(),
                         @"\b(bunnies|bunny|rabbits?|mice|mouse|frogs?|owls?|kittens?|puppies?)\b"))
                set.Add(m.Value);
        }

        // Drop ultra-generic tokens that match too many pages
        set.Remove("character");
        set.Remove("dog"); // too broad for picture books ("doggy" kept if present as alias from text patterns)
        set.Remove("cat");
        set.Remove("boy");
        set.Remove("girl");
        set.Remove("man");
        set.Remove("woman");
        return set.OrderByDescending(a => a.Length).ToList();
    }

    public static List<int> FindTextHitPages(IReadOnlyList<PageText> pages, IReadOnlyList<string> aliases)
    {
        var hits = new List<int>();
        if (aliases.Count == 0) return hits;
        foreach (var p in pages)
        {
            var text = (p.Text ?? "").ToLowerInvariant()
                .Replace('\u2019', '\'').Replace('\u2018', '\'');
            foreach (var a in aliases)
            {
                if (a.Length < 3) continue;
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(a)}\b", RegexOptions.IgnoreCase))
                {
                    hits.Add(p.Page);
                    break;
                }
            }
        }
        return hits;
    }

    /// <summary>
    /// Art plate page numbers for this cast member (max <paramref name="maxPlates"/>).
    /// Name on text page N → prefer art at N+1, then N−1, then N.
    /// </summary>
    public static List<int> ShortlistArtPages(
        IReadOnlyList<PageText> pages,
        IReadOnlyList<string> aliases,
        int maxPlates = 3)
    {
        maxPlates = Math.Clamp(maxPlates, 1, 8);
        if (pages.Count == 0) return new List<int>();

        var byPage = pages.ToDictionary(p => p.Page);
        var maxPage = pages.Max(p => p.Page);
        var selected = new List<int>();
        var seen = new HashSet<int>();

        void TryAdd(int page)
        {
            if (page < 1 || page > maxPage) return;
            if (!byPage.TryGetValue(page, out var pt)) return;
            if (!IsArtPage(pt)) return;
            if (!seen.Add(page)) return;
            selected.Add(page);
        }

        foreach (var n in FindTextHitPages(pages, aliases))
        {
            foreach (var page in new[] { n + 1, n - 1, n })
                TryAdd(page);
            if (selected.Count >= maxPlates) break;
        }

        // Fallback: remaining art pages (cast never named in OCR)
        if (selected.Count < maxPlates)
        {
            foreach (var p in pages.OrderBy(x => x.Page))
            {
                if (!IsArtPage(p)) continue;
                TryAdd(p.Page);
                if (selected.Count >= maxPlates) break;
            }
        }

        return selected.Take(maxPlates).ToList();
    }

    public static bool IsArtPage(PageText p)
    {
        var t = (p.Text ?? "").Trim();
        if (t.Length == 0) return true;
        if (t.Contains("illustration only", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(t, @"^z+\s*$", RegexOptions.IgnoreCase)) return true;
        var words = Regex.Split(t.ToLowerInvariant(), @"\s+").Where(w => w.Length > 0).ToArray();
        if (p.Page == 1 && words.Length <= 25) return true;
        if (words.Length <= 8) return true;
        if (words.Length >= 12) return false;
        return p.Page % 2 == 1;
    }

    public sealed record PageText(int Page, string Text);
}
