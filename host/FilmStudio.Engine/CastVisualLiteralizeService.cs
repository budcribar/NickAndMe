using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// AI pass: rewrite figurative / idiomatic visual prose into literal filmable descriptions.
/// Avoids never-ending regex nickname lists — the model judges phrase risk.
/// Prompt: <c>prompts/cast_visual_literalize.txt</c>.
/// </summary>
public sealed class CastVisualLiteralizeService
{
    public const string PromptRelativePath = "prompts/cast_visual_literalize.txt";

    private readonly ProjectStore _projects;
    private readonly IGrokChatClient _chat;
    private readonly ILogger<CastVisualLiteralizeService> _log;

    public CastVisualLiteralizeService(
        ProjectStore projects,
        IGrokChatClient chat,
        ILogger<CastVisualLiteralizeService> log)
    {
        _projects = projects;
        _chat = chat;
        _log = log;
    }

    /// <summary>
    /// Literalize description / visual_lock / wardrobe_always on each seed in-place (dict).
    /// Non-fatal: returns input seeds if chat fails.
    /// AI prompt handles figurative language + base-look vs later wardrobe — no special-case lists.
    /// </summary>
    public async Task<Dictionary<string, object?>> LiteralizeSeedsAsync(
        Dictionary<string, object?> seeds,
        string model = "grok-4.5",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (seeds.Count == 0 || !_chat.IsConfigured)
            return seeds;

        onProgress?.Invoke("Scrubbing visual descriptions (AI prompt)…");
        try
        {
            var system = await LoadSystemPromptAsync(_projects.WorkspaceRoot, ct).ConfigureAwait(false);
            var payload = new Dictionary<string, object?>
            {
                ["character_seed_tokens"] = BuildVisualPayload(seeds),
            };
            var user =
                "Scrub these character seeds for generative image models:\n" +
                "1) figurative/idiomatic → literal filmable\n" +
                "2) base portrait look only — strip later-story wardrobe/plot from description & visual_lock\n" +
                "Return JSON only with character_seed_tokens.\n\n" +
                JsonSerializer.Serialize(payload, JsonDefaults.Indented);

            var raw = await _chat.CompleteAsync(
                    system, user, model, temperature: 0.15, ct,
                    mode: ChatCallModes.CastVisualLiteralize)
                .ConfigureAwait(false);
            var parsed = GrokChatClient.ParseJsonObject(StripFences(raw));
            return MergeLiteralized(seeds, parsed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cast visual literalize failed — keeping pre-literalize seeds");
            onProgress?.Invoke("AI visual scrub skipped (non-fatal).");
            return seeds;
        }
    }

    /// <summary>
    /// Scrub one character's look fields via the same API prompt (Save look / generate).
    /// Returns cleaned description + visual_lock; non-fatal falls back to input.
    /// </summary>
    public async Task<(string Description, string VisualLock, bool UsedAi)> ScrubLookFieldsAsync(
        string charKey,
        string? description,
        string? visualLock,
        string? wardrobeAlwaysJson = null,
        string model = "grok-4.5",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var descIn = description ?? "";
        var visIn = visualLock ?? "";
        if (!_chat.IsConfigured)
            return (descIn, visIn, false);
        if (string.IsNullOrWhiteSpace(descIn) && string.IsNullOrWhiteSpace(visIn))
            return (descIn, visIn, false);

        var seed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = descIn,
            ["visual_lock"] = visIn,
        };
        if (!string.IsNullOrWhiteSpace(wardrobeAlwaysJson))
        {
            try
            {
                var wa = JsonSerializer.Deserialize<List<object?>>(wardrobeAlwaysJson);
                if (wa is not null) seed["wardrobe_always"] = wa;
            }
            catch { /* optional */ }
        }

        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [charKey] = seed,
        };
        onProgress?.Invoke("AI scrub: base look + literal filmable…");
        var cleaned = await LiteralizeSeedsAsync(bag, model, onProgress, ct).ConfigureAwait(false);
        if (cleaned.TryGetValue(charKey, out var cval) && cval is Dictionary<string, object?> cseed)
        {
            var d = cseed.TryGetValue("description", out var dv) ? dv?.ToString() ?? descIn : descIn;
            var v = cseed.TryGetValue("visual_lock", out var vv) ? vv?.ToString() ?? visIn : visIn;
            return (d.Trim(), v.Trim(), true);
        }
        return (descIn, visIn, false);
    }

    public static async Task<string> LoadSystemPromptAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var path = Path.Combine(
            workspaceRoot,
            PromptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException($"Visual literalize prompt not found: {path}");
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> BuildVisualPayload(Dictionary<string, object?> seeds)
    {
        var outSeeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in seeds)
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var slim = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (seed.TryGetValue("description", out var d)) slim["description"] = d;
            if (seed.TryGetValue("visual_lock", out var v)) slim["visual_lock"] = v;
            if (seed.TryGetValue("wardrobe_always", out var w)) slim["wardrobe_always"] = w;
            if (seed.TryGetValue("display_name_policy", out var p)) slim["display_name_policy"] = p;
            if (seed.TryGetValue("canonical_given_name", out var n)) slim["canonical_given_name"] = n;
            outSeeds[key] = slim;
        }
        return outSeeds;
    }

    private static Dictionary<string, object?> MergeLiteralized(
        Dictionary<string, object?> original,
        Dictionary<string, object?> parsed)
    {
        Dictionary<string, object?>? cleanedSeeds = null;
        if (parsed.TryGetValue("character_seed_tokens", out var s) && s is Dictionary<string, object?> d)
            cleanedSeeds = d;
        else if (parsed.TryGetValue("global_production_variables", out var g) &&
                 g is Dictionary<string, object?> gpv &&
                 gpv.TryGetValue("character_seed_tokens", out var s2) &&
                 s2 is Dictionary<string, object?> d2)
            cleanedSeeds = d2;

        if (cleanedSeeds is null || cleanedSeeds.Count == 0)
            return original;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in original)
        {
            if (val is not Dictionary<string, object?> seed)
            {
                result[key] = val;
                continue;
            }

            var copy = new Dictionary<string, object?>(seed, StringComparer.OrdinalIgnoreCase);
            if (cleanedSeeds.TryGetValue(key, out var cval) && cval is Dictionary<string, object?> clean)
            {
                if (clean.TryGetValue("description", out var desc) && desc is not null)
                    copy["description"] = desc.ToString()?.Trim();
                if (clean.TryGetValue("visual_lock", out var vl) && vl is not null)
                    copy["visual_lock"] = vl.ToString()?.Trim();
                if (clean.TryGetValue("wardrobe_always", out var wa) && wa is List<object?> list)
                    copy["wardrobe_always"] = list;
            }
            result[key] = copy;
        }

        // Add any unexpected keys from model (shouldn't, but keep closed)
        foreach (var (key, val) in cleanedSeeds)
        {
            if (!result.ContainsKey(key) && val is Dictionary<string, object?>)
                result[key] = val;
        }

        return result;
    }

    private static string StripFences(string text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = Regex.Replace(text, @"^```(?:json|text)?\s*", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*```\s*$", "");
        }
        return text.Trim();
    }
}
