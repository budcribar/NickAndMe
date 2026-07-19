using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Fakes;

/// <summary>
/// Deterministic chat stubs for offline / Playwright fakes mode.
/// Returns valid-looking Fountain, cast seeds, auto-review, and learning text.
/// </summary>
public sealed class FakeGrokChatClient : IGrokChatClient
{
    private readonly ILogger<FakeGrokChatClient> _log;

    public FakeGrokChatClient(ILogger<FakeGrokChatClient> log) => _log = log;

    public bool IsConfigured => true;

    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "Fake chat complete model={Model} sysLen={Sys} userLen={User}",
            model, systemPrompt?.Length ?? 0, userPrompt?.Length ?? 0);

        var sys = systemPrompt ?? "";
        var user = userPrompt ?? "";
        var blob = sys + "\n" + user;

        // ── Cast from screenplay → cast_seeds-shaped JSON ──────────────────
        if (sys.Contains("cast", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("fountain_to_cast", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("cast_seeds", StringComparison.OrdinalIgnoreCase) ||
            (sys.Contains("character", StringComparison.OrdinalIgnoreCase) &&
             sys.Contains("seed", StringComparison.OrdinalIgnoreCase)))
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
            sys.Contains("wardrobe", StringComparison.OrdinalIgnoreCase) &&
            sys.Contains("visual", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(user.Length > 40 ? user : "A pale nervous man in a dark wool coat, 1840s.");
        }

        // ── Book → Fountain ────────────────────────────────────────────────
        if (sys.Contains("Fountain", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("--- PAGE", StringComparison.OrdinalIgnoreCase) ||
            user.Contains("BEGIN BOOK", StringComparison.OrdinalIgnoreCase) ||
            sys.Contains("book_to_fountain", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(IsPoe(blob) ? PoeFountain : DefaultFountain);
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
          "movie_title": "The Tell-Tale Heart",
          "cast_seeds": {
            "Character_Narrator": {
              "display_name": "Narrator",
              "description": "Pale nervous adult man, 30s, thin face, dark 1840s wool coat, white shirt, haunted eyes, photoreal period drama",
              "visual_lock": "Same pale face, dark coat, short dark hair every shot; no modern clothing",
              "voice_profile": "Adult male, intimate, articulate, rising panic under calm English",
              "voice_label": "tense confessor",
              "age_band": "adult",
              "wardrobe": ["dark wool coat", "white collar shirt", "period trousers"]
            },
            "Character_OldMan": {
              "display_name": "Old Man",
              "description": "Elderly frail man in nightshirt, white hair, one pale blue filmed eye (vulture eye), 1840s bedchamber",
              "visual_lock": "White hair, pale blue film over one eye, nightshirt; never modern",
              "voice_profile": "Elderly male, weak, mostly silent or a single cry",
              "voice_label": "feeble elder",
              "age_band": "elder",
              "wardrobe": ["white nightshirt"]
            },
            "Character_Officer": {
              "display_name": "Officer",
              "description": "Mid-1800s police officer, dark coat, calm official bearing, lantern",
              "visual_lock": "Dark official coat, beard optional, calm eyes",
              "voice_profile": "Adult male, calm official tone",
              "voice_label": "officer",
              "age_band": "adult",
              "wardrobe": ["dark official coat"]
            }
          }
        }
        """;

    private const string DefaultCastJson = """
        {
          "movie_title": "Untitled",
          "cast_seeds": {
            "Character_Narrator": {
              "display_name": "Narrator",
              "description": "Adult in period clothing",
              "visual_lock": "Consistent face and wardrobe",
              "voice_profile": "Adult neutral",
              "age_band": "adult",
              "wardrobe": []
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
