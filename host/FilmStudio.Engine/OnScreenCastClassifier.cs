using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Closed-set on-screen cast keys from beat visual (+ speaker). Baseline: name substring match.
/// Writes <c>characters_on_screen</c> on each beat when AI succeeds.
/// </summary>
public sealed class OnScreenCastClassifier
{
    /// <summary>Shipped prompt id (matches host/evals/classifier_benchmarks/prompts/onscreen_cast/v2_grounded).</summary>
    public const string PromptVersion = "v2_grounded";

    /// <summary>
    /// Matches "voiceover" / "voice-over" / "voice over" or the abbreviation "V.O." / "VO" as a
    /// whole word — NOT a bare "vo" substring, which false-positives on "voice", "avoid", "provoke", etc.
    /// </summary>
    private static readonly Regex VoiceoverPattern = new(
        @"\bvoice[\s-]?over\b|\bv\.?\s*o\.?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IChatClient _chat;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<OnScreenCastClassifier> _log;

    public OnScreenCastClassifier(
        IChatClient chat,
        IOptions<FilmStudioOptions> opts,
        ILogger<OnScreenCastClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyOnScreenCastWithChat && _chat.IsConfigured;

    public async Task<SimpleClassifyResult> ClassifyStage1Async(
        Dictionary<string, object?> stage1,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.OnScreenCastClassifyModel;
        var result = new SimpleClassifyResult
        {
            Name = "onscreen_cast",
            PromptVersion = PromptVersion,
            Enabled = IsEnabled,
            Model = effectiveModel,
        };
        var castKeys = ExtractCastKeys(stage1);
        var targets = CollectSilentAndDialogue(stage1);
        result.ItemCount = targets.Count;
        if (castKeys.Count == 0 || targets.Count == 0)
        {
            result.Note = "no cast or beats";
            return result;
        }

        // Baseline heuristic into beat field for fallback
        var profiles = castKeys.ToDictionary(
            k => k,
            k => new ClipVideoPromptBuilder.CharacterProfile { DisplayName = k.Replace("Character_", "").Replace('_', ' ') },
            StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            var inferred = ClipVideoPromptBuilder.InferKeysFromProse(t.VisualEvent + " " + t.Dialogue, profiles);
            if (!string.IsNullOrWhiteSpace(t.SpeakerKey) &&
                !inferred.Contains(t.SpeakerKey, StringComparer.OrdinalIgnoreCase) &&
                !t.IsVoiceover)
                inferred.Add(t.SpeakerKey);
            t.HeuristicKeys = inferred;
            t.Beat["characters_on_screen"] = inferred.Cast<object?>().ToList();
        }

        if (!IsEnabled)
        {
            result.FallbackCount = targets.Count;
            result.Note = "heuristic only";
            onProgress?.Invoke($"On-screen cast: heuristic only ({targets.Count})");
            return result;
        }

        onProgress?.Invoke($"Classifying on-screen cast for {targets.Count} beat(s)…");
        var maxAttempts = Math.Clamp(_opts.SilentBeatClassifyMaxAttempts, 1, 5);
        var labeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 25;

        var chunks = new List<List<Target>>();
        for (var offset = 0; offset < targets.Count; offset += batchSize)
            chunks.Add(targets.Skip(offset).Take(batchSize).ToList());

        using var sem = new SemaphoreSlim(4);
        var tasks = chunks.Select(async chunk =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var missing = chunk.Select(t => t.Id).ToList();
                var byId = chunk.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                for (var attempt = 1; attempt <= maxAttempts && missing.Count > 0; attempt++)
                {
                    try
                    {
                        var payload = missing.Select(id =>
                        {
                            var t = byId[id];
                            return new Dictionary<string, object?>
                            {
                                ["id"] = t.Id,
                                ["visual_event"] = Trunc(t.VisualEvent, 240),
                                ["dialogue"] = Trunc(t.Dialogue, 120),
                                ["speaker_key"] = t.SpeakerKey,
                                ["is_voiceover"] = t.IsVoiceover,
                                ["heuristic_keys"] = t.HeuristicKeys,
                            };
                        }).ToList();
                        var user = "Pick on-screen Character_* keys from the closed cast. JSON only.\n" +
                                   JsonSerializer.Serialize(new { cast_keys = castKeys, beats = payload });
                        var raw = await _chat.CompleteAsync(SystemPrompt(), user, effectiveModel, 0, ct, ChatCallModes.OnScreenCastClassify)
                            .ConfigureAwait(false);
                        var parsed = ParseLabels(raw, castKeys);
                        lock (labeled)
                        {
                            result.ChatCalls++;
                            foreach (var id in missing.ToList())
                            {
                                if (!parsed.TryGetValue(id, out var keys)) continue;
                                byId[id].Beat["characters_on_screen"] = keys.Cast<object?>().ToList();
                                missing.Remove(id);
                                labeled.Add(id);
                            }
                        }
                        if (missing.Count > 0)
                            await Task.Delay(Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs) * attempt, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "OnScreenCast attempt {A}", attempt);
                        result.LastError = ex.Message;
                        await Task.Delay(Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs) * attempt, ct);
                    }
                }
            }
            finally
            {
                sem.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        result.AiCount = labeled.Count;
        result.FallbackCount = targets.Count - labeled.Count;
        result.Note = $"AI {labeled.Count}/{targets.Count}";
        onProgress?.Invoke($"On-screen cast: {result.Note}");
        return result;
    }

    public static string SystemPrompt() => """
You assign which locked cast members are ON CAMERA for each beat (any story).

Closed set only: return Character_* keys from cast_keys. Never invent keys outside that list.

## Include
- Everyone clearly visible or physically acting in the visual_event (named or unambiguously described).
- Non-voiceover speakers when the beat is their on-camera line (visual like "X speaks." or they appear in the action).
- Group cast keys when the visual shows that group acting (e.g. monkey troop, cub litter, seal crowd) and the key exists in cast_keys.

## Exclude
- Voiceover-only or off-screen speakers (is_voiceover: true, or spoken from off-screen / another room) UNLESS the visual explicitly shows their physical body on camera.
- Names mentioned only in dialogue or as possession of a prop ("Shere Khan's hide", "Kala Nag" in a line) when the visual does not show that character body on screen.
- Corpses, skins, hides, trophies, photos, statues — not living on-screen cast.
- Anonymous crowd/pack without a matching group cast key (do not invent individuals).

## Disambiguation
- Prefer the most specific matching key (longest / full name). Never also add a shorter key that is only a substring of another matched name (e.g. Kala Nag → Character_Kala_Nag only, not Character_Nag).
- Nicknames and hyphen variants count (Rikki / Rikki-tikki → Character_Rikki_Tikki when that key exists).
- Pronoun-only beats: if the subject is clearly a continuing named cast member from story context in the visual prose, include that key; if truly ambiguous, empty or only keys grounded in text.

## Heuristic
- You may correct heuristic_keys when they over-include (props, substring false hits) or under-include (groups, nicknames).

Empty list is OK for pure environment or sound-only beats with no readable faces/bodies.

JSON only:
{"labels":[{"id":"s1_b1","keys":["Character_Narrator"]}]}
""";

    public static Dictionary<string, List<string>> ParseLabels(string raw, IReadOnlyList<string> castKeys)
    {
        var allowed = new HashSet<string>(castKeys, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        raw = Strip(raw);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.GetProperty("labels");
            foreach (var el in arr.EnumerateArray())
            {
                var id = el.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var keys = new List<string>();
                if (el.TryGetProperty("keys", out var kEl) && kEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in kEl.EnumerateArray())
                    {
                        var s = k.GetString();
                        if (s is null) continue;
                        var hit = allowed.FirstOrDefault(a => a.Equals(s, StringComparison.OrdinalIgnoreCase));
                        if (hit is not null && !keys.Contains(hit, StringComparer.OrdinalIgnoreCase))
                            keys.Add(hit);
                    }
                }
                map[id!] = keys;
            }
        }
        catch { }
        return map;
    }

