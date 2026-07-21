using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmStudio.Engine;

/// <summary>
/// Parse Fountain into an in-memory screenplay model (beats, cast, locations)
/// used by Stage 2 and cast tooling. Does not write scenes.json — Stage 2 reads Fountain.
/// </summary>
public static class FountainStage1Importer
{
    public sealed class ImportResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? OutPath { get; init; }
        public string? FountainSavedPath { get; init; }
        public int SceneCount { get; init; }
        public int CharacterCount { get; init; }
        public int LocationCount { get; init; }
        public string? Title { get; init; }
    }

    /// <summary>
    /// Save canonical Fountain draft only (no scenes.json). Prefer
    /// <see cref="ScreenplayService.ImportAsDraft"/> / <see cref="ScreenplayService.SignOff"/>.
    /// </summary>
    public static ImportResult ImportToProject(
        ProjectStore projects,
        string projectId,
        string fountainText,
        string? originalFileName = null)
    {
        if (string.IsNullOrWhiteSpace(fountainText))
            return new ImportResult { Ok = false, Error = "Empty Fountain text" };

        var parsed = FountainParser.Parse(fountainText);
        var doc = Stage1Normalizer.Normalize(BuildStage1(parsed));

        var projectDir = projects.GetProjectDir(projectId);
        var sourceDir = Path.Combine(projectDir, "source");
        Directory.CreateDirectory(sourceDir);

        var normalized = fountainText.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.EndsWith('\n')) normalized += "\n";
        var fountainPath = Path.Combine(sourceDir, ScreenplayService.CanonicalFileName);
        File.WriteAllText(fountainPath, normalized);

        if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            var safeName = Path.GetFileName(originalFileName);
            if (!string.IsNullOrWhiteSpace(safeName) &&
                !safeName.Equals(ScreenplayService.CanonicalFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (!safeName.EndsWith(".fountain", StringComparison.OrdinalIgnoreCase) &&
                    !safeName.EndsWith(".spmd", StringComparison.OrdinalIgnoreCase))
                    safeName = Path.GetFileNameWithoutExtension(safeName) + ".fountain";
                try { File.WriteAllText(Path.Combine(sourceDir, safeName), normalized); } catch { /* ignore */ }
            }
        }

        projects.InvalidateSceneListCache(projectId);

        var gpv = doc["global_production_variables"] as Dictionary<string, object?>;
        var chars = gpv?["character_seed_tokens"] as Dictionary<string, object?>;
        var locs = gpv?["location_seed_tokens"] as Dictionary<string, object?>;
        var scenes = doc["scenes"] as List<object?>;

        return new ImportResult
        {
            Ok = true,
            OutPath = fountainPath,
            FountainSavedPath = fountainPath,
            SceneCount = scenes?.Count ?? 0,
            CharacterCount = chars?.Count ?? 0,
            LocationCount = locs?.Count ?? 0,
            Title = doc.TryGetValue("movie_title", out var t) ? t?.ToString() : null,
        };
    }

    public static Dictionary<string, object?> BuildStage1(FountainParser.ParseResult parsed)
    {
        var title = FirstTitle(parsed, "Title") ?? FirstTitle(parsed, "title") ?? "Untitled";
        title = CleanEmphasis(title).Replace("\n", " ").Trim();
        if (title.Length == 0) title = "Untitled";

        var author = FirstTitle(parsed, "Author") ?? FirstTitle(parsed, "Authors");

        var scenes = new List<object?>();
        var charSeeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var locSeeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, object?>? curScene = null;
        List<object?>? beats = null;
        string? pendingChar = null;
        string? pendingParen = null;
        var actionBuf = new StringBuilder();
        var dialogueBuf = new StringBuilder();
        var beatIndex = 0;
        var sceneNum = 0;

        void FlushAction()
        {
            if (actionBuf.Length == 0 || beats is null) return;
            var text = actionBuf.ToString().Trim();
            actionBuf.Clear();
            if (text.Length == 0) return;
            // Pure transitions are not filmable beats (would become empty clips)
            if (FountainParser.IsStandaloneTransitionLine(text) || IsNoopTransitionText(text))
                return;
            beatIndex++;
            var (ambient, sfx) = InferAmbientAndSfx(text);
            beats.Add(new Dictionary<string, object?>
            {
                ["beat_id"] = $"b{beatIndex}",
                ["intent"] = Trunc(text, 120),
                ["visual_event"] = text,
                ["shot_scale_hint"] = "medium",
                ["action_class"] = "action",
                ["continuity"] = beatIndex == 1 ? "new_setup" : "continuous_from_previous_beat",
                ["time_weight"] = Math.Clamp(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 12.0, 0.5, 3.0),
                ["delivery"] = "none",
                ["speaker"] = "",
                ["dialogue"] = "",
                ["ambient"] = ambient,
                ["sfx"] = sfx,
                ["audio"] = new Dictionary<string, object?>
                {
                    ["delivery"] = "none",
                    ["speaker"] = "",
                    ["dialogue"] = "",
                    ["ambient"] = ambient,
                    ["sfx"] = sfx,
                },
                ["characters_on_screen"] = CurrentOnScreen(curScene),
            });
        }

        void FlushDialogue()
        {
            if (pendingChar is null || dialogueBuf.Length == 0 || beats is null) return;
            var text = dialogueBuf.ToString().Trim();
            dialogueBuf.Clear();
            if (text.Length == 0) { pendingParen = null; return; }

            var charKey = EnsureCharacter(charSeeds, pendingChar);
            EnsureOnScreen(curScene, charKey);
            var visual = string.IsNullOrWhiteSpace(pendingParen)
                ? $"{pendingChar} speaks."
                : $"{pendingChar} ({pendingParen}).";
            var delivery = IsOffScreen(pendingChar) ? "voiceover_internal" : "spoken_on_camera";
            // Parenthetical may carry light sfx ("whispering", rare); usually empty for dialogue
            var (_, parenSfx) = InferAmbientAndSfx(pendingParen ?? "");

            // Long monologues → multiple beats so each clip fits the video model max
            var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(text, delivery);
            if (parts.Count == 0)
                parts = new[] { text };

            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                beatIndex++;
                var isFirst = beatIndex == 1;
                beats.Add(new Dictionary<string, object?>
                {
                    ["beat_id"] = $"b{beatIndex}",
                    ["intent"] = Trunc(
                        parts.Count > 1
                            ? $"Dialogue: {pendingChar} ({p + 1}/{parts.Count})"
                            : $"Dialogue: {pendingChar}",
                        120),
                    ["visual_event"] = visual,
                    ["shot_scale_hint"] = "medium close",
                    ["action_class"] = "dialogue",
                    ["continuity"] = isFirst
                        ? "new_setup"
                        : "continuous_from_previous_beat",
                    ["time_weight"] = Math.Clamp(
                        ClipDurationEstimator.CountWords(part) / 8.0, 0.5, 4.0),
                    ["delivery"] = delivery,
                    ["speaker"] = charKey,
                    ["dialogue"] = part,
                    ["ambient"] = "",
                    ["sfx"] = parenSfx,
                    ["audio"] = new Dictionary<string, object?>
                    {
                        ["delivery"] = delivery,
                        ["speaker"] = charKey,
                        ["dialogue"] = part,
                        ["ambient"] = "",
                        ["sfx"] = parenSfx,
                    },
                    ["primary_subject"] = charKey,
                    ["characters_on_screen"] = CurrentOnScreen(curScene),
                });
            }

            pendingParen = null;
        }

        void CloseScene()
        {
            FlushAction();
            FlushDialogue();
            pendingChar = null;
            if (curScene is null || beats is null) return;

            // Drop pure transition noise that slipped in as action
            beats.RemoveAll(b =>
                b is Dictionary<string, object?> d &&
                IsNoopBeatDict(d));

            if (beats.Count == 0)
            {
                // Real scene heading with no content yet → short establishing beat.
                // Phantom unspecified scenes that only had FADE IN are discarded.
                var setting = curScene.TryGetValue("setting", out var st) ? st?.ToString() ?? "" : "";
                if (setting.Contains("UNSPECIFIED", StringComparison.OrdinalIgnoreCase))
                {
                    curScene = null;
                    beats = null;
                    beatIndex = 0;
                    return;
                }

                beatIndex++;
                beats.Add(new Dictionary<string, object?>
                {
                    ["beat_id"] = $"b{beatIndex}",
                    ["intent"] = "Establish scene",
                    ["visual_event"] = string.IsNullOrWhiteSpace(setting) ? "Scene" : setting,
                    ["shot_scale_hint"] = "wide",
                    ["action_class"] = "establishing",
                    ["continuity"] = "new_setup",
                    ["time_weight"] = 1.0,
                    ["delivery"] = "none",
                    ["speaker"] = "",
                    ["dialogue"] = "",
                    ["ambient"] = "",
                    ["sfx"] = "",
                    ["audio"] = new Dictionary<string, object?>
                    {
                        ["delivery"] = "none",
                        ["speaker"] = "",
                        ["dialogue"] = "",
                        ["ambient"] = "",
                        ["sfx"] = "",
                    },
                    ["characters_on_screen"] = CurrentOnScreen(curScene),
                });
            }

            var dur = beats.OfType<Dictionary<string, object?>>()
                .Sum(b =>
                {
                    if (b.TryGetValue("time_weight", out var tw) && tw is double d) return d * 4.0;
                    return 4.0;
                });
            curScene["duration_target_seconds"] = (int)Math.Clamp(Math.Round(dur), 8, 180);
            curScene["story_beats"] = beats;
            curScene["summary"] = Trunc(
                string.Join(" ", beats.OfType<Dictionary<string, object?>>()
                    .Select(b => b.TryGetValue("visual_event", out var v) ? v?.ToString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))!),
                280);
            scenes.Add(curScene);
            curScene = null;
            beats = null;
            beatIndex = 0;
        }

        void OpenScene(string heading)
        {
            CloseScene();
            sceneNum++;
            var (locType, locName, setting) = ParseHeading(heading);
            var locId = EnsureLocation(locSeeds, locName, locType, setting);
            curScene = new Dictionary<string, object?>
            {
                ["scene_number"] = sceneNum,
                ["scene_filename"] = $"sc{sceneNum:D2}_{Slug(locName)}",
                ["setting"] = setting,
                ["location_type"] = locType,
                ["location_ids"] = new List<object?> { locId },
                ["primary_location_id"] = locId,
                ["characters_on_screen"] = new List<object?>(),
                ["dramatic_function"] = "",
                ["transition_type"] = sceneNum == 1 ? "fade_in" : "cut",
                ["story_beats"] = new List<object?>(),
            };
            beats = (List<object?>)curScene["story_beats"]!;
        }

        foreach (var el in parsed.Elements)
        {
            switch (el.Type)
            {
                case FountainParser.ElementType.SceneHeading:
                    OpenScene(el.Text);
                    break;

                case FountainParser.ElementType.Action:
                case FountainParser.ElementType.Lyric:
                {
                    var actionText = CleanEmphasis(el.Text);
                    // Do not invent INT. UNSPECIFIED just for FADE IN / CUT TO before the first heading
                    if (curScene is null &&
                        (FountainParser.IsStandaloneTransitionLine(actionText) ||
                         IsNoopTransitionText(actionText)))
                        break;
                    if (curScene is null)
                        OpenScene("INT. UNSPECIFIED - DAY");
                    FlushDialogue();
                    pendingChar = null;
                    if (actionBuf.Length > 0) actionBuf.Append(' ');
                    actionBuf.Append(actionText);
                    break;
                }

                case FountainParser.ElementType.Character:
                    if (curScene is null)
                        OpenScene("INT. UNSPECIFIED - DAY");
                    FlushAction();
                    FlushDialogue();
                    pendingChar = el.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(el.Meta))
                        pendingParen = el.Meta.Trim('(', ')').Trim();
                    else
                        pendingParen = null;
                    EnsureCharacter(charSeeds, pendingChar);
                    EnsureOnScreen(curScene, CharacterKey(pendingChar));
                    break;

                case FountainParser.ElementType.Parenthetical:
                    pendingParen = el.Text;
                    break;

                case FountainParser.ElementType.Dialogue:
                    if (curScene is null)
                        OpenScene("INT. UNSPECIFIED - DAY");
                    FlushAction();
                    if (dialogueBuf.Length > 0) dialogueBuf.Append(' ');
                    dialogueBuf.Append(CleanEmphasis(el.Text));
                    break;

                case FountainParser.ElementType.Transition:
                    FlushAction();
                    FlushDialogue();
                    pendingChar = null;
                    break;

                default:
                    break;
            }
        }

        CloseScene();

        if (scenes.Count == 0)
        {
            // Entire file was action without headings — one scene
            OpenScene("INT. UNSPECIFIED - DAY");
            foreach (var el in parsed.Elements.Where(e =>
                         e.Type is FountainParser.ElementType.Action or FountainParser.ElementType.Dialogue))
            {
                if (actionBuf.Length > 0) actionBuf.Append(' ');
                actionBuf.Append(CleanEmphasis(el.Text));
            }
            CloseScene();
        }

        // Ensure at least narrator if only action
        if (charSeeds.Count == 0)
        {
            charSeeds["Character_Narrator"] = new Dictionary<string, object?>
            {
                ["description"] = "Off-screen narrator.",
                ["display_name_policy"] = "never_on_screen",
                ["voice_profile"] = "Warm clear narrator.",
                ["voice_label"] = "Narrator",
            };
        }

        var totalSec = scenes.OfType<Dictionary<string, object?>>()
            .Sum(s => ToInt(s.TryGetValue("duration_target_seconds", out var d) ? d : 30));

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "stage1.v1",
            ["movie_title"] = title,
            ["source_book_title"] = title,
            ["generation"] = new Dictionary<string, object?>
            {
                ["method"] = "FountainStage1Importer",
                ["format"] = "fountain",
                ["author"] = author,
                ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            },
            ["global_production_variables"] = new Dictionary<string, object?>
            {
                ["target_aspect_ratio"] = "16:9",
                ["resolution"] = "720p",
                ["frame_rate"] = 24,
                ["directorial_treatment"] = "Cinematic lighting, clear coverage, natural performances.",
                ["total_runtime_target_seconds"] = totalSec > 0 ? totalSec : 900,
                ["character_seed_tokens"] = charSeeds,
                ["location_seed_tokens"] = locSeeds,
            },
            ["scenes"] = scenes,
            ["cumulative_duration_target_seconds"] = totalSec,
        };
    }

    private static string? FirstTitle(FountainParser.ParseResult p, string key) =>
        p.TitlePage.TryGetValue(key, out var v) ? v : null;

    private static readonly Regex AmbientCueRe = new(
        @"\b(" +
        @"rain|raining|rainfall|drizzle|storm|thunder|wind|winds|breeze|" +
        @"hum(?:ming)?|murmur(?:ing)?|buzz(?:ing)?|drone|" +
        @"room\s+tone|ambience|ambient|" +
        @"crackling\s+fire|fire\s+crackles?|ticking\s+clock|clock\s+ticks?|" +
        @"distant\s+traffic|traffic\s+noise|waves?|ocean|surf|" +
        @"birds?(?:\s+chirp(?:ing)?)?|crickets?|cicadas?|" +
        @"crowd\s+noise|soft\s+music|underscore" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SfxCueRe = new(
        @"\b(" +
        @"knock(?:s|ing)?|slam(?:s|med|ming)?|bang(?:s|ed|ing)?|crash(?:es|ed|ing)?|" +
        @"thud(?:s|ded)?|creak(?:s|ed|ing)?|click(?:s|ed|ing)?|snap(?:s|ped|ping)?|" +
        @"shatter(?:s|ed|ing)?|gunshot(?:s)?|explosion(?:s)?|blast(?:s)?|" +
        @"footsteps?|footfalls?|door\s+(?:opens?|closes?|slams?)|" +
        @"phone\s+rings?|glass\s+breaks?|splash(?:es|ed|ing)?|" +
        @"screech(?:es|ed|ing)?|roar(?:s|ed|ing)?|beep(?:s|ed|ing)?|" +
        @"alarm|siren|whistle|clap(?:s|ped|ping)?|thump(?:s|ed|ing)?" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Split action prose into continuous <c>ambient</c> bed vs transient <c>sfx</c> hits.
    /// Deterministic keyword cues only — no free-form NLP.
    /// </summary>
    public static (string Ambient, string Sfx) InferAmbientAndSfx(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", "");
        var ambient = new List<string>();
        var sfx = new List<string>();
        foreach (Match m in AmbientCueRe.Matches(text))
        {
            var t = m.Value.Trim().ToLowerInvariant();
            if (!ambient.Contains(t, StringComparer.OrdinalIgnoreCase))
                ambient.Add(t);
        }
        foreach (Match m in SfxCueRe.Matches(text))
        {
            var t = m.Value.Trim().ToLowerInvariant();
            if (!sfx.Contains(t, StringComparer.OrdinalIgnoreCase))
                sfx.Add(t);
        }
        return (string.Join(", ", ambient), string.Join(", ", sfx));
    }

    /// <summary>
    /// Parse scene heading into type + filmable location name.
    /// Strips time-of-day after the last " - " and drops vague placeholder segments
    /// (VARIOUS, MULTIPLE, …) so they never seed location_seed_tokens.
    /// Public for unit tests.
    /// </summary>
    public static (string LocType, string LocName, string Setting) ParseHeading(string heading)
    {
        heading = (heading ?? "").Trim();
        var locType = "int";
        var u = heading.ToUpperInvariant();
        if (u.StartsWith("EXT") || u.Contains("EXT."))
            locType = "ext";
        else if (u.Contains("INT./EXT") || u.Contains("INT/EXT") || u.StartsWith("I/E"))
            locType = "mixed";

        var rest = Regex.Replace(heading, @"^(INT\.?/EXT|INT/EXT|I/E|INT\.?|EXT\.?|EST\.?)\s*", "",
            RegexOptions.IgnoreCase).Trim();
        // strip time of day after last dash
        var locName = rest;
        var dash = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0) dash = rest.LastIndexOf(" – ", StringComparison.Ordinal);
        if (dash > 0)
            locName = rest[..dash].Trim();
        locName = SanitizeLocationName(locName);
        if (string.IsNullOrWhiteSpace(locName))
            locName = "Unspecified";
        return (locType, locName, heading);
    }

    /// <summary>
    /// Remove vague multi-place placeholders from a location name.
    /// e.g. "HOUSE - VARIOUS" → "HOUSE"; "MULTIPLE LOCATIONS" → "Unspecified".
    /// </summary>
    public static string SanitizeLocationName(string? locName)
    {
        locName = (locName ?? "").Trim();
        if (locName.Length == 0) return "Unspecified";

        // Split compound headings: HOUSE - VARIOUS → keep solid segments only
        var parts = Regex.Split(locName, @"\s+[-–]\s+")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var kept = parts.Where(p => !IsVagueLocationSegment(p)).ToList();
        if (kept.Count > 0)
            return string.Join(" - ", kept);

        // Single segment that is only vague language
        if (IsVagueLocationSegment(locName))
            return "Unspecified";

        return locName;
    }

    /// <summary>True when a heading segment is a non-filmable multi-place placeholder.</summary>
    public static bool IsVagueLocationSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return true;
        var s = Regex.Replace(segment.Trim(), @"\s+", " ");
        // Whole-segment placeholders
        if (Regex.IsMatch(
                s,
                @"^(VARIOUS|MULTIPLE|SEVERAL|ELSEWHERE|DIFFERENT|AROUND|THROUGHOUT|"
                + @"MULTIPLE\s+LOCATIONS?|VARIOUS\s+LOCATIONS?|DIFFERENT\s+ROOMS?|"
                + @"DIFFERENT\s+PLACES?|SEVERAL\s+ROOMS?|VARIOUS\s+ROOMS?|"
                + @"AROUND\s+THE\s+HOUSE|THROUGHOUT\s+THE\s+HOUSE)$",
                RegexOptions.IgnoreCase))
            return true;

        // Segment reduces to empty after stripping vague filler words only
        var stripped = Regex.Replace(
            s,
            @"\b(VARIOUS|MULTIPLE|SEVERAL|ELSEWHERE|DIFFERENT|LOCATIONS?|ROOMS?|PLACES?|AREAS?)\b",
            "",
            RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"[\s\-/&,]+", "").Trim();
        return stripped.Length == 0;
    }

    /// <summary>Transition-only lines that must not become filmable beats/clips.</summary>
    private static bool IsNoopTransitionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (FountainParser.IsStandaloneTransitionLine(text)) return true;
        var t = Regex.Replace(text.Trim(), @"\s+", " ");
        return Regex.IsMatch(
            t,
            @"^(FADE\s+IN|FADE\s+OUT|FADE\s+TO\s+BLACK|FADE\s+TO\s+WHITE|CUT\s+TO(\s+BLACK)?|DISSOLVE\s+TO|SMASH\s+CUT\s+TO|BLACK\s+OUT|THE\s+END)[\s\.:]*$",
            RegexOptions.IgnoreCase);
    }

    private static bool IsNoopBeatDict(Dictionary<string, object?> beat)
    {
        var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString() ?? "" : "";
        var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString() ?? "" : "";
        if (!string.IsNullOrWhiteSpace(dlg)) return false;
        return IsNoopTransitionText(ve);
    }

    private static string EnsureLocation(
        Dictionary<string, object?> seeds,
        string locName,
        string locType,
        string setting)
    {
        var id = "Loc_" + SlugKey(locName);
        if (!seeds.ContainsKey(id))
        {
            seeds[id] = new Dictionary<string, object?>
            {
                ["display_name"] = locName,
                ["description"] = setting,
                ["visual_lock"] = setting,
                ["reference_image_placeholder"] = id.ToLowerInvariant() + "_ref.png",
            };
        }
        return id;
    }

    private static string EnsureCharacter(Dictionary<string, object?> seeds, string displayName)
    {
        var key = CharacterKey(displayName);
        if (!seeds.ContainsKey(key))
        {
            var off = IsOffScreen(displayName);
            var name = CleanCharacterName(displayName);
            // Do not invent looks from Fountain. Leave description/visual_lock empty for on-screen
            // cast so Stage 2 cannot embed "as described in the screenplay" stubs into visual prompts.
            // Characters UI / cast extract / locked refs supply real identity later (gen-time CHARACTER VARIABLES).
            seeds[key] = new Dictionary<string, object?>
            {
                ["description"] = off
                    ? $"{name} (voice only; not on screen)."
                    : "",
                ["canonical_given_name"] = name,
                ["display_name_policy"] = off ? "never_on_screen" : "ok_anytime",
                ["voice_profile"] = "Consistent character voice every scene.",
                ["voice_label"] = name.Replace(' ', '_'),
                ["reference_image_placeholder"] = ProjectStore.CharacterRefFileName(key),
            };
            if (!off)
                ((Dictionary<string, object?>)seeds[key]!)["visual_lock"] = "";
        }
        return key;
    }

    private static string CharacterKey(string name)
    {
        var core = CleanCharacterName(name);
        var slug = Regex.Replace(core, @"[^A-Za-z0-9]+", "_").Trim('_');
        if (slug.Length == 0) slug = "Unknown";
        return "Character_" + slug;
    }

    private static string CleanCharacterName(string name)
    {
        name = Regex.Replace(name, @"\s*\([^)]*\)\s*", " ").Trim();
        name = name.TrimEnd('^').Trim();
        // Title case-ish from ALL CAPS
        if (name.Length > 0 && name.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            name = string.Join(' ', parts.Select(p =>
                p.Length <= 1 ? p.ToUpperInvariant()
                : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        }
        return name;
    }

    private static bool IsOffScreen(string name) =>
        name.Contains("O.S.", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("O.S", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("V.O.", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("V.O", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("OS)", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VO)", StringComparison.OrdinalIgnoreCase);

    private static void EnsureOnScreen(Dictionary<string, object?>? scene, string charKey)
    {
        if (scene is null) return;
        if (!scene.TryGetValue("characters_on_screen", out var cos) || cos is not List<object?> list)
        {
            list = new List<object?>();
            scene["characters_on_screen"] = list;
        }
        if (!list.Any(x => string.Equals(x?.ToString(), charKey, StringComparison.OrdinalIgnoreCase)))
            list.Add(charKey);
    }

    private static List<object?> CurrentOnScreen(Dictionary<string, object?>? scene)
    {
        if (scene?.TryGetValue("characters_on_screen", out var cos) == true && cos is List<object?> list)
            return list.ToList();
        return new List<object?>();
    }

    private static string Slug(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');

    private static string SlugKey(string s)
    {
        var parts = Regex.Split(s, @"[^A-Za-z0-9]+")
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..].ToLowerInvariant() : ""));
        var joined = string.Join('_', parts);
        return string.IsNullOrEmpty(joined) ? "Unspecified" : joined;
    }

    private static string CleanEmphasis(string s)
    {
        s = Regex.Replace(s, @"\*{1,3}([^*]+)\*{1,3}", "$1");
        s = Regex.Replace(s, @"_([^_]+)_", "$1");
        return s.Trim();
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    private static int ToInt(object? o) => o switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var i) => i,
        _ => 0,
    };
}
