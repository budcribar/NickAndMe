"""Scene list + clip drill-down review page."""
from __future__ import annotations

import streamlit as st

import review_app  # noqa: F401 — path bootstrap for renderer
from review_app import pipeline_api as api

st.title("🎞️ Scenes & Clips")

# ---- Stage 2 gate: empty blueprint needs clip plan from Stage 1 ----
try:
    s2 = api.stage2_status()
except Exception:
    s2 = {}

if not s2.get("stage2_ready"):
    st.warning(
        "No Stage 2 clip plan yet — this page lists **blueprint** scenes/clips, "
        "not the Stage 1 bible alone."
    )
    if s2.get("stage1_exists"):
        st.info(
            f"Stage 1 is ready (**{s2.get('stage1_scenes', 0)}** scenes). "
            "Generate the Grok clip plan to populate Scenes & Clips."
        )
        if st.button(
            "▶ Generate Stage 2 plan",
            type="primary",
            key="scenes_gen_stage2",
            help="Converts Stage 1 beats → clip durations, visual prompts, audio payloads",
        ):
            try:
                with st.spinner("Building Stage 2 clip plan…"):
                    summary = api.run_stage2_from_stage1()
                st.success(
                    f"Stage 2 written: **{summary.get('scenes')}** scenes · "
                    f"**{summary.get('clips')}** clips · "
                    f"~{int(summary.get('duration_sec') or 0)}s"
                )
                if summary.get("validation_issues"):
                    with st.expander("Validation notes (non-blocking)"):
                        st.code("\n".join(summary["validation_issues"][:30]))
                st.rerun()
            except Exception as e:
                st.error(str(e))
    else:
        st.error(
            "Stage 1 is missing. Go to **Adaptation** → Import book → Run Stage 1, "
            "then return here to generate Stage 2."
        )
    st.caption(
        "CLI: `python scripts/two_stage_adaptation/stage2_plan_grok.py "
        "--stage1 projects/<id>/scenes.json --out projects/<id>/blueprint.clips.grok.json`"
    )
    st.stop()

try:
    # Light list for navigation (no per-scene cost math × 90)
    scenes = api.list_scenes(light=True)
except Exception as e:
    st.error(str(e))
    st.stop()

if not scenes:
    st.info(
        "Blueprint reports Stage 2 ready but list is empty — try **Generate Stage 2** again "
        "or reload the app."
    )
    if st.button("▶ Re-generate Stage 2 plan", key="scenes_regen_stage2_empty"):
        try:
            with st.spinner("Building Stage 2 clip plan…"):
                summary = api.run_stage2_from_stage1()
            st.success(
                f"{summary.get('scenes')} scenes · {summary.get('clips')} clips"
            )
            st.rerun()
        except Exception as e:
            st.error(str(e))
    st.stop()

if "scene_num" not in st.session_state:
    # Prefer first incomplete / with clips
    st.session_state.scene_num = scenes[0]["scene_number"] if scenes else 1
if "clip_num" not in st.session_state:
    st.session_state.clip_num = None

