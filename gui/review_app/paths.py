"""
Workspace vs project roots.

Workspace (repo): gui/, cli/, renderer/, projects/, prompts/, docs/, scripts/
Project: self-contained film folder with blueprint, config, state, assets/
"""
from __future__ import annotations

import json
import os
import re
from pathlib import Path
from typing import Any, Dict, List, Optional

PROJECTS_DIRNAME = "projects"
WORKSPACE_CONFIG = "workspace.json"  # lives at projects/workspace.json
PROJECT_META = "project.json"


def workspace_root() -> Path:
    """Film tooling repo root (has renderer/, gui/, projects/)."""
    here = Path(__file__).resolve().parent  # .../gui/review_app
    for candidate in (here.parent.parent, here.parent, *here.parents):
        if (candidate / "renderer").is_dir() and (candidate / "projects").is_dir():
            return candidate
        if (candidate / "gui").is_dir() and (candidate / "renderer").is_dir():
            return candidate
        # legacy markers during transition
        if (candidate / "generation_script.py").is_file():
            return candidate
    return here.parent.parent


def projects_root() -> Path:
    return workspace_root() / PROJECTS_DIRNAME


def workspace_config_path() -> Path:
    """Active-project pointer: projects/workspace.json (not under gui/)."""
    return projects_root() / WORKSPACE_CONFIG


def load_workspace_config() -> Dict[str, Any]:
    path = workspace_config_path()
    if path.is_file():
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(data, dict):
                data.setdefault("active_project", None)
                return data
        except (json.JSONDecodeError, OSError):
            pass
    return {"active_project": None}


def save_workspace_config(cfg: Dict[str, Any]) -> None:
    path = workspace_config_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    out = {"active_project": cfg.get("active_project")}
    path.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")


def _slugify(name: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9._-]+", "_", (name or "").strip())
    s = s.strip("._-") or "project"
    return s[:80]


def project_label(meta_or_id: Any, *, projects: Optional[List[Dict[str, Any]]] = None) -> str:
    """
    Human label for sidebar / selectboxes.

    - Prefer title when it differs from folder id
    - Avoid "Foo (Foo)" when title == id
    - CamelCase folder ids get light spacing for display when title missing
    """
    if isinstance(meta_or_id, dict):
        meta = meta_or_id
        pid = str(meta.get("id") or "")
        title = (meta.get("title") or "").strip()
    else:
        pid = str(meta_or_id or "")
        title = ""
        if projects:
            for p in projects:
                if p.get("id") == pid:
                    title = (p.get("title") or "").strip()
                    break
    if not pid:
        return title or "(no project)"
    if title and title.lower() != pid.lower() and title.replace(" ", "").lower() != pid.lower():
        return title
    if title and title != pid:
        return title
    # Title is same as id (or empty) — prettify CamelCase / snake_case for display only
    pretty = re.sub(r"([a-z])([A-Z])", r"\1 \2", pid)
    pretty = pretty.replace("_", " ").replace("-", " ")
    pretty = re.sub(r"\s+", " ", pretty).strip()
    return pretty or pid


def list_projects() -> List[Dict[str, Any]]:
    """Return project metadata dicts sorted by title/id."""
    root = projects_root()
    if not root.is_dir():
        return []
    out: List[Dict[str, Any]] = []
    for child in sorted(root.iterdir()):
        if not child.is_dir() or child.name.startswith("."):
            continue
        meta = load_project_meta(child)
        meta["path"] = str(child.resolve())
        meta["id"] = meta.get("id") or child.name
        meta["label"] = project_label(meta)
        out.append(meta)
    out.sort(key=lambda m: (m.get("title") or m.get("id") or "").lower())
    return out


def load_project_meta(project_dir: Path) -> Dict[str, Any]:
    meta_path = project_dir / PROJECT_META
    if meta_path.is_file():
        try:
            data = json.loads(meta_path.read_text(encoding="utf-8"))
            if isinstance(data, dict):
                data.setdefault("id", project_dir.name)
                return data
        except (json.JSONDecodeError, OSError):
            pass
    return {
        "id": project_dir.name,
        "title": project_dir.name,
        "blueprint_file": "blueprint.clips.grok.json",
        "scenes_file": "scenes.json",
        "config_file": "pipeline_config.json",
        "state_file": "pipeline_state.json",
    }


def save_project_meta(project_dir: Path, meta: Dict[str, Any]) -> None:
    project_dir.mkdir(parents=True, exist_ok=True)
    meta = dict(meta)
    meta.setdefault("id", project_dir.name)
    (project_dir / PROJECT_META).write_text(
        json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )


def default_project_meta(project_id: str, title: Optional[str] = None) -> Dict[str, Any]:
    return {
        "id": project_id,
        "title": title or project_id,
        "blueprint_file": "blueprint.clips.grok.json",
        "scenes_file": "scenes.json",
        "config_file": "pipeline_config.json",
        "state_file": "pipeline_state.json",
        "description": "",
    }


def resolve_project_dir(project_id: Optional[str] = None) -> Optional[Path]:
    """Resolve project folder by id or active workspace selection."""
    cfg = load_workspace_config()
    pid = project_id or cfg.get("active_project")
    if not pid:
        return None
    path = projects_root() / str(pid)
    if path.is_dir():
        return path.resolve()
    return None


