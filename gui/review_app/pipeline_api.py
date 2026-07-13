"""
Thin façade over the film renderer for the Streamlit GUI.
Project-aware: all I/O is relative to the active project directory.
"""
from __future__ import annotations

import json
import os
import re
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from review_app.paths import (
    create_project,
    get_active_project_dir,
    list_projects,
    load_project_meta,
    load_workspace_config,
    projects_root,
    repo_root,
    set_active_project,
    workspace_root,
)

ROOT = repo_root()
_WORKSPACE = workspace_root()
if str(_WORKSPACE) not in sys.path:
    sys.path.insert(0, str(_WORKSPACE))
_GUI_DIR = Path(__file__).resolve().parent.parent
if str(_GUI_DIR) not in sys.path:
    sys.path.insert(0, str(_GUI_DIR))

# Always operate inside the active project (or workspace fallback)
os.chdir(ROOT)

from renderer import (  # noqa: E402
    AgenticGenerationEngine,
    GenerationFailure,
    clip_output_path,
    composite_output_path,
    file_is_usable,
    music_output_path,
)
from review_app import edit_log  # noqa: E402
from review_app import learning  # noqa: E402
from review_app.cost_estimate import (  # noqa: E402
    estimate_scene_cost,
    film_budget_report,
    format_usd,
    scenario_compare,
    summarize_cost_ledger,
)

_engine: Optional[AgenticGenerationEngine] = None


def get_engine(force_reload: bool = False) -> AgenticGenerationEngine:
    global _engine, ROOT
    ROOT = repo_root()
    proj = str(ROOT)
    # chdir is relatively expensive on WSL /mnt/c — only when project changes
    try:
        if os.path.normpath(os.getcwd()) != os.path.normpath(proj):
            os.chdir(ROOT)
    except OSError:
        os.chdir(ROOT)
    if _engine is None or force_reload:
        _engine = AgenticGenerationEngine(install_signals=False, project_dir=proj)
        return _engine
    # Rebind if project switched
    if os.path.normpath(getattr(_engine, "project_dir", "")) != os.path.normpath(proj):
        _engine = AgenticGenerationEngine(install_signals=False, project_dir=proj)
        return _engine
    desired = str(
        (_engine.config or {}).get("blueprint_file")
        or "blueprint.clips.grok.json"
    )
    if os.path.normpath(_engine.blueprint_path) != os.path.normpath(desired):
        # Still same project; config pointed at different blueprint filename
        _engine.blueprint_path = desired
        _engine.load_blueprint()
    return _engine


def _video_dir_index() -> Dict[str, int]:
    """
    One scandir of assets/video → {filename: size}. Faster than per-clip stat on WSL.
    """
    out: Dict[str, int] = {}
    try:
        with os.scandir("assets/video") as it:
            for ent in it:
                if not ent.is_file():
                    continue
                try:
                    out[ent.name] = int(ent.stat().st_size)
                except OSError:
                    continue
    except OSError:
        pass
    return out


def _clip_on_disk_from_index(
    video_index: Dict[str, int], scene_num: int, clip_num: int, min_bytes: int = 1024
) -> Tuple[bool, int]:
    name = f"scene_{int(scene_num):02d}_clip_{int(clip_num):02d}.mp4"
    size = int(video_index.get(name) or 0)
    return size >= min_bytes, size


def reload_engine() -> AgenticGenerationEngine:
    """Rebuild engine; re-import renderer so method signatures match disk (Streamlit stale modules)."""
    global _engine, ROOT, AgenticGenerationEngine, GenerationFailure
    global clip_output_path, composite_output_path, file_is_usable, music_output_path
    ROOT = repo_root()
    os.chdir(ROOT)
    try:
        import importlib

        import renderer as renderer_pkg
        import renderer.engine as engine_mod

        importlib.reload(engine_mod)
        importlib.reload(renderer_pkg)
        AgenticGenerationEngine = renderer_pkg.AgenticGenerationEngine
        GenerationFailure = renderer_pkg.GenerationFailure
        clip_output_path = renderer_pkg.clip_output_path
        composite_output_path = renderer_pkg.composite_output_path
        file_is_usable = renderer_pkg.file_is_usable
        music_output_path = renderer_pkg.music_output_path
    except Exception:
        pass
    _engine = AgenticGenerationEngine(
        install_signals=False, project_dir=str(ROOT)
    )
    return _engine


def active_blueprint_path() -> str:
    eng = get_engine()
    return str(Path(eng.project_dir) / eng.blueprint_path)


def active_project_info() -> Dict[str, Any]:
    proj = get_active_project_dir()
    if proj is None:
        return {
            "id": None,
            "title": "(workspace root)",
            "path": str(workspace_root()),
        }
    meta = load_project_meta(proj)
    meta["path"] = str(proj)
    return meta


def switch_project(project_id: str) -> Dict[str, Any]:
    path = set_active_project(project_id)
    reload_engine()
    edit_log.reload_paths()
    return active_project_info()


def new_project(name: str, title: Optional[str] = None) -> Dict[str, Any]:
    path = create_project(name, title=title)
    reload_engine()
    edit_log.reload_paths()
    return active_project_info()


def list_all_projects() -> List[Dict[str, Any]]:
    return list_projects()


# ---------- Config ----------

def get_config() -> Dict[str, Any]:
    return dict(get_engine().config)


def save_config(updates: Dict[str, Any]) -> Dict[str, Any]:
    eng = get_engine()
    eng.config.update(updates)
    eng.save_config_to_disk()
    if "blueprint_file" in updates:
        eng.blueprint_path = str(updates["blueprint_file"])
        eng.load_blueprint()
    edit_log.add_entry(
        "config_change",
        user_note=f"Updated {len(updates)} config key(s)",
        action_taken="Saved pipeline_config.json",
        extra={"keys": list(updates.keys())},
        targets=["pipeline_config.json"],
    )
    try:
        from review_app.pipeline_progress import invalidate_progress_cache

        invalidate_progress_cache()
    except Exception:
        pass
    return dict(eng.config)


def get_pipeline_ui_progress() -> Dict[str, Any]:
    """User/UI step markers stored on pipeline_state (sidebar progress)."""
    try:
        eng = get_engine()
        ui = eng.state.get("ui_progress")
        return dict(ui) if isinstance(ui, dict) else {}
    except Exception:
        return {}


def mark_pipeline_step(step: str, *, detail: str = "") -> Dict[str, Any]:
    """
    Record that a UI pipeline step was completed (e.g. configuration saved).
    Stored in pipeline_state.json under ui_progress.
    """
    eng = get_engine()
    ui = eng.state.setdefault("ui_progress", {})
    if not isinstance(ui, dict):
        ui = {}
        eng.state["ui_progress"] = ui
    ui[str(step)] = {
        "done": True,
        "at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "detail": detail or "",
    }
    eng.save_state()
    try:
        from review_app.pipeline_progress import invalidate_progress_cache

        invalidate_progress_cache()
    except Exception:
        pass
    return dict(ui)


# ---------- Blueprint / scenes ----------

def list_scenes(*, light: bool = False, include_costs: bool = True) -> List[Dict[str, Any]]:
    """
    light=True: skip thumbs, play_path probing beyond composite, and all cost math
      (fast for home dashboard / scene list shell).
    include_costs=False: same cost skip even when not fully light (still may set play/thumb).
    """
    eng = get_engine()
    out = []
    completed = eng.state.get("scenes_completed") or {}
    stale_by_scene: Dict[int, int] = {}
    stale_clips_by_scene: Dict[int, List[int]] = {}
    # light: count stale from state only (no per-file usable checks)
    if light:
        for key, entry in (eng.state.get("stale_clips") or {}).items():
            if not isinstance(entry, dict):
                continue
            if entry.get("stale") is False:
                continue
            try:
                sn = int(entry.get("scene_number") or str(key).split("_")[0])
            except (TypeError, ValueError):
                continue
            stale_by_scene[sn] = stale_by_scene.get(sn, 0) + 1
            try:
                cn = int(entry.get("clip_number") or str(key).split("_")[-1])
                stale_clips_by_scene.setdefault(sn, []).append(cn)
            except (TypeError, ValueError):
                pass
    else:
        for row in eng.list_stale_clips(only_existing=True):
            sn = row["scene"]
            stale_by_scene[sn] = stale_by_scene.get(sn, 0) + 1
            stale_clips_by_scene.setdefault(sn, []).append(row["clip"])

    video_index = _video_dir_index()
    # scene composites often live under assets/scenes
    scenes_index: Dict[str, int] = {}
    try:
        with os.scandir("assets/scenes") as it:
            for ent in it:
                if ent.is_file():
                    try:
                        scenes_index[ent.name] = int(ent.stat().st_size)
                    except OSError:
                        pass
    except OSError:
        pass

    for s in eng.blueprint.get("scenes", []):
        sn = s.get("scene_number")
        clips = s.get("veo_clips") or []
        n_clips = len(clips)
        on_disk = 0
        on_disk_map: Dict[int, bool] = {}
        for c in clips:
            cn = int(c.get("clip_number", 0))
            ok, _sz = _clip_on_disk_from_index(video_index, int(sn or 0), cn)
            on_disk_map[cn] = ok
            if ok:
                on_disk += 1

        composite = composite_output_path(sn)
        comp_name = os.path.basename(composite)
        composite_ok = int(scenes_index.get(comp_name) or video_index.get(comp_name) or 0) >= 1024
        if not composite_ok and not light:
            composite_ok = file_is_usable(composite, min_bytes=1024)
        play_path = composite if composite_ok else None
        thumb_path = None

        if not light:
            for c in clips:
                cn = int(c.get("clip_number", 0))
                if not play_path and on_disk_map.get(cn):
                    play_path = clip_output_path(sn, cn)
                seed = f"assets/video/scene_{int(sn):02d}_clip_{cn:02d}_seed_frame.png"
                seed_name = os.path.basename(seed)
                if not thumb_path and int(video_index.get(seed_name) or 0) >= 64:
                    thumb_path = seed
            if not thumb_path:
                import glob as _glob

                for pattern in (
                    f"assets/video/scene_{int(sn):02d}_clip_*_qa_frame_01.jpg",
                    f"assets/video/s{sn}c*_frame*.jpg",
                ):
                    matches = sorted(_glob.glob(pattern))
                    if matches:
                        thumb_path = matches[0]
                        break

        hero = (eng.state.get("scene_hero") or {}).get(str(sn))
        dirty_row = learning.get_scene_dirty(eng.state, int(sn)) if sn is not None else None
        flagged_approved = bool(
            completed.get(str(sn)) is True or completed.get(sn) is True
        )
        clips_complete = n_clips > 0 and on_disk >= n_clips
        # ✅ only when approved AND every planned clip is on disk
        approved_ok = flagged_approved and clips_complete
        approved_incomplete = flagged_approved and not clips_complete
        row: Dict[str, Any] = {
            "scene_number": sn,
            "setting": s.get("setting", ""),
            "scene_filename": s.get("scene_filename", ""),
            "clip_count": n_clips,
            "clips_on_disk": on_disk,
            "clips_complete": clips_complete,
            "stale_clips": stale_by_scene.get(sn, 0),
            "approved": approved_ok,
            "approved_flag": flagged_approved,
            "approved_incomplete": approved_incomplete,
            "hero": hero,
            "is_hero": bool(hero),
            "hero_resolution": (hero or {}).get("resolution"),
            "composite_exists": composite_ok,
            "composite_path": composite if composite_ok else None,
            "play_path": play_path,
            "thumb_path": thumb_path,
            "duration": s.get("total_estimated_duration_seconds"),
            "dirty": bool(
                dirty_row and (dirty_row.get("stage1") or dirty_row.get("stage2"))
            ),
            "dirty_stage1": bool(dirty_row and dirty_row.get("stage1")),
            "dirty_stage2": bool(dirty_row and dirty_row.get("stage2")),
            "dirty_reason": (dirty_row or {}).get("reason") or "",
            "dirty_cascade": (
                "stage1→stage2"
                if dirty_row and dirty_row.get("stage1")
                else ("stage2" if dirty_row and dirty_row.get("stage2") else None)
            ),
        }

        if not light and include_costs:
            stale_nums = stale_clips_by_scene.get(sn) or []
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
            row.update(
                {
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
                    "cost_label_stale": format_usd(
                        float(cost_stale.get("total_usd") or 0)
                    ),
                }
            )
        else:
            row.update(
                {
                    "cost_regen_all_usd": None,
                    "cost_regen_existing_usd": None,
                    "cost_regen_stale_usd": None,
                    "cost_label_all": "—",
                    "cost_label_existing": "—",
                    "cost_label_stale": "—",
                }
            )
        out.append(row)
    return out