# ---- Scene picker ----
if st.session_state.clip_num is None:
    st.subheader("All scenes")
    with st.expander("Stage 2 plan", expanded=False):
        st.caption(
            f"{s2.get('stage2_scenes', 0)} scenes · {s2.get('stage2_clips', 0)} clips "
            f"from Stage 1 ({s2.get('stage1_scenes', 0)} bible scenes)."
        )
        if st.button(
            "🔁 Re-generate Stage 2 plan",
            key="scenes_regen_stage2",
            help="Replaces blueprint clip plans from current Stage 1 (backs up previous file)",
        ):
            try:
                with st.spinner("Rebuilding Stage 2 clip plan…"):
                    summary = api.run_stage2_from_stage1()
                st.success(
                    f"Updated: **{summary.get('scenes')}** scenes · "
                    f"**{summary.get('clips')}** clips"
                )
                st.rerun()
            except Exception as e:
                st.error(str(e))
    if "playing_scene" not in st.session_state:
        st.session_state.playing_scene = None

    q = st.text_input("Filter setting text", "")
    st.caption("Open a scene for cost estimates, or use the **Cost** page for film-wide $.")
    rows = []
    for s in scenes:
        if q and q.lower() not in (s.get("setting") or "").lower():
            continue
        rows.append(s)

    # Inline player for the scene chosen via ▶
    play_sn = st.session_state.playing_scene
    if play_sn is not None:
        play_row = next((r for r in scenes if r["scene_number"] == play_sn), None)
        play_path = (play_row or {}).get("play_path") or (play_row or {}).get("composite_path")
        if not play_path:
            # light list may omit per-clip paths — resolve first on-disk clip
            from renderer import clip_output_path, file_is_usable

            for cn in range(1, int((play_row or {}).get("clip_count") or 0) + 1):
                p = clip_output_path(play_sn, cn)
                if file_is_usable(p, min_bytes=1024):
                    play_path = p
                    break
        with st.container(border=True):
            pc1, pc2 = st.columns([5, 1])
            with pc1:
                st.markdown(f"**Playing Scene {play_sn:02d}**")
                if play_path:
                    st.video(play_path)
                    src = "composite" if str(play_path).endswith("complete.mp4") else "clip"
                    st.caption(f"`{play_path}` ({src})")
                else:
                    st.info("No video on disk for this scene yet.")
            with pc2:
                if st.button("Close player", key="close_player"):
                    st.session_state.playing_scene = None
                    st.rerun()

    for s in rows:
        sn = s["scene_number"]
        stale_n = int(s.get("stale_clips") or 0)
        if s.get("dirty"):
            status = "🔁"
        elif stale_n:
            status = "⚠️"
        elif s.get("is_hero"):
            status = "⭐"
        elif s["approved"]:
            status = "✅"
        elif s["clips_on_disk"]:
            status = "📦"
        else:
            status = "·"
        stale_txt = f" · {stale_n} stale" if stale_n else ""
        dirty_txt = f" · dirty {s.get('dirty_cascade')}" if s.get("dirty") else ""
        hero_txt = f" · hero {s.get('hero_resolution')}" if s.get("is_hero") else ""
        label = (
            f"{status} Scene {sn:02d} — {s.get('setting', '')[:56]} "
            f"({s['clips_on_disk']}/{s['clip_count']} clips{stale_txt}{dirty_txt}{hero_txt})"
        )
        # open | play | badges (thumbs/costs deferred for speed)
        cols = st.columns([3.6, 0.5, 0.9])
        with cols[0]:
            if st.button(label, key=f"open_s{sn}", width="stretch"):
                st.session_state.scene_num = sn
                st.session_state.clip_num = 0  # 0 = scene overview
                st.session_state.playing_scene = None
                st.rerun()
        with cols[1]:
            if s.get("play_path") or s.get("composite_path"):
                if st.button("▶", key=f"play_s{sn}", help=f"Play scene {sn}", width="stretch"):
                    st.session_state.playing_scene = sn
                    st.rerun()
            else:
                st.caption("")
        with cols[2]:
            help_bits = []
            if s.get("dirty"):
                help_bits.append("dirty")
            if s.get("composite_path") or s.get("composite_exists"):
                help_bits.append("mux")
            if s.get("approved"):
                help_bits.append("ok")
            st.caption(" · ".join(help_bits) if help_bits else "")

    st.stop()

# ---- Scene overview or clip detail ----
sn = int(st.session_state.scene_num)
cn = st.session_state.clip_num

scene = api.get_scene(sn)
if not scene:
    st.error(f"Scene {sn} not found")
    st.stop()

clips = api.list_clips(sn)
scene_meta = next((x for x in scenes if x["scene_number"] == sn), {})
on_disk_n = sum(1 for r in clips if r.get("on_disk"))
total_n = len(clips)
missing_n = total_n - on_disk_n
draft_res = str(api.get_config().get("resolution", "480p"))

