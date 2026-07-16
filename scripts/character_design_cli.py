#!/usr/bin/env python3
"""
CLI bridge for FilmStudio.Api character design ops.

Usage (repo root):
  python scripts/character_design_cli.py --project Buster generate --char Character_Buster
  python scripts/character_design_cli.py --project Buster unlock --char Character_Buster
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--project", required=True)
    ap.add_argument(
        "action",
        choices=["generate", "lock-variant", "lock-image", "unlock"],
    )
    ap.add_argument("--char", required=True)
    ap.add_argument("--variant-index", type=int, default=1)
    ap.add_argument("--image", default="", help="Relative or absolute image path for lock-image")
    args = ap.parse_args()

    sys.path.insert(0, str(ROOT / "gui"))
    try:
        from review_app import pipeline_api as api
    except Exception as e:
        print(json.dumps({"ok": False, "error": f"import pipeline_api: {e}"}))
        return 1

    try:
        api.switch_project(args.project)
    except Exception as e:
        print(json.dumps({"ok": False, "error": f"switch_project: {e}"}))
        return 1

    try:
        if args.action == "generate":
            # Progress lines for FilmStudio.Api → SignalR (use -u / PYTHONUNBUFFERED)
            print(f"[progress] starting generate for {args.char}", flush=True)
            print("[progress] resolving book_refs / design prompt", flush=True)
            print("[progress] calling grok image api (may take a minute)…", flush=True)
            result = api.generate_character_variants(args.char)
            paths = result.get("paths") or []
            for i, p in enumerate(paths, start=1):
                print(f"[progress] saved variant {i}/3 → {Path(p).name}", flush=True)
            print(
                json.dumps(
                    {
                        "ok": True,
                        "action": "generate",
                        "char": args.char,
                        "paths": paths,
                        "mode": result.get("mode"),
                        "book_refs": [
                            Path(p).name for p in (result.get("book_refs") or [])
                        ],
                        "edit_error": result.get("edit_error"),
                    }
                ),
                flush=True,
            )
            return 0

        if args.action == "lock-variant":
            path = api.lock_character_variant(args.char, int(args.variant_index))
            print(
                json.dumps(
                    {
                        "ok": True,
                        "action": "lock-variant",
                        "char": args.char,
                        "path": path,
                        "variant_index": int(args.variant_index),
                    }
                )
            )
            return 0

        if args.action == "lock-image":
            if not args.image:
                print(json.dumps({"ok": False, "error": "--image required"}))
                return 2
            img = args.image
            p = Path(img)
            if not p.is_file():
                # try project-relative
                proj = api.get_active_project_dir()
                cand = (proj / img) if proj else None
                if cand is not None and cand.is_file():
                    img = str(cand)
                else:
                    cand2 = ROOT / img
                    if cand2.is_file():
                        img = str(cand2)
            path = api.lock_character_from_image(args.char, img)
            print(
                json.dumps(
                    {
                        "ok": True,
                        "action": "lock-image",
                        "char": args.char,
                        "path": path,
                        "source": args.image,
                    }
                )
            )
            return 0

        if args.action == "unlock":
            removed = api.unlock_character(args.char)
            print(
                json.dumps(
                    {
                        "ok": True,
                        "action": "unlock",
                        "char": args.char,
                        "removed": bool(removed),
                    }
                )
            )
            return 0

        print(json.dumps({"ok": False, "error": f"unknown action {args.action}"}))
        return 2
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e), "action": args.action}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
