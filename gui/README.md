# GUI — Streamlit review console

## Layout

```text
gui/
  streamlit_app.py     # Home / entrypoint
  pages/               # Multipage UI
  review_app/          # Renderer façade, edit log, costs, thumbs
```

## Run (from workspace root)

```bash
# activate venv first if using one
streamlit run gui/streamlit_app.py
```

The app switches into the **active project directory** (`projects/<id>/`) so each film
has isolated blueprints, assets, and state.

## Multi-project layout

```text
projects/
  workspace.json               # { "active_project": "NickAndMe" }
  NickAndMe/
    project.json
    pipeline_config.json
    pipeline_state.json
    nickandme.clips.grok.json  # Stage 2 generate plan
    nickandme.scenes.json      # Stage 1 bible
    assets/
    source/
    review_feedback/
    edit_feedback_log.json
```

Create or switch projects on the home page sidebar.

## Related

- CLI: `python -m cli`
- Renderer: `renderer/`
- Tools: `scripts/`