# =====================================================================
# SCENE OVERVIEW — simple path first; advanced tools collapsed
# =====================================================================
if cn is None or cn == 0:
    if st.button("← All scenes"):
        st.session_state.clip_num = None
        st.rerun()

    st.header(f"Scene {sn}: {scene.get('setting', '')}")
    st.caption(
        f"{total_n} clips · {on_disk_n} on disk · "
        f"~{scene.get('total_estimated_duration_seconds')}s · draft **{draft_res}**"
    )

    dirty = api.get_scene_dirty(sn)
    if dirty and (dirty.get("stage1") or dirty.get("stage2")):
        cascade = "stage1→stage2" if dirty.get("stage1") else "stage2"
        st.warning(
            f"Needs replan ({cascade}): {dirty.get('reason') or 'marked dirty'}"
        )

    # ---- Primary: generate video ----
    st.subheader("Generate video")
    if missing_n > 0:
        st.info(
            f"**{missing_n} of {total_n}** clips have no video yet. "
            "Click the button below to call Grok and create them (needs `XAI_API_KEY`)."
        )
    else:
        st.success(f"All **{total_n}** clips are on disk. Open a clip to review or re-generate one.")

    gcol1, gcol2 = st.columns([2, 1])
    with gcol1:
        gen_missing = st.checkbox(
            "Only missing clips (skip ones already on disk)",
            value=True,
            key=f"gen_missing_{sn}",
        )
        run_qa_scene = st.checkbox(
            "Run QA after each clip",
            value=True,
            key=f"gen_qa_{sn}",
        )
    with gcol2:
        try:
            est = api.scene_cost_estimate(
                sn, mode="all" if not gen_missing else "missing"
            ) or {}
        except Exception:
            # older cost helper may not have mode=missing
            try:
                est = api.scene_cost_estimate(sn, mode="all") or {}
            except Exception:
                est = {}
        if est:
            st.caption(
                f"Est. ~**${float(est.get('total_usd') or 0):.2f}** · "
                f"{est.get('clip_count', '?')} clips @ {est.get('resolution') or draft_res}"
            )

    gen_label = (
        f"▶ Generate missing clips ({missing_n})"
        if gen_missing and missing_n > 0
        else (
            f"▶ Generate all {total_n} clips"
            if not gen_missing
            else "▶ Generate scene clips"
        )
    )
    gen_disabled = total_n == 0 or (gen_missing and missing_n == 0)
    if st.button(
        gen_label,
        type="primary",
        key=f"gen_scene_{sn}",
        disabled=gen_disabled,
        help="Creates Grok video for each clip in order, then stitches the scene.",
    ):
        prog = st.progress(0.0, text="Starting…")
        log_box = st.empty()
        lines: list[str] = []

        def _on_prog(ev: dict) -> None:
            total = max(1, int(ev.get("total") or 1))
            idx = int(ev.get("index") or 0)
            event = ev.get("event") or ""
            if event == "done":
                frac = 1.0
            elif event in ("clip_done", "clip_error"):
                frac = min(1.0, idx / total)
            elif event == "clip_start":
                frac = min(0.99, max(0.0, (idx - 1) / total))
            else:
                frac = 0.02
            msg = ev.get("message") or event
            prog.progress(frac, text=msg)
            lines.append(msg)
            log_box.code("\n".join(lines[-20:]), language="text")

        try:
            with st.spinner(
                "Generating — each clip can take several minutes. Keep this tab open."
            ):
                summary = api.generate_scene_clips(
                    sn,
                    only_missing=bool(gen_missing),
                    run_qa=bool(run_qa_scene),
                    remux=True,
                    progress_cb=_on_prog,
                )
            done = summary.get("done") or []
            failed = summary.get("failed") or []
            if done and not failed:
                st.success(
                    f"Generated {len(done)} clip(s)"
                    + (
                        f" · remuxed `{summary.get('composite_path')}`"
                        if summary.get("composite_path")
                        else ""
                    )
                )
            elif done and failed:
                st.warning(
                    f"Partial: {len(done)} ok, {len(failed)} failed — "
                    f"{failed[0].get('error') if failed else ''}"
                )
            elif failed:
                st.error(failed[0].get("error") if failed else "Generation failed")
            else:
                st.info("Nothing to generate (all clips already on disk).")
            if summary.get("remux_error"):
                st.warning(f"Remux: {summary['remux_error']}")
            st.rerun()
        except Exception as e:
            st.error(str(e))

    # Play composite if any
    if scene_meta.get("composite_path") or scene_meta.get("play_path"):
        play_p = scene_meta.get("composite_path") or scene_meta.get("play_path")
        with st.expander("Play scene composite", expanded=on_disk_n == total_n and total_n > 0):
            st.video(play_p)

    # ---- Clip list (main navigation) ----
    st.subheader("Clips")
    st.caption("Open a clip to review, edit prompts, or re-generate just that one.")
    for row in clips:
        cnum = row["clip_number"]
        disk = "🟢 ready" if row["on_disk"] else "⚪ not generated"
        rev = row.get("review_status") or "pending"
        if row.get("stale"):
            rev_s = "stale"
        else:
            rev_s = {"pass": "pass", "fail": "fail", "pending": "pending"}.get(rev, rev)
        preview = (row.get("visual_prompt") or "")[:64].replace("\n", " ")
        c1, c2 = st.columns([4, 1])
        with c1:
            if st.button(
                f"Clip {cnum} · {disk} · {rev_s} — {preview}",
                key=f"open_c{cnum}",
                width="stretch",
            ):
                st.session_state.clip_num = cnum
                st.rerun()
        with c2:
            if not row["on_disk"]:
                if st.button("▶ Gen", key=f"quick_gen_{sn}_{cnum}", help=f"Generate clip {cnum} only"):
                    with st.spinner(f"Generating S{sn:02d} C{cnum}…"):
                        try:
                            path = api.regen_clip(
                                sn, int(cnum), run_qa=True, rebuild_wip=False
                            )
                            st.success(path)
                            st.rerun()
                        except Exception as e:
                            st.error(str(e))

    # ---- Secondary: finish scene ----
    st.divider()
    st.subheader("When clips look good")
    a1, a2, a3 = st.columns(3)
    with a1:
        if st.button(
            "Approve scene",
            type="primary" if on_disk_n == total_n and total_n else "secondary",
            key=f"approve_{sn}",
            help="Mark draft scene OK for the WIP timeline",
        ):
            try:
                api.approve_scene(sn)
                st.success("Scene approved.")
            except Exception as e:
                st.error(str(e))
    with a2:
        if st.button("Remux scene", key=f"remux_{sn}"):
            with st.spinner("FFmpeg remux…"):
                try:
                    path = api.remux_scene(sn)
                    st.success(path or "No clips to remux")
                except Exception as e:
                    st.error(str(e))
    with a3:
        if st.button("Remux + WIP movie", key=f"wip_{sn}"):
            with st.spinner("Remux + movie_wip.mp4…"):
                try:
                    path = api.remux_scenes_and_rebuild_wip(
                        [sn], reason=f"scene {sn} remux + WIP"
                    )
                    st.success(path or "Done")
                except Exception as e:
                    st.error(str(e))

    # ---- Advanced (collapsed) ----
    with st.expander("Cost estimate", expanded=False):
        c_all = api.scene_cost_estimate(sn, mode="all") or {}
        c_ex = api.scene_cost_estimate(sn, mode="existing") or {}
        k1, k2 = st.columns(2)
        k1.metric("All clips", f"${float(c_all.get('total_usd') or 0):.2f}")
        k2.metric("On disk only", f"${float(c_ex.get('total_usd') or 0):.2f}")
        st.caption(
            f"`{c_all.get('model_name')}` @ {c_all.get('resolution')} · "
            f"{c_all.get('total_duration_sec')}s"
        )

    with st.expander("Learning cascade (replan flags)", expanded=False):
        st.caption(
            "Mark for Stage 1/2 replan when the failure is not a single bad take. "
            "Does not auto-run planners."
        )
        d1, d2 = st.columns(2)
        with d1:
            if st.button("Needs Stage 2 replan", key=f"dirty_s2_{sn}", width="stretch"):
                try:
                    api.mark_scene_needs_replan(
                        sn, cascade="stage2", reason=f"Manual Stage 2 replan for scene {sn}"
                    )
                    st.success("Marked dirty (stage2).")
                    st.rerun()
                except Exception as e:
                    st.error(str(e))
        with d2:
            if st.button("Needs Stage 1→2 re-bible", key=f"dirty_s1_{sn}", width="stretch"):
                try:
                    api.mark_scene_needs_replan(
                        sn, cascade="stage1", reason=f"Manual Stage 1→2 for scene {sn}"
                    )
                    st.success("Marked dirty (stage1→stage2).")
                    st.rerun()
                except Exception as e:
                    st.error(str(e))
        if dirty and (dirty.get("stage1") or dirty.get("stage2")):
            c1, c2 = st.columns(2)
            with c1:
                if st.button("Clear Stage 2 dirty", key=f"clr_s2_{sn}"):
                    api.clear_scene_replan_flag(sn, keys=["stage2"])
                    st.rerun()
            with c2:
                if st.button("Clear all dirty", key=f"clr_all_{sn}"):
                    api.clear_scene_replan_flag(sn)
                    st.rerun()

    with st.expander("Hero / delivery pass (later)", expanded=False):
        hero_info = scene_meta.get("hero") or api.get_engine().get_scene_hero(sn)
        if hero_info:
            st.success(
                f"Hero locked @ **{hero_info.get('resolution')}** "
                f"({hero_info.get('clip_count')} clips)"
            )
        else:
            st.caption(
                f"Draft is **{draft_res}**. After you like the scene, hero regen "
                "re-renders on-disk clips at delivery resolution."
            )
        h1, h2, h3 = st.columns(3)
        with h1:
            hero_res = st.selectbox(
                "Hero resolution",
                options=["720p", "1080p", "480p"],
                index=0,
                key=f"hero_res_{sn}",
            )
        with h2:
            hero_only_disk = st.checkbox(
                "Only clips on disk", value=True, key=f"hero_disk_{sn}"
            )
        with h3:
            hero_qa = st.checkbox("Run QA", value=True, key=f"hero_qa_{sn}")
        hero_approve = st.checkbox(
            "Approve after success", value=True, key=f"hero_appr_{sn}"
        )
        try:
            hero_est = api.hero_cost_note(sn, resolution=hero_res)
            st.caption(
                f"Est. @ {hero_res}: ${float(hero_est.get('total_usd') or 0):.2f} "
                f"({hero_est.get('clip_count')} clips)"
            )
        except Exception:
            pass
        hb1, hb2 = st.columns(2)
        with hb1:
            if st.button(f"Hero regen at {hero_res}", key=f"hero_go_{sn}"):
                with st.spinner(f"Hero regen @ {hero_res}…"):
                    try:
                        meta = api.hero_regen_scene(
                            sn,
                            resolution=hero_res,
                            only_existing=hero_only_disk,
                            run_qa=hero_qa,
                            approve_after=hero_approve,
                        )
                        failed = meta.get("failed") or []
                        if failed:
                            st.warning(f"Partial failures: {failed}")
                        else:
                            st.success(f"Hero complete @ {meta.get('resolution')}")
                        st.rerun()
                    except Exception as e:
                        st.error(str(e))
        with hb2:
            if hero_info and st.button("Clear hero flag", key=f"hero_clear_{sn}"):
                try:
                    api.clear_scene_hero(sn)
                    st.success("Hero flag cleared.")
                    st.rerun()
                except Exception as e:
                    st.error(str(e))

    with st.expander("Model comparison / variants (optional)", expanded=False):
        pref = api.scene_video_settings(sn)
        models = api.available_video_models()
        model_labels = [
            f"{m.get('label') or m.get('model_name')} ({m.get('provider')}/{m.get('model_name')})"
            for m in models
        ]
        label_to_model = dict(zip(model_labels, models))
        st.caption(
            f"Preferred: **{pref.get('provider')}** / `{pref.get('model_name')}`"
        )
        pick = st.selectbox(
            "Set preferred model",
            options=["(use global default)"] + model_labels,
            key=f"pref_model_{sn}",
        )
        if st.button("Save preferred model", key=f"save_pref_{sn}"):
            try:
                if pick.startswith("(use"):
                    api.set_scene_video_settings(sn, clear=True)
                else:
                    m = label_to_model[pick]
                    api.set_scene_video_settings(
                        sn, provider=m.get("provider"), model_name=m.get("model_name")
                    )
                st.success("Saved.")
                st.rerun()
            except Exception as e:
                st.error(str(e))
        if st.button("Snapshot main", key=f"snap_{sn}"):
            try:
                vid = api.snapshot_main_variant(sn)
                st.success(vid or "Nothing to snapshot")
                st.rerun()
            except Exception as e:
                st.error(str(e))
        gen_pick = st.selectbox(
            "Model for new variant", options=model_labels, key=f"gen_model_{sn}"
        )
        only_exist = st.checkbox(
            "Only clips already on disk", value=True, key=f"var_exist_{sn}"
        )
        if st.button("Generate variant for comparison", key=f"gen_var_{sn}"):
            m = label_to_model[gen_pick]
            with st.spinner("Generating variant…"):
                try:
                    meta = api.generate_scene_variant(
                        sn,
                        provider=str(m.get("provider")),
                        model_name=str(m.get("model_name")),
                        only_existing=only_exist,
                        run_qa=False,
                        label=m.get("label"),
                    )
                    st.success(f"Variant: {meta.get('label')}")
                    st.rerun()
                except Exception as e:
                    st.error(str(e))
        vinfo = api.list_scene_variants(sn)
        variants = vinfo.get("variants") or {}
        playable = {
            vid: meta
            for vid, meta in variants.items()
            if meta.get("composite_path")
        }
        if len(playable) >= 1:
            ids = list(playable.keys())
            left_id = st.selectbox(
                "Compare A",
                options=ids,
                format_func=lambda i: playable[i].get("label") or i,
                key=f"cmp_a_{sn}",
            )
            st.video(playable[left_id]["composite_path"])
            right_id = st.selectbox(
                "Compare B",
                options=ids,
                index=min(1, len(ids) - 1),
                format_func=lambda i: playable[i].get("label") or i,
                key=f"cmp_b_{sn}",
            )
            st.video(playable[right_id]["composite_path"])
        else:
            st.caption("No variant composites yet.")

    st.stop()

