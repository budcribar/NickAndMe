"""
Persistent edit / feedback log for the Streamlit review UI.

Records reviewer notes so they can be fed back into:
  - nickandme.clips.grok.json (clip visual_prompt / negative_prompt)
  - prompts/adaptation_v16.txt (learned rules section)
  - renderer guidance notes (review_feedback/SCRIPT_NOTES.md + optional marker)
"""
from __future__ import annotations

import json
import os
import re
import time
import uuid
from pathlib import Path
from typing import Any, Dict, List, Optional

from review_app.paths import repo_root, workspace_root

# Shared prompts live under workspace prompts/; project logs stay in project dir
ADAPTATION_PROMPT_REL = Path("prompts") / "adaptation_v16.txt"
# Legacy filename kept for fallback only
_LEGACY_ADAPTATION = "ClaudeAdaptationPromptV16.txt"

ROOT = repo_root()
LOG_PATH = ROOT / "edit_feedback_log.json"
LEARNINGS_MD = ROOT / "review_feedback" / "LEARNINGS.md"
SCRIPT_NOTES_MD = ROOT / "review_feedback" / "SCRIPT_NOTES.md"


def _resolve_adaptation_prompt(ws: Path, project: Path) -> Path:
    for candidate in (
        ws / ADAPTATION_PROMPT_REL,
        project / ADAPTATION_PROMPT_REL,
        ws / _LEGACY_ADAPTATION,
        project / _LEGACY_ADAPTATION,
    ):
        if candidate.is_file():
            return candidate
    return ws / ADAPTATION_PROMPT_REL


ADAPTATION_PROMPT = _resolve_adaptation_prompt(workspace_root(), ROOT)
LEARNINGS_MARKER_START = "<!-- GUI_LEARNINGS_START -->"
LEARNINGS_MARKER_END = "<!-- GUI_LEARNINGS_END -->"
ADAPTATION_SECTION_HEADER = (
    "\n================================================================\n"
    "GUI REVIEW LEARNINGS (appended from Streamlit edit log)\n"
    "================================================================\n"
)


def reload_paths() -> None:
    """Call after switching projects so logs write into the new project dir."""
    global ROOT, LOG_PATH, LEARNINGS_MD, SCRIPT_NOTES_MD, ADAPTATION_PROMPT
    ROOT = repo_root()
    LOG_PATH = ROOT / "edit_feedback_log.json"
    LEARNINGS_MD = ROOT / "review_feedback" / "LEARNINGS.md"
    SCRIPT_NOTES_MD = ROOT / "review_feedback" / "SCRIPT_NOTES.md"
    ADAPTATION_PROMPT = _resolve_adaptation_prompt(workspace_root(), ROOT)


def _now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S")


def load_log() -> Dict[str, Any]:
    if LOG_PATH.is_file():
        try:
            data = json.loads(LOG_PATH.read_text(encoding="utf-8"))
            if isinstance(data, dict) and "entries" in data:
                return data
        except (json.JSONDecodeError, OSError):
            pass
    return {"version": 1, "entries": []}


def save_log(data: Dict[str, Any]) -> None:
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = LOG_PATH.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, LOG_PATH)


