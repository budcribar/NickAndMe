"""Pipeline configuration page."""
from __future__ import annotations

import streamlit as st

import review_app  # noqa: F401 — path bootstrap for renderer
from review_app import pipeline_api as api
from renderer import DEFAULT_CONFIG

st.set_page_config(page_title="Configuration", page_icon="⚙️", layout="wide")
st.title("⚙️ Configuration")
st.caption("Edits write to `pipeline_config.json`. Defaults fill any missing keys.")

try:
    cfg = api.get_config()
except Exception as e:
    st.error(str(e))
    st.stop()


def _idx(options, value, default=0):
    try:
        return options.index(value)
    except ValueError:
        return default


st.subheader("Blueprint")
blueprint_file = st.text_input(
    "blueprint_file",
    value=str(cfg.get("blueprint_file", "nickandme.clips.grok.json")),
    help="Stage 2 clip plan JSON used by Streamlit and the renderer (e.g. nickandme.clips.grok.json)",
)
st.caption(
    "Stage 1 story bible: `nickandme.scenes.json` (not for generate). "
    "Stage 2 generate plan: `nickandme.clips.grok.json` (default)."
)

st.subheader("Providers & models")
c1, c2 = st.columns(2)
with c1:
    video_provider = st.selectbox(
        "video_provider",
        ["grok", "veo"],
        index=_idx(["grok", "veo"], str(cfg.get("video_provider", "grok")).lower()),
    )
    _cdp = str(cfg.get("character_design_provider", "grok")).lower()
    character_design_provider = st.selectbox(
        "character_design_provider",
        ["grok", "gemini"],
        index=1 if _cdp in ("gemini", "imagen", "google") else 0,
    )
    _qap = str(cfg.get("qa_provider", "grok")).lower()
    qa_provider = st.selectbox(
        "qa_provider",
        ["grok", "gemini"],
        index=1 if _qap in ("gemini", "google") else 0,
    )
with c2:
    model_name = st.text_input("model_name (video)", cfg.get("model_name", ""))
    image_model_name = st.text_input("image_model_name", cfg.get("image_model_name", ""))
    qa_model_name = st.text_input("qa_model_name", cfg.get("qa_model_name", ""))
    aspect_ratio = st.selectbox(
        "aspect_ratio",
        ["16:9", "9:16", "1:1"],
        index=_idx(["16:9", "9:16", "1:1"], cfg.get("aspect_ratio", "16:9")),
    )

st.subheader("Pipeline behaviour")
c3, c4, c5 = st.columns(3)
with c3:
    regenerate_silent_clips = st.checkbox(
        "regenerate_silent_clips", bool(cfg.get("regenerate_silent_clips", True))
    )
    merge_scene_after_each_clip = st.checkbox(
        "merge_scene_after_each_clip", bool(cfg.get("merge_scene_after_each_clip", True))
    )
    smart_continuation = st.checkbox(
        "smart_continuation", bool(cfg.get("smart_continuation", True))
    )
with c4:
    qa_retry_on_fail = st.checkbox("qa_retry_on_fail", bool(cfg.get("qa_retry_on_fail", True)))
    qa_max_retries = st.number_input(
        "qa_max_retries", min_value=0, max_value=5, value=int(cfg.get("qa_max_retries", 2))
    )
    qa_frame_count = st.number_input(
        "qa_frame_count", min_value=1, max_value=8, value=int(cfg.get("qa_frame_count", 4))
    )
with c5:
    ensure_dialogue_audio = st.checkbox(
        "ensure_dialogue_audio (TTS)", bool(cfg.get("ensure_dialogue_audio", False))
    )
    dialogue_audio_mode = st.selectbox(
        "dialogue_audio_mode",
        ["replace", "mix"],
        index=_idx(["replace", "mix"], cfg.get("dialogue_audio_mode", "replace")),
    )
    use_video_audio_for_music = st.checkbox(
        "use_video_audio_for_music", bool(cfg.get("use_video_audio_for_music", True))
    )

st.subheader("Audio & WIP")
c6, c7 = st.columns(2)
with c6:
    dialogue_tts_volume = st.slider(
        "dialogue_tts_volume", 0.0, 2.0, float(cfg.get("dialogue_tts_volume", 1.0)), 0.05
    )
    native_audio_mix_volume = st.slider(
        "native_audio_mix_volume", 0.0, 1.0, float(cfg.get("native_audio_mix_volume", 0.12)), 0.01
    )
    composite_audio_gain_db = st.number_input(
        "composite_audio_gain_db",
        min_value=-12.0,
        max_value=24.0,
        value=float(cfg.get("composite_audio_gain_db", 6.0)),
        step=0.5,
    )