# =====================================================================
# SINGLE CLIP — no scene-level clutter
# =====================================================================
if st.button("← Back to scene"):
    st.session_state.clip_num = 0
    st.rerun()

st.header(f"Scene {sn} · Clip {cn}")
st.caption(scene.get("setting") or "")

row = api.get_clip(sn, int(cn))
if not row:
    st.error("Clip not found")
    st.stop()

st.subheader(f"Clip {cn} · {row.get('timestamp')}")
m1, m2, m3, m4 = st.columns(4)
m1.metric("On disk", "yes" if row["on_disk"] else "no")
m2.metric("Review", row.get("review_status") or "pending")
m3.metric("QA", str(row.get("qa_approved")))
m4.metric("Continuation", row.get("continuation") or "none")

if row.get("stale"):
    st.error(
        "**Out of date** — a character reference used in this clip was redesigned after this render. "
        f"Characters: {', '.join(row.get('stale_characters') or []) or '—'}. "
        f"Reasons: {'; '.join(row.get('stale_reasons') or []) or '—'}. "
        "Regenerate to clear; CLI/pipeline will not treat this file as reusable “done.”"
    )

if not row["on_disk"]:
    st.warning("No video yet for this clip.")
    if st.button(
        f"▶ Generate clip {cn}",
        type="primary",
        key=f"gen_one_{sn}_{cn}",
        help="Calls Grok Imagine video (needs XAI_API_KEY). Can take several minutes.",
    ):
        with st.spinner(f"Generating S{sn:02d} C{cn}…"):
            try:
                path = api.regen_clip(sn, int(cn), run_qa=True, rebuild_wip=False)
                st.success(f"Done: {path}")
                st.rerun()
            except Exception as e:
                st.error(str(e))

