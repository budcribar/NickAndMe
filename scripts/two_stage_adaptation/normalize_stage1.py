#!/usr/bin/env python3
"""Normalize Stage 1 scenes.json to match stage1_scene_bible.schema.json after LLM generation."""
from __future__ import annotations

import argparse
import json
import re
import shutil
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

ROOT = Path(__file__).resolve().parents[2]
SCHEMA_PATH = ROOT / "prompts" / "stage1_scene_bible.schema.json"

LOC_TYPE_MAP = {
    "interior": "int",
    "int": "int",
    "interior_only": "int",
    "exterior": "ext",
    "ext": "ext",
    "exterior_only": "ext",
    "mixed": "mixed",
    "interior_exterior_mix": "mixed",
    "interior/exterior": "mixed",
    "int/ext": "mixed",
    "montage_mix": "montage",
    "montage": "montage",
    "flashback": "flashback",
    "dream": "dream",
    "dreamscape": "dream",
}


def _project_scenes(project: Optional[str]) -> Path:
    pid = project or "NickAndMe"
    if not project:
        ws = ROOT / "projects" / "workspace.json"
        if ws.is_file():
            try:
                pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
            except (json.JSONDecodeError, OSError):
                pass
    return ROOT / "projects" / str(pid) / "nickandme.scenes.json"


def _norm_location_type(v: Any) -> str:
    s = str(v or "mixed").strip().lower().replace(" ", "_")
    if s in LOC_TYPE_MAP:
        return LOC_TYPE_MAP[s]
    if "flash" in s:
        return "flashback"
    if "dream" in s:
        return "dream"
    if "montage" in s:
        return "montage"
    if "ext" in s and "int" in s:
        return "mixed"
    if s.startswith("ext"):
        return "ext"
    if s.startswith("int"):
        return "int"
    return "mixed"


def _norm_story_day(v: Any) -> str:
    if v is None or v == "":
        return "unspecified"
    if isinstance(v, (int, float)):
        n = int(v)
        if n <= 0:
            return "Flashback / unspecified day"
        return f"Day {n}"
    return str(v)


def _norm_excerpts(raw: Any) -> List[Dict[str, str]]:
    out: List[Dict[str, str]] = []
    if not isinstance(raw, list):
        return out
    for item in raw:
        if isinstance(item, str):
            if item.strip():
                out.append({"source": "book", "excerpt": item.strip()[:3500]})
            continue
        if not isinstance(item, dict):
            continue
        excerpt = item.get("excerpt") or item.get("text") or item.get("quote") or ""
        source = item.get("source") or item.get("file") or item.get("ref") or "book"
        if not excerpt and item.get("page"):
            excerpt = str(item.get("content") or item.get("passage") or "")
        if excerpt:
            out.append({"source": str(source), "excerpt": str(excerpt)[:3500]})
    return out


def _norm_music(raw: Any) -> Dict[str, Any]:
    if not isinstance(raw, dict):
        return {"style_description": "cinematic underscore"}
    out = dict(raw)
    if not out.get("style_description"):
        out["style_description"] = (
            out.get("style")
            or out.get("description")
            or out.get("genre")
            or "cinematic underscore"
        )
    return out


def _clean_nulls(obj: Any) -> Any:
    if isinstance(obj, dict):
        return {k: _clean_nulls(v) for k, v in obj.items() if v is not None}
    if isinstance(obj, list):
        return [_clean_nulls(x) for x in obj]
    return obj