def add_entry(
    entry_type: str,
    user_note: str,
    *,
    scene: Optional[int] = None,
    clip: Optional[int] = None,
    character: Optional[str] = None,
    action_taken: str = "",
    before: str = "",
    after: str = "",
    suggested_rule: str = "",
    targets: Optional[List[str]] = None,
    extra: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    data = load_log()
    entry: Dict[str, Any] = {
        "id": str(uuid.uuid4())[:8],
        "ts": _now(),
        "type": entry_type,
        "scene": scene,
        "clip": clip,
        "character": character,
        "user_note": (user_note or "").strip(),
        "action_taken": action_taken,
        "before": before,
        "after": after,
        "suggested_rule": suggested_rule
        or _suggest_rule(entry_type, user_note, scene, clip, character),
        "targets": targets
        or ["nickandme.clips.grok.json", "prompts/adaptation_v16.txt", "renderer"],
        "applied": {
            "blueprint": False,
            "adaptation_prompt": False,
            "script_notes": False,
            "learnings_md": False,
        },
        "extra": extra or {},
    }
    data["entries"].insert(0, entry)
    save_log(data)
    return entry


def get_entry(entry_id: str) -> Optional[Dict[str, Any]]:
    for e in load_log().get("entries", []):
        if e.get("id") == entry_id:
            return e
    return None


def update_entry(entry_id: str, **fields: Any) -> Optional[Dict[str, Any]]:
    data = load_log()
    for e in data.get("entries", []):
        if e.get("id") == entry_id:
            e.update(fields)
            save_log(data)
            return e
    return None


def _suggest_rule(
    entry_type: str,
    user_note: str,
    scene: Optional[int],
    clip: Optional[int],
    character: Optional[str],
) -> str:
    note = (user_note or "").strip()
    if not note:
        return ""
    loc = ""
    if scene is not None and clip is not None:
        loc = f" (seen on S{scene}C{clip})"
    elif character:
        loc = f" (character {character})"
    # Heuristic templates for common failure modes
    low = note.lower()
    if any(w in low for w in ("facing", "wrong way", "camera", "eyeline", "looks at us", "looking at viewer")):
        return (
            f"Blocking/eyeline: when a character confronts or approaches someone, lock who they face "
            f"and camera position (OTS/behind); never rely on 'steps toward' alone{loc}. "
            f"Reviewer note: {note}"
        )
    if any(w in low for w in ("adult", "bodybuilder", "too old", "age")):
        return (
            f"Age variants: use Character_*_Young/_Teen tokens and child proportions; "
            f"do not lock adult refs on flashback kids{loc}. Reviewer note: {note}"
        )
    if any(w in low for w in ("window", "wall", "ball", "wrong action", "vo", "voiceover")):
        return (
            f"VO↔visual fidelity: the visual must match the book/dialogue event{loc}. "
            f"Reviewer note: {note}"
        )
    if entry_type.startswith("character"):
        return f"Character design: {note}{loc}"
    return f"Review learning{loc}: {note}"


def append_learnings_md(entry: Dict[str, Any]) -> str:
    LEARNINGS_MD.parent.mkdir(parents=True, exist_ok=True)
    if not LEARNINGS_MD.is_file():
        LEARNINGS_MD.write_text(
            "# Review learnings\n\n"
            "Auto-appended from the Streamlit edit log. Use these to improve "
            "`prompts/adaptation_v16.txt` and prompt rewrites.\n\n",
            encoding="utf-8",
        )
    rule = entry.get("suggested_rule") or entry.get("user_note") or ""
    block = (
        f"## {entry.get('ts')} — {entry.get('id')} ({entry.get('type')})\n"
        f"- Scene/clip: S{entry.get('scene')}C{entry.get('clip')}\n"
        f"- Character: {entry.get('character') or '—'}\n"
        f"- Note: {entry.get('user_note')}\n"
        f"- Action: {entry.get('action_taken')}\n"
        f"- Suggested rule: {rule}\n\n"
    )
    with LEARNINGS_MD.open("a", encoding="utf-8") as f:
        f.write(block)
    return str(LEARNINGS_MD)


def append_script_notes(entry: Dict[str, Any]) -> str:
    SCRIPT_NOTES_MD.parent.mkdir(parents=True, exist_ok=True)
    if not SCRIPT_NOTES_MD.is_file():
        SCRIPT_NOTES_MD.write_text(
            "# Renderer notes from review\n\n"
            "Feedback that may require engine changes (continuation, QA, anchors, GUI hooks).\n"
            "Do not auto-edit `renderer/` blindly — implement deliberately.\n\n",
            encoding="utf-8",
        )
    block = (
        f"## {entry.get('ts')} — {entry.get('id')}\n"
        f"- Type: {entry.get('type')}\n"
        f"- S{entry.get('scene')}C{entry.get('clip')} / {entry.get('character')}\n"
        f"- User: {entry.get('user_note')}\n"
        f"- Suggested: {entry.get('suggested_rule')}\n"
        f"- Blueprint before→after stored in edit_feedback_log.json id={entry.get('id')}\n\n"
    )
    with SCRIPT_NOTES_MD.open("a", encoding="utf-8") as f:
        f.write(block)
    return str(SCRIPT_NOTES_MD)


def append_adaptation_prompt(entry: Dict[str, Any]) -> str:
    """Append a bullet under the GUI learnings section of prompts/adaptation_v16.txt."""
    rule = (entry.get("suggested_rule") or entry.get("user_note") or "").strip()
    if not rule:
        raise ValueError("No rule text to append")
    if not ADAPTATION_PROMPT.is_file():
        raise FileNotFoundError(str(ADAPTATION_PROMPT))

    text = ADAPTATION_PROMPT.read_text(encoding="utf-8")
    bullet = f"- [{entry.get('id')} {entry.get('ts')}] {rule}\n"

    if LEARNINGS_MARKER_START in text and LEARNINGS_MARKER_END in text:
        pre, rest = text.split(LEARNINGS_MARKER_START, 1)
        mid, post = rest.split(LEARNINGS_MARKER_END, 1)
        mid = mid.rstrip() + "\n" + bullet
        new_text = pre + LEARNINGS_MARKER_START + mid + "\n" + LEARNINGS_MARKER_END + post
    else:
        new_text = (
            text.rstrip()
            + "\n"
            + ADAPTATION_SECTION_HEADER
            + LEARNINGS_MARKER_START
            + "\n"
            + bullet
            + LEARNINGS_MARKER_END
            + "\n"
        )

    tmp = ADAPTATION_PROMPT.with_suffix(".tmp")
    tmp.write_text(new_text, encoding="utf-8")
    os.replace(tmp, ADAPTATION_PROMPT)
    return str(ADAPTATION_PROMPT)


def mark_applied(entry_id: str, key: str) -> None:
    data = load_log()
    for e in data.get("entries", []):
        if e.get("id") == entry_id:
            e.setdefault("applied", {})[key] = True
            save_log(data)
            return


def filter_entries(
    entries: List[Dict[str, Any]],
    *,
    entry_type: Optional[str] = None,
    scene: Optional[int] = None,
    unapplied_only: bool = False,
) -> List[Dict[str, Any]]:
    out = []
    for e in entries:
        if entry_type and e.get("type") != entry_type:
            continue
        if scene is not None and e.get("scene") != scene:
            continue
        if unapplied_only:
            applied = e.get("applied") or {}
            if all(applied.get(k) for k in ("blueprint", "adaptation_prompt", "script_notes", "learnings_md")):
                continue
        out.append(e)
    return out
