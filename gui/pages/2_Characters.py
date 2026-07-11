"""Character design & cascade regen page (optimized for fast UI loads)."""
from __future__ import annotations

import os
import streamlit as st

from review_app import pipeline_api as api
from review_app.thumbnails import ui_image_path

st.set_page_config(page_title="Characters", page_icon="👤", layout="wide")
st.title("👤 Characters")
st.caption(
    "View locked references, generate 3 variants, click the best to lock, "
    "or cascade-regenerate clips that use a character."
)


def _char_cache_key() -> str:
    """Invalidate when blueprint/state/character assets change."""
    parts = []
    for p in ("nickandme.clips.grok.json", "nickandme.scenes.json", "pipeline_state.json"):
        try:
            parts.append(f"{p}:{os.path.getmtime(p):.0f}")
        except OSError:
            parts.append(f"{p}:0")
    try:
        # folder mtime changes when refs/variants written
        parts.append(f"chars:{os.path.getmtime('assets/characters'):.0f}")
    except OSError:
        parts.append("chars:0")
    return "|".join(parts)


@st.cache_data(show_spinner=False, ttl=120)
def _cached_list_characters(cache_key: str) -> list:
    _ = cache_key
    return api.list_characters(light=False)


@st.cache_data(show_spinner=False, ttl=600)
def _cached_thumb(path: str, mtime: float, max_px: int = 384) -> str:
    _ = mtime
    return ui_image_path(path, max_px=max_px)


def _show_image(path: str, caption: str = "", width: int = 320) -> None:
    if not path or not os.path.isfile(path):
        st.caption("(missing image)")
        return
    try:
        mtime = os.path.getmtime(path)
    except OSError:
        mtime = 0.0
    thumb = _cached_thumb(path, mtime, 384)
    st.image(thumb, caption=caption or None, width=width)


try:
    ck = _char_cache_key()
    if st.session_state.get("_chars_ck") != ck:
        # Drop list cache when assets change (st.cache_data keys by ck)
        st.session_state["_chars_ck"] = ck
    with st.spinner("Loading cast…"):
        chars = _cached_list_characters(ck)
except Exception as e:
    st.error(str(e))
    st.stop()

if "selected_char" not in st.session_state:
    st.session_state.selected_char = chars[0]["key"] if chars else None

col_nav, col_main = st.columns([1, 3])

with col_nav:
    st.subheader("Cast")
    for c in chars:
        badge = "✅" if c["locked"] else "⬜"
        stale_n = int(c.get("stale_clip_count") or 0)
        stale_mark = f" ⚠️{stale_n}" if stale_n else ""
        label = f"{badge} {c['key']} ({c['clip_count']} clips){stale_mark}"
        if st.button(label, key=f"nav_{c['key']}", use_container_width=True):
            st.session_state.selected_char = c["key"]
            st.rerun()

