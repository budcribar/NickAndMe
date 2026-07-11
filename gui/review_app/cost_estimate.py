"""
Estimated API cost for regenerating clips / scenes.

Rates are configurable via pipeline_config.json → cost_estimates.
Defaults approximate public xAI Imagine API list prices (USD) as of mid-2026;
treat as planning estimates, not invoices.
"""
from __future__ import annotations

import re
from typing import Any, Dict, List, Optional, Tuple

# Defaults mirror docs.x.ai Imagine pricing (grok-imagine-video family)
DEFAULT_COST_ESTIMATES = {
    "currency": "USD",
    "notes": (
        "Estimates only — based on list rates for Grok Imagine video. "
        "Update cost_estimates in pipeline_config.json if xAI changes pricing."
    ),
    # Output video $/second by resolution
    "video_output_per_sec": {
        "480p": 0.05,
        "720p": 0.07,
        "1080p": 0.25,
    },
    # Media inputs
    "video_input_image": 0.002,  # reference / start frame image
    "video_input_per_sec": 0.01,  # video→video / extend input (if billed)
    # Character design (optional, cascade of images)
    "image_output_quality": 0.05,  # grok-imagine-image-quality ~1K
    "image_output_standard": 0.02,
    # Assume one ref image input per clip when character locked
    "assume_ref_image_per_clip": True,
    # Fraction of clips that use last-frame / video continue (extra input cost)
    "assume_extend_fraction": 0.0,
    # Expected QA retries beyond first attempt (0 = one shot only)
    "assume_avg_retries": 0.0,
}


def _parse_timestamp_duration(ts: str) -> Optional[float]:
    """Parse '00:00-00:08' or '0:00-0:08' → seconds."""
    if not ts:
        return None
    m = re.match(
        r"^\s*(\d+):(\d{2})\s*-\s*(\d+):(\d{2})\s*$",
        str(ts).strip(),
    )
    if not m:
        return None
    a = int(m.group(1)) * 60 + int(m.group(2))
    b = int(m.group(3)) * 60 + int(m.group(4))
    if b < a:
        return None
    return float(b - a)


def _rates_from_config(config: Dict[str, Any]) -> Dict[str, Any]:
    rates = dict(DEFAULT_COST_ESTIMATES)
    custom = config.get("cost_estimates") or {}
    if isinstance(custom, dict):
        # shallow + nested video_output merge
        for k, v in custom.items():
            if k == "video_output_per_sec" and isinstance(v, dict):
                merged = dict(rates["video_output_per_sec"])
                merged.update(v)
                rates["video_output_per_sec"] = merged
            else:
                rates[k] = v
    return rates


def _output_rate(config: Dict[str, Any], rates: Dict[str, Any]) -> float:
    res = str(config.get("resolution", "720p")).lower().strip()
    table = rates.get("video_output_per_sec") or {}
    if res in table:
        return float(table[res])
    # model-specific overrides
    model = str(config.get("model_name", "")).lower()
    if "1.5" in model and res == "720p":
        return float(table.get("720p", 0.14))
    return float(table.get("720p", 0.07))


def clip_duration_seconds(clip: Dict[str, Any], config: Dict[str, Any]) -> float:
    d = _parse_timestamp_duration(clip.get("timestamp") or "")
    if d is not None and d > 0:
        return d
    default = float(config.get("duration_seconds") or 8)
    return max(1.0, default)


