# Nick and Me

Film generation: **renderer** + **gui** + **cli**.

## Run

```bash
# CLI generate (active project under projects/)
python -m cli

# Review UI
source .venv/bin/activate   # or Windows: .\.venv-win\Scripts\Activate.ps1
streamlit run gui/streamlit_app.py
```

Needs `XAI_API_KEY`, `ffmpeg` on PATH, and `pip install -r requirements-review.txt` in a project venv (not system Python).

## Layout

| Path | Role |
|------|------|
| `renderer/` | Film engine (generate, remux, characters, WIP) |
| `gui/` | Streamlit review UI |
| `cli/` | Command-line frontend |
| `projects/<id>/` | Per-film blueprint, config, state, assets |
| `projects/workspace.json` | Active project |
| `prompts/` | Adaptation + Stage 1/2 prompts |
| `scripts/` | Maintenance / two-stage tools |
| `docs/two_stage_adaptation/` | Stage 1/2 workflow |

More detail: `gui/README.md`, `cli/README.md`, `renderer/README.md`, `prompts/README.md`, `scripts/README.md`.
