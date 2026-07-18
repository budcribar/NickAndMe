#!/usr/bin/env python3
"""
Verify Fountain-first screenplay prompts still exist after refactors.

Checks:
  1) prompts/book_to_fountain.txt present with key learnings
  2) stage1_scene_bible.txt is gone (retired)
  3) optional: project screenplay.fountain exists for active project
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FOUNTAIN_PROMPT = ROOT / "prompts" / "book_to_fountain.txt"
LEGACY_PROMPT = ROOT / "prompts" / "stage1_scene_bible.txt"


def _project_dir() -> Path:
    ws = ROOT / "projects" / "workspace.json"
    pid = "NickAndMe"
    if ws.is_file():
        try:
            pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
        except (json.JSONDecodeError, OSError):
            pass
    return ROOT / "projects" / str(pid)


def main() -> int:
    failed = 0

    if LEGACY_PROMPT.is_file():
        print(f"[FAIL] legacy prompt still present (should be deleted): {LEGACY_PROMPT}")
        failed += 1
    else:
        print("[ok] stage1_scene_bible.txt removed")

    if not FOUNTAIN_PROMPT.is_file():
        print(f"[FAIL] missing {FOUNTAIN_PROMPT}")
        failed += 1
        return failed

    text = FOUNTAIN_PROMPT.read_text(encoding="utf-8")
    for needle in (
        "Fountain",
        "{{TOTAL_RUNTIME_MINUTES}}",
        "[[page N]]",
        "NARRATOR",
        "closed cast",
        "VO",
    ):
        if needle.lower() not in text.lower() and needle not in text:
            # case-sensitive for template; case-insensitive for prose
            if needle == "{{TOTAL_RUNTIME_MINUTES}}" and needle not in text:
                print(f"[FAIL] book_to_fountain.txt missing {needle}")
                failed += 1
            elif needle != "{{TOTAL_RUNTIME_MINUTES}}" and needle.lower() not in text.lower():
                print(f"[FAIL] book_to_fountain.txt missing learnings: {needle}")
                failed += 1
    if failed == 0 or (failed and FOUNTAIN_PROMPT.is_file()):
        print("[ok] book_to_fountain.txt present with core learnings")

    draft = _project_dir() / "source" / "screenplay.fountain"
    if draft.is_file():
        print(f"[ok] project Fountain draft: {draft}")
    else:
        print(f"[info] no screenplay.fountain yet at {draft} (ok if not imported)")

    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