def estimate_clip_cost(
    clip: Dict[str, Any],
    config: Dict[str, Any],
    *,
    is_extend: Optional[bool] = None,
) -> Dict[str, Any]:
    rates = _rates_from_config(config)
    duration = clip_duration_seconds(clip, config)
    out_rate = _output_rate(config, rates)
    retries = float(rates.get("assume_avg_retries") or 0)
    attempts = 1.0 + max(0.0, retries)

    video_out = duration * out_rate * attempts
    ref_img = 0.0
    if rates.get("assume_ref_image_per_clip", True):
        ref_img = float(rates.get("video_input_image") or 0) * attempts

    if is_extend is None:
        cont = str(clip.get("veo_continuation_source") or "none").lower()
        is_extend = cont == "extend_previous"
    extend_in = 0.0
    if is_extend:
        # bill input seconds for previous-frame video condition (list price style)
        extend_in = duration * float(rates.get("video_input_per_sec") or 0) * attempts
    elif float(rates.get("assume_extend_fraction") or 0) > 0:
        extend_in = (
            duration
            * float(rates.get("video_input_per_sec") or 0)
            * float(rates["assume_extend_fraction"])
            * attempts
        )

    total = video_out + ref_img + extend_in
    return {
        "duration_sec": duration,
        "attempts": attempts,
        "output_rate_per_sec": out_rate,
        "video_output_usd": round(video_out, 4),
        "ref_image_usd": round(ref_img, 4),
        "extend_input_usd": round(extend_in, 4),
        "total_usd": round(total, 4),
        "currency": rates.get("currency", "USD"),
    }


def estimate_scene_cost(
    scene: Dict[str, Any],
    config: Dict[str, Any],
    *,
    only_clip_numbers: Optional[List[int]] = None,
    only_existing_paths: Optional[Dict[int, bool]] = None,
    only_stale: bool = False,
    stale_clip_numbers: Optional[List[int]] = None,
) -> Dict[str, Any]:
    """
    Estimate cost to regenerate clips in a scene.

    only_clip_numbers: if set, only those clips
    only_existing_paths: map clip_number -> on_disk; when used with filter_existing
    """
    clips = scene.get("veo_clips") or []
    rates = _rates_from_config(config)
    selected: List[Tuple[Dict[str, Any], Dict[str, Any]]] = []

    for clip in clips:
        cn = int(clip.get("clip_number", 0))
        if only_clip_numbers is not None and cn not in only_clip_numbers:
            continue
        if only_stale and stale_clip_numbers is not None and cn not in stale_clip_numbers:
            continue
        if only_existing_paths is not None and not only_existing_paths.get(cn, False):
            continue
        est = estimate_clip_cost(clip, config)
        selected.append((clip, est))

    total = sum(e["total_usd"] for _, e in selected)
    duration = sum(e["duration_sec"] for _, e in selected)
    return {
        "scene_number": scene.get("scene_number"),
        "clip_count": len(selected),
        "total_duration_sec": round(duration, 2),
        "total_usd": round(total, 2),
        "currency": rates.get("currency", "USD"),
        "resolution": config.get("resolution", "720p"),
        "model_name": config.get("model_name", ""),
        "per_clip": [
            {
                "clip_number": int(c.get("clip_number", 0)),
                "timestamp": c.get("timestamp"),
                **est,
            }
            for c, est in selected
        ],
        "notes": rates.get("notes", ""),
        "output_rate_per_sec": _output_rate(config, rates),
    }


def format_usd(amount: float, currency: str = "USD") -> str:
    if currency != "USD":
        return f"{amount:.2f} {currency}"
    return f"${amount:.2f}"


def _config_with(
    base: Dict[str, Any],
    *,
    resolution: Optional[str] = None,
    model_name: Optional[str] = None,
    video_provider: Optional[str] = None,
    assume_avg_retries: Optional[float] = None,
) -> Dict[str, Any]:
    cfg = dict(base)
    if resolution:
        cfg["resolution"] = resolution
    if model_name:
        cfg["model_name"] = model_name
    if video_provider:
        cfg["video_provider"] = video_provider
    if assume_avg_retries is not None:
        ce = dict(cfg.get("cost_estimates") or {})
        ce["assume_avg_retries"] = assume_avg_retries
        cfg["cost_estimates"] = ce
    return cfg


