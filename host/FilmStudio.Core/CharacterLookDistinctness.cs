using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmStudio.Core;

/// <summary>
/// Cheap cross-cast check: flag look text that is near-identical to another character
/// (catches swapped/copied description or visual_lock before portrait gen).
/// General product logic — no title- or name-specific rules.
/// </summary>
public static class CharacterLookDistinctness
{
    /// <summary>Jaccard token overlap at or above this is treated as near-duplicate.</summary>
    public const double NearDuplicateThreshold = 0.78;

    public sealed record SimilarLookHit(
        string OtherCharKey,
        string Field,
        double Score);

    /// <summary>
    /// Compare proposed description/visual_lock for <paramref name="charKey"/> against
    /// every other seed in the cast. Returns hits for description and/or visual_lock.
    /// </summary>
    public static IReadOnlyList<SimilarLookHit> FindNearDuplicates(
        IReadOnlyDictionary<string, JsonElement> seeds,
        string charKey,
        string? description,
        string? visualLock,
        double threshold = NearDuplicateThreshold)
    {
        if (seeds.Count == 0 || string.IsNullOrWhiteSpace(charKey))
            return Array.Empty<SimilarLookHit>();

        var hits = new List<SimilarLookHit>();
        var descIn = Normalize(description);
        var visIn = Normalize(visualLock);

        foreach (var (otherKey, el) in seeds)
        {
            if (string.Equals(otherKey, charKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            string? otherDesc = null;
            string? otherVis = null;
            if (el.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String)
                otherDesc = dEl.GetString();
            if (el.TryGetProperty("visual_lock", out var vEl) && vEl.ValueKind == JsonValueKind.String)
                otherVis = vEl.GetString();

            var otherDescN = Normalize(otherDesc);
            var otherVisN = Normalize(otherVis);

            if (descIn.Length > 0 && otherDescN.Length > 0)
            {
                var score = Similarity(descIn, otherDescN);
                if (score >= threshold)
                    hits.Add(new SimilarLookHit(otherKey, "description", score));
            }

            if (visIn.Length > 0 && otherVisN.Length > 0)
            {
                var score = Similarity(visIn, otherVisN);
                if (score >= threshold)
                    hits.Add(new SimilarLookHit(otherKey, "visual_lock", score));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.OtherCharKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Operator-facing one-liner from hits (display names when available).
    /// </summary>
    public static string? FormatWarning(
        IReadOnlyList<SimilarLookHit> hits,
        Func<string, string>? displayNameForKey = null)
    {
        if (hits is null || hits.Count == 0)
            return null;

        var keys = hits
            .Select(h => h.OtherCharKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(k => displayNameForKey?.Invoke(k) ?? FriendlyKey(k))
            .ToList();

        if (keys.Count == 0)
            return null;

        var names = string.Join(", ", keys);
        return keys.Count == 1
            ? $"This look is almost the same as {names}. Make them distinct before generating portraits."
            : $"This look is almost the same as: {names}. Make them distinct before generating portraits.";
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var t = text.Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"\s+", " ");
        t = Regex.Replace(t, @"[^\p{L}\p{N}\s]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    /// <summary>
    /// Exact match after normalize → 1.0; else Jaccard on word tokens.
    /// </summary>
    public static double Similarity(string aNorm, string bNorm)
    {
        if (aNorm.Length == 0 && bNorm.Length == 0)
            return 1.0;
        if (aNorm.Length == 0 || bNorm.Length == 0)
            return 0.0;
        if (string.Equals(aNorm, bNorm, StringComparison.Ordinal))
            return 1.0;

        var ta = Tokenize(aNorm);
        var tb = Tokenize(bNorm);
        if (ta.Count == 0 || tb.Count == 0)
            return 0.0;

        var inter = 0;
        foreach (var t in ta)
        {
            if (tb.Contains(t))
                inter++;
        }

        var union = ta.Count + tb.Count - inter;
        return union <= 0 ? 0.0 : (double)inter / union;
    }

    private static HashSet<string> Tokenize(string norm)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in norm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Skip very short glue words so "police coat and hat" doesn't dominate alone
            if (part.Length <= 2)
                continue;
            set.Add(part);
        }
        return set;
    }

    private static string FriendlyKey(string charKey)
    {
        var k = (charKey ?? "").Trim();
        if (k.StartsWith("Character_", StringComparison.OrdinalIgnoreCase))
            k = k["Character_".Length..];
        return k.Replace('_', ' ').Trim();
    }
}
