using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Labels each non-first beat as hard_cut vs extend for video continuity.
/// Baseline mirrors <see cref="Stage2PlannerService"/> ForceNone rules (public helper).
/// Writes <c>cut_decision</c> = hard_cut|extend on each beat.
/// </summary>
public sealed class ExtendCutClassifier
{
    public const string PromptVersion = "v1";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<ExtendCutClassifier> _log;

    public ExtendCutClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<ExtendCutClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyExtendCutWithChat && _chat.IsConfigured;

    public async Task<SimpleClassifyResult> ClassifyStage1Async(
        Dictionary<string, object?> stage1,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.ExtendCutClassifyModel;
        var result = new SimpleClassifyResult
        {
            Name = "extend_hardcut",
            PromptVersion = PromptVersion,
            Enabled = IsEnabled,
            Model = effectiveModel,
        };
        var pairs = CollectPairs(stage1);
        result.ItemCount = pairs.Count;
        foreach (var p in pairs)
        {
            var hard = BaselineHardCut(p);
            p.Beat["cut_decision"] = hard ? "hard_cut" : "extend";
            if (hard)
                p.Beat["continuity"] = "new_setup";
        }

        if (!IsEnabled || pairs.Count == 0)
        {
            result.FallbackCount = pairs.Count;
            result.Note = "heuristic only";
            onProgress?.Invoke($"Extend/cut: heuristic only ({pairs.Count})");
            return result;
        }

        onProgress?.Invoke($"Classifying extend vs hard-cut for {pairs.Count} beat(s)…");
        var maxAttempts = Math.Clamp(_opts.SilentBeatClassifyMaxAttempts, 1, 5);
        var labeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 25;
        var chunks = new List<List<Pair>>();
        for (var offset = 0; offset < pairs.Count; offset += batchSize)
            chunks.Add(pairs.Skip(offset).Take(batchSize).ToList());

        using var sem = new SemaphoreSlim(4);
        var tasks = chunks.Select(async chunk =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var missing = chunk.Select(p => p.Id).ToList();
                var byId = chunk.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
                for (var attempt = 1; attempt <= maxAttempts && missing.Count > 0; attempt++)
                {
                    try
                    {
                        var payload = missing.Select(id =>
                        {
                            var p = byId[id];
                            return new Dictionary<string, object?>
                            {
                                ["id"] = p.Id,
                                ["scene"] = p.Scene,
                                ["setting"] = p.Setting,
                                ["prev_visual"] = Trunc(p.PrevVisual, 40),
                                ["prev_speaker"] = p.PrevSpeaker,
                                ["visual_event"] = Trunc(p.VisualEvent, 50),
                                ["speaker"] = p.Speaker,
                                ["action_class"] = p.ActionClass,
                                ["heuristic"] = BaselineHardCut(p) ? "hard_cut" : "extend",
                            };
                        }).ToList();
                        var user = "Label hard_cut vs extend for video continuity. JSON only.\n" +
                                   JsonSerializer.Serialize(new { beats = payload });
                        var raw = await _chat.CompleteAsync(SystemPrompt(), user, effectiveModel, 0, ct, ChatCallModes.ExtendCutClassify)
                            .ConfigureAwait(false);
                        var parsed = ParseLabels(raw);
                        lock (labeled)
                        {
                            result.ChatCalls++;
                            foreach (var id in missing.ToList())
                            {
                                if (!parsed.TryGetValue(id, out var dec)) continue;
                                var p = byId[id];
                                p.Beat["cut_decision"] = dec;
                                if (dec == "hard_cut")
                                    p.Beat["continuity"] = "new_setup";
                                else
                                    p.Beat["continuity"] = "continuous_from_previous_beat";
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
                        _log.LogWarning(ex, "ExtendCut attempt {A}", attempt);
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
        result.FallbackCount = pairs.Count - labeled.Count;
        result.Note = $"AI {labeled.Count}/{pairs.Count}";
        onProgress?.Invoke($"Extend/cut: {result.Note}");
        return result;
    }

    public static string SystemPrompt() => """
You decide video continuity for an automated film pipeline (any story).
Classes:
- hard_cut: new setup, location change, big energy/action, flashback, VO after on-camera speech, clear scene break.
- extend: same place continuous business, small gesture, should blend from previous clip tail.

JSON: {"labels":[{"id":"s1_b3","class":"extend"}]}
""";

    public static Dictionary<string, string> ParseLabels(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                var cls = el.TryGetProperty("class", out var c) ? c.GetString()
                    : el.TryGetProperty("decision", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) continue;
                cls = cls!.Trim().ToLowerInvariant().Replace(' ', '_');
                if (cls is "hard_cut" or "hardcut" or "cut" or "none") cls = "hard_cut";
                if (cls is "extend" or "continue" or "continuous") cls = "extend";
                if (cls is not ("hard_cut" or "extend")) continue;
                map[id!] = cls;
            }
        }
        catch { }
        return map;
    }

    /// <summary>Public baseline used by eval (mirrors Stage2 ForceNone intent for same-location pairs).</summary>
    public static bool BaselineHardCut(string visual, string actionClass, bool sameLocation, bool isFirst)
    {
        if (isFirst) return true;
        if (!sameLocation) return true;
        var ac = (actionClass ?? "").ToLowerInvariant();
        if (ac is "big_action" or "establishing") return true;
        var ve = (visual ?? "").ToLowerInvariant();
        if (Regex.IsMatch(ve,
                @"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket|wide shot|establishing|flashback|back to present|cut to)\b"))
            return true;
        return false;
    }

    private static bool BaselineHardCut(Pair p) =>
        BaselineHardCut(p.VisualEvent, p.ActionClass, p.SameLocation, p.IsFirst);

    private static List<Pair> CollectPairs(Dictionary<string, object?> stage1)
    {
        var list = new List<Pair>();
        var scenes = stage1.TryGetValue("scenes", out var sObj) && sObj is List<object?> sl ? sl : new();
        var si = 0;
        foreach (var sItem in scenes)
        {
            if (sItem is not Dictionary<string, object?> scene) continue;
            si++;
            var setting = scene.TryGetValue("setting", out var st) ? st?.ToString() ?? "" : "";
            var primary = scene.TryGetValue("primary_location_id", out var pl) ? pl?.ToString() ?? "" : "";
            var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl ? bl : new();
            string? prevVe = null;
            string? prevLid = null;
            string? prevSpeaker = null;
            var bi = 0;
            var first = true;
            foreach (var bItem in beats)
            {
                if (bItem is not Dictionary<string, object?> beat) continue;
                bi++;
                var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString() ?? "" : "";
                var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(ve) && string.IsNullOrWhiteSpace(dlg)) continue;
                var lid = beat.TryGetValue("location_id", out var l) ? l?.ToString() ?? primary : primary;
                var ac = beat.TryGetValue("action_class", out var a) ? a?.ToString() ?? "" : "";
                var speaker = beat.TryGetValue("speaker", out var sp) ? sp?.ToString() ?? "" : "";
                list.Add(new Pair
                {
                    Id = $"s{si}_b{bi}",
                    Scene = si,
                    Setting = setting,
                    VisualEvent = ve,
                    PrevVisual = prevVe ?? "",
                    Speaker = speaker,
                    PrevSpeaker = prevSpeaker ?? "",
                    ActionClass = ac,
                    SameLocation = prevLid is null || string.Equals(prevLid, lid, StringComparison.OrdinalIgnoreCase),
                    IsFirst = first,
                    Beat = beat,
                });
                first = false;
                prevVe = ve;
                prevLid = lid;
                prevSpeaker = speaker;
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

    // Token-accurate now (was raw character count) — see PromptTokenizer.
    private static string Trunc(string s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);

    private sealed class Pair
    {
        public required string Id { get; init; }
        public int Scene { get; init; }
        public string Setting { get; init; } = "";
        public string VisualEvent { get; init; } = "";
        public string PrevVisual { get; init; } = "";
        public string Speaker { get; init; } = "";
        public string PrevSpeaker { get; init; } = "";
        public string ActionClass { get; init; } = "";
        public bool SameLocation { get; init; }
        public bool IsFirst { get; init; }
        public required Dictionary<string, object?> Beat { get; init; }
    }
}
