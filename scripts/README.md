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