with c7:
    rebuild_wip_movie_after_scene = st.checkbox(
        "rebuild_wip_movie_after_scene", bool(cfg.get("rebuild_wip_movie_after_scene", True))
    )
    wip_movie_path = st.text_input(
        "wip_movie_path", cfg.get("wip_movie_path", "assets/movie_wip.mp4")
    )
    duration_seconds = st.number_input(
        "duration_seconds (default clip target)",
        min_value=1,
        max_value=15,
        value=int(cfg.get("duration_seconds", 8)),
    )
    resolution = st.selectbox(
        "resolution",
        ["720p", "480p"],
        index=_idx(["720p", "480p"], cfg.get("resolution", "720p")),
    )

st.subheader("Cost estimates (USD planning)")
st.caption(
    "Used by the Scenes page for regen cost. Not an invoice — match xAI list prices as needed."
)
_ce = cfg.get("cost_estimates") or {}
_vop = _ce.get("video_output_per_sec") or {}
cc1, cc2, cc3 = st.columns(3)
with cc1:
    rate_480 = st.number_input(
        "$/sec output 480p",
        min_value=0.0,
        max_value=5.0,
        value=float(_vop.get("480p", 0.05)),
        step=0.01,
        format="%.3f",
    )
    rate_720 = st.number_input(
        "$/sec output 720p",
        min_value=0.0,
        max_value=5.0,
        value=float(_vop.get("720p", 0.07)),
        step=0.01,
        format="%.3f",
    )
    rate_1080 = st.number_input(
        "$/sec output 1080p",
        min_value=0.0,
        max_value=5.0,
        value=float(_vop.get("1080p", 0.25)),
        step=0.01,
        format="%.3f",
    )
with cc2:
    video_input_image = st.number_input(
        "$ per ref image input",
        min_value=0.0,
        max_value=1.0,
        value=float(_ce.get("video_input_image", 0.002)),
        step=0.001,
        format="%.3f",
    )
    video_input_per_sec = st.number_input(
        "$/sec video input (extend)",
        min_value=0.0,
        max_value=1.0,
        value=float(_ce.get("video_input_per_sec", 0.01)),
        step=0.001,
        format="%.3f",
    )
with cc3:
    assume_ref_image_per_clip = st.checkbox(
        "assume_ref_image_per_clip",
        bool(_ce.get("assume_ref_image_per_clip", True)),
    )
    assume_avg_retries = st.number_input(
        "assume_avg_retries (extra attempts)",
        min_value=0.0,
        max_value=5.0,
        value=float(_ce.get("assume_avg_retries", 0.0)),
        step=0.25,
    )

st.divider()
if st.button("💾 Save configuration", type="primary"):
    updates = dict(cfg)
    updates.update(
        {
            "blueprint_file": blueprint_file.strip() or "nickandme.clips.grok.json",
            "video_provider": video_provider,
            "character_design_provider": character_design_provider,
            "qa_provider": qa_provider,
            "model_name": model_name,
            "image_model_name": image_model_name,
            "qa_model_name": qa_model_name,
            "aspect_ratio": aspect_ratio,
            "regenerate_silent_clips": regenerate_silent_clips,
            "merge_scene_after_each_clip": merge_scene_after_each_clip,
            "smart_continuation": smart_continuation,
            "qa_retry_on_fail": qa_retry_on_fail,
            "qa_max_retries": int(qa_max_retries),
            "qa_frame_count": int(qa_frame_count),
            "ensure_dialogue_audio": ensure_dialogue_audio,
            "dialogue_audio_mode": dialogue_audio_mode,
            "use_video_audio_for_music": use_video_audio_for_music,
            "dialogue_tts_volume": float(dialogue_tts_volume),
            "native_audio_mix_volume": float(native_audio_mix_volume),
            "composite_audio_gain_db": float(composite_audio_gain_db),
            "rebuild_wip_movie_after_scene": rebuild_wip_movie_after_scene,
            "wip_movie_path": wip_movie_path,
            "duration_seconds": int(duration_seconds),
            "resolution": resolution,
            "cost_estimates": {
                "currency": "USD",
                "video_output_per_sec": {
                    "480p": float(rate_480),
                    "720p": float(rate_720),
                    "1080p": float(rate_1080),
                },
                "video_input_image": float(video_input_image),
                "video_input_per_sec": float(video_input_per_sec),
                "assume_ref_image_per_clip": bool(assume_ref_image_per_clip),
                "assume_avg_retries": float(assume_avg_retries),
                "notes": (_ce.get("notes") or "Estimates only — update if xAI pricing changes."),
            },
        }
    )
    try:
        api.save_config(updates)
        st.success("Saved `pipeline_config.json`.")
    except Exception as e:
        st.error(str(e))

with st.expander("Raw config JSON"):
    st.json(cfg)

with st.expander("Engine defaults (reference)"):
    st.json(DEFAULT_CONFIG)

st.info(
    "Environment: set `XAI_API_KEY` for Grok video/image/QA. "
    "Optional `GEMINI_API_KEY` when using Gemini providers. "
    "`FFMPEG_PATH` if ffmpeg is not on PATH."
)
