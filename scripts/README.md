# Scripts

One-off and maintenance tools. Run from the **repo root** so paths like `nickandme.clips.grok.json` resolve.

## Keep outside this folder

| Path | Role |
|------|------|
| `renderer/` | Film engine |
| `cli/` | CLI frontend (`python -m cli`) |
| `gui/streamlit_app.py` | Review UI entrypoint |
| `gui/pages/` | Streamlit multipage UI |
| `gui/review_app/` | UI/API package |

## Tools in this folder

| Script | Purpose |
|--------|---------|
| `validate_blueprint.py` | Validate active clip plan |
| `check_scenes.py` | Quick scene/clip checks |
| `recalculate_all_timestamps.py` | Fix timestamps from durations |
| `clean_dialogue.py` | Dialogue cleanup helpers |
| `append_*.py` / `insert_*.py` / `build_*.py` | Historical scene-append tools |
| `extract_91_94.py` / `check_page_94.py` | Book page helpers |
| `_fix_*.py` / `_read_*.py` | Ad-hoc debug |

## Two-stage adaptation

```bash
python scripts/two_stage_adaptation/extract_stage1_from_blueprint.py
python scripts/two_stage_adaptation/stage2_plan_grok.py
```

See `prompts/` for schema and operator prompts; `docs/two_stage_adaptation/README.md` for workflow.

## Location inventory + pins

```bash
# 1) Cluster settings → Loc_* inventory
python scripts/inventory_locations.py

# 2) Apply pins into Stage 1 + Stage 2 JSON (location_seed_tokens, location_ids, clip.location_id)
python scripts/two_stage_adaptation/apply_location_pins.py

# 3) Verify Stage 1 schema/prompt still accept current + legacy bibles
python scripts/two_stage_adaptation/verify_stage1.py

# 4) Run Stage 1 LLM on the book (requires XAI_API_KEY) → nickandme.scenes.json
#    First extract PDF text if needed (book_full.txt)
python scripts/two_stage_adaptation/run_stage1_from_book.py
python scripts/two_stage_adaptation/run_stage1_from_book.py --chunk-pages 12 --resume
```

Writes / updates under `projects/<active>/`:
- `location_inventory.json` / `.md`
- `nickandme.scenes.json`, `nickandme.clips.grok.json` (with timestamped `.bak_loc_*`)