left, right = st.columns([1, 1])
with left:
    if row["on_disk"]:
        st.video(row["path"])
        st.caption(row["path"])
    else:
        st.caption("Video will appear here after generate.")

with right:
    st.markdown("**Dialogue**")
    st.write(row.get("dialogue") or "_none_")
    st.caption(f"speaker=`{row.get('speaker')}` · delivery=`{row.get('delivery')}`")

st.markdown("**Visual prompt**")
vp = st.text_area(
    "visual_prompt",
    value=row.get("visual_prompt") or "",
    height=140,
    key=f"vp_{sn}_{cn}",
)
neg = st.text_area(
    "negative_prompt",
    value=row.get("negative_prompt") or "",
    height=80,
    key=f"neg_{sn}_{cn}",
)
if st.button("Save prompts to blueprint"):
    try:
        old, new = api.update_clip_prompts(sn, int(cn), visual_prompt=vp, negative_prompt=neg)
        from review_app import edit_log

        edit_log.add_entry(
            "prompt_edit",
            user_note="Manual prompt edit from Scenes UI",
            scene=sn,
            clip=int(cn),
            action_taken="Updated visual/negative prompts",
            before=old,
            after=new,
            targets=["nickandme.clips.grok.json", "prompts/adaptation_v16.txt", "renderer"],
        )
        st.success("Saved blueprint.")
    except Exception as e:
        st.error(str(e))

