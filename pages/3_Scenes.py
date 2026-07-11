"""Scene list + clip drill-down review page."""
from __future__ import annotations

import streamlit as st

from review_app import pipeline_api as api

st.set_page_config(page_title="Scenes", page_icon="🎞️", layout="wide")
st.title("🎞️ Scenes & Clips")

try:
    # Light list for navigation (no per-scene cost math × 90)
    scenes = api.list_scenes(light=True)
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
            from generation_script import clip_output_path, file_is_usable

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
        if stale_n:
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
        hero_txt = f" · hero {s.get('hero_resolution')}" if s.get("is_hero") else ""
        label = (
            f"{status} Scene {sn:02d} — {s.get('setting', '')[:56]} "
            f"({s['clips_on_disk']}/{s['clip_count']} clips{stale_txt}{hero_txt})"
        )
        # open | play | badges (thumbs/costs deferred for speed)
        cols = st.columns([3.6, 0.5, 0.9])
        with cols[0]:
            if st.button(label, key=f"open_s{sn}", use_container_width=True):
                st.session_state.scene_num = sn
                st.session_state.clip_num = 0  # 0 = scene overview
                st.session_state.playing_scene = None
                st.rerun()
        with cols[1]:
            if s.get("play_path") or s.get("composite_path"):
                if st.button("▶", key=f"play_s{sn}", help=f"Play scene {sn}", use_container_width=True):
                    st.session_state.playing_scene = sn
                    st.rerun()
            else:
                st.caption("")
        with cols[2]:
            help_bits = []
            if s.get("composite_path") or s.get("composite_exists"):
                help_bits.append("mux")
            if s.get("approved"):
                help_bits.append("ok")
            st.caption(" · ".join(help_bits) if help_bits else "")

    st.stop()

# ---- Scene overview or clip detail ----
sn = int(st.session_state.scene_num)
cn = st.session_state.clip_num

if st.button("← Back to scene list"):
    st.session_state.clip_num = None
    st.rerun()

scene = api.get_scene(sn)
if not scene:
    st.error(f"Scene {sn} not found")
    st.stop()

st.header(f"Scene {sn}: {scene.get('setting', '')}")
st.caption(
    f"`{scene.get('scene_filename', '')}` · "
    f"{scene.get('total_estimated_duration_seconds')}s · "
    f"transition={scene.get('transition_type')}"
)

clips = api.list_clips(sn)

# Cost breakdown for this scene only (not full-film list_scenes)
scene_meta = next((x for x in scenes if x["scene_number"] == sn), {})
with st.expander("Estimated regen cost", expanded=False):
    c_all = api.scene_cost_estimate(sn, mode="all") or {}
    c_ex = api.scene_cost_estimate(sn, mode="existing") or {}
    c_st = api.scene_cost_estimate(sn, mode="stale") or {}
    k1, k2, k3 = st.columns(3)
    k1.metric("All clips", f"${float(c_all.get('total_usd') or 0):.2f}", f"{c_all.get('clip_count', 0)} clips")
    k2.metric("On disk only", f"${float(c_ex.get('total_usd') or 0):.2f}", f"{c_ex.get('clip_count', 0)} clips")
    k3.metric("Stale only", f"${float(c_st.get('total_usd') or 0):.2f}", f"{c_st.get('clip_count', 0)} clips")
    st.caption(
        f"Model `{c_all.get('model_name')}` @ {c_all.get('resolution')} · "
        f"~${float(c_all.get('output_rate_per_sec') or 0):.2f}/sec video out · "
        f"{c_all.get('total_duration_sec')}s total (all). "
        f"{c_all.get('notes') or ''}"
    )
    if c_all.get("per_clip"):
        st.dataframe(
            [
                {
                    "clip": p["clip_number"],
                    "timestamp": p.get("timestamp"),
                    "sec": p.get("duration_sec"),
                    "est_usd": p.get("total_usd"),
                }
                for p in c_all["per_clip"]
            ],
            hide_index=True,
            use_container_width=True,
        )

