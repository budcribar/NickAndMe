# Prompts

Shared operator prompts, schemas, and tiny examples for book → film adaptation.
Paths are relative to the **workspace root** (repo root with `renderer/`, `gui/`, `cli/`).

## Naming

`snake_case` + role + optional version:

| File | Role |
|------|------|
| `book_to_fountain.txt` | **Product path:** book → editable Fountain screenplay |
| `adaptation_v16.txt` | Legacy full-film adaptation rules (optional GUI append) |
| `shared_rules.txt` | Rules Stage 2 + verifier must all respect |
| `stage1_scene_bible.schema.json` | Optional schema for internal materialised scene lists (not an operator prompt) |
| `stage2_shot_planner.txt` | Shot plan from approved screenplay build (+ multi-cast tokens, audio_payload) |
| `verifier_clip.txt` | Clip QA verifier (routing hints for learning layers) |
| `compare_json_to_book.txt` | Fidelity audit against book text |
| `examples/scene_bible_minimal.json` | Minimal scene-list sample |
| `examples/clip_plan_minimal.json` | Minimal Stage 2 sample |

**Operator flow:** book PDF → prepare text → **Fountain draft** (`book_to_fountain.txt`) → edit → approve → shots.
There is no `stage1_scene_bible.txt` prompt; Fountain is the screenplay source of truth.


## Learning loop (Phase A)

Feedback is **routed** by layer — not sprayed into every prompt:

| Layer | Effect |
|-------|--------|
| `clip` | This take / visual_prompt |
| `stage2` | Stage 2 prompt + scene **dirty** for replan |
| `stage1` | Stage 1 prompt + dirty **stage1→stage2** |
| `verifier` | `verifier_clip.txt` (+ optional shared rules) |
| `engine` | `review_feedback/SCRIPT_NOTES.md` only |

Dirty flags live in project `pipeline_state.json` → `scene_dirty`.  
Phase A does **not** auto-run Stage 1/2 LLMs; UI shows a cascade checklist.

## Usage

- **Scenes** → choose feedback layer on Fail / Regen / Log.
- **Edit Log** → apply to layer prompts, shared rules, LEARNINGS, or script notes.
- **Scripts:** `scripts/two_stage_adaptation/` — see `docs/two_stage_adaptation/README.md`.

