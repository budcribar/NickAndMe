# Two-stage book → film adaptation

Split the old monolithic “book → full clip blueprint” into:

| Stage | Name | Input | Output | Depends on video generator? |
|-------|------|--------|--------|-------------------------------|
| **1** | Scene bible | Book text + cast rules | `*.scenes.json` | **No** |
| **2** | Shot planner | Scene bible + `VIDEO_PROVIDER` | Clip plan (`veo_clips` per scene) usable by `generation_script.py` | **Yes** |

## Why

- Max clip length, extend rules, and prompt budgets differ (Grok vs Veo).
- Story structure should not be re-adapted when you only change engine or duration grid.
- Cost / hero / cascade tooling can plan against Stage 2 without rewriting Stage 1.

## Files in this folder

| File | Purpose |
|------|---------|
| `Stage1_SceneBible.schema.json` | JSON Schema for Stage 1 |
| `Stage1_AdaptationPrompt.txt` | Operator prompt: book → scene bible |
| `Stage2_ShotPlannerPrompt.txt` | Operator prompt: scene bible → clip plan |
| `examples/scene_bible_minimal.json` | Tiny Stage 1 example |
| `examples/clip_plan_minimal.json` | Tiny Stage 2 example (pipeline-shaped) |

## Recommended workflow

1. Run **Stage 1** once (or when story changes).
2. Run **Stage 2** whenever you change `VIDEO_PROVIDER`, default duration, or resolution policy.
3. Feed Stage 2 JSON into `generation_script.py` / Streamlit (same field names as today: `scenes[].veo_clips`, etc.).
4. Stage 0 characters (portraits + `voice_profile`) live in Stage 1 `character_seed_tokens` and are **not** recreated in Stage 2.

## Extract Stage 1 from current blueprint + book pages

From repo root:

```bash
python docs/two_stage_adaptation/extract_stage1_from_blueprint.py
```

Writes `nickandme.scenes.json` by:

- Mapping each legacy scene → Stage 1 fields
- Turning each `veo_clip` into a `story_beat` (legacy clip id preserved)
- Copying character seeds (including voice locks)
- Matching **book context** from `book_text_pages_*.txt` via dialogue/keyword overlap into
  `source_book_refs` + `source_excerpts` per scene

Optional:

```bash
python docs/two_stage_adaptation/extract_stage1_from_blueprint.py \
  --blueprint nickandme.json \
  --out nickandme.scenes.json \
  --book-dir .
```

Note: matching is heuristic. Empty `source_excerpts` means no strong text hit — fill by hand or improve the matcher. PDF (`Nickandme.PDF`) is not auto-parsed unless you add a text dump next to the page files.

## Mapping to current repo

- Today’s `nickandme.json` ≈ Stage 1 + Stage 2 **merged**.
- Migration path: extract story fields into `nickandme.scenes.json`, keep clip arrays as Stage 2 output, or store both under one file:

```json
{
  "schema_version": "2.0",
  "scene_bible": { "...Stage 1..." },
  "clip_plans": {
    "grok": { "scenes": [ { "scene_number": 1, "veo_clips": [] } ] },
    "veo": { "scenes": [ { "scene_number": 1, "veo_clips": [] } ] }
  }
}
```

Pipeline can keep loading a **resolved** single plan (`active_clip_plan: "grok"`) for generate.