# Scene-level actions
a1, a2, a3 = st.columns(3)
with a1:
    if st.button("Approve scene (draft)", type="primary", help="Editorial OK — keeps current resolution (e.g. 480p)"):
        try:
            api.approve_scene(sn)
            st.success("Scene approved (draft); WIP updated if enabled.")
        except Exception as e:
            st.error(str(e))
with a2:
    if st.button("Remux scene from disk"):
        with st.spinner("FFmpeg remux…"):
            try:
                path = api.remux_scene(sn)
                st.success(path or "No clips to remux")
            except Exception as e:
                st.error(str(e))
with a3:
    if st.button("Remux scene + rebuild WIP"):
        with st.spinner("Remux + movie_wip.mp4…"):
            try:
                path = api.remux_scenes_and_rebuild_wip(
                    [sn], reason=f"scene {sn} remux + WIP"
                )
                st.success(path or "Remux done")
            except Exception as e:
                st.error(str(e))
    if scene_meta.get("composite_path"):
        st.caption(scene_meta["composite_path"])

# ---- Hero / delivery pass ----
st.divider()
st.subheader("Hero / delivery pass")
hero_info = scene_meta.get("hero") or api.get_engine().get_scene_hero(sn)
draft_res = str(api.get_config().get("resolution", "720p"))
if hero_info:
    st.success(
        f"⭐ **Hero locked** @ **{hero_info.get('resolution')}** "
        f"({hero_info.get('clip_count')} clips) · {hero_info.get('at')}"
    )
else:
    st.caption(
        f"Draft config resolution is **{draft_res}**. "
        "Hero regen re-renders on-disk clips at delivery resolution, then restores draft config."
    )

h1, h2, h3, h4 = st.columns([1.2, 1.2, 1.2, 1.5])
with h1:
    hero_res = st.selectbox(
        "Hero resolution",
        options=["720p", "1080p", "480p"],
        index=0,
        key=f"hero_res_{sn}",
    )
with h2:
    hero_only_disk = st.checkbox(
        "Only clips on disk",
        value=True,
        key=f"hero_disk_{sn}",
        help="Recommended — same as draft clips you already reviewed",
    )
with h3:
    hero_qa = st.checkbox("Run QA", value=True, key=f"hero_qa_{sn}")
with h4:
    hero_approve = st.checkbox(
        "Approve after success",
        value=True,
        key=f"hero_appr_{sn}",
        help="Marks scene approved and rebuilds WIP if all hero clips succeed",
    )

try:
    hero_est = api.hero_cost_note(sn, resolution=hero_res)
    est_usd = float(hero_est.get("total_usd") or 0)
    est_n = int(hero_est.get("clip_count") or 0)
    est_sec = hero_est.get("total_duration_sec")
    st.info(
        f"Estimated hero cost @ **{hero_res}**: **${est_usd:.2f}** "
        f"({est_n} clips · ~{est_sec}s video). Snapshot of current main is saved first."
    )
except Exception:
    st.caption("Could not compute hero cost estimate.")

hb1, hb2 = st.columns(2)
with hb1:
    if st.button(
        f"⭐ Hero regen at {hero_res}",
        type="primary",
        key=f"hero_go_{sn}",
        help="Snapshot draft → regen at hero res → remux → optional approve. Global draft res unchanged.",
    ):
        with st.spinner(
            f"Hero regen Scene {sn} @ {hero_res} — this can take a long time and costs API usage…"
        ):
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
                    st.warning(
                        f"Hero partial: {meta.get('clip_count')} ok, "
                        f"{len(failed)} failed: {failed}"
                    )
                else:
                    st.success(
                        f"Hero complete @ {meta.get('resolution')}: "
                        f"clips {meta.get('clip_numbers')} · "
                        f"draft res restored to {meta.get('draft_resolution_restored')}"
                    )
                st.rerun()
            except Exception as e:
                st.error(str(e))
with hb2:
    if hero_info and st.button(
        "Clear hero flag (back to draft)",
        key=f"hero_clear_{sn}",
        help="Does not delete 720 files — only removes the ⭐ hero marker so you can re-edit freely",
    ):
        try:
            api.clear_scene_hero(sn)
            st.success("Hero flag cleared — scene is draft again.")
            st.rerun()
        except Exception as e:
            st.error(str(e))

