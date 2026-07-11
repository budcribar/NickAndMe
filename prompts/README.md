# Prompts

Shared operator prompts, schemas, and tiny examples for book → film adaptation.
Paths are relative to the **workspace root** (repo root with `renderer/`, `gui/`, `cli/`).

## Naming

`snake_case` + role + optional version:

| File | Role |
|------|------|
| `adaptation_v16.txt` | Shared full-film adaptation rules (GUI review learnings append here) |
| `stage1_scene_bible.txt` | Stage 1: book → scene bible |
| `stage1_scene_bible.schema.json` | Stage 1 JSON Schema |
| `stage2_shot_planner.txt` | Stage 2: scene bible → Grok/Veo clip plan |
| `compare_json_to_book.txt` | Fidelity audit against book text |
| `examples/scene_bible_minimal.json` | Minimal Stage 1 sample |
| `examples/clip_plan_minimal.json` | Minimal Stage 2 sample |

## Usage

- **Streamlit Edit Log** → “apply to adaptation prompt” writes into `adaptation_v16.txt`.
- **Stage 1 / 2** operator prompts are for LLM sessions or future scripted adapters.
- **Scripts:** `scripts/two_stage_adaptation/` (extract / plan) — see `docs/two_stage_adaptation/README.md`.
