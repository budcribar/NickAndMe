using System.Text.RegularExpressions;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Fakes;

/// <summary>
/// Deterministic chat stubs for offline / Playwright fakes mode.
/// Returns valid-looking Fountain, cast seeds, auto-review, and learning text.
/// </summary>
public sealed class FakeGrokChatClient : IChatClient
{
    private readonly ILogger<FakeGrokChatClient> _log;

    public FakeGrokChatClient(ILogger<FakeGrokChatClient> log) => _log = log;

    public bool IsConfigured => true;

    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null)
    {
        _log.LogInformation(
            "Fake chat complete model={Model} mode={Mode} sysLen={Sys} userLen={User}",
            model, mode ?? "(none)", systemPrompt?.Length ?? 0, userPrompt?.Length ?? 0);

        var sys = systemPrompt ?? "";
        var user = userPrompt ?? "";
        var blob = sys + "\n" + user;

        // ── Cast from screenplay → cast_seeds-shaped JSON ──────────────────
        if (sys.Contains("casting director", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("CLOSED CAST", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("fountain_to_cast", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("closed cast", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("character_seed_tokens", StringComparison.OrdinalIgnoreCase) ||
            (sys.Contains("character", StringComparison.OrdinalIgnoreCase) &&
             sys.Contains("seed", StringComparison.OrdinalIgnoreCase) &&
             !sys.Contains("literal", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(IsPoe(blob) ? PoeCastJson : DefaultCastJson);
        }

        // ── Auto-review / QC JSON ──────────────────────────────────────────
        if (sys.Contains("auto-review", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("auto_review", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("QC", StringComparison.Ordinal) ||
            user.Contains("visual_prompt", StringComparison.OrdinalIgnoreCase) &&
            user.Contains("clip", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AutoReviewJson);
        }

        // ── Learning propose ───────────────────────────────────────────────
        if (sys.Contains("house rules", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("QC fail", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("Recent film QC fails", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(
                "- Keep candlelight and deep shadows consistent across chamber clips.\n" +
                "- Match the Narrator's pale face and dark coat on every cut.\n" +
                "- Heartbeat tension: hold tight on floorboards before the confession scream.\n" +
                "- Prefer continuity from previous clip tail; flag jumps as fail when clear.");
        }

        // ── Visual literalize ──────────────────────────────────────────────
        if (sys.Contains("literal", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("figurative", StringComparison.OrdinalIgnoreCase) ||
            (sys.Contains("wardrobe", StringComparison.OrdinalIgnoreCase) &&
             sys.Contains("visual", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(IsPoe(blob) ? PoeCastJson : DefaultCastJson);
        }

        // ── Book → Fountain ────────────────────────────────────────────────
        if (sys.Contains("Fountain", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("--- PAGE", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("BEGIN BOOK", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("book_to_fountain", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(IsPoe(blob) ? PoeFountain : DefaultFountain);
        }

        // ── Silent beat duration classes ───────────────────────────────────
        if (sys.Contains("DURATION BUDGETING", StringComparison.OrdinalIgnoreCase) ||
            mode == ChatCallModes.SilentBeatClassify)
        {
            return Task.FromResult(BuildSilentBeatLabelsJson(user));
        }

        if (mode == ChatCallModes.AmbientSfxClassify ||
            sys.Contains("ambient bed vs SFX", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("ambient BED vs transient SFX", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(BuildIdLabels(user, id =>
                $$"""{"id":"{{id}}","ambient":"","sfx":""}"""));
        }

        if (mode == ChatCallModes.OnScreenCastClassify ||
            sys.Contains("ON CAMERA", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(BuildIdLabels(user, id =>
                $$"""{"id":"{{id}}","keys":[]}"""));
        }

        if (mode == ChatCallModes.ExtendCutClassify ||
            sys.Contains("hard_cut vs extend", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(BuildIdLabels(user, id =>
            {
                var cls = id.EndsWith("_b1") ? "hard_cut" : "extend";
                return $$"""{"id":"{{id}}","class":"{{cls}}"}""";
            }));
        }

        if (mode == ChatCallModes.SpeciesKindClassify ||
            sys.Contains("animal|human|other", StringComparison.OrdinalIgnoreCase))
        {
            // key-based payload
            var labels = new List<string>();
            foreach (Match m in Regex.Matches(user, @"""key""\s*:\s*""([^""]+)"""))
            {
                var key = m.Groups[1].Value;
                var cls = key.Contains("Narrator", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Officer", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Man", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Mom", StringComparison.OrdinalIgnoreCase) ||
                          key.Contains("Dad", StringComparison.OrdinalIgnoreCase)
                    ? "human"
                    : "other";
                labels.Add($$"""{"key":"{{key}}","class":"{{cls}}"}""");
            }
            return Task.FromResult("""{"labels":[""" + string.Join(",", labels) + "]}");
        }

        if (mode == ChatCallModes.PlateRankClassify ||
            sys.Contains("book image basenames", StringComparison.OrdinalIgnoreCase))
        {
            var names = Regex.Matches(user, @"""([^""]+\.(?:png|jpe?g|webp))""", RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(n => "\"" + n.Replace("\\", "\\\\") + "\"")
                .ToList();
            return Task.FromResult("""{"ranked":[""" + string.Join(",", names) + "]}");
        }

        if (mode == ChatCallModes.ShotPlanRefineClassify ||
            sys.Contains("cinematographer refining shot plans", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("""{"refinements":[]}""");
        }

        if (mode == ChatCallModes.BeatPacingClassify ||
            sys.Contains("duration pacing for screenplay beats", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("""{"pacing":[]}""");
        }

        if (mode == ChatCallModes.CinematicLightingClassify ||
            sys.Contains("cinematographer and lighting director", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("""{"lighting_token":"Chiaroscuro flickering candlelight with deep obsidian shadows and desaturated cool-gray volumetric fog"}""");
        }

        if (mode == ChatCallModes.CameraDirectorClassify ||
            sys.Contains("Virtuoso Film Director and Director of Photography", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("""{"directives":[]}""");
        }

        if (mode == ChatCallModes.NegativePromptClassify ||
            sys.Contains("Period Visual Continuity Guard", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("""{"negative_tokens":"no modern wristwatches, no electric light bulbs, no plastic, no zippers, no printed text"}""");
        }

        // ── Minimal Stage1-shaped stub ─────────────────────────────────────
        return Task.FromResult("""
            {
              "schema_version": "stage1.v1",
              "movie_title": "Untitled",
              "global_production_variables": {
                "character_seed_tokens": {},
                "location_seed_tokens": {},
                "target_aspect_ratio": "16:9",
                "resolution": "480p",
                "frame_rate": 24,
                "total_runtime_target_seconds": 480
              },
              "scenes": []
            }
            """);
    }

    /// <summary>Echo beat ids with deterministic classes for fakes/CI (heuristic-shaped).</summary>
    private static string BuildSilentBeatLabelsJson(string user) =>
        BuildIdLabels(user, id =>
        {
            var cls = Regex.IsMatch(id, @"_b1$") ? "establishing" : "action";
            return $$"""{"id":"{{id}}","class":"{{cls}}","reason":"fake"}""";
        });

    private static string BuildIdLabels(string user, Func<string, string> labelForId)
    {
        var labels = new List<string>();
        foreach (Match m in Regex.Matches(user, @"""id""\s*:\s*""([^""]+)"""))
            labels.Add(labelForId(m.Groups[1].Value));
        return """{"labels":[""" + string.Join(",", labels) + "]}";
    }

    private static bool IsPoe(string blob) =>
        blob.Contains("TELL-TALE", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("Tell-Tale", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("vulture", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("Edgar Allan Poe", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("hideous heart", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("old man", StringComparison.OrdinalIgnoreCase) &&
        blob.Contains("eye", StringComparison.OrdinalIgnoreCase);

    private const string PoeFountain = """
        Title: The Tell-Tale Heart
        Credit: Written by
        Author: Edgar Allan Poe (adaptation)
        Source: The Tell-Tale Heart
        Draft date: 7/19/2026

        FADE IN:

        INT. CHAMBER - NIGHT

        Candlelight. A pale NARRATOR faces us — too calm.

        NARRATOR
        True! Nervous — very, very dreadfully nervous I had been and am.
        But why will you say that I am mad?

        He leans closer. A floorboard creaks.

        NARRATOR (CONT'D)
        The disease had sharpened my senses — not destroyed them.
        Above all was the sense of hearing acute.

        INT. CHAMBER - NIGHT - LATER

        An OLD MAN sleeps behind a curtained bed. One pale blue eye glints.

        NARRATOR (V.O.)
        I loved the old man. He had never wronged me.
        I think it was his eye! Yes, it was this!

        The Narrator opens the door a crack. Lantern light crawls in.

        NARRATOR (V.O.)
        You should have seen how wisely I proceeded —
        with what caution — with what foresight.

        INT. CHAMBER - NIGHT - THE EIGHTH NIGHT

        The veiled eye opens. The Narrator's breath shakes.

        NARRATOR
        (whisper)
        It is the beating of his hideous heart!

        A single terrible moment. Then stillness. Planks. Dark wood floor.

        INT. CHAMBER - DAY

        Three OFFICERS sit over the very boards. Polite. Suspecting nothing.

        OFFICER
        A cry was heard in the night. We were obliged to investigate.

        NARRATOR
        The old man is away in the country. Search — search well.

        He smiles. The smile dies. A sound — soft at first — under the floor.

        NARRATOR (CONT'D)
        Villains! Dissemble no more! I admit the deed!
        Tear up the planks! Here, here! —
        It is the beating of his hideous heart!

        FADE OUT.

        THE END
        """;

    private const string DefaultFountain = """
        Title: Cinematic Short
        Credit: Written by
        Author: Test
        Source: Adapted from book
        Draft date: 1/1/2026

        INT. ROOM - NIGHT

        A figure waits in dim light.

        NARRATOR
        Once, in a quiet room, the story began.

        FADE OUT.

        THE END
        """;

    private const string PoeCastJson = """
        {
          "schema_version": "cast_seeds.v1",
          "movie_title": "The Tell-Tale Heart",
          "render_style_lock": "STYLE LOCK: photoreal live-action period drama circa 1840s; candlelight; naturalistic skin and fabric",
          "performance_lock": "PERFORMANCE LOCK: first-person confessional; when the Narrator speaks on camera he often addresses an implied listener/viewer; other characters are observed in the chamber rather than addressing the audience.",
          "character_seed_tokens": {
            "Character_Narrator": {
              "canonical_given_name": "Narrator",
              "display_name_policy": "ok_anytime",
              "description": "Pale nervous adult man, mid-30s, thin gaunt face, dark shoulder-length hair, dark 1840s wool coat, white linen shirt, haunted open eyes, photoreal period drama",
              "visual_lock": "Always the same pale thin-faced adult man with dark hair and dark wool coat; distinct from the elderly Old Man; no modern clothing",
              "voice_profile": "Adult male, intimate, articulate, rising panic under calm diction; same on-camera and V.O.",
              "voice_label": "Narrator",
              "performance_notes": "Confessional speaker; on-camera dialogue often directed toward the implied listener/viewer rather than only at the Old Man.",
              "wardrobe_always": ["dark wool coat", "white linen shirt", "period trousers"],
              "reference_image_placeholder": "character_narrator_ref.png"
            },
            "Character_Old_Man": {
              "canonical_given_name": "Old Man",
              "display_name_policy": "ok_anytime",
              "description": "Elderly frail man in pale nightshirt, sparse white-gray hair, one distinctive pale blue filmed eye that catches light, deeply lined face",
              "visual_lock": "Always the same frail elderly man with sparse white-gray hair and one pale blue eye; never the Narrator's younger face",
              "voice_profile": "No spoken dialogue on screen; silent if any breath is heard",
              "voice_label": "Old Man",
              "wardrobe_always": ["pale period nightshirt"],
              "reference_image_placeholder": "character_old_man_ref.png"
            },
            "Character_Officer": {
              "canonical_given_name": "Officer",
              "display_name_policy": "ok_anytime",
              "description": "Adult man, solid build, neat short brown hair, clean-shaven, mid-19th-century dark wool constable coat with brass buttons, calm polite expression",
              "visual_lock": "Same neat brown-haired clean-shaven man in dark wool constable coat; composed official bearing",
              "voice_profile": "Adult male, medium pitch, polite official tone, moderate pace",
              "voice_label": "Officer",
              "wardrobe_always": ["dark wool constable coat with brass buttons", "dark trousers"],
              "reference_image_placeholder": "character_officer_ref.png"
            }
          }
        }
        """;

    private const string DefaultCastJson = """
        {
          "schema_version": "cast_seeds.v1",
          "movie_title": "Untitled",
          "character_seed_tokens": {
            "Character_Narrator": {
              "canonical_given_name": "Narrator",
              "display_name_policy": "ok_anytime",
              "description": "Adult human with clear face and period-appropriate clothing suitable for portrait lock",
              "visual_lock": "Same face, hair, and primary wardrobe in every scene",
              "voice_profile": "Adult clear voice, consistent every scene",
              "voice_label": "Narrator",
              "wardrobe_always": [],
              "reference_image_placeholder": "character_narrator_ref.png"
            }
          }
        }
        """;

    private const string AutoReviewJson = """
        {
          "suggestion": "fail",
          "confidence": "medium",
          "continuity": "weak",
          "category": "continuity",
          "summary": "Slight jump in wardrobe and light between previous tail and this clip.",
          "edits": [
            {
              "field": "visual_prompt",
              "action": "append",
              "text": " Match wardrobe and candlelight from previous clip tail; same coat, same room shadows."
            }
          ]
        }
        """;
}
