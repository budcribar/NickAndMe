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


def _beat_audio_delivery_speaker(beat: Dict[str, Any]) -> Tuple[str, str]:
    audio = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    delivery = str(
        (audio.get("delivery") if audio else None) or beat.get("delivery") or ""
    ).lower()
    speaker = str(
        (audio.get("speaker") if audio else None) or beat.get("speaker") or ""
    ).lower()
    return delivery, speaker


def _is_vo_beat(beat: Dict[str, Any]) -> bool:
    delivery, speaker = _beat_audio_delivery_speaker(beat)
    return delivery in (
        "voiceover_internal",
        "internal",
        "vo_internal",
        "thought",
        "thinking",
        "narration",
        "vo",
    ) or "narrator" in speaker


def _is_on_camera_speech_beat(beat: Dict[str, Any]) -> bool:
    delivery, speaker = _beat_audio_delivery_speaker(beat)
    return delivery == "spoken_on_camera" and "narrator" not in speaker


def _force_none(
    beat: Dict[str, Any],
    clip_index: int,
    *,
    prev_beat: Optional[Dict[str, Any]] = None,
    prev_location_id: Optional[str] = None,
    location_id: Optional[str] = None,
) -> bool:
    """
    When True → veo_continuation_source 'none' (fresh / ref-to-video).
    When False → may use extend_previous (last-frame continuity).

    Narrator VO no longer always forces none: VO→VO continuous action (dog walks to bushes
    then potties) should extend. Only force none for VO when the previous clip was
    on-camera speech (avoid frozen Mom mid-talk into a VO beat).
    """
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
    # Location change → hard cut
    if (
        prev_location_id
        and location_id
        and str(prev_location_id) != str(location_id)
    ):
        return True

    # VO after human on-camera speech → none (don't freeze Mom mid-lip-sync into VO)
    if _is_vo_beat(beat) and prev_beat and _is_on_camera_speech_beat(prev_beat):
        return True

    # VO after VO (or after silent) with continuous_from_previous → allow extend
    if _is_vo_beat(beat):
        if cont == "continuous_from_previous_beat" and ac in (
            "hold",
            "dialogue",
            "small_motion",
        ):
            return False
        # Default VO still prefers none unless Stage 1 marked continuous
        return cont != "continuous_from_previous_beat"

    ve = (beat.get("visual_event") or "").lower()
    if re.search(
        r"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket|wide shot|establishing|"
        r"flashback|back to present|cut to)\b",
        ve,
    ):
        return True
    return False


def _beat_duration_weight(beat: Dict[str, Any]) -> float:
    """
    Relative importance for clip length. Starts from Stage 1 time_weight, then
    nudges by action_class and spoken line length so equal weights still mix.
    """
    if not isinstance(beat, dict):
        return 1.0
    try:
        w = float(beat.get("time_weight") or 1.0)
    except (TypeError, ValueError):
        w = 1.0
    w = max(0.25, w)

    ac = str(beat.get("action_class") or "").lower()
    if ac == "big_action":
        w *= 1.4
    elif ac in ("dialogue", "small_motion"):
        w *= 1.15
    elif ac in ("hold",):
        w *= 0.85
    elif ac in ("establishing", "montage"):
        w *= 0.95
    elif ac in ("hard_cut", "flashback_enter", "flashback_exit"):
        w *= 1.05

    nested = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    dialogue = str(
        (nested.get("dialogue") if nested else None) or beat.get("dialogue") or ""
    ).strip()
    # Multi-turn: sum lines
    turns = beat.get("dialogue_turns") or beat.get("speech_turns")
    if isinstance(turns, list) and turns:
        parts = [
            str(t.get("dialogue") or t.get("line") or "").strip()
            for t in turns
            if isinstance(t, dict)
        ]
        if parts:
            dialogue = " ".join(parts)
    words = len(dialogue.split()) if dialogue else 0
    if words >= 28:
        w *= 1.35
    elif words >= 16:
        w *= 1.2
    elif words >= 8:
        w *= 1.08
    elif 0 < words <= 3:
        w *= 0.9

    delivery = str(
        (nested.get("delivery") if nested else None) or beat.get("delivery") or ""
    ).lower()
    if delivery == "voiceover_internal" and words >= 12:
        w *= 1.1  # book VO often needs a bit more airtime

    return max(0.25, w)


