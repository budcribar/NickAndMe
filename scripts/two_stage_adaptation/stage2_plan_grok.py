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

# Fallback Grok constraints (overridden by pipeline_config.duration_defaults when available)
GROK_MIN_CLIP = 6
GROK_MAX_CLIP = 10
GROK_ABS_MAX = 15
GROK_DEFAULT = 8
GROK_SCENE_MIN = 8
GROK_SCENE_MAX = 134
GROK_PROMPT_SOFT = 500
GROK_PROMPT_HARD = 800


def _duration_policy_from_config(
    config: Optional[Dict[str, Any]] = None,
) -> Dict[str, int]:
    """Load prefer/default/max clip seconds from renderer duration_defaults."""
    try:
        sys.path.insert(0, str(ROOT))
        from renderer.engine import resolve_duration_profile  # type: ignore

        if config is None:
            # try active project pipeline_config
            cfg_path = None
            ws = ROOT / "projects" / "workspace.json"
            pid = "NickAndMe"
            if ws.is_file():
                try:
                    pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
                except Exception:
                    pass
            cfg_path = ROOT / "projects" / str(pid) / "pipeline_config.json"
            if cfg_path.is_file():
                config = json.loads(cfg_path.read_text(encoding="utf-8"))
            else:
                config = {}
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


def _ts(start: int, end: int) -> str:
    def fmt(s: int) -> str:
        return f"{s // 60:02d}:{s % 60:02d}"

    return f"{fmt(start)}-{fmt(end)}"


def _clamp_prompt(text: str, hard: int = GROK_PROMPT_HARD) -> str:
    text = re.sub(r"\s+", " ", text).strip()
    suffix_m = re.search(r"\s*/\s*\d+p,\s*24fps\s*$", text, re.I)
    suffix = suffix_m.group(0) if suffix_m else " / 720p, 24fps"
    base = re.sub(r"\s*/\s*\d+p,\s*24fps\s*$", "", text, flags=re.I).strip()
    budget = hard - len(suffix)
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


def _build_visual_prompt(
    beat: Dict[str, Any],
    scene: Dict[str, Any],
    resolution: str,
    location_seeds: Optional[Dict[str, Any]] = None,
) -> str:
    ve = (beat.get("visual_event") or "").strip()
    # strip old tech suffix
    ve = re.sub(r"\s*/\s*\d+p.*$", "", ve, flags=re.I).strip()

    bits: List[str] = []
    # Place pin first (location consistency)
    place = _location_lock_phrase(scene, beat, location_seeds)
    if place and place.lower() not in ve.lower()[:100]:
        bits.append(place)

    primary = beat.get("primary_subject")
    # Ensure primary Character_* early for identity lock
    if primary and primary not in ve[:80]:
        bits.append(f"{primary}")

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

    audio = beat.get("audio") or {}
    delivery = (audio.get("delivery") or "none").lower()
    if delivery == "voiceover_internal":
        if "lips closed" not in ve.lower() and "not spoken" not in ve.lower():
            bits.append(
                "lips closed, internal monologue not spoken to another person"
            )

    # Continuous action language for big_action
    ac = (beat.get("action_class") or "").lower()
    if ac == "big_action":
        if "continuous" not in ve.lower() and "one continuous" not in ve.lower():
            bits.append(
                "ONE continuous take no cut; unbroken cause-to-effect motion"
            )

    must_not = beat.get("must_not") or []
    if must_not:
        short = "; ".join(str(m) for m in must_not[:2])
        if short and short.lower() not in ve.lower():
            bits.append(f"must not: {short}")

    body = ". ".join(b.strip().rstrip(".") for b in bits if b and str(b).strip())
    body = re.sub(r"\s+", " ", body).strip()
    if not body.endswith("."):
        # keep natural
        pass
    prompt = f"{body} / {resolution}, 24fps"
    # Prefer soft limit
    if len(prompt) > GROK_PROMPT_SOFT:
        prompt = _clamp_prompt(prompt, GROK_PROMPT_SOFT)
    else:
        prompt = _clamp_prompt(prompt, GROK_PROMPT_HARD)
    return prompt


def _build_audio_payload(beat: Dict[str, Any]) -> Dict[str, str]:
    audio = beat.get("audio") or {}
    delivery = (audio.get("delivery") or "none").strip().lower()
    if delivery not in ("spoken_on_camera", "voiceover_internal", "none"):
        delivery = "voiceover_internal" if (audio.get("dialogue") or "").strip() else "none"
    speaker = (audio.get("speaker") or "none").strip() or "none"
    dialogue = (audio.get("dialogue") or "").strip()
    if delivery == "none":
        speaker = "none"
        dialogue = ""
    return {
        "speaker": speaker,
        "dialogue": dialogue,
        "delivery": delivery,
    }


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


def plan_scene(
    scene: Dict[str, Any],
    resolution: str = "720p",
    location_seeds: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    beats = scene.get("story_beats") or []
    lids = list(scene.get("location_ids") or [])
    primary = scene.get("primary_location_id") or (lids[0] if lids else None)
    if not beats:
        # empty scene placeholder
        return {
            "scene_number": scene.get("scene_number"),
            "setting": scene.get("setting"),
            "location_ids": lids,
            "primary_location_id": primary,
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

        vp = _build_visual_prompt(beat, scene, resolution, location_seeds=location_seeds)
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
                vp = _clamp_prompt(f"{core} / {resolution}, 24fps", GROK_PROMPT_SOFT)

        clip = {
            "clip_number": i + 1,
            "timestamp": _ts(t, t + dur),
            "veo_continuation_source": cont,
            "location_id": lid,
            "visual_prompt": vp,
            "negative_prompt": neg,
            "audio_payload": _build_audio_payload(beat),
            "stage1_beat_id": beat.get("beat_id"),
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
    seeds = (plan.get("global_production_variables") or {}).get("location_seed_tokens") or {}
    for sc in plan.get("scenes") or []:
        clips = sc.get("veo_clips") or []
        lids = sc.get("location_ids") or []
        total = int(sc.get("total_estimated_duration_seconds") or 0)
        ssum = 0
        prev_end = 0
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
            if len(vp) > GROK_PROMPT_HARD:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: prompt len {len(vp)}"
                )
            if not vp.rstrip().endswith("24fps"):
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: missing fps suffix"
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
            if dur < 1 or dur > GROK_ABS_MAX:
                errs.append(
                    f"S{sc.get('scene_number')}C{c.get('clip_number')}: duration {dur}"
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
    planned_scenes = [
        plan_scene(
            s,
            resolution=args.resolution.strip() or "720p",
            location_seeds=loc_seeds,
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
            "prompt_soft_max": GROK_PROMPT_SOFT,
            "prompt_hard_max": GROK_PROMPT_HARD,
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
