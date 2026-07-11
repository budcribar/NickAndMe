# Two-stage book → film adaptation

Split the old monolithic “book → full clip blueprint” into:

| Stage | Name | Input | Output | Depends on video generator? |
|-------|------|--------|--------|-------------------------------|
| **1** | Scene bible | Book text + cast rules | `*.scenes.json` | **No** |
| **2** | Shot planner | Scene bible + `VIDEO_PROVIDER` | Clip plan (`veo_clips` per scene) usable by the renderer (`python -m cli`) | **Yes** |

## Why

- Max clip length, extend rules, and prompt budgets differ (Grok vs Veo).
- Story structure should not be re-adapted when you only change engine or duration grid.
- Cost / hero / cascade tooling can plan against Stage 2 without rewriting Stage 1.

## Prompts & schema (canonical location)

All operator prompts and the Stage 1 schema live under **`prompts/`** (repo root):

| File | Purpose |
|------|---------|
| `prompts/adaptation_v16.txt` | Full-film / shared adaptation rules (GUI learnings append here) |
| `prompts/stage1_scene_bible.txt` | Operator prompt: book → scene bible |
| `prompts/stage1_scene_bible.schema.json` | JSON Schema for Stage 1 |
| `prompts/stage2_shot_planner.txt` | Operator prompt: scene bible → clip plan |
| `prompts/compare_json_to_book.txt` | Fidelity check prompt |
| `prompts/examples/scene_bible_minimal.json` | Tiny Stage 1 example |
| `prompts/examples/clip_plan_minimal.json` | Tiny Stage 2 example (pipeline-shaped) |
| `scripts/two_stage_adaptation/*.py` | Extract Stage 1 / plan Stage 2 (Grok) |

## Recommended workflow

1. Run **Stage 1** once (or when story changes).
2. Run **Stage 2** whenever you change `VIDEO_PROVIDER`, default duration, or resolution policy.
3. Feed Stage 2 JSON into the renderer / Streamlit (same field names as today: `scenes[].veo_clips`, etc.).
4. Stage 0 characters (portraits + `voice_profile`) live in Stage 1 `character_seed_tokens` and are **not** recreated in Stage 2.

## Extract Stage 1 from current blueprint + book pages

From repo root:

```bash
python scripts/two_stage_adaptation/extract_stage1_from_blueprint.py
```

Writes `nickandme.scenes.json` by:

- Mapping each legacy scene → Stage 1 fields
- Turning each `veo_clip` into a `story_beat` (legacy clip id preserved)
- Copying character seeds (including voice locks)
- Matching **book context** from `book_text_pages_*.txt` via dialogue/keyword overlap into
  `source_book_refs` + `source_excerpts` per scene

Optional:

```bash
python scripts/two_stage_adaptation/extract_stage1_from_blueprint.py \
  --blueprint nickandme.clips.grok.json \
  --out nickandme.scenes.json \
  --book-dir .
```

Note: matching is heuristic. Empty `source_excerpts` means no strong text hit — fill by hand or improve the matcher. PDF (`Nickandme.PDF`) is not auto-parsed unless you add a text dump next to the page files.

## Mapping to current repo

- Stage 1 → `projects/<id>/nickandme.scenes.json` (or project `scenes_file`)
- Stage 2 Grok plan → `projects/<id>/nickandme.clips.grok.json` (pipeline-shaped: `scenes[].veo_clips`)
- Legacy single merged `nickandme.json` removed; use the two files above.

### Run Stage 2 (Grok)

```bash
# Full film Grok clip plan
python scripts/two_stage_adaptation/stage2_plan_grok.py

# Only scenes 1–2
python scripts/two_stage_adaptation/stage2_plan_grok.py --scenes 1-2 --out nickandme.clips.grok.s1-2.json

# Draft resolution in prompt suffix
python scripts/two_stage_adaptation/stage2_plan_grok.py --resolution 480p

# Merge planned scenes into the active Grok blueprint (backup first)
python scripts/two_stage_adaptation/stage2_plan_grok.py --scenes 1-2 --merge-into nickandme.clips.grok.json
```

Grok policy applied by the planner:

- Clip length prefer **6–10s** (default ~8)
- `extend_previous` only for continuous small motion / dialogue holds
- `none` for establishing, flashback edges, **big_action**, hard cuts
- Prompt soft max ~500 / hard 800 + resolution suffix
- Global negative + action/orientation extras
- Continuous-path language reinforced on window/kick smash beats

**Streamlit / CLI load `nickandme.clips.grok.json` by default**  
(`pipeline_config.blueprint_file`). Stage 1 remains `nickandme.scenes.json` (story only).

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