st.caption(
    "Workflow: iterate at draft resolution (e.g. 480) → **Approve scene (draft)** → "
    "when happy, **Hero regen at 720p**. Single-clip regen after hero clears the hero flag."
)

# ---- Model comparison / variants ----
st.divider()
st.subheader("Model comparison")
pref = api.scene_video_settings(sn)
models = api.available_video_models()
model_labels = [
    f"{m.get('label') or m.get('model_name')} ({m.get('provider')}/{m.get('model_name')})"
    for m in models
]
label_to_model = dict(zip(model_labels, models))

st.caption(
    f"Preferred for this scene: **{pref.get('provider')}** / `{pref.get('model_name')}` "
    f"(override stored on scene in `nickandme.json`; blank = global config)."
)

pc1, pc2, pc3 = st.columns([2, 2, 1])
with pc1:
    pick = st.selectbox(
        "Set preferred model",
        options=["(use global default)"] + model_labels,
        key=f"pref_model_{sn}",
    )
with pc2:
    if st.button("Save preferred model", key=f"save_pref_{sn}"):
        try:
            if pick.startswith("(use"):
                api.set_scene_video_settings(sn, clear=True)
                st.success("Cleared scene override — using global config.")
            else:
                m = label_to_model[pick]
                api.set_scene_video_settings(
                    sn, provider=m.get("provider"), model_name=m.get("model_name")
                )
                st.success(f"Saved {m.get('provider')}/{m.get('model_name')}")
            st.rerun()
        except Exception as e:
            st.error(str(e))
with pc3:
    if st.button("Snapshot main", key=f"snap_{sn}", help="Copy current main into variants/ for comparison"):
        try:
            vid = api.snapshot_main_variant(sn)
            st.success(vid or "Nothing to snapshot")
            st.rerun()
        except Exception as e:
            st.error(str(e))

st.markdown("**Generate alternate render** (writes to `assets/variants/…`, keeps main intact)")
g1, g2, g3 = st.columns([2, 1, 1])
with g1:
    gen_pick = st.selectbox(
        "Model for new variant",
        options=model_labels,
        key=f"gen_model_{sn}",
    )
with g2:
    only_exist = st.checkbox("Only clips already on disk", value=True, key=f"var_exist_{sn}")
with g3:
    run_qa_var = st.checkbox("Run QA", value=False, key=f"var_qa_{sn}")

if st.button("Generate variant for comparison", type="primary", key=f"gen_var_{sn}"):
    m = label_to_model[gen_pick]
    with st.spinner(
        f"Generating scene {sn} with {m.get('provider')}/{m.get('model_name')} — can take a long time…"
    ):
        try:
            meta = api.generate_scene_variant(
                sn,
                provider=str(m.get("provider")),
                model_name=str(m.get("model_name")),
                only_existing=only_exist,
                run_qa=run_qa_var,
                label=m.get("label"),
            )
            st.success(
                f"Variant ready: {meta.get('label')} · {meta.get('clip_count')} clips · "
                f"{meta.get('composite_path') or 'no composite'}"
            )
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
st.markdown(f"**Available variants:** {len(variants)} · playable composites: {len(playable)}")

if len(playable) >= 1:
    ids = list(playable.keys())
    default_a = "main" if "main" in ids else ids[0]
    default_b = next((i for i in ids if i != default_a), default_a)
    cmp1, cmp2 = st.columns(2)
    with cmp1:
        left_id = st.selectbox(
            "A",
            options=ids,
            index=ids.index(default_a) if default_a in ids else 0,
            format_func=lambda i: playable[i].get("label") or i,
            key=f"cmp_a_{sn}",
        )
        left = playable[left_id]
        st.caption(f"{left.get('provider')}/{left.get('model_name')} · `{left_id}`")
        st.video(left["composite_path"])
        if left_id != "main" and st.button("Promote A → main", key=f"promo_a_{sn}"):
            try:
                api.promote_scene_variant(sn, left_id)
                st.success("Promoted A to main timeline")
                st.rerun()
            except Exception as e:
                st.error(str(e))
    with cmp2:
        right_id = st.selectbox(
            "B",
            options=ids,
            index=ids.index(default_b) if default_b in ids else 0,
            format_func=lambda i: playable[i].get("label") or i,
            key=f"cmp_b_{sn}",
        )
        right = playable[right_id]
        st.caption(f"{right.get('provider')}/{right.get('model_name')} · `{right_id}`")
        st.video(right["composite_path"])
        if right_id != "main" and st.button("Promote B → main", key=f"promo_b_{sn}"):
            try:
                api.promote_scene_variant(sn, right_id)
                st.success("Promoted B to main timeline")
                st.rerun()
            except Exception as e:
                st.error(str(e))
