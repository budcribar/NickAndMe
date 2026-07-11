"""
Thin façade over AgenticGenerationEngine for the Streamlit review app.
Keeps a single engine instance in session and reloads when files change.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

os.chdir(ROOT)

from generation_script import (  # noqa: E402
    AgenticGenerationEngine,
    GenerationFailure,
    clip_output_path,
    composite_output_path,
    file_is_usable,
    music_output_path,
)
from review_app import edit_log  # noqa: E402
from review_app.cost_estimate import (  # noqa: E402
    estimate_scene_cost,
    film_budget_report,
    format_usd,
    scenario_compare,
)

_engine: Optional[AgenticGenerationEngine] = None


def get_engine(force_reload: bool = False) -> AgenticGenerationEngine:
    global _engine
    if _engine is None or force_reload:
        _engine = AgenticGenerationEngine(install_signals=False)
    return _engine


def reload_engine() -> AgenticGenerationEngine:
    eng = get_engine()
    eng.reload_all()
    return eng


# ---------- Config ----------

def get_config() -> Dict[str, Any]:
    return dict(get_engine().config)


def save_config(updates: Dict[str, Any]) -> Dict[str, Any]:
    eng = get_engine()
    eng.config.update(updates)
    eng.save_config_to_disk()
    edit_log.add_entry(
        "config_change",
        user_note=f"Updated {len(updates)} config key(s)",
        action_taken="Saved pipeline_config.json",
        extra={"keys": list(updates.keys())},
        targets=["pipeline_config.json"],
    )
    return dict(eng.config)


# ---------- Blueprint / scenes ----------

def list_scenes() -> List[Dict[str, Any]]:
    eng = get_engine()
    out = []
    completed = eng.state.get("scenes_completed") or {}
    stale_by_scene: Dict[int, int] = {}
    for row in eng.list_stale_clips(only_existing=True):
        stale_by_scene[row["scene"]] = stale_by_scene.get(row["scene"], 0) + 1
    for s in eng.blueprint.get("scenes", []):
        sn = s.get("scene_number")
        clips = s.get("veo_clips") or []
        on_disk = sum(
            1
            for c in clips
            if file_is_usable(clip_output_path(sn, c.get("clip_number", 0)), min_bytes=1024)
        )
        composite = composite_output_path(sn)
        composite_ok = file_is_usable(composite, min_bytes=1024)
        # Prefer scene composite for playback; else first on-disk clip
        play_path = composite if composite_ok else None
        thumb_path = None
        for c in clips:
            cn = int(c.get("clip_number", 0))
            cpath = clip_output_path(sn, cn)
            if not play_path and file_is_usable(cpath, min_bytes=1024):
                play_path = cpath
            # Thumbnail candidates: seed frame, then any QA frame
            seed = f"assets/video/scene_{sn:02d}_clip_{cn:02d}_seed_frame.png"
            if not thumb_path and file_is_usable(seed, min_bytes=64):
                thumb_path = seed
        if not thumb_path:
            # Fall back to any small jpg frame dumped for QA
            import glob as _glob

            for pattern in (
                f"assets/video/scene_{sn:02d}_clip_*_qa_frame_01.jpg",
                f"assets/video/s{sn}c*_frame*.jpg",
            ):
                matches = sorted(_glob.glob(pattern))
                if matches:
                    thumb_path = matches[0]
                    break
        # Cost estimates: full scene vs only clips already on disk vs only stale
        on_disk_map = {
            int(c.get("clip_number", 0)): file_is_usable(
                clip_output_path(sn, int(c.get("clip_number", 0))), min_bytes=1024
            )
            for c in clips
        }
        stale_nums = [
            r["clip"]
            for r in eng.list_stale_clips(only_existing=True)
            if r["scene"] == sn
        ]
        cost_all = estimate_scene_cost(s, eng.config)
        cost_existing = estimate_scene_cost(
            s, eng.config, only_existing_paths=on_disk_map
        )
        cost_stale = (
            estimate_scene_cost(
                s,
                eng.config,
                only_stale=True,
                stale_clip_numbers=stale_nums,
                only_existing_paths=on_disk_map,
            )
            if stale_nums
            else {
                "total_usd": 0.0,
                "clip_count": 0,
                "total_duration_sec": 0,
                "currency": "USD",
            }
        )
        hero = (eng.state.get("scene_hero") or {}).get(str(sn))
        out.append(
            {
                "scene_number": sn,
                "setting": s.get("setting", ""),
                "scene_filename": s.get("scene_filename", ""),
                "clip_count": len(clips),
                "clips_on_disk": on_disk,
                "stale_clips": stale_by_scene.get(sn, 0),
                "approved": bool(completed.get(str(sn))),
                "hero": hero,
                "is_hero": bool(hero),
                "hero_resolution": (hero or {}).get("resolution"),
                "composite_exists": composite_ok,
                "composite_path": composite if composite_ok else None,
                "play_path": play_path,
                "thumb_path": thumb_path,
                "duration": s.get("total_estimated_duration_seconds"),
                "cost_regen_all_usd": cost_all.get("total_usd"),
                "cost_regen_existing_usd": cost_existing.get("total_usd"),
                "cost_regen_stale_usd": cost_stale.get("total_usd"),
                "cost_regen_all": cost_all,
                "cost_regen_existing": cost_existing,
                "cost_regen_stale": cost_stale,
                "cost_label_all": format_usd(float(cost_all.get("total_usd") or 0)),
                "cost_label_existing": format_usd(
                    float(cost_existing.get("total_usd") or 0)
                ),
                "cost_label_stale": format_usd(float(cost_stale.get("total_usd") or 0)),
            }
        )
    return out


def get_scene(scene_num: int) -> Optional[Dict[str, Any]]:
    for s in get_engine().blueprint.get("scenes", []):
        if s.get("scene_number") == scene_num:
            return s
    return None


def list_clips(scene_num: int) -> List[Dict[str, Any]]:
    eng = get_engine()
    scene = get_scene(scene_num)
    if not scene:
        return []
    rows = []
    for c in scene.get("veo_clips") or []:
        cn = int(c.get("clip_number", 0))
        path = clip_output_path(scene_num, cn)
        job = eng.state.get("clip_jobs", {}).get(f"{scene_num}_{cn}", {})
        ap = c.get("audio_payload") or {}
        stale_info = eng.get_stale_clip_info(scene_num, cn)
        is_stale = eng.is_clip_stale(scene_num, cn)
        rows.append(
            {
                "clip_number": cn,
                "timestamp": c.get("timestamp", ""),
                "continuation": c.get("veo_continuation_source", "none"),
                "visual_prompt": c.get("visual_prompt", ""),
                "negative_prompt": c.get("negative_prompt", ""),
                "dialogue": (ap.get("dialogue") or ""),
                "delivery": ap.get("delivery"),
                "speaker": ap.get("speaker"),
                "path": path,
                "on_disk": file_is_usable(path, min_bytes=1024),
                "size_bytes": os.path.getsize(path) if file_is_usable(path, min_bytes=1) else 0,
                "qa_approved": job.get("qa_approved"),
                "review_status": "stale" if is_stale else job.get("review_status", "pending"),
                "review_note": job.get("review_note", ""),
                "job_status": job.get("status"),
                "stale": is_stale,
                "stale_characters": (stale_info or {}).get("characters") or job.get("stale_characters") or [],
                "stale_reasons": (stale_info or {}).get("reasons") or [],
                "stale_marked_at": (stale_info or {}).get("marked_at"),
            }
        )
    return rows


def get_clip(scene_num: int, clip_num: int) -> Optional[Dict[str, Any]]:
    for row in list_clips(scene_num):
        if row["clip_number"] == clip_num:
            return row
    return None


def update_clip_prompts(
    scene_num: int,
    clip_num: int,
    visual_prompt: Optional[str] = None,
    negative_prompt: Optional[str] = None,
) -> Tuple[str, str]:
    eng = get_engine()
    old_vp, old_neg = "", ""
    for scene in eng.blueprint.get("scenes", []):
        if scene.get("scene_number") != scene_num:
            continue
        for clip in scene.get("veo_clips") or []:
            if clip.get("clip_number") != clip_num:
                continue
            old_vp = clip.get("visual_prompt") or ""
            old_neg = clip.get("negative_prompt") or ""
            if visual_prompt is not None:
                clip["visual_prompt"] = visual_prompt
            if negative_prompt is not None:
                clip["negative_prompt"] = negative_prompt
            eng.save_blueprint_to_disk()
            return old_vp, clip.get("visual_prompt") or ""
    raise GenerationFailure(f"S{scene_num}C{clip_num} not found")


def pass_clip(scene_num: int, clip_num: int, note: str = "") -> None:
    eng = get_engine()
    eng.set_clip_review_status(scene_num, clip_num, "pass", note)
    edit_log.add_entry(
        "clip_pass",
        user_note=note or "Passed",
        scene=scene_num,
        clip=clip_num,
        action_taken="review_status=pass",
        targets=["pipeline_state.json"],
    )


def fail_clip(scene_num: int, clip_num: int, note: str = "") -> None:
    eng = get_engine()
    eng.set_clip_review_status(scene_num, clip_num, "fail", note)
    edit_log.add_entry(
        "clip_fail",
        user_note=note or "Failed",
        scene=scene_num,
        clip=clip_num,
        action_taken="review_status=fail",
        targets=["pipeline_state.json"],
    )


def regen_clip(
    scene_num: int,
    clip_num: int,
    feedback: str = "",
    apply_to_prompt: bool = True,
    run_qa: bool = True,
    rebuild_wip: bool = True,
) -> str:
    eng = get_engine()
    old_vp = ""
    for row in list_clips(scene_num):
        if row["clip_number"] == clip_num:
            old_vp = row["visual_prompt"]
            break
    fb = feedback.strip() if apply_to_prompt else ""
    path = eng.regenerate_clip(
        scene_num, clip_num, feedback=fb or None, run_qa=run_qa
    )
    new_vp = ""
    for row in list_clips(scene_num):
        if row["clip_number"] == clip_num:
            new_vp = row["visual_prompt"]
            break
    wip_path = None
    if rebuild_wip:
        wip_path = eng.remux_scenes_and_rebuild_wip(
            [scene_num], reason=f"after regen S{scene_num}C{clip_num}"
        )
    edit_log.add_entry(
        "clip_regen",
        user_note=feedback or "Regenerate without prompt change",
        scene=scene_num,
        clip=clip_num,
        action_taken=f"Wiped and regenerated → {path}; WIP={wip_path or 'skipped'}",
        before=old_vp,
        after=new_vp,
        targets=["nickandme.json", "assets/video", "assets/movie_wip.mp4"],
    )
    return path


def approve_scene(scene_num: int) -> None:
    get_engine().approve_scene(scene_num)
    edit_log.add_entry(
        "scene_approve",
        user_note=f"Approved scene {scene_num}",
        scene=scene_num,
        action_taken="scenes_completed + remux + WIP",
        targets=["pipeline_state.json", "assets"],
    )


def remux_scene(scene_num: int) -> Optional[str]:
    return get_engine().remux_scene_from_disk(scene_num)


def rebuild_wip_movie(
    reason: str = "manual refresh",
    *,
    approved_only: bool = False,
) -> Optional[str]:
    """Rebuild assets/movie_wip.mp4. Default includes all scene composites on disk."""
    return get_engine().rebuild_wip_movie(
        reason=reason, approved_only=approved_only, force=True
    )


def remux_scenes_and_rebuild_wip(
    scene_nums: List[int], reason: str = ""
) -> Optional[str]:
    return get_engine().remux_scenes_and_rebuild_wip(scene_nums, reason=reason)


# ---------- Characters ----------

def list_characters() -> List[Dict[str, Any]]:
    eng = get_engine()
    seeds = eng.blueprint.get("global_production_variables", {}).get("character_seed_tokens", {})
    stale_all = eng.list_stale_clips(only_existing=True)
    rows = []
    for key, info in seeds.items():
        ref = eng.character_ref_path(key)
        variants = [p for p in eng.character_variant_paths(key) if os.path.isfile(p)]
        hits = eng.clips_using_character(key)
        rev_entry = (eng.state.get("character_revisions") or {}).get(key) or {}
        stale_for_char = [
            r for r in stale_all if key in (r.get("characters") or [])
        ]
        rows.append(
            {
                "key": key,
                "description": info.get("description", ""),
                "age_band": info.get("age_band"),
                "variant_of": info.get("variant_of"),
                "ref_path": ref,
                "locked": os.path.isfile(ref),
                "variants": variants,
                "clip_count": len(hits),
                "clips": hits[:50],
                "revision": int(rev_entry.get("revision", 0)),
                "revision_updated_at": rev_entry.get("updated_at"),
                "revision_reason": rev_entry.get("reason"),
                "stale_clip_count": len(stale_for_char),
                "stale_clips": [(r["scene"], r["clip"]) for r in stale_for_char[:40]],
            }
        )
    # adults first, then young/teen
    def sort_key(r):
        k = r["key"]
        if k.endswith("_Young"):
            return (1, k)
        if k.endswith("_Teen"):
            return (2, k)
        return (0, k)

    return sorted(rows, key=sort_key)


def generate_character_variants(char_key: str) -> List[str]:
    eng = get_engine()
    paths = eng.generate_character_variants(char_key)
    edit_log.add_entry(
        "character_variants",
        user_note=f"Generated {len(paths)} variants",
        character=char_key,
        action_taken=", ".join(paths),
        targets=["assets/characters"],
    )
    return paths


def unlock_character(char_key: str) -> bool:
    eng = get_engine()
    removed = eng.unlock_character_ref(char_key)
    edit_log.add_entry(
        "character_unlock",
        user_note="Unlocked reference for redesign",
        character=char_key,
        action_taken="Deleted locked ref + variants",
        targets=["assets/characters"],
    )
    return removed


def lock_character_variant(char_key: str, variant_index: int) -> str:
    eng = get_engine()
    path = eng.lock_character_variant(char_key, variant_index)
    edit_log.add_entry(
        "character_lock",
        user_note=f"Locked variant {variant_index}",
        character=char_key,
        action_taken=f"Promoted to {path}",
        targets=["assets/characters"],
    )
    return path


def clips_using_character_detail(
    char_key: str,
    *,
    only_existing: bool = False,
    only_scene: Optional[int] = None,
) -> List[Dict[str, Any]]:
    """
    Clips whose visual_prompt mentions char_key.

    only_existing=True → only clips that already have a video file on disk
    (never generates brand-new scenes that were never rendered).
    """
    eng = get_engine()
    rows: List[Dict[str, Any]] = []
    for sn, cn in eng.clips_using_character(char_key):
        if only_scene is not None and sn != only_scene:
            continue
        path = clip_output_path(sn, cn)
        on_disk = file_is_usable(path, min_bytes=1024)
        if only_existing and not on_disk:
            continue
        rows.append(
            {
                "scene": sn,
                "clip": cn,
                "label": f"S{sn}C{cn}",
                "path": path,
                "on_disk": on_disk,
            }
        )
    return rows


def cascade_regen_character(
    char_key: str,
    only_scene: Optional[int] = None,
    feedback: str = "",
    dry_run: bool = False,
    only_existing: bool = True,
    selected: Optional[List[Tuple[int, int]]] = None,
    rebuild_wip: bool = True,
) -> List[Tuple[int, int]]:
    """
    Regenerate clips that use this character.

    Defaults:
      only_existing=True  — only wipe/regen clips already on disk (no new first-time renders)
      selected=None       — all matching hits after filters; or pass explicit (scene, clip) list
      rebuild_wip=True    — remux affected scenes and rebuild assets/movie_wip.mp4
    """
    eng = get_engine()
    if selected is not None:
        hits = list(selected)
    else:
        detail = clips_using_character_detail(
            char_key, only_existing=only_existing, only_scene=only_scene
        )
        hits = [(r["scene"], r["clip"]) for r in detail]
    if dry_run:
        return hits
    for sn, cn in hits:
        eng.regenerate_clip(sn, cn, feedback=feedback or None, run_qa=True)

    wip_path = None
    if rebuild_wip and hits:
        scenes = sorted({sn for sn, _ in hits})
        wip_path = eng.remux_scenes_and_rebuild_wip(
            scenes, reason=f"after cascade regen {char_key}"
        )

    edit_log.add_entry(
        "character_cascade_regen",
        user_note=feedback or f"Cascade regen for {char_key}",
        character=char_key,
        action_taken=(
            f"Regenerated {len(hits)} clip(s): {hits[:20]}; "
            f"remux+WIP={wip_path or 'skipped'}"
        ),
        targets=["nickandme.json", "assets/video", "assets/scenes", "assets/movie_wip.mp4"],
        extra={
            "clips": hits,
            "only_existing": only_existing,
            "only_scene": only_scene,
            "wip_path": wip_path,
        },
    )
    return hits


def movie_title() -> str:
    return get_engine().blueprint.get("movie_title", "Nick and Me")


def wip_path() -> Optional[str]:
    p = get_engine().config.get("wip_movie_path", "assets/movie_wip.mp4")
    return p if file_is_usable(p, min_bytes=1024) else None


def list_stale_clips(only_existing: bool = True) -> List[Dict[str, Any]]:
    return get_engine().list_stale_clips(only_existing=only_existing)


def mark_character_changed(char_key: str, reason: str = "") -> List[Tuple[int, int]]:
    """Manually flag a character redesign (if lock path was skipped)."""
    marked = get_engine().mark_character_changed(char_key, reason=reason, only_existing=True)
    edit_log.add_entry(
        "character_changed",
        user_note=reason or "Character marked changed",
        character=char_key,
        action_taken=f"Marked {len(marked)} clip(s) stale: {marked[:20]}",
        targets=["pipeline_state.json"],
        extra={"stale_clips": marked},
    )
    return marked


def scene_cost_estimate(
    scene_num: int,
    *,
    mode: str = "all",
) -> Optional[Dict[str, Any]]:
    """
    mode: all | existing | stale
    """
    eng = get_engine()
    scene = get_scene(scene_num)
    if not scene:
        return None
    clips = scene.get("veo_clips") or []
    on_disk_map = {
        int(c.get("clip_number", 0)): file_is_usable(
            clip_output_path(scene_num, int(c.get("clip_number", 0))), min_bytes=1024
        )
        for c in clips
    }
    if mode == "existing":
        return estimate_scene_cost(scene, eng.config, only_existing_paths=on_disk_map)
    if mode == "stale":
        stale_nums = [
            r["clip"]
            for r in eng.list_stale_clips(only_existing=True)
            if r["scene"] == scene_num
        ]
        return estimate_scene_cost(
            scene,
            eng.config,
            only_stale=True,
            stale_clip_numbers=stale_nums,
            only_existing_paths=on_disk_map,
        )
    return estimate_scene_cost(scene, eng.config)


def available_video_models() -> List[Dict[str, Any]]:
    return get_engine().available_video_models()


def scene_video_settings(scene_num: int) -> Dict[str, str]:
    scene = get_scene(scene_num)
    return get_engine().resolve_video_settings(scene)


def set_scene_video_settings(
    scene_num: int,
    provider: Optional[str] = None,
    model_name: Optional[str] = None,
    clear: bool = False,
) -> Dict[str, str]:
    settings = get_engine().set_scene_video_settings(
        scene_num, provider=provider, model_name=model_name, clear=clear
    )
    edit_log.add_entry(
        "scene_provider",
        user_note=f"Scene {scene_num} provider → {settings}",
        scene=scene_num,
        action_taken="Updated scene video_provider/model_name in blueprint",
        targets=["nickandme.json"],
        extra=settings,
    )
    return settings


def list_scene_variants(scene_num: int) -> Dict[str, Any]:
    return get_engine().list_scene_variants(scene_num)


def generate_scene_variant(
    scene_num: int,
    provider: str,
    model_name: str,
    *,
    only_existing: bool = True,
    run_qa: bool = False,
    label: Optional[str] = None,
) -> Dict[str, Any]:
    meta = get_engine().generate_scene_variant(
        scene_num,
        provider,
        model_name,
        only_existing=only_existing,
        run_qa=run_qa,
        label=label,
    )
    edit_log.add_entry(
        "scene_variant_generate",
        user_note=f"Generated variant {meta.get('label')} for scene {scene_num}",
        scene=scene_num,
        action_taken=f"{meta.get('clip_count')} clips → assets/variants",
        targets=["assets/variants", "pipeline_state.json"],
        extra=meta,
    )
    return meta


def promote_scene_variant(scene_num: int, variant_id: str) -> str:
    path = get_engine().promote_scene_variant(scene_num, variant_id)
    edit_log.add_entry(
        "scene_variant_promote",
        user_note=f"Promoted variant {variant_id} to main",
        scene=scene_num,
        action_taken=f"Main timeline ← {variant_id}",
        targets=["assets/video", "assets/scenes", "nickandme.json"],
        extra={"variant_id": variant_id, "path": path},
    )
    return path


def snapshot_main_variant(scene_num: int) -> Optional[str]:
    return get_engine().snapshot_main_as_variant(scene_num)


def hero_regen_scene(
    scene_num: int,
    *,
    resolution: str = "720p",
    only_existing: bool = True,
    run_qa: bool = True,
    approve_after: bool = True,
) -> Dict[str, Any]:
    """
    Delivery pass at higher resolution. Does not leave global draft resolution
    permanently set to hero res.
    """
    # Estimate for log
    est = scene_cost_estimate(scene_num, mode="existing" if only_existing else "all")
    meta = get_engine().hero_regen_scene(
        scene_num,
        resolution=resolution,
        only_existing=only_existing,
        run_qa=run_qa,
        approve_after=approve_after,
        snapshot_first=True,
    )
    edit_log.add_entry(
        "scene_hero_regen",
        user_note=f"Hero regen Scene {scene_num} @ {resolution}",
        scene=scene_num,
        action_taken=(
            f"Regenerated clips {meta.get('clip_numbers')} @ {resolution}; "
            f"draft config resolution restored to {meta.get('draft_resolution_restored')}"
        ),
        targets=["assets/video", "assets/scenes", "pipeline_state.json"],
        extra={"hero": meta, "estimate_usd": (est or {}).get("total_usd")},
    )
    return meta


def clear_scene_hero(scene_num: int) -> None:
    get_engine().clear_scene_hero(scene_num)
    edit_log.add_entry(
        "scene_hero_clear",
        user_note=f"Cleared hero flag for scene {scene_num}",
        scene=scene_num,
        action_taken="scene_hero removed — draft again",
        targets=["pipeline_state.json"],
    )


def hero_cost_note(scene_num: int, resolution: str = "720p") -> Dict[str, Any]:
    """Cost estimate if scene were regenerated at hero resolution (temp rate)."""
    eng = get_engine()
    scene = get_scene(scene_num)
    if not scene:
        return {}
    cfg = dict(eng.config)
    cfg["resolution"] = resolution
    clips = scene.get("veo_clips") or []
    on_disk_map = {
        int(c.get("clip_number", 0)): file_is_usable(
            clip_output_path(scene_num, int(c.get("clip_number", 0))), min_bytes=1024
        )
        for c in clips
    }
    return estimate_scene_cost(scene, cfg, only_existing_paths=on_disk_map)


def _on_disk_maps() -> Dict[int, Dict[int, bool]]:
    eng = get_engine()
    out: Dict[int, Dict[int, bool]] = {}
    for s in eng.blueprint.get("scenes", []):
        sn = int(s.get("scene_number") or 0)
        out[sn] = {}
        for c in s.get("veo_clips") or []:
            cn = int(c.get("clip_number", 0))
            out[sn][cn] = file_is_usable(clip_output_path(sn, cn), min_bytes=1024)
    return out


def film_cost_report(
    *,
    draft_resolution: Optional[str] = None,
    hero_resolution: str = "720p",
) -> Dict[str, Any]:
    eng = get_engine()
    hero_by = {}
    for k, v in (eng.state.get("scene_hero") or {}).items():
        try:
            hero_by[int(k)] = v
        except (TypeError, ValueError):
            pass
    return film_budget_report(
        eng.blueprint.get("scenes") or [],
        eng.config,
        on_disk_by_scene=_on_disk_maps(),
        hero_by_scene=hero_by,
        draft_resolution=draft_resolution or str(eng.config.get("resolution") or "720p"),
        hero_resolution=hero_resolution,
    )


def cost_scenario_compare(
    scenarios: List[Dict[str, Any]],
) -> List[Dict[str, Any]]:
    eng = get_engine()
    return scenario_compare(
        eng.blueprint.get("scenes") or [],
        eng.config,
        scenarios,
        on_disk_by_scene=_on_disk_maps(),
    )