st.divider()
st.subheader("Review actions")
from review_app import learning as learning_mod

feedback = st.text_area(
    "What's wrong? (optional — appended to prompt on regen)",
    placeholder="e.g. Nick faces Mrs. Engel window, camera behind him, not facing camera",
    key=f"fb_{sn}_{cn}",
)
# Heuristic suggestion only (do not force selectbox — typing would reset user choice)
_suggested = learning_mod.suggest_layer_from_note(feedback)
_layer_opts = list(learning_mod.FEEDBACK_LAYERS)
st.caption(
    f"Suggested layer from note: **`{_suggested}`** "
    f"({learning_mod.LAYER_LABELS.get(_suggested, '')})"
)
learning_layer = st.selectbox(
    "Feedback route (learning layer)",
    options=_layer_opts,
    format_func=lambda k: learning_mod.LAYER_LABELS.get(k, k),
    key=f"layer_{sn}_{cn}",
    help=(
        "clip = this take only · stage2 = replan shots · stage1 = re-bible then stage2 · "
        "verifier = detection rubric · engine = renderer code notes"
    ),
)
if st.button("Use suggested layer", key=f"use_sug_{sn}_{cn}"):
    st.session_state[f"layer_{sn}_{cn}"] = _suggested
    st.rerun()
if learning_layer in ("stage1", "stage2"):
    st.caption(
        f"Selecting **{learning_layer}** will mark this scene dirty for cascade replan "
        f"({'+'.join(learning_mod.dirty_keys_for_layer(learning_layer))})."
    )
