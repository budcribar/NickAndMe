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
    /// Allocate one duration per beat from content (not forced scene-budget padding).
    /// Optional scene target only gently stretches pure action clips, never dialogue.
    /// </summary>
    public static List<int> AllocateForBeats(
        IReadOnlyList<Dictionary<string, object?>> beats,
        int? sceneTargetSeconds = null)
    {
        if (beats is null || beats.Count == 0)
            return new List<int>();
        var durs = beats.Select(EstimateForBeat).ToList();
        if (durs.Count == 0) return durs;

        if (sceneTargetSeconds is int target && target > durs.Sum() + 2)
        {
            // Only pad action-only clips toward target (avoid charging for empty speech tails)
            var need = target - durs.Sum();
            var actionIdx = new List<int>();
            for (var i = 0; i < beats.Count; i++)
            {
                var dlg = Coerce(beats[i], "dialogue");
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

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Regex.Matches(text, @"[\p{L}\p{N}']+").Count;
    }

    private static string Coerce(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString()?.Trim() ?? "" : "";
}
