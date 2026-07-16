using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>
/// Keeps character visual fields filmable: strip book nicknames / epithet compounds
/// that image models misread as props (e.g. "noodle-head hat").
/// Replacements stay generic — never inject story-specific anti-patterns into Stage 1 output.
/// </summary>
public static class CharacterVisualTextScrubber
{
    // Book epithet "X-head" used as personality (not a literal head prop)
    private static readonly Regex NicknameHead =
        new(
            @"\b(?:silly|goofy|lovable|classic|signature)?\s*noodle[-\s]?heads?\b" +
            @"|\bnoodle[-\s]?head(?:ed)?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NoodleHeadHat =
        new(
            @"\b(?:signature\s+)?(?:silly\s+)?(?:goofy\s+)?noodle[-\s]?head\s+hat\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NoodleHeadExpression =
        new(
            @"\b(?:slightly\s+)?(?:goofy\s+|silly\s+)?noodle[-\s]?head\s+expression\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NoodleHeadDog =
        new(
            @"\b(?:the\s+)?noodle[-\s]?head\s+dog\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Leftover over-specific anti-pattern phrases we never want in seeds
    private static readonly Regex PastaAntiPattern =
        new(
            @"\s*[;(,]?\s*never\s+pasta(?:/food)?(?:\s+hat)?s?\b|;\s*never\s+pasta(?:/food)?(?:\s+hat)?s?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Scrub a description / visual_lock string. Applied only to visual seed fields —
    /// not dialogue or titles.
    /// </summary>
    public static string ScrubVisualProse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var t = text;

        // Concrete nickname → filmable generic (no anti-pasta boilerplate)
        t = NoodleHeadHat.Replace(t, "signature hat as shown in book art");
        t = NoodleHeadExpression.Replace(t, "sweet slightly goofy expression");
        t = NoodleHeadDog.Replace(t, "dog");
        t = NicknameHead.Replace(t, "");

        // Generic silly/goofy "object-head hat/expression" epithets
        t = Regex.Replace(
            t,
            @"\b(?:silly|goofy|lovable|signature)\s+(\w+)[-\s]head\s+(hat|expression)\b",
            m =>
            {
                var kind = m.Groups[2].Value.ToLowerInvariant();
                return kind == "hat"
                    ? "signature hat as shown in book art"
                    : "sweet slightly goofy expression";
            },
            RegexOptions.IgnoreCase);

        // Strip any previously injected story-specific anti-patterns
        t = PastaAntiPattern.Replace(t, "");
        t = Regex.Replace(t, @"\bnever pasta(?:/food)?(?:\s+hat)?s?\b", "", RegexOptions.IgnoreCase);

        // Clean punctuation / whitespace debris from removals
        t = Regex.Replace(t, @"\s{2,}", " ");
        t = Regex.Replace(t, @"\s+([,.;:])", "$1");
        t = Regex.Replace(t, @"([,.;:]){2,}", "$1");
        t = Regex.Replace(t, @"\(\s*\)", "");
        t = Regex.Replace(t, @"\s+—\s*—", " — ");
        t = t.Trim(' ', ',', ';', '-', '—');
        return t.Trim();
    }

    /// <summary>
    /// Wardrobe list entries: rewrite nickname-only hat lines; drop empty leftovers.
    /// </summary>
    public static List<string> ScrubWardrobeList(IEnumerable<string>? items)
    {
        var outList = new List<string>();
        if (items is null) return outList;
        foreach (var raw in items)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            if (NoodleHeadHat.IsMatch(s) ||
                (s.Contains("noodle", StringComparison.OrdinalIgnoreCase) &&
                 s.Contains("hat", StringComparison.OrdinalIgnoreCase)))
            {
                s = "signature hat as shown in book art";
            }
            else if (NicknameHead.IsMatch(s) ||
                     s.Contains("noodle-head", StringComparison.OrdinalIgnoreCase) ||
                     s.Contains("noodle head", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else
            {
                s = ScrubVisualProse(s);
            }

            if (string.IsNullOrWhiteSpace(s)) continue;
            if (s.Contains("pasta", StringComparison.OrdinalIgnoreCase)) continue;
            if (!outList.Contains(s, StringComparer.OrdinalIgnoreCase))
                outList.Add(s);
        }
        return outList;
    }

    public static bool LooksLikeNicknameVisualJunk(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("noodle-head", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("noodle head", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("noodlehead", StringComparison.OrdinalIgnoreCase));
}
