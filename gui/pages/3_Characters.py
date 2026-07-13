"""Character design & cascade regen page (optimized for fast UI loads)."""
from __future__ import annotations

import os
import streamlit as st

from review_app import pipeline_api as api
from review_app.thumbnails import ui_image_path

st.title("👤 Characters")
st.caption(
    "Cast comes from **Stage 1** seeds. For picture books, lock a **book plate** "
    "directly, or generate Grok variants and lock one of those. "
    "**Narrator** is voice-only — no reference image."
)


def _char_cache_key() -> str:
    """Invalidate when project / blueprint / stage1 / assets change."""
    parts = []
    try:
        info = api.active_project_info()
        parts.append(f"proj:{info.get('id')}")
    except Exception:
        parts.append("proj:?")
    try:
        paths = api.stage1_paths()
        proj = paths.get("project") or "."
        candidates = [
            paths.get("scenes_json") or "",
            os.path.join(proj, "pipeline_state.json"),
            os.path.join(proj, "pipeline_config.json"),
        ]
        cfg = api.get_config()
        bp = str(cfg.get("blueprint_file") or "blueprint.clips.grok.json")
        candidates.insert(0, bp if os.path.isabs(bp) else os.path.join(proj, bp))
    except Exception:
        candidates = [
            "blueprint.clips.grok.json",
            "scenes.json",
            "pipeline_state.json",
        ]
    for p in candidates:
        if not p:
            continue
        try:
            parts.append(f"{os.path.basename(str(p))}:{os.path.getmtime(p):.0f}")
        except OSError:
            parts.append(f"{os.path.basename(str(p))}:0")
    try:
        parts.append(f"chars:{os.path.getmtime('assets/characters'):.0f}")
    except OSError:
        parts.append("chars:0")
    return "|".join(parts)


@st.cache_data(show_spinner=False, ttl=30)
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


# ---- Load cast (always prefer Stage 1 seeds on empty blueprint) ----
sync: dict = {}
chars: list = []
try:
    # Sync Stage 1 → blueprint when needed
    sync = api.ensure_blueprint_character_seeds_from_stage1(force=False) or {}
    # If Stage 1 has seeds but list is empty, force sync + drop cache
    s1_seeds = api.stage1_character_seeds()
    ck = _char_cache_key()
    with st.spinner("Loading cast…"):
        chars = list(_cached_list_characters(ck) or [])
    if not chars and s1_seeds:
        _cached_list_characters.clear()
        api.reload_engine()
        sync = api.ensure_blueprint_character_seeds_from_stage1(force=True) or {}
        ck = _char_cache_key() + "|forced"
        chars = list(api.list_characters(light=False) or [])
except Exception as e:
    st.error(str(e))
    st.stop()

if sync.get("synced"):
    st.caption(
        f"Synced **{sync.get('count')}** character seeds from Stage 1 into the blueprint."
    )

# Stage 1 diagnostic so "no characters" is never silent
s1_status = api.stage1_status()
s1_n = int(s1_status.get("characters") or 0) if s1_status.get("present") else 0
if chars:
    st.success(
        f"**{len(chars)} character seed(s)** from Stage 1 / blueprint: "
        + ", ".join(
            (c.get("display_name") or c.get("key") or "?") for c in chars
        )
    )
elif s1_n > 0:
    st.warning(
        f"Stage 1 reports **{s1_n}** characters, but the cast list is empty. "
        "Click **Reload cast from Stage 1**."
    )
    if st.button("Reload cast from Stage 1", type="primary"):
        _cached_list_characters.clear()
        api.reload_engine()
        api.ensure_blueprint_character_seeds_from_stage1(force=True)
        st.rerun()
    st.stop()
else:
    st.error(
        "No character seeds found. "
        "Run **Adaptation → Stage 1** with a clean `book_full.txt` first "
        "(seeds live in `scenes.json` → `character_seed_tokens`)."
    )
    st.stop()

# Drop stale selection from a previous project / old OCR names (e.g. Character_Duster)
valid_keys = {c["key"] for c in chars}
if st.session_state.get("selected_char") not in valid_keys:
    st.session_state.selected_char = chars[0]["key"]

col_nav, col_main = st.columns([1, 3])

with col_nav:
    st.subheader("Cast")
    for c in chars:
        badge = "✅" if c["locked"] else "⬜"
        stale_n = int(c.get("stale_clip_count") or 0)
        stale_mark = f" ⚠️{stale_n}" if stale_n else ""
        name = c.get("display_name") or c.get("name") or c["key"]
        voice_mark = " 🎙" if c.get("voice_only") else ""
        label = f"{badge} {name}{voice_mark}{stale_mark}"
        if st.button(label, key=f"nav_{c['key']}", width="stretch"):
            st.session_state.selected_char = c["key"]
            st.rerun()
    st.caption(f"{len(chars)} seed(s) · Stage 1 count {s1_n}")

