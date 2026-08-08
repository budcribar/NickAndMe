using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;
using PageToMovie.Adaptation.Conversion;

namespace PageToMovie.Adaptation;

/// <summary>
/// Adaptation density: how much finished film a source yields when adapted naturally
/// (no pad-to-target, no forced crush below the story spine).
/// </summary>
/// <remarks>
/// <para><b>Definition</b></para>
/// <para>
/// <c>δ = natural_film_minutes / (source_words / 1000)</c>
/// — expected minutes of finished film per 1,000 source words under a natural adaptation.
/// </para>
/// <para>
/// Companion ratio (audiobook / full-prose speech baseline):
/// <c>τ = natural_film_minutes / audiobook_minutes</c>
/// where audiobook_minutes uses ~150 wpm on all source words (every word spoken).
/// τ ≪ 1 for novels (most prose is not spoken on screen); short literary monologues
/// (e.g. Tell-Tale Heart) keep most temporal mass as VO (τ closer to 1).
/// </para>
/// <para><b>Calibration</b></para>
/// <list type="bullet">
/// <item>Mary Had a Little Lamb (~140 words) slow read ~2 min → verse speech × staging.</item>
/// <item>PageToMovie Tell-Tale Heart film (YouTube, 16:49) for ~2.2k words → short
///     literary uses narration-rate speech × ~1.2 staging (~17 min), not novel δ≈2.</item>
/// <item>Feature novels: δ≈2 min/1k words (Fight Club / early HP / market band).</item>
/// </list>
/// </remarks>
public static class AdaptationDensity
{
    /// <summary>Full-prose narration reference rate (audiobook-ish), words per minute.</summary>
    public const double AudiobookWordsPerMinute = 150.0;

    /// <summary>
    /// Storybook read-aloud rate for short verse/micro sources, words per second. ~1.8 wps (≈108 wpm) is an
    /// expressive, unhurried children's read-aloud. Calibrated so a near-dialogue-free nursery rhyme still
    /// yields a watchable ~2 min film (Mary's 136 words → ~2 min): 1.15 over-inflated (~3), 2.5 under-shot
    /// (~1). Staging is added separately below.
    /// </summary>
    public const double StorybookWordsPerSecond = 1.8;

    /// <summary>Slow syllable rate for short verse performance read.</summary>
    public const double StorybookSyllablesPerSecond = 3.2;

    /// <summary>
    /// Staging on pure speech for nursery / micro sources (establish, pans).
    /// </summary>
    public const double VerseStagingMultiplier = 1.45;

    /// <summary>
    /// Staging on narration-rate speech for short literary (TTH calibration: 16:49 film ≈
    /// speech@2.6 wps × 1.2). Covers lantern holds, floorboard business, police hang.
    /// </summary>
    public const double ShortLiteraryStagingMultiplier = 1.20;

    // Novel / longform baseline δ (film minutes per 1k source words) — market feature band.
    public const double DeltaPictureBookPages = 12.0; // only when not using speech path
    public const double DeltaNovel = 2.0;

    private static readonly Regex QuotedSpan = new(
        "[\"“]([^\"”]{2,})[\"”]",
        RegexOptions.Compiled);

    public sealed class Estimate
    {
        public int SourceWords { get; init; }
        public int SourceSyllables { get; init; }
        public string BookKind { get; init; } = "";
        /// <summary>Fraction of characters inside quote marks (0–1), rough spoken-dialogue prior.</summary>
        public double QuotedDialogueFraction { get; init; }
        /// <summary>All source words at <see cref="AudiobookWordsPerMinute"/>.</summary>
        public double AudiobookMinutes { get; init; }
        /// <summary>δ — finished film minutes per 1,000 source words.</summary>
        public double MinutesPerThousandWords { get; init; }
        /// <summary>τ — natural film / audiobook; compression of temporal mass.</summary>
        public double TemporalCompressionRatio { get; init; }
        /// <summary>Natural finished-film minutes (starting point before any user cut).</summary>
        public int NaturalFilmMinutes { get; init; }
        /// <summary>How the estimate was derived (for logs / benchmark manifests).</summary>
        public string Method { get; init; } = "";
        public string Notes { get; init; } = "";
    }

    /// <summary>
    /// Pre-screenplay natural film estimate and density metrics for a prepared book.
    /// Does not call <see cref="BookTextAnalyzer.Analyze"/> when <paramref name="bookKind"/> is set
    /// (avoids recursion from Analyze → density).
    /// </summary>
    public static Estimate EstimateNatural(string? bookText, string? bookKind = null)
    {
        var text = bookText ?? "";
        string kind;
        int words;
        if (!string.IsNullOrWhiteSpace(bookKind))
        {
            kind = bookKind.Trim();
            // Prefer analyzer word count when available without re-entering suggested-runtime.
            words = TextMetrics.CountWords(BookToFountainConverter.NormalizeBookText(text));
            if (words <= 0)
                words = TextMetrics.CountWords(text);
        }
        else
        {
            var analysis = BookTextAnalyzer.Analyze(text);
            kind = analysis.BookKind;
            words = analysis.TextWords;
        }

        var syllables = TextMetrics.CountSyllables(text);
        var quoteFrac = EstimateQuotedDialogueFraction(text);
        return EstimateFromStats(kind, words, syllables, quoteFrac);
    }