def get_active_project_dir() -> Optional[Path]:
    # Env override for CLI
    env = os.environ.get("NICKANDME_PROJECT") or os.environ.get("FILM_PROJECT_DIR")
    if env:
        p = Path(env).expanduser()
        if not p.is_absolute():
            p = workspace_root() / p
        if p.is_dir():
            return p.resolve()
    return resolve_project_dir(None)


def set_active_project(project_id: str) -> Path:
    root = projects_root() / project_id
    if not root.is_dir():
        raise FileNotFoundError(f"Project not found: {project_id}")
    save_workspace_config({"active_project": project_id})
    return root.resolve()


def create_project(
    name: str,
    *,
    title: Optional[str] = None,
) -> Path:
    """
    Create projects/<slug>/ with empty assets tree and default config.
    """
    slug = _slugify(name)
    dest = projects_root() / slug
    if dest.exists():
        raise FileExistsError(f"Project already exists: {slug}")
    dest.mkdir(parents=True)

    meta = default_project_meta(slug, title=title or name)
    save_project_meta(dest, meta)

    # Minimal config (aligned with renderer.DEFAULT_CONFIG keys)
    config = {
        "video_provider": "grok",
        "character_design_provider": "grok",
        "qa_provider": "grok",
        "image_model_name": "grok-imagine-image-quality",
        "qa_model_name": "grok-4.5",
        "model_name": "grok-imagine-video",
        "use_video_audio_for_music": True,
        "regenerate_silent_clips": True,
        "merge_scene_after_each_clip": True,
        "smart_continuation": True,
        "qa_retry_on_fail": True,
        "qa_max_retries": 2,
        "rebuild_wip_movie_after_scene": True,
        "wip_movie_path": "assets/movie_wip.mp4",
        "aspect_ratio": "16:9",
        "duration_seconds": 8,
        "use_duration_defaults": True,
        "duration_defaults": {
            "fallback": 8,
            "providers": {
                "grok": {
                    "default": 8,
                    "min": 1,
                    "max": 15,
                    "prefer_min": 6,
                    "prefer_max": 10,
                    "models": {
                        "grok-imagine-video": {
                            "default": 8,
                            "prefer_min": 6,
                            "prefer_max": 10,
                        },
                        "grok-imagine-video-1.5": {
                            "default": 8,
                            "prefer_min": 6,
                            "prefer_max": 10,
                        },
                    },
                    "resolutions": {
                        "480p": {"default": 6},
                        "720p": {"default": 8},
                        "1080p": {"default": 8},
                    },
                },
                "veo": {
                    "default": 8,
                    "min": 4,
                    "max": 8,
                    "prefer_min": 7,
                    "prefer_max": 8,
                },
            },
        },
        "resolution": "720p",
        "qa_frame_count": 4,
        "blueprint_file": meta["blueprint_file"],
    }
    (dest / meta["config_file"]).write_text(
        json.dumps(config, indent=2) + "\n", encoding="utf-8"
    )
    state = {
        "characters_designed": False,
        "current_scene_index": 0,
        "scenes_completed": {},
        "scene_assets": {},
        "clip_context_ids": {},
        "clip_jobs": {},
        "music_jobs": {},
        "character_revisions": {},
        "stale_clips": {},
        "scene_variants": {},
        "scene_hero": {},
    }
    (dest / meta["state_file"]).write_text(
        json.dumps(state, indent=2) + "\n", encoding="utf-8"
    )

    empty_stage2 = {
        "schema_version": "stage2.v1",
        "movie_title": title or name,
        "source_book_title": title or name,
        "video_provider_profile": "grok",
        "global_production_variables": {
            "target_aspect_ratio": "16:9",
            "resolution": "720p",
            "frame_rate": 24,
            "directorial_treatment": "cinematic",
            "total_runtime_target_seconds": 0,
            "character_seed_tokens": {},
        },
        "scenes": [],
    }
    (dest / meta["blueprint_file"]).write_text(
        json.dumps(empty_stage2, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    empty_stage1 = {
        "schema_version": "stage1.v1",
        "movie_title": title or name,
        "source_book_title": title or name,
        "global_production_variables": empty_stage2["global_production_variables"],
        "scenes": [],
    }
    (dest / meta["scenes_file"]).write_text(
        json.dumps(empty_stage1, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )

    for d in (
        "assets",
        "assets/characters",
        "assets/video",
        "assets/scenes",
        "assets/music",
        "assets/variants",
        "review_feedback",
        "source",
    ):
        (dest / d).mkdir(parents=True, exist_ok=True)
    (dest / "edit_feedback_log.json").write_text(
        json.dumps({"version": 1, "entries": []}, indent=2) + "\n", encoding="utf-8"
    )
    (dest / "review_feedback" / "LEARNINGS.md").write_text(
        "# Review learnings\n\n", encoding="utf-8"
    )
    (dest / "review_feedback" / "SCRIPT_NOTES.md").write_text(
        "# Script notes\n\n", encoding="utf-8"
    )

    set_active_project(slug)
    return dest.resolve()


def repo_root() -> Path:
    """
    Active working directory for pipeline I/O.
    Prefer active project; fall back to workspace root.
    """
    proj = get_active_project_dir()
    if proj is not None:
        return proj
    return workspace_root()
