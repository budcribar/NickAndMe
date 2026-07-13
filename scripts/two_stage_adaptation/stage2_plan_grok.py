#!/usr/bin/env python3
"""
Stage 2 shot planner — Grok profile.

Converts Stage 1 scene bible (nickandme.scenes.json) into a pipeline-ready
clip plan optimized for xAI Grok Imagine video constraints.

Usage (repo root):
  python scripts/two_stage_adaptation/stage2_plan_grok.py
  python scripts/two_stage_adaptation/stage2_plan_grok.py --scenes 1-2 --out nickandme.clips.grok.s1-2.json
  python scripts/two_stage_adaptation/stage2_plan_grok.py --merge-into nickandme.clips.grok.json
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

ROOT = Path(__file__).resolve().parents[2]

GLOBAL_NEGATIVE = (
    "no legible text, no watermarks, no logos, no extra limbs, "
    "blur/obscure environmental signage or screens, no name tags, no name badges, "
    "no embroidered names, no lower thirds, no personal names on clothing or props"
)

# Fallback constraints (overridden by pipeline_config duration_defaults / prompt_limits)
GROK_MIN_CLIP = 6
GROK_MAX_CLIP = 10
GROK_ABS_MAX = 15
GROK_DEFAULT = 8
GROK_SCENE_MIN = 8
GROK_SCENE_MAX = 134
# Legacy names — prefer _prompt_limits_from_config() for active model
GROK_PROMPT_SOFT = 500
GROK_PROMPT_HARD = 800


def _load_project_config() -> Dict[str, Any]:
    """Active project's pipeline_config.json (or empty)."""
    try:
        ws = ROOT / "projects" / "workspace.json"
        pid = "NickAndMe"
        if ws.is_file():
            try:
                pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
            except Exception:
                pass
        cfg_path = ROOT / "projects" / str(pid) / "pipeline_config.json"
        if cfg_path.is_file():
            return json.loads(cfg_path.read_text(encoding="utf-8"))
    except Exception:
        pass
    return {}


def _duration_policy_from_config(
    config: Optional[Dict[str, Any]] = None,
) -> Dict[str, int]:
    """Load prefer/default/max clip seconds from renderer duration_defaults."""
    try:
        sys.path.insert(0, str(ROOT))
        from renderer.engine import resolve_duration_profile  # type: ignore

        if config is None:
            config = _load_project_config()
        prof = resolve_duration_profile(config or {})
        return {
            "default": int(prof.get("default", GROK_DEFAULT)),
            "prefer_min": int(prof.get("prefer_min", GROK_MIN_CLIP)),
            "prefer_max": int(prof.get("prefer_max", GROK_MAX_CLIP)),
            "min": int(prof.get("min", 1)),
            "max": int(prof.get("max", GROK_ABS_MAX)),
        }
    except Exception:
        return {
            "default": GROK_DEFAULT,
            "prefer_min": GROK_MIN_CLIP,
            "prefer_max": GROK_MAX_CLIP,
            "min": 1,
            "max": GROK_ABS_MAX,
        }


