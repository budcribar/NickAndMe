using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Optional chat re-rank of candidate book image basenames for a character when vision is empty.
/// Baseline remains filename/illustration heuristics in CharacterBookPlateService.
/// </summary>
public sealed class PlateRankClassifier
{
    public const string PromptVersion = "v1";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<PlateRankClassifier> _log;

    public PlateRankClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<PlateRankClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyPlateRankWithChat && _chat.IsConfigured;

    /// <summary>
    /// Re-order <paramref name="candidateNames"/> (basenames). Returns up to 3 names; on failure returns input order.
    /// </summary>
    public async Task<(IReadOnlyList<string> Ranked, bool UsedAi)> RankAsync(
        string charKey,
        string description,
        IReadOnlyList<string> candidateNames,
        CancellationToken ct = default)
    {
        if (candidateNames.Count == 0)
            return (Array.Empty<string>(), false);
        var baseline = candidateNames.Take(3).ToList();
        if (!IsEnabled || candidateNames.Count <= 1)
            return (baseline, false);

        var model = string.IsNullOrWhiteSpace(_opts.PlateRankClassifyModel) ? "grok-4.5" : _opts.PlateRankClassifyModel;
        var maxAttempts = Math.Clamp(_opts.SilentBeatClassifyMaxAttempts, 1, 3);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var user = JsonSerializer.Serialize(new
                {
                    character_key = charKey,
                    description = Trunc(description, 60),
                    candidates = candidateNames.Take(24).ToList(),
                    instruction = "Return top 3 basenames best matching the character (illustration pages, not pure text).",
                });
                var raw = await _chat.CompleteAsync(SystemPrompt(), user, model, 0, ct, ChatCallModes.PlateRankClassify)
                    .ConfigureAwait(false);
                var ranked = ParseRank(raw, candidateNames);
                if (ranked.Count > 0)
                    return (ranked.Take(3).ToList(), true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PlateRank attempt {A} for {Key}", attempt, charKey);
                await Task.Delay(Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs) * attempt, ct);
            }
        }
        return (baseline, false);
    }

    public static string SystemPrompt() => """
You pick book image basenames that best show a given character for portrait seeding (any story).
Prefer illustrated character art / cover over text-only pages.
Return only names from candidates.

JSON: {"ranked":["page_03.png","cover.png","embedded_p02.jpg"]}
""";

    public static List<string> ParseRank(string raw, IReadOnlyList<string> candidates)
    {
        var allowed = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        raw = Strip(raw);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            JsonElement arr;
            if (root.TryGetProperty("ranked", out var r)) arr = r;
            else if (root.TryGetProperty("labels", out var l)) arr = l;
            else if (root.ValueKind == JsonValueKind.Array) arr = root;
            else return list;
            foreach (var el in arr.EnumerateArray())
            {
                var name = el.ValueKind == JsonValueKind.String ? el.GetString()
                    : el.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var hit = allowed.FirstOrDefault(a =>
                    a.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    a.EndsWith(name!, StringComparison.OrdinalIgnoreCase) ||
                    name!.EndsWith(a, StringComparison.OrdinalIgnoreCase));
                if (hit is not null && !list.Contains(hit, StringComparer.OrdinalIgnoreCase))
                    list.Add(hit);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Recall@K: fraction of gold top items appearing in pred top-K.</summary>
    public static double RecallAtK(IReadOnlyList<string> pred, IReadOnlyList<string> gold, int k = 3)
    {
        if (gold.Count == 0) return pred.Count == 0 ? 1.0 : 0.0;
        var p = pred.Take(k).ToList();
        var hits = gold.Count(g => p.Any(x => x.Equals(g, StringComparison.OrdinalIgnoreCase)));
        return (double)hits / gold.Count;
    }

    private static string Strip(string raw)
    {
        raw = (raw ?? "").Trim();
        if (!raw.StartsWith("```")) return raw;
        raw = Regex.Replace(raw, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
        // Truncate at the closing fence wherever it falls — some models append prose
        // (e.g. a "Reasoning:" section) after the fenced JSON instead of ending on it.
        var fenceEnd = raw.IndexOf("```", StringComparison.Ordinal);
        return (fenceEnd >= 0 ? raw[..fenceEnd] : raw).TrimEnd();
    }

    // Token-accurate now (was raw character count) — see PromptTokenizer.
    private static string Trunc(string s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);
}