apply_fb = st.checkbox("Append feedback to visual_prompt when regenerating", value=True)
run_qa = st.checkbox("Run QA after regen", value=True)
mark_dirty = st.checkbox(
    "Mark scene dirty when layer needs replan",
    value=True,
    help="Only stage1/stage2 layers create dirty flags",
)

b1, b2, b3, b4 = st.columns(4)
with b1:
    if st.button("✅ Pass", width="stretch"):
        api.pass_clip(sn, int(cn), feedback, learning_layer="clip")
        st.success("Passed")
        st.rerun()
with b2:
    if st.button("❌ Fail", width="stretch"):
        result = api.fail_clip(
            sn,
            int(cn),
            feedback,
            learning_layer=learning_layer,
            mark_dirty=mark_dirty,
        )
        msg = f"Failed · layer={result.get('learning_layer')}"
        if result.get("dirty_row"):
            msg += " · scene marked dirty"
        st.warning(msg)
        st.rerun()
with b3:
    if st.button("♻️ Regen", type="primary", width="stretch"):
        with st.spinner("Generating clip — this can take several minutes…"):
            try:
                path = api.regen_clip(
                    sn,
                    int(cn),
                    feedback=feedback,
                    apply_to_prompt=apply_fb and bool(feedback.strip()),
                    run_qa=run_qa,
                    learning_layer=learning_layer,
                    mark_dirty=mark_dirty,
                )
                st.success(f"Done: {path}")
                st.rerun()
            except Exception as e:
                st.error(str(e))
with b4:
    if st.button("Log note only", width="stretch"):
        result = api.log_clip_feedback(
            sn,
            int(cn),
            feedback or "Note",
            learning_layer=learning_layer,
            mark_dirty=mark_dirty,
            before=row.get("visual_prompt") or "",
        )
        st.success(
            f"Logged `{result['entry']['id']}` · layer={result.get('learning_layer')}"
            + (" · dirty" if result.get("dirty_row") else "")
        )
        st.rerun()

# Neighbor navigation
nav_l, nav_r = st.columns(2)
nums = [c["clip_number"] for c in clips]
idx = nums.index(int(cn)) if int(cn) in nums else 0
with nav_l:
    if idx > 0 and st.button(f"← Clip {nums[idx - 1]}"):
        st.session_state.clip_num = nums[idx - 1]
        st.rerun()
with nav_r:
    if idx < len(nums) - 1 and st.button(f"Clip {nums[idx + 1]} →"):
        st.session_state.clip_num = nums[idx + 1]
        st.rerun()