    public static double SetF1(IReadOnlyList<string> pred, IReadOnlyList<string> gold)
    {
        var p = new HashSet<string>(pred, StringComparer.OrdinalIgnoreCase);
        var g = new HashSet<string>(gold, StringComparer.OrdinalIgnoreCase);
        if (p.Count == 0 && g.Count == 0) return 1.0;
        if (p.Count == 0 || g.Count == 0) return 0.0;
        var inter = p.Intersect(g, StringComparer.OrdinalIgnoreCase).Count();
        var prec = (double)inter / p.Count;
        var rec = (double)inter / g.Count;
        return prec + rec <= 0 ? 0 : 2 * prec * rec / (prec + rec);
    }

    private static List<string> ExtractCastKeys(Dictionary<string, object?> stage1)
    {
        var gpv = stage1.TryGetValue("global_production_variables", out var g) && g is Dictionary<string, object?> gd ? gd : null;
        var seeds = gpv is not null && gpv.TryGetValue("character_seed_tokens", out var c) && c is Dictionary<string, object?> cs ? cs : null;
        if (seeds is null) return new List<string>();
        return seeds.Keys.Where(k => k.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).ToList();
    }

    private static List<Target> CollectSilentAndDialogue(Dictionary<string, object?> stage1)
    {
        var list = new List<Target>();
        var scenes = stage1.TryGetValue("scenes", out var sObj) && sObj is List<object?> sl ? sl : new();
        var si = 0;
        foreach (var sItem in scenes)
        {
            if (sItem is not Dictionary<string, object?> scene) continue;
            si++;
            var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl ? bl : new();
            var bi = 0;
            foreach (var bItem in beats)
            {
                if (bItem is not Dictionary<string, object?> beat) continue;
                bi++;
                var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString() ?? "" : "";
                var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString() ?? "" : "";
                var sp = beat.TryGetValue("speaker", out var s) ? s?.ToString() ?? "" : "";
                var del = beat.TryGetValue("delivery", out var delv) ? delv?.ToString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(ve) && string.IsNullOrWhiteSpace(dlg)) continue;
                list.Add(new Target
                {
                    Id = $"s{si}_b{bi}",
                    VisualEvent = ve,
                    Dialogue = dlg,
                    SpeakerKey = sp,
                    IsVoiceover = VoiceoverPattern.IsMatch(del),
                    Beat = beat,
                });
            }
        }
        return list;
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

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    private sealed class Target
    {
        public required string Id { get; init; }
        public string VisualEvent { get; init; } = "";
        public string Dialogue { get; init; } = "";
        public string SpeakerKey { get; init; } = "";
        public bool IsVoiceover { get; init; }
        public required Dictionary<string, object?> Beat { get; init; }
        public List<string> HeuristicKeys { get; set; } = new();
    }
}

public sealed class SimpleClassifyResult
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public int ItemCount { get; set; }
    public int AiCount { get; set; }
    public int FallbackCount { get; set; }
    public int ChatCalls { get; set; }
    public string Note { get; set; } = "";
    public string? LastError { get; set; }

    public Dictionary<string, object?> ToMetaDict() => new()
    {
        ["name"] = Name,
        ["enabled"] = Enabled,
        ["prompt_version"] = PromptVersion,
        ["model"] = Model,
        ["items"] = ItemCount,
        ["ai_labels"] = AiCount,
        ["heuristic_fallback"] = FallbackCount,
        ["chat_calls"] = ChatCalls,
        ["note"] = Note,
    };
}
