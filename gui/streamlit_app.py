"""
Film Review Console (multi-project)

Run from workspace root:
  streamlit run gui/streamlit_app.py
"""
from __future__ import annotations

import sys
from pathlib import Path

_GUI_DIR = Path(__file__).resolve().parent
_WORKSPACE = _GUI_DIR.parent
for _p in (_WORKSPACE, _GUI_DIR):
    _s = str(_p)
    if _s not in sys.path:
        sys.path.insert(0, _s)

import streamlit as st

from review_app import pipeline_api as api
from review_app.paths import projects_root, workspace_root

st.set_page_config(
    page_title="Film Review Console",
    page_icon="🎬",
    layout="wide",
    initial_sidebar_state="expanded",
)

st.title("🎬 Film Review Console")
st.caption(
    "Multi-project review UI. Each project lives under `projects/<id>/` with its own "
    "blueprint, config, state, and assets."
)

# ---------- Project switcher ----------
projects = api.list_all_projects()
proj_ids = [p["id"] for p in projects]
active = api.active_project_info()
active_id = active.get("id")

with st.sidebar:
    st.header("Project")
    if proj_ids:
        try:
            idx = proj_ids.index(active_id) if active_id in proj_ids else 0
        except ValueError:
            idx = 0
        choice = st.selectbox(
            "Active project",
            options=proj_ids,
            index=idx,
            format_func=lambda i: next(
                (f"{p.get('title') or i} ({i})" for p in projects if p["id"] == i), i
            ),
        )
        if choice != active_id:
            api.switch_project(choice)
            st.rerun()
    else:
        st.warning("No projects yet.")

    st.caption(f"Workspace: `{workspace_root()}`")
    st.caption(f"Projects: `{projects_root()}`")

with st.expander("➕ Create project", expanded=not proj_ids):
    new_name = st.text_input("Project id / folder name", placeholder="MyFilm")
    new_title = st.text_input("Title (optional)", placeholder="My Film")
    if st.button("Create", type="primary"):
        if not (new_name or "").strip():
            st.error("Name required")
        else:
            try:
                info = api.new_project(new_name.strip(), title=(new_title or "").strip() or None)
                st.success(f"Created and activated `{info.get('id')}`")
                st.rerun()
            except Exception as e:
                st.error(str(e))

if not active_id and not proj_ids:
    st.info("Create a project to begin.")
    st.stop()

# ---------- Dashboard ----------
try:
    dash = api.home_dashboard()
except Exception as e:
    st.error(str(e))
    st.stop()

st.subheader(dash.get("title") or "Untitled")
proj = dash.get("project") or {}
st.caption(
    f"Project `{proj.get('id')}` · `{proj.get('path')}` · blueprint `{dash.get('blueprint_path')}`"
)

c1, c2, c3, c4, c5 = st.columns(5)
c1.metric("Scenes", f"{dash.get('approved', 0)}/{dash.get('scene_count', 0)} approved")
c2.metric("Clips on disk", f"{dash.get('clips_on_disk', 0)}/{dash.get('clips_total', 0)}")
c3.metric("Characters", f"{dash.get('chars_locked', 0)}/{dash.get('char_count', 0)} locked")
c4.metric("Hero scenes", dash.get("hero_count", 0))
c5.metric("Stale clips", dash.get("stale_count", 0))

stale_n = int(dash.get("stale_count") or 0)
if stale_n:
    labels = dash.get("stale_labels") or []
    st.warning(
        f"**{stale_n} clip(s) out of date** after character redesigns — "
        f"{', '.join(str(x) for x in labels)}"
        + ("…" if stale_n > len(labels) else "")
        + ". Regenerate them (Scenes or character cascade)."
    )
else:
    st.caption("No stale clips (on-disk renders match current character revisions).")

wip = dash.get("wip_path")
if wip:
    meta_bits = []
    if dash.get("wip_updated_at"):
        meta_bits.append(str(dash["wip_updated_at"]))
    if dash.get("wip_scene_count") is not None:
        meta_bits.append(f"{dash['wip_scene_count']} scenes")
    st.success(f"WIP movie: `{wip}`" + (f" ({', '.join(meta_bits)})" if meta_bits else ""))
    st.video(wip)
else:
    st.info("No WIP movie yet — approve a scene after clips exist.")

if st.button("Rebuild WIP from approved scenes"):
    try:
        path = api.rebuild_wip_movie(approved_only=True)
        st.success(f"Rebuilt: `{path}`")
        st.rerun()
    except Exception as e:
        st.error(str(e))

st.divider()
st.markdown(
    """
**Pages**

| Page | Purpose |
|------|---------|
| **Configuration** | `pipeline_config.json` |
| **Characters** | Locked refs, variants, cascade regen |
| **Scenes** | Review / Pass / Fail / Regen clips |
| **Edit Log** | Feedback → learnings / adaptation prompt |
| **Cost** | Budget estimates |

**CLI generate:** `python -m cli` from the workspace root.
"""
)