with col_main:
    key = st.session_state.selected_char
    if not key:
        st.info("No character seeds in blueprint.")
        st.stop()

    char = next((c for c in chars if c["key"] == key), None)
    if not char:
        st.warning("Character not found.")
        st.stop()

    st.header(char["key"])
    meta = []
    if char.get("age_band"):
        meta.append(f"age_band=`{char['age_band']}`")
    if char.get("variant_of"):
        meta.append(f"variant_of=`{char['variant_of']}`")
    rev = int(char.get("revision") or 0)
    meta.append(f"design rev **{rev}**")
    if char.get("revision_updated_at"):
        meta.append(f"updated `{char['revision_updated_at']}`")
    if meta:
        st.caption(" · ".join(meta))

    # Description can be long — collapse by default
    with st.expander("Description", expanded=False):
        st.write(char.get("description") or "_No description_")

    stale_n = int(char.get("stale_clip_count") or 0)
    if stale_n:
        st.warning(
            f"**{stale_n} generated clip(s) are out of date** after this character’s last redesign. "
            f"Pipeline will not reuse them as “done” until you regen. "
            f"Stale: {', '.join(f'S{s}C{c}' for s, c in (char.get('stale_clips') or [])[:20])}"
            + ("…" if stale_n > 20 else "")
        )
        if char.get("revision_reason"):
            st.caption(f"Last change: {char['revision_reason']}")

    st.subheader("Locked reference")
    if char["locked"]:
        _show_image(char["ref_path"], caption=os.path.basename(char["ref_path"]), width=280)
    else:
        st.warning("No locked reference yet — generate variants and pick one.")

    # ---- Voice lock (Stage 0 identity for native Grok audio) ----
    st.subheader("Voice lock")
    st.caption(
        "Locked vocal identity for **native** video audio. Injected into Grok AUDIO prompts "
        "every time this speaker talks — not a separate TTS engine."
    )
    try:
        vinfo = api.get_character_voice(key)
    except Exception:
        vinfo = {}
    with st.expander("Edit voice profile", expanded=bool(vinfo.get("voice_profile"))):
        v_label = st.text_input(
            "Voice label",
            value=vinfo.get("voice_label") or key,
            key=f"vlabel_{key}",
        )
        v_profile = st.text_area(
            "Voice profile (prompt lock — be specific: age, pitch, pace, accent, energy)",
            value=vinfo.get("voice_profile") or "",
            height=110,
            key=f"vprof_{key}",
            placeholder=(
                "e.g. American male late 20s, soft mid baritone, tired Midwestern, "
                "measured ~140 wpm, warm but weary; identical every scene"
            ),
        )
        if st.button("💾 Save voice lock", key=f"vsave_{key}"):
            try:
                api.save_character_voice(
                    key,
                    voice_profile=v_profile,
                    voice_label=v_label,
                )
                _cached_list_characters.clear()
                st.success("Voice lock saved to active blueprint — new clips will use it.")
                st.rerun()
            except Exception as e:
                st.error(str(e))
        if vinfo.get("voice_profile"):
            st.info(f"**Active lock:** {vinfo.get('voice_profile')[:280]}…")

    b1, b2, b3 = st.columns(3)
    with b1:
        if st.button("🎲 Generate 3 variants", type="primary", key="gen_var"):
            with st.spinner("Calling image model… this can take a minute"):
                try:
                    paths = api.generate_character_variants(key)
                    _cached_list_characters.clear()
                    st.success(f"Saved {len(paths)} variants")
                    st.rerun()
                except Exception as e:
                    st.error(str(e))
    with b2:
        if st.button("🔓 Unlock (delete locked ref)", key="unlock"):
            api.unlock_character(key)
            _cached_list_characters.clear()
            st.success("Unlocked")
            st.rerun()
    with b3:
        if st.button("🔄 Refresh list", key="refresh_chars"):
            api.reload_engine()
            _cached_list_characters.clear()
            st.rerun()

    st.subheader("Pick best variant")
    variants = char.get("variants") or []
    if not variants:
        st.info("No open variants on disk. Click **Generate 3 variants**.")
    else:
        cols = st.columns(min(3, len(variants)))
        for i, vp in enumerate(variants):
            idx = i + 1
            if "_variant_0" in vp:
                try:
                    idx = int(vp.split("_variant_0")[-1].split(".")[0])
                except ValueError:
                    pass
            with cols[i % 3]:
                _show_image(vp, caption=f"Option {idx}", width=220)
                if st.button(f"Lock option {idx}", key=f"lock_{key}_{idx}"):
                    try:
                        path = api.lock_character_variant(key, idx)
                        _cached_list_characters.clear()
                        st.success(f"Locked → {path}")
                        st.rerun()
                    except Exception as e:
                        st.error(str(e))

    st.divider()
    # Heavy cascade UI only when opened — avoids double full-blueprint disk probes every load
    with st.expander("Clips & cascade regenerate", expanded=False):
        only_existing = st.checkbox(
            "Only clips already generated on disk (recommended)",
            value=True,
            help="Off = every blueprint mention of this character (can include never-rendered scenes).",
            key=f"only_exist_{key}",
        )

        # One detail fetch for selected filter; cheap on-disk probe only for that set
        with st.spinner("Indexing clips…"):
            detail = api.clips_using_character_detail(key, only_existing=only_existing)
            if only_existing:
                # total mentions from cached list_characters row
                n_all = int(char.get("clip_count") or 0)
                n_disk = len(detail)
            else:
                n_all = len(detail)
                n_disk = sum(1 for r in detail if r["on_disk"])

        st.caption(
            f"Blueprint mentions: **{n_all}** · "
            f"On disk (this filter): **{n_disk if only_existing else sum(1 for r in detail if r['on_disk'])}** · "
            f"Showing: **{len(detail)}**"
        )

        selected_pairs: list = []
        if not detail:
            if only_existing:
                st.info(
                    "No generated clips for this character yet. "
                    "Uncheck “only on disk” only if you intentionally want first-time renders."
                )
            else:
                st.write("_None found in visual prompts._")
        else:
            by_scene: dict = {}
            for r in detail:
                by_scene.setdefault(r["scene"], []).append(r)

            scene_options = sorted(by_scene.keys())
            pick_scenes = st.multiselect(
                "Scenes to include",
                options=scene_options,
                default=scene_options,
                format_func=lambda s: f"Scene {s} ({len(by_scene[s])} clip(s))",
                key=f"cascade_scenes_{key}",
            )

            clip_options = []
            for sn in pick_scenes:
                for r in by_scene[sn]:
                    disk = "on disk" if r["on_disk"] else "NOT generated"
                    clip_options.append(
                        (r["scene"], r["clip"], f"S{r['scene']}C{r['clip']} ({disk})")
                    )

            labels = [t[2] for t in clip_options]
            picked_labels = st.multiselect(
                "Clips to regenerate",
                options=labels,
                default=labels,
                key=f"cascade_clips_{key}",
            )
            selected_pairs = [
                (t[0], t[1]) for t in clip_options if t[2] in picked_labels
            ]
            st.write(f"**{len(selected_pairs)}** clip(s) selected.")

        st.subheader("Cascade regenerate")
        st.warning(
            "This **wipes and re-renders** the selected clips only. "
            "It does not invent new story scenes — it only redoes clips that match the filters. "
            "With “only on disk” checked, never-generated clips are skipped."
        )
        cascade_feedback = st.text_area(
            "Optional feedback to append to each clip prompt",
            placeholder="e.g. plain unmarked pizza shirt, no name tag; match locked child proportions",
            key="cascade_fb",
        )
        dry = st.checkbox("Dry run (list only)", value=True)
        if st.button("Run cascade", key="cascade_go"):
            if not detail:
                st.error("Nothing to regenerate with current filters.")
            elif not selected_pairs:
                st.error("Select at least one clip.")
            else:
                with st.spinner("Working…"):
                    try:
                        hits = api.cascade_regen_character(
                            key,
                            feedback=cascade_feedback.strip(),
                            dry_run=dry,
                            only_existing=only_existing,
                            selected=selected_pairs,
                        )
                        _cached_list_characters.clear()
                        if dry:
                            st.info(f"Would regenerate {len(hits)} clips: {hits}")
                        else:
                            st.success(f"Regenerated {len(hits)} clips: {hits}")
                    except Exception as e:
                        st.error(str(e))