def film_budget_report(
    scenes: List[Dict[str, Any]],
    config: Dict[str, Any],
    *,
    on_disk_by_scene: Dict[int, Dict[int, bool]],
    hero_by_scene: Optional[Dict[int, Dict[str, Any]]] = None,
    draft_resolution: Optional[str] = None,
    hero_resolution: str = "720p",
    include_hero_upgrade: bool = True,
) -> Dict[str, Any]:
    """
    Full-film planning report.

    spent_estimate: cost to have produced clips already on disk at draft_resolution
      (or scene hero resolution when marked hero)
    remaining_first_pass: cost to generate clips not yet on disk at draft_resolution
    remaining_hero: cost to hero-regen all non-hero scenes (on-disk clips) at hero_resolution
    total_if_finish_draft: spent + remaining_first_pass
    total_if_finish_hero: spent + remaining_first_pass + remaining_hero (approx)
    """
    draft_resolution = draft_resolution or str(config.get("resolution") or "480p")
    hero_by_scene = hero_by_scene or {}
    draft_cfg = _config_with(config, resolution=draft_resolution)
    hero_cfg = _config_with(config, resolution=hero_resolution)

    rows: List[Dict[str, Any]] = []
    spent = 0.0
    remaining_draft = 0.0
    remaining_hero = 0.0
    total_all_draft = 0.0
    total_all_hero = 0.0
    clips_on_disk = 0
    clips_missing = 0
    clips_total = 0
    sec_on_disk = 0.0
    sec_missing = 0.0

    for scene in scenes:
        sn = int(scene.get("scene_number") or 0)
        clips = scene.get("veo_clips") or []
        disk_map = on_disk_by_scene.get(sn) or {}
        hero = hero_by_scene.get(sn)
        is_hero = bool(hero)

        # Per-clip classification
        existing_map = {int(c.get("clip_number", 0)): bool(disk_map.get(int(c.get("clip_number", 0)))) for c in clips}
        missing_map = {cn: (not on) for cn, on in existing_map.items()}
        # only_existing_paths True means include; for missing we need invert
        missing_only = {cn: True for cn, on in existing_map.items() if not on}
        existing_only = {cn: True for cn, on in existing_map.items() if on}

        # spent: existing at hero res if hero else draft
        spent_cfg = (
            _config_with(config, resolution=str(hero.get("resolution") or hero_resolution))
            if is_hero
            else draft_cfg
        )
        est_spent = estimate_scene_cost(scene, spent_cfg, only_existing_paths=existing_only) if existing_only else {
            "total_usd": 0.0, "clip_count": 0, "total_duration_sec": 0
        }
        est_missing = estimate_scene_cost(scene, draft_cfg, only_existing_paths=missing_only) if missing_only else {
            "total_usd": 0.0, "clip_count": 0, "total_duration_sec": 0
        }
        # hero upgrade: re-do on-disk clips at hero res if not already hero
        if include_hero_upgrade and existing_only and not is_hero:
            est_hero = estimate_scene_cost(scene, hero_cfg, only_existing_paths=existing_only)
        else:
            est_hero = {"total_usd": 0.0, "clip_count": 0, "total_duration_sec": 0}

        est_all_draft = estimate_scene_cost(scene, draft_cfg)
        est_all_hero = estimate_scene_cost(scene, hero_cfg)

        n_disk = sum(1 for v in existing_map.values() if v)
        n_miss = sum(1 for v in existing_map.values() if not v)
        n_all = len(clips)

        spent += float(est_spent.get("total_usd") or 0)
        remaining_draft += float(est_missing.get("total_usd") or 0)
        remaining_hero += float(est_hero.get("total_usd") or 0)
        total_all_draft += float(est_all_draft.get("total_usd") or 0)
        total_all_hero += float(est_all_hero.get("total_usd") or 0)
        clips_on_disk += n_disk
        clips_missing += n_miss
        clips_total += n_all
        sec_on_disk += float(est_spent.get("total_duration_sec") or 0)
        sec_missing += float(est_missing.get("total_duration_sec") or 0)

        rows.append(
            {
                "scene": sn,
                "setting": (scene.get("setting") or "")[:60],
                "clips_total": n_all,
                "clips_on_disk": n_disk,
                "clips_missing": n_miss,
                "is_hero": is_hero,
                "hero_resolution": (hero or {}).get("resolution"),
                "spent_usd": float(est_spent.get("total_usd") or 0),
                "remaining_draft_usd": float(est_missing.get("total_usd") or 0),
                "hero_upgrade_usd": float(est_hero.get("total_usd") or 0),
                "all_draft_usd": float(est_all_draft.get("total_usd") or 0),
                "all_hero_usd": float(est_all_hero.get("total_usd") or 0),
                "duration_on_disk_sec": float(est_spent.get("total_duration_sec") or 0),
                "duration_missing_sec": float(est_missing.get("total_duration_sec") or 0),
            }
        )

    rows.sort(key=lambda r: r["scene"])
    return {
        "draft_resolution": draft_resolution,
        "hero_resolution": hero_resolution,
        "currency": "USD",
        "model_name": config.get("model_name"),
        "video_provider": config.get("video_provider"),
        "output_rate_draft": _output_rate(draft_cfg, _rates_from_config(draft_cfg)),
        "output_rate_hero": _output_rate(hero_cfg, _rates_from_config(hero_cfg)),
        "summary": {
            "clips_total": clips_total,
            "clips_on_disk": clips_on_disk,
            "clips_missing": clips_missing,
            "sec_on_disk": round(sec_on_disk, 1),
            "sec_missing": round(sec_missing, 1),
            "spent_usd": round(spent, 2),
            "remaining_first_pass_usd": round(remaining_draft, 2),
            "remaining_hero_upgrade_usd": round(remaining_hero, 2),
            "finish_draft_usd": round(spent + remaining_draft, 2),
            "finish_draft_plus_hero_usd": round(spent + remaining_draft + remaining_hero, 2),
            "full_film_all_draft_usd": round(total_all_draft, 2),
            "full_film_all_hero_usd": round(total_all_hero, 2),
            "scenes_with_media": sum(1 for r in rows if r["clips_on_disk"] > 0),
            "scenes_hero": sum(1 for r in rows if r["is_hero"]),
            "scenes_total": len(rows),
        },
        "scenes": rows,
        "notes": (
            "Estimates only (list rates). Spent assumes on-disk clips cost what the "
            "selected draft/hero rates would charge — not actual invoice history."
        ),
    }