def home_dashboard() -> Dict[str, Any]:
    """Minimal stats for the home page (no per-scene cost math, no video decode)."""
    eng = get_engine()
    scenes = list_scenes(light=True)
    chars = list_characters(light=True)
    stale = eng.list_stale_clips(only_existing=True)
    wip = wip_path()
    wip_meta = (eng.state.get("wip_movie") or {}) if isinstance(eng.state, dict) else {}
    proj = active_project_info()
    dirty = learning.dirty_summary(eng.state)
    cost = summarize_cost_ledger(eng.get_cost_ledger())
    # Attach dirty flags onto light scene rows for list badges
    dirty_by_sn = {int(r["scene"]): r for r in dirty.get("scenes") or [] if r.get("scene") is not None}
    for s in scenes:
        sn = s.get("scene_number")
        d = dirty_by_sn.get(int(sn)) if sn is not None else None
        s["dirty"] = bool(d)
        s["dirty_stage1"] = bool(d and d.get("stage1"))
        s["dirty_stage2"] = bool(d and d.get("stage2"))
        s["dirty_cascade"] = (d or {}).get("cascade")
    return {
        "title": eng.blueprint.get("movie_title", "Untitled"),
        "project": proj,
        "scenes": scenes,
        "scene_count": len(scenes),
        "approved": sum(1 for s in scenes if s.get("approved")),
        "hero_count": sum(1 for s in scenes if s.get("is_hero")),
        "clips_on_disk": sum(int(s.get("clips_on_disk") or 0) for s in scenes),
        "clips_total": sum(int(s.get("clip_count") or 0) for s in scenes),
        "char_count": len(chars),
        "chars_locked": sum(1 for c in chars if c.get("locked")),
        "stale_count": len(stale),
        "stale_labels": [r.get("label") for r in stale[:15]],
        "dirty_count": dirty.get("dirty_count", 0),
        "dirty_need_stage1": dirty.get("need_stage1", 0),
        "dirty_need_stage2": dirty.get("need_stage2", 0),
        "dirty_scenes": dirty.get("scenes") or [],
        "wip_path": wip,
        "wip_updated_at": wip_meta.get("updated_at"),
        "wip_scene_count": wip_meta.get("scene_count"),
        "blueprint_path": active_blueprint_path(),
        # Running actual spend (list-rate ledger; same source as Cost page)
        "actual_usd": float(cost.get("actual_usd") or 0),
        "actual_events": int(cost.get("event_count") or 0),
        "actual_video_jobs": int(cost.get("video_jobs") or 0),
        "actual_video_sec": float(cost.get("video_sec") or 0),
    }


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
    video_index = _video_dir_index()
    stale_map = eng.state.get("stale_clips") or {}
    job_map = eng.state.get("clip_jobs") or {}
    rows = []
    for c in scene.get("veo_clips") or []:
        cn = int(c.get("clip_number", 0))
        path = clip_output_path(scene_num, cn)
        job = job_map.get(f"{scene_num}_{cn}", {}) or {}
        ap = c.get("audio_payload") or {}
        key = f"{scene_num}_{cn}"
        stale_info = stale_map.get(key) if isinstance(stale_map.get(key), dict) else None
        is_stale = bool(
            (stale_info and stale_info.get("stale", True))
            or job.get("stale")
        )
        on_disk, size_bytes = _clip_on_disk_from_index(video_index, scene_num, cn)
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
                "on_disk": on_disk,
                "size_bytes": size_bytes if on_disk else 0,
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


def pass_clip(
    scene_num: int,
    clip_num: int,
    note: str = "",
    *,
    learning_layer: str = "clip",
) -> None:
    eng = get_engine()
    eng.set_clip_review_status(scene_num, clip_num, "pass", note)
    edit_log.add_entry(
        "clip_pass",
        user_note=note or "Passed",
        scene=scene_num,
        clip=clip_num,
        action_taken="review_status=pass",
        learning_layer=learning_layer or "clip",
        targets=["pipeline_state.json"],
    )


def fail_clip(
    scene_num: int,
    clip_num: int,
    note: str = "",
    *,
    learning_layer: Optional[str] = None,
    mark_dirty: bool = True,
) -> Dict[str, Any]:
    """
    Mark clip failed. If learning_layer is stage1/stage2, mark scene dirty for replan.
    Returns {entry, dirty_row|None}.
    """
    eng = get_engine()
    eng.set_clip_review_status(scene_num, clip_num, "fail", note)
    layer = learning.normalize_layer(
        learning_layer or learning.suggest_layer_from_note(note)
    )
    entry = edit_log.add_entry(
        "clip_fail",
        user_note=note or "Failed",
        scene=scene_num,
        clip=clip_num,
        action_taken="review_status=fail",
        learning_layer=layer,
    )
    dirty_row = None
    keys = learning.dirty_keys_for_layer(layer)
    if mark_dirty and keys:
        dirty_row = learning.mark_scene_dirty(
            eng.state,
            scene_num,
            keys=keys,
            reason=note or f"fail S{scene_num}C{clip_num}",
            entry_id=entry.get("id"),
            learning_layer=layer,
        )
        eng.save_state()
        edit_log.mark_applied(entry["id"], "dirty_marked")
    return {"entry": entry, "dirty_row": dirty_row, "learning_layer": layer}


