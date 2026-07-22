using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Batch-classify silent beats' <c>action_class</c> for duration budgeting via chat.
/// Policy: AI preferred → retry on transport/parse flake → heuristic fallback only when
/// no valid class for that beat. Disagreement with heuristic is not a failure.
/// Prompt version and model are recorded for offline comparison (see BeatLabelEval).
/// </summary>
public sealed class SilentBeatActionClassifier
{
    /// <summary>Eval / ship prompt id (duration-linked defs; tied with v1/v3 on gold).</summary>
    public const string PromptVersion = "v2";

    public const string DefaultModel = "grok-4.5";
    public const double DefaultTemperature = 0.0;
    public const int DefaultMaxAttempts = 3; // 1 try + 2 retries
    public const int DefaultBatchSize = 40;

    private readonly IChatClient _chat;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<SilentBeatActionClassifier> _log;

    public SilentBeatActionClassifier(
        IChatClient chat,
        IOptions<FilmStudioOptions> opts,
        ILogger<SilentBeatActionClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled =>
        _opts.ClassifySilentBeatsWithChat && _chat.IsConfigured;

    /// <summary>
    /// Mutates silent story beats in <paramref name="stage1"/> (sets <c>action_class</c>).
    /// Dialogue beats are left alone.
    /// </summary>
    public async Task<SilentBeatClassifyResult> ClassifyStage1Async(
        Dictionary<string, object?> stage1,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var model = string.IsNullOrWhiteSpace(_opts.SilentBeatClassifyModel)
            ? DefaultModel
            : _opts.SilentBeatClassifyModel.Trim();
        var temp = _opts.SilentBeatClassifyTemperature;
        if (double.IsNaN(temp) || temp < 0)
            temp = DefaultTemperature;

        var result = new SilentBeatClassifyResult
        {
            PromptVersion = PromptVersion,
            Model = model,
            Temperature = temp,
            Enabled = IsEnabled,
        };

        var targets = CollectSilentBeats(stage1);
        result.SilentBeatCount = targets.Count;
        if (targets.Count == 0)
        {
            result.Note = "no silent beats";
            return result;
        }

        // Always seed heuristic first so every beat has a valid class if AI is skipped
        foreach (var t in targets)
        {
            var h = FountainStage1Importer.InferActionClass(t.VisualEvent, t.IsFirstSilentInScene);
            t.Beat["action_class"] = h;
            t.Heuristic = h;
        }

        if (!IsEnabled)
        {
            result.FallbackCount = targets.Count;
            result.Note = _opts.ClassifySilentBeatsWithChat
                ? "chat not configured — heuristic only"
                : "ClassifySilentBeatsWithChat=false — heuristic only";
            onProgress?.Invoke($"Silent beat classes: heuristic only ({targets.Count})");
            return result;
        }

        onProgress?.Invoke($"Classifying {targets.Count} silent beat(s) for duration (chat)…");

        var byId = targets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var aiLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maxAttempts = Math.Clamp(_opts.SilentBeatClassifyMaxAttempts, 1, 5);
        var totalAttempts = 0;

        // Chunk all silent beats; per chunk: try/retry then leave unlabeled → heuristic
        for (var offset = 0; offset < targets.Count; offset += DefaultBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = targets.Skip(offset).Take(DefaultBatchSize).ToList();
            var missing = chunk.Select(t => t.Id).ToList();

            for (var attempt = 1; attempt <= maxAttempts && missing.Count > 0; attempt++)
            {
                totalAttempts++;
                var batch = missing.Select(id => byId[id]).ToList();
                try
                {
                    onProgress?.Invoke(
                        attempt == 1
                            ? $"  Chat batch {batch.Count} beat(s) ({offset + 1}–{offset + chunk.Count}/{targets.Count})…"
                            : $"  Retry {attempt - 1}/{maxAttempts - 1} for {batch.Count} beat(s)…");

                    var raw = await CallChatAsync(batch, stage1, model, temp, ct).ConfigureAwait(false);
                    result.ChatCalls++;
                    var parsed = ParseLabels(raw);
                    var newly = 0;
                    foreach (var id in missing.ToList())
                    {
                        if (!parsed.TryGetValue(id, out var cls)) continue;
                        aiLabels[id] = cls;
                        newly++;
                    }
                    missing = missing.Where(id => !aiLabels.ContainsKey(id)).ToList();

                    if (missing.Count == 0)
                        break;

                    if (newly == 0)
                    {
                        _log.LogWarning(
                            "SilentBeat classify attempt {Attempt}: no usable labels for {N} beats",
                            attempt, batch.Count);
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }

                    // Partial success: one fill-in pass for remaining ids (counts as an attempt)
                    if (missing.Count > 0 && attempt < maxAttempts)
                    {
                        totalAttempts++;
                        result.ChatCalls++;
                        onProgress?.Invoke($"  Fill-in {missing.Count} missing label(s)…");
                        try
                        {
                            var fillBatch = missing.Select(id => byId[id]).ToList();
                            var fillRaw = await CallChatAsync(fillBatch, stage1, model, temp, ct)
                                .ConfigureAwait(false);
                            foreach (var kv in ParseLabels(fillRaw))
                                aiLabels[kv.Key] = kv.Value;
                            missing = missing.Where(id => !aiLabels.ContainsKey(id)).ToList();
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "SilentBeat fill-in failed");
                            result.LastError = Trim(ex.Message, 240);
                            await BackoffAsync(attempt, ct).ConfigureAwait(false);
                        }
                    }
                    else if (missing.Count > 0)
                    {
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "SilentBeat classify attempt {Attempt} failed", attempt);
                    result.LastError = Trim(ex.Message, 240);
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                }
            }
        }

        result.Attempts = totalAttempts;
        var aiCount = 0;
        var fbCount = 0;
        foreach (var t in targets)
        {
            if (aiLabels.TryGetValue(t.Id, out var cls))
            {
                t.Beat["action_class"] = cls;
                // Establishing often wants a wider scale hint when AI reclassifies
                if (string.Equals(cls, "establishing", StringComparison.OrdinalIgnoreCase))
                    t.Beat["shot_scale_hint"] = "wide";
                else if (t.Beat.TryGetValue("shot_scale_hint", out var sh) &&
                         string.Equals(sh?.ToString(), "wide", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(t.Heuristic, "establishing", StringComparison.OrdinalIgnoreCase))
                    t.Beat["shot_scale_hint"] = "medium";
                aiCount++;
            }
            else
            {
                // Heuristic already written
                fbCount++;
            }
        }

        result.AiCount = aiCount;
        result.FallbackCount = fbCount;
        result.Note = fbCount == 0
            ? $"AI labels {aiCount}/{targets.Count}"
            : $"AI labels {aiCount}/{targets.Count}; heuristic fallback {fbCount}";
        onProgress?.Invoke($"Silent beat classes: {result.Note} (prompt={PromptVersion}, model={model})");
        _log.LogInformation(
            "SilentBeat classify project beats={Total} ai={Ai} fallback={Fb} attempts={Att} model={Model} prompt={Prompt}",
            targets.Count, aiCount, fbCount, totalAttempts, model, PromptVersion);
        return result;
    }

    private async Task<string> CallChatAsync(
        List<SilentTarget> batch,
        Dictionary<string, object?> stage1,
        string model,
        double temperature,
        CancellationToken ct)
    {
        var flat = CollectAllBeatsForNeighbors(stage1);
        // Group once per call instead of re-filtering+sorting the whole book's beat list
        // for every single beat in the batch.
        var byScene = flat
            .GroupBy(x => x.Scene)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.IndexInScene).ToList());
        var payload = batch.Select(b =>
        {
            FlatNeighbor? prev = null, next = null;
            if (byScene.TryGetValue(b.Scene, out var sceneBeats))
            {
                var ix = sceneBeats.FindIndex(x => x.Id == b.Id);
                if (ix > 0) prev = sceneBeats[ix - 1];
                if (ix >= 0 && ix < sceneBeats.Count - 1) next = sceneBeats[ix + 1];
            }
            return new Dictionary<string, object?>
            {
                ["id"] = b.Id,
                ["scene"] = b.Scene,
                ["beat_index"] = b.IndexInScene,
                ["is_first_silent_in_scene"] = b.IsFirstSilentInScene,
                ["setting"] = Trunc(b.Setting, 100),
                ["visual_event"] = Trunc(b.VisualEvent, 280),
                ["prev_beat"] = prev is null ? null : DescribeNeighbor(prev),
                ["next_beat"] = next is null ? null : DescribeNeighbor(next),
            };
        }).ToList();