else:
    st.info(
        "No composites to compare yet. Generate the scene (main), click **Snapshot main**, "
        "then **Generate variant for comparison** with another model."
    )

if scene_meta.get("composite_path"):
    with st.expander("Play scene composite", expanded=False):
        st.video(scene_meta["composite_path"])

st.divider()

# Clip grid when cn == 0
if cn == 0:
    st.subheader("Clips")
    for row in clips:
        cnum = row["clip_number"]
        disk = "🟢" if row["on_disk"] else "⚪"
        rev = row.get("review_status") or "pending"
        if row.get("stale"):
            rev_icon = "⚠️"
        else:
            rev_icon = {"pass": "✅", "fail": "❌", "pending": "·", "stale": "⚠️"}.get(rev, "·")
        preview = (row.get("visual_prompt") or "")[:70].replace("\n", " ")
        stale_bit = ""
        if row.get("stale"):
            chars = ",".join(row.get("stale_characters") or []) or "character"
            stale_bit = f" OUT OF DATE ({chars})"
        label = f"{disk}{rev_icon} Clip {cnum}{stale_bit}  [{row.get('delivery') or '—'}]  {preview}"
        if st.button(label, key=f"open_c{cnum}", use_container_width=True):
            st.session_state.clip_num = cnum
            st.rerun()
    st.stop()

# ---- Single clip ----
if st.button("← Back to scene clips"):
    st.session_state.clip_num = 0
    st.rerun()

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

left, right = st.columns([1, 1])
with left:
    if row["on_disk"]:
        st.video(row["path"])
        st.caption(row["path"])
    else:
        st.warning("No video file yet.")

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
if st.button("Save prompts to nickandme.json"):
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
            targets=["nickandme.json", "ClaudeAdaptationPromptV16.txt", "generation_script"],
        )
        st.success("Saved blueprint.")
    except Exception as e:
        st.error(str(e))

st.divider()
st.subheader("Review actions")
feedback = st.text_area(
    "What's wrong? (optional — appended to prompt on regen)",
    placeholder="e.g. Nick faces Mrs. Engel window, camera behind him, not facing camera",
    key=f"fb_{sn}_{cn}",
)
apply_fb = st.checkbox("Append feedback to visual_prompt when regenerating", value=True)
run_qa = st.checkbox("Run QA after regen", value=True)

b1, b2, b3, b4 = st.columns(4)
with b1:
    if st.button("✅ Pass", use_container_width=True):
        api.pass_clip(sn, int(cn), feedback)
        st.success("Passed")
        st.rerun()
with b2:
    if st.button("❌ Fail", use_container_width=True):
        api.fail_clip(sn, int(cn), feedback)
        st.warning("Failed")
        st.rerun()
with b3:
    if st.button("♻️ Regen", type="primary", use_container_width=True):
        with st.spinner("Generating clip — this can take several minutes…"):
            try:
                path = api.regen_clip(
                    sn,
                    int(cn),
                    feedback=feedback,
                    apply_to_prompt=apply_fb and bool(feedback.strip()),
                    run_qa=run_qa,
                )
                st.success(f"Done: {path}")
                st.rerun()
            except Exception as e:
                st.error(str(e))
with b4:
    if st.button("Log note only", use_container_width=True):
        from review_app import edit_log

        edit_log.add_entry(
            "clip_note",
            user_note=feedback or "Note",
            scene=sn,
            clip=int(cn),
            action_taken="Logged without regen",
            before=row.get("visual_prompt") or "",
            after=row.get("visual_prompt") or "",
        )
        st.success("Logged to edit_feedback_log.json")

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
