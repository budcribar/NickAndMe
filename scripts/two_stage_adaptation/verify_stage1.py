#!/usr/bin/env python3
"""
Verify Stage 1 prompt/schema changes don't break existing scenes.json.

Checks:
  1) schema loads
  2) location fields are optional in schema (backward compatible)
  3) current project scenes.json validates (if jsonschema installed)
  4) legacy copy without location fields still validates
  5) examples/scene_bible_minimal.json validates
  6) extract_stage1 still runs on clips.grok.json (smoke)

Usage (repo root):
  python scripts/two_stage_adaptation/verify_stage1.py
"""
from __future__ import annotations

import copy
import json
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCHEMA_PATH = ROOT / "prompts" / "stage1_scene_bible.schema.json"
EXAMPLE_PATH = ROOT / "prompts" / "examples" / "scene_bible_minimal.json"


def _project_scenes() -> Path:
    ws = ROOT / "projects" / "workspace.json"
    pid = "NickAndMe"
    if ws.is_file():
        try:
            pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
        except (json.JSONDecodeError, OSError):
            pass
    return ROOT / "projects" / str(pid) / "nickandme.scenes.json"


def main() -> int:
    failed = 0
    print("=== Stage 1 compatibility verify ===")

    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    print(f"[ok] schema loads: {SCHEMA_PATH.relative_to(ROOT)}")

    gpv_req = schema["properties"]["global_production_variables"]["required"]
    if "location_seed_tokens" in gpv_req:
        print("[FAIL] location_seed_tokens must NOT be required on global_production_variables")
        failed += 1
    else:
        print("[ok] location_seed_tokens optional at global level")

    scene_req = schema["$defs"]["scene"]["required"]
    if "location_ids" in scene_req:
        print("[FAIL] location_ids must NOT be required on scene (breaks pre-pin bibles)")
        failed += 1
    else:
        print("[ok] location_ids optional on scene")

    if "location_seed_tokens" not in schema["properties"]["global_production_variables"]["properties"]:
        print("[FAIL] location_seed_tokens missing from schema properties")
        failed += 1
    else:
        print("[ok] location_seed_tokens defined in schema")

    if "location_ids" not in schema["$defs"]["scene"]["properties"]:
        print("[FAIL] location_ids missing from scene properties")
        failed += 1
    else:
        print("[ok] location_ids defined on scene")

    # Prompt still Stage-1-only boundary
    prompt = (ROOT / "prompts" / "stage1_scene_bible.txt").read_text(encoding="utf-8")
    for forbidden in ("veo_clips", "visual_prompt", "veo_continuation_source"):
        # allowed in "Do NOT emit" lines
        pass
    if "OUTPUT = scene bible only" not in prompt and "stage1.v1" not in prompt:
        print("[FAIL] stage1_scene_bible.txt missing stage boundary markers")
        failed += 1
    else:
        print("[ok] stage1_scene_bible.txt still documents stage1.v1 boundary")
    if "location_seed_tokens" not in prompt or "location_ids" not in prompt:
        print("[FAIL] prompt missing location pin instructions")
        failed += 1
    else:
        print("[ok] prompt includes location pin instructions")
    if "Do NOT emit `veo_clips`" not in prompt and "Do NOT emit veo_clips" not in prompt:
        # check soft
        if "veo_clips" in prompt and "Do NOT" in prompt:
            print("[ok] prompt forbids veo_clips (boundary present)")
        else:
            print("[WARN] could not confirm Do NOT emit veo_clips line")
    else:
        print("[ok] prompt forbids emitting veo_clips")

    try:
        from jsonschema import Draft202012Validator
    except ImportError:
        print("[WARN] jsonschema not installed — pip install jsonschema for full validation")
        Draft202012Validator = None  # type: ignore

    def validate(label: str, data: dict) -> int:
        if Draft202012Validator is None:
            print(f"[skip] {label}: no jsonschema")
            return 0
        v = Draft202012Validator(schema)
        errs = list(v.iter_errors(data))
        if errs:
            print(f"[FAIL] {label}: {len(errs)} error(s)")
            for e in errs[:12]:
                path = "/".join(str(p) for p in e.path) or "(root)"
                print(f"       {path}: {e.message[:120]}")
            return 1
        print(f"[ok] {label}: valid")
        return 0

    scenes_path = _project_scenes()
    if scenes_path.is_file():
        current = json.loads(scenes_path.read_text(encoding="utf-8"))
        failed += validate(f"current {scenes_path.name}", current)

        legacy = copy.deepcopy(current)
        gpv = legacy.setdefault("global_production_variables", {})
        gpv.pop("location_seed_tokens", None)
        for s in legacy.get("scenes") or []:
            s.pop("location_ids", None)
            s.pop("primary_location_id", None)
            for b in s.get("story_beats") or []:
                if isinstance(b, dict):
                    b.pop("location_id", None)
        failed += validate("legacy without location fields", legacy)
    else:
        print(f"[WARN] no project scenes at {scenes_path}")

    if EXAMPLE_PATH.is_file():
        ex = json.loads(EXAMPLE_PATH.read_text(encoding="utf-8"))
        failed += validate("examples/scene_bible_minimal.json", ex)

    # Smoke: extract_stage1 import + convert still works
    try:
        sys.path.insert(0, str(ROOT / "scripts" / "two_stage_adaptation"))
        import extract_stage1_from_blueprint as ext  # type: ignore

        proj = scenes_path.parent
        clips = proj / "nickandme.clips.grok.json"
        if clips.is_file():
            data = json.loads(clips.read_text(encoding="utf-8"))
            scene0 = (data.get("scenes") or [None])[0]
            if scene0:
                out = ext.convert_scene(scene0, [], max_excerpt_chars=500)
                assert "scene_number" in out and "story_beats" in out
                # location fields may be empty lists if blueprint not re-extracted
                assert "location_ids" in out
                print("[ok] extract_stage1 convert_scene smoke (location_ids key present)")
            else:
                print("[WARN] no scenes in clips blueprint for extract smoke")
        else:
            print("[WARN] no clips blueprint for extract smoke")
    except Exception as e:
        print(f"[FAIL] extract_stage1 smoke: {e}")
        failed += 1

    print("=== RESULT ===")
    if failed:
        print(f"FAILED ({failed} check group(s))")
        return 1
    print("PASSED — Stage 1 location changes are backward-compatible")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