    /// <summary>
    /// Core estimator from precomputed stats (used by <see cref="BookTextAnalyzer"/> and benchmarks).
    /// </summary>
    public static Estimate EstimateFromStats(
        string bookKind,
        int words,
        int syllables,
        double quotedDialogueFraction)
    {
        var kind = string.IsNullOrWhiteSpace(bookKind) ? "short" : bookKind.Trim();
        words = Math.Max(0, words);
        syllables = Math.Max(0, syllables);
        var quoteFrac = Math.Clamp(quotedDialogueFraction, 0, 1);
        var audiobookMin = words <= 0 ? 0 : words / AudiobookWordsPerMinute;

        int natural;
        double delta;
        string method;
        string notes;

        if (words > 0 && words < 500)
        {
            // Nursery / micro: slow read-aloud × staging (Mary ~2 min).
            var speechSec = Math.Max(
                words / StorybookWordsPerSecond,
                syllables / Math.Max(0.1, StorybookSyllablesPerSecond));
            var filmSec = speechSec * VerseStagingMultiplier;
            // Allow 1 min floor so true micro sources (Mary ~1–2 min) are not forced to 2–3.
            natural = Math.Clamp((int)Math.Round(filmSec / 60.0), 1, 15);
            delta = natural / (words / 1000.0);
            method = "verse_speech_x_staging";
            notes =
                $"Slow read-aloud speech × {VerseStagingMultiplier:F2} staging; " +
                "film ≈ performance length (no novel compression).";
        }
        else if (kind is "short" or "picture_book")
        {
            // Short literary / picture-book prose: most words become VO or on-camera speech.
            // Calibrated on PageToMovie Tell-Tale Heart (YouTube 16:49 ≈ 17 min for ~2.2k words):
            //   speechSec = max(words/2.6, syllables/4.2)  // TextMetrics / narration rates
            //   filmMin   ≈ speechSec × 1.20 / 60
            var speechSec = Math.Max(
                words / TextMetrics.DialogueWordsPerSecond,
                syllables / 4.2);
            var filmMin = speechSec * ShortLiteraryStagingMultiplier / 60.0;
            natural = kind == "picture_book"
                ? Math.Clamp((int)Math.Round(filmMin), 1, 40)
                : Math.Clamp((int)Math.Round(filmMin), 2, 45);
            delta = words > 0 ? natural / (words / 1000.0) : DeltaPictureBookPages;
            method = "short_literary_speech_x_staging";
            notes =
                $"Narration-rate speech (TextMetrics) × {ShortLiteraryStagingMultiplier:F2} " +
                "staging; calibrated on Tell-Tale Heart (~17 min / ~2.2k words). " +
                "Not novel δ — short fiction keeps most temporal mass.";
        }
        else
        {
            // Novels: market feature density (~2 min/1k), nudged by quoted dialogue.
            var dialogueFactor = 0.85 + 0.5 * Math.Clamp(quoteFrac / 0.30, 0.0, 1.0);
            delta = DeltaNovel * dialogueFactor;
            var raw = delta * (words / 1000.0);
            natural = Math.Clamp((int)Math.Round(raw), 40, 180);
            delta = words > 0 ? natural / (words / 1000.0) : DeltaNovel;
            method = "novel_delta_x_dialogue_mix";
            notes =
                $"Feature-band δ≈{DeltaNovel} min/1k words (market adaptations), adjusted by " +
                $"quoted-dialogue fraction {quoteFrac:P0}; not full-prose speech.";
        }

        var tau = audiobookMin > 0.01 ? natural / audiobookMin : 0;

        return new Estimate
        {
            SourceWords = words,
            SourceSyllables = syllables,
            BookKind = kind,
            QuotedDialogueFraction = Math.Round(quoteFrac, 3),
            AudiobookMinutes = Math.Round(audiobookMin, 1),
            MinutesPerThousandWords = Math.Round(delta, 2),
            TemporalCompressionRatio = Math.Round(tau, 3),
            NaturalFilmMinutes = natural,
            Method = method,
            Notes = notes,
        };
    }

    /// <summary>
    /// Suggested reduced budget for dual benchmarks: half of natural, floored for longform.
    /// Returns null when the book is short enough that reduce mode should be skipped.
    /// </summary>
    public static int? SuggestReducedBenchmarkMinutes(Estimate natural, int longThresholdMinutes = 45)
    {
        if (natural.NaturalFilmMinutes < longThresholdMinutes)
            return null;
        var half = (int)Math.Round(natural.NaturalFilmMinutes * 0.5);
        return Math.Clamp(half, 20, natural.NaturalFilmMinutes - 5);
    }

    /// <summary>Rough prior: character mass inside ASCII/curly quotes over total letters.</summary>
    public static double EstimateQuotedDialogueFraction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var letters = text.Count(char.IsLetter);
        if (letters < 20) return 0;

        var quotedLetters = 0;
        foreach (Match m in QuotedSpan.Matches(text))
            quotedLetters += m.Groups[1].Value.Count(char.IsLetter);

        return Math.Clamp(quotedLetters / (double)letters, 0, 1);
    }
}