        var user =
            "Label each silent beat for duration budgeting. Return JSON only.\n\n" +
            JsonSerializer.Serialize(new { beats = payload });

        return await _chat.CompleteAsync(
            SystemPromptV2(),
            user,
            model,
            temperature,
            ct,
            ChatCallModes.SilentBeatClassify).ConfigureAwait(false);
    }

    private async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var baseMs = Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs);
        if (baseMs == 0) return;
        var ms = Math.Min(4000, baseMs * attempt * attempt);
        await Task.Delay(ms, ct).ConfigureAwait(false);
    }

    internal static List<SilentTarget> CollectSilentBeats(Dictionary<string, object?> stage1)
    {
        var list = new List<SilentTarget>();
        var scenes = stage1.TryGetValue("scenes", out var sObj) && sObj is List<object?> sl
            ? sl
            : new List<object?>();
        var sceneIdx = 0;
        foreach (var sItem in scenes)
        {
            if (sItem is not Dictionary<string, object?> scene) continue;
            sceneIdx++;
            var setting = scene.TryGetValue("setting", out var st) ? st?.ToString() ?? "" : "";
            var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl
                ? bl
                : new List<object?>();
            var firstSilent = true;
            var bi = 0;
            foreach (var bItem in beats)
            {
                if (bItem is not Dictionary<string, object?> beat) continue;
                bi++;
                var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString()?.Trim() ?? "" : "";
                var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString()?.Trim() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(dlg) || ve.Length == 0)
                    continue;
                var isFirst = firstSilent;
                firstSilent = false;
                list.Add(new SilentTarget
                {
                    Id = $"s{sceneIdx}_b{bi}",
                    Scene = sceneIdx,
                    IndexInScene = bi,
                    Setting = setting,
                    VisualEvent = ve,
                    IsFirstSilentInScene = isFirst,
                    Beat = beat,
                });
            }
        }
        return list;
    }

    private static List<FlatNeighbor> CollectAllBeatsForNeighbors(Dictionary<string, object?> stage1)
    {
        var list = new List<FlatNeighbor>();
        var scenes = stage1.TryGetValue("scenes", out var sObj) && sObj is List<object?> sl
            ? sl
            : new List<object?>();
        var sceneIdx = 0;
        foreach (var sItem in scenes)
        {
            if (sItem is not Dictionary<string, object?> scene) continue;
            sceneIdx++;
            var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl
                ? bl
                : new List<object?>();
            var bi = 0;
            foreach (var bItem in beats)
            {
                if (bItem is not Dictionary<string, object?> beat) continue;
                bi++;
                var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString()?.Trim() ?? "" : "";
                var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString()?.Trim() ?? "" : "";
                list.Add(new FlatNeighbor(
                    Id: $"s{sceneIdx}_b{bi}",
                    Scene: sceneIdx,
                    IndexInScene: bi,
                    VisualEvent: ve,
                    Dialogue: dlg,
                    IsSilent: string.IsNullOrWhiteSpace(dlg) && ve.Length > 0));
            }
        }
        return list;
    }

    private static object DescribeNeighbor(FlatNeighbor b)
    {
        if (b.IsSilent)
            return new { kind = "silent", visual = Trunc(b.VisualEvent, 120) };
        return new
        {
            kind = "dialogue",
            visual = Trunc(b.VisualEvent, 80),
            dialogue = Trunc(b.Dialogue, 80),
        };
    }

    /// <summary>Public for unit tests and BeatLabelEval alignment.</summary>
    public static string SystemPromptV2() => """
You label silent film beats for DURATION BUDGETING in a video pipeline (any story).
Each label maps to planned clip length — optimize for that, not literary theory.

Classes (pick exactly one):
- establishing: NEW place/room open, first wide setup of a location. NOT every scene's first beat.
  Mid-story business (shopping, sitting at window continuing a prior room, writing a letter) is NOT establishing.
  Duration intent: ~4–5 seconds max.
- hold: micro performance / stillness — smile, look, hands, pause, freeze, short gesture, reaction.
  Duration intent: ~3 seconds.
- action: ordinary physical business (walk, open door, set tray, cross room) without spectacle.
  Duration intent: ~3–5 seconds.
- big_action: chase, fight, crash, leap, climb under danger, vault, violent or high-energy continuous motion.
  Duration intent: longer (up to ~10–12s).

Critical bias correction:
- The FIRST silent beat of a scene is OFTEN action or hold, not establishing.
- Only use establishing when the visual is truly about revealing/establishing a place or setup.
- Prefer hold over action for pure reaction/emotion with little locomotion.
- Prefer big_action only when energy/motion is the point of the shot.

Return JSON only:
{ "labels": [ { "id": "s1_b2", "class": "hold", "reason": "short reaction" } ] }
Use only the four class strings above.
""";

    public static Dictionary<string, string> ParseLabels(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        raw = raw.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            raw = Regex.Replace(raw, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            // Truncate at the closing fence wherever it falls — some models append prose
            // (e.g. a "Reasoning:" section) after the fenced JSON instead of ending on it.
            var fenceEnd = raw.IndexOf("```", StringComparison.Ordinal);
            raw = (fenceEnd >= 0 ? raw[..fenceEnd] : raw).TrimEnd();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.TryGetProperty("labels", out var l))
                arr = l;
            else
                return map;

            foreach (var el in arr.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var cls = el.TryGetProperty("class", out var cEl) ? cEl.GetString()
                    : el.TryGetProperty("action_class", out var aEl) ? aEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) continue;
                var n = NormalizeClass(cls!);
                if (n is null) continue;
                map[id!] = n;
            }
        }
        catch
        {
            // leave empty → retry / fallback
        }
        return map;
    }

    public static string? NormalizeClass(string c)
    {
        c = (c ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
        return c switch
        {
            "establishing" or "hold" or "action" or "big_action" => c,
            "bigaction" => "big_action",
            _ => null,
        };
    }

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    internal sealed class SilentTarget
    {
        public required string Id { get; init; }
        public int Scene { get; init; }
        public int IndexInScene { get; init; }
        public string Setting { get; init; } = "";
        public string VisualEvent { get; init; } = "";
        public bool IsFirstSilentInScene { get; init; }
        public required Dictionary<string, object?> Beat { get; init; }
        public string Heuristic { get; set; } = "";
    }

    private sealed record FlatNeighbor(
        string Id,
        int Scene,
        int IndexInScene,
        string VisualEvent,
        string Dialogue,
        bool IsSilent);
}

public sealed class SilentBeatClassifyResult
{
    public bool Enabled { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public double Temperature { get; set; }
    public int SilentBeatCount { get; set; }
    public int AiCount { get; set; }
    public int FallbackCount { get; set; }
    public int Attempts { get; set; }
    public int ChatCalls { get; set; }
    public string Note { get; set; } = "";
    public string? LastError { get; set; }

    public Dictionary<string, object?> ToMetaDict() => new()
    {
        ["enabled"] = Enabled,
        ["prompt_version"] = PromptVersion,
        ["model"] = Model,
        ["temperature"] = Temperature,
        ["silent_beats"] = SilentBeatCount,
        ["ai_labels"] = AiCount,
        ["heuristic_fallback"] = FallbackCount,
        ["attempts"] = Attempts,
        ["chat_calls"] = ChatCalls,
        ["note"] = Note,
        ["last_error"] = LastError,
    };
}
