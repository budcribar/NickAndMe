using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>
/// Keeps character visual fields filmable: strip book nicknames / food metaphors
/// that image models misread as props (e.g. "noodle-head hat" → pasta).
/// Used by Stage 1 normalizer and portrait prompts.
/// </summary>
public static class CharacterVisualTextScrubber
{
    // Book epithet "X-head" used as personality (not a literal head prop)
    private static readonly Regex NicknameHead =
        new(
            @"\b(?:silly|goofy|lovable|classic|signature)?\s*noodle[-\s]?heads?\b" +
            @"|\bnoodle[-\s]?head(?:ed)?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "noodle-head hat" / "noodle head dog" compounds
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

    /// <summary>
    /// Scrub a description / visual_lock string. Leaves book titles in other fields alone
    /// when this is only applied to seed visual prose.
    /// </summary>
    public static string ScrubVisualProse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var t = text;

        // Concrete replacements first (most specific)
        t = NoodleHeadHat.Replace(t, "signature nightcap (as in book art; never pasta/food)");
        t = NoodleHeadExpression.Replace(t, "sweet slightly goofy expression");
        t = NoodleHeadDog.Replace(t, "dog");

        // Remaining epithet uses
        t = NicknameHead.Replace(t, "");

        // Generic: "food/object-head" epithets used as adjectives (carrot-head, potato-head style)
        // only when clearly an epithet before hat/expression/dog — not "arrowhead" architecture
        t = Regex.Replace(
            t,
            @"\b(?:silly|goofy|lovable|signature)\s+(\w+)[-\s]head\s+(hat|expression)\b",
            m =>
            {
                var kind = m.Groups[2].Value.ToLowerInvariant();
                return kind == "hat"
                    ? "signature hat (as in book art)"
                    : "sweet slightly goofy expression";
            },
            RegexOptions.IgnoreCase);

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
                s = "signature nightcap as in book art (never pasta/food)";
            }
            else if (NicknameHead.IsMatch(s) ||
                     s.Contains("noodle-head", StringComparison.OrdinalIgnoreCase) ||
                     s.Contains("noodle head", StringComparison.OrdinalIgnoreCase))
            {
                // Epithet-only wardrobe line is not filmable
                continue;
            }
            else
            {
                s = ScrubVisualProse(s);
            }

            if (string.IsNullOrWhiteSpace(s)) continue;
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