def _prompt_limits_from_config(
    config: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """visual_prompt soft/hard from target provider+model (config.prompt_limits)."""
    try:
        sys.path.insert(0, str(ROOT))
        from renderer.engine import resolve_prompt_limits  # type: ignore

        if config is None:
            config = _load_project_config()
        return resolve_prompt_limits(config or {})
    except Exception:
        return {
            "soft": GROK_PROMPT_SOFT,
            "hard": GROK_PROMPT_HARD,
            "full_max": 0,
            "source": "fallback",
        }


def _ts(start: int, end: int) -> str:
    def fmt(s: int) -> str:
        return f"{s // 60:02d}:{s % 60:02d}"

    return f"{fmt(start)}-{fmt(end)}"


def _clamp_prompt(text: str, hard: Optional[int] = None) -> str:
    if hard is None:
        hard = int(_prompt_limits_from_config().get("hard") or GROK_PROMPT_HARD)
    text = re.sub(r"\s+", " ", text).strip()
    suffix_m = re.search(r"\s*/\s*\d+p,\s*24fps\s*$", text, re.I)
    suffix = suffix_m.group(0) if suffix_m else " / 720p, 24fps"
    base = re.sub(r"\s*/\s*\d+p,\s*24fps\s*$", "", text, flags=re.I).strip()
    budget = int(hard) - len(suffix)
    if len(base) > budget:
        base = base[: max(40, budget - 1)].rsplit(" ", 1)[0] + "…"
    return f"{base}{suffix}"


def _force_none(beat: Dict[str, Any], clip_index: int) -> bool:
    if clip_index == 0:
        return True
    ac = (beat.get("action_class") or "").lower()
    cont = (beat.get("continuity") or "").lower()
    if ac in (
        "big_action",
        "establishing",
        "hard_cut",
        "flashback_enter",
        "flashback_exit",
        "montage",
    ):
        return True
    if cont in ("new_setup", "return_to_present", "parallel"):
        return True
    # Narrator VO / internal VO: never extend from previous (avoids frozen Mom mid-speech
    # carrying into a VO clip so she appears to keep talking).
    audio = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    delivery = str(
        (audio.get("delivery") if audio else None) or beat.get("delivery") or ""
    ).lower()
    speaker = str(
        (audio.get("speaker") if audio else None) or beat.get("speaker") or ""
    ).lower()
    if delivery in (
        "voiceover_internal",
        "internal",
        "vo_internal",
        "thought",
        "thinking",
        "narration",
        "vo",
    ) or "narrator" in speaker:
        return True
    ve = (beat.get("visual_event") or "").lower()
    if re.search(
        r"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket|wide shot|establishing|"
        r"flashback|back to present|cut to)\b",
        ve,
    ):
        return True
    return False


def _allocate_durations(
    beats: Sequence[Dict[str, Any]],
    target: int,
    *,
    policy: Optional[Dict[str, int]] = None,
) -> List[int]:
    """
    Allocate integer seconds per beat under provider duration_defaults preference.
    Total exactly equals clamped target.
    """
    pol = policy or _duration_policy_from_config()
    d_def = int(pol.get("default", GROK_DEFAULT))
    d_min = int(pol.get("prefer_min", GROK_MIN_CLIP))
    d_max = int(pol.get("prefer_max", GROK_MAX_CLIP))
    # Hard cap: Grok image/reference-to-video rejects >10s (HTTP 400).
    # Never plan clips longer than that even if Stage 1 duration_target is large.
    d_hard = min(int(pol.get("max", GROK_ABS_MAX)), GROK_MAX_CLIP, 10)
    d_max = min(d_max, d_hard)
    d_min = min(d_min, d_max)
    d_def = max(d_min, min(d_max, d_def))

    n = len(beats)
    if n == 0:
        return []
    target = max(GROK_SCENE_MIN, min(GROK_SCENE_MAX, int(target or n * d_def)))
    # Prefer default seconds each; adjust target toward n*default when close
    preferred = n * d_def
    if abs(target - preferred) <= n:  # small drift — snap toward default grid
        target = max(GROK_SCENE_MIN, min(GROK_SCENE_MAX, preferred))
    # Minimum total if each clip at least prefer_min
    min_total = n * d_min
    max_total = n * d_max
    if target < min_total:
        target = min_total
    if target > max_total:
        # Do NOT stretch past d_hard — more beats / shorter clips is safer than 12s API fails
        target = max_total

    weights = [float(b.get("time_weight") or 1.0) for b in beats]
    wsum = sum(weights) or float(n)
    raw = [target * (w / wsum) for w in weights]
    durs = [int(round(x)) for x in raw]
    # Clamp each to prefer band then fix sum
    durs = [max(d_min, min(d_max, d)) for d in durs]
    # Fix sum
    diff = target - sum(durs)
    i = 0
    guard = 0
    while diff != 0 and guard < 10000:
        guard += 1
        idx = i % n
        if diff > 0 and durs[idx] < d_max:
            durs[idx] += 1
            diff -= 1
        elif diff < 0 and durs[idx] > d_min:
            durs[idx] -= 1
            diff += 1
        else:
            i += 1
            if i > n * 20:
                break
            continue
        i += 1
    return durs


def _neg_extras(beat: Dict[str, Any]) -> str:
    extras: List[str] = []
    for m in beat.get("must_not") or []:
        m = str(m).strip()
        if not m:
            continue
        # short negative-friendly fragments
        low = m.lower()
        if "miss" in low or "over wall" in low:
            extras.append("ball misses window, ball over wall, jump cut mid-flight")
        if "facing camera" in low or "viewer" in low:
            extras.append("facing camera only, looking at viewer, back to house")
        if "bodybuilder" in low or "adult" in low:
            extras.append("adult bodybuilder child")
        if "name" in low:
            extras.append("name tag, name badge")
    ve = (beat.get("visual_event") or "").lower()
    if "smash" in ve or "window" in ve or "kick" in ve:
        extras.append(
            "ball misses window, ball over wall, jump cut mid-flight, teleporting ball, "
            "two separate shots glued together"
        )
    if "ots" in ve or "camera behind" in ve or "back to camera" in ve:
        extras.append("facing camera only, boy looking at viewer, back to house")
    # Single-speaker rule for multi-cast dialogue (permanent negative)
    audio = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    delivery = str(
        (audio.get("delivery") if audio else None) or beat.get("delivery") or ""
    ).lower()
    if delivery == "spoken_on_camera":
        extras.append(
            "dog talking, animal mouthing words, dual lip-sync, non-speaker speaking, "
            "two characters lip-syncing same line, karaoke dog"
        )
    # unique keep order
    seen = set()
    out = []
    for e in extras:
        for part in e.split(","):
            p = part.strip()
            if p and p not in seen:
                seen.add(p)
                out.append(p)
    return ", ".join(out)


def _location_lock_phrase(
    scene: Dict[str, Any],
    beat: Dict[str, Any],
    location_seeds: Optional[Dict[str, Any]] = None,
) -> str:
    """Short place pin from location_seed_tokens for visual_prompt prefix."""
    location_seeds = location_seeds or {}
    lids = scene.get("location_ids") or []
    lid = (
        beat.get("location_id")
        or scene.get("primary_location_id")
        or (lids[0] if lids else "")
    )
    if not lid:
        return ""
    seed = location_seeds.get(lid) or {}
    lock = (seed.get("visual_lock") or seed.get("description") or seed.get("display_name") or lid).strip()
    if not lock:
        return lid
    # Keep short for prompt budget
    if len(lock) > 120:
        lock = lock[:117].rsplit(" ", 1)[0] + "…"
    return lock


def _scene_cast_tokens(scene: Dict[str, Any], beat: Dict[str, Any]) -> List[str]:
    """
    Ordered Character_* tokens for this shot: primary first, then scene cast, then beat.
    Skips narrator / never-on-screen-style keys for visual locking.
    """
    out: List[str] = []
    seen: set = set()

    def _add(tok: Any) -> None:
        if not tok:
            return
        t = str(tok).strip()
        if not t.startswith("Character_"):
            return
        if "narrator" in t.lower():
            return
        if t in seen:
            return
        seen.add(t)
        out.append(t)

    _add(beat.get("primary_subject"))
    for t in scene.get("characters_on_screen") or []:
        _add(t)
    # Also pick up any Character_* already in visual_event
    ve = str(beat.get("visual_event") or "")
    for m in re.finditer(r"Character_[A-Za-z0-9_]+", ve):
        _add(m.group(0))
    return out


def _identity_cues(
    tokens: Sequence[str],
    character_seeds: Optional[Dict[str, Any]] = None,
    *,
    max_chars: int = 100,
) -> str:
    """Short stable-identity phrase from seeds (colors/markings) for visual_prompt."""
    character_seeds = character_seeds or {}
    bits: List[str] = []
    for tok in tokens[:3]:
        seed = character_seeds.get(tok) if isinstance(character_seeds, dict) else None
        if not isinstance(seed, dict):
            continue
        desc = str(seed.get("description") or "").strip()
        if not desc:
            continue
        # Prefer first clause with color/marking words if present
        short = re.sub(r"\s+", " ", desc)
        if len(short) > max_chars:
            short = short[: max_chars - 1].rsplit(" ", 1)[0] + "…"
        bits.append(f"{tok} ({short})")
    if not bits:
        return ""
    return "Same identity as locked refs: " + "; ".join(bits)


def _dialogue_quote_for_prompt(dialogue: str, max_len: int = 90) -> str:
    """Compact spoken line for visual_prompt attribution (ASCII-safe quotes)."""
    d = re.sub(r"\s+", " ", (dialogue or "").strip())
    d = d.replace('"', "'").replace("“", "'").replace("”", "'")
    if len(d) > max_len:
        d = d[: max_len - 1].rsplit(" ", 1)[0] + "…"
    return d


def _parse_dialogue_turns(beat: Dict[str, Any]) -> List[Dict[str, str]]:
    """
    Ordered speech turns for a beat. Supports:
      - dialogue_turns / speech_turns: [{speaker, dialogue, delivery?}, ...]
      - tagged dialogue: Character_A: line. Character_B: line.
      - single root speaker + dialogue
    Prefers keeping multi-speaker banter as ONE list (one continuous clip), not N clips.
    """
    nested = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    turns_raw = (
        beat.get("dialogue_turns")
        or beat.get("speech_turns")
        or (nested.get("dialogue_turns") if nested else None)
    )
    out: List[Dict[str, str]] = []
    if isinstance(turns_raw, list):
        for turn in turns_raw:
            if not isinstance(turn, dict):
                continue
            sp = str(turn.get("speaker") or "").strip()
            line = str(turn.get("dialogue") or turn.get("line") or "").strip()
            if not sp or not line:
                continue
            deliv = str(
                turn.get("delivery")
                or beat.get("delivery")
                or nested.get("delivery")
                or "spoken_on_camera"
            ).lower()
            if "narrator" in sp.lower():
                deliv = "voiceover_internal"
            out.append({"speaker": sp, "dialogue": line, "delivery": deliv})
        if out:
            return out

    dialogue = str(
        (nested.get("dialogue") if nested else None)
        or beat.get("dialogue")
        or ""
    ).strip()
    # Character_X: "line" Character_Y: line
    found = re.findall(
        r"(Character_[A-Za-z0-9_]+)\s*:\s*[\"'“]?(.*?)(?=\s*Character_[A-Za-z0-9_]+\s*:|[\"'”]?\s*$)",
        dialogue,
        flags=re.S,
    )
    found = [(sp, ln.strip().strip("\"'“”").strip()) for sp, ln in found if ln.strip()]
    if len(found) >= 2 and len({sp for sp, _ in found}) >= 2:
        for sp, line in found:
            deliv = (
                "voiceover_internal"
                if "narrator" in sp.lower()
                else "spoken_on_camera"
            )
            out.append({"speaker": sp, "dialogue": line, "delivery": deliv})
        return out

    # Single-speaker beat
    sp = str(
        (nested.get("speaker") if nested else None) or beat.get("speaker") or ""
    ).strip()
    if sp and dialogue and sp.lower() not in ("none", "n/a", "-"):
        deliv = str(
            (nested.get("delivery") if nested else None)
            or beat.get("delivery")
            or "spoken_on_camera"
        ).lower()
        if "narrator" in sp.lower():
            deliv = "voiceover_internal"
        out.append({"speaker": sp, "dialogue": dialogue, "delivery": deliv})
    return out


def _speech_attribution_clause(
    *,
    delivery: str = "",
    speaker: str = "",
    dialogue: str = "",
    turns: Optional[List[Dict[str, str]]] = None,
) -> List[str]:
    """
    Explicit who-talks + what-they-say for Grok.

    Multi-speaker banter stays on ONE clip via dialogue_turns (ordered).
    Split into separate clips only when Stage 2 chooses hard cuts / new setups.
    """
    if turns and len(turns) >= 2:
        bits: List[str] = [
            "MULTI-SPEAKER DIALOGUE in order on this continuous take "
            "(voices bounce; do not merge lines into one speaker)"
        ]
        for i, t in enumerate(turns, 1):
            sp = str(t.get("speaker") or "").strip()
            line = _dialogue_quote_for_prompt(str(t.get("dialogue") or ""), max_len=70)
            deliv = str(t.get("delivery") or "spoken_on_camera").lower()
            if not sp or not line:
                continue
            if deliv == "voiceover_internal" or "narrator" in sp.lower():
                bits.append(f'{i}) OFF-CAMERA {sp} VO says: "{line}"')
            else:
                bits.append(
                    f'{i}) {sp} ON CAMERA lip-syncs says: "{line}"'
                )
        bits.append(
            "LIP-SYNC: only the active speaker for each turn moves their mouth; "
            "listeners (and any dog/pet) keep mouth/snout CLOSED while others talk; "
            "never two mouths moving on the same words"
        )
        return bits

    sp = (speaker or "").strip()
    if not sp or sp.lower() in ("none", "n/a", "-"):
        return []
    deliv = (delivery or "").strip().lower()
    quote = _dialogue_quote_for_prompt(dialogue) if dialogue else ""
    out: List[str] = []
    if deliv == "voiceover_internal":
        if quote:
            out.append(
                f'OFF-CAMERA VOICEOVER by {sp} only saying: "{quote}" '
                f"(not visible; not lip-synced)"
            )
        else:
            out.append(
                f"OFF-CAMERA VOICEOVER by {sp} only (not visible; not lip-synced)"
            )
        out.append(
            "on-screen mouths/snouts CLOSED and still — VO is not lip-synced conversation"
        )
    elif deliv == "spoken_on_camera" and "narrator" not in sp.lower():
        if quote:
            out.append(
                f'ONLY {sp} speaks on camera and lip-syncs saying: "{quote}"; '
                f"other characters and any dog keep mouth/snout closed and still "
                f"(listening) until their turn"
            )
        else:
            out.append(
                f"ONLY {sp} speaks on camera and lip-syncs; other characters and any dog keep "
                f"mouth/snout closed and still (listening)"
            )
    return out


def _normalize_beats_speech(beats: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """
    Attach dialogue_turns for multi-speaker banter; keep ONE beat (one clip) so
    conversation can bounce without a hard cut per line.
    """
    out: List[Dict[str, Any]] = []
    for beat in beats:
        if not isinstance(beat, dict):
            continue
        b = dict(beat)
        turns = _parse_dialogue_turns(b)
        if len(turns) >= 2:
            b["dialogue_turns"] = turns
            # Legacy single fields = first turn + joined script for tooling
            b["speaker"] = turns[0]["speaker"]
            b["dialogue"] = " ".join(t["dialogue"] for t in turns)
            # Scene delivery: on-camera if any turn is; pure narrator chain stays VO
            if all(
                t.get("delivery") == "voiceover_internal"
                or "narrator" in str(t.get("speaker") or "").lower()
                for t in turns
            ):
                b["delivery"] = "voiceover_internal"
            else:
                b["delivery"] = "spoken_on_camera"
            # Visual focus: first on-camera speaker if any
            for t in turns:
                sp = t["speaker"]
                if "narrator" not in sp.lower() and t.get("delivery") != "voiceover_internal":
                    b["primary_subject"] = sp
                    break
        out.append(b)
    return out


def _build_visual_prompt(
    beat: Dict[str, Any],
    scene: Dict[str, Any],
    resolution: str,
    location_seeds: Optional[Dict[str, Any]] = None,
    character_seeds: Optional[Dict[str, Any]] = None,
    *,
    prompt_soft: Optional[int] = None,
    prompt_hard: Optional[int] = None,
) -> str:
    ve = (beat.get("visual_event") or "").strip()
    # strip old tech suffix
    ve = re.sub(r"\s*/\s*\d+p.*$", "", ve, flags=re.I).strip()

    bits: List[str] = []
    # Place pin first (location consistency)
    place = _location_lock_phrase(scene, beat, location_seeds)
    if place and place.lower() not in ve.lower()[:100]:
        bits.append(place)

    cast = _scene_cast_tokens(scene, beat)
    primary = beat.get("primary_subject") or (cast[0] if cast else None)
    # Ensure primary Character_* early for identity lock
    if primary and str(primary) not in ve[:100]:
        bits.append(str(primary))

    # Name other on-screen cast tokens (multi-ref automation)
    others = [t for t in cast if t != primary and t not in ve]
    if others:
        bits.append("also on screen: " + ", ".join(others[:4]))

    # Scene one-liner context (short)
    setting = (scene.get("setting") or "")[:60]
    if setting and setting.lower() not in ve.lower()[:100]:
        # only for fresh establishes
        if (beat.get("action_class") or "") in ("establishing", "flashback_enter"):
            bits.append(setting)

    bits.append(ve)

    block = beat.get("blocking_notes") or ""
    if block and block.lower() not in ve.lower():
        bits.append(block)

    audio = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    # Stage 1 often stores delivery/speaker/dialogue on the beat root (not nested)
    delivery = (
        (audio.get("delivery") if audio else None)
        or beat.get("delivery")
        or "none"
    )
    delivery = str(delivery).lower()
    speaker = str(
        (audio.get("speaker") if audio else None) or beat.get("speaker") or ""
    ).strip()
    dialogue = str(
        (audio.get("dialogue") if audio else None) or beat.get("dialogue") or ""
    ).strip()
    turns = _parse_dialogue_turns(beat)
    # Who talks + exact line(s). Multi-speaker banter = ordered turns on ONE take.
    # BEFORE identity cues so attribution survives prompt soft-clamp
    for clause in _speech_attribution_clause(
        delivery=delivery,
        speaker=speaker,
        dialogue=dialogue,
        turns=turns if len(turns) >= 2 else None,
    ):
        body_so_far = " ".join(bits).lower()
        if clause.lower() not in body_so_far:
            bits.append(clause)

    # Continuous action language for big_action
    ac = (beat.get("action_class") or "").lower()
    if ac == "big_action":
        if "continuous" not in ve.lower() and "one continuous" not in ve.lower():
            bits.append(
                "ONE continuous take no cut; unbroken cause-to-effect motion"
            )

    must_not = beat.get("must_not") or []
    if must_not:
        short = "; ".join(str(m) for m in must_not[:3])
        if short and short.lower() not in ve.lower():
            bits.append(f"must not: {short}")

    # Identity last (can truncate under soft limit; engine still injects locks at generate)
    id_cue = _identity_cues(cast, character_seeds, max_chars=72)
    if id_cue and id_cue.lower() not in ve.lower():
        bits.append(id_cue)

    body = ". ".join(b.strip().rstrip(".") for b in bits if b and str(b).strip())
    body = re.sub(r"\s+", " ", body).strip()
    if not body.endswith("."):
        # keep natural
        pass
    prompt = f"{body} / {resolution}, 24fps"
    # Model-aware soft/hard (from pipeline_config.prompt_limits)
    lim = _prompt_limits_from_config()
    soft = int(prompt_soft if prompt_soft is not None else lim.get("soft") or GROK_PROMPT_SOFT)
    hard = int(prompt_hard if prompt_hard is not None else lim.get("hard") or GROK_PROMPT_HARD)
    if len(prompt) > soft:
        prompt = _clamp_prompt(prompt, soft)
    elif len(prompt) > hard:
        prompt = _clamp_prompt(prompt, hard)
    return prompt


def _beat_audio_fields(beat: Dict[str, Any]) -> Dict[str, str]:
    """
    Normalize audio from Stage 1 beats.

    Stage 1 schema stores delivery/speaker/dialogue on the beat root (and optional
    ambient_or_sfx). Nested beat['audio'] is also accepted when present.
    """
    nested = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    delivery = (
        nested.get("delivery")
        or beat.get("delivery")
        or "none"
    )
    delivery = str(delivery).strip().lower()
    speaker = (
        nested.get("speaker")
        or beat.get("speaker")
        or "none"
    )
    speaker = str(speaker).strip() or "none"
    dialogue = (
        nested.get("dialogue")
        or beat.get("dialogue")
        or ""
    )
    dialogue = str(dialogue).strip()
    ambient = (
        nested.get("ambient")
        or nested.get("atmosphere")
        or beat.get("ambient_or_sfx")
        or beat.get("ambient")
        or ""
    )
    ambient = str(ambient).strip()
    sfx = str(nested.get("sfx") or nested.get("sound_effects") or "").strip()

    if delivery not in ("spoken_on_camera", "voiceover_internal", "none"):
        # Heuristic: book-style narration with Character_Narrator
        if dialogue and (
            "narrator" in speaker.lower()
            or delivery in ("vo", "voiceover", "narration", "voice_over")
        ):
            delivery = "voiceover_internal"
        elif dialogue:
            delivery = "voiceover_internal" if "narrator" in speaker.lower() else "spoken_on_camera"
        else:
            delivery = "none"

    if delivery == "none":
        speaker = "none"
        dialogue = ""

    # Narrator is never on-camera lip-sync
    if "narrator" in speaker.lower() and delivery == "spoken_on_camera":
        delivery = "voiceover_internal"

    out: Dict[str, Any] = {
        "speaker": speaker,
        "dialogue": dialogue,
        "delivery": delivery,
    }
    if ambient:
        out["ambient"] = ambient
    if sfx:
        out["sfx"] = sfx
    return out


def _build_audio_payload(beat: Dict[str, Any]) -> Dict[str, Any]:
    """
    audio_payload with optional dialogue_turns for multi-speaker continuous takes.

    Legacy fields speaker/dialogue/delivery always set (first turn / joined script)
    so older tooling keeps working.
    """
    fields = _beat_audio_fields(beat)
    turns = _parse_dialogue_turns(beat)
    out: Dict[str, Any] = dict(fields)
    if len(turns) >= 2:
        out["dialogue_turns"] = turns
        out["speaker"] = turns[0]["speaker"]
        # Joined script for weak-audio checks / UI preview
        out["dialogue"] = " ".join(
            f'{t["speaker"]}: {t["dialogue"]}' for t in turns
        )
        if all(
            t.get("delivery") == "voiceover_internal"
            or "narrator" in str(t.get("speaker") or "").lower()
            for t in turns
        ):
            out["delivery"] = "voiceover_internal"
        else:
            out["delivery"] = "spoken_on_camera"
    return out


def _music_bed(scene: Dict[str, Any], total: int) -> Dict[str, Any]:
    mi = scene.get("music_intent") or {}
    style = mi.get("style_description") or "cinematic underscore"
    vocal = mi.get("vocal_style") or ""
    # Simple even split into up to 3 sections
    n_sec = 1 if total <= 12 else (2 if total <= 24 else 3)
    base = total // n_sec
    rem = total - base * n_sec
    structure = []
    labels = ["A", "B", "C"]
    for i in range(n_sec):
        dur = base + (1 if i < rem else 0)
        structure.append(
            {
                "section_label": labels[i],
                "section_type": "instrumental",
                "is_repeat_of": None,
                "duration_seconds": dur,
                "production_notes": [
                    mi.get("mood_arc") or style,
                ],
                "lyrics": None,
            }
        )
    return {
        "style_description": style,
        "vocal_style": vocal,
        "song_structure": structure,
    }


def _union_characters_on_screen(scene: Dict[str, Any]) -> List[str]:
    """Stage 1 cast list ∪ primary_subjects (so engine multi-ref never loses the hero)."""
    out: List[str] = []
    seen: set = set()
    for t in list(scene.get("characters_on_screen") or []):
        s = str(t).strip()
        if s.startswith("Character_") and s not in seen:
            seen.add(s)
            out.append(s)
    for beat in scene.get("story_beats") or []:
        if not isinstance(beat, dict):
            continue
        ps = str(beat.get("primary_subject") or "").strip()
        if ps.startswith("Character_") and ps not in seen and "narrator" not in ps.lower():
            seen.add(ps)
            out.append(ps)
    return out


def plan_scene(
    scene: Dict[str, Any],
    resolution: str = "720p",
    location_seeds: Optional[Dict[str, Any]] = None,
    character_seeds: Optional[Dict[str, Any]] = None,
    *,
    prompt_soft: Optional[int] = None,
    prompt_hard: Optional[int] = None,
) -> Dict[str, Any]:
    beats = list(scene.get("story_beats") or [])
    # Multi-speaker banter → dialogue_turns on the same beat (one continuous clip)
    beats = _normalize_beats_speech(beats)
    lim = _prompt_limits_from_config()
    soft = int(prompt_soft if prompt_soft is not None else lim.get("soft") or GROK_PROMPT_SOFT)
    hard = int(prompt_hard if prompt_hard is not None else lim.get("hard") or GROK_PROMPT_HARD)
    lids = list(scene.get("location_ids") or [])
    primary = scene.get("primary_location_id") or (lids[0] if lids else None)
    cast = _union_characters_on_screen(scene)
    if not beats:
        # empty scene placeholder
        return {
            "scene_number": scene.get("scene_number"),
            "setting": scene.get("setting"),
            "location_ids": lids,
            "primary_location_id": primary,
            "characters_on_screen": cast,
            "scene_filename": scene.get("scene_filename"),
            "transition_type": scene.get("transition_type") or "cut",
            "lighting_continuity_token": scene.get("lighting_continuity_token") or "",
            "total_estimated_duration_seconds": GROK_SCENE_MIN,
            "music_bed": _music_bed(scene, GROK_SCENE_MIN),
            "veo_clips": [],
            "stage1_scene_number": scene.get("scene_number"),
            "stage1_beat_map": [],
            "video_provider_profile": "grok",
        }

    pol = _duration_policy_from_config()
    d_def = int(pol.get("default", GROK_DEFAULT))
    target = int(scene.get("duration_target_seconds") or len(beats) * d_def)
    durs = _allocate_durations(beats, target, policy=pol)
    total = sum(durs)

    # Work on a shallow copy so cast is visible inside _build_visual_prompt
    scene_work = dict(scene)
    scene_work["characters_on_screen"] = cast

    clips: List[Dict[str, Any]] = []
    beat_map: List[str] = []
    t = 0
    prev_lid: Optional[str] = None
    for i, (beat, dur) in enumerate(zip(beats, durs)):
        lid = (
            beat.get("location_id")
            or primary
            or (lids[0] if lids else None)
        )
        cont = "none" if _force_none(beat, i) else "extend_previous"
        # never extend after big_action even if continuity says continuous
        if (beat.get("action_class") or "").lower() == "big_action":
            cont = "none"
        # location change forces hard cut
        if prev_lid and lid and lid != prev_lid:
            cont = "none"

        neg = GLOBAL_NEGATIVE
        extra = _neg_extras(beat)
        if extra:
            neg = f"{neg}, {extra}"

        vp = _build_visual_prompt(
            beat,
            scene_work,
            resolution,
            location_seeds=location_seeds,
            character_seeds=character_seeds,
            prompt_soft=soft,
            prompt_hard=hard,
        )
        # Special polish for known failure modes: continuous window smash
        if (beat.get("action_class") or "") == "big_action" and re.search(
            r"window|smash|kickball|kick", vp, re.I
        ):
            # strengthen continuous path language if not already present
            if "unbroken" not in vp.lower() and "continuous take" not in vp.lower():
                core = re.sub(r"\s*/\s*\d+p.*$", "", vp, flags=re.I).strip()
                core += (
                    " ONE continuous take: kick, unbroken flight INTO window, glass SMASHES; "
                    "never miss window, never cut mid-flight"
                )
                vp = _clamp_prompt(f"{core} / {resolution}, 24fps", soft)

        ap = _build_audio_payload(beat)
        clip = {
            "clip_number": i + 1,
            "timestamp": _ts(t, t + dur),
            "veo_continuation_source": cont,
            "location_id": lid,
            "visual_prompt": vp,
            "negative_prompt": neg,
            "audio_payload": ap,
            "stage1_beat_id": beat.get("beat_id"),
            "primary_subject": beat.get("primary_subject"),
            "duration_seconds": dur,
        }
        clips.append(clip)
        beat_map.append(str(beat.get("beat_id") or f"b{i+1}"))
        t += dur
        prev_lid = lid

    return {
        "scene_number": scene.get("scene_number"),
        "setting": scene.get("setting"),
        "location_ids": lids,
        "primary_location_id": primary,
        "characters_on_screen": cast,
        "scene_filename": scene.get("scene_filename"),
        "transition_type": scene.get("transition_type") or "cut",
        "lighting_continuity_token": scene.get("lighting_continuity_token") or "",
        "total_estimated_duration_seconds": total,
        "music_bed": _music_bed(scene, total),
        "veo_clips": clips,
        "stage1_scene_number": scene.get("scene_number"),
        "stage1_beat_map": beat_map,
        "video_provider_profile": "grok",
        "spoiler_constraints": scene.get("spoiler_constraints") or [],
        "source_book_refs": scene.get("source_book_refs") or [],
    }


def validate_plan(plan: Dict[str, Any]) -> List[str]:
    errs: List[str] = []
    gpv = plan.get("global_production_variables") or {}
    seeds = gpv.get("location_seed_tokens") or {}
    char_seeds = gpv.get("character_seed_tokens") or {}
    meta = plan.get("stage2_meta") or {}
    prompt_hard = int(
        meta.get("prompt_hard_max")
        or _prompt_limits_from_config().get("hard")
        or GROK_PROMPT_HARD
    )
    for sc in plan.get("scenes") or []:
        clips = sc.get("veo_clips") or []
        lids = sc.get("location_ids") or []
        cast = sc.get("characters_on_screen") or []
        total = int(sc.get("total_estimated_duration_seconds") or 0)
        ssum = 0
        prev_end = 0
        if clips and not cast:
            errs.append(
                f"S{sc.get('scene_number')}: missing characters_on_screen "
                "(copy from Stage 1 for multi-ref automation)"
            )
        for c in clips:
            lid = c.get("location_id")
            if not lid:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: missing location_id"
                )
            elif lids and lid not in lids:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                    f"location_id {lid} not in scene location_ids"
                )
            elif seeds and lid not in seeds and lid != "Loc_Unknown":
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                    f"location_id {lid} missing from location_seed_tokens"
                )
            ts = c.get("timestamp") or ""
            m = re.match(r"(\d+):(\d+)-(\d+):(\d+)", ts)
            if not m:
                errs.append(f"S{sc.get('scene_number')}C{c.get('clip_number')}: bad timestamp {ts}")
                continue
            a = int(m.group(1)) * 60 + int(m.group(2))
            b = int(m.group(3)) * 60 + int(m.group(4))
            dur = b - a
            ssum += dur
            if a != prev_end:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: gap/overlap at {ts}"
                )
            prev_end = b
            vp = c.get("visual_prompt") or ""
            if len(vp) > prompt_hard:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                    f"prompt len {len(vp)} > model hard max {prompt_hard}"
                )
            if not vp.rstrip().endswith("24fps"):
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: missing fps suffix"
                )
            ps = c.get("primary_subject")
            if ps and str(ps).startswith("Character_") and str(ps) not in vp:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                    f"primary_subject {ps} missing from visual_prompt"
                )
            ap = c.get("audio_payload") or {}
            if ap.get("delivery") not in (
                "spoken_on_camera",
                "voiceover_internal",
                "none",
            ):
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: bad delivery"
                )
            # Who talks + what they say must be explicit (single or multi-turn)
            sp = str(ap.get("speaker") or "").strip()
            deliv = str(ap.get("delivery") or "")
            dlg = str(ap.get("dialogue") or "").strip()
            turns = ap.get("dialogue_turns") if isinstance(ap.get("dialogue_turns"), list) else []
            vp_l = vp.lower()
            if turns and len(turns) >= 2:
                if "multi-speaker" not in vp_l and "dialogue turns" not in vp_l and "in order" not in vp_l:
                    # soft: still require each speaker token
                    missing = [
                        str(t.get("speaker") or "")
                        for t in turns
                        if str(t.get("speaker") or "")
                        and str(t.get("speaker")) not in vp
                    ]
                    if missing:
                        errs.append(
                            f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                            f"multi-turn clip should name speakers {missing} in visual_prompt"
                        )
            elif dlg and deliv in ("voiceover_internal", "spoken_on_camera") and sp and sp.lower() not in ("none", ""):
                if sp not in vp and "voiceover" not in vp_l:
                    errs.append(
                        f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                        f"speech clip should name speaker {sp} in visual_prompt"
                    )
                if "saying:" not in vp_l and "says:" not in vp_l and "saying " not in vp_l:
                    errs.append(
                        f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                        f"speech clip should attribute line for {sp} (saying: \"…\") in visual_prompt"
                    )
            if dur < 1 or dur > GROK_ABS_MAX:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: duration {dur}"
                )
            # Soft identity: if cast has 2+ on-screen and prompt only has one Character_*, warn
            if len(cast) >= 2:
                mentioned = sum(1 for t in cast if t in vp)
                if mentioned < min(2, len(cast)) and "also on screen" not in vp.lower():
                    errs.append(
                        f"S{sc.get('scene_number')}C{c.get('clip_number')}: "
                        f"visual_prompt should name multiple on-screen cast tokens {cast}"
                    )
        if isinstance(char_seeds, dict):
            for ck, sv in char_seeds.items():
                if not isinstance(sv, dict):
                    continue
                ph = str(sv.get("reference_image_placeholder") or "")
                if ph and ("/" in ph or "\\" in ph):
                    errs.append(
                        f"character_seed {ck}: reference_image_placeholder should be "
                        f"bare filename, got {ph!r}"
                    )
        if ssum != total:
            errs.append(
                f"S{sc.get('scene_number')}: total {total} != sum clips {ssum}"
            )
        mb = sc.get("music_bed") or {}
        msum = sum(
            int(x.get("duration_seconds") or 0)
            for x in (mb.get("song_structure") or [])
        )
        if (mb.get("song_structure") or []) and msum != total:
            errs.append(
                f"S{sc.get('scene_number')}: music sum {msum} != scene {total}"
            )
    return errs