with col_main:
    key = st.session_state.selected_char
    char = next((c for c in chars if c["key"] == key), None)
    if not char:
        st.session_state.selected_char = chars[0]["key"]
        st.rerun()

    display = char.get("display_name") or char.get("name") or char["key"]
    st.header(display)
    st.caption(
        f"Seed id: `{char['key']}` · "
        f"{'locked' if char.get('locked') else 'not locked'} · "
        f"{int(char.get('clip_count') or 0)} clips in Stage 2 plan"
    )

    # Voice-only cast (narrator) — no likeness plates
    seed = {}
    try:
        seed = (
            api.get_engine()
            .blueprint.get("global_production_variables", {})
            .get("character_seed_tokens", {})
            .get(key)
            or {}
        )
    except Exception:
        seed = {}
    is_voice_only = bool(
        char.get("voice_only")
        or api.is_voice_only_character(key, seed if isinstance(seed, dict) else None)
    )
    if is_voice_only:
        st.info(
            "**Voice only (narrator)** — **no reference image**. "
            "Set voice below for Grok native VO. Book plates and locks do not apply."
        )
        try:
            scrub = api.scrub_voice_only_character_images(key)
            if scrub.get("removed_files"):
                _cached_list_characters.clear()
                st.caption(
                    "Removed mistaken narrator image file(s): "
                    + ", ".join(
                        os.path.basename(p) for p in (scrub.get("removed_files") or [])[:6]
                    )
                )
        except Exception:
            pass
    else:
        book_refs = seed.get("design_reference_images") or seed.get(
            "book_reference_images"
        ) or []

        if book_refs:
            st.subheader("Book plates — click to lock")
            st.caption(
                "These are from the PDF. **Use as locked ref** sets this plate as the "
                "character look for video (no Grok variants required)."
            )
            cols = st.columns(min(3, len(book_refs)))
            for i, rp in enumerate(book_refs[:3]):
                with cols[i % len(cols)]:
                    _show_image(rp, caption=os.path.basename(str(rp)), width=200)
                    if st.button(
                        f"Use as locked ref",
                        key=f"lock_bookref_{key}_{i}",
                        type="primary" if i == 0 else "secondary",
                        width="stretch",
                    ):
                        try:
                            path = api.lock_character_from_image(key, str(rp))
                            _cached_list_characters.clear()
                            st.success(f"Locked book plate → `{path}`")
                            st.rerun()
                        except Exception as e:
                            st.error(str(e))
        else:
            st.warning(
                "No book plates on this seed yet. "
                "Re-run Stage 1 (after clean import) or re-import to attach book plates."
            )

        if char.get("locked") and char.get("ref_path"):
            st.subheader("Locked reference (active)")
            _show_image(char["ref_path"], caption="Active lock — used in video", width=320)
        else:
            st.info(
                "No locked reference yet. **Use as locked ref** on a book plate above, "
                "or generate Grok variants below and lock one of those."
            )

    # Voice — expanded for narrator (primary setup); collapsed for on-screen cast
    with st.expander(
        "Voice setup (Grok native speech)",
        expanded=is_voice_only,
    ):
        vinfo = api.get_character_voice(key)
        st.caption(
            "This **voice_profile** is injected into Grok video prompts so the model "
            "speaks as this character (narrator VO or on-camera). Film audio is Grok-native only."
        )

        if is_voice_only:
            presets = getattr(api, "NARRATOR_VOICE_PRESETS", {}) or {}
            preset_labels = {
                "male_warm": "Male · warm storyteller",
                "female_warm": "Female · warm storyteller",
                "male_deep": "Male · deep",
                "female_bright": "Female · bright",
                "custom": "Custom (edit text below)",
            }
            # Detect current preset
            cur_g = (vinfo.get("voice_gender") or "").lower()
            cur_p = (vinfo.get("voice_profile") or "").strip()
            default_preset = "custom"
            for pk, pv in presets.items():
                if (pv.get("voice_profile") or "").strip() == cur_p:
                    default_preset = pk
                    break
            if default_preset == "custom" and cur_g in ("male", "female"):
                # partial match by gender
                for pk, pv in presets.items():
                    if pv.get("voice_gender") == cur_g and "warm" in pk:
                        default_preset = pk
                        break
            opts = list(presets.keys()) + ["custom"]
            try:
                pidx = opts.index(default_preset)
            except ValueError:
                pidx = len(opts) - 1
            pick = st.selectbox(
                "Narrator voice preset",
                options=opts,
                index=pidx,
                format_func=lambda k: preset_labels.get(k, k),
                key=f"narr_preset_{key}",
                help="Sets gender + Grok voice_profile text used on every narrator clip",
            )
            if pick != "custom" and pick in presets:
                if st.button("Apply preset", key=f"apply_preset_{key}", type="primary"):
                    try:
                        pr = presets[pick]
                        api.save_character_voice(
                            key,
                            voice_profile=pr.get("voice_profile"),
                            voice_label=pr.get("voice_label"),
                            voice_gender=pr.get("voice_gender"),
                        )
                        _cached_list_characters.clear()
                        st.success(
                            f"Narrator set to **{preset_labels.get(pick, pick)}** "
                            f"({pr.get('voice_gender')})"
                        )
                        st.rerun()
                    except Exception as e:
                        st.error(str(e))

        gender_opts = ["auto", "male", "female", "neutral"]
        cur_gender = (vinfo.get("voice_gender") or "auto").lower()
        if cur_gender not in gender_opts:
            cur_gender = "auto"
        vg = st.selectbox(
            "Voice gender (for Grok)",
            options=gender_opts,
            index=gender_opts.index(cur_gender),
            key=f"vg_{key}",
            help="Explicit male/female helps Grok pick the right narrator timbre",
        )
        vp = st.text_area(
            "voice_profile (used in Grok AUDIO prompt)",
            value=vinfo.get("voice_profile") or "",
            height=100,
            key=f"vp_{key}",
        )
        vl = st.text_input(
            "voice_label",
            value=vinfo.get("voice_label") or "",
            key=f"vl_{key}",
        )
        if st.button("Save voice", key=f"save_voice_{key}", type="primary"):
            try:
                api.save_character_voice(
                    key,
                    voice_profile=vp,
                    voice_label=vl,
                    voice_gender=None if vg == "auto" else vg,
                )
                _cached_list_characters.clear()
                st.success("Voice saved for Grok generation")
                st.rerun()
            except Exception as e:
                st.error(str(e))

    if is_voice_only:
        if st.button("🔄 Refresh list", key="refresh_chars"):
            _cached_list_characters.clear()
            api.reload_engine()
            api.ensure_blueprint_character_seeds_from_stage1(force=True)
            st.rerun()
    else:
        b1, b2, b3 = st.columns(3)
        with b1:
            if st.button("🎲 Generate 3 variants", type="primary", key="gen_var"):
                with st.spinner(
                    "Calling image model with book page references when available…"
                ):
                    try:
                        result = api.generate_character_variants(key)
                        paths = (
                            result.get("paths")
                            if isinstance(result, dict)
                            else result
                        )
                        mode = (
                            result.get("mode")
                            if isinstance(result, dict)
                            else "unknown"
                        )
                        refs = (
                            result.get("book_refs")
                            if isinstance(result, dict)
                            else []
                        )
                        _cached_list_characters.clear()
                        if mode in ("book_edit", "book_edit_multi"):
                            st.success(
                                f"Saved {len(paths)} variants using **book art** "
                                f"({', '.join(os.path.basename(r) for r in (refs or [])[:3])})."
                            )
                        else:
                            st.warning(
                                f"Saved {len(paths)} variants in **{mode}** mode "
                                "(may not match the book)."
                            )
                        st.rerun()
                    except Exception as e:
                        st.error(str(e))
            st.caption(
                "Uses book plates via Grok **image edit** when available. "
                "Does not re-run Stage 1."
            )
        with b2:
            if st.button("🔓 Unlock (delete locked ref)", key="unlock"):
                api.unlock_character(key)
                _cached_list_characters.clear()
                st.success("Unlocked")
                st.rerun()
        with b3:
            if st.button("🔄 Refresh list", key="refresh_chars"):
                _cached_list_characters.clear()
                api.reload_engine()
                api.ensure_blueprint_character_seeds_from_stage1(force=True)
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
                        idx = i + 1
                with cols[i % len(cols)]:
                    _show_image(vp, caption=f"Variant {idx}", width=220)
                    if st.button(f"Lock {idx}", key=f"lock_{key}_{idx}"):
                        try:
                            path = api.lock_character_variant(key, idx)
                            _cached_list_characters.clear()
                            st.success(f"Locked → `{path}`")
                            st.rerun()
                        except Exception as e:
                            st.error(str(e))

    if is_voice_only:
        st.stop()

    st.divider()
    with st.expander("Clips & cascade regenerate", expanded=False):
        only_existing = st.checkbox(
            "Only clips already generated on disk (recommended)",
            value=True,
            help="Off = every blueprint mention of this character (can include never-rendered scenes).",
            key=f"only_exist_{key}",
        )
        with st.spinner("Indexing clips…"):
            detail = api.clips_using_character_detail(key, only_existing=only_existing)
            if only_existing:
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
            "It does not invent new story scenes — it only redoes clips that match the filters."
        )
        cascade_feedback = st.text_area(
            "Optional feedback to append to each clip prompt",
            placeholder="e.g. match locked proportions; plain shirt",
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