def normalize(data: Dict[str, Any]) -> Dict[str, Any]:
    data = _clean_nulls(data)
    data["schema_version"] = "stage1.v1"
    data.setdefault("movie_title", data.get("source_book_title") or "Untitled")
    data.setdefault("source_book_title", data.get("movie_title") or "Untitled")
    # Strip accidental legacy default when another title is present elsewhere
    if data.get("movie_title") == "Nick and Me" and data.get("source_book_title") not in (
        None,
        "",
        "Nick and Me",
    ):
        data["movie_title"] = data["source_book_title"]

    gpv = data.setdefault("global_production_variables", {})
    gpv.setdefault("target_aspect_ratio", "16:9")
    gpv.setdefault("resolution", "720p")
    fr = gpv.get("frame_rate", 24)
    if isinstance(fr, str):
        m = re.search(r"\d+", fr)
        gpv["frame_rate"] = int(m.group(0)) if m else 24
    else:
        gpv["frame_rate"] = int(fr or 24)
    gpv.setdefault(
        "directorial_treatment",
        "cinematic lighting, film grain, steady camera, high-contrast",
    )
    gpv.setdefault("total_runtime_target_seconds", 5400)
    gpv.setdefault("character_seed_tokens", {})
    gpv.setdefault("location_seed_tokens", {})

    # Clean character seeds: drop null optional fields already cleaned; ensure required
    for key, seed in list((gpv.get("character_seed_tokens") or {}).items()):
        if not isinstance(seed, dict):
            continue
        seed.setdefault("description", key)
        seed.setdefault("reference_image_placeholder", f"{key.lower()}_ref.png")
        seed.setdefault("voice_profile", "Consistent character voice every scene.")
        seed.setdefault("voice_label", key)
        # remove empty optional nulls already gone; ensure strings not None
        for opt in (
            "canonical_given_name",
            "name_reveal_note",
            "age_band",
            "variant_of",
        ):
            if opt in seed and seed[opt] is None:
                del seed[opt]
        if seed.get("name_reveal_scene") is None and "name_reveal_scene" in seed:
            del seed["name_reveal_scene"]
        # Keep book likeness fields when present
        pages = seed.get("source_image_pages")
        if pages is not None and not isinstance(pages, list):
            try:
                seed["source_image_pages"] = [int(pages)]
            except (TypeError, ValueError):
                seed.pop("source_image_pages", None)
        for img_key in ("design_reference_images", "book_reference_images"):
            if img_key in seed and seed[img_key] is None:
                del seed[img_key]

    for key, seed in list((gpv.get("location_seed_tokens") or {}).items()):
        if not isinstance(seed, dict):
            continue
        seed.setdefault("display_name", key)
        seed.setdefault("description", seed.get("display_name") or key)
        seed.setdefault("visual_lock", seed.get("description") or key)

    scenes = data.get("scenes") or []
    for s in scenes:
        if not isinstance(s, dict):
            continue
        sn = s.get("scene_number")
        s.setdefault("scene_filename", f"Scene_{int(sn or 0):02d}")
        s.setdefault("setting", "")
        s["story_day"] = _norm_story_day(s.get("story_day"))
        s["location_type"] = _norm_location_type(s.get("location_type"))
        s.setdefault("duration_target_seconds", 24)
        try:
            s["duration_target_seconds"] = max(8, min(134, int(s["duration_target_seconds"])))
        except (TypeError, ValueError):
            s["duration_target_seconds"] = 24
        s.setdefault("dramatic_function", "")
        s.setdefault("summary", s.get("setting") or f"Scene {sn}")
        s.setdefault("transition_type", "cut")
        s.setdefault("lighting_continuity_token", "consistent scene lighting")
        s.setdefault("story_beats", [])
        s["music_intent"] = _norm_music(s.get("music_intent"))
        if "source_excerpts" in s:
            s["source_excerpts"] = _norm_excerpts(s.get("source_excerpts"))
            if not s["source_excerpts"]:
                del s["source_excerpts"]
        lids = s.get("location_ids") or []
        if isinstance(lids, str):
            lids = [lids]
        s["location_ids"] = [str(x) for x in lids if x]
        if s["location_ids"] and not s.get("primary_location_id"):
            s["primary_location_id"] = s["location_ids"][0]
        # beats
        for b in s.get("story_beats") or []:
            if not isinstance(b, dict):
                continue
            b.setdefault("beat_id", "b1")
            b.setdefault("intent", "")
            b.setdefault("visual_event", b.get("intent") or "")
            b.setdefault("shot_scale_hint", "ms")
            b.setdefault("continuity", "new_setup")
            # strip stage2 leaks
            for leak in ("visual_prompt", "negative_prompt", "timestamp", "veo_continuation_source"):
                b.pop(leak, None)

    total = sum(int(s.get("duration_target_seconds") or 0) for s in scenes if isinstance(s, dict))
    data["cumulative_duration_target_seconds"] = total
    data["scenes"] = scenes
    return data


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", default=None)
    ap.add_argument("--path", default=None)
    args = ap.parse_args()
    path = Path(args.path) if args.path else _project_scenes(args.project)
    data = json.loads(path.read_text(encoding="utf-8"))
    fixed = normalize(data)

    bak = path.with_suffix(path.suffix + f".bak_norm_{time.strftime('%Y%m%d_%H%M%S')}")
    shutil.copy2(path, bak)
    path.write_text(json.dumps(fixed, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {path} (backup {bak.name})")
    print(
        f"scenes={len(fixed.get('scenes') or [])} "
        f"runtime={fixed.get('cumulative_duration_target_seconds')}s "
        f"chars={len((fixed.get('global_production_variables') or {}).get('character_seed_tokens') or {})} "
        f"locs={len((fixed.get('global_production_variables') or {}).get('location_seed_tokens') or {})}"
    )

    try:
        from jsonschema import Draft202012Validator

        schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        errs = list(Draft202012Validator(schema).iter_errors(fixed))
        print(f"schema errors: {len(errs)}")
        for e in errs[:25]:
            p = "/".join(str(x) for x in e.path) or "(root)"
            print(f"  {p}: {e.message[:120]}")
        return 1 if errs else 0
    except ImportError:
        print("jsonschema not installed")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