def merge_into_blueprint(
    blueprint: Dict[str, Any], plan_scenes: List[Dict[str, Any]]
) -> Dict[str, Any]:
    """Replace matching scene clip plans in a full nickandme.clips.grok.json-style blueprint."""
    by_num = {int(s["scene_number"]): s for s in plan_scenes}
    out = json.loads(json.dumps(blueprint))  # deep copy
    for sc in out.get("scenes") or []:
        sn = int(sc.get("scene_number") or 0)
        if sn not in by_num:
            continue
        planned = by_num[sn]
        sc["veo_clips"] = planned["veo_clips"]
        sc["total_estimated_duration_seconds"] = planned[
            "total_estimated_duration_seconds"
        ]
        sc["music_bed"] = planned["music_bed"]
        sc["setting"] = planned.get("setting") or sc.get("setting")
        sc["lighting_continuity_token"] = planned.get(
            "lighting_continuity_token"
        ) or sc.get("lighting_continuity_token")
        sc["transition_type"] = planned.get("transition_type") or sc.get(
            "transition_type"
        )
        sc["scene_filename"] = planned.get("scene_filename") or sc.get(
            "scene_filename"
        )
    out["_stage2_grok"] = {
        "merged_scenes": sorted(by_num.keys()),
        "video_provider_profile": "grok",
    }
    return out