def scenario_compare(
    scenes: List[Dict[str, Any]],
    base_config: Dict[str, Any],
    scenarios: List[Dict[str, Any]],
    *,
    on_disk_by_scene: Dict[int, Dict[int, bool]],
) -> List[Dict[str, Any]]:
    """
    scenarios: list of {label, resolution, model_name?, video_provider?, assume_avg_retries?}
    Returns full-film cost for each scenario (all clips + missing-only + on-disk regen).
    """
    out = []
    for sc in scenarios:
        cfg = _config_with(
            base_config,
            resolution=sc.get("resolution"),
            model_name=sc.get("model_name"),
            video_provider=sc.get("video_provider"),
            assume_avg_retries=sc.get("assume_avg_retries"),
        )
        all_usd = 0.0
        missing_usd = 0.0
        disk_usd = 0.0
        for scene in scenes:
            sn = int(scene.get("scene_number") or 0)
            disk_map = on_disk_by_scene.get(sn) or {}
            clips = scene.get("veo_clips") or []
            existing_only = {
                int(c.get("clip_number", 0)): True
                for c in clips
                if disk_map.get(int(c.get("clip_number", 0)))
            }
            missing_only = {
                int(c.get("clip_number", 0)): True
                for c in clips
                if not disk_map.get(int(c.get("clip_number", 0)))
            }
            all_usd += float(estimate_scene_cost(scene, cfg).get("total_usd") or 0)
            if existing_only:
                disk_usd += float(
                    estimate_scene_cost(scene, cfg, only_existing_paths=existing_only).get("total_usd") or 0
                )
            if missing_only:
                missing_usd += float(
                    estimate_scene_cost(scene, cfg, only_existing_paths=missing_only).get("total_usd") or 0
                )
        rates = _rates_from_config(cfg)
        out.append(
            {
                "label": sc.get("label") or f"{cfg.get('resolution')} / {cfg.get('model_name')}",
                "resolution": cfg.get("resolution"),
                "model_name": cfg.get("model_name"),
                "video_provider": cfg.get("video_provider"),
                "rate_per_sec": _output_rate(cfg, rates),
                "full_film_usd": round(all_usd, 2),
                "remaining_missing_usd": round(missing_usd, 2),
                "regen_on_disk_usd": round(disk_usd, 2),
            }
        )
    return out
