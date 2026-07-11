# Renderer

Shared film engine: Grok generate/regen, characters, remux, WIP movie, state.

Used by:

- `python -m cli` (interactive)
- `streamlit run gui/streamlit_app.py` (via `gui/review_app/pipeline_api.py`)

## Package

```text
renderer/
  engine.py      # AgenticGenerationEngine (former generation_script.py)
  __init__.py    # public exports
  __main__.py    # python -m renderer → same as cli
```

Import:

```python
from renderer import AgenticGenerationEngine, DEFAULT_CONFIG, clip_output_path
```
