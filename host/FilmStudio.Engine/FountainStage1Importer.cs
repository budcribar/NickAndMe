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
            beatIndex++;
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
                ["dialogue"] = "",
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
            beatIndex++;
            var visual = string.IsNullOrWhiteSpace(pendingParen)
                ? $"{pendingChar} speaks."
                : $"{pendingChar} ({pendingParen}).";
            beats.Add(new Dictionary<string, object?>
            {
                ["beat_id"] = $"b{beatIndex}",
                ["intent"] = Trunc($"Dialogue: {pendingChar}", 120),
                ["visual_event"] = visual,
                ["shot_scale_hint"] = "medium close",
                ["action_class"] = "dialogue",
                ["continuity"] = beatIndex == 1 ? "new_setup" : "continuous_from_previous_beat",
                ["time_weight"] = Math.Clamp(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 8.0, 0.5, 4.0),
                ["delivery"] = IsOffScreen(pendingChar) ? "voiceover_internal" : "on_camera",
                ["speaker"] = charKey,
                ["dialogue"] = text,
                ["primary_subject"] = charKey,
                ["characters_on_screen"] = CurrentOnScreen(curScene),
            });
            pendingParen = null;
        }

        void CloseScene()
        {
            FlushAction();
            FlushDialogue();
            pendingChar = null;
            if (curScene is null || beats is null) return;
            if (beats.Count == 0)
            {
                beatIndex++;
                beats.Add(new Dictionary<string, object?>
                {
                    ["beat_id"] = $"b{beatIndex}",
                    ["intent"] = "Establish scene",
                    ["visual_event"] = curScene.TryGetValue("setting", out var st) ? st?.ToString() ?? "Scene" : "Scene",
                    ["shot_scale_hint"] = "wide",
                    ["action_class"] = "establishing",
                    ["continuity"] = "new_setup",
                    ["time_weight"] = 1.0,
                    ["delivery"] = "none",
                    ["dialogue"] = "",
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
                    if (curScene is null)
                        OpenScene("INT. UNSPECIFIED - DAY");
                    FlushDialogue();
                    pendingChar = null;
                    if (actionBuf.Length > 0) actionBuf.Append(' ');
                    actionBuf.Append(CleanEmphasis(el.Text));
                    // Mentioned characters in ALL CAPS action? skip for simplicity
                    break;

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

    private static (string LocType, string LocName, string Setting) ParseHeading(string heading)
    {
        heading = heading.Trim();
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
        if (string.IsNullOrWhiteSpace(locName))
            locName = "Unspecified";
        return (locType, locName, heading);
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
            seeds[key] = new Dictionary<string, object?>
            {
                ["description"] = off
                    ? $"{CleanCharacterName(displayName)} (voice only; not on screen)."
                    : $"{CleanCharacterName(displayName)}, as described in the screenplay.",
                ["canonical_given_name"] = CleanCharacterName(displayName),
                ["display_name_policy"] = off ? "never_on_screen" : "ok_anytime",
                ["voice_profile"] = "Consistent character voice every scene.",
                ["voice_label"] = CleanCharacterName(displayName).Replace(' ', '_'),
                ["reference_image_placeholder"] = ProjectStore.CharacterRefFileName(key),
            };
            if (!off)
                ((Dictionary<string, object?>)seeds[key]!)["visual_lock"] =
                    $"Match {CleanCharacterName(displayName)} as cast for this production.";
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
