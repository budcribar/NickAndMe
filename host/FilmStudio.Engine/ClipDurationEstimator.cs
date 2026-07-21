using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>
/// Cost-aware clip length estimates from dialogue + action (no API calls).
/// Prefer tight clips: pay for speech + short action, not long empty holds.
/// </summary>
public static class ClipDurationEstimator
{
    /// <summary>API / product floor (seconds). Short dialogue can be this tight.</summary>
    public const int MinSeconds = 3;

    /// <summary>Soft max for a single clip (cost + model comfort).</summary>
    public const int MaxSeconds = 10;

    /// <summary>Absolute max if action is huge.</summary>
    public const int AbsMaxSeconds = 12;

    /// <summary>Words per second for spoken dialogue (~150 wpm).</summary>
    public const double DialogueWordsPerSecond = 2.5;

    /// <summary>Pad after speech so the line can land (not multi-second dead air).</summary>
    public const double SpeechTailSeconds = 0.45;

    /// <summary>
    /// Extra headroom under <see cref="MaxSeconds"/> when packing monologue splits so lip-sync
    /// / model end-trim does not cut the last words (speech + visual head still fit).
    /// </summary>
    public const double DialogueModelPaddingSeconds = 1.5;

    /// <summary>Minimum visual beat with no dialogue.</summary>
    public const int ActionOnlyMinSeconds = 3;

    /// <summary>
    /// Estimate duration for a planned beat (Stage 2).
    /// </summary>
    public static int EstimateForBeat(Dictionary<string, object?> beat)
    {
        if (beat is null)
            return MinSeconds;
        var dialogue = Coerce(beat, "dialogue");
        var visual = Coerce(beat, "visual_event");
        var actionClass = Coerce(beat, "action_class").ToLowerInvariant();
        var delivery = Coerce(beat, "delivery").ToLowerInvariant();
        return Estimate(dialogue, visual, actionClass, delivery);
    }

