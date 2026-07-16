using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>Coerce Stage 1 JSON after LLM generation (schema cleanup).</summary>
public static class Stage1Normalizer
{
    private static readonly Dictionary<string, string> LocTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["interior"] = "int", ["int"] = "int", ["interior_only"] = "int",
        ["exterior"] = "ext", ["ext"] = "ext", ["exterior_only"] = "ext",
        ["mixed"] = "mixed", ["interior_exterior_mix"] = "mixed",
        ["interior/exterior"] = "mixed", ["int/ext"] = "mixed",
        ["montage_mix"] = "montage", ["montage"] = "montage",
        ["flashback"] = "flashback", ["dream"] = "dream", ["dreamscape"] = "dream",
    };

    public static Dictionary<string, object?> Normalize(Dictionary<string, object?> data)
    {
        data = CleanNulls(data) as Dictionary<string, object?> ?? new();
        data["schema_version"] = "stage1.v1";
        data.TryGetValue("source_book_title", out var sbt);
        data.TryGetValue("movie_title", out var mt);
        data["movie_title"] = CoerceString(mt) is { Length: > 0 } mts ? mts : (CoerceString(sbt) ?? "Untitled");
        data["source_book_title"] = CoerceString(sbt) is { Length: > 0 } sbs ? sbs : data["movie_title"];

        var gpv = GetOrCreateDict(data, "global_production_variables");
        gpv["target_aspect_ratio"] = gpv.TryGetValue("target_aspect_ratio", out var ar) && ar is not null
            ? ar : "16:9";
        gpv["resolution"] = gpv.TryGetValue("resolution", out var res) && res is not null ? res : "720p";
        gpv["frame_rate"] = ParseFrameRate(gpv.TryGetValue("frame_rate", out var fr) ? fr : 24);
        if (!gpv.ContainsKey("directorial_treatment") || gpv["directorial_treatment"] is null)
            gpv["directorial_treatment"] = "cinematic lighting, film grain, steady camera, high-contrast";

        var scenes = GetList(data, "scenes");
        if (!gpv.ContainsKey("total_runtime_target_seconds") || gpv["total_runtime_target_seconds"] is null)
        {
            var sceneSum = 0;
            foreach (var s in scenes.OfType<Dictionary<string, object?>>())
                sceneSum += ToInt(s.TryGetValue("duration_target_seconds", out var d) ? d : 0);
            gpv["total_runtime_target_seconds"] = sceneSum > 0 ? sceneSum : 900;
        }

        if (!gpv.ContainsKey("character_seed_tokens") || gpv["character_seed_tokens"] is null)
            gpv["character_seed_tokens"] = new Dictionary<string, object?>();
        if (!gpv.ContainsKey("location_seed_tokens") || gpv["location_seed_tokens"] is null)
            gpv["location_seed_tokens"] = new Dictionary<string, object?>();

        var treat = CoerceString(gpv.TryGetValue("directorial_treatment", out var dt) ? dt : "") ?? "";
        var rsl = CoerceString(
            gpv.TryGetValue("render_style_lock", out var r1) ? r1
            : gpv.TryGetValue("style_lock", out var r2) ? r2 : null) ?? "";
        if (string.IsNullOrWhiteSpace(rsl) &&
            Regex.IsMatch(treat, @"styliz|animated|picture-book|cartoon|pixar|dreamworks|illustration|2d\b|3d\b",
                RegexOptions.IgnoreCase))
        {
            gpv["render_style_lock"] =
                "STYLE LOCK: stylized animated children's picture-book look for ALL on-screen " +
                "cast (animals and humans share the same medium) -- not photoreal, not live-action";
        }
        else if (!string.IsNullOrWhiteSpace(rsl))
        {
            gpv["render_style_lock"] = rsl;
        }

        NormalizeCharacterSeeds(gpv);
        NormalizeLocationSeeds(gpv);

        foreach (var sObj in scenes.OfType<Dictionary<string, object?>>())
            NormalizeScene(sObj);

        var total = scenes.OfType<Dictionary<string, object?>>()
            .Sum(s => ToInt(s.TryGetValue("duration_target_seconds", out var d) ? d : 0));
        data["cumulative_duration_target_seconds"] = total;
        data["scenes"] = scenes;
        data["global_production_variables"] = gpv;
        return data;
    }

    private static void NormalizeCharacterSeeds(Dictionary<string, object?> gpv)
    {
        var seeds = GetDict(gpv, "character_seed_tokens");
        foreach (var (key, val) in seeds.ToList())
        {
            if (val is not Dictionary<string, object?> seed) continue;

            // Filmable identity only — strip book nicknames ("noodle-head") from visual prose
            var rawDesc = CoerceString(seed.TryGetValue("description", out var d) ? d : null) ?? key;
            seed["description"] = CharacterVisualTextScrubber.ScrubVisualProse(rawDesc);
            if (string.IsNullOrWhiteSpace(CoerceString(seed["description"])))
                seed["description"] = key;

            seed["reference_image_placeholder"] =
                CoerceString(seed.TryGetValue("reference_image_placeholder", out var ph) ? ph : null)
                ?? ProjectStore.CharacterRefFileName(key);
            seed["voice_profile"] =
                CoerceString(seed.TryGetValue("voice_profile", out var vp) ? vp : null)
                ?? "Consistent character voice every scene.";
            seed["voice_label"] =
                CoerceString(seed.TryGetValue("voice_label", out var vl) ? vl : null) ?? key;

            var pol = (CoerceString(seed.TryGetValue("display_name_policy", out var polV) ? polV : null) ?? "")
                .ToLowerInvariant();
            var isVoiceOnly = pol.Contains("never") ||
                              key.EndsWith("_Narrator", StringComparison.OrdinalIgnoreCase) ||
                              key.Equals("Character_Narrator", StringComparison.OrdinalIgnoreCase) ||
                              key.Contains("narrator", StringComparison.OrdinalIgnoreCase);
            if (isVoiceOnly)
            {
                seed.Remove("visual_lock");
                seed.Remove("wardrobe_always");
            }
            else
            {
                var vlck = CoerceString(seed.TryGetValue("visual_lock", out var v) ? v : null);
                if (string.IsNullOrWhiteSpace(vlck))
                {
                    var desc = CoerceString(seed["description"]) ?? key;
                    seed["visual_lock"] = desc.Length > 220 ? desc[..220] + "…" : desc;
                }
                else
                {
                    seed["visual_lock"] = CharacterVisualTextScrubber.ScrubVisualProse(vlck);
                }

                var always = CharacterVisualTextScrubber.ScrubWardrobeList(
                    CoerceStringList(seed.TryGetValue("wardrobe_always", out var wa) ? wa : null));
                if (always.Count > 0)
                    seed["wardrobe_always"] = always;
                else
                    seed.Remove("wardrobe_always");
            }
            seeds[key] = seed;
        }
        gpv["character_seed_tokens"] = seeds;
    }

    private static void NormalizeLocationSeeds(Dictionary<string, object?> gpv)
    {
        var seeds = GetDict(gpv, "location_seed_tokens");
        foreach (var (key, val) in seeds.ToList())
        {
            if (val is not Dictionary<string, object?> seed) continue;
            seed["display_name"] =
                CoerceString(seed.TryGetValue("display_name", out var dn) ? dn : null) ?? key;
            seed["description"] =
                CoerceString(seed.TryGetValue("description", out var d) ? d : null)
                ?? seed["display_name"];
            seed["visual_lock"] =
                CoerceString(seed.TryGetValue("visual_lock", out var v) ? v : null)
                ?? seed["description"];
            seeds[key] = seed;
        }
        gpv["location_seed_tokens"] = seeds;
    }

    private static void NormalizeScene(Dictionary<string, object?> s)
    {
        var sn = ToInt(s.TryGetValue("scene_number", out var n) ? n : 0);
        s["scene_filename"] =
            CoerceString(s.TryGetValue("scene_filename", out var sf) ? sf : null)
            ?? $"Scene_{sn:D2}";
        s["setting"] = CoerceString(s.TryGetValue("setting", out var set) ? set : null) ?? "";
        s["story_day"] = NormStoryDay(s.TryGetValue("story_day", out var sd) ? sd : null);
        s["location_type"] = NormLocationType(s.TryGetValue("location_type", out var lt) ? lt : null);
        var dur = ToInt(s.TryGetValue("duration_target_seconds", out var d) ? d : 24);
        s["duration_target_seconds"] = Math.Clamp(dur <= 0 ? 24 : dur, 8, 134);
        s["dramatic_function"] =
            CoerceString(s.TryGetValue("dramatic_function", out var df) ? df : null) ?? "";
        s["summary"] =
            CoerceString(s.TryGetValue("summary", out var sum) ? sum : null)
            ?? (string.IsNullOrEmpty(s["setting"] as string) ? $"Scene {sn}" : (string)s["setting"]!);
        s["transition_type"] =
            CoerceString(s.TryGetValue("transition_type", out var tt) ? tt : null) ?? "cut";
        s["lighting_continuity_token"] =
            CoerceString(s.TryGetValue("lighting_continuity_token", out var lc) ? lc : null)
            ?? "consistent scene lighting";
        if (!s.ContainsKey("story_beats") || s["story_beats"] is null)
            s["story_beats"] = new List<object?>();
        s["music_intent"] = NormMusic(s.TryGetValue("music_intent", out var mi) ? mi : null);

        var lids = s.TryGetValue("location_ids", out var lidsRaw) ? lidsRaw : null;
        var lidList = new List<string>();
        if (lids is string one) lidList.Add(one);
        else if (lids is List<object?> list)
            lidList.AddRange(list.Select(x => CoerceString(x)).Where(x => !string.IsNullOrEmpty(x))!);
        s["location_ids"] = lidList;
        if (lidList.Count > 0 &&
            string.IsNullOrEmpty(CoerceString(s.TryGetValue("primary_location_id", out var pl) ? pl : null)))
            s["primary_location_id"] = lidList[0];

        // Scrub nickname junk from scene wardrobe maps
        if (s.TryGetValue("wardrobe_by_character", out var wbc) &&
            wbc is Dictionary<string, object?> wmap)
        {
            foreach (var (ck, items) in wmap.ToList())
            {
                var cleaned = CharacterVisualTextScrubber.ScrubWardrobeList(CoerceStringList(items));
                if (cleaned.Count > 0)
                    wmap[ck] = cleaned;
                else
                    wmap.Remove(ck);
            }
            s["wardrobe_by_character"] = wmap;
        }

        foreach (var b in GetList(s, "story_beats").OfType<Dictionary<string, object?>>())
        {
            b["beat_id"] = CoerceString(b.TryGetValue("beat_id", out var bi) ? bi : null) ?? "b1";
            b["intent"] = CoerceString(b.TryGetValue("intent", out var intent) ? intent : null) ?? "";
            b["visual_event"] =
                CoerceString(b.TryGetValue("visual_event", out var ve) ? ve : null)
                ?? (string)b["intent"]!;
            // visual_event may mention nicknames from the book VO — only scrub clear food-hat junk
            if (b["visual_event"] is string veStr &&
                CharacterVisualTextScrubber.LooksLikeNicknameVisualJunk(veStr))
                b["visual_event"] = CharacterVisualTextScrubber.ScrubVisualProse(veStr);
            b["shot_scale_hint"] =
                CoerceString(b.TryGetValue("shot_scale_hint", out var ssh) ? ssh : null) ?? "ms";
            b["continuity"] =
                CoerceString(b.TryGetValue("continuity", out var c) ? c : null) ?? "new_setup";
            foreach (var leak in new[] { "visual_prompt", "negative_prompt", "timestamp", "veo_continuation_source" })
                b.Remove(leak);

            foreach (var wkey in new[] { "wardrobe_put_on", "wardrobe_remove" })
            {
                if (!b.TryGetValue(wkey, out var wraw)) continue;
                var cleaned = CharacterVisualTextScrubber.ScrubWardrobeList(CoerceStringList(wraw));
                if (cleaned.Count > 0)
                    b[wkey] = cleaned;
                else
                    b.Remove(wkey);
            }
        }
    }

    private static string NormLocationType(object? v)
    {
        var s = (CoerceString(v) ?? "mixed").Trim().ToLowerInvariant().Replace(' ', '_');
        if (LocTypeMap.TryGetValue(s, out var mapped)) return mapped;
        if (s.Contains("flash")) return "flashback";
        if (s.Contains("dream")) return "dream";
        if (s.Contains("montage")) return "montage";
        if (s.Contains("ext") && s.Contains("int")) return "mixed";
        if (s.StartsWith("ext")) return "ext";
        if (s.StartsWith("int")) return "int";
        return "mixed";
    }

    private static string NormStoryDay(object? v)
    {
        if (v is null) return "unspecified";
        if (v is long or int or double)
        {
            var n = Convert.ToInt32(v);
            return n <= 0 ? "Flashback / unspecified day" : $"Day {n}";
        }
        var s = CoerceString(v);
        return string.IsNullOrWhiteSpace(s) ? "unspecified" : s!;
    }

    private static Dictionary<string, object?> NormMusic(object? raw)
    {
        if (raw is not Dictionary<string, object?> d)
            return new Dictionary<string, object?> { ["style_description"] = "cinematic underscore" };
        if (string.IsNullOrWhiteSpace(CoerceString(d.TryGetValue("style_description", out var sd) ? sd : null)))
        {
            d["style_description"] =
                CoerceString(d.TryGetValue("style", out var st) ? st : null)
                ?? CoerceString(d.TryGetValue("description", out var desc) ? desc : null)
                ?? CoerceString(d.TryGetValue("genre", out var g) ? g : null)
                ?? "cinematic underscore";
        }
        return d;
    }

    public static List<string> CoerceStringList(object? val, int maxItems = 12)
    {
        var raw = new List<string>();
        if (val is null) return raw;
        if (val is string s)
        {
            raw.AddRange(Regex.Split(s, @"\s+and\s+|[,;|/]")
                .Select(p => p.Trim())
                .Where(p => p.Length > 0));
        }
        else if (val is List<object?> list)
        {
            foreach (var x in list)
            {
                var t = CoerceString(x);
                if (!string.IsNullOrWhiteSpace(t)) raw.Add(t!);
            }
        }
        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw)
        {
            var cleaned = Regex.Replace(item, @"\s+", " ").Trim(' ', '.', ',', ';', ':');
            if (cleaned.Length < 2) continue;
            if (!seen.Add(cleaned.ToLowerInvariant())) continue;
            outList.Add(cleaned.Length > 80 ? cleaned[..80] : cleaned);
            if (outList.Count >= maxItems) break;
        }
        return outList;
    }

    private static object? CleanNulls(object? obj)
    {
        if (obj is Dictionary<string, object?> d)
        {
            var n = new Dictionary<string, object?>();
            foreach (var (k, v) in d)
            {
                if (v is null) continue;
                n[k] = CleanNulls(v);
            }
            return n;
        }
        if (obj is List<object?> list)
            return list.Select(CleanNulls).ToList();
        return obj;
    }

    private static Dictionary<string, object?> GetOrCreateDict(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v is Dictionary<string, object?> existing)
            return existing;
        var n = new Dictionary<string, object?>();
        d[key] = n;
        return n;
    }

    private static Dictionary<string, object?> GetDict(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v is Dictionary<string, object?> existing)
            return existing;
        var n = new Dictionary<string, object?>();
        d[key] = n;
        return n;
    }

    private static List<object?> GetList(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v is List<object?> list) return list;
        var n = new List<object?>();
        d[key] = n;
        return n;
    }

    private static int ParseFrameRate(object? fr)
    {
        if (fr is int i) return i;
        if (fr is long l) return (int)l;
        if (fr is double db) return (int)db;
        var s = CoerceString(fr) ?? "24";
        var m = Regex.Match(s, @"\d+");
        return m.Success ? int.Parse(m.Value) : 24;
    }

    private static int ToInt(object? v)
    {
        try
        {
            return v switch
            {
                null => 0,
                int i => i,
                long l => (int)l,
                double d => (int)d,
                string s when int.TryParse(s, out var n) => n,
                _ => Convert.ToInt32(v),
            };
        }
        catch { return 0; }
    }

    private static string? CoerceString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        _ => v.ToString(),
    };
}
