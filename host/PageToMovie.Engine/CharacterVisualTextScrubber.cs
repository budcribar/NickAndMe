using System.Text.RegularExpressions;

namespace PageToMovie.Engine;

/// <summary>
/// Keeps character <em>visual</em> fields filmable across any book:
/// nicknames/epithets out of description/visual_lock/wardrobe; cross-species
/// "matching the hero animal's look" rewritten to shared <em>medium</em> language.
/// Does not inject story-specific anti-patterns (no food-hat boilerplate).
/// Dialogue/titles are out of scope — only visual seed fields.
/// </summary>
public static class CharacterVisualTextScrubber
{
    // "silly X-head hat" / "goofy X-head expression" — nickname compounds as props
    private static readonly Regex EpithetHeadProp = new(
        @"\b(?:silly|goofy|lovable|classic|signature|famous|beloved)?\s*" +
        @"[A-Za-z][A-Za-z0-9']{1,20}[-\s]heads?\s+(hat|cap|expression|look|grin)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare epithet "X-head" / "X head" / "'noodle head' look" (personality label, not anatomy)
    // Avoid pure anatomy words: arrowhead, bulkhead, spearhead, etc. when alone.
    private static readonly Regex EpithetHeadBare = new(
        @"\b(?:silly|goofy|lovable|signature|classic|soft|rounded|cute|sweet)?\s*" +
        @"['""]?(?!arrow|bulk|spear|war|bridge|dead|hot|cool)[A-Za-z]{3,16}['""]?\s*[-\s]\s*heads?['""]?" +
        @"(?:\s+(?:look|silhouette|expression|grin|face))?" +
        @"|\b(?!arrow|bulk|spear|war|bridge|dead|hot|cool)[A-Za-z]{3,16}[-\s]headed?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Title-style "the Noodle Head Dog" already covered by EpithetAnimalTitle; also
    // "soft noodle-head silhouette" without animal word.
    private static readonly Regex QuotedEpithetLook = new(
        @"['""][^'""]{0,24}head[^'""]{0,12}['""]\s*(?:look|silhouette|expression)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "the X-head dog/cat/bear" title-style animal nicknames inside visual prose
    private static readonly Regex EpithetAnimalTitle = new(
        @"\b(?:the\s+)?[A-Za-z][A-Za-z0-9']{1,16}[-\s]head\s+(dog|cat|bear|fox|rabbit|bunny|mouse|bird|horse|pig)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Cross-species style bleed: "matching the dog's CG look" / "same look as the fox hero"
    private static readonly Regex MatchingAnimalLook = new(
        @"\bmatching\s+(?:the\s+)?(?:\w+'s\s+)?(?:\w+\s+){0,3}?" +
        @"(?:dog|cat|bear|fox|rabbit|bunny|mouse|bird|horse|animal|hero)'?s?\s+" +
        @"(?:children'?s\s+)?(?:picture-book\s+)?(?:CG\s+)?(?:look|style|medium|render)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MatchingNamedHeroLook = new(
        @"\bmatching\s+[A-Z][A-Za-z0-9_]{1,24}'?s?\s+(?:CG\s+)?(?:look|style)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SameLookAsAnimal = new(
        @"\bsame\s+(?:CG\s+)?(?:look|style)\s+as\s+(?:the\s+)?(?:\w+\s+){0,2}?" +
        @"(?:dog|cat|bear|fox|animal|hero)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Shared render medium only — no species/age assumption.
    /// Use for animal-to-animal or unknown cast; do not force "human adult".
    /// </summary>
    public const string SharedFilmMediumPhrase =
        "same stylized picture-book soft-3D medium as the film";

    /// <summary>
    /// Medium phrase for known human cast when scrubbing "matching the animal's look"
    /// (keeps species from bleeding without rewriting animals as human).
    /// </summary>
    public const string SharedFilmMediumHumanDisambiguationPhrase =
        "same stylized picture-book soft-3D medium as the film (human — not an animal)";

    /// <summary>
    /// Scrub description / visual_lock prose for Stage 1 seeds and portrait prompts.
    /// </summary>
    /// <param name="disambiguateCrossSpeciesAsHuman">
    /// When true (caller knows this seed is human), cross-species "matching animal look"
    /// rewrites may add a human/not-animal disambiguation. Default false so animal seeds
    /// never get "human adult" injected.
    /// </param>
    public static string ScrubVisualProse(string? text, bool disambiguateCrossSpeciesAsHuman = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var t = text;

        // Nickname-as-prop → filmable generic (no book-specific replacement text)
        t = EpithetHeadProp.Replace(t, m =>
        {
            var kind = m.Groups[1].Value.ToLowerInvariant();
            return kind is "hat" or "cap"
                ? "signature hat as shown in book art"
                : "sweet slightly goofy expression";
        });

        t = EpithetAnimalTitle.Replace(t, m => m.Groups[1].Value.ToLowerInvariant()); // keep species word only
        t = QuotedEpithetLook.Replace(t, "");
        t = EpithetHeadBare.Replace(t, "");
        // Personality-as-face (not filmable anatomy)
        t = Regex.Replace(t, @"\bnot\s+very\s+bright\s+(?:expression|look|face)\b",
            "sweet slightly goofy expression", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\bnot\s+very\s+bright\b", "", RegexOptions.IgnoreCase);

        // Shared medium, not shared species
        t = SoftenCrossSpeciesStyleLanguage(t, disambiguateCrossSpeciesAsHuman);

        // Lightweight only. Figurative language + later-story wardrobe are scrubbed via
        // CastVisualLiteralizeService / prompts/cast_visual_literalize.txt (API prompt).
        // Do not grow book-specific regex lists here.

        return CleanDebris(t);
    }

    /// <summary>
    /// "Matching the {animal}'s look" → shared render medium (not shared species).
    /// Neutral by default so animal-to-animal style matching is not rewritten as human adult.
    /// </summary>
    /// <param name="disambiguateAsHuman">
    /// When true, use medium phrasing that also says human / not an animal (for known human seeds only).
    /// </param>
    public static string SoftenCrossSpeciesStyleLanguage(
        string? text,
        bool disambiguateAsHuman = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var t = text;
        var medium = disambiguateAsHuman
            ? SharedFilmMediumHumanDisambiguationPhrase
            : SharedFilmMediumPhrase;

        t = MatchingAnimalLook.Replace(t, medium);
        t = SameLookAsAnimal.Replace(t, medium);
        // Named hero ("matching Character_Hero's CG look") — same medium language
        t = MatchingNamedHeroLook.Replace(t, medium);

        return CleanDebris(t);
    }

    /// <summary>Wardrobe list: drop pure nicknames; rewrite nickname-hat lines generically.</summary>
    public static List<string> ScrubWardrobeList(IEnumerable<string>? items)
    {
        var outList = new List<string>();
        if (items is null) return outList;

        foreach (var raw in items)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();

            if (EpithetHeadProp.IsMatch(s) ||
                (Regex.IsMatch(s, @"[-\s]head\s+(hat|cap)\b", RegexOptions.IgnoreCase)))
            {
                s = "signature hat as shown in book art";
            }
            else if (EpithetHeadBare.IsMatch(s) || EpithetAnimalTitle.IsMatch(s))
            {
                continue; // pure nickname — not filmable wardrobe
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

    public static bool LooksLikeNicknameVisualJunk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return EpithetHeadProp.IsMatch(text) ||
               EpithetHeadBare.IsMatch(text) ||
               EpithetAnimalTitle.IsMatch(text);
    }

    /// <summary>
    /// True when the seed is primarily an animal of the given class — not a human whose
    /// text only mentions matching that animal's render medium.
    /// </summary>
    public static bool IsPrimarilyAnimalCharacter(
        string charKey,
        string ageBand,
        string description,
        string visualLock,
        string animalWord = "dog")
    {
        charKey ??= "";
        ageBand ??= "";
        description ??= "";
        visualLock ??= "";
        animalWord = string.IsNullOrWhiteSpace(animalWord) ? "dog" : animalWord.Trim();
        var animal = Regex.Escape(animalWord);
        if (ageBand.Contains(animalWord, StringComparison.OrdinalIgnoreCase) ||
            ageBand.Contains("animal", StringComparison.OrdinalIgnoreCase))
            return true;

        // Key is the animal name/token (Character_Buster is book-specific — only use explicit species tokens)
        if (charKey.Contains(animalWord, StringComparison.OrdinalIgnoreCase))
            return true;

        var blob = $"{description} {visualLock}";
        // Style-only mentions of the animal
        if (MatchingAnimalLook.IsMatch(blob) || SameLookAsAnimal.IsMatch(blob) || MatchingNamedHeroLook.IsMatch(blob))
            return false;
        if (Regex.IsMatch(
                blob,
                @"\b(adult\s+)?(man|woman|human|mother|father|mom|dad|parent|boy|girl|child)\b",
                RegexOptions.IgnoreCase))
            return false;

        return Regex.IsMatch(
            description,
            $@"\b(small|medium|large)?\s*([\w-]+\s+){{0,3}}{animal}\b",
            RegexOptions.IgnoreCase);
    }

    public static bool IsHumanAdultCharacter(
        string charKey,
        string ageBand,
        string description,
        string visualLock)
    {
        charKey ??= "";
        ageBand ??= "";
        description ??= "";
        visualLock ??= "";
        if (IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "dog") ||
            IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "cat") ||
            IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "bear") ||
            IsPrimarilyAnimalCharacter(charKey, ageBand, description, visualLock, "fox"))
            return false;

        var blob = $"{charKey} {ageBand} {description} {visualLock}";
        if (Regex.IsMatch(
                blob,
                @"\b(man|woman|human|mother|father|mom|dad|daddy|parent|adult|boy|girl)\b",
                RegexOptions.IgnoreCase))
            return true;

        // Relational cast keys (any book)
        return Regex.IsMatch(
            charKey,
            @"Mom|Dad|Daddy|Mother|Father|Mum|Parent|Grandma|Grandpa|Uncle|Aunt|Sister|Brother",
            RegexOptions.IgnoreCase);
    }

    private static string CleanDebris(string t)
    {
        t = Regex.Replace(t, @"\s{2,}", " ");
        t = Regex.Replace(t, @"\s+([,.;:])", "$1");
        t = Regex.Replace(t, @"([,.;:]){2,}", "$1");
        t = Regex.Replace(t, @"\(\s*\)", "");
        t = Regex.Replace(t, @"\s+—\s*—", " — ");
        return t.Trim(' ', ',', ';', '-', '—', ' ');
    }
}
