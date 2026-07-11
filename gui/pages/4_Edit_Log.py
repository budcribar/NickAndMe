"""Edit / feedback log — feed learnings back into blueprint ecosystem."""
from __future__ import annotations

import streamlit as st

from review_app import edit_log

st.set_page_config(page_title="Edit Log", page_icon="📝", layout="wide")
st.title("📝 Edit Log")
st.caption(
    "Every Pass/Fail/Regen/character action can land here. Apply entries to "
    "`LEARNINGS.md`, `prompts/adaptation_v16.txt`, and script notes so future "
    "adaptations and engine work stay aligned."
)

data = edit_log.load_log()
entries = data.get("entries") or []

# Capture free-form learning without a clip action
with st.expander("➕ Add manual learning", expanded=False):
    note = st.text_area("What did we learn?", key="manual_note")
    suggested = st.text_area(
        "Suggested rule (optional — auto-drafted if empty)",
        key="manual_rule",
    )
    sc = st.number_input("Scene (optional)", min_value=0, value=0)
    cl = st.number_input("Clip (optional)", min_value=0, value=0)
    if st.button("Add to log"):
        if not note.strip():
            st.error("Note required")
        else:
            entry = edit_log.add_entry(
                "manual_learning",
                user_note=note,
                scene=int(sc) or None,
                clip=int(cl) or None,
                suggested_rule=suggested.strip(),
                action_taken="Manual log entry",
            )
            st.success(f"Added {entry['id']}")
            st.rerun()

c1, c2, c3 = st.columns(3)
with c1:
    type_filter = st.selectbox(
        "Type filter",
        ["(all)"]
        + sorted({e.get("type") or "" for e in entries if e.get("type")}),
    )
with c2:
    scene_filter = st.number_input("Scene filter (0=all)", min_value=0, value=0)
with c3:
    unapplied = st.checkbox("Unapplied only", value=False)

filtered = edit_log.filter_entries(
    entries,
    entry_type=None if type_filter == "(all)" else type_filter,
    scene=int(scene_filter) if int(scene_filter) > 0 else None,
    unapplied_only=unapplied,
)

st.write(f"**{len(filtered)}** entries (of {len(entries)} total)")
st.markdown(
    f"- Log file: `{edit_log.LOG_PATH}`  \n"
    f"- Learnings MD: `{edit_log.LEARNINGS_MD}`  \n"
    f"- Script notes: `{edit_log.SCRIPT_NOTES_MD}`  \n"
    f"- Adaptation prompt: `{edit_log.ADAPTATION_PROMPT}`"
)

if not filtered:
    st.info("No log entries yet. Pass/Fail/Regen on the Scenes page, or add a manual learning.")
    st.stop()

for e in filtered:
    applied = e.get("applied") or {}
    flags = "".join(
        [
            "B" if applied.get("blueprint") else "·",
            "A" if applied.get("adaptation_prompt") else "·",
            "S" if applied.get("script_notes") else "·",
            "L" if applied.get("learnings_md") else "·",
        ]
    )
    title = (
        f"`{e.get('id')}` · {e.get('ts')} · **{e.get('type')}** · "
        f"S{e.get('scene')}C{e.get('clip')} · applied[{flags}]"
    )
    with st.expander(title, expanded=False):
        st.markdown(f"**User note:** {e.get('user_note')}")
        st.markdown(f"**Action:** {e.get('action_taken')}")
        if e.get("character"):
            st.markdown(f"**Character:** `{e.get('character')}`")
        st.markdown(f"**Suggested rule:** {e.get('suggested_rule')}")
        if e.get("before") or e.get("after"):
            bc1, bc2 = st.columns(2)
            with bc1:
                st.text_area("Before", e.get("before") or "", height=120, key=f"b_{e['id']}")
            with bc2:
                st.text_area("After", e.get("after") or "", height=120, key=f"a_{e['id']}")

        new_rule = st.text_area(
            "Edit suggested rule before applying",
            value=e.get("suggested_rule") or "",
            key=f"rule_{e['id']}",
        )
        if new_rule != (e.get("suggested_rule") or ""):
            if st.button("Update suggested rule", key=f"upd_{e['id']}"):
                edit_log.update_entry(e["id"], suggested_rule=new_rule)
                st.rerun()

        x1, x2, x3, x4 = st.columns(4)
        with x1:
            if st.button("→ LEARNINGS.md", key=f"learn_{e['id']}"):
                try:
                    path = edit_log.append_learnings_md({**e, "suggested_rule": new_rule})
                    edit_log.mark_applied(e["id"], "learnings_md")
                    st.success(f"Appended to {path}")
                except Exception as ex:
                    st.error(str(ex))
        with x2:
            if st.button("→ Adaptation V16", key=f"v16_{e['id']}"):
                try:
                    path = edit_log.append_adaptation_prompt({**e, "suggested_rule": new_rule})
                    edit_log.mark_applied(e["id"], "adaptation_prompt")
                    st.success(f"Appended to {path}")
                except Exception as ex:
                    st.error(str(ex))
        with x3:
            if st.button("→ Script notes", key=f"scr_{e['id']}"):
                try:
                    path = edit_log.append_script_notes({**e, "suggested_rule": new_rule})
                    edit_log.mark_applied(e["id"], "script_notes")
                    st.success(f"Appended to {path}")
                except Exception as ex:
                    st.error(str(ex))
        with x4:
            if st.button("Mark blueprint applied", key=f"bp_{e['id']}"):
                # Blueprint changes usually already happened on regen; this is bookkeeping
                edit_log.mark_applied(e["id"], "blueprint")
                st.success("Marked")
                st.rerun()

        if st.button("Apply all three docs", key=f"all_{e['id']}"):
            errors = []
            try:
                edit_log.append_learnings_md({**e, "suggested_rule": new_rule})
                edit_log.mark_applied(e["id"], "learnings_md")
            except Exception as ex:
                errors.append(str(ex))
            try:
                edit_log.append_adaptation_prompt({**e, "suggested_rule": new_rule})
                edit_log.mark_applied(e["id"], "adaptation_prompt")
            except Exception as ex:
                errors.append(str(ex))
            try:
                edit_log.append_script_notes({**e, "suggested_rule": new_rule})
                edit_log.mark_applied(e["id"], "script_notes")
            except Exception as ex:
                errors.append(str(ex))
            edit_log.mark_applied(e["id"], "blueprint")
            if errors:
                st.error("; ".join(errors))
            else:
                st.success("Applied to LEARNINGS.md, V16, and SCRIPT_NOTES.md")
            st.rerun()

st.divider()
st.subheader("How feedback flows")
st.markdown(
    """
1. **Scenes → Regen with feedback** — appends text into that clip’s `visual_prompt` in the active blueprint (`nickandme.clips.grok.json`) and logs before/after.
2. **Edit Log → Adaptation V16** — adds a permanent rule under a GUI learnings section (for next full adaptations).
3. **Edit Log → Script notes** — writes `review_feedback/SCRIPT_NOTES.md` so engine changes are intentional, not auto-patched into `renderer/`.
4. **LEARNINGS.md** — human-readable running diary of all review decisions.
"""
)