def regen_clip(
    scene_num: int,
    clip_num: int,
    feedback: str = "",
    apply_to_prompt: bool = True,
    run_qa: bool = True,
    rebuild_wip: bool = True,
    *,
    learning_layer: Optional[str] = None,
    mark_dirty: bool = True,
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
    layer = learning.normalize_layer(
        learning_layer
        or (learning.suggest_layer_from_note(feedback) if feedback else "clip")
    )
    entry = edit_log.add_entry(
        "clip_regen",
        user_note=feedback or "Regenerate without prompt change",
        scene=scene_num,
        clip=clip_num,
        action_taken=f"Wiped and regenerated → {path}; WIP={wip_path or 'skipped'}",
        before=old_vp,
        after=new_vp,
        learning_layer=layer,
        targets=learning.targets_for_layer(layer)
        + ["assets/video", "assets/movie_wip.mp4"],
    )
    keys = learning.dirty_keys_for_layer(layer)
    if mark_dirty and keys:
        learning.mark_scene_dirty(
            eng.state,
            scene_num,
            keys=keys,
            reason=feedback or f"regen S{scene_num}C{clip_num}",
            entry_id=entry.get("id"),
            learning_layer=layer,
        )
        eng.save_state()
        edit_log.mark_applied(entry["id"], "dirty_marked")
    return path


def generate_scene_clips(
    scene_num: int,
    *,
    only_missing: bool = True,
    run_qa: bool = True,
    remux: bool = True,
    rebuild_wip: bool = False,
    progress_cb=None,
) -> Dict[str, Any]:
    """
    Generate video for all (or missing) clips in a scene — primary first-pass UI path.

    only_missing=True: skip clips already on disk.
    progress_cb(dict): optional {event, scene, clip, index, total, message, path?, error?}
    """
    eng = get_engine()
    rows = list_clips(scene_num)
    if not rows:
        raise ValueError(f"Scene {scene_num} has no clips in the Stage 2 plan.")

    todo: List[int] = []
    for row in rows:
        cn = int(row["clip_number"])
        if only_missing and row.get("on_disk"):
            continue
        todo.append(cn)

    summary: Dict[str, Any] = {
        "scene": scene_num,
        "only_missing": only_missing,
        "planned": [int(r["clip_number"]) for r in rows],
        "todo": list(todo),
        "done": [],
        "failed": [],
        "paths": {},
        "composite_path": None,
        "wip_path": None,
        "skipped_existing": [
            int(r["clip_number"])
            for r in rows
            if only_missing and r.get("on_disk")
        ],
    }

    total = len(todo)
    if progress_cb:
        progress_cb(
            {
                "event": "start",
                "scene": scene_num,
                "total": total,
                "message": (
                    f"Scene {scene_num}: {total} clip(s) to generate"
                    if total
                    else f"Scene {scene_num}: nothing to do (all on disk)"
                ),
            }
        )

    for i, cn in enumerate(todo, start=1):
        if progress_cb:
            progress_cb(
                {
                    "event": "clip_start",
                    "scene": scene_num,
                    "clip": cn,
                    "index": i,
                    "total": total,
                    "message": f"Generating S{scene_num:02d} C{cn} ({i}/{total})…",
                }
            )
        try:
            path = eng.regenerate_clip(
                scene_num, cn, feedback=None, run_qa=run_qa
            )
            summary["done"].append(cn)
            summary["paths"][str(cn)] = path
            if progress_cb:
                progress_cb(
                    {
                        "event": "clip_done",
                        "scene": scene_num,
                        "clip": cn,
                        "index": i,
                        "total": total,
                        "path": path,
                        "message": f"Done S{scene_num:02d} C{cn}",
                    }
                )
        except Exception as e:
            summary["failed"].append({"clip": cn, "error": str(e)})
            if progress_cb:
                progress_cb(
                    {
                        "event": "clip_error",
                        "scene": scene_num,
                        "clip": cn,
                        "index": i,
                        "total": total,
                        "error": str(e),
                        "message": f"Failed S{scene_num:02d} C{cn}: {e}",
                    }
                )
            # stop on first failure so user can fix API key / prompt before burning more
            break

    if remux and summary["done"]:
        try:
            if rebuild_wip:
                summary["wip_path"] = eng.remux_scenes_and_rebuild_wip(
                    [scene_num], reason=f"after generate scene {scene_num}"
                )
                summary["composite_path"] = eng.remux_scene_from_disk(scene_num)
            else:
                summary["composite_path"] = eng.remux_scene_from_disk(scene_num)
        except Exception as e:
            summary["remux_error"] = str(e)

    edit_log.add_entry(
        "scene_generate",
        user_note=(
            f"Generate scene {scene_num}: "
            f"{len(summary['done'])} ok, {len(summary['failed'])} failed, "
            f"{len(summary['skipped_existing'])} skipped"
        ),
        scene=scene_num,
        action_taken=f"done={summary['done']} failed={summary['failed']}",
        learning_layer="clip",
        targets=["assets/video", "assets/scenes"],
        extra={
            "done": summary["done"],
            "failed": summary["failed"],
            "skipped": summary["skipped_existing"],
            "only_missing": only_missing,
        },
    )
    if progress_cb:
        progress_cb(
            {
                "event": "done",
                "scene": scene_num,
                "total": total,
                "message": (
                    f"Finished: {len(summary['done'])} generated, "
                    f"{len(summary['failed'])} failed"
                ),
                **{k: summary[k] for k in ("done", "failed", "composite_path")},
            }
        )
    return summary


def generate_scenes_clips(
    scene_nums: List[int],
    *,
    only_missing: bool = True,
    run_qa: bool = True,
    remux: bool = True,
    rebuild_wip: bool = False,
    stop_on_fail: bool = True,
    progress_cb=None,
) -> Dict[str, Any]:
    """
    Generate video for multiple scenes in order (batch from scene list UI).

    progress_cb events include scene_index / scene_total for outer progress.
    """
    nums = sorted({int(n) for n in scene_nums if n is not None})
    if not nums:
        raise ValueError("No scenes selected.")

    batch: Dict[str, Any] = {
        "scenes_requested": list(nums),
        "only_missing": only_missing,
        "per_scene": [],
        "scenes_ok": [],
        "scenes_failed": [],
        "scenes_skipped": [],
        "clips_done": 0,
        "clips_failed": 0,
    }
    scene_total = len(nums)

    for si, sn in enumerate(nums, start=1):
        if progress_cb:
            progress_cb(
                {
                    "event": "scene_start",
                    "scene": sn,
                    "scene_index": si,
                    "scene_total": scene_total,
                    "message": f"Scene {sn} ({si}/{scene_total})…",
                }
            )

        def _wrap(ev: dict, _sn: int = sn, _si: int = si) -> None:
            if not progress_cb:
                return
            payload = dict(ev)
            payload.setdefault("scene", _sn)
            payload["scene_index"] = _si
            payload["scene_total"] = scene_total
            progress_cb(payload)

        try:
            summary = generate_scene_clips(
                sn,
                only_missing=only_missing,
                run_qa=run_qa,
                remux=remux,
                rebuild_wip=False,  # remux WIP once at end if requested
                progress_cb=_wrap,
            )
        except Exception as e:
            summary = {
                "scene": sn,
                "done": [],
                "failed": [{"clip": None, "error": str(e)}],
                "skipped_existing": [],
                "todo": [],
            }

        batch["per_scene"].append(summary)
        batch["clips_done"] += len(summary.get("done") or [])
        batch["clips_failed"] += len(summary.get("failed") or [])

        if summary.get("failed"):
            batch["scenes_failed"].append(sn)
            if stop_on_fail:
                if progress_cb:
                    progress_cb(
                        {
                            "event": "batch_stopped",
                            "scene": sn,
                            "scene_index": si,
                            "scene_total": scene_total,
                            "message": f"Stopped after failure on scene {sn}",
                        }
                    )
                break
        elif not (summary.get("todo") or summary.get("done")):
            # nothing to do / all skipped
            batch["scenes_skipped"].append(sn)
            batch["scenes_ok"].append(sn)
        else:
            batch["scenes_ok"].append(sn)

    if rebuild_wip and batch["scenes_ok"]:
        try:
            eng = get_engine()
            batch["wip_path"] = eng.remux_scenes_and_rebuild_wip(
                batch["scenes_ok"],
                reason=f"after batch generate scenes {batch['scenes_ok']}",
            )
        except Exception as e:
            batch["wip_error"] = str(e)

    edit_log.add_entry(
        "scenes_batch_generate",
        user_note=(
            f"Batch generate {len(nums)} scene(s): "
            f"{len(batch['scenes_ok'])} ok, {len(batch['scenes_failed'])} failed"
        ),
        action_taken=(
            f"ok={batch['scenes_ok']} failed={batch['scenes_failed']} "
            f"clips_done={batch['clips_done']}"
        ),
        learning_layer="clip",
        targets=["assets/video", "assets/scenes"],
        extra={
            "scenes_requested": nums,
            "scenes_ok": batch["scenes_ok"],
            "scenes_failed": batch["scenes_failed"],
            "only_missing": only_missing,
        },
    )
    if progress_cb:
        progress_cb(
            {
                "event": "batch_done",
                "scene_total": scene_total,
                "message": (
                    f"Batch done: {len(batch['scenes_ok'])} scene(s) ok, "
                    f"{len(batch['scenes_failed'])} failed, "
                    f"{batch['clips_done']} clip(s) generated"
                ),
            }
        )
    return batch


def log_clip_feedback(
    scene_num: int,
    clip_num: int,
    note: str,
    *,
    learning_layer: Optional[str] = None,
    mark_dirty: bool = True,
    before: str = "",
) -> Dict[str, Any]:
    """Log feedback without pass/fail/regen; optionally dirty the scene."""
    eng = get_engine()
    layer = learning.normalize_layer(
        learning_layer or learning.suggest_layer_from_note(note)
    )
    entry = edit_log.add_entry(
        "clip_note",
        user_note=note or "Note",
        scene=scene_num,
        clip=clip_num,
        action_taken="Logged without regen",
        before=before,
        after=before,
        learning_layer=layer,
    )
    dirty_row = None
    keys = learning.dirty_keys_for_layer(layer)
    if mark_dirty and keys:
        dirty_row = learning.mark_scene_dirty(
            eng.state,
            scene_num,
            keys=keys,
            reason=note,
            entry_id=entry.get("id"),
            learning_layer=layer,
        )
        eng.save_state()
        edit_log.mark_applied(entry["id"], "dirty_marked")
    return {"entry": entry, "dirty_row": dirty_row, "learning_layer": layer}


def mark_scene_needs_replan(
    scene_num: int,
    *,
    cascade: str = "stage2",
    reason: str = "",
    note: str = "",
) -> Dict[str, Any]:
    """
    cascade: 'stage2' | 'stage1' (stage1 implies stage1+stage2 dirty).
    """
    eng = get_engine()
    cascade = (cascade or "stage2").lower()
    if cascade in ("stage1", "s1", "bible", "stage1→stage2", "stage1->stage2"):
        keys = ("stage1", "stage2")
        layer = "stage1"
        action = "Marked needs Stage 1 re-bible then Stage 2 replan"
    else:
        keys = ("stage2",)
        layer = "stage2"
        action = "Marked needs Stage 2 replan"
    entry = edit_log.add_entry(
        "scene_dirty",
        user_note=note or reason or action,
        scene=scene_num,
        action_taken=action,
        learning_layer=layer,
        suggested_rule=reason or note,
    )
    row = learning.mark_scene_dirty(
        eng.state,
        scene_num,
        keys=keys,
        reason=reason or note or action,
        entry_id=entry.get("id"),
        learning_layer=layer,
    )
    eng.save_state()
    edit_log.mark_applied(entry["id"], "dirty_marked")
    return {"entry": entry, "dirty_row": row}


def clear_scene_replan_flag(
    scene_num: int,
    *,
    keys: Optional[List[str]] = None,
) -> None:
    eng = get_engine()
    learning.clear_scene_dirty(eng.state, scene_num, keys=keys)
    eng.save_state()
    edit_log.add_entry(
        "scene_dirty_clear",
        user_note=f"Cleared dirty flags for scene {scene_num}"
        + (f" ({','.join(keys)})" if keys else " (all)"),
        scene=scene_num,
        action_taken="scene_dirty cleared",
        learning_layer="clip",
        targets=["pipeline_state.json"],
    )


def list_dirty_scenes() -> List[Dict[str, Any]]:
    return learning.list_dirty_scenes(get_engine().state)


def dirty_summary() -> Dict[str, Any]:
    return learning.dirty_summary(get_engine().state)


def get_scene_dirty(scene_num: int) -> Optional[Dict[str, Any]]:
    return learning.get_scene_dirty(get_engine().state, scene_num)


def approve_scene(
    scene_num: int,
    *,
    remux: bool = True,
    rebuild_wip: bool = False,
    background_wip: bool = True,
    require_all_clips: bool = True,
) -> Dict[str, Any]:
    """
    Mark scene approved (+ optional remux).

    background_wip=True (default): schedule smart WIP rebuild/append in a daemon
    thread so the UI returns immediately. Concurrent full rebuilds cancel & restart;
    safe appends are queued.
    rebuild_wip=True: also rebuild WIP synchronously (slow; rarely needed).
    require_all_clips=True: refuse approval if any planned clip is missing on disk.
    """
    eng = get_engine()
    if require_all_clips:
        scene = get_scene(scene_num)
        if not scene:
            raise ValueError(f"Scene {scene_num} not found")
        missing: List[int] = []
        for c in scene.get("veo_clips") or []:
            cn = int(c.get("clip_number") or 0)
            if not file_is_usable(clip_output_path(scene_num, cn), min_bytes=1024):
                missing.append(cn)
        if missing:
            raise ValueError(
                f"Cannot approve scene {scene_num}: missing clip(s) "
                f"{missing} ({len(missing)} not on disk). Generate them first."
            )
    result = eng.approve_scene(
        scene_num, remux=remux, rebuild_wip=rebuild_wip
    )
    if not isinstance(result, dict):
        result = {"approved": True, "scene": scene_num}

    bits = ["scenes_completed"]
    if remux:
        bits.append("remux")
    if rebuild_wip:
        bits.append("WIP sync")

    if background_wip and not rebuild_wip:
        try:
            from review_app.wip_jobs import schedule_wip_update

            eng = get_engine()
            job = schedule_wip_update(
                eng, scene_num=int(scene_num), reason=f"after approve S{scene_num}"
            )
            result["wip_job"] = job
            bits.append(f"WIP bg ({job.get('mode') or 'job'})")
        except Exception as e:
            result["wip_job_error"] = str(e)

    extra = {k: v for k, v in result.items() if k != "wip_job"}
    job = result.get("wip_job") if isinstance(result.get("wip_job"), dict) else {}
    extra["wip_job_status"] = job.get("status")
    extra["wip_job_mode"] = job.get("mode")
    edit_log.add_entry(
        "scene_approve",
        user_note=f"Approved scene {scene_num}",
        scene=scene_num,
        action_taken=" + ".join(bits),
        targets=["pipeline_state.json", "assets"],
        extra=extra,
    )
    try:
        from review_app.pipeline_progress import invalidate_progress_cache

        invalidate_progress_cache()
    except Exception:
        pass
    return result


def wip_job_status() -> Dict[str, Any]:
    """Background WIP rebuild/append status (memory + pipeline_state)."""
    try:
        from review_app.wip_jobs import get_wip_job_status

        live = get_wip_job_status()
        if live.get("status") and live.get("status") != "idle":
            return live
    except Exception:
        pass
    try:
        eng = get_engine()
        job = eng.state.get("wip_job")
        if isinstance(job, dict):
            return job
    except Exception:
        pass
    return {"status": "idle", "message": ""}


def remux_scene(scene_num: int) -> Optional[str]:
    return get_engine().remux_scene_from_disk(scene_num)


def rebuild_wip_movie(
    reason: str = "manual refresh",
    *,
    approved_only: bool = False,
) -> Optional[str]:
    return get_engine().rebuild_wip_movie(
        reason=reason, approved_only=approved_only, force=True
    )


def remux_scenes_and_rebuild_wip(
    scene_nums: List[int], reason: str = ""
) -> Optional[str]:
    return get_engine().remux_scenes_and_rebuild_wip(scene_nums, reason=reason)


# ---------- Characters ----------

def _index_clips_by_character() -> Dict[str, List[Tuple[int, int]]]:
    eng = get_engine()
    seeds = eng.blueprint.get("global_production_variables", {}).get(
        "character_seed_tokens", {}
    )
    keys = list(seeds.keys())
    index: Dict[str, List[Tuple[int, int]]] = {k: [] for k in keys}
    for scene in eng.blueprint.get("scenes", []):
        sn = int(scene.get("scene_number") or 0)
        for clip in scene.get("veo_clips") or []:
            cn = int(clip.get("clip_number") or 0)
            vp = clip.get("visual_prompt") or ""
            if not vp:
                continue
            for key in keys:
                if key in vp:
                    index[key].append((sn, cn))
    return index


def character_display_name(key: str, info: Optional[Dict[str, Any]] = None) -> str:
    """
    Human-facing cast name for the GUI (director view).
    Technical seed key stays for refs / pipeline; video prompts still use tokens.

    For Character_P (Nick's brother): prefer canonical_given_name (book reveal),
    then a non-technical voice_label — never show bare "P" / "Narrator" if we can
    avoid it.
    """
    info = info if isinstance(info, dict) else {}
    label = (info.get("voice_label") or "").strip()
    canonical = (info.get("canonical_given_name") or "").strip()
    low_label = label.lower()
    is_p_token = key == "Character_P" or key.startswith("Character_P_")

    def _with_age(base: str) -> str:
        b = (base or "").strip()
        if not b:
            return b
        if key.endswith("_Young") or "child" in low_label:
            if re.search(r"\b(young|child)\b", b, re.I):
                return b
            return f"{b} (young)"
        if key.endswith("_Teen") or "teen" in low_label:
            if re.search(r"\bteen\b", b, re.I):
                return b
            return f"{b} (teen)"
        return b

    def _is_technical_label(s: str) -> bool:
        s = (s or "").strip()
        if not s or s == key or s.startswith("Character_"):
            return True
        if re.fullmatch(r"[A-Za-z]", s):
            return True
        # "P (narrator…)", "P as child", bare "Narrator" as generic role
        if re.match(r"^p\b", s, re.I) and (
            "narrator" in s.lower() or s.lower() in ("p", "p as child", "p as teen")
        ):
            return True
        if s.lower() in ("narrator", "the narrator"):
            return True
        return False

    # 1) Book given name (director may know it; video still withholds until reveal)
    if canonical:
        return _with_age(canonical)

    # 2) Strip Character_ prefix (prefer human id over SFX-style voice_label)
    if key.startswith("Character_"):
        rest = key[len("Character_") :]
        if rest.endswith("_Young"):
            return f"{rest[: -len('_Young')]} (young)"
        if rest.endswith("_Teen"):
            return f"{rest[: -len('_Teen')]} (teen)"
        # Prefer seed key base over labels like "Buster_DogYips"
        if rest and rest.lower() not in ("p", "narrator"):
            return _with_age(rest.replace("_", " "))

    # 3) Friendly voice_label (only if plain words, not Token_Style)
    if label and not _is_technical_label(label) and "_" not in label:
        return label

    # 4) Character_P family — Nick's brother (not "Narrator")
    if is_p_token:
        return _with_age("Nick's brother")

    if key.startswith("Character_"):
        return key[len("Character_") :].replace("_", " ")
    return key


def load_stage1_document() -> Optional[Dict[str, Any]]:
    """Load Stage 1 scenes bible for the active project (if present)."""
    paths = stage1_paths()
    p = Path(paths.get("scenes_json") or "")
    if not p.is_file():
        return None
    try:
        data = json.loads(p.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except (json.JSONDecodeError, OSError):
        return None


def stage1_character_seeds() -> Dict[str, Any]:
    """character_seed_tokens from Stage 1 bible (empty dict if missing)."""
    doc = load_stage1_document()
    if not doc:
        return {}
    gpv = doc.get("global_production_variables") or {}
    seeds = gpv.get("character_seed_tokens") or {}
    return dict(seeds) if isinstance(seeds, dict) else {}


def _book_image_inventory() -> List[Dict[str, Any]]:
    """Flatten source/book_images (manifest + disk) into candidate rows."""
    proj = get_active_project_dir()
    if proj is None:
        return []
    source = proj / "source"
    img_dir = source / "book_images"
    rows: List[Dict[str, Any]] = []
    man_path = img_dir / "manifest.json"
    if man_path.is_file():
        try:
            man = json.loads(man_path.read_text(encoding="utf-8"))
            for im in man.get("images") or []:
                if not isinstance(im, dict):
                    continue
                rel = str(im.get("path") or "")
                # manifest paths are relative to source/
                fp = source / rel if rel else None
                if fp is None or not fp.is_file():
                    name = Path(rel).name if rel else ""
                    fp = img_dir / name if name else None
                if fp is None or not fp.is_file():
                    continue
                rows.append(
                    {
                        "path": str(fp.relative_to(proj)).replace("\\", "/"),
                        "abs": str(fp),
                        "page": int(im.get("page") or 0),
                        "kind": str(im.get("kind") or ""),
                        "name": fp.name.lower(),
                    }
                )
        except (json.JSONDecodeError, OSError, ValueError):
            pass
    if not rows and img_dir.is_dir():
        try:
            for f in sorted(img_dir.iterdir()):
                if f.suffix.lower() not in (".png", ".jpg", ".jpeg", ".webp"):
                    continue
                rows.append(
                    {
                        "path": str(f.relative_to(proj)).replace("\\", "/"),
                        "abs": str(f),
                        "page": 0,
                        "kind": "file",
                        "name": f.name.lower(),
                    }
                )
        except OSError:
            pass
    return rows


def attach_book_images_to_character_seeds(
    *,
    force: bool = False,
    copy_into_assets: bool = True,
) -> Dict[str, Any]:
    """
    After Stage 1 / PDF extract: attach book page images to each character seed.

    Delegates to scripts/two_stage_adaptation/attach_character_images.py so CLI
    and GUI share one implementation (LLM page picks + heuristic fallback).
    """
    import importlib.util

    proj = get_active_project_dir()
    if proj is None:
        return {"ok": False, "reason": "no_project"}

    script = workspace_root() / "scripts" / "two_stage_adaptation" / "attach_character_images.py"
    if not script.is_file():
        return {"ok": False, "reason": f"missing {script}"}
    spec = importlib.util.spec_from_file_location("attach_character_images", script)
    if spec is None or spec.loader is None:
        return {"ok": False, "reason": "cannot_load_attach_character_images"}
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    result = mod.attach_character_images(
        project_id=proj.name,
        force=force,
        copy_into_assets=copy_into_assets,
        update_blueprint=True,
    )
    # Reload engine blueprint if seeds were written
    try:
        if result.get("ok") and result.get("blueprint_updated"):
            reload_engine()
    except Exception:
        pass
    return result


def ensure_blueprint_character_seeds_from_stage1(
    *, force: bool = False
) -> Dict[str, Any]:
    """
    If the Stage 2 blueprint has no character_seed_tokens, copy them from Stage 1.

    Common after Stage 1 before Stage 2 planning — Characters UI / Stage 0 need seeds
    on the generate blueprint the engine loads.
    Also attaches book_images as design_reference_images when present.
    """
    eng = get_engine()
    gpv = eng.blueprint.setdefault("global_production_variables", {})
    existing = gpv.get("character_seed_tokens") or {}
    if existing and not force:
        # Still try to attach book refs if missing
        try:
            attach_book_images_to_character_seeds(force=False)
        except Exception:
            pass
        return {
            "synced": False,
            "reason": "blueprint_already_has_seeds",
            "count": len(existing),
        }
    stage1_seeds = stage1_character_seeds()
    if not stage1_seeds:
        return {"synced": False, "reason": "no_stage1_seeds", "count": 0}
    gpv["character_seed_tokens"] = stage1_seeds
    # Align title/runtime from Stage 1 when blueprint is still a stub
    s1 = load_stage1_document() or {}
    if s1.get("movie_title") and not (
        eng.blueprint.get("scenes") or []
    ):
        # Only overwrite title on empty Stage 2 stub
        if s1.get("movie_title") not in (None, "", "Nick and Me"):
            eng.blueprint["movie_title"] = s1.get("movie_title")
        if s1.get("source_book_title"):
            eng.blueprint["source_book_title"] = s1.get("source_book_title")
    s1_rt = (s1.get("global_production_variables") or {}).get(
        "total_runtime_target_seconds"
    ) or s1.get("cumulative_duration_target_seconds")
    if s1_rt and not (eng.blueprint.get("scenes") or []):
        gpv["total_runtime_target_seconds"] = int(s1_rt)
    eng.save_blueprint_to_disk()
    img_attach: Dict[str, Any] = {}
    try:
        img_attach = attach_book_images_to_character_seeds(force=True)
    except Exception as e:
        img_attach = {"ok": False, "error": str(e)}
    return {
        "synced": True,
        "reason": "copied_from_stage1",
        "count": len(stage1_seeds),
        "keys": list(stage1_seeds.keys()),
        "book_images": img_attach,
    }


def is_voice_only_character(char_key: str, info: Optional[Dict[str, Any]] = None) -> bool:
    """Narrator / never_on_screen — no likeness plate or locked ref image."""
    key = str(char_key or "")
    pol = ""
    if isinstance(info, dict):
        pol = str(info.get("display_name_policy") or "").lower()
    if not pol:
        try:
            seeds = (
                get_engine()
                .blueprint.get("global_production_variables", {})
                .get("character_seed_tokens", {})
                or {}
            )
            seed = seeds.get(key) if isinstance(seeds, dict) else None
            if isinstance(seed, dict):
                pol = str(seed.get("display_name_policy") or "").lower()
        except Exception:
            pass
    return (
        "never" in pol
        or key.endswith("_Narrator")
        or key == "Character_Narrator"
        or "narrator" in key.lower()
    )


def scrub_voice_only_character_images(char_key: Optional[str] = None) -> Dict[str, Any]:
    """
    Drop book plates / locked ref files for narrator-style seeds.
    If char_key is None, scrub all voice-only seeds in the active project.
    """
    eng = get_engine()
    seeds = eng.blueprint.setdefault("global_production_variables", {}).setdefault(
        "character_seed_tokens", {}
    )
    if not isinstance(seeds, dict):
        return {"ok": False, "reason": "no_seeds", "cleared": []}
    keys = [char_key] if char_key else list(seeds.keys())
    cleared: List[str] = []
    removed_files: List[str] = []
    for key in keys:
        if not key or key not in seeds:
            continue
        info = seeds.get(key) if isinstance(seeds.get(key), dict) else {}
        if not is_voice_only_character(key, info if isinstance(info, dict) else {}):
            continue
        seed = seeds[key]
        if not isinstance(seed, dict):
            continue
        for fld in (
            "design_reference_images",
            "book_reference_images",
            "source_image_pages",
        ):
            seed.pop(fld, None)
        # Keep a bare placeholder name for schema if present, but never require a file
        if not seed.get("reference_image_placeholder"):
            seed["reference_image_placeholder"] = f"{key.lower()}_voice_only.txt"
        # Remove any mistaken locked ref / variants on disk
        try:
            ref = eng.character_ref_path(key)
            if ref and os.path.isfile(ref):
                os.remove(ref)
                removed_files.append(ref)
            for vp in eng.character_variant_paths(key):
                if os.path.isfile(vp):
                    os.remove(vp)
                    removed_files.append(vp)
        except OSError:
            pass
        # Stray bookref copies named for narrator
        try:
            chars_dir = Path(eng.project_dir) / "assets" / "characters"
            if chars_dir.is_dir():
                slug = key.lower().replace("character_", "")
                for p in chars_dir.glob(f"*{slug}*"):
                    if p.is_file() and p.suffix.lower() in (
                        ".png",
                        ".jpg",
                        ".jpeg",
                        ".webp",
                    ):
                        p.unlink(missing_ok=True)
                        removed_files.append(str(p))
        except OSError:
            pass
        cleared.append(key)
    if cleared:
        eng.save_blueprint_to_disk()
    return {"ok": True, "cleared": cleared, "removed_files": removed_files}


def list_characters(*, light: bool = False) -> List[Dict[str, Any]]:
    eng = get_engine()
    # After Stage 1, blueprint may still be an empty Stage 2 stub — pull seeds.
    # Skip disk write on light=True (nav/progress-adjacent); full page load may sync.
    seeds = eng.blueprint.get("global_production_variables", {}).get(
        "character_seed_tokens", {}
    )
    if not seeds:
        if not light:
            try:
                ensure_blueprint_character_seeds_from_stage1(force=False)
                seeds = eng.blueprint.get("global_production_variables", {}).get(
                    "character_seed_tokens", {}
                )
            except Exception:
                pass
        if not seeds:
            seeds = stage1_character_seeds()
    # One-time hygiene: strip mistaken narrator plates (cheap; skips if none)
    if not light and seeds:
        try:
            for _k, _info in list(seeds.items()):
                if is_voice_only_character(_k, _info if isinstance(_info, dict) else {}):
                    if (
                        (isinstance(_info, dict) and (
                            _info.get("design_reference_images")
                            or _info.get("book_reference_images")
                            or _info.get("source_image_pages")
                        ))
                        or os.path.isfile(eng.character_ref_path(_k))
                    ):
                        scrub_voice_only_character_images(_k)
                        seeds = eng.blueprint.get("global_production_variables", {}).get(
                            "character_seed_tokens", {}
                        )
                        break
        except Exception:
            pass
    index = {} if light else _index_clips_by_character()
    stale_by_char: Dict[str, List[Tuple[int, int]]] = {k: [] for k in seeds}
    if not light:
        for r in eng.list_stale_clips(only_existing=True):
            for ck in r.get("characters") or []:
                if ck in stale_by_char:
                    stale_by_char[ck].append((r["scene"], r["clip"]))

    rows = []
    for key, info in seeds.items():
        if not isinstance(info, dict):
            info = {}
        voice_only = is_voice_only_character(key, info)
        ref = eng.character_ref_path(key)
        has_ref = (not voice_only) and os.path.isfile(ref)
        variants = (
            []
            if voice_only
            else [p for p in eng.character_variant_paths(key) if os.path.isfile(p)]
        )
        hits = index.get(key) or []
        rev_entry = (eng.state.get("character_revisions") or {}).get(key) or {}
        stale_for_char = stale_by_char.get(key) or []
        display = character_display_name(key, info)
        # Narrator is "ready" when voice_profile is set — no image lock required
        voice_ready = bool((info.get("voice_profile") or "").strip())
        rows.append(
            {
                "key": key,
                "display_name": display,
                "name": display,  # alias for UI
                "description": info.get("description", ""),
                "age_band": info.get("age_band"),
                "variant_of": info.get("variant_of"),
                "display_name_policy": info.get("display_name_policy") or "",
                "canonical_given_name": info.get("canonical_given_name") or "",
                "voice_only": voice_only,
                "ref_path": ref if has_ref else None,
                "locked": voice_ready if voice_only else has_ref,
                "variants": variants,
                "clip_count": len(hits),
                "clips": hits[:50],
                "revision": int(rev_entry.get("revision", 0)),
                "revision_updated_at": rev_entry.get("updated_at"),
                "revision_reason": rev_entry.get("reason"),
                "stale_clip_count": len(stale_for_char),
                "stale_clips": stale_for_char[:40],
                "voice_profile": info.get("voice_profile") or "",
                "voice_label": info.get("voice_label") or "",
            }
        )

    def sort_key(r):
        k = r["key"]
        name = (r.get("display_name") or k).lower()
        if k.endswith("_Young"):
            return (1, name, k)
        if k.endswith("_Teen"):
            return (2, name, k)
        return (0, name, k)

    return sorted(rows, key=sort_key)


def get_character_voice(char_key: str) -> Dict[str, str]:
    return get_engine().get_character_voice_profile(char_key)


# Presets for Grok native narrator / character voice_profile (no external TTS)
NARRATOR_VOICE_PRESETS: Dict[str, Dict[str, str]] = {
    "male_warm": {
        "voice_gender": "male",
        "voice_label": "Narrator_WarmMale",
        "voice_profile": (
            "Warm adult male storyteller, medium-low pitch, unhurried friendly pace, "
            "clear diction for rhymes, gentle smile in the voice; same voice every scene."
        ),
    },
    "female_warm": {
        "voice_gender": "female",
        "voice_label": "Narrator_WarmFemale",
        "voice_profile": (
            "Warm adult female storyteller, medium pitch, unhurried friendly pace, "
            "clear diction for rhymes, gentle smile in the voice; same voice every scene."
        ),
    },
    "male_deep": {
        "voice_gender": "male",
        "voice_label": "Narrator_DeepMale",
        "voice_profile": (
            "Deep adult male storyteller, rich low pitch, calm deliberate pace, "
            "clear storytelling diction; same voice every scene."
        ),
    },
    "female_bright": {
        "voice_gender": "female",
        "voice_label": "Narrator_BrightFemale",
        "voice_profile": (
            "Bright adult female storyteller, medium-high pitch, lively but clear pace, "
            "expressive for children's books; same voice every scene."
        ),
    },
}


def save_character_voice(
    char_key: str,
    *,
    voice_profile: Optional[str] = None,
    voice_label: Optional[str] = None,
    voice_gender: Optional[str] = None,
) -> Dict[str, Any]:
    """Save Grok native voice_profile (+ optional gender) on character seed."""
    eng = get_engine()
    try:
        info = eng.set_character_voice_profile(
            char_key,
            voice_profile=voice_profile,
            voice_label=voice_label,
            voice_gender=voice_gender,
        )
    except TypeError as e:
        # Stale Streamlit process may lack newer kwargs — patch seed fields directly
        if "voice_gender" not in str(e) and "tts_voice" not in str(e):
            raise
        info = eng.set_character_voice_profile(
            char_key,
            voice_profile=voice_profile,
            voice_label=voice_label,
        )
        seeds = eng.blueprint.setdefault(
            "global_production_variables", {}
        ).setdefault("character_seed_tokens", {})
        seed = seeds.get(char_key)
        if isinstance(seed, dict):
            seed.pop("tts_voice", None)
            seed.pop("edge_tts_voice", None)
            if voice_gender is not None:
                g = str(voice_gender).strip().lower()
                if g in ("male", "female", "neutral"):
                    seed["voice_gender"] = g
                elif g in ("", "auto", "unset"):
                    seed.pop("voice_gender", None)
            eng.save_blueprint_to_disk()
            info = dict(seed)
    edit_log.add_entry(
        "character_voice",
        user_note=f"Updated voice for {char_key}",
        character=char_key,
        action_taken="Saved voice_profile / voice_label / gender on character seed",
        targets=["blueprint"],
        extra={
            "voice_profile": (voice_profile or "")[:200],
            "voice_gender": voice_gender or "",
        },
    )
    return info


def generate_character_variants(char_key: str) -> Dict[str, Any]:
    """
    Generate 3 variants. Returns {paths, mode, book_refs} so the UI can show
    whether book-art references were used (required for picture-book likeness).
    """
    if is_voice_only_character(char_key):
        raise GenerationFailure(
            f"{char_key} is voice-only (narrator) — no reference image or variants."
        )
    eng = get_engine()
    # Prefer book-art edits; do not silently invent a different look
    paths = eng.generate_character_variants(char_key, allow_text_fallback=False)
    meta = getattr(eng, "_last_character_gen_meta", None) or {}
    result = {
        "paths": paths,
        "mode": meta.get("mode") or "unknown",
        "book_refs": list(meta.get("book_refs") or []),
        "edit_error": meta.get("edit_error"),
    }
    edit_log.add_entry(
        "character_variants",
        user_note=(
            f"Generated {len(paths)} variants for {char_key} "
            f"(mode={result['mode']})"
        ),
        character=char_key,
        action_taken=", ".join(paths),
        targets=["assets/characters"],
        extra={
            "mode": result["mode"],
            "book_refs": [os.path.basename(p) for p in result["book_refs"]],
        },
    )
    return result


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


def _after_character_lock(char_key: str, path: str, note: str) -> str:
    edit_log.add_entry(
        "character_lock",
        user_note=note,
        character=char_key,
        action_taken=f"Locked → {path}",
        targets=["assets/characters"],
    )
    try:
        chars = list_characters(light=True)
        # Mark step complete when all on-screen (non-narrator) chars locked
        need = [
            c
            for c in chars
            if not (
                str(c.get("key") or "").endswith("_Narrator")
                or c.get("key") == "Character_Narrator"
                or "never" in str(c.get("display_name_policy") or "").lower()
            )
        ]
        if need and all(c.get("locked") for c in need):
            mark_pipeline_step(
                "characters", detail=f"{len(need)}/{len(need)} on-screen locked"
            )
        try:
            from review_app.pipeline_progress import invalidate_progress_cache

            invalidate_progress_cache()
        except Exception:
            pass
    except Exception:
        pass
    return path


def lock_character_variant(char_key: str, variant_index: int) -> str:
    if is_voice_only_character(char_key):
        raise GenerationFailure(
            f"{char_key} is voice-only (narrator) — no reference image to lock."
        )
    eng = get_engine()
    path = eng.lock_character_variant(char_key, variant_index)
    return _after_character_lock(
        char_key, path, f"Locked generated variant {variant_index}"
    )


def lock_character_from_image(char_key: str, image_path: str) -> str:
    """Lock a book plate (or any image path) as the character reference."""
    if is_voice_only_character(char_key):
        raise GenerationFailure(
            f"{char_key} is voice-only (narrator) — no reference image to lock."
        )
    eng = get_engine()
    path = eng.lock_character_from_path(char_key, image_path)
    return _after_character_lock(
        char_key, path, f"Locked book plate {os.path.basename(image_path)}"
    )


def clips_using_character_detail(
    char_key: str,
    *,
    only_existing: bool = False,
    only_scene: Optional[int] = None,
) -> List[Dict[str, Any]]:
    eng = get_engine()
    hits = _index_clips_by_character().get(char_key) or eng.clips_using_character(
        char_key
    )
    rows: List[Dict[str, Any]] = []
    for sn, cn in hits:
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

    wip_p = None
    if rebuild_wip and hits:
        scenes = sorted({sn for sn, _ in hits})
        wip_p = eng.remux_scenes_and_rebuild_wip(
            scenes, reason=f"after cascade regen {char_key}"
        )

    edit_log.add_entry(
        "character_cascade_regen",
        user_note=feedback or f"Cascade regen for {char_key}",
        character=char_key,
        action_taken=(
            f"Regenerated {len(hits)} clip(s): {hits[:20]}; "
            f"remux+WIP={wip_p or 'skipped'}"
        ),
        targets=["blueprint", "assets/video", "assets/scenes", "assets/movie_wip.mp4"],
        extra={
            "clips": hits,
            "only_existing": only_existing,
            "only_scene": only_scene,
            "wip_path": wip_p,
        },
    )
    return hits


def movie_title() -> str:
    return get_engine().blueprint.get("movie_title", "Untitled")


def wip_path() -> Optional[str]:
    p = get_engine().config.get("wip_movie_path", "assets/movie_wip.mp4")
    return p if file_is_usable(p, min_bytes=1024) else None


def list_stale_clips(only_existing: bool = True) -> List[Dict[str, Any]]:
    return get_engine().list_stale_clips(only_existing=only_existing)


def mark_character_changed(char_key: str, reason: str = "") -> List[Tuple[int, int]]:
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
    if mode == "missing":
        missing_nums = [cn for cn, ok in on_disk_map.items() if not ok]
        return estimate_scene_cost(
            scene, eng.config, only_clip_numbers=missing_nums
        )
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
        targets=["blueprint"],
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
        targets=["assets/video", "assets/scenes", "blueprint"],
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
        cost_ledger=eng.get_cost_ledger(),
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


def actual_cost_summary() -> Dict[str, Any]:
    eng = get_engine()
    summary = summarize_cost_ledger(eng.get_cost_ledger())
    totals = eng.state.get("cost_totals") if isinstance(eng.state, dict) else {}
    summary["state_totals"] = totals if isinstance(totals, dict) else {}
    return summary


def backfill_actual_costs() -> Dict[str, Any]:
    """Infer ledger entries for on-disk clips that predate tracking."""
    eng = get_engine()
    result = eng.backfill_cost_ledger_from_completed_jobs(only_missing=True)
    edit_log.add_entry(
        "cost_backfill",
        user_note=f"Backfilled {result.get('added')} cost ledger events",
        action_taken=str(result),
        learning_layer="engine",
        targets=["pipeline_state.json"],
    )
    return result


def recent_cost_events(limit: int = 50) -> List[Dict[str, Any]]:
    ledger = get_engine().get_cost_ledger()
    return list(reversed(ledger[-max(1, int(limit)) :]))


# ---------- Stage 1 adaptation (book → scenes.json) ----------

def stage1_paths() -> Dict[str, str]:
    """Paths for Stage 1 I/O under the active project / workspace."""
    proj = get_active_project_dir() or workspace_root()
    meta = load_project_meta(proj) if proj else {}
    scenes_name = meta.get("scenes_file") or "nickandme.scenes.json"
    # Prefer nickandme.scenes.json when present (legacy name)
    scenes = proj / scenes_name
    if not scenes.is_file():
        alt = proj / "nickandme.scenes.json"
        if alt.is_file() or scenes_name == "scenes.json":
            scenes = alt if (alt.is_file() or not scenes.is_file()) else scenes
    book = proj / "source" / "book_full.txt"
    source = proj / "source"
    pdfs = list(source.glob("*.pdf")) + list(source.glob("*.PDF"))
    pdf = None
    if pdfs:
        pdfs.sort(key=lambda p: (0 if "nick" in p.name.lower() else 1, -p.stat().st_size))
        pdf = pdfs[0]
    img_manifest = source / "book_images" / "manifest.json"
    extract_meta = source / "extract_meta.json"
    return {
        "project": str(proj),
        "scenes_json": str(scenes),
        "book_full": str(book),
        "book_exists": str(book.is_file()),
        "pdf": str(pdf) if pdf else "",
        "pdf_exists": str(pdf is not None and pdf.is_file()),
        "book_images_manifest": str(img_manifest) if img_manifest.is_file() else "",
        "extract_meta": str(extract_meta) if extract_meta.is_file() else "",
        "scenes_exists": str(scenes.is_file()),
        "prompt": str(workspace_root() / "prompts" / "stage1_scene_bible.txt"),
    }


def book_source_meta() -> Dict[str, Any]:
    """
    Stage 1 defaults + text quality after PDF import.

    Fast path: use source/extract_meta.json when it is as new as book_full.txt
    (avoids re-analyzing the full book on every page load).
    Re-score only when the book is newer than extract_meta.
    """
    paths = stage1_paths()
    out: Dict[str, Any] = {"present": False}
    meta_path = Path(paths.get("extract_meta") or "")
    book = Path(paths.get("book_full") or "")

    stored: Dict[str, Any] = {}
    if meta_path.is_file():
        try:
            stored = json.loads(meta_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            stored = {}

    if not book.is_file() and not stored:
        return out

    # Prefer cached extract_meta when book has not changed — but always re-score
    # if meta claims good while the text is still OCR soup (common after extract-only).
    try:
        book_newer = (
            book.is_file()
            and meta_path.is_file()
            and book.stat().st_mtime > meta_path.stat().st_mtime + 0.5
        )
    except OSError:
        book_newer = bool(book.is_file())

    # Always score current book_full.txt (source of truth for Stage 1 unlock).
    # extract_meta supplies strategy/history only — never override a clean vision transcript
    # with a stale "poor" from an older OCR pass (or the reverse).
    try:
        import importlib.util

        script = workspace_root() / "scripts" / "two_stage_adaptation" / "extract_book_source.py"
        if book.is_file() and script.is_file():
            spec = importlib.util.spec_from_file_location("extract_book_source", script)
            if spec is not None and spec.loader is not None:
                mod = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(mod)
                raw = book.read_text(encoding="utf-8", errors="ignore")
                pages = len(re.findall(r"--- PAGE \d+ ---", raw))
                live = mod.analyze_book_text(raw, pages_hint=pages or None)
                out.update(stored)
                out.update(live)
                out["present"] = True
                out["source"] = "live_book_full"
                out["analysis"] = live
                # Gate Stage 1 on live score only
                out["ready_for_stage1"] = bool(
                    live.get("text_quality") == "good"
                    and float(live.get("garbage_score") or 0) < 0.45
                )
                # Keep prepare strategy note if present
                if stored.get("strategy"):
                    out["strategy"] = stored["strategy"]
                if stored.get("text_engine"):
                    out["text_engine"] = stored.get("text_engine")
                # Prefer live engine tag when vision wrote the file more recently
                if book_newer and stored.get("vision", {}).get("ran"):
                    out["text_engine"] = stored.get("text_engine") or "grok_vision"
                return out
    except Exception as e:
        if stored:
            out.update(stored)
            out["present"] = True
            out["source"] = "extract_meta.json"
            out["live_analysis_error"] = str(e)
            return out
        out["error"] = str(e)
        return out

    if stored:
        out.update(stored)
        out["present"] = True
        out["source"] = "extract_meta.json"
    return out


def stage1_status() -> Dict[str, Any]:
    """Lightweight stats for Stage 1 bible on disk."""
    paths = stage1_paths()
    out: Dict[str, Any] = {"paths": paths}
    p = Path(paths["scenes_json"])
    if not p.is_file():
        out["present"] = False
        return out
    try:
        data = json.loads(p.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as e:
        out["present"] = True
        out["error"] = str(e)
        return out
    scenes = data.get("scenes") or []
    gpv = data.get("global_production_variables") or {}
    out.update(
        {
            "present": True,
            "schema_version": data.get("schema_version"),
            "movie_title": data.get("movie_title"),
            "scene_count": len(scenes),
            "beat_count": sum(len(s.get("story_beats") or []) for s in scenes),
            "characters": len(gpv.get("character_seed_tokens") or {}),
            "locations": len(gpv.get("location_seed_tokens") or {}),
            "runtime_sec": data.get("cumulative_duration_target_seconds"),
            "generation": data.get("generation"),
            "mtime": time.strftime(
                "%Y-%m-%d %H:%M:%S", time.localtime(p.stat().st_mtime)
            ),
        }
    )
    return out


def import_book_upload(
    *,
    filename: str,
    data: bytes,
    extract_pdf: bool = True,
    render_pages: str = "cover,sparse",
    force: bool = True,
    auto_prepare: bool = True,
    progress_cb=None,
) -> Dict[str, Any]:
    """
    Save an uploaded PDF or TXT into project source/ and prepare for Stage 1.

    - .pdf → written to source/<name>.pdf, then auto-prepare (extract + vision if needed)
    - .txt → written to source/book_full.txt (and copy as source/<name> if not book_full)
    """
    proj = get_active_project_dir()
    if proj is None:
        raise FileNotFoundError("No active project")
    if not data:
        raise ValueError("Empty file")
    name = Path(filename or "upload.bin").name
    ext = name.rsplit(".", 1)[-1].lower() if "." in name else ""
    source = proj / "source"
    source.mkdir(parents=True, exist_ok=True)

    result: Dict[str, Any] = {
        "project": proj.name,
        "original_name": name,
        "bytes": len(data),
    }

    if ext == "pdf":
        # Keep a stable primary name if it's the main book, else keep filename
        dest = source / name
        dest.write_bytes(data)
        result["saved_path"] = str(dest)
        result["kind"] = "pdf"
        if extract_pdf or auto_prepare:
            if auto_prepare:
                prep = prepare_book_source(
                    force_extract=force,
                    render_pages=render_pages,
                    auto_vision=True,
                    progress_cb=progress_cb,
                )
                result["prepare"] = prep
                result["extract"] = prep.get("extract") or {}
            else:
                summary = extract_book_from_pdf(
                    force=force,
                    render_pages=render_pages,
                    pdf_path=dest,
                    progress_cb=progress_cb,
                )
                result["extract"] = summary
        edit_log.add_entry(
            "book_upload",
            user_note=f"Uploaded PDF {name} ({len(data)} bytes)",
            action_taken=f"Saved {dest}; prepare={bool(result.get('prepare'))}",
            learning_layer="stage1",
            targets=["source/", "source/book_full.txt"],
            extra=result,
        )
        return result

    if ext in ("txt", "text", "md"):
        # Always install as book_full.txt for Stage 1; keep original copy too
        raw = data.decode("utf-8", errors="ignore")
        # Ensure page markers if plain dump without them
        if "--- PAGE " not in raw:
            # single blob — Stage 1 chunker will fall back to char chunks
            pass
        book_full = source / "book_full.txt"
        book_full.write_text(raw, encoding="utf-8")
        if name.lower() != "book_full.txt":
            (source / name).write_text(raw, encoding="utf-8")
        result["saved_path"] = str(book_full)
        result["kind"] = "txt"
        result["text_chars"] = len(raw)
        if auto_prepare:
            prep = prepare_book_source(
                force_extract=False,
                auto_vision=False,  # clean TXT — no vision
                progress_cb=progress_cb,
            )
            result["prepare"] = prep
        edit_log.add_entry(
            "book_upload",
            user_note=f"Uploaded text {name} ({len(data)} bytes)",
            action_taken=f"Saved {book_full}",
            learning_layer="stage1",
            targets=["source/book_full.txt"],
            extra=result,
        )
        return result

    raise ValueError(f"Unsupported file type .{ext or '?'} — use .pdf or .txt")


def extract_book_from_pdf(
    *,
    force: bool = True,
    render_pages: str = "cover,sparse",
    progress_cb=None,
    pdf_path: Optional[Path] = None,
) -> Dict[str, Any]:
    """Extract book_full.txt + book_images/ from project PDF."""
    import importlib.util

    ws = workspace_root()
    script = ws / "scripts" / "two_stage_adaptation" / "extract_book_source.py"
    if not script.is_file():
        raise FileNotFoundError(str(script))
    spec = importlib.util.spec_from_file_location("extract_book_source", script)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {script}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    proj = get_active_project_dir()
    if proj is None:
        raise FileNotFoundError("No active project")
    modes = [m.strip() for m in render_pages.split(",") if m.strip() and m.strip() != "none"]
    if progress_cb:
        progress_cb({"event": "start", "message": "Extracting PDF…", "chunk": 0, "total": 1})
    summary = mod.extract_book_source(
        project_id=proj.name,
        pdf_path=Path(pdf_path) if pdf_path else None,
        write_text=True,
        extract_images=True,
        render_modes=modes,
        force=force,
    )
    if progress_cb:
        progress_cb(
            {
                "event": "done",
                "message": (
                    f"pages={summary.get('pages')} chars={summary.get('text_chars')} "
                    f"images={summary.get('images')}"
                ),
                "chunk": 1,
                "total": 1,
            }
        )
    edit_log.add_entry(
        "book_extract",
        user_note=f"Extracted PDF {summary.get('pdf_name')}",
        action_taken=str(summary),
        learning_layer="stage1",
        targets=["source/book_full.txt", "source/book_images/"],
        extra=summary,
    )
    return summary


def prepare_book_source(
    *,
    force_extract: bool = True,
    force_vision: bool = False,
    render_pages: str = "cover,sparse",
    vision_model: str = "grok-4.5",
    auto_vision: bool = True,
    progress_cb=None,
) -> Dict[str, Any]:
    """
    Auto path: extract PDF → score text → Grok vision if garbled → Stage 1 defaults.

    Prefer this over raw extract when the user wants a one-click "make book ready".
    """
    import importlib.util

    ws = workspace_root()
    script = ws / "scripts" / "two_stage_adaptation" / "prepare_book_source.py"
    if not script.is_file():
        raise FileNotFoundError(str(script))
    # Load as package-adjacent module so sibling imports work
    prep_dir = str(script.parent)
    if prep_dir not in sys.path:
        sys.path.insert(0, prep_dir)
    spec = importlib.util.spec_from_file_location("prepare_book_source", script)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {script}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    proj = get_active_project_dir()
    if proj is None:
        raise FileNotFoundError("No active project")

    summary = mod.prepare_book_source(
        project_id=proj.name,
        force_extract=force_extract,
        force_vision=force_vision,
        render_pages=render_pages,
        vision_model=vision_model,
        auto_vision=auto_vision,
        progress_cb=progress_cb,
    )
    edit_log.add_entry(
        "book_prepare",
        user_note=summary.get("message") or summary.get("action") or "prepare_book_source",
        action_taken=(
            f"action={summary.get('action')} ready={summary.get('ready_for_stage1')} "
            f"runtime≈{summary.get('suggested_total_minutes')}min"
        ),
        learning_layer="stage1",
        targets=["source/book_full.txt", "source/extract_meta.json", "source/book_images/"],
        extra={
            k: summary.get(k)
            for k in (
                "action",
                "ready_for_stage1",
                "text_quality",
                "book_kind",
                "suggested_total_minutes",
                "suggested_chunk_pages",
                "needs_user",
            )
        },
    )
    return summary


def book_images_status() -> Dict[str, Any]:
    paths = stage1_paths()
    man = paths.get("book_images_manifest") or ""
    if not man or not Path(man).is_file():
        return {"present": False, "count": 0, "images": []}
    try:
        data = json.loads(Path(man).read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as e:
        return {"present": True, "error": str(e), "count": 0, "images": []}
    return {
        "present": True,
        "count": data.get("count", 0),
        "embedded_count": data.get("embedded_count", 0),
        "rendered_count": data.get("rendered_count", 0),
        "images": data.get("images") or [],
        "manifest": man,
        "notes": data.get("notes"),
    }


def run_stage1_from_book(
    *,
    chunk_pages: int = 10,
    total_minutes: Optional[int] = None,
    model: str = "grok-4.5",
    resume: bool = False,
    max_chunks: int = 0,
    extract_pdf_if_needed: bool = True,
    progress_cb=None,
) -> Dict[str, Any]:
    """
    Run prompts/stage1_scene_bible.txt on the project book (requires XAI_API_KEY).

    total_minutes: target film length in minutes. None = use book analysis estimate
    (picture book / short / novel heuristic), not a fixed 90.

    Uses existing clean book_full.txt when present. Does not re-extract PDF OCR
    over a good vision transcript — prepare only runs if text is missing/garbled.
    progress_cb receives dict events: start, chunk_start, chunk_done, normalize, verify, done.
    """
    import importlib.util

    ws = workspace_root()
    script = ws / "scripts" / "two_stage_adaptation" / "run_stage1_from_book.py"
    if not script.is_file():
        raise FileNotFoundError(str(script))
    spec = importlib.util.spec_from_file_location("run_stage1_from_book", script)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {script}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    proj = get_active_project_dir()
    if proj is None:
        raise FileNotFoundError("No active project")
    paths = stage1_paths()
    book = Path(paths["book_full"])
    pdf_ok = paths.get("pdf_exists") == "True"
    # Do NOT call extract_book_from_pdf here — that rewrites OCR over vision text.
    # run_stage1_job prepares only when book text is missing or still garbled.
    if not book.is_file() and not pdf_ok:
        raise FileNotFoundError(
            f"Book text missing: {book}. Place a PDF under source/ "
            "or write source/book_full.txt (Adaptation → Import book)."
        )

    summary = mod.run_stage1_job(
        project_id=proj.name,
        book_path=book if book.is_file() else None,
        out_path=Path(paths["scenes_json"]),
        model=model,
        chunk_pages=chunk_pages,
        total_minutes=total_minutes,
        resume=resume,
        max_chunks=max_chunks,
        normalize=True,
        progress_cb=progress_cb,
    )
    # Hand-off for Characters / Stage 0 / Stage 2:
    # 1) copy character seeds into generate blueprint
    # 2) attach book page images as design_reference_images (likeness for variants)
    try:
        eng = get_engine()
        force_sync = not (eng.blueprint.get("scenes") or [])
        sync = ensure_blueprint_character_seeds_from_stage1(force=force_sync)
        summary["blueprint_seed_sync"] = sync
        if sync.get("synced"):
            print(
                f"[Stage1] Synced {sync.get('count')} character seeds into Stage 2 blueprint",
                flush=True,
            )
        # Always try image attach after Stage 1 (even if seeds already present)
        imgs = attach_book_images_to_character_seeds(force=True)
        summary["character_book_images"] = imgs
        if imgs.get("ok"):
            print(
                f"[Stage1] Attached book images to {imgs.get('count')} character seed(s)",
                flush=True,
            )
        else:
            print(
                f"[Stage1] Book image attach: {imgs.get('reason') or imgs}",
                flush=True,
            )
    except Exception as e:
        summary["blueprint_seed_sync"] = {"synced": False, "error": str(e)}
        print(f"[Stage1] Blueprint seed / image hand-off skipped: {e}", flush=True)
    try:
        from review_app.pipeline_progress import invalidate_progress_cache

        invalidate_progress_cache()
    except Exception:
        pass
    edit_log.add_entry(
        "stage1_run",
        user_note=f"Stage 1 run complete: {summary.get('scenes')} scenes",
        action_taken=f"Wrote {summary.get('out_path')}",
        learning_layer="stage1",
        targets=["nickandme.scenes.json", "prompts/stage1_scene_bible.txt"],
        extra=summary,
    )
    return summary


# ---------- Stage 2 (scene bible → Grok clip plan) ----------

def stage2_status() -> Dict[str, Any]:
    """
    Whether Stage 1 exists and Stage 2 blueprint has clip scenes.

    Cached briefly in session_state (by file mtimes) so Scenes page clicks stay snappy.
    """
    paths = stage1_paths()
    proj = get_active_project_dir()
    meta = load_project_meta(proj) if proj else {}
    bp_name = meta.get("blueprint_file") or "blueprint.clips.grok.json"
    bp_path = (proj / bp_name) if proj else Path(bp_name)
    s1_path = Path(paths.get("scenes_json") or "")
    try:
        bp_mtime = bp_path.stat().st_mtime if bp_path.is_file() else 0.0
    except OSError:
        bp_mtime = 0.0
    try:
        s1_mtime = s1_path.stat().st_mtime if s1_path.is_file() else 0.0
    except OSError:
        s1_mtime = 0.0
    cache_key = f"stage2_status::{proj.name if proj else 'none'}::{bp_mtime:.0f}::{s1_mtime:.0f}"
    try:
        import streamlit as st

        cached = st.session_state.get("_stage2_status_cache")
        if (
            isinstance(cached, dict)
            and cached.get("_key") == cache_key
            and (time.time() - float(cached.get("_ts") or 0)) < 20.0
        ):
            return {k: v for k, v in cached.items() if not str(k).startswith("_")}
    except Exception:
        cached = None

    # Prefer blueprint first (common path once Stage 2 exists) — avoid loading Stage 1 JSON
    bp_scenes = 0
    bp_clips = 0
    last_completed_at = ""
    last_run_message = ""
    last_run_ok: Optional[bool] = None
    validation_issue_count = 0
    if bp_path.is_file():
        try:
            bp = json.loads(bp_path.read_text(encoding="utf-8"))
            scenes = bp.get("scenes") or []
            if isinstance(scenes, list):
                bp_scenes = len(scenes)
                for sc in scenes:
                    bp_clips += len((sc or {}).get("veo_clips") or [])
            meta_s2 = bp.get("stage2_meta") if isinstance(bp.get("stage2_meta"), dict) else {}
            last_completed_at = str(
                meta_s2.get("completed_at") or meta_s2.get("last_partial_at") or ""
            )
            last_run_message = str(meta_s2.get("last_run_message") or "")
            if "last_run_ok" in meta_s2:
                last_run_ok = bool(meta_s2.get("last_run_ok"))
            validation_issue_count = int(meta_s2.get("validation_issue_count") or 0)
            if not last_completed_at and bp_mtime:
                # Fallback: file mtime when older plans lack completed_at
                last_completed_at = time.strftime(
                    "%Y-%m-%dT%H:%M:%S", time.localtime(bp_mtime)
                )
        except (json.JSONDecodeError, OSError, TypeError, ValueError):
            pass

    s1_n = 0
    s1_exists = s1_path.is_file()
    # Parse Stage 1 only when Stage 2 empty (plan gate) or file is small enough to be cheap.
    # When Stage 2 is ready, skip the full Stage 1 JSON read — Scenes page does not need it.
    if s1_exists and bp_scenes == 0:
        try:
            s1 = json.loads(s1_path.read_text(encoding="utf-8"))
            s1_n = len((s1 or {}).get("scenes") or []) if isinstance(s1, dict) else 0
            s1_exists = s1_n > 0
        except (json.JSONDecodeError, OSError, TypeError):
            s1_exists = s1_path.is_file()
    elif s1_exists and bp_scenes > 0:
        # Approximate: Stage 1 existed if file present; avoid re-parsing large bible every click
        s1_n = bp_scenes

    result = {
        "stage1_exists": s1_exists,
        "stage1_scenes": s1_n,
        "blueprint_path": str(bp_path),
        "blueprint_exists": bp_path.is_file(),
        "stage2_scenes": bp_scenes,
        "stage2_clips": bp_clips,
        "stage2_ready": bp_scenes > 0 and bp_clips > 0,
        "scenes_json": paths.get("scenes_json") or "",
        "last_completed_at": last_completed_at,
        "last_run_message": last_run_message
        or (
            f"{bp_scenes} scenes · {bp_clips} clips"
            if bp_scenes
            else ""
        ),
        "last_run_ok": last_run_ok if last_run_ok is not None else (bp_scenes > 0 and bp_clips > 0),
        "validation_issue_count": validation_issue_count,
        "blueprint_mtime": bp_mtime,
    }
    try:
        import streamlit as st

        st.session_state["_stage2_status_cache"] = {
            **result,
            "_key": cache_key,
            "_ts": time.time(),
        }
    except Exception:
        pass
    return result


def run_stage2_from_stage1(
    *,
    scenes: str = "all",
    resolution: str = "720p",
    preserve_existing_seeds: bool = True,
) -> Dict[str, Any]:
    """
    Convert Stage 1 scene bible → Stage 2 Grok clip plan (deterministic planner).

    Writes the active project's blueprint_file (e.g. blueprint.clips.grok.json).
    Preserves richer character_seed_tokens already on the blueprint when present.
    No API key required for the default planner.
    """
    import importlib.util
    import shutil
    from datetime import datetime

    ws = workspace_root()
    script = ws / "scripts" / "two_stage_adaptation" / "stage2_plan_grok.py"
    if not script.is_file():
        raise FileNotFoundError(str(script))
    spec = importlib.util.spec_from_file_location("stage2_plan_grok", script)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load {script}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    proj = get_active_project_dir()
    if proj is None:
        raise FileNotFoundError("No active project")
    paths = stage1_paths()
    stage1_path = Path(paths["scenes_json"])
    if not stage1_path.is_file():
        # Prefer project scenes.json when stage1_paths fell through to missing alt
        alt = proj / "scenes.json"
        if alt.is_file():
            stage1_path = alt
        else:
            raise FileNotFoundError(
                f"Stage 1 missing: {paths['scenes_json']}. "
                "Run Adaptation → Stage 1 first."
            )

    stage1 = json.loads(stage1_path.read_text(encoding="utf-8"))
    scenes_in = list(stage1.get("scenes") or [])
    if not scenes_in:
        raise ValueError("Stage 1 has zero scenes — re-run Stage 1 before Stage 2.")

    want = mod.parse_scene_range(scenes)
    if want is not None:
        scenes_in = [s for s in scenes_in if int(s.get("scene_number") or 0) in set(want)]
        if not scenes_in:
            raise ValueError(f"No Stage 1 scenes match filter {scenes!r}")

    gpv = dict(stage1.get("global_production_variables") or {})
    loc_seeds = gpv.get("location_seed_tokens") or {}
    char_seeds = gpv.get("character_seed_tokens") or {}
    if isinstance(char_seeds, dict):
        for _ck, _sv in char_seeds.items():
            if not isinstance(_sv, dict):
                continue
            ph = str(_sv.get("reference_image_placeholder") or "").replace("\\", "/")
            if ph and ("/" in ph or ph.startswith("assets")):
                _sv["reference_image_placeholder"] = Path(ph).name
        gpv["character_seed_tokens"] = char_seeds
    res = (resolution or "720p").strip() or "720p"
    planned_scenes = [
        mod.plan_scene(
            s,
            resolution=res,
            location_seeds=loc_seeds,
            character_seeds=char_seeds if isinstance(char_seeds, dict) else {},
        )
        for s in scenes_in
    ]

    meta = load_project_meta(proj)
    bp_name = meta.get("blueprint_file") or "blueprint.clips.grok.json"
    # Prefer config blueprint_file if set
    try:
        cfg = json.loads((proj / (meta.get("config_file") or "pipeline_config.json")).read_text(encoding="utf-8"))
        if isinstance(cfg, dict) and cfg.get("blueprint_file"):
            bp_name = str(cfg["blueprint_file"])
    except (json.JSONDecodeError, OSError, TypeError):
        pass
    out_path = proj / bp_name

    existing_bp: Dict[str, Any] = {}
    if out_path.is_file():
        try:
            existing_bp = json.loads(out_path.read_text(encoding="utf-8"))
            if not isinstance(existing_bp, dict):
                existing_bp = {}
        except (json.JSONDecodeError, OSError):
            existing_bp = {}

    # Full rewrite when filter is all; merge selected scenes into existing blueprint
    if want is None:
        plan: Dict[str, Any] = {
            "schema_version": "stage2.v1",
            "movie_title": stage1.get("movie_title") or existing_bp.get("movie_title"),
            "source_book_title": stage1.get("source_book_title")
            or existing_bp.get("source_book_title"),
            "video_provider_profile": "grok",
            "global_production_variables": gpv,
            "scenes": planned_scenes,
            "stage2_meta": {
                "source_stage1": stage1_path.name,
                "resolution": res,
                "scene_filter": scenes,
                "clip_duration_policy": mod._duration_policy_from_config(),
                **(
                    lambda lim: {
                        "prompt_soft_max": lim.get("soft"),
                        "prompt_hard_max": lim.get("hard"),
                        "prompt_limits_source": lim.get("source"),
                        "prompt_limits_provider": lim.get("provider"),
                        "prompt_limits_model": lim.get("model"),
                    }
                )(
                    mod._prompt_limits_from_config()
                    if hasattr(mod, "_prompt_limits_from_config")
                    else {
                        "soft": getattr(mod, "GROK_PROMPT_SOFT", 500),
                        "hard": getattr(mod, "GROK_PROMPT_HARD", 800),
                        "source": "fallback",
                    }
                ),
            },
        }
        if preserve_existing_seeds:
            old_seeds = (existing_bp.get("global_production_variables") or {}).get(
                "character_seed_tokens"
            ) or {}
            new_seeds = dict(gpv.get("character_seed_tokens") or {})
            if isinstance(old_seeds, dict) and old_seeds:
                merged_seeds = dict(new_seeds)
                for k, v in old_seeds.items():
                    if not isinstance(v, dict):
                        continue
                    cur = merged_seeds.get(k) if isinstance(merged_seeds.get(k), dict) else {}
                    # Prefer existing if it has design/book refs already attached
                    if (v.get("design_reference_images") or v.get("book_reference_images")) or (
                        not (cur.get("design_reference_images") or cur.get("book_reference_images"))
                    ):
                        merged = dict(cur)
                        merged.update(v)
                        # keep any newer keys from stage1 description if old empty
                        for field in (
                            "description",
                            "voice_profile",
                            "voice_label",
                            "reference_image_placeholder",
                        ):
                            if not merged.get(field) and cur.get(field):
                                merged[field] = cur[field]
                        merged_seeds[k] = merged
                plan["global_production_variables"] = dict(gpv)
                plan["global_production_variables"]["character_seed_tokens"] = merged_seeds
    else:
        if not existing_bp:
            raise FileNotFoundError(
                f"Partial Stage 2 (--scenes {scenes}) needs an existing blueprint at {out_path}"
            )
        plan = mod.merge_into_blueprint(existing_bp, planned_scenes)
        # If blueprint had zero scenes, merge_into only updates existing — append missing
        existing_nums = {
            int(s.get("scene_number") or 0) for s in (plan.get("scenes") or [])
        }
        extra = [
            s
            for s in planned_scenes
            if int(s.get("scene_number") or 0) not in existing_nums
        ]
        if extra:
            plan.setdefault("scenes", []).extend(extra)
            plan["scenes"] = sorted(
                plan["scenes"], key=lambda s: int(s.get("scene_number") or 0)
            )
        plan["schema_version"] = plan.get("schema_version") or "stage2.v1"
        plan["video_provider_profile"] = plan.get("video_provider_profile") or "grok"
        meta_s2 = dict(plan.get("stage2_meta") or {})
        meta_s2.update(
            {
                "source_stage1": stage1_path.name,
                "resolution": res,
                "scene_filter": scenes,
                "last_partial_at": datetime.now().isoformat(timespec="seconds"),
            }
        )
        plan["stage2_meta"] = meta_s2

    total_dur = sum(
        int(s.get("total_estimated_duration_seconds") or 0)
        for s in (plan.get("scenes") or [])
    )
    total_clips = sum(len(s.get("veo_clips") or []) for s in (plan.get("scenes") or []))
    n_scenes = len(plan.get("scenes") or [])
    plan.setdefault("stage2_meta", {})
    plan["stage2_meta"]["total_duration_seconds"] = total_dur
    plan["stage2_meta"]["total_clips"] = total_clips

    errs = mod.validate_plan(plan)
    # Plan is "successful" if it wrote scenes/clips; validation issues are advisory
    wrote_ok = n_scenes > 0 and total_clips > 0
    completed_at = datetime.now().isoformat(timespec="seconds")
    run_msg = (
        f"Stage 2 complete: {n_scenes} scenes · {total_clips} clips · ~{total_dur}s"
        if wrote_ok
        else "Stage 2 finished but produced no clips"
    )
    plan["stage2_meta"]["completed_at"] = completed_at
    plan["stage2_meta"]["last_run_ok"] = wrote_ok
    plan["stage2_meta"]["last_run_message"] = run_msg
    plan["stage2_meta"]["validation_issue_count"] = len(errs)
    plan["stage2_meta"]["source_stage1"] = stage1_path.name
    plan["stage2_meta"]["resolution"] = res

    # Backup previous blueprint
    if out_path.is_file():
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        bak = out_path.with_suffix(out_path.suffix + f".bak_pre_stage2_{stamp}")
        try:
            shutil.copy2(out_path, bak)
        except OSError:
            bak = None
    else:
        bak = None

    out_path.write_text(
        json.dumps(plan, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )

    # Reload engine so Scenes page sees new clips
    try:
        reload_engine()
    except Exception:
        pass
    try:
        from review_app.pipeline_progress import invalidate_progress_cache

        invalidate_progress_cache()
    except Exception:
        pass
    try:
        import streamlit as st

        st.session_state.pop("_stage2_status_cache", None)
    except Exception:
        pass

    if wrote_ok:
        try:
            mark_pipeline_step(
                "stage2",
                detail=f"{n_scenes} scenes · {total_clips} clips",
            )
        except Exception:
            pass

    summary: Dict[str, Any] = {
        "ok": wrote_ok,
        "scenes": n_scenes,
        "clips": total_clips,
        "duration_sec": total_dur,
        "out_path": str(out_path),
        "source_stage1": str(stage1_path),
        "backup": str(bak) if bak else "",
        "validation_issues": errs[:40],
        "validation_issue_count": len(errs),
        "scene_filter": scenes,
        "resolution": res,
        "completed_at": completed_at,
        "message": run_msg,
    }
    edit_log.add_entry(
        "stage2_run",
        user_note=run_msg,
        action_taken=f"Wrote {out_path.name}"
        + (f" ({len(errs)} validation notes)" if errs else " (validate OK)"),
        learning_layer="stage2",
        targets=[out_path.name, "scripts/two_stage_adaptation/stage2_plan_grok.py"],
        extra={k: v for k, v in summary.items() if k != "validation_issues"},
    )
    return summary