def _allocate_durations(
    beats: Sequence[Dict[str, Any]],
    target: int,
    *,
    policy: Optional[Dict[str, int]] = None,
) -> List[int]:
    """
    Allocate integer seconds per beat with **mixed lengths by default**.

    Design:
      - Base each clip near model default (e.g. 6–8s), not prefer_max.
      - Scale by time_weight + action_class + dialogue length.
      - Stage 1 duration_target is a soft budget: do NOT pad every clip to 10s
        just because the scene target is large (old behaviour).
      - High-weight / long-dialogue beats grow toward prefer_max first.
      - Hard per-clip cap stays ≤10s for Grok image/reference-to-video.

    Total equals the *effective* goal (may be less than Stage 1 target when that
    target would force uniform max-length padding).
    """
    pol = policy or _duration_policy_from_config()
    d_def = int(pol.get("default", GROK_DEFAULT))
    d_min = int(pol.get("prefer_min", GROK_MIN_CLIP))
    d_max = int(pol.get("prefer_max", GROK_MAX_CLIP))
    # Hard cap: Grok image/reference-to-video rejects >10s (HTTP 400).
    d_hard = min(int(pol.get("max", GROK_ABS_MAX)), GROK_MAX_CLIP, 10)
    d_max = min(d_max, d_hard)
    d_min = min(d_min, d_max)
    d_def = max(d_min, min(d_max, d_def))

    n = len(beats)
    if n == 0:
        return []

    beat_list = [b if isinstance(b, dict) else {} for b in beats]
    weights = [_beat_duration_weight(b) for b in beat_list]
    w_mean = (sum(weights) / n) if n else 1.0

    # Natural length: weight 1.0 (relative to mean) → d_def; high → d_max; low → d_min
    natural: List[int] = []
    for w in weights:
        rel = (w / w_mean) if w_mean > 0 else 1.0
        if rel >= 1.0:
            # rel 1 → d_def, rel ≥ ~1.8 → d_max
            t = min(1.0, (rel - 1.0) / 0.8)
            d = d_def + t * (d_max - d_def)
        else:
            # rel 1 → d_def, rel ≤ ~0.5 → d_min
            t = min(1.0, (1.0 - rel) / 0.5)
            d = d_def - t * (d_def - d_min)
        natural.append(int(round(d)))
    natural = [max(d_min, min(d_max, d)) for d in natural]

    # Soft ceiling on *average* length so equal-weight scenes don't all become d_max
    # when Stage 1 budget is huge (e.g. 50s / 4 beats → old code forced 10,10,10,10).
    soft_avg_max = min(d_max, d_def + max(1, (d_max - d_def + 1) // 2))
    # Beats clearly heavier than average may still use full d_max
    per_cap = [
        d_max if weights[i] >= w_mean * 1.2 else soft_avg_max for i in range(n)
    ]
    natural = [min(natural[i], per_cap[i]) for i in range(n)]
    # Ensure heavy beats aren't stuck at soft cap if weight demands more
    for i in range(n):
        if weights[i] >= w_mean * 1.2:
            # re-apply high-weight path up to d_max
            rel = weights[i] / w_mean
            t = min(1.0, (rel - 1.0) / 0.8)
            natural[i] = max(natural[i], int(round(d_def + t * (d_max - d_def))))
            natural[i] = max(d_min, min(d_max, natural[i]))

    preferred_total = sum(natural)
    min_total = n * d_min
    # Max we will grow to: heavy → d_max, others → soft_avg_max (not all d_max)
    stretch_cap_total = sum(per_cap)
    stretch_cap_total = max(preferred_total, min(n * d_max, stretch_cap_total))

    stage1 = int(target or preferred_total)
    stage1 = max(GROK_SCENE_MIN, min(GROK_SCENE_MAX, stage1))
    if stage1 < min_total:
        stage1 = min_total

    # Soft goal: honor Stage 1 when close; never force-fill to n*d_max
    if stage1 <= preferred_total:
        goal = max(min_total, stage1)
    else:
        # Allow modest growth toward Stage 1, but stop at stretch_cap
        goal = min(stage1, stretch_cap_total)
        # If Stage 1 is only slightly above preferred, snap to preferred (keep natural mix)
        if stage1 <= preferred_total + max(2, n // 2):
            goal = preferred_total

    goal = max(min_total, min(stretch_cap_total, goal))

    durs = list(natural)
    # Rank indices: grow high-weight first; shrink low-weight first
    grow_order = sorted(range(n), key=lambda i: (-weights[i], i))
    shrink_order = sorted(range(n), key=lambda i: (weights[i], i))

    def _fix_sum(goal_total: int) -> None:
        nonlocal durs
        diff = goal_total - sum(durs)
        guard = 0
        while diff != 0 and guard < 10000:
            guard += 1
            if diff > 0:
                progressed = False
                for idx in grow_order:
                    cap = per_cap[idx]
                    if durs[idx] < cap:
                        durs[idx] += 1
                        diff -= 1
                        progressed = True
                        break
                if not progressed:
                    # last resort: allow full d_max
                    for idx in grow_order:
                        if durs[idx] < d_max:
                            durs[idx] += 1
                            diff -= 1
                            progressed = True
                            break
                if not progressed:
                    break
            else:
                progressed = False
                for idx in shrink_order:
                    if durs[idx] > d_min:
                        durs[idx] -= 1
                        diff += 1
                        progressed = True
                        break
                if not progressed:
                    break

    _fix_sum(goal)

    # Mild variety insurance: if everything still equal and n>=3, nudge ends
    if n >= 3 and len(set(durs)) == 1 and d_max > d_min:
        mid = durs[0]
        if mid > d_min:
            durs[0] = max(d_min, mid - 1)
        if mid < d_max and weights[-1] >= w_mean * 0.95:
            # give last beat a touch more if dialogue/action allows via per_cap
            durs[-1] = min(per_cap[-1], mid + 1)
        # rebalance total if we drifted
        _fix_sum(sum(durs) if abs(sum(durs) - goal) > n else goal)

    return durs


# --- Generalized sticky wardrobe (any character, any item) --------------------
# Tracks always-on items from visual_lock / "always wears …" plus items put on
# mid-scene so later clips restate them (location cuts otherwise drop props).

_WARDROBE_CORE = (
    r"pajamas?|pjs|pyjamas?|nightshirts?|nightgowns?|onesies?|"
    r"hats?|caps?|beanies?|hoods?|"
    r"collars?|leashes?|bandanas?|scar(?:f|ves)|bow\s*ties?|ties?|"
    r"jackets?|coats?|cardigans?|sweaters?|hoodies?|vests?|robes?|"
    r"shirts?|blouses?|dresses?|skirts?|pants|trousers|jeans|"
    r"glasses|spectacles|sunglasses|"
    r"boots?|shoes|sneakers|gloves?|socks?|aprons?|costumes?|uniforms?|raincoats?"
)

_ITEM_PHRASE_RE = re.compile(
    rf"((?:[\w''-]{{1,24}}\s+){{0,5}}(?:{_WARDROBE_CORE})"
    rf"(?:\s+(?:matching\s+(?:his|her|their)\s+\w+|on\s+(?:the\s+)?head|"
    rf"over\s+(?:the\s+)?same\s+\w+)){{0,1}})",
    re.I,
)

_CHAR_TOKEN_RE = re.compile(r"Character_[A-Za-z0-9_]+")

_ALWAYS_ON_RES = (
    re.compile(
        rf"always\s+(?:wearing|wears)\s+([^.!;]{{3,90}})",
        re.I,
    ),
    re.compile(
        rf"(?:signature|never\s+without)\s+([^.!;]{{3,90}})",
        re.I,
    ),
    re.compile(
        rf"((?:[\w''-]{{1,24}}\s+){{0,4}}(?:{_WARDROBE_CORE}))\s+always\s+on",
        re.I,
    ),
)

_WEAR_SCOPED_RES = (
    re.compile(
        rf"({_CHAR_TOKEN_RE.pattern})\s+(?:now\s+)?wears\s+([^.!;]+)",
        re.I,
    ),
    re.compile(
        rf"({_CHAR_TOKEN_RE.pattern})\s+(?:is\s+)?(?:still\s+)?wearing\s+([^.!;]+)",
        re.I,
    ),
    re.compile(
        rf"({_CHAR_TOKEN_RE.pattern})\s+puts?\s+on\s+([^.!;]+)",
        re.I,
    ),
    re.compile(
        rf"({_CHAR_TOKEN_RE.pattern}),\s*still\s+in\s+([^,.;]+)",
        re.I,
    ),
    re.compile(
        rf"({_CHAR_TOKEN_RE.pattern})\s+in\s+(?:the\s+)?(?:same\s+)?"
        rf"((?:[\w''-]{{1,24}}\s+){{0,4}}(?:{_WARDROBE_CORE})[^,.;]*)",
        re.I,
    ),
)

_WEAR_DEFAULT_RES = (
    re.compile(rf"(?:now\s+)?wears\s+([^.!;]{{4,90}})", re.I),
    re.compile(rf"(?:is\s+)?(?:still\s+)?wearing\s+([^.!;]{{4,90}})", re.I),
    re.compile(rf"puts?\s+on\s+([^.!;]{{4,90}})", re.I),
    re.compile(
        rf"\bin\s+(?:the\s+)?(?:same\s+)?"
        rf"((?:[\w''-]{{1,24}}\s+){{0,4}}(?:{_WARDROBE_CORE})[^,.;]*)",
        re.I,
    ),
)

_REMOVE_SCOPED_RES = (
    re.compile(
        rf"({_CHAR_TOKEN_RE.pattern})\s+(?:removes?|takes?\s+off)\s+([^.!;]+)",
        re.I,
    ),
    re.compile(
        rf"({_CHAR_TOKEN_RE.pattern})\s+without\s+(?:the\s+|his\s+|her\s+)?"
        rf"((?:[\w''-]{{1,24}}\s+){{0,3}}(?:{_WARDROBE_CORE}))",
        re.I,
    ),
)

_REMOVE_DEFAULT_RES = (
    re.compile(rf"(?:removes?|takes?\s+off)\s+([^.!;]{{3,60}})", re.I),
    re.compile(
        rf"\bwithout\s+(?:the\s+|his\s+|her\s+)?"
        rf"((?:[\w''-]{{1,24}}\s+){{0,3}}(?:{_WARDROBE_CORE}))",
        re.I,
    ),
    re.compile(
        rf"\bno\s+(?:longer\s+)?(?:wearing\s+)?"
        rf"((?:[\w''-]{{1,24}}\s+){{0,3}}(?:{_WARDROBE_CORE}))",
        re.I,
    ),
)


def _normalize_wardrobe_item(s: str) -> str:
    t = re.sub(r"\s+", " ", (s or "").strip(" .,;:\"'"))
    t = re.sub(
        r"^(?:always\s+)?(?:still\s+)?(?:wearing|wears|wear)\s+",
        "",
        t,
        flags=re.I,
    )
    # Strip leading filler repeatedly (Always soft blue cardigan → blue cardigan)
    for _ in range(4):
        t2 = re.sub(
            r"^(?:always|still|the|a|an|his|her|their|its|same|own|this|that|soft)\s+",
            "",
            t,
            flags=re.I,
        )
        if t2 == t:
            break
        t = t2
    # Drop trailing clause junk / location fluff
    t = re.split(
        r"\b(?:that|which|who|while|when|as\s+he|as\s+she|matching\s+coat)\b",
        t,
        maxsplit=1,
    )[0].strip(" .,;:")
    t = re.sub(r"\s+on\s+(?:the\s+)?head$", "", t, flags=re.I).strip()
    return t[:72]


def _wardrobe_item_key(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", (s or "").lower())


def _wardrobe_core_noun(s: str) -> str:
    m = re.search(rf"({_WARDROBE_CORE})\b", s or "", re.I)
    if not m:
        return ""
    w = m.group(1).lower()
    # Light plural fold for sticky matching (keep invariant plurals)
    if w in ("glasses", "sunglasses", "pants", "trousers", "jeans", "pjs"):
        return w
    if w.endswith("ies") and len(w) > 4:
        return w[:-3] + "y"
    if w.endswith("s") and not w.endswith("ss") and len(w) > 3:
        return w[:-1]
    return w


def _extract_wardrobe_items(text: str) -> List[str]:
    """Pull wardrobe noun-phrases from free text."""
    out: List[str] = []
    seen: set = set()
    for m in _ITEM_PHRASE_RE.finditer(text or ""):
        item = _normalize_wardrobe_item(m.group(1))
        if len(item) < 3:
            continue
        key = _wardrobe_item_key(item)
        if not key or key in seen:
            continue
        # Skip pure time-of-day false positives ("night" alone etc.) — core must hit
        if not _wardrobe_core_noun(item):
            continue
        seen.add(key)
        out.append(item)
    return out


def _dedupe_wardrobe_items(items: Sequence[str]) -> List[str]:
    out: List[str] = []
    seen: set = set()
    for it in items:
        item = _normalize_wardrobe_item(it)
        if not item:
            continue
        key = _wardrobe_item_key(item)
        # Prefer longer phrase for same core noun
        core = _wardrobe_core_noun(item)
        if core:
            # replace shorter same-core entry
            replaced = False
            for i, prev in enumerate(out):
                if _wardrobe_core_noun(prev) == core:
                    if len(item) > len(prev):
                        out[i] = item
                        seen.discard(_wardrobe_item_key(prev))
                        seen.add(key)
                    replaced = True
                    break
            if replaced:
                continue
        if key in seen:
            continue
        seen.add(key)
        out.append(item)
    return out


def _coerce_wardrobe_phrases(val: Any, *, max_items: int = 12) -> List[str]:
    """Accept Stage 1 free-text wardrobe lists (opaque phrases — no item enum)."""
    if val is None:
        return []
    if isinstance(val, str):
        parts = re.split(r"\s+and\s+|[,;|/]", val)
        raw = [p.strip() for p in parts if p and str(p).strip()]
    elif isinstance(val, (list, tuple)):
        raw = [str(x).strip() for x in val if x is not None and str(x).strip()]
    else:
        return []
    out: List[str] = []
    seen: set = set()
    for s in raw:
        item = _normalize_wardrobe_item(s)
        if len(item) < 2:
            continue
        key = _wardrobe_item_key(item)
        if not key or key in seen:
            continue
        seen.add(key)
        out.append(item[:80])
        if len(out) >= max_items:
            break
    return out


def _always_on_wardrobe_from_seed(seed: Dict[str, Any]) -> List[str]:
    """
    Always-on items for a character.

    Prefer Stage 1 structured `wardrobe_always` (free-text phrases). Fall back to
    visual_lock / description heuristics only for legacy Stage 1 files.
    """
    structured = _coerce_wardrobe_phrases(seed.get("wardrobe_always"))
    if structured:
        return structured

    vl = str(seed.get("visual_lock") or "")
    desc = str(seed.get("description") or "")
    blob = f"{vl}. {desc}"
    found: List[str] = []
    for rx in _ALWAYS_ON_RES:
        for m in rx.finditer(blob):
            chunk = m.group(1)
            items = _extract_wardrobe_items(chunk)
            if items:
                found.extend(items)
            else:
                norm = _normalize_wardrobe_item(chunk)
                if norm and ( _wardrobe_core_noun(norm) or len(norm) >= 4):
                    found.append(norm)
    for chunk in re.split(r"[;.|]", vl):
        cl = chunk.lower()
        if any(
            x in cl
            for x in (
                "once ",
                "after ",
                "only at",
                "bedtime only",
                "when in",
                "bare fur only before",
                "before pajamas",
            )
        ):
            continue
        if re.search(r"\balways\b|\bsignature\b|\bnever\s+bare", cl):
            found.extend(_extract_wardrobe_items(chunk))
            # Also accept free phrases without known core nouns (legacy soft)
            norm = _normalize_wardrobe_item(chunk)
            if norm and len(norm) >= 6 and not _extract_wardrobe_items(chunk):
                found.append(norm)
    return _dedupe_wardrobe_items(found)


def _scene_has_structured_wardrobe(scene: Optional[Dict[str, Any]]) -> bool:
    if not scene:
        return False
    wbc = scene.get("wardrobe_by_character")
    if isinstance(wbc, dict) and any(_coerce_wardrobe_phrases(v) for v in wbc.values()):
        return True
    for beat in scene.get("story_beats") or []:
        if not isinstance(beat, dict):
            continue
        if _coerce_wardrobe_phrases(beat.get("wardrobe_put_on")) or _coerce_wardrobe_phrases(
            beat.get("wardrobe_remove")
        ):
            return True
    return False


def _init_scene_wardrobe_state(
    cast: Sequence[str],
    character_seeds: Optional[Dict[str, Any]] = None,
    *,
    scene: Optional[Dict[str, Any]] = None,
) -> Dict[str, List[str]]:
    """
    Per-character ordered sticky items for one scene.

    Primary: Stage 1 structured lists (wardrobe_always + wardrobe_by_character).
    Fallback: free-text heuristics on wardrobe_notes when structured scene data is absent.
    """
    seeds = character_seeds or {}
    state: Dict[str, List[str]] = {}
    for tok in cast:
        if not str(tok).startswith("Character_"):
            continue
        if "narrator" in str(tok).lower():
            continue
        seed = seeds.get(tok) if isinstance(seeds, dict) else None
        items: List[str] = []
        if isinstance(seed, dict):
            items = _always_on_wardrobe_from_seed(seed)
        state[str(tok)] = list(items)

    if scene:
        wbc = scene.get("wardrobe_by_character")
        if isinstance(wbc, dict):
            for tok, raw in wbc.items():
                token = str(tok).strip()
                if not token.startswith("Character_") or "narrator" in token.lower():
                    continue
                phrases = _coerce_wardrobe_phrases(raw)
                if not phrases:
                    continue
                # Ensure key exists even if not in cast yet
                cur = list(state.get(token) or [])
                state[token] = _dedupe_wardrobe_items(cur + phrases)

        # Legacy prose path only when Stage 1 did not supply structured scene wardrobe
        if not _scene_has_structured_wardrobe(scene):
            notes = " ".join(
                str(scene.get(k) or "")
                for k in ("wardrobe_notes", "setting", "summary", "continuity_notes")
            )
            state = _apply_wardrobe_text(
                state,
                notes,
                default_chars=[c for c in cast if "narrator" not in c.lower()],
            )
    return state


def _remove_items_from_list(items: List[str], mention: str) -> List[str]:
    cores = {_wardrobe_core_noun(x) for x in _extract_wardrobe_items(mention)}
    if not cores:
        core = _wardrobe_core_noun(mention)
        if core:
            cores = {core}
    if not cores:
        return items
    return [it for it in items if _wardrobe_core_noun(it) not in cores]


def _add_items_to_char(
    state: Dict[str, List[str]], char: str, mention: str
) -> None:
    if not char or "narrator" in char.lower():
        return
    # Prefer whole Stage 1 phrases; fall back to noun extraction for legacy prose
    extracted = _coerce_wardrobe_phrases([mention])
    if not extracted:
        extracted = _extract_wardrobe_items(mention)
    if not extracted:
        norm = _normalize_wardrobe_item(mention)
        if norm and (_wardrobe_core_noun(norm) or len(norm) >= 4):
            extracted = [norm]
    if not extracted:
        return
    cur = list(state.get(char) or [])
    state[char] = _dedupe_wardrobe_items(cur + extracted)


def _chars_named_in_text(
    text: str, candidates: Sequence[str]
) -> List[str]:
    """Prefer cast members whose token/short name appears near wardrobe prose."""
    blob = (text or "").lower()
    hit: List[str] = []
    for c in candidates:
        if not c:
            continue
        if c.lower() in blob:
            hit.append(c)
            continue
        short = c.replace("Character_", "").replace("_", " ").strip()
        if short and short.lower() in blob:
            hit.append(c)
            continue
        first = short.split()[0] if short else ""
        if first and len(first) >= 3 and re.search(rf"\b{re.escape(first.lower())}\b", blob):
            hit.append(c)
    return hit


def _apply_wardrobe_text(
    state: Dict[str, List[str]],
    text: str,
    *,
    default_chars: Optional[Sequence[str]] = None,
) -> Dict[str, List[str]]:
    """Update sticky wardrobe from free text (removals then additions)."""
    blob = str(text or "")
    if not blob.strip():
        return state
    out = {k: list(v) for k, v in state.items()}
    defaults = [c for c in (default_chars or []) if c and "narrator" not in c.lower()]

    def _targets_for_span(span_text: str) -> List[str]:
        named = _chars_named_in_text(span_text, defaults or list(out.keys()))
        if named:
            return named
        # Single default only — do not paint every on-screen character
        if defaults:
            return [defaults[0]]
        keys = [k for k in out.keys() if "narrator" not in k.lower()]
        return keys[:1]

    # Removals (scoped)
    for rx in _REMOVE_SCOPED_RES:
        for m in rx.finditer(blob):
            char, mention = m.group(1), m.group(2)
            out[char] = _remove_items_from_list(list(out.get(char) or []), mention)

    # Bare-fur / undress defaults (animals)
    if re.search(r"\bbare\s+fur\s+only\b|\bnatural\s+fur\s+only\b|\bdaytime\s+fur\b", blob, re.I):
        clothing_cores = {
            "pajama",
            "pj",
            "pyjama",
            "nightshirt",
            "nightgown",
            "onesie",
            "jacket",
            "coat",
            "sweater",
            "cardigan",
            "hoodie",
            "robe",
            "shirt",
            "dress",
            "costume",
            "uniform",
        }
        for ch in _targets_for_span(blob) or list(out.keys()):
            out[ch] = [
                it
                for it in (out.get(ch) or [])
                if _wardrobe_core_noun(it) not in clothing_cores
            ]

    for rx in _REMOVE_DEFAULT_RES:
        for m in rx.finditer(blob):
            mention = m.group(1)
            for ch in _targets_for_span(blob[max(0, m.start() - 40) : m.end() + 20]):
                out[ch] = _remove_items_from_list(list(out.get(ch) or []), mention)

    # Additions (scoped)
    for rx in _WEAR_SCOPED_RES:
        for m in rx.finditer(blob):
            char, mention = m.group(1), m.group(2)
            _add_items_to_char(out, char, mention)

    # Unscoped wear → named character or primary only
    for rx in _WEAR_DEFAULT_RES:
        for m in rx.finditer(blob):
            start = m.start()
            pre = blob[max(0, start - 48) : start]
            if _CHAR_TOKEN_RE.search(pre):
                continue
            mention = m.group(1)
            ctx = blob[max(0, start - 40) : m.end() + 20]
            for ch in _targets_for_span(ctx):
                _add_items_to_char(out, ch, mention)

    return out


def _update_wardrobe_from_beat(
    state: Dict[str, List[str]],
    beat: Dict[str, Any],
    scene: Dict[str, Any],
    *,
    cast: Optional[Sequence[str]] = None,
) -> Dict[str, List[str]]:
    primary = str(beat.get("primary_subject") or "").strip()
    defaults: List[str] = []
    if primary.startswith("Character_") and "narrator" not in primary.lower():
        defaults.append(primary)
    for c in cast or scene.get("characters_on_screen") or []:
        cs = str(c)
        if cs not in defaults and cs.startswith("Character_") and "narrator" not in cs.lower():
            defaults.append(cs)

    out = {k: list(v) for k, v in state.items()}

    # Stage 1 structured deltas (preferred — free-text phrases, no item enum)
    put_on = _coerce_wardrobe_phrases(beat.get("wardrobe_put_on"), max_items=8)
    remove = _coerce_wardrobe_phrases(beat.get("wardrobe_remove"), max_items=8)
    target = defaults[0] if defaults else ""
    if target:
        if remove:
            cur = list(out.get(target) or [])
            for phrase in remove:
                cur = _remove_items_from_list(cur, phrase)
                # Also drop exact/fuzzy phrase matches without known core noun
                pk = _wardrobe_item_key(phrase)
                cur = [it for it in cur if _wardrobe_item_key(it) != pk]
            out[target] = cur
        for phrase in put_on:
            _add_items_to_char(out, target, phrase)

    # Legacy prose scan only when Stage 1 has no structured wardrobe for this project scene
    if not _scene_has_structured_wardrobe(scene) and not put_on and not remove:
        wear_blob = " ".join(
            str(x or "")
            for x in (
                beat.get("visual_event"),
                beat.get("intent"),
                beat.get("blocking_notes"),
            )
        )
        out = _apply_wardrobe_text(out, wear_blob, default_chars=defaults)
    return out


def _item_mentioned_in_prompt(item: str, prompt: str) -> bool:
    pl = (prompt or "").lower()
    il = (item or "").lower()
    if il and il in pl:
        return True
    core = _wardrobe_core_noun(item)
    if core and re.search(rf"\b{re.escape(core)}s?\b", pl):
        return True
    # multi-word free phrases (structured Stage 1 items with no known core noun)
    toks = [t for t in re.split(r"\s+", il) if len(t) > 2][-2:]
    if toks and all(t in pl for t in toks):
        return True
    if len(toks) == 1 and toks[0] in pl:
        return True
    return False


def _wardrobe_continuity_clause(
    state: Dict[str, List[str]],
    *,
    cast_on_clip: Sequence[str],
    clip_index: int,
    visual_prompt: str,
    primary_subject: str = "",
) -> str:
    """
    Restate sticky wardrobe so models do not drop props after cuts / location changes.
    Clip 0: only items missing from the prompt. Later clips: items not already clear
    in the prompt (always restate if any sticky item is missing — e.g. PJs after a cut).
    Prefer primary_subject first so hero wardrobe wins limited prompt budget.
    """
    bits: List[str] = []
    ordered = list(cast_on_clip)
    ps = (primary_subject or "").strip()
    if ps in ordered:
        ordered = [ps] + [c for c in ordered if c != ps]
    for tok in ordered:
        if not str(tok).startswith("Character_") or "narrator" in str(tok).lower():
            continue
        items = list(state.get(tok) or [])
        if not items:
            continue
        missing = [it for it in items if not _item_mentioned_in_prompt(it, visual_prompt)]
        if clip_index == 0:
            need = missing
        else:
            # Later clips: restate anything missing; if all present, skip
            need = missing if missing else []
            # After location/setup change, lightly restate primary's full set if none missing
            # but prompt lacks "still wearing" (identity cue alone is weak for mid-scene dress)
            if not need and tok == ps:
                weak = [
                    it
                    for it in items
                    if _wardrobe_core_noun(it)
                    not in ("hat", "cap", "beanie")  # usually always-on via identity
                    and not re.search(
                        rf"still\s+(?:wearing|in).{{0,40}}{_wardrobe_core_noun(it)}",
                        visual_prompt or "",
                        re.I,
                    )
                ]
                # If a non-hat sticky item is only weakly present, force restatement
                need = weak[:3] if weak else []
        if not need:
            continue
        phrase = "; ".join(need[:4])
        bits.append(
            f"WARDROBE CONTINUITY: {tok} is STILL wearing ({phrase}) — "
            f"do not remove, swap, or drop these items mid-scene"
        )
        # Budget: hero + at most one support
        if len(bits) >= 2:
            break
    return ". ".join(bits)


def _wardrobe_negative_extras(state: Dict[str, List[str]], cast: Sequence[str]) -> str:
    """Generic negatives from sticky phrases (works for any free-text item)."""
    frags: List[str] = []
    seen: set = set()
    for tok in cast:
        for it in state.get(tok) or []:
            core = _wardrobe_core_noun(it)
            # Phrase tail for unknown item types (collar tag, cast, badge, …)
            words = [w for w in re.split(r"\s+", it) if w]
            tail = " ".join(words[-2:]) if len(words) >= 2 else (words[0] if words else it)
            key = core or _wardrobe_item_key(tail)
            if not key or key in seen:
                continue
            seen.add(key)
            label = core or tail
            frags.append(f"without {label}")
            frags.append(f"removing {label}")
            if core in ("pajama", "pj", "pyjama", "nightshirt", "onesie"):
                frags.append("bare fur only")
            if core == "hat":
                frags.append("bare-headed")
    return ", ".join(frags[:12])


def _neg_extras(beat: Dict[str, Any]) -> str:
    extras: List[str] = []
    nested = beat.get("audio") if isinstance(beat.get("audio"), dict) else {}
    delivery = str(
        (nested.get("delivery") if nested else None) or beat.get("delivery") or ""
    ).lower()
    speaker = str(
        (nested.get("speaker") if nested else None) or beat.get("speaker") or ""
    ).lower()
    # Narrator / internal VO: ban on-screen lip-sync (Mom/dog especially)
    if delivery in (
        "voiceover_internal",
        "internal",
        "vo_internal",
        "narration",
        "vo",
    ) or "narrator" in speaker:
        extras.extend(
            [
                "lip-sync",
                "talking mouth",
                "speaking lips",
                "dog talking",
                "animal mouthing words",
                "mom speaking",
                "character talking on camera",
                "dual dialogue",
            ]
        )
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
        vlock = str(seed.get("visual_lock") or "").strip()
        if not desc and not vlock:
            continue
        # Prefer wardrobe/props lock (hat etc.) + short identity
        short = re.sub(r"\s+", " ", desc or vlock)
        if len(short) > max_chars:
            short = short[: max_chars - 1].rsplit(" ", 1)[0] + "…"
        cue = f"{tok} ({short})"
        if vlock:
            vl = re.sub(r"\s+", " ", vlock)
            if len(vl) > 48:
                vl = vl[:47].rsplit(" ", 1)[0] + "…"
            if vl.lower() not in short.lower():
                cue += f" [{vl}]"
        elif re.search(r"\bhat\b", short, re.I):
            cue += " [wearing signature hat]"
        bits.append(cue)
    if not bits:
        return ""
    return "Same identity as locked refs: " + "; ".join(bits)


def _dialogue_quote_for_prompt(dialogue: str, max_len: int = 90) -> str:
    """
    Compact spoken line for visual_prompt attribution.

    Avoid nested double-quotes that break wrappers like saying: \"...\".
    Use <<...>> form for the attributed line when quotes/apostrophes appear.
    """
    d = re.sub(r"\s+", " ", (dialogue or "").strip())
    if len(d) >= 2 and d[0] in "\"'“”‘’" and d[-1] in "\"'“”‘’":
        d = d[1:-1].strip()
    for a, b in (
        ("“", "'"),
        ("”", "'"),
        ("„", "'"),
        ("«", "'"),
        ("»", "'"),
        ("‘", "'"),
        ("’", "'"),
        ('"', "'"),
    ):
        d = d.replace(a, b)
    if len(d) > max_len:
        d = d[: max_len - 1].rsplit(" ", 1)[0] + "…"
    return d


def _speech_line_delim(dialogue: str, max_len: int = 100) -> str:
    """Safe line delimiters (no nested quote breakage)."""
    line = _dialogue_quote_for_prompt(dialogue, max_len=max_len)
    if not line:
        return ""
    # << >> survives Mom said / Buster's / 'quoted' book speech
    return f"saying the words between << and >>: <<{line}>>"


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
    on_screen: Optional[List[str]] = None,
) -> List[str]:
    """
    Explicit who-talks + what-they-say for Grok.

    Multi-speaker banter stays on ONE clip via dialogue_turns (ordered).
    Split into separate clips only when Stage 2 chooses hard cuts / new setups.

    For narrator VO: name every on-screen Character_* as NO lip-sync (Mom/dog especially).
    """
    cast = [
        t
        for t in (on_screen or [])
        if str(t).startswith("Character_") and "narrator" not in str(t).lower()
    ]

    if turns and len(turns) >= 2:
        bits: List[str] = [
            "MULTI-SPEAKER DIALOGUE in order on this continuous take "
            "(voices bounce; do not merge lines into one speaker)"
        ]
        for i, t in enumerate(turns, 1):
            sp = str(t.get("speaker") or "").strip()
            raw_line = str(t.get("dialogue") or "")
            line_bit = _speech_line_delim(raw_line, max_len=70)
            deliv = str(t.get("delivery") or "spoken_on_camera").lower()
            if not sp or not line_bit:
                continue
            if deliv == "voiceover_internal" or "narrator" in sp.lower():
                bits.append(f"{i}) OFF-CAMERA {sp} VO {line_bit}")
            else:
                bits.append(f"{i}) {sp} ON CAMERA lip-syncs {line_bit}")
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
    line_bit = _speech_line_delim(dialogue) if dialogue else ""
    out: List[str] = []
    if deliv == "voiceover_internal" or "narrator" in sp.lower():
        if line_bit:
            out.append(
                f"OFF-CAMERA VOICEOVER by {sp} only {line_bit} "
                f"({sp} is NOT on screen and is the ONLY voice)"
            )
        else:
            out.append(
                f"OFF-CAMERA VOICEOVER by {sp} only ({sp} is NOT on screen)"
            )
        # Name each on-screen body — models latch onto "Mom said" and lip-sync Mom
        if cast:
            named = ", ".join(cast[:5])
            out.append(
                f"{named}: lips and snouts FROZEN CLOSED (react/listen only) — "
                f"NO lip-sync, NO speaking, NO mouthing the VO, NO animal talking"
            )
        else:
            out.append(
                "all on-screen mouths/snouts FROZEN CLOSED — VO is not lip-synced conversation"
            )
        # When VO text mentions Mom/Dad said or thoughts in dog's head, restate
        dlg_l = (dialogue or "").lower()
        if re.search(r"\b(said|says|mom|dad|head|think)\b", dlg_l):
            out.append(
                "book-style storytelling only: quoted 'Mom said…' or thoughts in a character's "
                "head are still Narrator VO — never Mom/Dad/dog on-camera speech"
            )
    elif deliv == "spoken_on_camera" and "narrator" not in sp.lower():
        if line_bit:
            out.append(
                f"ONLY {sp} speaks on camera and lip-syncs {line_bit}; "
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


def _is_high_action_beat(beat: Dict[str, Any]) -> bool:
    ac = str(beat.get("action_class") or "").lower()
    if ac in ("big_action", "montage"):
        return True
    blob = " ".join(
        str(beat.get(k) or "")
        for k in ("visual_event", "intent", "blocking_notes")
    ).lower()
    return bool(
        re.search(
            r"\b(bound|bounding|jump|jumping|race|racing|chase|chasing|"
            r"run|running|sprint|leap|leaping|dash|dashing|spin|spinning)\b",
            blob,
        )
    )


def _high_action_wardrobe_prefix(
    primary: str,
    wardrobe_state: Optional[Dict[str, List[str]]],
    character_seeds: Optional[Dict[str, Any]] = None,
) -> str:
    """
    'Character_X wearing (hat; collar) securely' — prepended before action verbs
    so motion clips do not strip always-on props.
    """
    if not primary or not str(primary).startswith("Character_"):
        return ""
    items: List[str] = list((wardrobe_state or {}).get(primary) or [])
    if not items and character_seeds:
        seed = (character_seeds or {}).get(primary)
        if isinstance(seed, dict):
            items = _always_on_wardrobe_from_seed(seed)
    items = [it for it in items if it][:3]
    if not items:
        return ""
    phrase = "; ".join(items)
    return f"{primary} wearing ({phrase}) securely"


_OR_STOPWORDS = {
    "a",
    "an",
    "the",
    "and",
    "of",
    "to",
    "in",
    "on",
    "at",
    "for",
    "his",
    "her",
    "their",
    "he",
    "she",
    "it",
    "they",
    "with",
    "from",
    "into",
    "across",
    "how",
    "very",
    "hard",
    "have",
    "has",
    "will",
    "he'll",
    "she'll",
    "they'll",
    "dream",
    "dreams",
    "of",
    "or",
}


def _content_tokens(text: str) -> set:
    """Distinctive content words for overlap scoring (language-agnostic enough)."""
    toks = re.findall(r"[a-zA-Z']{3,}", (text or "").lower())
    return {t for t in toks if t not in _OR_STOPWORDS and not t.startswith("character_")}


def _split_or_alternatives(*texts: str) -> List[str]:
    """
    Extract exclusive alternatives linked by 'or' / ', Or' in VO or stage notes.

    General: works for any pair/list (environments, actions, objects) — not a fixed
    catalog of story keywords. Returns cleaned alternative phrases (2+).

    Each text is processed separately so intent/summary is not glued onto VO alternatives.
    """
    alts: List[str] = []
    seen_l: set = set()

    def _add(part: str) -> None:
        p = re.sub(r"\s+", " ", (part or "")).strip(" .,;:\"'")
        # Drop leading filler common in rhyme / prose
        p = re.sub(
            r"^(?:he'?ll|she'?ll|they'?ll|he|she|they|and)\s+",
            "",
            p,
            flags=re.I,
        )
        p = re.sub(
            r"^(?:have|has|had)\s+a\s+dream\s+of\s+",
            "",
            p,
            flags=re.I,
        )
        p = re.sub(r"^(?:a\s+dream\s+of\s+)", "", p, flags=re.I)
        # Trim trailing subordinate clauses after alternative
        p = re.split(r"\s+/\s+|\s+and how\b|\s+because\b", p, maxsplit=1, flags=re.I)[
            0
        ].strip(" .,;:\"'")
        if len(p) < 4:
            return
        key = p.lower()
        if key in seen_l:
            return
        seen_l.add(key)
        alts.append(p[:90])

    for text in texts:
        if not text or not re.search(r"\bor\b", str(text), re.I):
            continue
        # Line-by-line within each field
        for line in re.split(r"[\n/;]+", str(text)):
            line = line.strip()
            if not re.search(r"\bor\b", line, re.I):
                continue
            parts = re.split(r"\s*,?\s+\bor\b\s+", line, flags=re.I)
            if len(parts) < 2:
                continue
            for part in parts:
                _add(part)

    if len(alts) < 2:
        return []
    return alts[:4]


def _exclusive_or_environment_lock(
    beat: Dict[str, Any], scene: Dict[str, Any]
) -> Tuple[str, List[str]]:
    """
    When narration/notes present exclusive Or-alternatives, force a single choice.

    Policy (general, no per-story catalog):
      1. Stage 1 visual_event is the chosen environment/action for this clip.
      2. Never blend multiple Or-options into one frame.
      3. Rejected options → short must_not fragments derived from their content words.
      4. If Stage 1 visual_event already mixes several options, still assert exclusivity
         toward the visual_event as written (operator should fix Stage 1).

    Returns (prompt_clause, extra_must_not_strings).
    """
    ve = str(beat.get("visual_event") or "").strip()
    dialogue = str(beat.get("dialogue") or "")
    intent = str(beat.get("intent") or "")
    notes = " ".join(
        str(scene.get(k) or "")
        for k in ("summary", "continuity_notes", "wardrobe_notes", "setting")
    )
    # Alternatives come from VO / intent / notes — not from inventing domains
    alts = _split_or_alternatives(dialogue, intent, notes)
    if not alts and not re.search(r"\bor\b", f"{dialogue} {intent}", re.I):
        return "", []

    ve_toks = _content_tokens(ve)
    # Score each alternative by content-token overlap with the visual_event
    scored: List[Tuple[int, str]] = []
    for alt in alts:
        at = _content_tokens(alt)
        score = len(at & ve_toks) if at and ve_toks else 0
        scored.append((score, alt))
    scored.sort(key=lambda x: (-x[0], len(x[1])))

    chosen = [alt for sc, alt in scored if sc > 0]
    rejected = [alt for sc, alt in scored if sc == 0]

    # If Stage 1 never committed (no overlap), still exclusive-lock to visual_event
    if not chosen and ve:
        clause = (
            "EXCLUSIVE OR-SPLIT: Depict ONLY the single environment/action in this "
            "visual_event. Do not mash up alternate options linked by 'or' in the "
            "narration/VO into the same frame (unless Stage 1 explicitly requests a blend)."
        )
        # Ban distinctive words from VO alternatives not grounded in visual_event
        ban: List[str] = []
        for alt in alts:
            for w in sorted(_content_tokens(alt) - ve_toks)[:3]:
                ban.append(w)
        # de-dupe preserve order
        seen: set = set()
        ban_u = []
        for w in ban:
            if w not in seen:
                seen.add(w)
                ban_u.append(w)
        return clause, [f"blended alternate or-option ({w})" for w in ban_u[:6]]

    if not chosen and not ve:
        return (
            "EXCLUSIVE OR-SPLIT: Narration offers alternatives joined by 'or' — "
            "pick one coherent setting only; do not composite multiple options.",
            [],
        )

    # Chosen = alternatives grounded in visual_event; reject the rest
    chosen_bit = "; ".join(chosen[:2])
    reject_bit = "; ".join(rejected[:3]) if rejected else ""
    clause = (
        f"EXCLUSIVE OR-SPLIT: Film ONLY ({chosen_bit}) as established in visual_event. "
        "Do not blend other 'or' alternatives into this frame"
        + (f" — exclude: {reject_bit}" if reject_bit else "")
        + "."
    )
    extras: List[str] = []
    for alt in rejected:
        # Short ban phrase from the rejected alternative itself
        short = re.sub(r"\s+", " ", alt).strip()[:50]
        if short:
            extras.append(f"also showing: {short}")
    if rejected:
        extras.append("simultaneous mashup of mutually exclusive or-options")
    return clause, extras


def _inject_high_action_wardrobe_into_event(
    visual_event: str,
    primary: str,
    wardrobe_prefix: str,
) -> str:
    """Put 'Character_X wearing (…) securely' before the first action of primary."""
    if not wardrobe_prefix or not primary:
        return visual_event
    ve = visual_event or ""
    if "wearing (" in ve.lower() and "securely" in ve.lower():
        return ve
    # If event already starts with Character_X … rewrite to insert wardrobe
    pat = re.compile(rf"({re.escape(primary)})\s+", re.I)
    if pat.search(ve):
        return pat.sub(rf"{wardrobe_prefix} ", ve, count=1)
    return f"{wardrobe_prefix} {ve}".strip()


def _build_visual_prompt(
    beat: Dict[str, Any],
    scene: Dict[str, Any],
    resolution: str,
    location_seeds: Optional[Dict[str, Any]] = None,
    character_seeds: Optional[Dict[str, Any]] = None,
    *,
    prompt_soft: Optional[int] = None,
    prompt_hard: Optional[int] = None,
    wardrobe_state: Optional[Dict[str, List[str]]] = None,
) -> str:
    ve = (beat.get("visual_event") or "").strip()
    # strip old tech suffix
    ve = re.sub(r"\s*/\s*\d+p.*$", "", ve, flags=re.I).strip()

    bits: List[str] = []
    cast = _scene_cast_tokens(scene, beat)
    primary = str(
        beat.get("primary_subject") or (cast[0] if cast else "") or ""
    ).strip()

    # Exclusive Or-split (any alternatives joined by 'or' in VO) — style-lock first
    or_lock, or_must_not = _exclusive_or_environment_lock(beat, scene)
    if or_lock:
        bits.append(or_lock)

    # Place pin (location consistency)
    place = _location_lock_phrase(scene, beat, location_seeds)
    if place and place.lower() not in ve.lower()[:100]:
        bits.append(place)

    # HIGH-ACTION WARDROBE LOCK: prepend sticky props before action verbs
    if _is_high_action_beat(beat) and primary:
        w_prefix = _high_action_wardrobe_prefix(
            primary, wardrobe_state, character_seeds=character_seeds
        )
        if w_prefix:
            ve = _inject_high_action_wardrobe_into_event(ve, primary, w_prefix)

    # Ensure primary Character_* early for identity lock (if not already in ve)
    if primary and primary not in ve[:120]:
        bits.append(primary)

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
        on_screen=cast,
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
    # Attach generic must_not for rejected Or-alternatives
    if or_must_not:
        extras = beat.setdefault("_stage2_must_not_extra", [])
        if isinstance(extras, list):
            for x in or_must_not:
                if x and x not in extras:
                    extras.append(x)

    must_not = list(beat.get("must_not") or [])
    for extra in beat.get("_stage2_must_not_extra") or []:
        if extra and extra not in must_not:
            must_not.append(extra)
    if must_not:
        short = "; ".join(str(m) for m in must_not[:4])
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
    prev_beat: Optional[Dict[str, Any]] = None
    # Sticky wardrobe across the scene (any character / any item: hat, collar, PJs, …)
    wardrobe_state: Dict[str, List[str]] = _init_scene_wardrobe_state(
        cast, character_seeds, scene=scene
    )

    for i, (beat, dur) in enumerate(zip(beats, durs)):
        lid = (
            beat.get("location_id")
            or primary
            or (lids[0] if lids else None)
        )
        cont = (
            "none"
            if _force_none(
                beat,
                i,
                prev_beat=prev_beat,
                prev_location_id=prev_lid,
                location_id=lid,
            )
            else "extend_previous"
        )
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

        # Update sticky wardrobe from this beat before prompt (introduce → stick)
        wardrobe_state = _update_wardrobe_from_beat(
            wardrobe_state, beat, scene, cast=cast
        )

        vp = _build_visual_prompt(
            beat,
            scene_work,
            resolution,
            location_seeds=location_seeds,
            character_seeds=character_seeds,
            prompt_soft=soft,
            prompt_hard=hard,
            wardrobe_state=wardrobe_state,
        )
        # Restate sticky items so location changes / cuts do not drop props
        clip_cast = list(cast)
        ps = str(beat.get("primary_subject") or "").strip()
        if ps.startswith("Character_") and ps not in clip_cast:
            clip_cast.insert(0, ps)
        if "wardrobe continuity" not in vp.lower():
            # Protect wardrobe from soft-limit truncation, and recompute after body fit
            # so items only present in truncated identity tails still get restated.
            m = re.search(r"\s*/\s*\d+p.*24fps\s*$", vp, flags=re.I)
            body = vp[: m.start()].strip() if m else vp
            suffix = m.group(0) if m else f" / {resolution}, 24fps"
            vp_out = None
            for lim in (soft, hard):
                # Estimate wardrobe length from current body, fit body, then recompute
                ward_est = _wardrobe_continuity_clause(
                    wardrobe_state,
                    cast_on_clip=clip_cast,
                    clip_index=i,
                    visual_prompt=body,
                    primary_subject=ps,
                )
                reserve = min(220, max(len(ward_est) + 8, 24))
                room = max(60, int(lim) - len(suffix) - reserve)
                body_fit = body
                if len(body_fit) > room:
                    body_fit = body_fit[: max(40, room - 1)].rsplit(" ", 1)[0] + "…"
                ward_txt = _wardrobe_continuity_clause(
                    wardrobe_state,
                    cast_on_clip=clip_cast,
                    clip_index=i,
                    visual_prompt=body_fit,
                    primary_subject=ps,
                )
                if ward_txt:
                    room2 = max(60, int(lim) - len(suffix) - len(ward_txt) - 2)
                    if len(body_fit) > room2:
                        body_fit = body_fit[: max(40, room2 - 1)].rsplit(" ", 1)[0] + "…"
                        # final recompute if truncated again
                        ward_txt = _wardrobe_continuity_clause(
                            wardrobe_state,
                            cast_on_clip=clip_cast,
                            clip_index=i,
                            visual_prompt=body_fit,
                            primary_subject=ps,
                        ) or ward_txt
                    candidate = f"{body_fit.rstrip('. ')}. {ward_txt}{suffix}"
                else:
                    candidate = f"{body_fit.rstrip('. ')}{suffix}"
                if len(candidate) <= int(lim) + 24 or lim == hard:
                    vp_out = candidate
                    break
            if vp_out:
                vp = vp_out
        ward_neg = _wardrobe_negative_extras(wardrobe_state, clip_cast)
        if ward_neg:
            for frag in ward_neg.split(", "):
                if frag and frag.lower() not in neg.lower():
                    neg = f"{neg}, {frag}"

        # Spatial continuity cue when extending last frame
        if cont == "extend_previous":
            cont_cue = (
                "CONTINUE from previous last frame — same place and character positions; "
                "do not reset to the door or restart the walk; pick up exactly where the last clip ended"
            )
            if "continue from previous" not in vp.lower():
                m = re.search(r"\s*/\s*\d+p.*24fps\s*$", vp, flags=re.I)
                body = vp[: m.start()].strip() if m else vp
                suffix = m.group(0) if m else f" / {resolution}, 24fps"
                body = body.rstrip(". ") + ". " + cont_cue
                vp = _clamp_prompt(f"{body}{suffix}", soft)
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
        prev_beat = beat

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
