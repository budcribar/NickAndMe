#!/usr/bin/env python3
"""
Pull character likeness images out of extracted PDF book_images into Stage 1 seeds.

After Stage 1 produces character_seed_tokens:
  - Prefer seed.source_image_pages (page numbers from the LLM)
  - Else heuristic: hero/dog → cover + early pages; others → fewer pages
  - Copy into assets/characters/<seed>_bookref_* 
  - Set design_reference_images + book_reference_images on each seed
  - Write back scenes.json (and optional Stage 2 blueprint if present)

Usage (repo root):
  python scripts/two_stage_adaptation/attach_character_images.py --project BusterTheNoodleheadDog
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

ROOT = Path(__file__).resolve().parents[2]


def _project_dir(project_id: Optional[str]) -> Path:
    if project_id:
        return ROOT / "projects" / project_id
    ws = ROOT / "projects" / "workspace.json"
    pid = "NickAndMe"
    if ws.is_file():
        try:
            pid = json.loads(ws.read_text(encoding="utf-8")).get("active_project") or pid
        except (json.JSONDecodeError, OSError):
            pass
    return ROOT / "projects" / str(pid)


def _scenes_path(project: Path) -> Path:
    meta = project / "project.json"
    name = "scenes.json"
    if meta.is_file():
        try:
            name = json.loads(meta.read_text(encoding="utf-8")).get("scenes_file") or name
        except (json.JSONDecodeError, OSError):
            pass
    p = project / name
    if p.is_file():
        return p
    alt = project / "nickandme.scenes.json"
    return alt if alt.is_file() else p


def _blueprint_path(project: Path) -> Optional[Path]:
    meta = project / "project.json"
    name = "blueprint.clips.grok.json"
    if meta.is_file():
        try:
            name = (
                json.loads(meta.read_text(encoding="utf-8")).get("blueprint_file") or name
            )
        except (json.JSONDecodeError, OSError):
            pass
    p = project / name
    return p if p.is_file() else None


def load_book_image_inventory(project: Path) -> List[Dict[str, Any]]:
    """Return rows: {path_rel, abs, page, kind, name} under project."""
    source = project / "source"
    img_dir = source / "book_images"
    rows: List[Dict[str, Any]] = []
    man = img_dir / "manifest.json"
    if man.is_file():
        try:
            data = json.loads(man.read_text(encoding="utf-8"))
            for im in data.get("images") or []:
                if not isinstance(im, dict):
                    continue
                rel = str(im.get("path") or "").replace("\\", "/")
                # paths in manifest are relative to source/
                fp = source / rel if rel else None
                if fp is None or not fp.is_file():
                    fp = img_dir / Path(rel).name if rel else None
                if fp is None or not fp.is_file():
                    continue
                page = im.get("page")
                try:
                    page_i = int(page) if page is not None else 0
                except (TypeError, ValueError):
                    page_i = 0
                rows.append(
                    {
                        "path_rel": str(fp.relative_to(project)).replace("\\", "/"),
                        "abs": str(fp),
                        "page": page_i,
                        "kind": str(im.get("kind") or ""),
                        "name": fp.name.lower(),
                    }
                )
        except (json.JSONDecodeError, OSError, ValueError):
            pass

    if not rows and img_dir.is_dir():
        for f in sorted(img_dir.iterdir()):
            if f.suffix.lower() not in (".png", ".jpg", ".jpeg", ".webp"):
                continue
            m = re.search(r"(?:page|p|embedded_p)0*(\d+)", f.name, re.I)
            page_i = int(m.group(1)) if m else 0
            rows.append(
                {
                    "path_rel": str(f.relative_to(project)).replace("\\", "/"),
                    "abs": str(f),
                    "page": page_i,
                    "kind": "file",
                    "name": f.name.lower(),
                }
            )
    return rows


def inventory_summary_for_prompt(project: Path, *, max_rows: int = 40) -> str:
    """Compact inventory text for Stage 1 user message."""
    rows = load_book_image_inventory(project)
    if not rows:
        return "(no book_images yet — PDF extract may still be pending)"
    lines = ["AVAILABLE_BOOK_IMAGES (use page numbers in character seeds):"]
    # unique by page
    seen_pages = set()
    for r in sorted(rows, key=lambda x: (x["page"], x["name"])):
        pg = r["page"]
        if pg and pg in seen_pages:
            continue
        if pg:
            seen_pages.add(pg)
        lines.append(
            f"  - page {pg or '?'}: {r['path_rel']} ({r['kind'] or 'image'})"
        )
        if len(lines) > max_rows:
            lines.append(f"  … ({len(rows)} total files)")
            break
    return "\n".join(lines)


def _pages_for_seed(seed: Dict[str, Any]) -> List[int]:
    """Normalize source_image_pages from LLM (ints or strings)."""
    raw = seed.get("source_image_pages") or seed.get("image_pages") or []
    if isinstance(raw, (int, float)):
        raw = [int(raw)]
    if isinstance(raw, str):
        raw = re.findall(r"\d+", raw)
    out: List[int] = []
    if isinstance(raw, list):
        for x in raw:
            try:
                out.append(int(x))
            except (TypeError, ValueError):
                continue
    return out


def _rows_for_pages(
    inventory: Sequence[Dict[str, Any]], pages: Sequence[int]
) -> List[Dict[str, Any]]:
    by_page: Dict[int, List[Dict[str, Any]]] = {}
    for r in inventory:
        by_page.setdefault(int(r.get("page") or 0), []).append(r)
    picks: List[Dict[str, Any]] = []
    for pg in pages:
        cands = by_page.get(int(pg)) or []
        # Prefer rendered page stills (correct PDF orientation) over raw embedded
        # (embedded can be upside-down when page.rotation is 180).
        cands = sorted(
            cands,
            key=lambda r: (
                0 if r.get("kind") == "rendered_page" or r["name"].startswith("page_") else 1,
                0 if r.get("kind") == "embedded" else 1,
                r["name"],
            ),
        )
        if cands:
            picks.append(cands[0])
    return picks


def _heuristic_picks(
    inventory: List[Dict[str, Any]],
    *,
    key: str,
    seed: Dict[str, Any],
    index: int,
) -> List[Dict[str, Any]]:
    ordered = sorted(
        inventory,
        key=lambda r: (
            0 if "cover" in r["name"] else 1,
            # Prefer correctly oriented page renders over raw embedded (rotation issues)
            0 if r.get("kind") == "rendered_page" or r["name"].startswith("page_") else 1,
            0 if r.get("kind") == "embedded" else 2,
            r.get("page") or 99,
            r["name"],
        ),
    )
    early = [r for r in ordered if (r.get("page") or 99) <= 8 or "cover" in r["name"]]
    if not early:
        early = ordered[:6]

    token = key.replace("Character_", "").lower()
    given = str(seed.get("canonical_given_name") or "").lower()
    name_hits = [
        r
        for r in inventory
        if token in r["name"] or (given and given in r["name"])
    ]
    desc = str(seed.get("description") or "").lower()
    is_hero = index == 0 or "dog" in desc or "buster" in token

    if name_hits:
        return name_hits[:3]
    if is_hero:
        return early[:3]
    return (early[:1] + early[2:3])[:2] or early[:1]


def attach_character_images(
    *,
    project_id: Optional[str] = None,
    force: bool = True,
    copy_into_assets: bool = True,
    update_blueprint: bool = True,
) -> Dict[str, Any]:
    """
    Main entry: write design_reference_images onto Stage 1 seeds from PDF extract.
    """
    project = _project_dir(project_id)
    scenes_p = _scenes_path(project)
    if not scenes_p.is_file():
        return {"ok": False, "reason": f"no_stage1:{scenes_p}"}

    inventory = load_book_image_inventory(project)
    if not inventory:
        return {
            "ok": False,
            "reason": "no_book_images",
            "hint": "Run extract_book_source / prepare_book_source first so source/book_images exists.",
        }

    data = json.loads(scenes_p.read_text(encoding="utf-8"))
    gpv = data.setdefault("global_production_variables", {})
    seeds = gpv.get("character_seed_tokens") or {}
    if not isinstance(seeds, dict) or not seeds:
        return {"ok": False, "reason": "no_character_seeds"}

    chars_dir = project / "assets" / "characters"
    if copy_into_assets:
        chars_dir.mkdir(parents=True, exist_ok=True)

    attached: Dict[str, Any] = {}
    for i, (key, seed) in enumerate(seeds.items()):
        if not isinstance(seed, dict):
            continue
        pol = str(seed.get("display_name_policy") or "").lower()
        is_narr = (
            "never" in pol
            or key.endswith("_Narrator")
            or key == "Character_Narrator"
            or "narrator" in key.lower()
        )
        # Narrator / never_on_screen = voice only — never assign plates
        if is_narr:
            seed.pop("design_reference_images", None)
            seed.pop("book_reference_images", None)
            seed.pop("source_image_pages", None)
            attached[key] = {
                "skipped": True,
                "reason": "voice_only_narrator",
            }
            continue

        if (
            seed.get("design_reference_images")
            and not force
            and seed.get("source_image_pages")
        ):
            attached[key] = {
                "skipped": True,
                "refs": seed.get("design_reference_images"),
            }
            continue

        pages = _pages_for_seed(seed)
        if pages:
            picks = _rows_for_pages(inventory, pages)
            method = "source_image_pages"
        else:
            picks = _heuristic_picks(inventory, key=key, seed=seed, index=i)
            method = "heuristic"
            # record inferred pages for transparency
            seed["source_image_pages"] = [
                int(r["page"]) for r in picks if r.get("page")
            ]

        rel_paths: List[str] = []
        for j, row in enumerate(picks[:3]):
            src = Path(row["abs"])
            if copy_into_assets and src.is_file():
                dest = chars_dir / f"{key.lower()}_bookref_{j + 1}{src.suffix.lower()}"
                try:
                    shutil.copy2(src, dest)
                    rel_paths.append(
                        str(dest.relative_to(project)).replace("\\", "/")
                    )
                except OSError:
                    rel_paths.append(row["path_rel"])
            else:
                rel_paths.append(row["path_rel"])

        if not rel_paths:
            attached[key] = {"skipped": True, "reason": "no_picks", "method": method}
            continue

        seed["design_reference_images"] = rel_paths
        seed["book_reference_images"] = rel_paths
        # Keep placeholder path for locked final ref
        seed.setdefault(
            "reference_image_placeholder",
            f"assets/characters/{key.lower()}_ref.png",
        )
        attached[key] = {
            "method": method,
            "pages": seed.get("source_image_pages"),
            "refs": rel_paths,
        }

    gpv["character_seed_tokens"] = seeds
    data["character_images_attached_at"] = time.strftime("%Y-%m-%dT%H:%M:%S")
    scenes_p.write_text(
        json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )

    bp_updated = False
    if update_blueprint:
        bp_path = _blueprint_path(project)
        if bp_path and bp_path.is_file():
            try:
                bp = json.loads(bp_path.read_text(encoding="utf-8"))
                bgpv = bp.setdefault("global_production_variables", {})
                bseeds = bgpv.get("character_seed_tokens") or {}
                if not isinstance(bseeds, dict) or not bseeds:
                    bseeds = {
                        k: dict(v) if isinstance(v, dict) else v
                        for k, v in seeds.items()
                    }
                else:
                    for k, v in seeds.items():
                        if not isinstance(v, dict):
                            continue
                        if k not in bseeds or not isinstance(bseeds.get(k), dict):
                            bseeds[k] = dict(v)
                        else:
                            bseeds[k]["design_reference_images"] = v.get(
                                "design_reference_images"
                            )
                            bseeds[k]["book_reference_images"] = v.get(
                                "book_reference_images"
                            )
                            bseeds[k]["source_image_pages"] = v.get(
                                "source_image_pages"
                            )
                bgpv["character_seed_tokens"] = bseeds
                bp_path.write_text(
                    json.dumps(bp, indent=2, ensure_ascii=False) + "\n",
                    encoding="utf-8",
                )
                bp_updated = True
            except (json.JSONDecodeError, OSError):
                bp_updated = False

    return {
        "ok": True,
        "project": project.name,
        "scenes_json": str(scenes_p),
        "attached": attached,
        "count": sum(1 for v in attached.values() if v.get("refs")),
        "inventory": len(inventory),
        "blueprint_updated": bp_updated,
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--project", default=None)
    ap.add_argument("--force", action="store_true", default=True)
    ap.add_argument("--no-copy", action="store_true")
    ap.add_argument("--no-blueprint", action="store_true")
    args = ap.parse_args()
    result = attach_character_images(
        project_id=args.project,
        force=bool(args.force),
        copy_into_assets=not args.no_copy,
        update_blueprint=not args.no_blueprint,
    )
    if not result.get("ok"):
        print(f"[Error] {result}")
        return 1
    print(
        f"[Success] Attached book images to {result.get('count')} character(s) "
        f"(inventory={result.get('inventory')}, blueprint={result.get('blueprint_updated')})"
    )
    for k, v in (result.get("attached") or {}).items():
        print(f"  {k}: {v}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