    /// <summary>
    /// Estimate from a blueprint clip element at gen time.
    /// </summary>
    public static int EstimateForClip(JsonElement clipEl)
    {
        if (clipEl.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return MinSeconds;
        var dialogue = "";
        var delivery = "none";
        if (clipEl.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object)
        {
            if (ap.TryGetProperty("dialogue", out var d))
                dialogue = d.GetString() ?? "";
            if (ap.TryGetProperty("delivery", out var del))
                delivery = (del.GetString() ?? "none").ToLowerInvariant();
        }

        var visual = clipEl.TryGetProperty("visual_prompt", out var vp)
            ? vp.GetString() ?? ""
            : "";
        var planned = 0;
        if (clipEl.TryGetProperty("duration_seconds", out var ds))
        {
            if (ds.TryGetInt32(out var p))
                planned = p;
            else if (ds.TryGetDouble(out var d) && d > 0)
                planned = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            else if (ds.ValueKind == JsonValueKind.String &&
                     double.TryParse(ds.GetString(), System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0)
                planned = (int)Math.Round(s, MidpointRounding.AwayFromZero);
        }

        var est = Estimate(dialogue, visual, actionClass: "", delivery);
        // Prefer estimator; never use a planned value that is much longer without dialogue
        if (planned > 0 && string.IsNullOrWhiteSpace(dialogue))
            return Math.Clamp(Math.Min(planned, est + 2), MinSeconds, MaxSeconds);
        if (planned > 0 && !string.IsNullOrWhiteSpace(dialogue))
            // Cap over-planned dialogue clips
            return Math.Clamp(Math.Min(planned, Math.Max(est, MinSeconds)), MinSeconds, MaxSeconds);
        return est;
    }

    public static int Estimate(
        string? dialogue,
        string? visualOrAction,
        string actionClass = "",
        string delivery = "none")
    {
        var dlg = (dialogue ?? "").Trim();
        var visual = (visualOrAction ?? "").Trim();
        // Callers may pass mixed-case labels from JSON / blueprints
        actionClass = (actionClass ?? "").Trim().ToLowerInvariant();
        delivery = (delivery ?? "none").Trim().ToLowerInvariant();
        if (delivery.Length == 0) delivery = "none";

        double speech = 0;
        if (dlg.Length > 0)
        {
            var words = CountWords(dlg);
            speech = words / DialogueWordsPerSecond + SpeechTailSeconds;
            // Very short lines still need a beat
            speech = Math.Max(1.6, speech);
            // VO can be slightly snappier
            if (delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought")
                speech *= 0.95;
        }

        double action = 0;
        if (dlg.Length == 0)
        {
            var aw = CountWords(visual);
            action = actionClass switch
            {
                "big_action" => Math.Clamp(4.5 + aw / 8.0, 5, AbsMaxSeconds),
                "establishing" => Math.Clamp(3.5 + aw / 10.0, ActionOnlyMinSeconds, 8),
                "hold" => ActionOnlyMinSeconds,
                _ => Math.Clamp(3.0 + aw / 12.0, ActionOnlyMinSeconds, 8),
            };
        }
        else
        {
            // Dialogue clip: short visual head only (lip-sync / reaction under the line)
            action = actionClass is "big_action" ? 1.2 : 0.6;
        }

        var total = speech + action;
        if (total <= 0)
            total = ActionOnlyMinSeconds;

        var rounded = (int)Math.Round(total, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, MinSeconds, MaxSeconds);
    }

    /// <summary>
    /// Same as <see cref="Estimate"/> but without the model max clamp — used to detect
    /// monologues that would be crushed into <see cref="MaxSeconds"/>.
    /// </summary>
    public static double EstimateUncapped(
        string? dialogue,
        string? visualOrAction,
        string actionClass = "",
        string delivery = "none")
    {
        var dlg = (dialogue ?? "").Trim();
        var visual = (visualOrAction ?? "").Trim();
        actionClass = (actionClass ?? "").Trim().ToLowerInvariant();
        delivery = (delivery ?? "none").Trim().ToLowerInvariant();
        if (delivery.Length == 0) delivery = "none";

        double speech = 0;
        if (dlg.Length > 0)
        {
            var words = CountWords(dlg);
            speech = words / DialogueWordsPerSecond + SpeechTailSeconds;
            speech = Math.Max(1.6, speech);
            if (delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought")
                speech *= 0.95;
        }

        double action = 0;
        if (dlg.Length == 0)
        {
            var aw = CountWords(visual);
            action = actionClass switch
            {
                "big_action" => Math.Clamp(4.5 + aw / 8.0, 5, AbsMaxSeconds),
                "establishing" => Math.Clamp(3.5 + aw / 10.0, ActionOnlyMinSeconds, 8),
                "hold" => ActionOnlyMinSeconds,
                _ => Math.Clamp(3.0 + aw / 12.0, ActionOnlyMinSeconds, 8),
            };
        }
        else
        {
            action = actionClass is "big_action" ? 1.2 : 0.6;
        }

        var total = speech + action;
        if (total <= 0)
            total = ActionOnlyMinSeconds;
        return total;
    }

    /// <summary>
    /// True when spoken dialogue (+ visual head) needs more time than the model clip max
    /// after packing padding — monologue should be split into multiple beats/clips.
    /// </summary>
    public static bool DialogueExceedsModelMax(
        string? dialogue,
        string delivery = "spoken_on_camera",
        int modelMaxSeconds = MaxSeconds,
        double paddingSeconds = DialogueModelPaddingSeconds)
    {
        if (string.IsNullOrWhiteSpace(dialogue))
            return false;
        var budget = Math.Max(MinSeconds, modelMaxSeconds - paddingSeconds);
        var need = EstimateUncapped(dialogue, visualOrAction: "", actionClass: "dialogue", delivery: delivery);
        return need > budget + 0.01;
    }

    /// <summary>
    /// Split dialogue so each piece fits under the model max with padding.
    /// Prefers sentence / clause boundaries; falls back to word packs for run-ons.
    /// </summary>
    public static IReadOnlyList<string> SplitDialogueToFitModelMax(
        string? dialogue,
        string delivery = "spoken_on_camera",
        int modelMaxSeconds = MaxSeconds,
        double paddingSeconds = DialogueModelPaddingSeconds)
    {
        var text = (dialogue ?? "").Trim();
        if (text.Length == 0)
            return Array.Empty<string>();
        if (!DialogueExceedsModelMax(text, delivery, modelMaxSeconds, paddingSeconds))
            return new[] { text };

        var budget = Math.Max(MinSeconds, modelMaxSeconds - paddingSeconds);
        var units = SegmentDialogueUnits(text);
        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            var s = current.ToString().Trim();
            current.Clear();
            if (s.Length > 0)
                chunks.Add(s);
        }

        foreach (var unit in units)
        {
            var u = unit.Trim();
            if (u.Length == 0) continue;

            // Unit alone still too long → pack by words
            if (EstimateUncapped(u, "", "dialogue", delivery) > budget)
            {
                Flush();
                foreach (var piece in PackByWords(u, delivery, budget))
                    chunks.Add(piece);
                continue;
            }

            var trial = current.Length == 0 ? u : current + " " + u;
            if (current.Length > 0 &&
                EstimateUncapped(trial, "", "dialogue", delivery) > budget)
            {
                Flush();
                current.Append(u);
            }
            else
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(u);
            }
        }

        Flush();
        return chunks.Count > 0 ? chunks : new[] { text };
    }

    /// <summary>
    /// Expand story beats: long monologue / dialogue becomes multiple beats that each fit
    /// the video model max. Action beats and short lines pass through. Reassigns beat_id
    /// to sequential b1, b2, … and keeps speaker/delivery/audio in sync.
    /// </summary>
    public static List<Dictionary<string, object?>> ExpandLongDialogueBeats(
        IReadOnlyList<Dictionary<string, object?>>? beats,
        int modelMaxSeconds = MaxSeconds,
        double paddingSeconds = DialogueModelPaddingSeconds)
    {
        var result = new List<Dictionary<string, object?>>();
        if (beats is null || beats.Count == 0)
            return result;

        var nextId = 1;
        foreach (var beat in beats)
        {
            if (beat is null)
                continue;

            var dialogue = Coerce(beat, "dialogue");
            if (string.IsNullOrWhiteSpace(dialogue) &&
                beat.TryGetValue("audio", out var a0) && a0 is Dictionary<string, object?> audio0)
                dialogue = Coerce(audio0, "dialogue");

            var delivery = Coerce(beat, "delivery");
            if (string.IsNullOrWhiteSpace(delivery) &&
                beat.TryGetValue("audio", out var a1) && a1 is Dictionary<string, object?> audio1)
                delivery = Coerce(audio1, "delivery");
            if (string.IsNullOrWhiteSpace(delivery))
                delivery = "spoken_on_camera";

            if (string.IsNullOrWhiteSpace(dialogue) ||
                !DialogueExceedsModelMax(dialogue, delivery, modelMaxSeconds, paddingSeconds))
            {
                result.Add(CloneBeatWithId(beat, $"b{nextId++}", dialogueOverride: null, partIndex: 0, partCount: 1));
                continue;
            }

            var parts = SplitDialogueToFitModelMax(dialogue, delivery, modelMaxSeconds, paddingSeconds);
            for (var p = 0; p < parts.Count; p++)
            {
                result.Add(CloneBeatWithId(beat, $"b{nextId++}", parts[p], p, parts.Count));
            }
        }

        return result;
    }

    /// <summary>
    /// Allocate one duration per beat from content (not forced scene-budget padding).
    /// Optional scene target only gently stretches pure action clips, never dialogue.
    /// </summary>
    public static List<int> AllocateForBeats(
        IReadOnlyList<Dictionary<string, object?>> beats,
        int? sceneTargetSeconds = null)
    {
        if (beats is null || beats.Count == 0)
            return new List<int>();
        // Null list entries are treated as empty action beats (not skipped — preserve index alignment)
        var durs = beats.Select(b => EstimateForBeat(b!)).ToList();
        if (durs.Count == 0) return durs;

        if (sceneTargetSeconds is int target && target > durs.Sum() + 2)
        {
            // Only pad action-only clips toward target (avoid charging for empty speech tails)
            var need = target - durs.Sum();
            var actionIdx = new List<int>();
            for (var i = 0; i < beats.Count; i++)
            {
                var dlg = beats[i] is null ? "" : Coerce(beats[i]!, "dialogue");
                if (string.IsNullOrWhiteSpace(dlg) && durs[i] < MaxSeconds)
                    actionIdx.Add(i);
            }
            var guard = 0;
            while (need > 0 && actionIdx.Count > 0 && guard++ < 50)
            {
                foreach (var i in actionIdx)
                {
                    if (need <= 0) break;
                    if (durs[i] >= MaxSeconds) continue;
                    durs[i]++;
                    need--;
                }
                if (actionIdx.All(i => durs[i] >= MaxSeconds)) break;
            }
        }

        return durs;
    }

    public static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Regex.Matches(text, @"[\p{L}\p{N}']+").Count;
    }

    /// <summary>
    /// Sentence / clause units for packing (keeps trailing punctuation on the unit).
    /// </summary>
    internal static List<string> SegmentDialogueUnits(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        if (text.Length == 0)
            return new List<string>();

        // Split after . ! ? or ; or em/en dash phrases, keep delimiter on left piece.
        var parts = Regex.Split(
            text,
            @"(?<=[.!?;])\s+|(?<=\u2014|\u2013|--)\s+");
        var list = parts
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return list.Count > 0 ? list : new List<string> { text };
    }

    private static List<string> PackByWords(string text, string delivery, double budgetSeconds)
    {
        var words = Regex.Matches(text, @"[\p{L}\p{N}']+|[^\s\p{L}\p{N}]+")
            .Select(m => m.Value)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();
        if (words.Count == 0)
            return new List<string> { text.Trim() };

        var chunks = new List<string>();
        var current = new List<string>();

        string Join(List<string> ws)
        {
            var sb = new StringBuilder();
            foreach (var w in ws)
            {
                if (sb.Length == 0)
                {
                    sb.Append(w);
                    continue;
                }
                // No space before trailing punctuation-only tokens
                if (Regex.IsMatch(w, @"^[.!?;,:]+$"))
                    sb.Append(w);
                else
                    sb.Append(' ').Append(w);
            }
            return sb.ToString().Trim();
        }

        foreach (var w in words)
        {
            current.Add(w);
            var trial = Join(current);
            if (EstimateUncapped(trial, "", "dialogue", delivery) > budgetSeconds && current.Count > 1)
            {
                current.RemoveAt(current.Count - 1);
                chunks.Add(Join(current));
                current.Clear();
                current.Add(w);
            }
        }

        if (current.Count > 0)
            chunks.Add(Join(current));
        return chunks;
    }

    private static Dictionary<string, object?> CloneBeatWithId(
        Dictionary<string, object?> source,
        string beatId,
        string? dialogueOverride,
        int partIndex,
        int partCount)
    {
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in source)
        {
            if (v is Dictionary<string, object?> nested)
                copy[k] = new Dictionary<string, object?>(nested, StringComparer.OrdinalIgnoreCase);
            else if (v is List<object?> list)
                copy[k] = list.ToList();
            else
                copy[k] = v;
        }

        copy["beat_id"] = beatId;
        if (dialogueOverride is not null)
        {
            copy["dialogue"] = dialogueOverride;
            if (copy.TryGetValue("audio", out var a) && a is Dictionary<string, object?> audio)
                audio["dialogue"] = dialogueOverride;

            var words = CountWords(dialogueOverride);
            copy["time_weight"] = Math.Clamp(words / 8.0, 0.5, 4.0);

            if (partCount > 1)
            {
                if (partIndex > 0)
                    copy["continuity"] = "continuous_from_previous_beat";
                // Keep visual_event stable (e.g. "NARRATOR speaks.") for monologue parts
            }
        }

        return copy;
    }

    private static string Coerce(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString()?.Trim() ?? "" : "";
}