def parse_scene_range(spec: str) -> Optional[List[int]]:
    if not spec or spec.lower() in ("all", "*"):
        return None
    nums: set = set()
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-", 1)
            a, b = int(a), int(b)
            if a > b:
                a, b = b, a
            nums.update(range(a, b + 1))
        else:
            nums.add(int(part))
    return sorted(nums)


def main() -> int:
    ap = argparse.ArgumentParser(description="Stage 2 Grok shot planner")
    ap.add_argument(
        "--stage1",
        default=str(ROOT / "nickandme.scenes.json"),
        help="Stage 1 scene bible path",
    )
    ap.add_argument(
        "--out",
        default=str(ROOT / "nickandme.clips.grok.json"),
        help="Output Stage 2 Grok clip plan",
    )
    ap.add_argument(
        "--scenes",
        default="all",
        help="Scene filter: all | 1-2 | 1,3,5",
    )
    ap.add_argument(
        "--resolution",
        default="720p",
        help="Prompt suffix resolution (e.g. 480p or 720p)",
    )
    ap.add_argument(
        "--merge-into",
        default="",
        help="Optional path to full blueprint to merge planned scenes into (writes --merge-out)",
    )
    ap.add_argument(
        "--merge-out",
        default="",
        help="Where to write merged blueprint (default: overwrite --merge-into with .bak)",
    )
    args = ap.parse_args()

    stage1_path = Path(args.stage1)
    if not stage1_path.is_file():
        print(f"[Error] Stage 1 not found: {stage1_path}", file=sys.stderr)
        return 1

    stage1 = json.loads(stage1_path.read_text(encoding="utf-8"))
    if stage1.get("schema_version") not in ("stage1.v1", "stage1.v1.0"):
        print(
            f"[Warning] Unexpected schema_version={stage1.get('schema_version')!r}; continuing"
        )

    want = parse_scene_range(args.scenes)
    scenes_in = stage1.get("scenes") or []
    if want is not None:
        scenes_in = [s for s in scenes_in if int(s.get("scene_number") or 0) in set(want)]

    gpv = stage1.get("global_production_variables") or {}
    loc_seeds = gpv.get("location_seed_tokens") or {}
    char_seeds = gpv.get("character_seed_tokens") or {}
    # Normalize seed placeholders to bare filenames (automation: avoid double paths)
    if isinstance(char_seeds, dict):
        for _ck, _sv in char_seeds.items():
            if not isinstance(_sv, dict):
                continue
            ph = str(_sv.get("reference_image_placeholder") or "").replace("\\", "/")
            if ph and ("/" in ph or ph.startswith("assets")):
                _sv["reference_image_placeholder"] = Path(ph).name
    planned_scenes = [
        plan_scene(
            s,
            resolution=args.resolution.strip() or "720p",
            location_seeds=loc_seeds,
            character_seeds=char_seeds if isinstance(char_seeds, dict) else {},
        )
        for s in scenes_in
    ]

    plan: Dict[str, Any] = {
        "schema_version": "stage2.v1",
        "movie_title": stage1.get("movie_title"),
        "source_book_title": stage1.get("source_book_title"),
        "video_provider_profile": "grok",
        "global_production_variables": gpv,
        "scenes": planned_scenes,
        "stage2_meta": {
            "source_stage1": stage1_path.name,
            "resolution": args.resolution,
            "scene_filter": args.scenes,
            "clip_duration_policy": _duration_policy_from_config(),
            **{
                k: v
                for k, v in {
                    "prompt_soft_max": _prompt_limits_from_config().get("soft"),
                    "prompt_hard_max": _prompt_limits_from_config().get("hard"),
                    "prompt_limits_source": _prompt_limits_from_config().get("source"),
                    "prompt_limits_model": _prompt_limits_from_config().get("model"),
                    "prompt_limits_provider": _prompt_limits_from_config().get("provider"),
                }.items()
            },
        },
    }

    total_dur = sum(
        int(s.get("total_estimated_duration_seconds") or 0) for s in planned_scenes
    )
    total_clips = sum(len(s.get("veo_clips") or []) for s in planned_scenes)
    plan["stage2_meta"]["total_duration_seconds"] = total_dur
    plan["stage2_meta"]["total_clips"] = total_clips

    errs = validate_plan(plan)
    if errs:
        print("[Validate] issues:")
        for e in errs[:30]:
            print(" ", e)
        if len(errs) > 30:
            print(f"  ... +{len(errs)-30} more")
    else:
        print("[Validate] OK")

    out_path = Path(args.out)
    out_path.write_text(
        json.dumps(plan, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"[Success] Wrote {out_path}")
    print(f"  scenes={len(planned_scenes)} clips={total_clips} duration={total_dur}s")

    # Preview S1–S2 if present
    for sn in (1, 2):
        sc = next((x for x in planned_scenes if x.get("scene_number") == sn), None)
        if not sc:
            continue
        print(f"\n=== Scene {sn} ({sc.get('total_estimated_duration_seconds')}s) ===")
        for c in sc.get("veo_clips") or []:
            print(
                f"  C{c['clip_number']} {c['timestamp']} {c['veo_continuation_source']:16} "
                f"len={len(c['visual_prompt'])} | {(c['visual_prompt'])[:90]}…"
            )

    if args.merge_into:
        bp_path = Path(args.merge_into)
        if not bp_path.is_file():
            print(f"[Error] merge-into not found: {bp_path}", file=sys.stderr)
            return 1
        bp = json.loads(bp_path.read_text(encoding="utf-8"))
        merged = merge_into_blueprint(bp, planned_scenes)
        merge_out = Path(args.merge_out) if args.merge_out else bp_path
        if merge_out.resolve() == bp_path.resolve():
            bak = bp_path.with_suffix(bp_path.suffix + ".bak_pre_stage2_grok")
            bak.write_text(bp_path.read_text(encoding="utf-8"), encoding="utf-8")
            print(f"[Backup] {bak}")
        merge_out.write_text(
            json.dumps(merged, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        print(f"[Success] Merged Grok clip plan into {merge_out}")

    return 0 if not errs else 0  # still write; validation is advisory unless critical


if __name__ == "__main__":
    raise SystemExit(main())
